using Serilog.Formatting.Display;
using Serilog.Events;

namespace ASUMI.Exchanger.RabbitMQ.Service.Observability;

/// <summary>
/// Формирует строку записи для Loki в виде logfmt:
/// <c>level=ERR cat="data" msg="..." {"SourceContext":"..."}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Собственный форматтер нужен потому, что категорию надо вывести отдельным полем.
/// Через шаблон {cat} этого не сделать: если свойства нет, в вывод попадёт буквальный
/// текст «cat», то есть поле без значения у всех записей без категории.
/// </para>
/// <para>
/// Само сообщение и JSON-свойства рендерит Serilog — ручная сборка JSON неверно
/// обрабатывает составные значения свойств и даёт в хвосте мусор вида
/// {"Host":"[Host, \"...\"]"}. category попадает в хвост дважды, в поле и в JSON:
/// это безвредно, зато не требует править чужое событие, общее для всех сенков.
/// </para>
/// </remarks>
internal sealed class LokiLogFormatter
{
    private const string CategoryPropertyName = "cat";

    /// <summary>
    /// :j рендерит сообщение как JSON-строку, то есть с кавычками и экранированием.
    /// Без этого logfmt-парсер не находит границу сообщения: имена файлов АСУМИ с
    /// пробелами выглядели бы как начало новых полей.
    /// </summary>
    private readonly MessageTemplateTextFormatter _messageFormatter =
        new("msg={Message:j}", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Свойства события, не попавшие в шаблон сообщения.</summary>
    private readonly MessageTemplateTextFormatter _propertiesFormatter =
        new("{Properties:j}", System.Globalization.CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        // level= нужен Loki для detected_level, иначе записи попадают в unknown.
        output.Write("level=");
        output.Write(RenderLevel(logEvent.Level));

        output.Write(' ');
        output.Write(CategoryPropertyName);
        output.Write('=');
        var category = logEvent.Properties.TryGetValue(CategoryPropertyName, out var cat)
            ? LogValueText.AsText(cat)
            : null;
        output.Write(Quote(category ?? LogCategories.System));

        output.Write(' ');
        _messageFormatter.Format(logEvent, output);

        output.Write(' ');
        _propertiesFormatter.Format(logEvent, output);
    }

    private static string RenderLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "INF"
    };

    /// <summary>Значение поля берётся в кавычках, иначе пробелы внутри него ломают logfmt.</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}