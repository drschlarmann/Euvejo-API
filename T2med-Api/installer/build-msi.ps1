$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = Join-Path $PSScriptRoot 'payload'
if ((Resolve-Path $PSScriptRoot).Path -ne (Join-Path $project 'installer')) { throw 'Unerwarteter Buildpfad.' }
$payloadFull = [IO.Path]::GetFullPath($payload)
if (-not $payloadFull.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Payloadpfad liegt ausserhalb des Installer-Verzeichnisses.'
}
if ((Test-Path -LiteralPath $payloadFull) -and ((Get-Item -LiteralPath $payloadFull).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Payloadpfad darf keine Verknuepfung sein.'
}
if (Test-Path -LiteralPath $payloadFull) {
    Remove-Item -LiteralPath $payloadFull -Recurse -Force
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null

dotnet publish (Join-Path $project 'Euvejo-Api.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'API-Publish fehlgeschlagen.' }
dotnet publish (Join-Path $PSScriptRoot 'MsiActions\Euvejo-Api-MsiActions.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'MSI-Helper-Publish fehlgeschlagen.' }
dotnet publish (Join-Path $project '..\T2med-Api-Config\Euvejo-Api-Config.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Konfigurationsprogramm-Publish fehlgeschlagen.' }
dotnet publish (Join-Path $project '..\T2med-Api-Test\Euvejo-Api-Test.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $payload
if ($LASTEXITCODE -ne 0) { throw 'API-Testprogramm-Publish fehlgeschlagen.' }
if (-not (Test-Path (Join-Path $payload 'Euvejo-Api-Test.exe'))) { throw 'API-Testprogramm fehlt im Payload.' }
if (-not (Test-Path (Join-Path $payload 'Euvejo-Api-Config.exe'))) { throw 'Konfigurationsprogramm fehlt im Payload.' }

Push-Location $PSScriptRoot
try {
    wix build msi\Product.wxs -arch x64 -ext WixToolset.UI.wixext -out Euvejo-Api.msi
    if ($LASTEXITCODE -ne 0) { throw 'MSI-Build fehlgeschlagen.' }
    Copy-Item -LiteralPath Euvejo-Api.msi -Destination Euvejo-Api-Version1.0.15.msi -Force
} finally {
    Pop-Location
}
