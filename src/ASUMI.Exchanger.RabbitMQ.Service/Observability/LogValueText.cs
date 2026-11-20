using Serilog.Events;

namespace ASUMI.Exchanger.RabbitMQ.Service.Observability;

/// <summary>
/// Достаёт текст из значения свойства события.
/// </summary>
/// <remarks>
/// ScalarValue.ToString() возвращает значение вместе с кавычками, поэтому наивный
/// ToString() давал cat="\"system\"" и ломал сопоставление категорий по SourceContext:
/// значение заканчивалось на кавычке и не подходило под EndsWith(".FilePublisherWorker").
/// </remarks>
internal static class LogValueText
{
    public static string? AsText(object? value) => value switch
    {
        null => null,
        ScalarValue scalar => scalar.Value as string ?? scalar.ToString(),
        _ => value as string ?? value.ToString()
    };

    public static string? AsText(LogEventProperty property) => AsText(property.Value);
}