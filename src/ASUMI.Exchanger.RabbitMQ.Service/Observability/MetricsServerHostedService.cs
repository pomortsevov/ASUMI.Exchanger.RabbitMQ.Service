using System.Net;
using System.Net.Sockets;
using ASUMI.Exchanger.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;

namespace ASUMI.Exchanger.RabbitMQ.Service.Observability;

/// <summary>
/// Отдаёт /metrics для Prometheus. Жизненный цикл отдан хосту, чтобы сервер гасился
/// вместе со службой.
/// </summary>
public sealed class MetricsServerHostedService : IHostedService, IDisposable
{
    private readonly MetricsOptions _options;
    private readonly ILogger<MetricsServerHostedService> _logger;
    private MetricServer? _server;
    private bool _disposed;

    public MetricsServerHostedService(
        IOptions<MetricsOptions> options,
        ILogger<MetricsServerHostedService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Не включён в конфиге — порт не поднимаем вообще. Служба должна работать
        // и когда метрики не собираются.
        if (!_options.Enabled)
        {
            _logger.LogInformation("Metrics endpoint disabled by configuration.");
            return Task.CompletedTask;
        }

        // Проверяем порт заранее. Иначе конфликт выглядит как невнятное
        // HttpListenerException (183), из которого не видно, что виноват второй
        // экземпляр этой же службы. На проде такого не бывает, на машине
        // разработчика — регулярно, когда запущены и VS, и консольный экземпляр.
        if (!IsPortAvailable(_options.Port))
        {
            var ownerHint =
                $"Get-NetTCPConnection -LocalPort {_options.Port} -State Listen | Select-Object OwningProcess";

            _logger.LogError(
                "Port {Port} is already in use, metrics endpoint not started. Most likely another " +
                "instance of this service is running, which would also publish contracts twice. " +
                "Find it with: {OwnerHint}, then either stop it or change Metrics:Port. " +
                "Publishing continues without metrics.",
                _options.Port, ownerHint);
            return Task.CompletedTask;
        }

        try
        {
            _server = new MetricServer(port: _options.Port);
            _server.Start();
            _logger.LogInformation(
                "Metrics endpoint listening on http://0.0.0.0:{Port}/metrics.", _options.Port);
        }
        catch (Exception ex)
        {
            // На Windows MetricServer работает через HttpListener и без зарезервированного
            // URL бросает access denied. Метрики не должны ронять службу, поэтому гасим
            // сервер и продолжаем работать.
            _server?.Stop();
            _server = null;

            // Префикс именно с корнем, а не /metrics: MetricServer регистрирует
            // http://+:PORT/, и резервирование URL в Windows покрывает ровно указанный
            // путь — резервирование http://+:PORT/metrics прав на корень не даёт.
            var reserveHint = $"netsh http add urlacl url=http://+:{_options.Port}/ user=\"<account>\"";

            _logger.LogError(
                ex,
                "Could not start the metrics endpoint on port {Port}. Publishing continues without " +
                "metrics. On Windows reserve the URL first from an elevated prompt: {ReserveHint}",
                _options.Port, reserveHint);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Пытается занять порт и сразу освобождает. Порт остаётся за нами, но так можно
    /// узнать заранее, что он уже кем-то занят, не дожидаясь исключения от HttpListener.
    /// </summary>
    private static bool IsPortAvailable(int port)
    {
        TcpListener? probe = null;
        try
        {
            probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            probe?.Stop();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopServer();
        return Task.CompletedTask;
    }

    private void StopServer()
    {
        if (_server is null)
            return;

        try
        {
            _server.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while stopping the metrics endpoint.");
        }
        finally
        {
            _server = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Подстраховка на случай, если хост не вызвал StopAsync — порт не должен
        // оставаться занятым после выгрузки процесса.
        StopServer();
        GC.SuppressFinalize(this);
    }
}