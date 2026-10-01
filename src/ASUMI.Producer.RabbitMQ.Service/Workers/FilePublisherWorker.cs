using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ASUMI.Producer.RabbitMQ.Service.Workers;

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
            "FilePublisherWorker starting. Source={Source}, Processed={Processed}, Mask={Mask}, Interval={Interval}s",
            _options.SourcePath, _options.ProcessedPath, _options.FileMask, _options.PollIntervalSeconds);

        // Гарантируем существование папок
        Directory.CreateDirectory(_options.SourcePath);
        Directory.CreateDirectory(_options.ProcessedPath);

        var inputDir = new DirectoryInfo(_options.SourcePath);
        var outputDir = new DirectoryInfo(_options.ProcessedPath);
        var interval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(inputDir, outputDir, stoppingToken);
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
        CancellationToken stoppingToken)
    {
        var files = inputDir.GetFiles(_options.FileMask);
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
                await ProcessFileAsync(fi, outputDir, stoppingToken);
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
            }
        }
    }

    private async Task ProcessFileAsync(
        FileInfo fi,
        DirectoryInfo outputDir,
        CancellationToken stoppingToken)
    {
        // 1. Прочитать содержимое В СЫРЫХ БАЙТАХ.
        // Декодировать нельзя: файлы АСУМИ приходят в UTF-16LE (BOM FF FE), и любая
        // конвертация через Encoding.UTF8 портит их — на провереном файле 3414 байт
        // превращались в 1996, то есть терялась половина содержимого. Байты на диске
        // и байты в RabbitMQ должны совпадать байт в байт.
        var body = await File.ReadAllBytesAsync(fi.FullName, stoppingToken);

        _logger.LogInformation(
            "Read {File} ({Bytes} bytes).",
            fi.Name, body.Length);

        WarnIfLikelyUtf16(fi.Name, body);

        // 2. Публикация в RabbitMQ.
        // Если публикация не удалась — НЕ перемещаем и НЕ удаляем файл: вернёмся к нему на следующем цикле.
        var published = await _publisher.PublishAsync(body, stoppingToken);
        if (!published)
        {
            _logger.LogWarning(
                "RabbitMQ not available — {File} will be retried on next cycle.",
                fi.Name);
            return;
        }

        _logger.LogInformation(
            "Published {File} ({Bytes} bytes) to exchange {Exchange} with routingKey {RoutingKey}.",
            fi.Name, body.Length, _rabbitMq.ExchangeName, _rabbitMq.RoutingKey);

        // 3. Переместить в processed — только после успешной публикации
        var processedName = $"{Path.GetFileNameWithoutExtension(fi.Name)}-" +
                            $"{DateTime.Now:yyyyMMdd-HHmmssfff}{fi.Extension}";
        var dst = Path.Combine(outputDir.FullName, processedName);

        fi.CopyTo(dst, overwrite: true);
        _logger.LogInformation("Copied to processed: {ProcessedFile}", processedName);

        // 4. Удалить исходник
        fi.Delete();
        _logger.LogDebug("Source file deleted: {SourceFile}", fi.FullName);
    }

    /// <summary>
    /// Файлы АСУМИ приходят в UTF-16LE. Сообщение отправляется байт в байт, поэтому consumer
    /// обязан уметь читать UTF-16 — предупреждаем в лог, чтобы это не выяснилось позже.
    /// </summary>
    private void WarnIfLikelyUtf16(string fileName, byte[] body)
    {
        if (body.Length < 2)
            return;

        var littleEndian = body[0] == 0xFF && body[1] == 0xFE;
        var bigEndian = body[0] == 0xFE && body[1] == 0xFF;
        if (!littleEndian && !bigEndian)
            return;

        _logger.LogWarning(
            "{File} starts with a UTF-16 BOM ({Encoding}). It is published byte-for-byte, so the " +
            "consumer must decode UTF-16. If it expects UTF-8, decoding will fail.",
            fileName, littleEndian ? "UTF-16LE" : "UTF-16BE");
    }
}