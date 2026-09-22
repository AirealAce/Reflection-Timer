param([string]$DotNet = 'dotnet', [string]$Node = 'node', [string]$RuntimeVersion = '')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $PSScriptRoot 'ReflectionTimer.Desktop\ReflectionTimer.Desktop.csproj'
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if (-not $RuntimeVersion) {
    $runtimeMetadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $RuntimeVersion = $runtimeMetadata.'latest-runtime'
}
if ($RuntimeVersion -notmatch '^10\.0\.\d+$') { throw 'Choose a stable .NET 10 runtime patch version.' }

# The same credential-free gate runs locally and in CI, before slow packaging.
& (Join-Path $PSScriptRoot 'validate.ps1') -DotNet $DotNet -Node $Node
# Fresh staging prevents a prior private build from leaking into a public release.
$runRoot = Join-Path $PSScriptRoot ('artifacts\public-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$packageName = "ReflectionTimer-$version-win-x64"
$payloadRoot = Join-Path $runRoot $packageName
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
# A referenced project can otherwise reuse a private Release DLL containing its
# old CodeView/PDB path. Rebuild all references without symbols before publishing.
& $DotNet build $projectPath -c Release -r win-x64 --self-contained true --no-incremental --nologo '-p:PublicRelease=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishTrimmed=false' "-p:PathMap=$repoRoot=/_/src" "-p:RuntimeFrameworkVersion=$RuntimeVersion"
if ($LASTEXITCODE -ne 0) { throw 'Clean public build failed; no ZIP produced.' }
& $DotNet publish $projectPath -c Release -r win-x64 --self-contained true --no-build -o $payloadRoot --nologo '-p:PublicRelease=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishTrimmed=false' "-p:PathMap=$repoRoot=/_/src" "-p:RuntimeFrameworkVersion=$RuntimeVersion"
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed; no ZIP produced.' }

$files = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
$allowedExtensions = @('.exe', '.dll', '.json', '.txt', '.html', '.gs', '.mp3', '.js', '.css', '.md', '.xml')
foreach ($file in $files) {
    if ($file.Extension.ToLowerInvariant() -notin $allowedExtensions -or $file.Name -match '^state\.|^diagnostics\.|^\.env|\.pdb$') {
        throw "Unexpected public package file: $($file.Name). No ZIP produced."
    }
}
# Accept only catalogued, byte-for-byte verified recordings, never additional user audio.
& $Node (Join-Path $repoRoot 'scripts\bundled-audio.cjs') $payloadRoot
if ($LASTEXITCODE -ne 0) { throw 'Bundled audio verification failed; no ZIP produced.' }
# User audio and state are never copied. Check text and
# managed binaries for personalized config as a second safety net (UTF-8/UTF-16).
$privacyPatterns = @('https://docs\.google\.com/spreadsheets/d/[A-Za-z0-9_-]{20,}', 'https://script\.google\.com/macros/s/[A-Za-z0-9_-]{20,}/exec',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----', '\bgh[pousr]_[A-Za-z0-9]{30,}\b', '\bgithub_pat_[A-Za-z0-9_]{40,}\b', '\bAIza[A-Za-z0-9_-]{35}\b')
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $text = $encoding.GetString($bytes)
        foreach ($pattern in $privacyPatterns) { if ($text -match $pattern) { throw "Personal connection identifier found in $($file.Name). No ZIP produced." } }
        if ($file.Name -like 'ReflectionTimer*' -and $text -match '(?i)[A-Z]:\\Users\\|/home/|/Users/') {
            throw "Private build path found in $($file.Name). No ZIP produced."
        }
        if ($file.Extension -in @('.json', '.gs', '.html') -and $text -match '"ApiToken"\s*:\s*"[^"]+"|function setupReflectionTimer\(\)\s*\{|"SetupDraft"\s*:|"Outbox"\s*:') {
            throw "Personalized setup/state data found in $($file.Name). No ZIP produced."
        }
    }
}
foreach ($required in @('ReflectionTimer.exe', 'coreclr.dll', 'hostfxr.dll', 'System.Windows.Forms.dll', 'START-HERE.html', 'google-sheets-script.gs', 'THIRD-PARTY-NOTICES.txt', 'AUDIO-NOTICES.txt', 'Web\index.html', 'Web\app.js', 'Web\compact.html', 'Web\themes.css', 'Web\themes.js', 'Microsoft.Web.WebView2.WinForms.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $required))) { throw "Required self-contained package file is missing: $required" }
}
$manifestEntries = @($files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($payloadRoot.Length + 1).Replace('\','/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{ FormatVersion = 1; Version = $version; Runtime = 'win-x64'; RuntimeVersion = $RuntimeVersion; Files = $manifestEntries }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payloadRoot 'package-manifest.json') -Encoding utf8
$archivePath = Join-Path $runRoot ($packageName + '.zip')
Compress-Archive -LiteralPath $payloadRoot -DestinationPath $archivePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
($checksum + '  ' + [IO.Path]::GetFileName($archivePath)) | Set-Content -LiteralPath ($archivePath + '.sha256.txt') -Encoding ascii
[pscustomobject]@{ Archive = $archivePath; Sha256 = $checksum; PayloadFiles = $files.Count; SizeMB = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1) } | ConvertTo-Json
Write-Host 'Package built locally only. Review the unsigned release and publish deliberately; nothing was uploaded to GitHub.'
