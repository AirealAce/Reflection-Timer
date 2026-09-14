param([string]$DotNet = 'dotnet', [string]$PackageDirectory = '')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$programsRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'
$installPath = [IO.Path]::GetFullPath((Join-Path $programsRoot 'ReflectionTimerDesktop'))
$exePath = Join-Path $installPath 'ReflectionTimer.exe'
if (Get-Process ReflectionTimer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }) {
    throw 'Quit Reflection Timer from its tray menu before installing. Saved data will be retained.'
}
if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $PSScriptRoot ('artifacts\install-' + [guid]::NewGuid().ToString('N'))
    & $DotNet run --project (Join-Path $PSScriptRoot 'ReflectionTimer.Tests\ReflectionTimer.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Desktop checks failed; installation cancelled.' }
    & $DotNet publish (Join-Path $PSScriptRoot 'ReflectionTimer.Desktop\ReflectionTimer.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $PackageDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; installation cancelled.' }
}
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($required in @('ReflectionTimer.exe', 'coreclr.dll', 'Web\index.html', 'Web\app.js', 'Web\compact.html', 'Web\themes.css')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory $required) -PathType Leaf)) { throw "Incomplete app package: $required" }
}
if (Test-Path -LiteralPath (Join-Path $PackageDirectory 'state.dat')) { throw 'A package must not contain saved user data.' }
$manifestPath = Join-Path $PackageDirectory 'package-manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $entry.Path))
        if (-not $path.StartsWith($PackageDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package manifest path.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Package integrity check failed.' }
    }
}
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$stagingPath = $installPath + '-staging-' + $stamp
$backupPath = $installPath + '-backup-' + $stamp
# Verify every directory involved in the atomic swap remains under the named per-user Programs folder.
$programsBoundary = [IO.Path]::GetFullPath($programsRoot) + '\'
foreach ($path in @($installPath, $stagingPath, $backupPath)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($programsBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installation path.' }
}
Copy-Item -LiteralPath $PackageDirectory -Destination $stagingPath -Recurse
# Locally added audio stays available at its previous location after the upgrade.
if (Test-Path -LiteralPath $installPath) {
    foreach ($file in Get-ChildItem -LiteralPath $installPath -File -Recurse -Filter '*.mp3') {
        $destination = Join-Path $stagingPath $file.FullName.Substring($installPath.Length + 1)
        if (-not (Test-Path -LiteralPath $destination)) {
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}
$dataBackup = Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.reflection-timer-backups\' + $stamp)
$profileSources = @{
    Shared = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.reflection-timer'
    Legacy = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ReflectionTimerDesktop'
}
foreach ($name in $profileSources.Keys) {
    $dataPath = $profileSources[$name]
    if (Test-Path -LiteralPath $dataPath) {
        $copyDirectory = Join-Path $dataBackup $name
        New-Item -ItemType Directory -Path $copyDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $dataPath -File | Where-Object { $_.Name -like 'state.dat*' -or $_.Name -like 'diagnostics.dat*' } |
            ForEach-Object {
                $copyPath = Join-Path $copyDirectory $_.Name
                Copy-Item -LiteralPath $_.FullName -Destination $copyPath
                if ((Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $copyPath).Hash) { throw 'Encrypted profile backup verification failed.' }
            }
    }
}
# Recheck immediately before changing the executable path.
if (Get-Process ReflectionTimer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }) { throw 'Reflection Timer reopened. Quit it and rerun the installer.' }
$hadPrevious = Test-Path -LiteralPath $installPath
if ($hadPrevious) { Move-Item -LiteralPath $installPath -Destination $backupPath }
try { Move-Item -LiteralPath $stagingPath -Destination $installPath }
catch {
    if ($hadPrevious -and -not (Test-Path -LiteralPath $installPath)) { Move-Item -LiteralPath $backupPath -Destination $installPath }
    throw
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Reflection Timer Desktop.lnk'
$shellObject = New-Object -ComObject WScript.Shell
if ((Test-Path -LiteralPath $shortcutPath) -and $shellObject.CreateShortcut($shortcutPath).TargetPath -ne $exePath) {
    throw 'App installed, but a different shortcut already uses this name; that shortcut was preserved.'
}
$shortcut = $shellObject.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installPath
$shortcut.Description = 'Accessible focus sessions and Google Sheets reflections'
$shortcut.IconLocation = $exePath + ',0'
$shortcut.Save()
[pscustomobject]@{ Installed = $exePath; PreviousApp = $(if ($hadPrevious) { $backupPath } else { $null }); EncryptedDataBackup = $dataBackup; Shortcut = $shortcutPath } | ConvertTo-Json
Write-Host 'Installed. Launch the usual shortcut. Your existing desktop settings and Sheets connection are retained. Microsoft Edge WebView2 Runtime is required.'
