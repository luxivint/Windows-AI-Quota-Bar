# Builds the release: a self-contained app folder, a portable zip and the Setup.exe installer.
# Usage: .\build-release.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.1")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

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

Get-ChildItem artifacts -File | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
