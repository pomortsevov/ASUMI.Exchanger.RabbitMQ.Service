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
- `prometheus-net` 8.2.1 — метрики для Prometheus (`MetricServer`, без `prometheus-net.AspNetCore`)
- Serilog (консоль + файл с ротацией по дням + собственный sink для Grafana Loki)
- Отправка в Loki реализована вручную на `HttpClient` и `System.Text.Json` — без сторонних пакетов, см. «Отправка логов в Grafana Loki»

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
                          │   (успех)           │                     │ (TTL 5 мин)│
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
3. Для каждого файла: читает → публикует в `asumi.exchange` с routing key `asumi.json` → перемещает в `ProcessedPath` → удаляет исходник.
4. **Если RabbitMQ недоступен** — файл остаётся в `SourcePath`, попытка повторяется на следующем цикле. Ничего не теряется.
5. Публикация считается успешной только после **подтверждения от брокера** (publisher confirms). Если для routing key нет привязки, брокер возвращает сообщение, `PublishAsync` возвращает `false`, и файл остаётся в `SourcePath`. Успешного лога при потерянном сообщении быть не может.
6. Сообщения лежат в `asumi.queue` до 5 минут (TTL) или до тех пор, пока их не заберёт consumer.

> **Кодировка.** Файлы АСУМИ приходят в **UTF-16LE**. Служба отправляет их в RabbitMQ **байт в байт**, без перекодирования. Consumer обязан читать UTF-16 — если он рассчитан на UTF-8, сообщения не разберутся. Служба пишет предупреждение, только если встретила файл **без** UTF-16 BOM, то есть об отклонении от ожидаемого формата.

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
        │   ├── LokiOptions.cs
        │   ├── MetricsOptions.cs
        │   └── RabbitMqOptions.cs
        ├── Hosting/
        │   ├── PasswordResolver.cs
        │   └── ServiceConstants.cs
        ├── Infrastructure/
        │   ├── RabbitMqConnectionProvider.cs
        │   ├── RabbitMqMessagePublisher.cs
        │   ├── RabbitMqTopologyHostedService.cs
        │   └── RabbitMqTopologyInitializer.cs
        ├── Observability/
        │   ├── LokiSink.cs
        │   ├── MetricsServerHostedService.cs
        │   └── ProducerMetrics.cs
        └── Workers/
            └── FilePublisherWorker.cs
```

## Требования

- **Для сборки**: .NET SDK 8.0.101 или новее.
- **Для запуска**: .NET Runtime 8.0.x (`Microsoft.NETCore.App 8.0.x`).
- **Для установки службы**: PowerShell от имени администратора.
- **RabbitMQ**: доступный брокер (см. раздел «Где работает RabbitMQ»).

Проверить SDK и Runtime:

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

## Где работает RabbitMQ

> **Раздел переписан 01.10.2026.** Прежний вариант описывал `docker-compose.yml` из
> этого репозитория — такого файла здесь нет, и Docker на машине разработчика не
> установлен. Ниже то, что есть на самом деле.

### Брокер, к которому подключается служба

В конфигурации по умолчанию указан **удалённый брокер `172.18.28.118:5672`** под
учёткой `NSK-ASUMI-PRODUCER`. Локальный RabbitMQ для этих целей не используется —
`dotnet run` тоже ходит на `172.18.28.118`, потому что `appsettings.Development.json`
переопределяет только учётку, а `HostName` там совпадает с продовым.

### Локальный брокер в WSL

На машине разработчика RabbitMQ запущен **внутри WSL** (`Ubuntu-22.04`), а Docker
работает уже внутри этой дистрибутивы. На Windows команды `docker` нет.

```powershell
# контейнеры видны только из WSL
wsl -e sh -c "docker ps --format '{{.Names}}\t{{.Image}}\t{{.Ports}}'" | Select-String rabbitmq
```

Фактическое состояние стека:

| Параметр | Значение |
|---|---|
| Дистрибутив WSL | `Ubuntu-22.04` |
| Compose-проект | `monitoring` |
| Контейнер с AMQP наружу | `monitoring_rabbitmq_1` |
| Образ | `rabbitmq:4.3.6-management` |
| Порты | `0.0.0.0:5672` → AMQP, `127.0.0.1:15672` → Management |
| Кластер | также есть `monitoring_rabbitmq_2` и `monitoring_rabbitmq_3`, наружу не проброшены |

Порт наружу пробрасывает `wslrelay`, поэтому из Windows `localhost` работает без
дополнительных настроек:

```powershell
Test-NetConnection localhost -Port 5672
Test-NetConnection localhost -Port 15672
```

Management UI: <http://localhost:15672>. Учётные данные брокера — `rmuser` / `rmpassword`.

### Инфраструктура — в отдельном репозитории

Стек целиком, включая compose-файлы, живёт **не здесь**. Им управляет репозиторий
[`RabbitMQ.Consumer.PilotICE.Infra`](https://github.com/pomortsevov/RabbitMQ.Consumer.PilotICE.Infra)
(приватный, нужен доступ):

- compose-проект `monitoring` с RabbitMQ, Loki, Prometheus, Grafana, Alloy, Keycloak и остальным;
- `prometheus/targets/` — список целей для скрейпа, подхватывается каждые 30 секунд;
- `ansible/inventory/group_vars/all.yml` — список целей, включая нашу
  (`external_scrape_targets`, job `asumi_producer`).

Править compose руками здесь нечего и не нужно: изменения в стеке делаются там.

Если стек не поднят, разворачивать его следует в том репозитории, а не в этом.

## Локальный запуск (консоль)

Из корня репозитория. Пароль в репозитории не хранится, поэтому его нужно задать — иначе служба поднимется, но публиковать не сможет (см. раздел «Пароль RabbitMQ»):

```powershell
$env:RabbitMQ__Password = "StrongP@ss"
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

По умолчанию берётся окружение `Production` и `appsettings.json`. Чтобы использовать `appsettings.Development.json`:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

> **Что меняется в Development.** В `appsettings.Development.json` переопределяются параметры подключения к RabbitMQ (`HostName`, `UserName`, `MessageTtlMs`, `QueueType`, `QuorumInitialGroupSize`) и `PublishConfirmTimeoutSeconds`. Все остальные настройки (`FilePublisher.SourcePath`, `ProcessedPath` и т.д.) берутся из `appsettings.json` — то есть `dotnet run` работает с теми же папками `C:\SourceJson` и `C:\ProcessedJson`, что и продакшен-служба.

Остановить — `Ctrl+C`.

Ожидаемый вывод при работающем RabbitMQ:

```
[15:52:00 INF] Starting ASUMI Producer RabbitMQ Service v1.0.8...
[15:52:00 INF] RabbitMQ password resolved from Machine environment scope (RabbitMQ__Password).
[15:52:00 INF] Loki sink enabled for http://172.18.28.118:3100, job=ASUMI.Producer.RabbitMQ.Service.
[15:52:00 INF] Declaring RabbitMQ topology...
[15:52:00 INF] Attempting to connect to RabbitMQ at 172.18.28.118:5672/ as NSK-ASUMI-PRODUCER...
[15:52:00 INF] Connected to RabbitMQ. ConnectionName=ASUMI.Producer.RabbitMQ.Service
[15:52:05 INF] Exchange declared: asumi.exchange (topic, durable)
[15:52:05 INF] Queue declared: asumi.queue (durable, type=quorum, TTL=300000ms)
[15:52:05 INF] Queue bound: asumi.queue -> asumi.exchange (routingKey=asumi.json)
[15:52:05 INF] RabbitMQ topology is ready.
[15:52:05 INF] FilePublisherWorker starting. Source=C:\SourceJson, Processed=C:\ProcessedJson, Mask=*.json, Interval=10s
[15:52:15 INF] Read Z_25.01.22_13-42__d.ID-12_b.ID-.json (1475 bytes).
[15:52:15 INF] Published Z_25.01.22_13-42__d.ID-12_b.ID-.json (1475 bytes) to exchange asumi.exchange with routingKey asumi.json.
[15:52:15 INF] Moved to processed: Z_25.01.22_13-42__d.ID-12_b.ID--20260921-155215341.json
```

Ожидаемый вывод при **недоступном** RabbitMQ (служба не падает):

```
[15:54:00 INF] Declaring RabbitMQ topology...
[15:54:00 INF] Attempting to connect to RabbitMQ at 172.18.28.118:5672/ as NSK-ASUMI-PRODUCER...
[15:54:04 WRN] Could not connect to RabbitMQ at 172.18.28.118:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
[15:54:09 INF] Attempting to connect to RabbitMQ at 172.18.28.118:5672/ as NSK-ASUMI-PRODUCER...
[15:54:13 WRN] Could not connect to RabbitMQ at 172.18.28.118:5672/ (BrokerUnreachableException): None of the specified endpoints were reachable. Will retry in 5s.
...
```

Как только RabbitMQ поднимется — служба автоматически подключится и продолжит работу:

```
[15:55:24 INF] Connected to RabbitMQ. ConnectionName=ASUMI.Producer.RabbitMQ.Service
[15:55:29 INF] Published Z_25.01.22_13-42__d.ID-6_b.ID-.json (1330 bytes) to exchange asumi.exchange with routingKey asumi.json.
[15:55:29 INF] Moved to processed: Z_25.01.22_13-42__d.ID-6_b.ID--20260921-155529934.json
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
    "HostName": "172.18.28.118",
    "Port": 5672,
    "VirtualHost": "/",
    "UserName": "NSK-ASUMI-PRODUCER",
    "Password": "",
    "ExchangeName": "asumi.exchange",
    "QueueName": "asumi.queue",
    "RoutingKey": "asumi.json",
    "MessageTtlMs": 300000,
    "QueueType": "quorum",
    "QuorumInitialGroupSize": 3,
    "PublishConfirmTimeoutSeconds": 5,
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
  "Loki": {
    "Enabled": true,
    "Url": "http://172.18.28.118:3100",
    "Job": "ASUMI.Producer.RabbitMQ.Service",
    "ServiceName": "ASUMI.Producer.RabbitMQ.Service",
    "BatchSize": 100,
    "FlushPeriodSeconds": 2,
    "TimeoutSeconds": 5,
    "QueueCapacity": 10000
  },
  "Metrics": {
    "Enabled": true,
    "Port": 9101
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

> `"Password": ""` — намеренно пусто. Секрет задаётся переменной окружения `RabbitMQ__Password`, см. раздел «Пароль RabbitMQ».

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
| `MessageTtlMs` | TTL сообщений в очереди | `300000` |
| `QueueType` | Тип очереди: `quorum`, `classic`, `stream` | `quorum` |
| `QuorumInitialGroupSize` | Размер начальной группы реплик, только для quorum | `3` |
| `PublishConfirmTimeoutSeconds` | Ожидание publisher confirm, секунды | `5` |
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

### Пароль RabbitMQ

Пароль **не хранится в репозитории**. В `appsettings.json` и `appsettings.Development.json` поле `"Password"` намеренно пустое:

```json
"UserName": "NSK-ASUMI-PRODUCER",
"Password": "",
```

Секрет передаётся одним из двух способов.

#### Способ 1. Переменная окружения (рекомендуется)

Для локального запуска — на уровне текущего процесса, действует до закрытия окна PowerShell:

```powershell
$env:RabbitMQ__Password = "StrongP@ss"
dotnet run --project src\ASUMI.Producer.RabbitMQ.Service
```

На сервере — постоянно, для всех новых процессов:

```powershell
# PowerShell от имени администратора
[Environment]::SetEnvironmentVariable("RabbitMQ__Password", "StrongP@ss", "Machine")
sc.exe stop "ASUMI Producer RabbitMQ Service"
sc.exe start "ASUMI Producer RabbitMQ Service"
```

Служба читает переменную при старте, поэтому перезапуск обязателен.

**Где это лежит в реестре:**

```
HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment
```

Имя значения — `RabbitMQ__Password`, тип `REG_SZ`. Проверить и изменить вручную:

```powershell
# прочитать
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v RabbitMQ__Password

# записать
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v RabbitMQ__Password /t REG_SZ /d "StrongP@ss" /f
```

Два замечания. Значение хранится открытым текстом и доступно администраторам и процессам пользователя — в отличие от пароля в базе. И при ручной правке через `reg add` переменная не разошлётся по системе: в отличие от `SetEnvironmentVariable`, никто не отправляет `WM_SETTINGCHANGE`, поэтому нужна перезагрузка машины или повторный вход.

Удалить переменную:

```powershell
[Environment]::SetEnvironmentVariable("RabbitMQ__Password", $null, "Machine")
```

#### Способ 2. `appsettings.Production.json`

Файл рядом с `.exe` в `C:\Services\ASUMI.Producer.RabbitMQ.Service\`. В репозиторий не попадает — уже в `.gitignore`. Перекрывает `appsettings.json`:

```json
{
  "RabbitMQ": {
    "Password": "StrongP@ss"
  }
}
```

Читается штатно, `Host.CreateDefaultBuilder` подхватывает `appsettings.{DOTNET_ENVIRONMENT}.json` поверх базового. Перезапуск службы не нужен, достаточно перезапустить — то есть он тоже нужен, так как конфигурация читается при старте процесса. Разница со способом 1 в том, что секрет лежит файлом на диске рядом с `.exe`, и его достаточно просто скопировать.

#### Если пароль не задан

Служба **не падает** — она поднимается, пишет в лог `ERR` и ждёт брокер. Файлы при этом остаются в `SourcePath` и не теряются:

```
[08:25:56 ERR] RabbitMQ Password is empty. Set the RabbitMQ__Password environment variable
(Machine scope) or put it in appsettings.Production.json next to the exe.
Publishing will fail until the secret is provided.
```

Уровень `ERR`, а не `WRN`: публикация невозможна, пока секрет не задан. Сама служба при этом продолжает работать.

#### Уже установленная парольная политика

Если пароль уже лежал в `appsettings.json` в предыдущих коммитах, он остаётся в истории репозитория. Удаление пароля из файла этого не отменяет — при необходимости история переписывается через `git filter-repo`.

### Отправка логов в Grafana Loki

Служба пишет логи одновременно в консоль, в файл рядом с `.exe` и в **Grafana Loki** через push API. Loki по умолчанию включён и находится на том же хосте, что и RabbitMQ.

Включается ключом `Loki:Enabled`. В лог при старте видно, что произошло:

```
[INF] Loki sink enabled for http://172.18.28.118:3100, job=ASUMI.Producer.RabbitMQ.Service.
```

или

```
[INF] Loki sink disabled by configuration.
```

#### Запрос в Grafana

```logql
{job="ASUMI.Producer.RabbitMQ.Service"}
```

С меткой окружения, чтобы отделить локальные запуски от боевых:

```logql
{job="ASUMI.Producer.RabbitMQ.Service", env="Production"}
```

Только ошибки:

```logql
{job="ASUMI.Producer.RabbitMQ.Service"} |~ "(?i)error|fail"
```

#### Метки

Sink добавляет к каждому потоку четыре метки:

| Метка | Значение | Зачем |
|---|---|---|
| `job` | `ASUMI.Producer.RabbitMQ.Service` | выбор потока в Grafana |
| `service_name` | `ASUMI.Producer.RabbitMQ.Service` | совпадает с именем сборки |
| `env` | `Production` / `Development` | разделение боевых и локальных запусков |
| `host` | имя машины, например `NSK-NA02` | на какой машине шел процесс |

Loki сам проставляет `detected_level`, потому что строка начинается с `level=INF`. Без этого записи попадали бы в `unknown` и не фильтровались по уровню.

> **Внимание, имена меток.** В этом Loki уже заняты `service=asumi-producer` и `service=asumi_exporter` — они принадлежат другим компонентам АСУМИ. Если занять то же имя, записи сольются с чужими и в Grafana получится каша. Своё имя держите отличным от соседних.

#### Описание ключей `Loki`

| Ключ | Назначение | По умолчанию |
|---|---|---|
| `Enabled` | Отправка в Loki | `false` |
| `Url` | Базовый адрес Loki | `http://localhost:3100` |
| `Job` | Метка `job` | `ASUMI.Producer.RabbitMQ.Service` |
| `ServiceName` | Метка `service_name` | `ASUMI.Producer.RabbitMQ.Service` |
| `BatchSize` | Записей в одном запросе | `100` |
| `FlushPeriodSeconds` | Как часто отправлять накопленное, секунды | `2` |
| `TimeoutSeconds` | Таймаут HTTP-запроса, секунды | `5` |
| `QueueCapacity` | Размер очереди в памяти | `10000` |
| `ApiKey` | Bearer-токен, если Loki с авторизацией | — |

#### Поведение при недоступном Loki

Недоступность Loki **не влияет на публикацию договоров**. События складываются в ограниченную очередь в памяти и отправляются фоновым таймером, поэтому медленный или лежащий Loki не блокирует воркер.

- при переполнении очереди отбрасываются самые старые записи — лучше потерять лог, чем застрять на публикации
- ошибка отправки приводит к отбрасыванию батча, исключение наружу не выпускается никогда
- при остановке службы делается финальный сброс, чтобы хвост логов не потерялся

Диагностика самого sink'а — почему записи отбрасываются — пишется отдельно, чтобы не попадать обратно в лог:

```
C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\loki-selflog.log
```

#### Почему не `Serilog.Sinks.Loki`

Пакет написан вручную. Последняя стабильная версия `Serilog.Sinks.Loki` вышла около 2020 года и тянет `Newtonsoft.Json 12.0.3` вместе с `Serilog.Sinks.Http 5.2.1` — неподдерживаемые транзитивные зависимости под Serilog 8. Свой sink использует только `HttpClient` и `System.Text.Json`, которые уже есть в проекте.

### Метрики Prometheus

Служба отдаёт метрики по HTTP на `/metrics`. Используется `prometheus-net 8.2.1`; `prometheus-net.AspNetCore` не подключается, потому что он потребовал бы рантайм ASP.NET Core, а `MetricServer` из ядрового пакета работает поверх `HttpListener`.

Включается ключом `Metrics:Enabled`. При старте видно, что произошло:

```
[INF] Metrics endpoint listening on http://0.0.0.0:9101/metrics.
```

или, если порт поднять не удалось:

```
[ERR] Could not start the metrics endpoint on port 9101. Publishing continues without
metrics. On Windows reserve the URL first from an elevated prompt: netsh http add urlacl
url=http://+:9101/ user="<account>"
```

Во втором случае публикация продолжается как обычно — метрики не влияют на обработку договоров.

#### Метрики

| Имя | Тип | Что показывает |
|---|---|---|
| `asumi_producer_files_pending` | gauge | файлов в `SourcePath` на последнем сканировании |
| `asumi_producer_files_published_total` | counter | подтверждённо опубликованных файлов |
| `asumi_producer_files_failed_total` | counter | файлов, отброшенных из-за необработанной ошибки |
| `asumi_producer_files_deferred_total` | counter | публикаций, отложенных из-за недоступного RabbitMQ |
| `asumi_producer_publish_duration_seconds` | histogram | от чтения файла до подтверждения брокером |
| `asumi_producer_rabbitmq_connected` | gauge | 1/0, состояние соединения с RabbitMQ |
| `asumi_producer_last_publish_timestamp_seconds` | gauge | время последней подтверждённой публикации |
| `asumi_producer_files_unexpected_encoding_total` | counter | файлов без ожидаемого UTF-16 BOM |

Префикс `asumi_producer_` выбран, чтобы не пересечься с мёртвыми `asumi_*` от старого питоновского экспортёра, на который ссылается дашборд.

`files_deferred_total` и `files_unexpected_encoding_total` в дашборде не использовались, но отвечают на вопросы, которые иначе видны только по логам: почему растёт `files_pending`, и не изменился ли формат файлов на стороне АСУМИ.

#### Про кодировку

Файлы АСУМИ приходят в **UTF-16LE** — проверено на 269 файлах без исключения, ни одного в UTF-8 и ни одного без BOM. Поэтому ожидаемым считается UTF-16, а предупреждение в лог уходит об **отклонении** от него:

```
[WRN] contract.json has no UTF-16 BOM (no BOM). ASUMI writes UTF-16LE, so this is unexpected.
The file is published byte-for-byte and the consumer has to decode it accordingly.
```

Обратная логика была бы шумом: при 100% файлов в UTF-16 предупреждение летело бы на каждом, то есть на каждой записи в Loki.

Файлы отправляются байт в байт, поэтому кодировку разбирает consumer. Если АСУМИ сменит формат, `files_unexpected_encoding_total` покажет ненулевое значение — это сигнал, что consumer нужно обновлять.

Границы гистограммы взяты те же, что были у старого экспортёра, чтобы панель P99 работала без правки запроса:

```
0.005 0.01 0.025 0.05 0.1 0.25 0.5 1 2.5 5 10
```

В хвосте гистограммы будут значения ровно на `PublishConfirmTimeoutSeconds`, потому что этим таймаутом ограничено ожидание подтверждения. Это ожидаемо, а не баг.

#### Почему порт 9101, а не 9100

Порт 9100 на машине разработки и на сервере занят службой ASCON Pilot (`Ascon.Pilot.WinService`). Оба процесса регистрировали в HTTP.SYS префикс `http://+:9100/`, из-за чего запросы `/metrics` обслуживал то один, то другой — какой именно, со стороны приложения не гарантировалось. Порт 9101 свободен и конфликта не создаёт.

#### Резервирование URL на Windows

`MetricServer` работает через `HttpListener`, и без зарезервированного URL он бросает access denied. Один раз на машине, из PowerShell **от имени администратора**:

```
netsh http add urlacl url=http://+:9101/ user="NT AUTHORITY\SYSTEM"
```

Для локального запуска под своей учёткой подставьте её имя.

> **Важно:** резервировать нужно URL **с корнем**, то есть `http://+:9101/`. `MetricServer` регистрирует именно этот префикс, а резервирование в Windows покрывает ровно указанный путь — запись для `http://+:9101/metrics` прав на корень не даёт.

Отдельно: порт должен быть открыт в брандмауэре Windows для адреса ВМ со стеком. Файрволом управляет не наш стек.

#### Описание ключей `Metrics`

| Ключ | Назначение | По умолчанию |
|---|---|---|
| `Enabled` | Поднимать ли эндпоинт метрик | `false` |
| `Port` | Порт эндпоинта | `9101` |

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
wsl -e sh -c "docker ps --filter name=rabbitmq"
wsl -e sh -c "docker logs monitoring_rabbitmq_1 --tail 30"
```

### RabbitMQ не принимает сообщения

Проверьте в UI http://localhost:15672:

- **Exchanges** → `asumi.exchange` существует, тип `topic`.
- **Queues** → `asumi.queue` существует, привязана к `asumi.exchange` с routing key `asumi.json`.
- **Queues** → `asumi.queue` → **Message rates** — есть ли публикации.

### Сообщения исчезают из очереди через 5 минут

Это **нормально** — у очереди `asumi.queue` установлен `x-message-ttl = 300000` (`MessageTtlMs`), то есть 5 минут. Если consumer не заберёт сообщение за это время, оно удаляется.

> **Важно:** TTL очереди нельзя изменить после создания. Чтобы поменять — удалите очередь в UI RabbitMQ, потом перезапустите службу. То же касается `QueueType`: тип очереди необратим.

### Ошибка `PRECONDITION_FAILED` при `QueueDeclare`

Очередь уже существует с другими параметрами. Типичные причины:

- тип в конфиге не совпадает с типом в брокере (например, в конфиге `classic`, а очередь создана как `quorum`)
- не совпадает TTL
- для quorum не совпадает `QuorumInitialGroupSize`

Посмотрите реальные аргументы очереди:

```powershell
# Management UI или HTTP API — посмотреть x-queue-type, x-message-ttl, x-quorum-initial-group-size
```

Приведите `QueueType`, `QuorumInitialGroupSize` и `MessageTtlMs` в `appsettings.json` к этим значениям. Пересоздавать очередь не нужно — объявление станет идемпотентным.

### Метрики не отдаются

Смотрите, что написано при старте: `Metrics endpoint listening on ...` либо `Loki sink`-подобное сообщение об отказе с указанием команды `netsh`.

```powershell
# эндпоинт отвечает?
Invoke-WebRequest http://localhost:9101/metrics -UseBasicParsing | Select-Object -ExpandProperty Content

# отвечает ли по IP, а не только по имени
Invoke-WebRequest http://10.54.11.48:9101/metrics -UseBasicParsing

# есть ли резервирование
netsh http show urlacl | Select-String 9101

# не конфликтует ли префикс с другой службой
netsh http show servicestate | Select-String "9101" -Context 0,12
```

Типовые причины:

- **не зарезервирован URL** — в логе `Could not start the metrics endpoint`, нужно выполнить `netsh http add urlacl` от администратора
- **резервирование сделано для `/metrics` вместо корня** — поправьте на `http://+:9101/`
- **`183 conflicts with an existing registration`** — префикс уже занят другой службой, проверьте `servicestate` и выберите свободный порт
- **порт закрыт брандмауэром** для адреса ВМ со стеком

### Логи не появляются в Loki

Проверьте, что sink вообще включился — при старте есть строка `Loki sink enabled for ...`. Если `Loki sink disabled by configuration`, проверьте `Loki:Enabled`.

Дальше смотрите диагностику самого sink'а, а не основной лог:

```powershell
Get-Content C:\Services\ASUMI.Producer.RabbitMQ.Service\logs\loki-selflog.log -Tail 20
```

Там будут код ответа от Loki и текст ошибки. Доступность:

```powershell
Test-NetConnection 172.18.28.118 -Port 3100
Invoke-RestMethod http://172.18.28.118:3100/ready
```

Проверить, что записи реально дошли:

```logql
{job="ASUMI.Producer.RabbitMQ.Service"}
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
- **`Configuration/`** — типизированные опции (`RabbitMqOptions`, `FilePublisherOptions`, `LokiOptions`, `MetricsOptions`) с валидацией через DataAnnotations.
- **`Hosting/`** — `ServiceConstants`, `PasswordResolver` (определяет, из какого scope окружения взят пароль).
- **`Infrastructure/`** — реализации: подключение, публикация, объявление топологии.
- **`Observability/`** — `LokiSink` (свой, без сторонних пакетов), `ProducerMetrics`, `MetricsServerHostedService`.
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

**Пароль RabbitMQ в `appsettings.json` отсутствует.** Значение `"Password": ""` — намеренное. Перед развёртыванием на сервере задайте секрет одним из способов из раздела [«Пароль RabbitMQ»](#пароль-rabbitmq): переменная окружения `RabbitMQ__Password` или `appsettings.Production.json` рядом с `.exe`.

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