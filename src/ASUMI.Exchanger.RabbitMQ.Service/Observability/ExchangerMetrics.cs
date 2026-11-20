using Prometheus;

namespace ASUMI.Exchanger.RabbitMQ.Service.Observability;

/// <summary>
/// Метрики сервиса для Prometheus.
/// <para>
/// Префикс asumi_exchanger_ выбран, чтобы не пересечься с мёртвыми asumi_* от старого
/// питоновского экспортёра, на который ссылается дашборд.
/// </para>
/// <para>
/// Статический класс осознанно: счётчики всё равно process-wide, а так не меняется
/// сигнатура конструктора FilePublisherWorker.
/// </para>
/// </summary>
public static class ExchangerMetrics
{
    public static readonly Gauge FilesPending = Metrics.CreateGauge(
        "asumi_exchanger_files_pending",
        "Files found in the source directory and waiting to be published.");

    public static readonly Counter FilesPublished = Metrics.CreateCounter(
        "asumi_exchanger_files_published_total",
        "Files confirmed published to RabbitMQ.");

    public static readonly Counter FilesDeferred = Metrics.CreateCounter(
        "asumi_exchanger_files_deferred_total",
        "Publications skipped because RabbitMQ was unavailable; the file is retried next cycle.");

    /// <summary>
    /// Файлы без ожидаемого UTF-16 BOM. АСУМИ пишет UTF-16LE, поэтому ненулевое
    /// значение означает, что формат на стороне отправителя изменился и consumer,
    /// читающий UTF-16, начнёт получать нечитаемые сообщения.
    /// </summary>
    public static readonly Counter FilesUnexpectedEncoding = Metrics.CreateCounter(
        "asumi_exchanger_files_unexpected_encoding_total",
        "Files without the expected UTF-16 BOM, published byte-for-byte to a UTF-16 consumer.");

    /// <summary>
    /// Файлы, которые не попали в очередь. Причина в метке reason:
    /// <c>invalid_json</c> — не разбирается как JSON, ушёл в FailedPath;
    /// <c>unexpected_error</c> — необработанное исключение при обработке.
    /// Без метки это две несвязанные величины, из-за чего files_failed_total
    /// показывал ноль при непустой папке FailedPath.
    /// </summary>
    public static readonly Counter FilesFailed = Metrics.CreateCounter(
        "asumi_exchanger_files_failed_total",
        "Files that were not published to RabbitMQ, by reason.",
        new CounterConfiguration { LabelNames = new[] { "reason" } });

    public static readonly Histogram PublishDuration = Metrics.CreateHistogram(
        "asumi_exchanger_publish_duration_seconds",
        "Time from reading a file until RabbitMQ confirms its publication.",
        // Границы те же, что были у старого экспортёра, чтобы панель P99 в Grafana
        // продолжила работать без правки запроса.
        new HistogramConfiguration
        {
            Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 }
        });

    public static readonly Gauge RabbitMqConnected = Metrics.CreateGauge(
        "asumi_exchanger_rabbitmq_connected",
        "Whether the service has an open RabbitMQ connection (1/0).");

    public static readonly Gauge LastPublishTimestamp = Metrics.CreateGauge(
        "asumi_exchanger_last_publish_timestamp_seconds",
        "Unix timestamp of the last confirmed publication.");
}