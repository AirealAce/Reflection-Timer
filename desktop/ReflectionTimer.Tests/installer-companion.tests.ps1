$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Preserve-BrowserCompanion.ps1')
$checks = 0
function Assert-CompanionCheck([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $script:checks++
}
function Get-TestHash([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToBase64String($algorithm.ComputeHash([IO.File]::ReadAllBytes($Path))) }
    finally { $algorithm.Dispose() }
}
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $temporaryRoot ('ReflectionTimer-Companion-Installer-Test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    function New-CompanionFixture([string]$Name) {
        $old = Join-Path $testRoot ($Name + '\installed')
        $stage = Join-Path $testRoot ($Name + '\staged')
        foreach ($folder in @($old, $stage)) {
            New-Item -ItemType Directory -Path (Join-Path $folder 'browser-companion') -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $folder 'ReflectionTimer.exe'), 'synthetic executable')
        }
        [IO.File]::WriteAllText((Join-Path $stage 'browser-companion\manifest.json'), '{}')
        [IO.File]::WriteAllText((Join-Path $stage 'browser-companion\background.js'), '// synthetic companion')
        return [pscustomobject]@{
            Installed = $old; Staged = $stage
            Source = Join-Path $old 'browser-companion\native-host.json'
            Destination = Join-Path $stage 'browser-companion\native-host.json'
            Manifest = [ordered]@{
                name = 'com.reflectiontimer.focus'; description = 'old description'
                path = Join-Path $old 'ReflectionTimer.exe'; type = 'stdio'
                allowed_origins = @('chrome-extension://mgalafjodgnoeponalohbmdopkkbnfok/')
                unrelated = 'do not preserve arbitrary fields'
            }
        }
    }
    function Write-FixtureManifest($Fixture) {
        [IO.File]::WriteAllText($Fixture.Source, ($Fixture.Manifest | ConvertTo-Json))
    }
    $fresh = New-CompanionFixture 'fresh'
    Assert-CompanionCheck (-not (Copy-ConfiguredBrowserCompanion $fresh.Installed $fresh.Staged)) 'Fresh installation must not configure a companion.'
    Assert-CompanionCheck (-not (Test-Path -LiteralPath $fresh.Destination)) 'Fresh installation must not create a native host manifest.'

    $valid = New-CompanionFixture 'valid'
    Write-FixtureManifest $valid
    $before = Get-TestHash $valid.Source
    Assert-CompanionCheck (Copy-ConfiguredBrowserCompanion $valid.Installed $valid.Staged) 'A valid existing connection must survive upgrade.'
    $restored = Get-Content -LiteralPath $valid.Destination -Raw | ConvertFrom-Json
    Assert-CompanionCheck ($restored.path -eq (Join-Path $valid.Installed 'ReflectionTimer.exe')) 'Preserved host must point to the final installed executable, not staging.'
    Assert-CompanionCheck ($restored.allowed_origins.Count -eq 1 -and $restored.allowed_origins[0] -ceq $valid.Manifest.allowed_origins[0]) 'Preserved host must retain only the fixed companion origin.'
    Assert-CompanionCheck (-not ($restored.PSObject.Properties.Name -contains 'unrelated')) 'Unknown manifest fields must not be carried forward.'
    Assert-CompanionCheck ((Get-TestHash $valid.Source) -eq $before) 'The original configured manifest must remain untouched for rollback.'
    $bytes = [IO.File]::ReadAllBytes($valid.Destination)
    Assert-CompanionCheck (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'The preserved native messaging manifest must be UTF-8 without a BOM.'

    $mutations = @{
        WrongHost = { param($m) $m.name = 'other.host' }
        ArrayHost = { param($m) $m.name = @('other.host', 'com.reflectiontimer.focus') }
        WrongTransport = { param($m) $m.type = 'socket' }
        ArrayTransport = { param($m) $m.type = @('socket', 'stdio') }
        RelativeExecutable = { param($m) $m.path = 'ReflectionTimer.exe' }
        OtherExecutable = { param($m) $m.path = Join-Path $testRoot 'another-app\ReflectionTimer.exe' }
        StagingExecutable = { param($m, $f) $m.path = Join-Path $f.Staged 'ReflectionTimer.exe' }
        WrongOrigin = { param($m) $m.allowed_origins = @('chrome-extension://other/') }
        ExtraOrigin = { param($m) $m.allowed_origins += 'chrome-extension://other/' }
        ScalarOrigin = { param($m) $m.allowed_origins = $m.allowed_origins[0] }
        MissingOrigin = { param($m) $m.Remove('allowed_origins') }
    }
    foreach ($name in $mutations.Keys) {
        $fixture = New-CompanionFixture $name
        & $mutations[$name] $fixture.Manifest $fixture
        Write-FixtureManifest $fixture
        $before = Get-TestHash $fixture.Source
        $warnings = @()
        $preserved = Copy-ConfiguredBrowserCompanion $fixture.Installed $fixture.Staged -WarningVariable warnings -WarningAction SilentlyContinue
        Assert-CompanionCheck (-not $preserved -and -not (Test-Path -LiteralPath $fixture.Destination)) "$name must not configure a companion."
        Assert-CompanionCheck ($warnings.Count -eq 1) "$name must provide repair guidance."
        Assert-CompanionCheck ((Get-TestHash $fixture.Source) -eq $before) "$name must retain the original in the previous-install directory."
    }
    $malformed = New-CompanionFixture 'malformed'
    [IO.File]::WriteAllText($malformed.Source, '{invalid')
    Assert-CompanionCheck (-not (Copy-ConfiguredBrowserCompanion $malformed.Installed $malformed.Staged -WarningAction SilentlyContinue)) 'Malformed JSON must fail closed.'
    $incomplete = New-CompanionFixture 'incomplete'
    Write-FixtureManifest $incomplete
    Remove-Item -LiteralPath (Join-Path $incomplete.Staged 'browser-companion\background.js')
    Assert-CompanionCheck (-not (Copy-ConfiguredBrowserCompanion $incomplete.Installed $incomplete.Staged -WarningAction SilentlyContinue)) 'An incomplete replacement package must not keep a misleading connection configuration.'
    Assert-CompanionCheck (-not (Test-Path -LiteralPath $incomplete.Destination)) 'Incomplete packages must leave their native host unconfigured.'
    $packaged = New-CompanionFixture 'packaged'
    [IO.File]::WriteAllText($packaged.Destination, '{}')
    $rejected = $false
    try { Copy-ConfiguredBrowserCompanion $packaged.Installed $packaged.Staged | Out-Null } catch { $rejected = $true }
    Assert-CompanionCheck $rejected 'A preconfigured package must be rejected, even for a fresh installation.'
    $rejected = $false
    try { Copy-ConfiguredBrowserCompanion $valid.Installed $valid.Installed | Out-Null } catch { $rejected = $true }
    Assert-CompanionCheck $rejected 'Preservation must never overwrite the previous installation.'
    Write-Output "$checks browser companion installer checks passed. No user installation, registry or browser profile was accessed."
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot -ne $testRoot -or -not $resolvedTestRoot.StartsWith($temporaryRoot + '\ReflectionTimer-Companion-Installer-Test-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe synthetic test cleanup path.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
