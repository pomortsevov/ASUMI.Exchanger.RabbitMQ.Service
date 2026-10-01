using System.ComponentModel.DataAnnotations;

namespace ASUMI.Producer.RabbitMQ.Service.Configuration;

/// <summary>
/// Отправка логов в Grafana Loki через push API.
/// По умолчанию выключено: если Loki недоступен, служба всё равно должна работать.
/// </summary>
public sealed class LokiOptions
{
    public const string SectionName = "Loki";

    /// <summary>Включена ли отправка в Loki.</summary>
    public bool Enabled { get; set; }

    /// <summary>Базовый адрес Loki, например http://172.18.28.118:3100.</summary>
    [Required]
    public string Url { get; set; } = "http://localhost:3100";

    /// <summary>Метка job — по ней выбираются потоки в Grafana.</summary>
    [Required]
    public string Job { get; set; } = "ASUMI.Producer.RabbitMQ.Service";

    /// <summary>Метка service_name. Держите отличной от job у соседних компонентов.</summary>
    [Required]
    public string ServiceName { get; set; } = "ASUMI.Producer.RabbitMQ.Service";

    /// <summary>Сколько записей отправлять за один запрос.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>Как часто отправлять накопленное, секунды.</summary>
    [Range(1, 60)]
    public int FlushPeriodSeconds { get; set; } = 2;

    /// <summary>Таймаут HTTP-запроса к Loki, секунды.</summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Размер очереди в памяти. При переполнении самые старые записи отбрасываются —
    /// лучше потерять лог, чем заблокировать публикацию договоров.
    /// </summary>
    [Range(100, 100000)]
    public int QueueCapacity { get; set; } = 10000;

    /// <summary>Bearer-токен, если Loki настроен с авторизацией.</summary>
    public string? ApiKey { get; set; }
}