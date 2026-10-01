using Prometheus;

namespace ASUMI.Producer.RabbitMQ.Service.Observability;

/// <summary>
/// Метрики сервиса для Prometheus.
/// <para>
/// Префикс asumi_producer_ выбран, чтобы не пересечься с мёртвыми asumi_* от старого
/// питоновского экспортёра, на который ссылается дашборд.
/// </para>
/// <para>
/// Статический класс осознанно: счётчики всё равно process-wide, а так не меняется
/// сигнатура конструктора FilePublisherWorker.
/// </para>
/// </summary>
public static class ProducerMetrics
{
    public static readonly Gauge FilesPending = Metrics.CreateGauge(
        "asumi_producer_files_pending",
        "Files found in the source directory and waiting to be published.");

    public static readonly Counter FilesPublished = Metrics.CreateCounter(
        "asumi_producer_files_published_total",
        "Files confirmed published to RabbitMQ.");

    public static readonly Counter FilesFailed = Metrics.CreateCounter(
        "asumi_producer_files_failed_total",
        "Files abandoned after an unhandled error.");

    public static readonly Counter FilesDeferred = Metrics.CreateCounter(
        "asumi_producer_files_deferred_total",
        "Publications skipped because RabbitMQ was unavailable; the file is retried next cycle.");

    public static readonly Counter BomWarnings = Metrics.CreateCounter(
        "asumi_producer_bom_warnings_total",
        "Files whose content starts with a UTF-16 BOM and are published byte-for-byte.");

    public static readonly Histogram PublishDuration = Metrics.CreateHistogram(
        "asumi_producer_publish_duration_seconds",
        "Time from reading a file until RabbitMQ confirms its publication.",
        // Границы те же, что были у старого экспортёра, чтобы панель P99 в Grafana
        // продолжила работать без правки запроса.
        new HistogramConfiguration
        {
            Buckets = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 }
        });

    public static readonly Gauge RabbitMqConnected = Metrics.CreateGauge(
        "asumi_producer_rabbitmq_connected",
        "Whether the service has an open RabbitMQ connection (1/0).");

    public static readonly Gauge LastPublishTimestamp = Metrics.CreateGauge(
        "asumi_producer_last_publish_timestamp_seconds",
        "Unix timestamp of the last confirmed publication.");
}