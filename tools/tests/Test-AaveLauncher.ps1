[CmdletBinding()]
param([string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$launcher = Join-Path $repo 'tools\Start-AA-AAVE.ps1'
if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) {
    Write-Output 'FAIL normal launch plan is unavailable: no process-scoped launcher exists.'
    exit 1
}
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('aave-launch-plan-' + [guid]::NewGuid().ToString('N'))
$passed = 0
$failed = 0
$results = [Collections.Generic.List[object]]::new()
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; $script:results.Add([ordered]@{Name=$Name;Passed=$true}); Write-Output ('PASS ' + $Name) }
    catch { $script:failed++; $script:results.Add([ordered]@{Name=$Name;Passed=$false;Error=$_.Exception.Message}); Write-Output ('FAIL ' + $Name + ': ' + $_.Exception.Message) }
}
function Reject([scriptblock]$Action, [string]$Message) {
    try { $null = & $Action }
    catch { if ($_.Exception.Message -notmatch $Message) { throw ('Unexpected rejection: ' + $_.Exception.Message) }; return }
    throw 'Invalid launch was accepted.'
}
function Plan { & $launcher -AAInstallPath $fixtureRoot -Version $script:version -PlanOnly -DevelopmentAudit }
function Save-Json([string]$Path, [object]$Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false)) }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Manifest { [ordered]@{name='AAVideoExport';version_number=$script:version} }
function Profile { [ordered]@{EnabledMods=@([ordered]@{name='OtherMod';version='7.2.1'},[ordered]@{name='AAVideoExport';version=$script:version})} }
function Descriptor { [ordered]@{Schema='aave.install.v1';Version=$script:version;AAInstallPath=$fixtureRoot;BootstrapSha256=(Hash $script:bootstrap);PluginSha256=(Hash $script:plugin);CoreSha256=(Hash $script:core)} }
$environmentNames = @('AA_VIDEOEXPORT_RUNTIME_RECIPES','AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','AA_VIDEOEXPORT_TEST_QUIT','AA_VIDEOEXPORT_TEST_STORY','AA_VIDEOEXPORT_NATIVE_TESTS','AA_VIDEOEXPORT_NATIVE_MEDIA','AA_VIDEOEXPORT_FIXTURE_WORKSPACE')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $built = Join-Path $repo 'src\VideoExport.Plugin\bin\Release\net6.0'
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $built 'AAVideoExport.dll')).Version.ToString(3)
    $payload = Join-Path $fixtureRoot ('mods\AAVideoExport\' + $version)
    $patchers = Join-Path $fixtureRoot 'BepInEx\patchers'
    $profileRoot = Join-Path $fixtureRoot 'profiles\Default'
    New-Item -ItemType Directory -Path $payload,$patchers,$profileRoot -Force | Out-Null
    # A genuine local PE is copied only to inspect a launch plan. Never start it.
    Copy-Item -LiteralPath 'F:\AzureArchive_101\AzureArchive.exe' -Destination (Join-Path $fixtureRoot 'AzureArchive.exe')
    $plugin = Join-Path $payload 'AAVideoExport.dll'
    $core = Join-Path $payload 'AAVideoExport.Core.dll'
    $bootstrap = Join-Path $patchers 'AAVideoExport.RuntimeBootstrap.dll'
    Copy-Item -LiteralPath (Join-Path $built 'AAVideoExport.dll') -Destination $plugin
    Copy-Item -LiteralPath (Join-Path $built 'AAVideoExport.Core.dll') -Destination $core
    Copy-Item -LiteralPath (Join-Path $repo 'src\VideoExport.RuntimeBootstrap\bin\Release\net6.0\AAVideoExport.RuntimeBootstrap.dll') -Destination $bootstrap
    $manifestPath = Join-Path $payload 'manifest.json'
    $profilePath = Join-Path $profileRoot 'modconfig.json'
    $activePath = Join-Path $fixtureRoot 'ActiveProfile.txt'
    $descriptorPath = Join-Path $fixtureRoot ('AAVideoExport-install-' + $version + '.json')
    Save-Json $manifestPath (Manifest); Save-Json $profilePath (Profile); Save-Json $descriptorPath (Descriptor)
    [IO.File]::WriteAllText($activePath, 'Default')
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, 'test-inherited', 'Process') }
    [Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_RECIPES','0','Process')
    [Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','0','Process')

    Test 'normal launch plan uses the actual host and working directory with no test arguments' {
        $plan = Plan
        Check ($plan.Schema -eq 'aave.launch-plan.v1' -and $plan.Executable -eq (Join-Path $fixtureRoot 'AzureArchive.exe') -and $plan.WorkingDirectory -eq $fixtureRoot) 'Wrong executable or working directory.'
        Check (@($plan.Arguments).Count -eq 0 -and !$plan.UseShellExecute) 'Normal launch acquired command/test arguments or shell execution.'
        Check ($plan.ActiveProfile -eq 'Default' -and $plan.Version -eq $version) 'Selected profile/version was not verified.'
    }
    Test 'explicit legacy development launch sets observer flags on the child only' {
        $plan = Plan
        Check ($plan.EnvironmentSet.AA_VIDEOEXPORT_RUNTIME_RECIPES -eq '1' -and $plan.EnvironmentSet.AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION -eq '1') 'Required child observer flags are absent.'
        Check ([Environment]::GetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_RECIPES','Process') -eq '0' -and [Environment]::GetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','Process') -eq '0') 'Launcher changed caller environment.'
    }
    Test 'inherited test fixture media and automatic quit flags are absent from the child' {
        $plan = Plan
        Check (!$plan.ChildHasTestEnvironment) 'Child still has an acceptance/test variable.'
        foreach ($name in $environmentNames | Where-Object { $_ -notmatch 'RUNTIME_' }) {
            Check (@($plan.EnvironmentRemoved) -contains $name) ('Not removed from child: ' + $name)
            Check ([Environment]::GetEnvironmentVariable($name,'Process') -eq 'test-inherited') 'Test flag changed in caller process.'
        }
    }
    Test 'plan inspection does not write installation profile or settings files' {
        $before = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName + '=' + (Hash $_.FullName) })
        $null = Plan
        $after = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName + '=' + (Hash $_.FullName) })
        Check (($before -join "`n") -ceq ($after -join "`n")) 'Plan inspection mutated installed files.'
    }
    Test 'versioned installed launcher derives its own version and AA root' {
        $installedScript = Join-Path $fixtureRoot ('Start-AA-AAVE-' + $version + '.ps1')
        Copy-Item -LiteralPath $launcher -Destination $installedScript
        $plan = & $installedScript -PlanOnly -DevelopmentAudit
        Check ($plan.Version -eq $version -and $plan.AAInstallPath -eq $fixtureRoot) 'Installed defaults did not derive exact version/root.'
    }
    Test 'another selected AAVE version is refused without changing the profile' {
        $profile = Profile; $profile.EnabledMods[1].version = '99.0.0'; Save-Json $profilePath $profile
        try { Reject { Plan } 'selected|profile'; Check ((Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json).EnabledMods[1].version -eq '99.0.0') 'Launcher edited profile selection.' }
        finally { Save-Json $profilePath (Profile) }
    }
    Test 'duplicate AAVE profile entries are refused' {
        $profile = Profile; $profile.EnabledMods += [ordered]@{name='AAVideoExport';version=$version}; Save-Json $profilePath $profile
        try { Reject { Plan } 'exactly one|duplicate' } finally { Save-Json $profilePath (Profile) }
    }
    Test 'unsafe active profile names cannot escape the profiles directory' {
        [IO.File]::WriteAllText($activePath, '..\outside')
        try { Reject { Plan } 'profile' } finally { [IO.File]::WriteAllText($activePath, 'Default') }
    }
    Test 'manifest identity must match the requested version' {
        Save-Json $manifestPath ([ordered]@{name='AAVideoExport';version_number='99.0.0'})
        try { Reject { Plan } 'manifest|version' } finally { Save-Json $manifestPath (Manifest) }
    }
    Test 'missing installation descriptor is refused' {
        $text = [IO.File]::ReadAllText($descriptorPath); Remove-Item -LiteralPath $descriptorPath
        try { Reject { Plan } 'descriptor' } finally { [IO.File]::WriteAllText($descriptorPath,$text) }
    }
    Test 'descriptor belongs to this exact AA root' {
        $descriptor = Descriptor; $descriptor.AAInstallPath = (Join-Path $fixtureRoot 'wrong'); Save-Json $descriptorPath $descriptor
        try { Reject { Plan } 'root|installation' } finally { Save-Json $descriptorPath (Descriptor) }
    }
    Test 'descriptor version cannot select different verified payload' {
        $descriptor = Descriptor; $descriptor.Version = '99.0.0'; Save-Json $descriptorPath $descriptor
        try { Reject { Plan } 'version' } finally { Save-Json $descriptorPath (Descriptor) }
    }
    Test 'changed plugin bytes are rejected even when assembly name and MVID still match' {
        $bytes = [IO.File]::ReadAllBytes($plugin); [IO.File]::WriteAllBytes($plugin, ($bytes + [byte]0))
        try { Reject { Plan } 'hash|fingerprint|SHA' } finally { [IO.File]::WriteAllBytes($plugin,$bytes) }
    }
    Test 'bootstrap simple name must be the actual early observer assembly' {
        $bytes = [IO.File]::ReadAllBytes($bootstrap); Copy-Item -LiteralPath $core -Destination $bootstrap -Force; Save-Json $descriptorPath (Descriptor)
        try { Reject { Plan } 'assembly|bootstrap' }
        finally { [IO.File]::WriteAllBytes($bootstrap,$bytes); Save-Json $descriptorPath (Descriptor) }
    }
    Test 'missing own bootstrap is refused' {
        $bytes = [IO.File]::ReadAllBytes($bootstrap); Remove-Item -LiteralPath $bootstrap
        try { Reject { Plan } 'bootstrap' } finally { [IO.File]::WriteAllBytes($bootstrap,$bytes) }
    }
    Test 'invalid native executable is refused before launch planning' {
        $path = Join-Path $fixtureRoot 'AzureArchive.exe'; $bytes = [IO.File]::ReadAllBytes($path); [IO.File]::WriteAllText($path,'not a PE executable')
        try { Reject { Plan } 'executable|PE' } finally { [IO.File]::WriteAllBytes($path,$bytes) }
    }
    Test 'path-like versions cannot address files outside the installed payload' { Reject { & $launcher -AAInstallPath $fixtureRoot -Version '..\elsewhere' -PlanOnly } 'version' }
    Test 'explicit descriptor path is validated using the same identity and hashes' {
        $plan = & $launcher -AAInstallPath $fixtureRoot -Version $version -DescriptorPath $descriptorPath -PlanOnly -DevelopmentAudit
        Check ($plan.DescriptorPath -eq $descriptorPath) 'Explicit descriptor path was not used.'
    }
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $resolved).StartsWith('aave-launch-plan-')) { throw 'Refusing unverified launcher fixture cleanup.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output ("RESULT {0} passed; {1} failed; plan-only tests, no AA process started" -f $passed,$failed)
if ($failed) { exit 1 }
