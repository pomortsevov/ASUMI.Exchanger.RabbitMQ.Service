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
    private readonly IConnection _connection;
    private bool _disposed;

    public RabbitMqConnectionProvider(
        ILogger<RabbitMqConnectionProvider> logger,
        IOptions<RabbitMqOptions> options)
    {
        _logger = logger;
        _options = options.Value;

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

        _logger.LogInformation(
            "Connecting to RabbitMQ at {Host}:{Port}{VHost} as {User}",
            _options.HostName, _options.Port, _options.VirtualHost, _options.UserName);

        try
        {
            _connection = factory.CreateConnection(_options.ConnectionName);
            _logger.LogInformation("Connected to RabbitMQ. ConnectionName={ConnectionName}", _options.ConnectionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to connect to RabbitMQ at {Host}:{Port}{VHost}",
                _options.HostName, _options.Port, _options.VirtualHost);
            throw;
        }
    }

    public IModel CreateChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.CreateModel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_connection.IsOpen)
            {
                _connection.Close(TimeSpan.FromSeconds(5));
            }
            _connection.Dispose();
            _logger.LogInformation("RabbitMQ connection closed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ connection.");
        }
    }
}