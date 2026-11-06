# Finishes a release that GitHub Actions built (.github/workflows/release.yml):
#   1. downloads the draft's installer and checksum,
#   2. checks the build-provenance attestation (built by this repository's workflow) and the checksum,
#   3. signs the installer with the update key (DPAPI-protected on this PC, never in CI),
#   4. uploads the .sig and, with -Publish, publishes the release.
# Usage: .\tools\sign-release.ps1 -Version 0.5.0 [-Publish]
param([Parameter(Mandatory)][string]$Version, [switch]$Publish)
$ErrorActionPreference = "Stop"
$repo = "valuex-creative/Windows-AI-Quota-Bar"
$tag = "v$Version"
$setup = "WindowsAIQuotaBar-Setup-$Version.exe"
$dir = Join-Path ([IO.Path]::GetTempPath()) "qb-release-$Version"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory $dir | Out-Null

gh release download $tag --repo $repo -p $setup -p "$setup.sha256" -D $dir
if ($LASTEXITCODE) { throw "download failed (is there a draft release $tag?)" }
$file = Join-Path $dir $setup

gh attestation verify $file --repo $repo --signer-workflow "$repo/.github/workflows/release.yml"
if ($LASTEXITCODE) { throw "attestation check failed: this installer was not built by the release workflow" }

$expected = (Get-Content (Join-Path $dir "$setup.sha256") -Raw).Split(' ')[0].Trim()
$actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) { throw "checksum mismatch" }

Push-Location (Split-Path $PSScriptRoot)
try {
  dotnet run --project tools/ReleaseSigner -c Release -- sign $file
  if ($LASTEXITCODE) { throw "signing failed" }
  dotnet run --project tools/ReleaseSigner -c Release --no-build -- verify $file
  if ($LASTEXITCODE) { throw "signature check failed" }
} finally { Pop-Location }

gh release upload $tag "$file.sig" --repo $repo --clobber
if ($LASTEXITCODE) { throw "upload failed" }
"Signed and uploaded $setup.sig"
if ($Publish) {
  gh release edit $tag --repo $repo --draft=false --latest
  "Published $tag"
} else { "Review the draft, then: gh release edit $tag --repo $repo --draft=false --latest" }
