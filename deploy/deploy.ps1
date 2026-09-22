<#
.SYNOPSIS
    Развёртывание ASUMI.Producer.RabbitMQ.Service на Windows Server.

.DESCRIPTION
    Скрипт выполняет:
      1. Проверку окружения (.NET 8 Runtime, доступность RabbitMQ).
      2. Установку .NET 8 Runtime при необходимости (offline-установщик или winget).
      3. Копирование артефакта на целевой сервер.
      4. Создание службы Windows с настройкой recovery.
      5. Запуск и проверку.

.PARAMETER ServerName
    Имя целевого сервера (например, nsk-sapp01). Если совпадает с локальным — 
    развёртывание локальное, иначе — через PowerShell Remoting (WinRM).

.PARAMETER ArtifactPath
    Путь к папке с артефактом (exe + appsettings). Локальный путь или UNC.

.PARAMETER ServicePath
    Куда разворачивать службу на сервере. По умолчанию C:\Services\...

.PARAMETER ServiceName
    Имя службы в SCM. По умолчанию "ASUMI Producer RabbitMQ Service".

.PARAMETER DotNetInstallerPath
    Путь к offline-установщику .NET 8 Runtime. Если не указан — установка 
    через winget или скачивание с Microsoft.

.PARAMETER SkipDotNetInstall
    Пропустить установку .NET (если уверены, что он уже есть).

.EXAMPLE
    .\deploy.ps1 -ServerName nsk-sapp01 `
                 -ArtifactPath "\\fileserver\share\ASUMI.Producer.RabbitMQ.Service" `
                 -DotNetInstallerPath "\\fileserver\share\dotnet-runtime-8.0.22-win-x64.exe"

.EXAMPLE
    .\deploy.ps1 -ServerName nsk-sapp01 -SkipDotNetInstall
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerName,

    [Parameter(Mandatory = $false)]
    [string]$ArtifactPath = "\\fileserver\share\ASUMI.Producer.RabbitMQ.Service",

    [Parameter(Mandatory = $false)]
    [string]$ServicePath = "C:\Services\ASUMI.Producer.RabbitMQ.Service",

    [Parameter(Mandatory = $false)]
    [string]$ServiceName = "ASUMI Producer RabbitMQ Service",

    [Parameter(Mandatory = $false)]
    [string]$DotNetInstallerPath = "",

    [Parameter(Mandatory = $false)]
    [switch]$SkipDotNetInstall
)

$ErrorActionPreference = "Stop"

function Write-Step   { param([string]$Msg) Write-Host "`n=== $Msg ===" -ForegroundColor Cyan }
function Write-Ok     { param([string]$Msg) Write-Host "[OK]  $Msg" -ForegroundColor Green }
function Write-Warn   { param([string]$Msg) Write-Host "[!]   $Msg" -ForegroundColor Yellow }
function Write-Fail   { param([string]$Msg) Write-Host "[ERR] $Msg" -ForegroundColor Red }

# ---------------------------------------------------------------
# Проверка: локальный запуск или удалённый
# ---------------------------------------------------------------
$isLocal = ($ServerName -eq $env:COMPUTERNAME) -or ($ServerName -eq "localhost") -or ($ServerName -eq ".")

if ($isLocal) {
    Write-Step "Развёртывание на ЛОКАЛЬНОМ сервере ($ServerName)"
    $session = $null
} else {
    Write-Step "Развёртывание на УДАЛЁННОМ сервере ($ServerName) через WinRM"
    try {
        $session = New-PSSession -ComputerName $ServerName -ErrorAction Stop
        Write-Ok "Удалённая сессия с $ServerName установлена"
    } catch {
        Write-Fail "Не удалось подключиться к $ServerName по WinRM: $_"
        Write-Warn "Проверьте: WinRM включён, firewall, права администратора на удалённой машине"
        exit 1
    }
}

function Invoke-OnTarget {
    param([scriptblock]$ScriptBlock)
    if ($isLocal) {
        & $ScriptBlock
    } else {
        Invoke-Command -Session $session -ScriptBlock $ScriptBlock
    }
}

# ---------------------------------------------------------------
# Шаг 1. Проверка .NET 8 Runtime
# ---------------------------------------------------------------
Write-Step "Шаг 1. Проверка .NET 8 Runtime"

$dotnetCheck = Invoke-OnTarget {
    $result = @{
        DotNetFound = $false
        Version     = $null
        DotnetExe   = $null
    }
    $dotnetExe = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetExe) {
        $runtimes = & dotnet --list-runtimes 2>$null
        $net8 = $runtimes | Where-Object { $_ -match "Microsoft\.NETCore\.App 8\.0\." }
        if ($net8) {
            $result.DotNetFound = $true
            $result.Version = ($net8 -split '\s+')[1]
            $result.DotnetExe = $dotnetExe.Source
        }
    }
    $result
}

if ($dotnetCheck.DotNetFound) {
    Write-Ok ".NET 8 Runtime найден: $($dotnetCheck.Version) ($($dotnetCheck.DotnetExe))"
} else {
    Write-Warn ".NET 8 Runtime НЕ найден на $ServerName"
    
    if ($SkipDotNetInstall) {
        Write-Fail "Флаг -SkipDotNetInstall установлен, но .NET 8 не найден. Прерываю."
        exit 1
    }

    Write-Step "Шаг 1.1. Установка .NET 8 Runtime"

    if ($DotNetInstallerPath -and (Test-Path $DotNetInstallerPath)) {
        # Вариант A: offline-установщик
        Write-Host "Использую offline-установщик: $DotNetInstallerPath"
        
        $installResult = Invoke-OnTarget -ScriptBlock {
            param($installerPath)
            
            # Копируем установщик локально на целевой сервер (для удалённого случая)
            $localInstaller = "$env:TEMP\dotnet-runtime-8-installer.exe"
            Copy-Item -Path $installerPath -Destination $localInstaller -Force
            
            Write-Host "Запускаю установщик..."
            $proc = Start-Process -FilePath $localInstaller `
                                  -ArgumentList "/install", "/quiet", "/norestart" `
                                  -Wait -PassThru
            Remove-Item $localInstaller -Force -ErrorAction SilentlyContinue
            return $proc.ExitCode
        } -ArgumentList $DotNetInstallerPath
        
        # Для Invoke-Command с параметрами используем другой вызов
        if (-not $isLocal) {
            $installResult = Invoke-Command -Session $session -ScriptBlock {
                param($installerPath)
                $localInstaller = "$env:TEMP\dotnet-runtime-8-installer.exe"
                Copy-Item -Path $installerPath -Destination $localInstaller -Force
                $proc = Start-Process -FilePath $localInstaller `
                                      -ArgumentList "/install", "/quiet", "/norestart" `
                                      -Wait -PassThru
                Remove-Item $localInstaller -Force -ErrorAction SilentlyContinue
                return $proc.ExitCode
            } -ArgumentList $DotNetInstallerPath
        }
        
        if ($installResult -eq 0 -or $installResult -eq 3010) {
            Write-Ok "Установщик .NET завершён (exit code: $installResult)"
            if ($installResult -eq 3010) {
                Write-Warn "Требуется перезагрузка сервера для завершения установки .NET"
            }
        } else {
            Write-Fail "Установщик .NET завершился с ошибкой: exit code $installResult"
            exit 1
        }
    } elseif (Invoke-OnTarget { Get-Command winget -ErrorAction SilentlyContinue }) {
        # Вариант B: winget
        Write-Host "Использую winget для установки .NET 8 Runtime..."
        Invoke-OnTarget {
            winget install Microsoft.DotNet.Runtime.8 --silent --accept-package-agreements --accept-source-agreements
        }
        Write-Ok "winget установил .NET 8 Runtime"
    } else {
        # Вариант C: скачивание с Microsoft
        Write-Warn "Ни offline-установщик, ни winget не доступны."
        Write-Host "Скачиваю установщик с Microsoft..."
        
        $dotnetUrl = "https://dotnet.microsoft.com/download/dotnet/8.0"
        Write-Fail "Автоматическая загрузка с Microsoft не реализована (URL меняется)."
        Write-Host ""
        Write-Host "Сделайте одно из следующего:" -ForegroundColor Yellow
        Write-Host "  1. Скачайте вручную: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        Write-Host "     Файл: 'Windows x64 Installer' (.NET Runtime 8.0.x)" -ForegroundColor Yellow
        Write-Host "  2. Положите .exe на файловый ресурс и передайте через -DotNetInstallerPath" -ForegroundColor Yellow
        Write-Host "  3. Установите .NET на сервере вручную и запустите с -SkipDotNetInstall" -ForegroundColor Yellow
        exit 1
    }

    # Перепроверка
    Start-Sleep -Seconds 3
    $recheck = Invoke-OnTarget {
        $runtimes = & dotnet --list-runtimes 2>$null
        ($runtimes | Where-Object { $_ -match "Microsoft\.NETCore\.App 8\.0\." }) -ne $null
    }
    
    if ($recheck) {
        Write-Ok ".NET 8 Runtime успешно установлен"
    } else {
        Write-Fail ".NET 8 Runtime всё ещё не найден после установки"
        exit 1
    }
}

# ---------------------------------------------------------------
# Шаг 2. Проверка доступности RabbitMQ
# ---------------------------------------------------------------
Write-Step "Шаг 2. Проверка доступности RabbitMQ"

# Прочитаем appsettings из артефакта, чтобы узнать HostName и Port
$appsettingsPath = Join-Path $ArtifactPath "appsettings.json"
if (Test-Path $appsettingsPath) {
    $appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
    $rabbitHost = $appsettings.RabbitMQ.HostName
    $rabbitPort = $appsettings.RabbitMQ.Port
    
    Write-Host "Проверяю подключение к $rabbitHost`:$rabbitPort..."
    $test = Invoke-OnTarget -ScriptBlock {
        param($hostName, $port)
        Test-NetConnection -ComputerName $hostName -Port $port -WarningAction SilentlyContinue
    } -ArgumentList $rabbitHost, $rabbitPort
    
    if ($test.TcpTestSucceeded) {
        Write-Ok "RabbitMQ доступен: $rabbitHost`:$rabbitPort"
    } else {
        Write-Warn "RabbitMQ НЕ доступен: $rabbitHost`:$rabbitPort"
        Write-Warn "Служба запустится, но будет ждать подключения. Это ОК, если RabbitMQ поднимется позже."
    }
} else {
    Write-Warn "appsettings.json не найден в $ArtifactPath, пропускаю проверку RabbitMQ"
}

# ---------------------------------------------------------------
# Шаг 3. Остановка и удаление существующей службы
# ---------------------------------------------------------------
Write-Step "Шаг 3. Остановка и удаление существующей службы (если есть)"

Invoke-OnTarget -ScriptBlock {
    param($svcName)
    $existing = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Останавливаю службу '$svcName'..."
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        & sc.exe delete $svcName | Out-Null
        Start-Sleep -Seconds 2
        Write-Host "Служба удалена."
    } else {
        Write-Host "Служба '$svcName' не найдена, пропускаю."
    }
} -ArgumentList $ServiceName

# ---------------------------------------------------------------
# Шаг 4. Копирование артефакта
# ---------------------------------------------------------------
Write-Step "Шаг 4. Копирование артефакта в $ServicePath"

Invoke-OnTarget -ScriptBlock {
    param($src, $dst)
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force
    Write-Host "Файлы скопированы в $dst"
    Get-ChildItem $dst | Select-Object Name, Length | Format-Table
} -ArgumentList $ArtifactPath, $ServicePath

# ---------------------------------------------------------------
# Шаг 5. Создание службы
# ---------------------------------------------------------------
Write-Step "Шаг 5. Создание службы '$ServiceName'"

Invoke-OnTarget -ScriptBlock {
    param($svcName, $svcPath)
    $exePath = Join-Path $svcPath "ASUMI.Producer.RabbitMQ.Service.exe"
    
    & sc.exe create $svcName binPath= $exePath start= auto DisplayName= $svcName | Out-Null
    & sc.exe description $svcName "Publishes ASUMI contract JSON files to RabbitMQ." | Out-Null
    & sc.exe failure $svcName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
    
    # AppDirectory — рабочая директория
    & reg add "HKLM\SYSTEM\CurrentControlSet\Services\$svcName" /v AppDirectory /t REG_SZ /d $svcPath /f | Out-Null
    
    Write-Host "Служба '$svcName' создана."
} -ArgumentList $ServiceName, $ServicePath

# ---------------------------------------------------------------
# Шаг 6. Запуск
# ---------------------------------------------------------------
Write-Step "Шаг 6. Запуск службы"

Invoke-OnTarget -ScriptBlock {
    param($svcName)
    & sc.exe start $svcName | Out-Null
    Start-Sleep -Seconds 5
    Get-Service -Name $svcName | Format-Table Name, Status, StartType
} -ArgumentList $ServiceName

# ---------------------------------------------------------------
# Шаг 7. Проверка логов
# ---------------------------------------------------------------
Write-Step "Шаг 7. Проверка логов"

Invoke-OnTarget -ScriptBlock {
    param($svcPath)
    $logsDir = Join-Path $svcPath "logs"
    if (Test-Path $logsDir) {
        $logFile = Get-ChildItem $logsDir -Filter "asumi-producer-rabbitmq-*.log" |
                   Sort-Object LastWriteTime -Descending |
                   Select-Object -First 1
        if ($logFile) {
            Write-Host "Последние 15 строк из $($logFile.Name):" -ForegroundColor Cyan
            Get-Content $logFile.FullName -Tail 15
        } else {
            Write-Warn "Лог-файлы не найдены в $logsDir"
        }
    } else {
        Write-Warn "Папка логов не найдена: $logsDir"
        Write-Warn "Проверьте, что в Program.cs путь к логам через AppContext.BaseDirectory"
    }
} -ArgumentList $ServicePath

# ---------------------------------------------------------------
# Завершение
# ---------------------------------------------------------------
Write-Step "Готово"
Write-Host "Сервер:       $ServerName" -ForegroundColor Green
Write-Host "Служба:       $ServiceName" -ForegroundColor Green
Write-Host "Путь:         $ServicePath" -ForegroundColor Green
Write-Host "Логи:         $ServicePath\logs\" -ForegroundColor Green
Write-Host ""
Write-Host "Управление службой:" -ForegroundColor Cyan
Write-Host "  sc.exe query `"$ServiceName`""
Write-Host "  sc.exe stop `"$ServiceName`""
Write-Host "  sc.exe start `"$ServiceName`""

if ($session) {
    Remove-PSSession -Session $session
}