using ASUMI.Producer.RabbitMQ.Service.Abstractions;
using ASUMI.Producer.RabbitMQ.Service.Configuration;
using ASUMI.Producer.RabbitMQ.Service.Hosting;
using ASUMI.Producer.RabbitMQ.Service.Infrastructure;
using ASUMI.Producer.RabbitMQ.Service.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Settings.Configuration;
using System.Globalization;

this is not valid C#

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
            Log.Information("Starting {ServiceName}...", ServiceConstants.ServiceName);

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