using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ASUMI.Producer.RabbitMQ.Service.Observability;

/// <summary>
/// Отправляет логи в Loki через push API (POST /loki/api/v1/push).
/// <para>
/// Сделан вручную вместо Serilog.Sinks.Loki: тот последний раз обновлялся около 2020 года
/// и тянет Newtonsoft.Json 12 вместе с Serilog.Sinks.Http 5, что несовместимо по духу
/// с Serilog 8.
/// </para>
/// <para>
/// Принцип: Loki не должен влиять на службу. События складываются в ограниченную очередь,
/// отправка идёт по таймеру в фоне, а любая ошибка приводит к отбрасыванию батча и записи
/// в SelfLog. Исключения наружу не выпускаются никогда.
/// </para>
/// </summary>
internal sealed class LokiSink : ILogEventSink, IDisposable
{
    private const string PushPath = "/loki/api/v1/push";

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly LokiOptions _options;
    private readonly HttpClient _http;
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly IFormatProvider _formatProvider;
    private readonly Dictionary<string, string> _labels;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Timer _timer;
    private readonly LogEventLevel _minimumLevel;

    private long _droppedSinceSuccess;
    private long _failedBatches;
    private bool _disposed;

    public LokiSink(LokiOptions options, IFormatProvider formatProvider, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        _options = options;
        _formatProvider = formatProvider;
        _minimumLevel = minimumLevel;

        _http = new HttpClient
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        _formatter = new MessageTemplateTextFormatter(
            // level= в начале строки нужен, чтобы Loki сам определил уровень и положил
            // запись в метку detected_level, а не в unknown.
            //
            // msg={Message:j} — двоеточие j рендерит сообщение как JSON-строку, то есть
            // С КАВЫЧКАМИ и с экранированием. Без кавычек строка вида
            // "level=ERR Moved Fail-!! Z_....json to failed: ..." не разбирается
            // logfmt-парсером: имена файлов АСУМИ с пробелами выглядят как начало
            // новых полей, и запись в Grafana распадается.
            "level={Level:u3} msg={Message:j} {Properties:j}",
            formatProvider);

        _labels = BuildLabels(options);

        _timer = new Timer(
            _ => _ = FlushAsync(),
            null,
            TimeSpan.FromSeconds(options.FlushPeriodSeconds),
            TimeSpan.FromSeconds(options.FlushPeriodSeconds));
    }

    private static Dictionary<string, string> BuildLabels(LokiOptions options)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(environment))
            environment = "Production";

        return new Dictionary<string, string>
        {
            ["job"] = options.Job,
            ["service_name"] = options.ServiceName,
            ["env"] = environment,
            ["host"] = Environment.MachineName
        };
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minimumLevel)
            return;

        if (_disposed)
            return;

        try
        {
            using var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            _queue.Enqueue(writer.ToString());
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("LokiSink.Emit failed: {0}", ex);
        }

        while (_queue.Count > _options.QueueCapacity && _queue.TryDequeue(out _))
        {
            Interlocked.Increment(ref _droppedSinceSuccess);
        }
    }

    private async Task FlushAsync()
    {
        if (_disposed || _queue.IsEmpty)
            return;

        if (!await _flushGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            var count = Math.Min(_options.BatchSize, _queue.Count);
            var values = new List<string>(count * 2);
            var taken = 0;

            for (var i = 0; i < count; i++)
            {
                if (!_queue.TryDequeue(out var line))
                    break;

                // Loki ждёт время в наносекундах эпохи Unix, строкой.
                values.Add((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L + i)
                    .ToString(CultureInfo.InvariantCulture));
                values.Add(line);
                taken++;
            }

            if (taken == 0)
                return;

            await PostAsync(values, taken).ConfigureAwait(false);

            if (Interlocked.Exchange(ref _droppedSinceSuccess, 0) > 0)
            {
                SelfLog.WriteLine("LokiSink dropped {0} events earlier because the queue was full.",
                    _droppedSinceSuccess);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBatches);
            SelfLog.WriteLine("LokiSink flush failed ({0} batches so far): {1}",
                _failedBatches, ex);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task PostAsync(List<string> values, int count)
    {
        var payload = BuildPayload(values, count);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(PushPath, content).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            SelfLog.WriteLine("LokiSink got {0} from Loki: {1}", response.StatusCode, body[..Math.Min(400, body.Length)]);
        }
    }

    private string BuildPayload(List<string> values, int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("streams");

            writer.WriteStartObject();
            writer.WriteStartObject("stream");
            foreach (var label in _labels)
                writer.WriteString(label.Key, label.Value);
            writer.WriteEndObject();

            writer.WriteStartArray("values");
            for (var i = 0; i < count; i++)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(values[i * 2]);
                writer.WriteStringValue(values[i * 2 + 1]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Dispose();

        try
        {
            // Последний сброс перед выходом, чтобы не потерять хвост логов при остановке.
            _ = FlushAsync();
            _flushGate.Wait(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("LokiSink.Dispose failed: {0}", ex);
        }

        _http.Dispose();
        _flushGate.Dispose();
    }
}