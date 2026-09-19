param(
    [string]$InstallDir = "C:\Program Files\euvejo-api",
    [string]$ServiceName = "euvejo-api",
    [switch]$RemoveFiles
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Bitte PowerShell als Administrator starten."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", "00:00:30")
    }

    sc.exe delete $ServiceName | Out-Null
    Write-Host "Dienst wurde entfernt: $ServiceName"
}
else {
    Write-Host "Dienst war nicht vorhanden: $ServiceName"
}

if ($RemoveFiles -and (Test-Path -LiteralPath $InstallDir)) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Installationsverzeichnis wurde entfernt: $InstallDir"
}
elseif (Test-Path -LiteralPath $InstallDir) {
    Write-Host "Installationsverzeichnis bleibt erhalten: $InstallDir"
}
