using Microsoft.Extensions.Configuration;

namespace ASUMI.Exchanger.RabbitMQ.Service.Hosting;

/// <summary>
/// Определяет, откуда взят пароль RabbitMQ, и подставляет значение в конфигурацию,
/// если в самой конфигурации его нет.
/// <para>
/// Зачем это нужно. Windows собирает для процесса единый блок окружения, объединяя
/// Machine и User scope, поэтому <c>Host.CreateDefaultBuilder</c> обычно видит
/// переменную без всяких подсказок. Но блок окружения зависит от учётной записи:
/// интерактивный сеанс пользователя получает и Machine, и User scope, а служба под
/// LocalSystem — только Machine scope. Переменная, заданная в User scope, на сервере
/// просто не будет видна, и это трудно заметить без диагностики.
/// </para>
/// <para>
/// Значение пароля нигде не логируется — только источник.
/// </para>
/// </summary>
internal static class PasswordResolver
{
    public const string EnvVarName = "RabbitMQ__Password";

    private const string ConfigKey = "RabbitMQ:Password";

    /// <summary>
    /// Приоритет: Machine scope выше User scope. Если переменная задана в обоих
    /// и значения различаются, побеждает машинная — её читает и служба, и сеанс.
    /// </summary>
    public static string? Resolve(IConfiguration configuration, out string source)
    {
        var fromMachine = Read(EnvironmentVariableTarget.Machine);
        var fromUser = Read(EnvironmentVariableTarget.User);

        if (!string.IsNullOrWhiteSpace(fromMachine))
        {
            configuration[ConfigKey] = fromMachine;
            source = $"Machine environment scope ({EnvVarName})";
            return fromMachine;
        }

        if (!string.IsNullOrWhiteSpace(fromUser))
        {
            configuration[ConfigKey] = fromUser;
            source = $"User environment scope ({EnvVarName})";
            return fromUser;
        }

        // Переменная могла попасть в окружение процесса иным способом — например, из
        // аргументов командной строки CI. Конфигурация её уже видит.
        var fromConfiguration = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            source = "configuration (appsettings.Production.json or command line)";
            return fromConfiguration;
        }

        source = string.Empty;
        return null;
    }

    private static string? Read(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(EnvVarName, target);
        }
        catch (System.Security.SecurityException)
        {
            // Нет прав на чтение чужого ветки реестра — не повод падать.
            return null;
        }
    }
}