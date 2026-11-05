# Builds the release: a self-contained app folder, a portable zip, the Setup.exe installer
# and its .sha256 (the in-app updater refuses an installer without a matching checksum).
# Usage: .\build-release.ps1 [-Version 0.1.2]   (default: <Version> in Directory.Build.props)
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }
"Building $Version"

$out = "artifacts\WindowsAIQuotaBar"
if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }

# No debug symbols and mapped source paths, so the binaries carry no local paths.
dotnet publish src/QuotaBar.App -c Release -r win-x64 --self-contained true -o $out `
  -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false `
  -p:ContinuousIntegrationBuild=true -p:Deterministic=true "-p:PathMap=$PSScriptRoot=/_"
if ($LASTEXITCODE) { throw "publish failed" }

Compress-Archive -Path "$out\*" -DestinationPath "artifacts\WindowsAIQuotaBar-$Version-win-x64-portable.zip" -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$Version" installer\WindowsAIQuotaBar.iss
if ($LASTEXITCODE) { throw "installer build failed" }

$setup = "WindowsAIQuotaBar-Setup-$Version.exe"
$hash = (Get-FileHash "artifacts\$setup" -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$PSScriptRoot\artifacts\$setup.sha256", "$hash  $setup`n")

# Sign the installer with the maintainer's update key (DPAPI-protected on this PC, never in the repo).
# Installed copies refuse an update whose signature does not match the public key compiled into the app.
dotnet run --project tools/ReleaseSigner -c Release -- sign "artifacts\$setup"
if ($LASTEXITCODE) { throw "signing failed (no key? see tools/ReleaseSigner)" }
dotnet run --project tools/ReleaseSigner -c Release --no-build -- verify "artifacts\$setup"
if ($LASTEXITCODE) { throw "signature check failed" }

Get-ChildItem artifacts -File | ForEach-Object { "{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"Publish: gh release create v$Version artifacts\$setup artifacts\$setup.sha256 artifacts\$setup.sig artifacts\WindowsAIQuotaBar-$Version-win-x64-portable.zip --notes-file <notes>"
