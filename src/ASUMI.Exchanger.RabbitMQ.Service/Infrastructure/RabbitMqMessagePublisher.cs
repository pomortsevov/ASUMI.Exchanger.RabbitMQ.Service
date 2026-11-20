using ASUMI.Exchanger.RabbitMQ.Service.Abstractions;
using ASUMI.Exchanger.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ASUMI.Exchanger.RabbitMQ.Service.Infrastructure;

public sealed class RabbitMqMessagePublisher : IMessagePublisher, IDisposable
{
    private readonly ILogger<RabbitMqMessagePublisher> _logger;
    private readonly RabbitMqOptions _options;
    private readonly IRabbitMqConnectionProvider _connectionProvider;

    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IModel? _channel;
    private bool _disposed;

    /// <summary>Произошёл ли BasicReturn для текущей публикации (сообщение ушло в никуда).</summary>
    private bool _returned;

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

                // Publisher confirms: без них BasicPublish возвращается сразу, и мы не знаем,
                // дошло ли сообщение до брокера.
                _channel.ConfirmSelect();
                _channel.BasicReturn += OnBasicReturn;
            }

            var props = _channel.CreateBasicProperties();
            props.Persistent = _options.Persistent;
            props.ContentType = "application/json";
            props.ContentEncoding = "utf-8";
            props.MessageId = Guid.NewGuid().ToString("N");

            _returned = false;

            try
            {
                _channel.BasicPublish(
                    exchange: _options.ExchangeName,
                    routingKey: _options.RoutingKey,
                    basicProperties: props,
                    body: body,
                    // mandatory: если для routingKey нет привязки, брокер вернёт сообщение
                    // через BasicReturn, а не отбросит молча.
                    mandatory: true);

                // Ждём подтверждения от брокера. Без этого успешный BasicPublish
                // не означает, что сообщение где-то лежит.
                var timeout = TimeSpan.FromSeconds(_options.PublishConfirmTimeoutSeconds);
                if (!_channel.WaitForConfirms(timeout))
                {
                    _logger.LogWarning(
                        "Broker did not confirm publish within {Timeout}s. Treating as failure.",
                        _options.PublishConfirmTimeoutSeconds);

                    _channel?.Dispose();
                    _channel = null;
                    return false;
                }

                if (_returned)
                {
                    _logger.LogWarning(
                        "Message was returned by broker — no queue is bound to {Exchange} with routingKey {RoutingKey}.",
                        _options.ExchangeName, _options.RoutingKey);
                    return false;
                }

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

    /// <summary>
    /// Брокер вернул сообщение: под exchange/routingKey нет очереди. Публикация не удалась —
    /// вызывающий код оставит файл в SourcePath.
    /// </summary>
    private void OnBasicReturn(object? sender, BasicReturnEventArgs ea)
    {
        _returned = true;
        _logger.LogWarning(
            "BasicReturn: replyCode={ReplyCode} replyText={ReplyText} exchange={Exchange} routingKey={RoutingKey}",
            ea.ReplyCode, ea.ReplyText, ea.Exchange, ea.RoutingKey);
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