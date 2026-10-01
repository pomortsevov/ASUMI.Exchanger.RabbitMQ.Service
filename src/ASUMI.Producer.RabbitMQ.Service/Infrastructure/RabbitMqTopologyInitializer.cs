using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ASUMI.Producer.RabbitMQ.Service.Infrastructure;

public sealed class RabbitMqTopologyInitializer : IRabbitMqTopologyInitializer
{
    private readonly ILogger<RabbitMqTopologyInitializer> _logger;
    private readonly RabbitMqOptions _options;
    private readonly IRabbitMqConnectionProvider _rabbit;

    public RabbitMqTopologyInitializer(
        ILogger<RabbitMqTopologyInitializer> logger,
        IOptions<RabbitMqOptions> options,
        IRabbitMqConnectionProvider rabbit)
    {
        _logger = logger;
        _options = options.Value;
        _rabbit = rabbit;
    }

    public async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Declaring RabbitMQ topology...");

        using var channel = await _rabbit.WaitForChannelAsync(cancellationToken);

        // 1. Exchange (topic, durable)
        channel.ExchangeDeclare(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null);

        _logger.LogInformation(
            "Exchange declared: {Exchange} (topic, durable)",
            _options.ExchangeName);

        // 2. Queue (durable, с TTL на сообщения)
        var queueArgs = new Dictionary<string, object>
        {
            { "x-message-ttl", _options.MessageTtlMs },
            { "x-queue-type", _options.QueueType }
        };

        // Размер группы реплик — только для quorum. Для classic/stream аргумент неприменим
        // и вызовет 406.
        if (string.Equals(_options.QueueType, "quorum", StringComparison.OrdinalIgnoreCase))
        {
            queueArgs["x-quorum-initial-group-size"] = _options.QuorumInitialGroupSize;
        }

        channel.QueueDeclare(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArgs);

        _logger.LogInformation(
            "Queue declared: {Queue} (durable, type={QueueType}, TTL={TtlMs}ms)",
            _options.QueueName, _options.QueueType, _options.MessageTtlMs);

        // 3. Bind очереди к exchange по routing key
        channel.QueueBind(
            queue: _options.QueueName,
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey);

        _logger.LogInformation(
            "Queue bound: {Queue} -> {Exchange} (routingKey={RoutingKey})",
            _options.QueueName, _options.ExchangeName, _options.RoutingKey);

        _logger.LogInformation("RabbitMQ topology is ready.");
    }
}