namespace ASUMI.Exchanger.RabbitMQ.Service.Hosting;

public static class ServiceConstants
{
    /// <summary>
    /// Имя службы в SCM. Оно же отображаемое имя, поэтому отдельной константы
    /// для DisplayName нет — иначе они разъедутся.
    /// Имя соединения с RabbitMQ берётся из RabbitMqOptions.ConnectionName,
    /// чтобы не расходилось с конфигурацией.
    /// </summary>
    public const string ServiceName = "ASUMI Producer RabbitMQ Service";
}
