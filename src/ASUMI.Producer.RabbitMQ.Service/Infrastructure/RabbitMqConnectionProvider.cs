using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
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
                _logger.LogInformation(
                    "Attempting to connect to RabbitMQ at {Host}:{Port}{VHost} as {User}...",
                    _options.HostName, _options.Port, _options.VirtualHost, _options.UserName);

                var connection = factory.CreateConnection(_options.ConnectionName);

                connection.ConnectionShutdown += (_, ea) =>
                    _logger.LogWarning("RabbitMQ connection shutdown: {Reason}", ea.ReplyText);
                connection.CallbackException += (_, ea) =>
                    _logger.LogError(ea.Exception, "RabbitMQ callback exception.");
                connection.ConnectionBlocked += (_, ea) =>
                    _logger.LogWarning("RabbitMQ connection blocked: {Reason}", ea.Reason);

                _connection = connection;
                _logger.LogInformation("Connected to RabbitMQ. ConnectionName={ConnectionName}", _options.ConnectionName);
            }
            catch (Exception ex)
            {
                // Пишем только тип и сообщение без стектрейса — иначе лог засоряется при недоступном RabbitMQ.
                _logger.LogWarning(
                    "Could not connect to RabbitMQ at {Host}:{Port}{VHost} ({ExceptionType}): {Message}. Will retry in {RetryInterval}s.",
                    _options.HostName, _options.Port, _options.VirtualHost,
                    ex.GetType().Name, ex.Message, _options.ReconnectIntervalSeconds);
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
            _logger.LogInformation("RabbitMQ connection closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ connection.");
        }

        _connectionLock.Dispose();
    }
}