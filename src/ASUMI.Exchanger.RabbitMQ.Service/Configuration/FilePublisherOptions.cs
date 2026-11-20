using System.ComponentModel.DataAnnotations;

namespace ASUMI.Exchanger.RabbitMQ.Service.Configuration;

public sealed class FilePublisherOptions
{
    public const string SectionName = "FilePublisher";

    /// <summary>Включён ли воркер. Удобно для быстрого отключения без пересборки.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Папка, откуда читаются *.json-файлы.</summary>
    [Required]
    public string SourcePath { get; set; } = "C:\\SourceJson";

    /// <summary>Папка, куда копируются успешно обработанные файлы.</summary>
    [Required]
    public string ProcessedPath { get; set; } = "C:\\ProcessedJson";

    /// <summary>
    /// Папка для файлов, которые не разбираются как JSON. Из очереди они исключены,
    /// но и не удаляются — остаются для разбора.
    /// </summary>
    [Required]
    public string FailedPath { get; set; } = "C:\\FailedJson";

    /// <summary>Маска файлов для обработки.</summary>
    [Required]
    public string FileMask { get; set; } = "*.json";

    /// <summary>Как часто проверять папку, секунды.</summary>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 10;
}