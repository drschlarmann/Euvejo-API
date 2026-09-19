param(
    [string]$InstallDir = "C:\Program Files\euvejo-api",
    [string]$ServiceName = "euvejo-api",
    [switch]$OverwriteConfig
)

$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadDir = Join-Path $packageRoot "payload"

if (-not (Test-Path -LiteralPath $payloadDir)) {
    throw "Payload-Verzeichnis wurde nicht gefunden: $payloadDir"
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Bitte PowerShell als Administrator starten."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus("Stopped", "00:00:30")
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

$existingConfig = Join-Path $InstallDir "appsettings.json"
$backupConfig = $null
if ((Test-Path -LiteralPath $existingConfig) -and -not $OverwriteConfig) {
    $backupConfig = Join-Path $env:TEMP ("euvejo-api-appsettings-" + [guid]::NewGuid() + ".json")
    Copy-Item -LiteralPath $existingConfig -Destination $backupConfig -Force
}

Copy-Item -Path (Join-Path $payloadDir "*") -Destination $InstallDir -Recurse -Force

if ($backupConfig) {
    Copy-Item -LiteralPath $backupConfig -Destination $existingConfig -Force
    Remove-Item -LiteralPath $backupConfig -Force
}

$exePath = Join-Path $InstallDir "Euvejo-Api.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Euvejo-Api.exe wurde nach der Installation nicht gefunden: $exePath"
}

if (-not $service) {
    New-Service `
        -Name $ServiceName `
        -BinaryPathName "`"$exePath`"" `
        -DisplayName "Euvejo-API" `
        -Description "Lokale Euvejo-API fuer Euvejo" `
        -StartupType Automatic | Out-Null
}

$networkSetup = Start-Process -FilePath (Join-Path $InstallDir 'Euvejo-Api-MsiActions.exe') -ArgumentList "configure-network `"$existingConfig`"" -Wait -PassThru -WindowStyle Hidden
if ($networkSetup.ExitCode -ne 0) {
    throw 'Netzwerk-Konfiguration fehlgeschlagen.'
}
Start-Service -Name $ServiceName
Write-Host "Euvejo-Api wurde installiert und gestartet."
Write-Host "Installationspfad: $InstallDir"
Write-Host "Dienstname: $ServiceName"
