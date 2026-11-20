using ASUMI.Exchanger.RabbitMQ.Service.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ASUMI.Exchanger.RabbitMQ.Service.Infrastructure;

/// <summary>
/// Запускает объявление топологии RabbitMQ при старте хоста.
/// Не падает, если RabbitMQ недоступен — ждёт и повторяет.
/// </summary>
public sealed class RabbitMqTopologyHostedService : IHostedService
{
    private readonly ILogger<RabbitMqTopologyHostedService> _logger;
    private readonly IRabbitMqTopologyInitializer _initializer;

    public RabbitMqTopologyHostedService(
        ILogger<RabbitMqTopologyHostedService> logger,
        IRabbitMqTopologyInitializer initializer)
    {
        _logger = logger;
        _initializer = initializer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _initializer.EnsureTopologyAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Topology initialization cancelled.");
        }
        catch (Exception ex)
        {
            // Не роняем хост — FilePublisherWorker дождётся RabbitMQ при первой публикации.
            _logger.LogError(ex, "Unexpected error during topology initialization. Service will continue.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}