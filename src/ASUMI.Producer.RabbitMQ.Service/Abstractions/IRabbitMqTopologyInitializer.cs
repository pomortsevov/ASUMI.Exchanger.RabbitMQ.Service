namespace ASUMI.Producer.RabbitMQ.Service.Abstractions;

/// <summary>
/// Объявляет топологию RabbitMQ (exchange, queue, bind) при старте приложения.
/// Устойчив к недоступному RabbitMQ: ждёт и повторяет попытки.
/// </summary>
public interface IRabbitMqTopologyInitializer
{
    Task EnsureTopologyAsync(CancellationToken cancellationToken);
}