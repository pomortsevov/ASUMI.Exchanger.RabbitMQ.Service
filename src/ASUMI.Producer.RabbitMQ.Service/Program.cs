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

                        cfg.WriteTo.Sink(new LokiSink(loki, invariant));
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
}