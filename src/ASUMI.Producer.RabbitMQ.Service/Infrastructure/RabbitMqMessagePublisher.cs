using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ASUMI.Producer.RabbitMQ.Service.Infrastructure;

public sealed class RabbitMqMessagePublisher : IMessagePublisher, IDisposable
{
    private readonly ILogger<RabbitMqMessagePublisher> _logger;
    private readonly RabbitMqOptions _options;
    private readonly IRabbitMqConnectionProvider _connectionProvider;

    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IModel? _channel;
    private bool _disposed;

    public RabbitMqMessagePublisher(
        ILogger<RabbitMqMessagePublisher> logger,
        IOptions<RabbitMqOptions> options,
        IRabbitMqConnectionProvider connectionProvider)
    {
        _logger = logger;
        _options = options.Value;
        _connectionProvider = connectionProvider;
    }

    public async Task<bool> PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            // Если канала нет или он закрыт — открываем новый. WaitForChannelAsync подождёт RabbitMQ.
            if (_channel is null || !_channel.IsOpen)
            {
                _channel?.Dispose();
                _channel = null;

                try
                {
                    _channel = await _connectionProvider.WaitForChannelAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            var props = _channel.CreateBasicProperties();
            props.Persistent = _options.Persistent;
            props.ContentType = "application/json";
            props.ContentEncoding = "utf-8";

            try
            {
                _channel.BasicPublish(
                    exchange: _options.ExchangeName,
                    routingKey: _options.RoutingKey,
                    basicProperties: props,
                    body: body);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Publish failed ({ExceptionType}): {Message}. Channel will be recreated on next attempt.",
                    ex.GetType().Name, ex.Message);

                _channel?.Dispose();
                _channel = null;
                return false;
            }
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _channel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing publisher channel.");
        }

        _publishLock.Dispose();
    }
}