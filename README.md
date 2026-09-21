# ASUMI.Producer.RabbitMQ.Service

Windows-служба на .NET 8 (Worker Service) для публикации сообщений из ASUMI в RabbitMQ.

Работает в двух режимах:

- **Консоль** — для локальной отладки (`dotnet run`).
- **Служба Windows** — для продакшена (SCM, автозапуск, восстановление).

## Стек

- .NET 8 (SDK 8.0.101+)
- `Microsoft.Extensions.Hosting` 8.0.0
- `Microsoft.Extensions.Hosting.WindowsServices` 8.0.0
- Serilog (консоль + файл с ротацией по дням)
- `Microsoft.Extensions.Options.DataAnnotations` для валидации конфигурации

## Структура репозитория

```
ASUMI.Producer.RabbitMQ.Service/
├── ASUMI.Producer.RabbitMQ.Service.sln
├── Directory.Build.props
├── .gitignore
├── README.md
├── src/
│   └── ASUMI.Producer.RabbitMQ.Service/
│       ├── ASUMI.Producer.RabbitMQ.Service.csproj
│       ├── Program.cs
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       ├── Properties/
│       │   └── launchSettings.json
│       ├── Configuration/
│       │   └── AsumiProducerRabbitMqOptions.cs
│       ├── Abstractions/
│       │   └── IMessageSource.cs
│       ├── Hosting/
│       │   └── ServiceConstants.cs
│       └── Workers/
│           └── HeartbeatWorker.cs
└── tests/
    └── ASUMI.Producer.RabbitMQ.Service.Tests/
```

## Требования

- **Для сборки**: .NET SDK 8.0.101 или новее.
- **Для запуска**: .NET Runtime 8.0.x (`Microsoft.NETCore.App 8.0.x`).
- **Для установки службы**: PowerShell от имени администратора.

Проверить установленные SDK и Runtime:

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

## Локальный запуск (консоль)

Из корня репозитория:

```powershell
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

По умолчанию приложение возьмёт окружение `Production` и `appsettings.json`. Чтобы использовать `appsettings.Development.json`:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

Остановить — `Ctrl+C`.

Ожидаемый вывод в консоль:

```
[13:30:00 INF] Starting ASUMI Producer RabbitMQ Service...
[13:30:00 INF] HeartbeatWorker started. Interval: 5s
[13:30:00 INF] Hello World #1 at 13:30:00
[13:30:05 INF] Hello World #2 at 13:30:05
...
```

## Публикация

Опубликовать Release-сборку в папку `C:\Services\ASUMI.Producer.RabbitMQ.Service`:

```powershell
dotnet publish src\ASUMI.Producer.RabbitMQ.Service -c Release -o C:\Services\ASUMI.Producer.RabbitMQ.Service
```

> **Важно:** папка `C:\Services\...` должна быть **свободна** или `.exe` не должен быть запущен. Если служба уже установлена и работает, сначала остановите и удалите её (см. ниже «Обновление службы»).

На выходе в `C:\Services\ASUMI.Producer.RabbitMQ.Service\` появятся:

```
ASUMI.Producer.RabbitMQ.Service.exe    ← главный файл
appsettings.json
appsettings.Development.json
```

Проверить:

```powershell
ls C:\Services\ASUMI.Producer.RabbitMQ.Service
```

## Установка как службы Windows

Все команды ниже — из PowerShell **от имени администратора**.

### 1. Создать службу

```powershell
sc.exe create "ASUMI Producer RabbitMQ Service" binPath= "C:\Services\ASUMI.Producer.RabbitMQ.Service\ASUMI.Producer.RabbitMQ.Service.exe" start= auto DisplayName= "ASUMI Producer RabbitMQ Service"
```

> **Синтаксис `sc.exe` критичен:** пробел **после** `binPath=`, `start=` и `DisplayName=` обязателен. Вводите команду **одной строкой** — переносы через PowerShell `>>` могут привести к ошибке.

### 2. Добавить описание (опционально, для оснастки «Службы»)

```powershell
sc.exe description "ASUMI Producer RabbitMQ Service" "Publishes messages from ASUMI source to RabbitMQ."
```

### 3. Запустить

```powershell
sc.exe start "ASUMI Producer RabbitMQ Service"
```

### 4. Проверить статус

```powershell
sc.exe query "ASUMI Producer RabbitMQ Service"
```

Должно быть `STATE: 4 RUNNING`. В первые 1–2 секунды после старта — `STATE: 2 START_PENDING`, это нормально.

### 5. Проверить процесс

```powershell
Get-Process -Name "ASUMI.Producer.RabbitMQ.Service"
```

## Логи

При запуске как службы логи пишутся **рядом с `.exe`** (благодаря `AppContext.BaseDirectory` в `Program.cs`):

```
C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-YYYYMMDD.log
```

Ротация — по дням, хранение — 14 файлов. Прочитать последние 20 строк:

```powershell
Get-Content C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-*.log -Tail 20
```

При запуске через `dotnet run` логи будут в `bin\Debug\net8.0\win-x64\logs\` (или `bin\Release\...`), тоже рядом с `.exe`.

## Конфигурация

Настройки читаются из:

1. `appsettings.json` — общие.
2. `appsettings.{DOTNET_ENVIRONMENT}.json` — переопределения для среды (`Development`, `Production`, ...).
3. Переменные окружения с префиксом `ASUMI_` (стандартное поведение `Host.CreateDefaultBuilder`).

Пример `appsettings.json`:

```json
{
  "AsumiProducerRabbitMq": {
    "Enabled": true,
    "IntervalSeconds": 5
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    }
  }
}
```

### Приоритет сред

- **Консольный запуск** — по умолчанию `Production`. Чтобы использовать `Development`, задайте `$env:DOTNET_ENVIRONMENT = "Development"`.
- **Служба** — `Production`, если переменная не задана в реестре службы.

### Переопределение через переменные окружения

Любое значение можно переопределить без правки `appsettings.json`:

```powershell
[Environment]::SetEnvironmentVariable("AsumiProducerRabbitMq__IntervalSeconds", "10", "Machine")
```

> Двойное подчёркивание `__` — разделитель вложенности. `AsumiProducerRabbitMq__IntervalSeconds` = `AsumiProducerRabbitMq.IntervalSeconds` в JSON.

Чтобы изменения подхватились службой — перезапустите её:

```powershell
sc.exe stop "ASUMI Producer RabbitMQ Service"
sc.exe start "ASUMI Producer RabbitMQ Service"
```

## Управление службой

| Действие | Команда |
|---|---|
| Запустить | `sc.exe start "ASUMI Producer RabbitMQ Service"` |
| Остановить | `sc.exe stop "ASUMI Producer RabbitMQ Service"` |
| Перезапустить | `sc.exe stop ...` затем `sc.exe start ...` |
| Статус | `sc.exe query "ASUMI Producer RabbitMQ Service"` |
| Удалить | `sc.exe delete "ASUMI Producer RabbitMQ Service"` |
| Оснастка «Службы» | `services.msc` |

## Обновление службы

Правильный порядок, чтобы не словить `IOException: file is being used by another process`:

```powershell
# 1. Остановить
sc.exe stop "ASUMI Producer RabbitMQ Service"
Start-Sleep -Seconds 5

# 2. Удалить
sc.exe delete "ASUMI Producer RabbitMQ Service"
Start-Sleep -Seconds 2

# 3. Убедиться, что процесс мёртв
Get-Process -Name "ASUMI.Producer.RabbitMQ.Service" -ErrorAction SilentlyContinue

# 4. Пересобрать
cd C:\Develop\PilotICE\RabbitMQ\ASUMI.Producer.RabbitMQ.Service
dotnet publish src\ASUMI.Producer.RabbitMQ.Service -c Release -o C:\Services\ASUMI.Producer.RabbitMQ.Service

# 5. Создать и запустить заново
sc.exe create "ASUMI Producer RabbitMQ Service" binPath= "C:\Services\ASUMI.Producer.RabbitMQ.Service\ASUMI.Producer.RabbitMQ.Service.exe" start= auto
sc.exe start "ASUMI Producer RabbitMQ Service"

# 6. Проверить
sc.exe query "ASUMI Producer RabbitMQ Service"
Get-Content C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-*.log -Tail 10
```

## Диагностика

### Служба не запускается

Посмотреть журнал событий Windows:

```powershell
Get-EventLog -LogName Application -Source "ASUMI Producer RabbitMQ Service" -Newest 10 -ErrorAction SilentlyContinue | Format-List TimeGenerated, EntryType, Message
```

Или все ошибки приложений за последний час:

```powershell
Get-EventLog -LogName Application -EntryType Error -After (Get-Date).AddHours(-1) -Newest 20 | Format-List TimeGenerated, Source, Message
```

### Логи не появляются

Проверьте, что папка `logs\` создалась рядом с `.exe`:

```powershell
ls C:\Services\ASUMI.Producer.RabbitMQ.Service\logs
```

Если папки нет — проверьте, что в `Program.cs` путь вычисляется через `AppContext.BaseDirectory`, а не `"logs/..."` (иначе логи уйдут в рабочую директорию процесса, для службы — `C:\Windows\System32\logs\`).

### `Access Denied` при `sc.exe create`

PowerShell запущен не от администратора. Закройте и откройте заново: **ПКМ по «Windows PowerShell» → Запуск от имени администратора**. Проверка:

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

Должно вернуть `True`.

### `IOException: file is being used by another process` при `dotnet publish`

`.exe` залочен работающей службой. Остановите и удалите её (см. «Обновление службы»), затем повторите публикацию.

### Служба висит в `START_PENDING` больше 30 секунд

SCM даёт 30 секунд на переход в `RUNNING`. Если превысить — служба прибивается, в Event Log будет ошибка 7009 или 7000. Смотрите Event Log, ищите исключение в `Main` / `ExecuteAsync`.

## Разработка

### Запуск тестов

```powershell
dotnet test
```

### Восстановление пакетов

```powershell
dotnet restore
```

### Сборка без публикации

```powershell
dotnet build
```

### Очистка артефактов

```powershell
dotnet clean
```

## Лицензия и авторство

Внутренний проект ASUMI.