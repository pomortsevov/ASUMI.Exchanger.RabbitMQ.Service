using System.ComponentModel.DataAnnotations;

namespace ASUMI.Producer.RabbitMQ.Service.Configuration;

public sealed class AsumiProducerRabbitMqOptions
{
    public const string SectionName = "AsumiProducerRabbitMq";

    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 5;

    public bool Enabled { get; set; } = true;
}
