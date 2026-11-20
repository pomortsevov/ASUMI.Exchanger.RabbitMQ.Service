using System.Text;
using System.Text.Json;

namespace ASUMI.Exchanger.RabbitMQ.Service.Validation;

/// <summary>
/// Проверяет, сможет ли consumer разобрать файл. Сами байты не меняются: файл
/// отправляется в RabbitMQ как есть, проверка живёт отдельно.
/// <para>
/// Комментарии и висячие запятые разрешены намеренно: примерно каждый шестой файл
/// АСУМИ содержит //-комментарии, и это норма выгрузки, а не порча. Строгий парсер
/// отклонил бы 16% договоров.
/// </para>
/// </summary>
internal static class JsonValidator
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Возвращает true, если файл разбирается. При ошибке в <paramref name="error"/>
    /// кладётся причина, пригодная для лога.
    /// </summary>
    public static bool IsValidJson(byte[] body, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(Decode(body), Options);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (DecoderFallbackException ex)
        {
            // Файл не декодируется целиком — для consumer это тоже нечитаемо.
            error = "not decodable: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            // Наша собственная ошибка проверки. Отклонять по ней нельзя: цена ошибки
            // здесь — потерянный договор, поэтому пропускаем файл в очередь.
            error = "validator error: " + ex.GetType().Name + ": " + ex.Message;
            return true;
        }
    }

    /// <summary>
    /// Декодирует с учётом BOM и убирает сам BOM из результата: JsonDocument
    /// не принимает U+FEFF в начале.
    /// </summary>
    private static string Decode(byte[] body)
    {
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
            return Encoding.Unicode.GetString(body, 2, body.Length - 2);

        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);

        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            return Encoding.UTF8.GetString(body, 3, body.Length - 3);

        return Encoding.UTF8.GetString(body);
    }
}