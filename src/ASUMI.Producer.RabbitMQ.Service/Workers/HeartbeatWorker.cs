using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ASUMI.Producer.RabbitMQ.Service.Workers;

public sealed class HeartbeatWorker : BackgroundService
{
    private static readonly Action<ILogger, int, DateTime, Exception?> LogHeartbeat =
        LoggerMessage.Define<int, DateTime>(
            LogLevel.Information,
            new EventId(1, nameof(LogHeartbeat)),
            "Hello World #{Count} at {Time:HH:mm:ss}");

    private static readonly Action<ILogger, int, Exception?> LogStarted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(2, nameof(LogStarted)),
            "HeartbeatWorker started. Interval: {Interval}s");

    private static readonly Action<ILogger, Exception?> LogDisabled =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3, nameof(LogDisabled)),
            "HeartbeatWorker is disabled by configuration. Exiting.");

    private static readonly Action<ILogger, int, Exception?> LogStopped =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(4, nameof(LogStopped)),
            "HeartbeatWorker stopped. Total ticks: {Count}");

    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly AsumiProducerRabbitMqOptions _options;

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IOptions<AsumiProducerRabbitMqOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger, null);
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        LogStarted(_logger, _options.IntervalSeconds, null);

        var count = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            count++;
            LogHeartbeat(_logger, count, DateTime.Now, null);

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        LogStopped(_logger, count, null);
    }
}