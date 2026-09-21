using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ASUMI.Producer.RabbitMQ.Service.Workers;

public sealed class FilePublisherWorker : BackgroundService
{
    private readonly ILogger<FilePublisherWorker> _logger;
    private readonly FilePublisherOptions _options;

    public FilePublisherWorker(
        ILogger<FilePublisherWorker> logger,
        IOptions<FilePublisherOptions> options)
    {
        _logger = logger;
        _options = options.Value;
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
        // 1. Прочитать содержимое
        var content = await File.ReadAllTextAsync(fi.FullName, Encoding.UTF8, stoppingToken);
        var body = Encoding.UTF8.GetBytes(content);

        _logger.LogInformation(
            "Read {File} ({Chars} chars, {Bytes} bytes UTF-8).",
            fi.Name, content.Length, body.Length);

        // 2. TODO: публикация в RabbitMQ — добавим на следующем этапе.
        // Пока просто логируем.
        _logger.LogInformation("Would publish {File} to RabbitMQ (not implemented yet).", fi.Name);

        // 3. Переместить в processed
        var processedName = $"{Path.GetFileNameWithoutExtension(fi.Name)}-" +
                            $"{DateTime.Now:yyyyMMdd-HHmmssfff}{fi.Extension}";
        var dst = Path.Combine(outputDir.FullName, processedName);

        fi.CopyTo(dst, overwrite: true);
        _logger.LogInformation("Copied to processed: {ProcessedFile}", processedName);

        // 4. Удалить исходник
        fi.Delete();
        _logger.LogDebug("Source file deleted: {SourceFile}", fi.FullName);
    }
}