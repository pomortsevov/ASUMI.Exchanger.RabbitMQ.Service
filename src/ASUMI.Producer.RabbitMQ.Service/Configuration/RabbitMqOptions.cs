using System.ComponentModel.DataAnnotations;

namespace ASUMI.Producer.RabbitMQ.Service.Configuration;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    [Required]
    public string HostName { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required]
    public string VirtualHost { get; set; } = "/";

    [Required]
    public string UserName { get; set; } = "guest";

    /// <summary>
    /// Пароль намеренно пуст в appsettings.json — секрет хранится в переменной окружения
    /// RabbitMQ__Password или в appsettings.Production.json рядом с exe.
    /// Не [Required]: служба должна подниматься и ждать брокер, а не падать без секрета.
    /// Пока пароль не задан, файлы остаются в SourcePath.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    [Required]
    public string ExchangeName { get; set; } = "asumi.exchange";

    [Required]
    public string QueueName { get; set; } = "asumi.queue";

    [Required]
    public string RoutingKey { get; set; } = "asumi.json";

    [Range(0, int.MaxValue)]
    public int MessageTtlMs { get; set; } = 30000;

    /// <summary>
    /// Тип очереди: quorum, classic или stream. Должен совпадать с уже созданной в брокере —
    /// иначе QueueDeclare вернёт 406 PRECONDITION_FAILED. Тип нельзя сменить после создания.
    /// </summary>
    [Required]
    public string QueueType { get; set; } = "quorum";

    /// <summary>
    /// Размер начальной группы реплик для quorum-очереди. Учитывается только при создании
    /// очереди. Для существующей quorum-очереди значение в брокере должно совпадать,
    /// иначе QueueDeclare вернёт 406.
    /// </summary>
    [Range(1, 15)]
    public int QuorumInitialGroupSize { get; set; } = 3;

    /// <summary>
    /// Сколько ждать подтверждения от брокера, что сообщение принято, перед тем как
    /// считать публикацию успешной. Нужен, чтобы обнаружить публикацию в никуда.
    /// </summary>
    [Range(1, 60)]
    public int PublishConfirmTimeoutSeconds { get; set; } = 5;

    public bool Persistent { get; set; } = true;

    public bool AutomaticRecovery { get; set; } = true;

    [Range(1, 3600)]
    public int NetworkRecoveryIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Пауза между попытками подключения, если RabbitMQ недоступен. Не путать с NetworkRecoveryIntervalSeconds —
    /// тот используется после того, как соединение уже было установлено, а потом упало.
    /// </summary>
    [Range(1, 300)]
    public int ReconnectIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Как часто повторно писать предупреждение о недоступном брокере, пока он лежит.
    /// Первая неудача логируется всегда, дальше — не чаще этого интервала, остальное
    /// уходит в Debug и в Loki не попадает.
    /// </summary>
    [Range(10, 3600)]
    public int RepeatWarningIntervalSeconds { get; set; } = 300;

    [Required]
    public string ConnectionName { get; set; } = "ASUMI.Producer.RabbitMQ.Service";
}