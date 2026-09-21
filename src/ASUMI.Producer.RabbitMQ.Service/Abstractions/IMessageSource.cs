namespace ASUMI.Producer.RabbitMQ.Service.Abstractions;

/// <summary>
/// сточник сообщений. Сегодня — заглушка, завтра — FileSystemMessageSource, RabbitMqMessageSource.
/// </summary>
public interface IMessageSource
{
    string Name { get; }

    IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken);
}
