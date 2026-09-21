using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ASUMI.Producer.RabbitMQ.Service.Workers;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly AsumiProducerRabbitMqOptions _options;
    private readonly IRabbitMqConnectionProvider _rabbit;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AsumiProducerRabbitMqOptions> options,
        IRabbitMqConnectionProvider rabbit)
    {
        _logger = logger;
        _options = options.Value;
        _rabbit = rabbit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("HeartbeatWorker is disabled by configuration. Exiting.");
            return;
        }

        _logger.LogInformation("HeartbeatWorker starting. Waiting for RabbitMQ connection...");

        IModel channel;
        try
        {
            channel = await _rabbit.WaitForChannelAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HeartbeatWorker cancelled while waiting for RabbitMQ.");
            return;
        }

        using (channel)
        {
            _logger.LogInformation("RabbitMQ channel opened. ChannelNumber={ChannelNumber}", channel.ChannelNumber);
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        _logger.LogInformation("HeartbeatWorker running. Interval: {Interval}s", _options.IntervalSeconds);

        var count = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            count++;
            _logger.LogInformation("Hello World #{Count} at {Time:HH:mm:ss}", count, DateTime.Now);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("HeartbeatWorker stopped. Total ticks: {Count}", count);
    }
}