using ASUMI.Exchanger.RabbitMQ.Service.Abstractions;
using ASUMI.Exchanger.RabbitMQ.Service.Configuration;
using ASUMI.Exchanger.RabbitMQ.Service.Observability;
using ASUMI.Exchanger.RabbitMQ.Service.Validation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ASUMI.Exchanger.RabbitMQ.Service.Workers;

public sealed class FilePublisherWorker : BackgroundService
{
    private readonly ILogger<FilePublisherWorker> _logger;
    private readonly FilePublisherOptions _options;
    private readonly RabbitMqOptions _rabbitMq;
    private readonly IMessagePublisher _publisher;

    public FilePublisherWorker(
        ILogger<FilePublisherWorker> logger,
        IOptions<FilePublisherOptions> options,
        IOptions<RabbitMqOptions> rabbitMq,
        IMessagePublisher publisher)
    {
        _logger = logger;
        _options = options.Value;
        _rabbitMq = rabbitMq.Value;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("FilePublisherWorker is disabled by configuration. Exiting.");
            return;
        }

        _logger.LogInformation(
            "FilePublisherWorker starting. Source={Source}, Processed={Processed}, Failed={Failed}, Mask={Mask}, Interval={Interval}s",
            _options.SourcePath, _options.ProcessedPath, _options.FailedPath, _options.FileMask, _options.PollIntervalSeconds);

        // Гарантируем существование папок
        Directory.CreateDirectory(_options.SourcePath);
        Directory.CreateDirectory(_options.ProcessedPath);
        Directory.CreateDirectory(_options.FailedPath);

        var inputDir = new DirectoryInfo(_options.SourcePath);
        var outputDir = new DirectoryInfo(_options.ProcessedPath);
        var failedDir = new DirectoryInfo(_options.FailedPath);
        var interval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(inputDir, outputDir, failedDir, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error in polling loop. Will retry after {Interval}s.",
                    _options.PollIntervalSeconds);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("FilePublisherWorker stopped.");
    }

    private async Task ProcessOnceAsync(
        DirectoryInfo inputDir,
        DirectoryInfo outputDir,
        DirectoryInfo failedDir,
        CancellationToken stoppingToken)
    {
        var files = inputDir.GetFiles(_options.FileMask);

        // Ставим до раннего возврата: иначе при пустом каталоге метрика не обновится
        // и продолжит показывать прошлое значение.
        ExchangerMetrics.FilesPending.Set(files.Length);

        if (files.Length == 0)
        {
            _logger.LogDebug("No files found in {Source}.", _options.SourcePath);
            return;
        }

        foreach (var fi in files)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await ProcessFileAsync(fi, outputDir, failedDir, stoppingToken);
            }
            catch (IOException ioEx)
            {
                _logger.LogWarning(
                    "I/O error while processing {File} ({Type}): {Message}. Will retry on next cycle.",
                    fi.Name, ioEx.GetType().Name, ioEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process {File}.", fi.Name);
                ExchangerMetrics.FilesFailed.WithLabels("unexpected_error").Inc();
            }
        }
    }

    private async Task ProcessFileAsync(
        FileInfo fi,
        DirectoryInfo outputDir,
        DirectoryInfo failedDir,
        CancellationToken stoppingToken)
    {
        // 1. Прочитать содержимое В СЫРЫХ БАЙТАХ.
        // Декодировать нельзя: файлы АСУМИ приходят в UTF-16LE (BOM FF FE), и любая
        // конвертация через Encoding.UTF8 портит их — на провереном файле 3414 байт
        // превращались в 1996, то есть терялась половина содержимого. Байты на диске
        // и байты в RabbitMQ должны совпадать байт в байт.
        var startedAt = Stopwatch.StartNew();
        var body = await File.ReadAllBytesAsync(fi.FullName, stoppingToken);

        _logger.LogInformation(
            "Read {File} ({Bytes} bytes).",
            fi.Name, body.Length);

        CheckForUnexpectedEncoding(fi.Name, body);

        // 2. Проверка JSON. Байты в RabbitMQ уходят без изменений, проверяется копия.
        // Файл, который consumer не осилит, в очередь не попадает — иначе он там упадёт.
        if (!JsonValidator.IsValidJson(body, out var parseError))
        {
            ExchangerMetrics.FilesFailed.WithLabels("invalid_json").Inc();

            var failedName = $"{Path.GetFileNameWithoutExtension(fi.Name)}-" +
                             $"{DateTime.Now:yyyyMMdd-HHmmssfff}{fi.Extension}";
            File.Move(fi.FullName, Path.Combine(failedDir.FullName, failedName), overwrite: true);

            _logger.LogError(
                "Moved {File} to failed: not valid JSON ({Reason}). File: {FailedFile}",
                fi.Name, parseError, failedName);
            return;
        }

        // 3. Публикация в RabbitMQ.
        // Если публикация не удалась — НЕ перемещаем и НЕ удаляем файл: вернёмся к нему на следующем цикле.
        var published = await _publisher.PublishAsync(body, stoppingToken);
        if (!published)
        {
            _logger.LogWarning(
                "RabbitMQ not available — {File} will be retried on next cycle.",
                fi.Name);
            ExchangerMetrics.FilesDeferred.Inc();
            return;
        }

        _logger.LogInformation(
            "Published {File} ({Bytes} bytes) to exchange {Exchange} with routingKey {RoutingKey}.",
            fi.Name, body.Length, _rabbitMq.ExchangeName, _rabbitMq.RoutingKey);

        ExchangerMetrics.PublishDuration.Observe(startedAt.Elapsed.TotalSeconds);
        ExchangerMetrics.FilesPublished.Inc();
        ExchangerMetrics.LastPublishTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // 3. Переместить в processed — только после успешной публикации.
        // Move, а не CopyTo + Delete: перемещение атомарно в пределах тома, поэтому
        // промежуточного состояния "скопировано, но не удалено" не существует. При падении
        // процесса в old-варианте файл успевал остаться в SourcePath и публиковался
        // повторно — consumer получал дубликат.
        var processedName = $"{Path.GetFileNameWithoutExtension(fi.Name)}-" +
                            $"{DateTime.Now:yyyyMMdd-HHmmssfff}{fi.Extension}";
        var dst = Path.Combine(outputDir.FullName, processedName);

        File.Move(fi.FullName, dst, overwrite: true);
        _logger.LogInformation("Moved to processed: {ProcessedFile}", processedName);
    }

    /// <summary>
/// UTF-16 — ожидаемый формат файлов АСУМИ: проверено на 269 файлах без исключения,
/// ни одного в UTF-8 и ни одного без BOM. Поэтому предупреждаем не о UTF-16, а об
/// отклонении от него. Иначе WRN летел на каждом файле, то есть на 100% записей, и
/// сигнал превращался в шум.
/// <para>
/// Файл отправляется байт в байт, поэтому кодировку разбирает consumer. Смена формата
/// на стороне АСУМИ сломала бы его незаметно — метрика отклонений это покажет.
/// </para>
/// </summary>
    private void CheckForUnexpectedEncoding(string fileName, byte[] body)
    {
        var hasUtf16Bom = body.Length >= 2 &&
                          ((body[0] == 0xFF && body[1] == 0xFE) || (body[0] == 0xFE && body[1] == 0xFF));
        if (hasUtf16Bom)
            return;

        var hasUtf8Bom = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF;
        var detected = hasUtf8Bom ? "UTF-8 BOM" : "no BOM";

        ExchangerMetrics.FilesUnexpectedEncoding.Inc();

        _logger.LogWarning(
            "{File} has no UTF-16 BOM ({Detected}). ASUMI writes UTF-16LE, so this is unexpected. " +
            "The file is published byte-for-byte and the consumer has to decode it accordingly.",
            fileName, detected);
    }
}