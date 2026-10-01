using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using ASUMI.Producer.RabbitMQ.Service.Hosting;
using ASUMI.Producer.RabbitMQ.Service.Infrastructure;
using ASUMI.Producer.RabbitMQ.Service.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
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

                    services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
                    services.AddSingleton<IRabbitMqTopologyInitializer, RabbitMqTopologyInitializer>();
                    services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();

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