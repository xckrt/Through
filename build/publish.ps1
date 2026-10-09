# Сборка установщика: Tropa-Setup одним файлом с программой внутри.
#   pwsh build/publish.ps1 -Version 1.0.0
# Результат: artifacts/Tropa-Setup-<версия>.exe и .sha256 рядом.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$art = Join-Path $root "artifacts"
$stage = Join-Path $art "stage"
if (Test-Path $art) { Remove-Item -Recurse -Force $art }
New-Item -ItemType Directory -Force $stage | Out-Null

# окно и служба самодостаточные (.NET внутри): у пользователя ничего не нужно ставить заранее
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:Version=$Version", "-p:DebugType=none")
dotnet publish (Join-Path $root "src/Tropa.Tray") @common -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish Tropa.Tray" }
dotnet publish (Join-Path $root "src/Tropa.Service") @common -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish Tropa.Service" }

$payload = Join-Path $art "payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
"payload: {0:N1} МБ" -f ((Get-Item $payload).Length / 1MB)

# установщик — один exe на .NET Framework 4.8 (он есть в каждой Windows 10/11), payload.zip внутри
$setupOut = Join-Path $art "setup"
dotnet build (Join-Path $root "src/Tropa.Setup") -c Release "-p:Version=$Version" "-p:PayloadPath=$payload" -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "build Tropa.Setup" }

$setup = Join-Path $art "Tropa-Setup-$Version.exe"
Move-Item (Join-Path $setupOut "Tropa.Setup.exe") $setup
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Tropa-Setup-$Version.exe" | Set-Content -Encoding ascii "$setup.sha256"
"установщик: {0} ({1:N1} МБ)" -f $setup, ((Get-Item $setup).Length / 1MB)
"sha256: $hash"
