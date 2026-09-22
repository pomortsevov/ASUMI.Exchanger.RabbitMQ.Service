# ASUMI.Producer.RabbitMQ.Service

[![build](https://github.com/pomortsevov/ASUMI.Producer.RabbitMQ.Service/actions/workflows/build.yml/badge.svg)](https://github.com/pomortsevov/ASUMI.Producer.RabbitMQ.Service/actions/workflows/build.yml)


Windows-служба на .NET 8 для публикации JSON-файлов с договорами из СЭД **АСУМИ** в RabbitMQ.

АСУМИ генерирует JSON-файлы с договорами и согласованиями и складывает их в папку. Служба читает эти файлы, публикует содержимое в RabbitMQ и перемещает обработанные файлы в папку «processed». Consumer, который будет забирать сообщения из очереди, появится отдельно и позже.

Служба **устойчива к недоступному RabbitMQ**: при старте ждёт подключения, не падает, при разрыве соединения автоматически переподключается. Файл **не удаляется и не перемещается**, пока публикация не пройдёт успешно.

## Стек

- .NET 8 (SDK 8.0.101+)
- `Microsoft.Extensions.Hosting` 8.0.0
- `Microsoft.Extensions.Hosting.WindowsServices` 8.0.0
- `Microsoft.Extensions.Options.DataAnnotations` — валидация конфигурации
- `RabbitMQ.Client` 6.8.1
- Serilog (консоль + файл с ротацией по дням)

## Как работает

```
┌──────────┐    *.json     ┌─────────────────────┐    BasicPublish    ┌────────────┐
│  АСУМИ   │ ────────────► │ ASUMI.Producer.     │ ─────────────────► │  RabbitMQ  │
│          │  SourcePath   │ RabbitMQ.Service    │  asumi.exchange    │            │
└──────────┘               └─────────┬───────────┘  routingKey:       └─────┬──────┘
                                     │              asumi.json                │
                                     ▼                                        ▼
                          ┌─────────────────────┐                     ┌────────────┐
                          │   ProcessedPath     │                     │ asumi.queue│
                          │   (успех)           │                     │  (TTL 30s) │
                          └─────────────────────┘                     └─────┬──────┘
                                                                            │
                                                                            ▼
                                                                     ┌────────────┐
                                                                     │  Consumer  │
                                                                     │  МИТ       │
                                                                     └────────────┘
```

1. АСУМИ кладёт `*.json` в `SourcePath`.
2. Служба раз в `PollIntervalSeconds` сканирует папку.
3. Для каждого файла: читает → публикует в `asumi.exchange` с routing key `asumi.json` → копирует в `ProcessedPath` → удаляет исходник.
4. **Если RabbitMQ недоступен** — файл остаётся в `SourcePath`, попытка повторяется на следующем цикле. Ничего не теряется.
5. Сообщения лежат в `asumi.queue` до 30 секунд (TTL) или до тех пор, пока их не заберёт consumer.

## Структура репозитория

```
ASUMI.Producer.RabbitMQ.Service/
├── ASUMI.Producer.RabbitMQ.Service.sln
├── Directory.Build.props
├── .gitignore
├── README.md
└── src/
    └── ASUMI.Producer.RabbitMQ.Service/
        ├── ASUMI.Producer.RabbitMQ.Service.csproj
        ├── Program.cs
        ├── appsettings.json
        ├── appsettings.Development.json
        ├── Properties/
        │   └── launchSettings.json
        ├── Abstractions/
        │   ├── IMessagePublisher.cs
        │   ├── IRabbitMqConnectionProvider.cs
        │   └── IRabbitMqTopologyInitializer.cs
        ├── Configuration/
        │   ├── FilePublisherOptions.cs
        │   └── RabbitMqOptions.cs
        ├── Hosting/
        │   └── ServiceConstants.cs
        ├── Infrastructure/
        │   ├── RabbitMqConnectionProvider.cs
        │   ├── RabbitMqMessagePublisher.cs
        │   ├── RabbitMqTopologyHostedService.cs
        │   └── RabbitMqTopologyInitializer.cs
        └── Workers/
            └── FilePublisherWorker.cs
```

## Требования

- **Для сборки**: .NET SDK 8.0.101 или новее.
- **Для запуска**: .NET Runtime 8.0.x (`Microsoft.NETCore.App 8.0.x`).
- **Для установки службы**: PowerShell от имени администратора.
- **RabbitMQ**: доступный брокер (см. раздел «Запуск RabbitMQ локально»).

Проверить SDK и Runtime:

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

## Запуск RabbitMQ локально (Docker)

`docker-compose.yml`:

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

> **Что меняется в Development.** В `appsettings.Development.json` переопределяются только параметры подключения к RabbitMQ (`HostName`, `UserName`, `Password`) — это позволяет разработчику подключаться к локальному брокеру. Все остальные настройки (`FilePublisher.SourcePath`, `ProcessedPath`, `MessageTtlMs` и т.д.) берутся из `appsettings.json`. То есть `dotnet run` в Development работает с теми же папками `C:\SourceJson` и `C:\ProcessedJson`, что и продакшен-служба.

Остановить — `Ctrl+C`.

Ожидаемый вывод при работающем RabbitMQ:

```
[15:52:00 INF] Starting ASUMI Producer RabbitMQ Service...
[15:52:00 INF] Declaring RabbitMQ topology...
[15:52:00 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:52:00 INF] Connected to RabbitMQ. ConnectionName=ASUMI.Producer.RabbitMQ.Service
[15:52:05 INF] Exchange declared: asumi.exchange (topic, durable)
[15:52:05 INF] Queue declared: asumi.queue (durable, TTL=30000ms)
[15:52:05 INF] Queue bound: asumi.queue -> asumi.exchange (routingKey=asumi.json)
[15:52:05 INF] RabbitMQ topology is ready.
[15:52:05 INF] FilePublisherWorker starting. Source=C:\SourceJson, Processed=C:\ProcessedJson, Mask=*.json, Interval=10s
[15:52:15 INF] Read Z_25.01.22_13-42__d.ID-12_b.ID-.json (1165 chars, 1475 bytes UTF-8).
[15:52:15 INF] Published Z_25.01.22_13-42__d.ID-12_b.ID-.json (1475 bytes) to exchange asumi.exchange with routingKey asumi.json.
[15:52:15 INF] Copied to processed: Z_25.01.22_13-42__d.ID-12_b.ID--20260921-155215341.json
```

Ожидаемый вывод при **недоступном** RabbitMQ (служба не падает):

```
[15:54:00 INF] Starting ASUMI Producer RabbitMQ Service...
[15:54:00 INF] Declaring RabbitMQ topology...
[15:54:00 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:54:04 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
[15:54:09 INF] Attempting to connect to RabbitMQ at localhost:5672/ as rmuser...
[15:54:13 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
...
```

Как только RabbitMQ поднимется — служба автоматически подключится и продолжит работу:

```
[15:55:24 INF] Connected to RabbitMQ. ConnectionName=ASUMI.Producer.RabbitMQ.Service
[15:55:29 INF] Published Z_25.01.22_13-42__d.ID-6_b.ID-.json (1330 bytes) to exchange asumi.exchange with routingKey asumi.json.
[15:55:29 INF] Copied to processed: Z_25.01.22_13-42__d.ID-6_b.ID--20260921-155529934.json
```

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
sc.exe description "ASUMI Producer RabbitMQ Service" "Publishes ASUMI contract JSON files to RabbitMQ."
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
- Переменные окружения с секциями через `__`.

### Полный список секций

```json
{
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
  "FilePublisher": {
    "Enabled": true,
    "SourcePath": "C:\\SourceJson",
    "ProcessedPath": "C:\\ProcessedJson",
    "FileMask": "*.json",
    "PollIntervalSeconds": 10
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

| Ключ | Назначение | По умолчанию |
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

### Описание ключей `FilePublisher`

| Ключ | Назначение | По умолчанию |
|---|---|---|
| `Enabled` | Включён ли воркер | `true` |
| `SourcePath` | Папка, откуда читаются `*.json` | `C:\SourceJson` |
| `ProcessedPath` | Папка для успешно опубликованных файлов | `C:\ProcessedJson` |
| `FileMask` | Маска файлов | `*.json` |
| `PollIntervalSeconds` | Интервал сканирования папки | `10` |

### Переопределение через переменные окружения

```powershell
[Environment]::SetEnvironmentVariable("RabbitMQ__Password", "StrongP@ss", "Machine")
[Environment]::SetEnvironmentVariable("FilePublisher__PollIntervalSeconds", "30", "Machine")
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

### RabbitMQ недоступен

Это **нормальный сценарий** — служба не падает, а ждёт подключения. В логе раз в 5 секунд:

```
[15:54:04 WRN] Could not connect to RabbitMQ at localhost:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
```

Как только RabbitMQ поднимется — в логе появится `Connected to RabbitMQ`, и служба продолжит работу. Файлы в `SourcePath` **не теряются** — они будут опубликованы после восстановления соединения.

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

### Сообщения исчезают из очереди через 30 секунд

Это **нормально** — у очереди `asumi.queue` установлен `x-message-ttl = 30000` (`MessageTtlMs`). Если consumer не заберёт сообщение за 30 секунд, оно удаляется. Когда появится consumer, TTL можно увеличить или отключить (`MessageTtlMs: 0`).

> **Важно:** TTL очереди нельзя изменить после создания. Чтобы поменять — удалите очередь в UI RabbitMQ, потом перезапустите службу.

### Ошибка `PRECONDITION_FAILED` при `QueueDeclare`

Очередь уже существует с другими параметрами (например, без TTL). Удалите её в UI (`Delete`), затем перезапустите службу:

```powershell
sc.exe stop "ASUMI Producer RabbitMQ Service"
sc.exe start "ASUMI Producer RabbitMQ Service"
```

### Логи не появляются

Проверьте, что папка `logs\` создалась рядом с `.exe`:

```powershell
ls C:\Services\ASUMI.Producer.RabbitMQ.Service\logs
```

## Разработка

### Сборка

```powershell
dotnet build
```

### Очистка артефактов

```powershell
dotnet clean
dotnet build-server shutdown
Remove-Item -Recurse -Force src\ASUMI.Producer.RabbitMQ.Service\obj, src\ASUMI.Producer.RabbitMQ.Service\bin -ErrorAction SilentlyContinue
```

### Структура кода

- **`Abstractions/`** — интерфейсы: `IMessagePublisher`, `IRabbitMqConnectionProvider`, `IRabbitMqTopologyInitializer`. Только контракты, без реализации.
- **`Configuration/`** — типизированные опции (`RabbitMqOptions`, `FilePublisherOptions`) с валидацией через DataAnnotations.
- **`Hosting/`** — константы хоста (`ServiceConstants`).
- **`Infrastructure/`** — реализации: подключение, публикация, объявление топологии.
- **`Workers/`** — `BackgroundService`-ы.

### Как добавить новый воркер

1. Создать класс в `Workers/`, унаследовать от `BackgroundService`.
2. В конструкторе запросить зависимости (`IOptions<T>`, `IMessagePublisher`).
3. Реализовать `ExecuteAsync(CancellationToken)`, уважать `stoppingToken`.
4. Зарегистрировать в `Program.cs`: `services.AddHostedService<NewWorker>();`.

### Как добавить новый источник сообщений

1. Создать интерфейс в `Abstractions/`.
2. Реализовать в `Infrastructure/`.
3. Зарегистрировать в DI: `services.AddSingleton<IMessageSource, FileSystemMessageSource>();`.
4. Использовать в воркере.

## Roadmap

- [ ] **Consumer** — отдельный сервис, который забирает сообщения из `asumi.queue` и обрабатывает их.
- [ ] **Валидация JSON** перед публикацией — битые файлы в отдельную папку `Failed`.
- [ ] **Обработка повторных сбоев** — счётчик попыток по файлу, перенос «безнадёжных» в `Failed`.
- [ ] **Версионность** — автоматический номер версии из Git (`Nerdbank.GitVersioning`).
- [ ] **CI/CD** — сборка, тесты, публикация артефакта.
- [ ] **Юнит-тесты** — `FilePublisherWorker`, `RabbitMqMessagePublisher`, options-валидация.
- [ ] **Метрики** — счётчики опубликованных/проваленных файлов.
- [ ] **Health-check** — endpoint или Windows Performance Counter.

## Развёртывание на сервере

Для развёртывания на Windows Server есть скрипт `deploy/deploy.ps1`, который выполняет все шаги автоматически: проверку .NET 8 Runtime, установку при необходимости, копирование артефакта, создание службы, настройку recovery, запуск и проверку логов.

### Предварительные требования

**На сервере:**

- Windows Server 2019 или новее.
- WinRM включён (для удалённого развёртывания): `Enable-PSRemoting -Force`.
- Учётная запись с правами администратора.
- Доступ к RabbitMQ (проверьте `Test-NetConnection <rabbitmq-host> -Port 5672`).
- .NET 8 Runtime — либо уже установлен, либо будет установлен скриптом.

**На машине, откуда развёртываете:**

- PowerShell 5.1 или новее, от имени администратора.
- Доступ к файловому ресурсу с артефактом и установщиком .NET.
- Если не домен — добавьте сервер в TrustedHosts:
  ```powershell
  Set-Item WSMan:\localhost\Client\TrustedHosts -Value "nsk-sapp01" -Force
  ```

### Подготовка файлов

**1. Скачайте .NET 8 Runtime installer:**

- https://dotnet.microsoft.com/download/dotnet/8.0
- Раздел **.NET Runtime 8.0.x** → **Windows** → **x64** → `.exe`.
- Положите на файловый ресурс: `\\fileserver\share\dotnet-runtime-8.0.22-win-x64.exe`.

**2. Скачайте артефакт из CI:**

- Откройте https://github.com/pomortsevov/ASUMI.Producer.RabbitMQ.Service/actions
- Выберите свежий успешный запуск `build`.
- Внизу страницы → **Artifacts** → `ASUMI.Producer.RabbitMQ.Service` → скачайте ZIP.
- Распакуйте и положите содержимое в `\\fileserver\share\ASUMI.Producer.RabbitMQ.Service\`.

### Запуск развёртывания

**Удалённое (с вашей машины на сервер):**

```powershell
.\deploy\deploy.ps1 `
    -ServerName nsk-sapp01 `
    -ArtifactPath "\\fileserver\share\ASUMI.Producer.RabbitMQ.Service" `
    -DotNetInstallerPath "\\fileserver\share\dotnet-runtime-8.0.22-win-x64.exe"
```

**Локальное (на самом сервере):**

```powershell
.\deploy\deploy.ps1 `
    -ServerName nsk-sapp01 `
    -ArtifactPath "C:\Install\ASUMI.Producer.RabbitMQ.Service" `
    -DotNetInstallerPath "C:\Install\dotnet-runtime-8.0.22-win-x64.exe"
```

**Если .NET 8 уже установлен на сервере:**

```powershell
.\deploy\deploy.ps1 -ServerName nsk-sapp01 -SkipDotNetInstall
```

### Параметры скрипта

| Параметр | Обязательный | По умолчанию | Описание |
|---|---|---|---|
| `-ServerName` | Да | — | Имя целевого сервера (`nsk-sapp01`). Если совпадает с локальным — развёртывание локальное. |
| `-ArtifactPath` | Нет | `\\fileserver\share\ASUMI.Producer.RabbitMQ.Service` | Путь к папке с `.exe` и `appsettings*.json`. |
| `-ServicePath` | Нет | `C:\Services\ASUMI.Producer.RabbitMQ.Service` | Куда разворачивать службу на сервере. |
| `-ServiceName` | Нет | `ASUMI Producer RabbitMQ Service` | Имя службы в SCM. |
| `-DotNetInstallerPath` | Нет | — | Путь к offline-установщику .NET 8 Runtime. |
| `-SkipDotNetInstall` | Нет | — | Пропустить установку .NET (если уверены, что он уже есть). |

### Что делает скрипт

1. **Проверяет .NET 8 Runtime** на целевом сервере. Если нет — устанавливает через offline-установщик, winget или предлагает скачать вручную.
2. **Проверяет доступность RabbitMQ** (читает `HostName` и `Port` из `appsettings.json`).
3. **Останавливает и удаляет** существующую службу, если есть.
4. **Копирует артефакт** в `C:\Services\ASUMI.Producer.RabbitMQ.Service`.
5. **Создаёт службу** через `sc.exe create`, настраивает `failure` (recovery) и `AppDirectory`.
6. **Запускает службу** и проверяет статус.
7. **Показывает последние 15 строк логов** из `logs\`.

### Настройка `appsettings.json` под прод

Артефакт содержит `appsettings.json` из репозитория (с `localhost` и тестовыми кредами). **Перед развёртыванием на проде** замените значения на реальные:

```json
{
  "RabbitMQ": {
    "HostName": "rabbitmq.prod.local",
    "Port": 5672,
    "VirtualHost": "/prod",
    "UserName": "asumi_producer",
    "Password": "",
    "ExchangeName": "asumi.exchange.prod",
    "QueueName": "asumi.queue.prod",
    "RoutingKey": "asumi.json"
  },
  "FilePublisher": {
    "SourcePath": "D:\\ASUMI\\SourceJson",
    "ProcessedPath": "D:\\ASUMI\\ProcessedJson",
    "PollIntervalSeconds": 10
  }
}
```

**Пароль RabbitMQ — не храните в `appsettings.json`.** Способы:

**Через переменные окружения** (рекомендуется):

```powershell
[Environment]::SetEnvironmentVariable("RabbitMQ__Password", "StrongP@ss", "Machine")
```

`Host.CreateDefaultBuilder` подхватит `RabbitMQ__Password` и перекроет значение из файла.

**Через `appsettings.Production.json`** — отдельный файл рядом с `.exe`, не коммитится в репозиторий:

```json
{
  "RabbitMQ": {
    "Password": "StrongP@ss"
  }
}
```

Окружение по умолчанию — `Production`, файл подхватится поверх `appsettings.json`.

### После развёртывания

Проверьте:

```powershell
# Статус службы
sc.exe query "ASUMI Producer RabbitMQ Service"

# Логи
Get-Content C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\asumi-producer-rabbitmq-*.log -Tail 20

# Smoke-тест
'{ "test": true }' | Set-Content D:\ASUMI\SourceJson\smoke.json -Encoding UTF8
Start-Sleep -Seconds 15
ls D:\ASUMI\SourceJson       # должно быть пусто
ls D:\ASUMI\ProcessedJson    # должен появиться smoke-*.json
```

Сообщение должно появиться в RabbitMQ UI: http://rabbitmq.prod.local:15672 → **Queues** → `asumi.queue.prod`.

### Обновление на сервере

Повторный запуск скрипта **автоматически** останавливает и удаляет старую службу перед установкой новой. Просто:

```powershell
.\deploy\deploy.ps1 -ServerName nsk-sapp01 -SkipDotNetInstall
```

Скрипт скачает свежий артефакт из `-ArtifactPath` и развернёт его.

## Лицензия и авторство

Внутренний проект СЭД **АСУМИ**. Все права принадлежат организации.