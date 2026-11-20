namespace ASUMI.Exchanger.RabbitMQ.Service.Abstractions;

/// <summary>
/// Публикация сообщения в RabbitMQ.
/// Не бросает исключение при разрыве соединения — возвращает false.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Публикует сообщение в exchange с routing key из настроек.
    /// Возвращает true, если публикация прошла успешно; false — если соединение недоступно.
    /// </summary>
    Task<bool> PublishAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}