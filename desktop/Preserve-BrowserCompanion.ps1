function Copy-ConfiguredBrowserCompanion {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory,
          [Parameter(Mandatory = $true)][string]$StagingDirectory)

    $installedRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $stagedRoot = [IO.Path]::GetFullPath($StagingDirectory).TrimEnd('\')
    if ($installedRoot -eq $stagedRoot) { throw 'Companion preservation requires a separate staging directory.' }
    $relativeManifest = 'browser-companion\native-host.json'
    $existingPath = Join-Path $installedRoot $relativeManifest
    $destination = Join-Path $stagedRoot $relativeManifest
    if (Test-Path -LiteralPath $destination) { throw 'An app package must not contain configured browser companion data.' }
    if (-not (Test-Path -LiteralPath $existingPath -PathType Leaf)) { return $false }

    $expectedExecutable = Join-Path $installedRoot 'ReflectionTimer.exe'
    $expectedOrigin = 'chrome-extension://mgalafjodgnoeponalohbmdopkkbnfok/'
    try {
        $existing = Get-Content -LiteralPath $existingPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $valid = $existing.name -is [string] -and $existing.name -ceq 'com.reflectiontimer.focus' -and
            $existing.type -is [string] -and $existing.type -ceq 'stdio' -and $existing.path -is [string] -and
            [IO.Path]::IsPathRooted($existing.path) -and
            [string]::Equals([IO.Path]::GetFullPath($existing.path), $expectedExecutable, [StringComparison]::OrdinalIgnoreCase) -and
            $existing.allowed_origins -is [array] -and $existing.allowed_origins.Count -eq 1 -and
            $existing.allowed_origins[0] -ceq $expectedOrigin
    }
    catch { $valid = $false }
    $companionFolder = Join-Path $stagedRoot 'browser-companion'
    if (-not $valid -or -not (Test-Path -LiteralPath (Join-Path $companionFolder 'manifest.json') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $companionFolder 'background.js') -PathType Leaf)) {
        Write-Warning 'Browser companion configuration was not carried forward. Its original file remains in the previous app backup. Use Configure companion after installation to repair the connection.'
        return $false
    }

    # Keep the registered final executable path, never the temporary staging path.
    # Regenerate only known fields; unrelated old manifest fields are not trusted.
    $manifest = [ordered]@{
        name = 'com.reflectiontimer.focus'
        description = 'Reflection Timer optional browser focus metadata companion'
        path = $expectedExecutable
        type = 'stdio'
        allowed_origins = @($expectedOrigin)
    } | ConvertTo-Json
    [IO.File]::WriteAllText($destination, $manifest, (New-Object Text.UTF8Encoding($false)))
    return $true
}
