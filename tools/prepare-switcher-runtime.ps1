param([string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destination = Join-Path $workspace 'artifacts/switcher-runtime'
if ($RuntimeDirectory) {
    $runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $runtime 'SwitcherHost.exe'))) { throw 'SwitcherHost.exe is missing.' }
} else {
    # Pinned upstream release. Its --self-test uses empty temporary homes and never opens the user's Desktop.
    $dependencies = Join-Path $workspace 'artifacts/dependencies'
    New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
    $package = Join-Path $dependencies 'Codex-Account-Switcher-v0.1.27.exe'
    $expected = '1fc3e5636b903377f4b87e7ca90ae88db3bf8e2355709e25b45259e5d47dab06'
    if (-not (Test-Path -LiteralPath $package)) {
        Invoke-WebRequest 'https://github.com/liuzhao1225/codex-account-switcher/releases/download/v0.1.27/Codex-Account-Switcher-windows-x64.exe' -OutFile $package
    }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Upstream package checksum mismatch.' }
    $result = Join-Path $dependencies ('self-test-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $savedResultVariable = $env:CODEX_SWITCHER_SELF_TEST_RESULT
    try {
        $env:CODEX_SWITCHER_SELF_TEST_RESULT = $result
        $probe = Start-Process -FilePath $package -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
        if (-not $probe.WaitForExit(45000)) {
            Stop-Process -Id $probe.Id
            throw 'Upstream package self-test timed out.'
        }
        if (-not (Test-Path -LiteralPath $result) -or -not ([IO.File]::ReadAllText($result).StartsWith('PASS:'))) { throw 'Upstream package self-test failed.' }
    } finally { $env:CODEX_SWITCHER_SELF_TEST_RESULT = $savedResultVariable }
    $runtime = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Codex Account Switcher Runtime/484056741F47AA25DBA69D39E1C6B8BC5D62DF8026BB84596547A3D4125FB5C4'
    $hostFile = Join-Path $runtime 'SwitcherHost.exe'
    if (-not (Test-Path -LiteralPath $hostFile) -or
        (Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash -ne '818CA6743261FD3D33D17411E9C954B7BEBC543DF584F5035D4543CCDF9E572F') {
        throw 'The extracted host does not match the pinned release.'
    }
}
# Never recursively replace an arbitrary user-supplied directory. This destination is fixed inside the workspace.
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $runtime -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
$manifest = Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Name -ne 'runtime-manifest.json' } | ForEach-Object {
    [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[IO.File]::WriteAllText((Join-Path $destination 'runtime-manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output "Ready: $destination"
