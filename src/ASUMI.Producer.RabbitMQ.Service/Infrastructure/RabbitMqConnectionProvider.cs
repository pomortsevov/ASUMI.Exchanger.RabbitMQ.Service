using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using ASUMI.Producer.RabbitMQ.Service.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ASUMI.Producer.RabbitMQ.Service.Infrastructure;

public sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider
{
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly RabbitMqOptions _options;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    /// <summary>Счётчик неудачных попыток подряд: сбрасывается при успешном подключении.</summary>
    private int _consecutiveFailures;

    /// <summary>Когда последний раз писали предупреждение о неудаче.</summary>
    private DateTime _lastFailureWarningUtc = DateTime.MinValue;

    public RabbitMqConnectionProvider(
        ILogger<RabbitMqConnectionProvider> logger,
        IOptions<RabbitMqOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsConnected => _connection is { IsOpen: true };

    public IModel? TryCreateChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = _connection;
        if (connection is null || !connection.IsOpen)
            return null;

        try
        {
            return connection.CreateModel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create channel. Connection may be lost.");
            return null;
        }
    }

    public async Task<IModel> WaitForChannelAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = TryCreateChannel();
            if (channel is not null)
                return channel;

            await EnsureConnectedAsync(cancellationToken);

            // Пауза нужна только если соединение так и не появилось — иначе после
            // успешного подключения простаивали бы лишние ReconnectIntervalSeconds.
            if (IsConnected)
                continue;

            await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectIntervalSeconds), cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// Пытается установить соединение. Если уже подключены — ничего не делает.
    /// Не бросает исключение наружу: логирует и оставляет соединение null.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_disposed || IsConnected) return;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) return;

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                AutomaticRecoveryEnabled = _options.AutomaticRecovery,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(_options.NetworkRecoveryIntervalSeconds),
                DispatchConsumersAsync = false
            };

            try
            {
                // Debug, а не Information: попытка сама по себе ничего не значит, исход
                // логируется отдельно. При недоступном брокере это 288 лишних строк в час.
                _logger.LogDebug(
                    "Attempting to connect to RabbitMQ at {Host}:{Port}{VHost} as {User}...",
                    _options.HostName, _options.Port, _options.VirtualHost, _options.UserName);

                var connection = factory.CreateConnection(_options.ConnectionName);

                connection.ConnectionShutdown += (_, ea) =>
                {
                    ProducerMetrics.RabbitMqConnected.Set(0);
                    _logger.LogWarning("RabbitMQ connection shutdown: {Reason}", ea.ReplyText);
                };
                connection.CallbackException += (_, ea) =>
                    _logger.LogError(ea.Exception, "RabbitMQ callback exception.");
                connection.ConnectionBlocked += (_, ea) =>
                    _logger.LogWarning("RabbitMQ connection blocked: {Reason}", ea.Reason);

                _connection = connection;
                ProducerMetrics.RabbitMqConnected.Set(1);
                _consecutiveFailures = 0;
                _logger.LogInformation("Connected to RabbitMQ. ConnectionName={ConnectionName}", _options.ConnectionName);
            }
            catch (Exception ex)
            {
                ProducerMetrics.RabbitMqConnected.Set(0);
                _consecutiveFailures++;

                // Повторяющиеся неудачи не пишем на уровне Warning: иначе при аварии
                // брокера лог засоряется двумя строками раз в пять секунд. Предупреждение
                // уходит на первой неудаче и затем не чаще раза в
                // RepeatWarningIntervalSeconds, а счётчик неудач виден в сообщении.
                var repeatInterval = TimeSpan.FromSeconds(_options.RepeatWarningIntervalSeconds);
                var shouldWarn = _consecutiveFailures == 1 ||
                                 (DateTime.UtcNow - _lastFailureWarningUtc) >= repeatInterval;

                // Пишем только тип и сообщение без стектрейса — иначе лог засоряется при недоступном RabbitMQ.
                if (shouldWarn)
                {
                    _lastFailureWarningUtc = DateTime.UtcNow;
                    _logger.LogWarning(
                        "Could not connect to RabbitMQ at {Host}:{Port}{VHost} ({ExceptionType}): {Message}. " +
                        "Will retry in {RetryInterval}s. Consecutive failures: {Failures}.",
                        _options.HostName, _options.Port, _options.VirtualHost,
                        ex.GetType().Name, ex.Message, _options.ReconnectIntervalSeconds, _consecutiveFailures);
                }
                else
                {
                    _logger.LogDebug(
                        "Could not connect to RabbitMQ ({ExceptionType}): {Message}. Consecutive failures: {Failures}.",
                        ex.GetType().Name, ex.Message, _consecutiveFailures);
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_connection is { IsOpen: true })
                _connection.Close(TimeSpan.FromSeconds(5));
            _connection?.Dispose();
            ProducerMetrics.RabbitMqConnected.Set(0);
            _logger.LogInformation("RabbitMQ connection closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ connection.");
        }

        _connectionLock.Dispose();
    }
}