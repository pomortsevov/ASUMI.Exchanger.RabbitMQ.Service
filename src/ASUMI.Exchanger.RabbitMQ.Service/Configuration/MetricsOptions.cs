using System.ComponentModel.DataAnnotations;

namespace ASUMI.Exchanger.RabbitMQ.Service.Configuration;

/// <summary>
/// Эндпоинт /metrics для Prometheus.
/// </summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>Поднимать ли HTTP-эндпоинт метрик.</summary>
    public bool Enabled { get; set; }

    /// <summary>Порт эндпоинта.</summary>
    /// <remarks>
    /// 9101, а не 9100: порт 9100 на этой и серверной машинах занят службой ASCON Pilot
    /// (Ascon.Pilot.WinService). Оба процесса регистрировали в HTTP.SYS префикс
    /// http://+:9100/, и запросы /metrics обслуживал то один, то другой — какой именно,
    /// со стороны приложения не гарантировалось.
    /// </remarks>
    [Range(1024, 65535)]
    public int Port { get; set; } = 9101;
}