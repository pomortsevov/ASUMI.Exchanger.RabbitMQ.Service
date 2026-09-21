# ASUMI.Producer.RabbitMQ.Service

Windows-служба на .NET 8 (Worker Service) для публикации сообщений из ASUMI в RabbitMQ.

Работает в двух режимах:

- **Консоль** — для локальной отладки (`dotnet run`).
- **Служба Windows** — для продакшена (SCM, автозапуск).

Служба **устойчива к недоступному RabbitMQ**: при старте ждёт подключения, не падает, при разрыве соединения автоматически переподключается.

## Стек

- .NET 8 (SDK 8.0.101+)
- `Microsoft.Extensions.Hosting` 8.0.0
- `Microsoft.Extensions.Hosting.WindowsServices` 8.0.0
- `Microsoft.Extensions.Options.DataAnnotations` — валидация конфигурации
- `RabbitMQ.Client` 6.8.1
- Serilog (консоль + файл с ротацией по дням)

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
│       ├── Abstractions/
│       │   ├── IMessageSource.cs
│       │   ├── IRabbitMqConnectionProvider.cs
│       │   └── IRabbitMqTopologyInitializer.cs
│       ├── Configuration/
│       │   ├── AsumiProducerRabbitMqOptions.cs
│       │   └── RabbitMqOptions.cs
│       ├── Hosting/
│       │   └── ServiceConstants.cs
│       ├── Infrastructure/
│       │   ├── RabbitMqConnectionProvider.cs
│       │   ├── RabbitMqTopologyInitializer.cs
│       │   └── RabbitMqTopologyHostedService.cs
│       └── Workers/
│           └── HeartbeatWorker.cs
└── tests/
    └── ASUMI.Producer.RabbitMQ.Service.Tests/
```

## Требования

- **Для сборки**: .NET SDK 8.0.101 или новее.
- **Для запуска**: .NET Runtime 8.0.x (`Microsoft.NETCore.App 8.0.x`).
- **Для установки службы**: PowerShell от имени администратора.
- **RabbitMQ**: доступный брокер, например через Docker (см. ниже).

Проверить SDK и Runtime:

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

## Запуск RabbitMQ локально (Docker)

Пример `docker-compose.yml`:

```yaml
version: "2.1"
services:
  rabbitmq:
    image: rabbitmq:3.10.7-management
    hostname: rabbitmq
    restart: always
    environment:
      - RABBITMQ_DEFAULT_USER=rmuser
      - RABBITMQ_DEFAULT_PASS=rmpassword
      - RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=-rabbit log_levels [{connection,error},{default,error}] disk_free_limit 2147483648
    volumes:
      - ./rabbitmq:/var/lib/rabbitmq
    ports:
      - 15672:15672
      - 5672:5672
```

Запустить:

```powershell
docker compose up -d
```

Проверить:

```powershell
docker ps --filter "name=rabbitmq"
Test-NetConnection localhost -Port 5672
Test-NetConnection localhost -Port 15672
```

Management UI: http://localhost:15672, логин `rmuser`, пароль `rmpassword`.

## Локальный запуск (консоль)

Из корня репозитория:

```powershell
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

По умолчанию берётся окружение `Production` и `appsettings.json`. Чтобы использовать `appsettings.Development.json`:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

Остановить — `Ctrl+C`.

Ожидаемый вывод при работающем RabbitMQ:

```
[15:00:00 INF] Starting ASUMI Producer RabbitMQ Service...
[15:00:00 INF] Declaring RabbitMQ topology...
[15:00:00 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:00:00 INF] Connected to RabbitMQ. ConnectionName=ASUMI.Producer.RabbitMQ.Service
[15:00:00 INF] Exchange declared: asumi.exchange (topic, durable)
[15:00:00 INF] Queue declared: asumi.queue (durable, TTL=30000ms)
[15:00:00 INF] Queue bound: asumi.queue -> asumi.exchange (routingKey=asumi.json)
[15:00:00 INF] RabbitMQ topology is ready.
[15:00:05 INF] HeartbeatWorker starting. Waiting for RabbitMQ connection...
[15:00:05 INF] RabbitMQ channel opened. ChannelNumber=1
[15:00:05 INF] HeartbeatWorker running. Interval: 2s
[15:00:05 INF] Hello World #1 at 15:00:05
...
```

Ожидаемый вывод при **недоступном** RabbitMQ (служба не падает):

```
[15:01:00 INF] Starting ASUMI Producer RabbitMQ Service...
[15:01:00 INF] Declaring RabbitMQ topology...
[15:01:00 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:01:00 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
[15:01:05 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:01:05 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
...
```

Как только RabbitMQ поднимется — служба автоматически подключится и продолжит работу.

## Публикация

```powershell
dotnet publish src\ASUMI.Producer.RabbitMQ.Service -c Release -o C:\Services\ASUMI.Producer.RabbitMQ.Service
```

> **Важно:** папка `C:\Services\...` должна быть свободна, либо установленная служба должна быть остановлена и удалена — иначе `.exe` будет залочен и `dotnet publish` упадёт с `IOException`.

На выходе:

```
ASUMI.Producer.RabbitMQ.Service.exe
appsettings.json
appsettings.Development.json
```

## Установка как службы Windows

Все команды — из PowerShell **от имени администратора**, **одной строкой** (не использовать перенос `\` в PowerShell для `sc.exe` — может сломать парсинг).

### 1. Создать службу

```powershell
sc.exe create "ASUMI Producer RabbitMQ Service" binPath= "C:\Services\ASUMI.Producer.RabbitMQ.Service\ASUMI.Producer.RabbitMQ.Service.exe" start= auto DisplayName= "ASUMI Producer RabbitMQ Service"
```

> Пробелы после `binPath=`, `start=`, `DisplayName=` обязательны.

### 2. Описание (опционально)

```powershell
sc.exe description "ASUMI Producer RabbitMQ Service" "Publishes messages from ASUMI source to RabbitMQ."
```

### 3. Запустить

```powershell
sc.exe start "ASUMI Producer RabbitMQ Service"
```

### 4. Проверить

```powershell
sc.exe query "ASUMI Producer RabbitMQ Service"
```

`STATE` должен быть `RUNNING`. В первые 1–2 секунды — `START_PENDING`.

## Логи

При запуске как службы логи пишутся **рядом с `.exe`** — благодаря `AppContext.BaseDirectory` в `Program.cs`:

```
C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-YYYYMMDD.log
```

Ротация — по дням, хранение — 14 файлов. Последние 20 строк:

```powershell
Get-Content C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-*.log -Tail 20
```

При запуске через `dotnet run` логи — в `bin\Debug\net8.0\win-x64\logs\` (или `bin\Release\...`).

> **Если логи оказались в `C:\Windows\System32\logs\`** — это значит, что `Program.cs` использует относительный путь `"logs/..."`, а не `AppContext.BaseDirectory`. Рабочая директория службы Windows по умолчанию — `C:\Windows\System32\`.

## Конфигурация

Файлы:

- `appsettings.json` — общие настройки.
- `appsettings.{DOTNET_ENVIRONMENT}.json` — переопределения для среды (`Development`, `Production`).
- Переменные окружения с префиксом `DOTNET_` и секциями через `__`.

### Полный список секций

```json
{
  "AsumiProducerRabbitMq": {
    "Enabled": true,
    "IntervalSeconds": 5
  },
  "RabbitMQ": {
    "HostName": "localhost",
    "Port": 5672,
    "VirtualHost": "/",
    "UserName": "rmuser",
    "Password": "rmpassword",
    "ExchangeName": "asumi.exchange",
    "QueueName": "asumi.queue",
    "RoutingKey": "asumi.json",
    "MessageTtlMs": 30000,
    "Persistent": true,
    "AutomaticRecovery": true,
    "NetworkRecoveryIntervalSeconds": 10,
    "ReconnectIntervalSeconds": 5,
    "ConnectionName": "ASUMI.Producer.RabbitMQ.Service"
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

### Описание ключей `RabbitMQ`

| Ключ | Назначение | Значение по умолчанию |
|---|---|---|
| `HostName` | Хост RabbitMQ | `localhost` |
| `Port` | Порт AMQP | `5672` |
| `VirtualHost` | Виртуальный хост | `/` |
| `UserName` | Пользователь | — |
| `Password` | Пароль | — |
| `ExchangeName` | Имя topic-exchange | `asumi.exchange` |
| `QueueName` | Имя очереди | `asumi.queue` |
| `RoutingKey` | Routing key для bind и publish | `asumi.json` |
| `MessageTtlMs` | TTL сообщений в очереди | `30000` |
| `Persistent` | Persistent-сообщения | `true` |
| `AutomaticRecovery` | Авто-восстановление соединения клиентом | `true` |
| `NetworkRecoveryIntervalSeconds` | Интервал авто-восстановления (после разрыва) | `10` |
| `ReconnectIntervalSeconds` | Интервал попыток при первом подключении | `5` |
| `ConnectionName` | Имя соединения в UI RabbitMQ | `ASUMI.Producer.RabbitMQ.Service` |

### Переопределение через переменные окружения

```powershell
[Environment]::SetEnvironmentVariable("RabbitMQ__Password", "StrongP@ss", "Machine")
```

> `__` (двойное подчёркивание) — разделитель вложенности. `RabbitMQ__Password` = `RabbitMQ.Password` в JSON.

После изменения переменных — перезапустить службу:

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
| Оснастка | `services.msc` |

## Обновление службы

Порядок, чтобы не словить `IOException: file is being used by another process`:

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

```powershell
Get-EventLog -LogName Application -Source "ASUMI Producer RabbitMQ Service" -Newest 10 -ErrorAction SilentlyContinue | Format-List TimeGenerated, EntryType, Message
```

### Служба запускается и сразу падает

Обычно — исключение в `Program.Main` или при построении хоста. Смотрите Event Log (см. выше). Возможные причины:

- Ошибка в `appsettings.json` — не проходит валидация.
- Опечатка в имени обязательной опции `RabbitMQ:*`.
- Пакет не установлен (`dotnet restore`).

### RabbitMQ недоступен

Это **нормальный сценарий** — служба не падает, а ждёт подключения. В логе раз в 5 секунд:

```
[15:01:00 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
```

Как только RabbitMQ поднимется — в логе появится `Connected to RabbitMQ`, и служба продолжит работу.

Проверить доступность RabbitMQ:

```powershell
Test-NetConnection localhost -Port 5672
docker ps --filter "name=rabbitmq"
docker logs rabbitmq --tail 30
```

### RabbitMQ не принимает сообщения

Проверьте в UI http://localhost:15672:

- **Exchanges** → `asumi.exchange` существует, тип `topic`.
- **Queues** → `asumi.queue` существует, привязана к `asumi.exchange` с routing key `asumi.json`.
- **Queues** → `asumi.queue` → **Message rates** — есть ли публикации.

### Ошибка `PRECONDITION_FAILED` при `QueueDeclare`

Очередь уже существует с другими параметрами (например, без TTL). Удалите её в UI (`Delete`), затем перезапустите службу:

```powershell
sc.exe restart "ASUMI Producer RabbitMQ Service"
```

### Логи не появляются

Проверьте, что папка `logs\` создалась рядом с `.exe`:

```powershell
ls C:\Services\ASUMI.Producer.RabbitMQ.Service\logs
```

Если нет — убедитесь, что в `Program.cs` используется `AppContext.BaseDirectory`, а не `"logs/..."`.

## Разработка

### Сборка

```powershell
dotnet build
```

### Тесты

```powershell
dotnet test
```

### Очистка артефактов

```powershell
dotnet clean
dotnet build-server shutdown
Remove-Item -Recurse -Force src\ASUMI.Producer.RabbitMQ.Service\obj, src\ASUMI.Producer.RabbitMQ.Service\bin -ErrorAction SilentlyContinue
```

### Структура кода

- **`Abstractions/`** — интерфейсы (`IRabbitMqConnectionProvider`, `IRabbitMqTopologyInitializer`, `IMessageSource`). Здесь только контракты, без реализации.
- **`Configuration/`** — типизированные опции (`AsumiProducerRabbitMqOptions`, `RabbitMqOptions`) с валидацией через DataAnnotations.
- **`Hosting/`** — константы хоста (`ServiceConstants`).
- **`Infrastructure/`** — реализации: подключение, топология, hosted-service-обёртки.
- **`Workers/`** — `BackgroundService`-ы.

### Как добавить новый воркер

1. Создать класс в `Workers/`, унаследовать от `BackgroundService`.
2. В конструкторе запросить нужные зависимости (`IRabbitMqConnectionProvider`, `IOptions<T>`).
3. Реализовать `ExecuteAsync(CancellationToken)`, уважать `stoppingToken`.
4. Зарегистрировать в `Program.cs`: `services.AddHostedService<NewWorker>();`.

### Как добавить новый источник сообщений

1. Реализовать `IMessageSource` в `Infrastructure/`.
2. Зарегистрировать в DI: `services.AddSingleton<IMessageSource, FileSystemMessageSource>();`.
3. Использовать в `PublishWorker`.

## CI/CD

Заготовка для GitHub Actions (`.github/workflows/build.yml`):

```yaml
name: build

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release
      - run: dotnet publish src\ASUMI.Producer.RabbitMQ.Service -c Release -o out

      - uses: actions/upload-artifact@v4
        with:
          name: ASUMI.Producer.RabbitMQ.Service
          path: out/
```

Для GitLab CI синтаксис аналогичен, структура шагов та же.

## Лицензия и авторство

Внутренний проект ASUMI.