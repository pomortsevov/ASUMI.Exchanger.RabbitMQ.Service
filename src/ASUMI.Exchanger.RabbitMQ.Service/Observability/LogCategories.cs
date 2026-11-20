using Serilog.Core;
using Serilog.Core.Enrichers;
using Serilog.Events;

namespace ASUMI.Exchanger.RabbitMQ.Service.Observability;

/// <summary>
/// Категории записей журнала. Попадают в Loki отдельным полем cat, поэтому фильтруются
/// запросом: {job="ASUMI.Producer.RabbitMQ.Service"} | cat="system"
/// (метки Loki пока остаются прежними — см. комментарий в LokiOptions).
/// </summary>
public static class LogCategories
{
    /// <summary>Инфраструктура: подключение к брокеру, топология, метрики, порты.</summary>
    public const string System = "system";

    /// <summary>Работа с данными: сканирование папки, чтение файлов, публикация.</summary>
    public const string Data = "data";

    /// <summary>Запуск и конфигурация: источник пароля, включение сендоров.</summary>
    public const string Config = "config";
}

/// <summary>
/// Проставляет cat по SourceContext, чтобы не указывать категорию в каждом вызове
/// LogXxx. Enricher выполняется после провайдеров, поэтому SourceContext уже есть.
/// </summary>
public sealed class LogCategoryEnricher : ILogEventEnricher
{
    private const string PropertyName = "cat";

    /// <summary>Классы, которые работают с данными, а не с инфраструктурой.</summary>
    private static readonly string[] DataSources =
    {
        "FilePublisherWorker"
    };

    /// <summary>Запуск и разбор конфигурации.</summary>
    private static readonly string[] ConfigSources =
    {
        "Program",
        "PasswordResolver",
        "JsonValidator"
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.ContainsKey(PropertyName))
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, Resolve(logEvent)));
    }

    private static string Resolve(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value)
            ? LogValueText.AsText(value)
            : null;

        if (string.IsNullOrEmpty(source))
            return LogCategories.System;

        if (Matches(source, ConfigSources))
            return LogCategories.Config;

        if (Matches(source, DataSources))
            return LogCategories.Data;

        return LogCategories.System;
    }

    private static bool Matches(string source, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            // SourceContext — полное имя типа, поэтому сравниваем по хвосту после точки.
            if (source.EndsWith("." + candidate, StringComparison.Ordinal) ||
                string.Equals(source, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}