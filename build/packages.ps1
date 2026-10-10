# Манифесты пакетных менеджеров для версии: winget (artifacts/winget/<версия>/) и Scoop (bucket/tropa.json).
#   pwsh build/packages.ps1 -Version 1.2.0
# Хеши берутся из artifacts/*.sha256, поэтому запускать после publish.ps1 (и после подписи).
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$art = Join-Path $root "artifacts"
$repo = "https://github.com/xckrt/Tropa"
$base = "$repo/releases/download/v$Version"

function Hash($file) { (Get-Content (Join-Path $art "$file.sha256")).Split(" ")[0].ToUpperInvariant() }
$setupHash = Hash "Tropa-Setup-$Version.exe"
$portableHash = Hash "Tropa-Portable-$Version.zip"

# ---- winget: три файла манифеста (схема 1.6), отправляются в microsoft/winget-pkgs (см. docs/packages.md)
$id = "xckrt.Tropa"
$out = Join-Path $art "winget/$Version"
New-Item -ItemType Directory -Force $out | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false

[IO.File]::WriteAllText((Join-Path $out "$id.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $out "$id.installer.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: machine
InstallModes:
  - interactive
  - silent
  - silentWithProgress
InstallerSwitches:
  Silent: /silent
  SilentWithProgress: /silent
UpgradeBehavior: install
ElevationRequirement: elevatesSelf
Installers:
  - Architecture: x64
    InstallerUrl: $base/Tropa-Setup-$Version.exe
    InstallerSha256: $setupHash
ManifestType: installer
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $out "$id.locale.en-US.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: xckrt
PublisherUrl: https://github.com/xckrt
PackageName: Tropa
PackageUrl: https://xckrt.github.io/Tropa/
License: MIT
LicenseUrl: $repo/blob/main/LICENSE
PrivacyUrl: $repo/blob/main/PRIVACY.md
ShortDescription: Diagnoses network blocking and configures the zapret DPI engine automatically.
Description: |-
  Tropa checks services stage by stage (DNS, TCP, TLS, HTTP, transfer), names how a connection is blocked and picks a
  working zapret strategy for it, then keeps it working across reboots and network changes. It also includes a local
  Telegram proxy and secure DNS (DoH). The interface is in English and Russian.
Moniker: tropa
Tags:
  - dpi
  - network
  - zapret
  - youtube
  - discord
  - telegram
  - doh
ReleaseNotesUrl: $repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $out "$id.locale.ru-RU.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: ru-RU
Publisher: xckrt
PackageName: Тропа
License: MIT
ShortDescription: Сама находит, как провайдер режет YouTube, Discord и Telegram, и настраивает обход (zapret).
ManifestType: locale
ManifestVersion: 1.6.0
"@, $utf8)
"winget: $out"

# ---- Scoop: бакет в этом репозитории — scoop bucket add tropa https://github.com/xckrt/Tropa
$bucket = Join-Path $root "bucket"
New-Item -ItemType Directory -Force $bucket | Out-Null
$scoop = [ordered]@{
    version     = $Version
    description = "Сама находит, как провайдер режет YouTube, Discord и Telegram, и настраивает обход (переносная версия)"
    homepage    = "https://xckrt.github.io/Tropa/"
    license     = "MIT"
    notes       = "Обход работает, пока открыта Тропа. Чтобы он держался всегда, поставьте обычную версию: winget install xckrt.Tropa"
    architecture = [ordered]@{ "64bit" = [ordered]@{ url = "$base/Tropa-Portable-$Version.zip"; hash = $portableHash.ToLowerInvariant() } }
    shortcuts   = @(, @("Tropa.exe", "Тропа"))
    persist     = "data"
    checkver    = [ordered]@{ github = $repo }
    autoupdate  = [ordered]@{
        architecture = [ordered]@{
            "64bit" = [ordered]@{
                url  = "$repo/releases/download/v`$version/Tropa-Portable-`$version.zip"
                hash = [ordered]@{ url = "`$url.sha256" }
            }
        }
    }
}
[IO.File]::WriteAllText((Join-Path $bucket "tropa.json"), ($scoop | ConvertTo-Json -Depth 8) + "`n", $utf8)
"scoop: $(Join-Path $bucket 'tropa.json')"
