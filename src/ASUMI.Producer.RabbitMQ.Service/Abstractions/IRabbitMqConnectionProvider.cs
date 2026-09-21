using RabbitMQ.Client;

namespace ASUMI.Producer.RabbitMQ.Service.Abstractions;

public interface IRabbitMqConnectionProvider : IDisposable
{
    IModel CreateChannel();
}