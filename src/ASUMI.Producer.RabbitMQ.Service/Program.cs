using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using ASUMI.Producer.RabbitMQ.Service.Hosting;
using ASUMI.Producer.RabbitMQ.Service.Infrastructure;
using ASUMI.Producer.RabbitMQ.Service.Observability;
using ASUMI.Producer.RabbitMQ.Service.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Debugging;
using Serilog.Settings.Configuration;
using System.Globalization;
using System.Reflection;

namespace ASUMI.Producer.RabbitMQ.Service;

public class Program
{
    public static int Main(string[] args)
    {
        var invariant = CultureInfo.InvariantCulture;
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "asumi-producer-rabbitmq-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: invariant)
            .WriteTo.File(
                path: logPath,
                formatProvider: invariant,
                rollingInterval: RollingInterval.Day)
            .CreateBootstrapLogger();

        try
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? "unknown";

            Log.Information("Starting {ServiceName} v{Version}...", ServiceConstants.ServiceName, version);

            // Один экземпляр на машину. Два процесса сканируют SourcePath параллельно и
            // могут опубликовать один договор дважды. На проде это исключено самой SCM,
            // а на машине разработчика легко получить, запустив службу и F5 одновременно.
            // Global\, а не Local\, иначе служба в сессии 0 и консольный экземпляр
            // не увидят друг друга.
            using var instanceMutex = new Mutex(
                initiallyOwned: true, $"Global\\{ServiceConstants.ServiceName}", out var isFirstInstance);

            if (!isFirstInstance)
            {
                Log.Fatal(
                    "Another instance of {ServiceName} is already running on this machine. " +
                    "Two instances would publish the same contract twice. Stop the running one first.",
                    ServiceConstants.ServiceName);
                return 2;
            }

            // Заполняется в ConfigureServices — там PasswordResolver смотрит оба scope.
            var passwordSource = string.Empty;
            var lokiSinkEnabled = false;
            var lokiSinkUrl = string.Empty;
            var lokiSinkJob = string.Empty;

            var host = Host.CreateDefaultBuilder(args)
                .UseWindowsService(o => o.ServiceName = ServiceConstants.ServiceName)
                .UseSerilog((ctx, cfg) =>
                {
                    cfg.ReadFrom.Configuration(ctx.Configuration, new ConfigurationReaderOptions())
                       .Enrich.FromLogContext()
                       .Enrich.With(new LogCategoryEnricher())
                       .Enrich.WithProperty("Service", ServiceConstants.ServiceName)
                       .WriteTo.Console(formatProvider: invariant)
                       .WriteTo.File(
                           path: logPath,
                           formatProvider: invariant,
                           rollingInterval: RollingInterval.Day,
                           retainedFileCountLimit: 14,
                           shared: true);

                    // Loki добавляется только если явно включён в конфиге: недоступный
                    // Loki не должен мешать службе работать.
                    var loki = ctx.Configuration.GetSection(LokiOptions.SectionName).Get<LokiOptions>();
                    if (loki is { Enabled: true })
                    {
                        // Диагностика самого sink'а — в отдельный файл, чтобы увидеть
                        // причины отбрасывания записей, не получая их обратно в лог.
                        var selfLogPath = Path.Combine(AppContext.BaseDirectory, "logs", "loki-selflog.log");
                        SelfLog.Enable(msg => { try { File.AppendAllText(selfLogPath, msg + Environment.NewLine); } catch { /* SelfLog не должен бросать */ } });

                        cfg.WriteTo.Sink(new LokiSink(loki));
                        lokiSinkEnabled = true;
                        lokiSinkUrl = loki.Url;
                        lokiSinkJob = loki.Job;
                    }
                })
                .ConfigureServices((ctx, services) =>
                {
                    // Выполняется до бинда опций ниже, чтобы значение успело попасть в конфигурацию.
                    PasswordResolver.Resolve(ctx.Configuration, out var src);
                    passwordSource = src;

                    services.AddOptions<RabbitMqOptions>()
                        .Bind(ctx.Configuration.GetSection(RabbitMqOptions.SectionName))
                        .ValidateDataAnnotations()
                        .ValidateOnStart();

                    services.AddOptions<FilePublisherOptions>()
                        .Bind(ctx.Configuration.GetSection(FilePublisherOptions.SectionName))
                        .ValidateDataAnnotations()
                        .ValidateOnStart();

                    services.AddOptions<MetricsOptions>()
                        .Bind(ctx.Configuration.GetSection(MetricsOptions.SectionName))
                        .ValidateDataAnnotations()
                        .ValidateOnStart();

                    // До первого соединения метрика не публикуется, и на графике был бы провал.
                    ProducerMetrics.RabbitMqConnected.Set(0);

                    services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
                    services.AddSingleton<IRabbitMqTopologyInitializer, RabbitMqTopologyInitializer>();
                    services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();

                    // Метрики первыми, чтобы порт поднялся до начала публикации.
                    services.AddHostedService<MetricsServerHostedService>();
                    services.AddHostedService<RabbitMqTopologyHostedService>();
                    services.AddHostedService<FilePublisherWorker>();
                })
                .Build();

            // Откуда взят пароль — самая частая причина «работало локально, не работает
            // на сервере». Значение не логируем, только источник.
            var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            if (!string.IsNullOrEmpty(passwordSource))
            {
                startupLogger.LogInformation(
                    "RabbitMQ password resolved from {Source}.", passwordSource);
            }
            else
            {
                startupLogger.LogError(
                    "RabbitMQ password not found. Checked Machine scope, User scope and configuration. " +
                    "Set {EnvVarName} in Machine scope for a Windows service — User scope is not " +
                    "visible to a process running as LocalSystem, or put Password into " +
                    "appsettings.Production.json next to the exe. Publishing will fail until then.",
                    PasswordResolver.EnvVarName);
            }

            if (lokiSinkEnabled)
            {
                startupLogger.LogInformation(
                    "Loki sink enabled for {Url}, job={Job}.", lokiSinkUrl, lokiSinkJob);
            }
            else
            {
                startupLogger.LogInformation("Loki sink disabled by configuration.");
            }

            // Границы запуска видны и в файле, и в Loki. Без них перезапуск неотличим от
            // паузы: между последней записью одного процесса и первой следующего нет
            // ничего, и в Grafana выглядит так, будто служба замолчала. Разделитель
            // печатается ПОСЛЕ Build(), иначе строка уйдёт только в консоль и файл,
            // а в Loki рестарт по-прежнему не виден.
            var startedAt = DateTimeOffset.UtcNow;
            var pid = Environment.ProcessId;
            var machine = Environment.MachineName;

            startupLogger.LogInformation(
                StartBanner,
                ServiceConstants.ServiceName, version, pid, machine,
                startedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", invariant));

            // Метка остановки нужна, чтобы отличить плановый перезапуск от падения и
            // увидеть, сколько служба проработала. Регистрируется до Run, иначе
            // остановка по сигналу от SCM не попала бы в лог вовсе.
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
                startupLogger.LogInformation(
                    StopBanner,
                    ServiceConstants.ServiceName, version, pid, machine,
                    FormatUptime(DateTimeOffset.UtcNow - startedAt)));

            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{ServiceName} terminated unexpectedly.", ServiceConstants.ServiceName);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Разделитель на старте. Хеши нужны для двух вещей: глазу в консоли и поиску в Loki
    /// запросом |= "####" — так находится любая граница запуска независимо от уровня.
    /// </summary>
    /// <remarks>
    /// Шаблон константный, а не собирается склейкой: так срабатывает проверка CA2254 и
    /// значения остаются полями события, а не готовым текстом. Version, Pid и Host вставлены
    /// намеренно — без них два рестарта подряд неразличимы, а PID нужен, чтобы сопоставить
    /// запись с процессом в диспетчере и с журналом событий Windows.
    /// </remarks>
    private const string StartBanner =
        "######## {ServiceName} STARTED v{Version} pid={Pid} host={Host} at {StartedAt} ########";

    /// <summary>Разделитель на остановке: видно, что выход был штатным, и сколько шла служба.</summary>
    private const string StopBanner =
        "######## {ServiceName} STOPPED v{Version} pid={Pid} host={Host} uptime={Uptime} ########";

    /// <summary>Время работы в форме чч:мм:сс. Часы набегают через сутки и дальше.</summary>
    private static string FormatUptime(TimeSpan uptime) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}");
}