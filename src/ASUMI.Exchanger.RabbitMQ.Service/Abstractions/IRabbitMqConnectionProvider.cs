using RabbitMQ.Client;

namespace ASUMI.Exchanger.RabbitMQ.Service.Abstractions;

public interface IRabbitMqConnectionProvider : IDisposable
{
    /// <summary>Есть ли сейчас открытое соединение с RabbitMQ.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Возвращает канал, если соединение установлено. Возвращает null, если соединения нет —
    /// вызывающий код должен подождать и повторить попытку.
    /// </summary>
    IModel? TryCreateChannel();

    /// <summary>
    /// Ждёт, пока соединение появится. Бросает OperationCanceledException, если отменено.
    /// Возвращает канал, когда соединение доступно.
    /// </summary>
    Task<IModel> WaitForChannelAsync(CancellationToken cancellationToken);
}