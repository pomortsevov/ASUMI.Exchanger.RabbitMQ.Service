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

    [Required]
    public string Password { get; set; } = "guest";

    [Required]
    public string ExchangeName { get; set; } = "asumi.exchange";

    [Required]
    public string QueueName { get; set; } = "asumi.queue";

    [Required]
    public string RoutingKey { get; set; } = "asumi.json";

    [Range(0, int.MaxValue)]
    public int MessageTtlMs { get; set; } = 30000;

    public bool Persistent { get; set; } = true;

    public bool AutomaticRecovery { get; set; } = true;

    [Range(1, 3600)]
    public int NetworkRecoveryIntervalSeconds { get; set; } = 10;

    [Required]
    public string ConnectionName { get; set; } = "ASUMI.Producer.RabbitMQ.Service";
}