param([string]$DotNet = 'dotnet', [string]$Node = 'node')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & $Node scripts/version.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Version validation failed.' }
    & $Node scripts/check-public-source.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Public-source privacy scan failed.' }
    & $DotNet run --project desktop/ReflectionTimer.Tests/ReflectionTimer.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed.' }
    & $Node desktop/ReflectionTimer.Tests/ui.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser accessibility tests failed.' }
    & $Node --test test/apps-script.test.js test/receiver-setup.test.js test/release-assets.test.js test/version.test.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Receiver/package/version tests failed.' }
} finally { Pop-Location }
