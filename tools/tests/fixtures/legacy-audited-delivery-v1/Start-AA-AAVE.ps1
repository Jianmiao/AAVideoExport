[CmdletBinding()]
param(
    [string]$AAInstallPath = $PSScriptRoot,
    [string]$Version,
    [string]$DescriptorPath,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'

function Assert-AaveLaunchFile([string]$Path, [string]$Description) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $Description`: $Path" }
}
function Get-AaveLaunchHash([string]$Path) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash($stream)) }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Read-AaveLaunchJson([string]$Path, [string]$Description) {
    Assert-AaveLaunchFile $Path $Description
    try { return [IO.File]::ReadAllText($Path,[Text.UTF8Encoding]::new($false,$true)) | ConvertFrom-Json -Depth 30 }
    catch { throw "Invalid $Description JSON: $($_.Exception.Message)" }
}
function Test-AaveTestVariable([string]$Name) {
    return $Name -match '^AA_VIDEOEXPORT_(TEST_.*|NATIVE_TESTS|NATIVE_MEDIA|FIXTURE_WORKSPACE)$'
}
function Assert-AaveAssembly([string]$Path, [string]$Name, [string]$ExpectedVersion = '') {
    Assert-AaveLaunchFile $Path $Name
    try { $assembly = [Reflection.AssemblyName]::GetAssemblyName($Path) }
    catch { throw "Invalid assembly for $Name`: $($_.Exception.Message)" }
    if ($assembly.Name -cne $Name) { throw "Assembly identity mismatch for $Name`: $($assembly.Name)" }
    if ($ExpectedVersion -and $assembly.Version.ToString() -ne ($ExpectedVersion + '.0')) {
        throw "Assembly version mismatch for $Name`: expected $ExpectedVersion.0, found $($assembly.Version)"
    }
}

if (!$Version) {
    if ((Split-Path -Leaf $PSCommandPath) -notmatch '^Start-AA-AAVE-(\d+\.\d+\.\d+)\.ps1$') {
        throw 'A version is required. Pass -Version or use the installed versioned launcher.'
    }
    $Version = $Matches[1]
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The AAVE version must be an exact three-part version.' }
if (!(Test-Path -LiteralPath $AAInstallPath -PathType Container)) { throw 'The AA installation root does not exist.' }
$aaRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $AAInstallPath).Path).TrimEnd('\')
$executable = Join-Path $aaRoot 'AzureArchive.exe'
Assert-AaveLaunchFile $executable 'AA executable'
$exeStream = [IO.File]::OpenRead($executable)
try {
    if ($exeStream.Length -lt 64 -or $exeStream.ReadByte() -ne 0x4d -or $exeStream.ReadByte() -ne 0x5a) {
        throw 'The AA executable is not a real PE binary.'
    }
}
finally { $exeStream.Dispose() }

$activePath = Join-Path $aaRoot 'ActiveProfile.txt'
Assert-AaveLaunchFile $activePath 'active profile selection'
if ((Get-Item -LiteralPath $activePath).Length -gt 4096) { throw 'Active profile selection is unbounded.' }
$activeLines = [IO.File]::ReadAllLines($activePath,[Text.UTF8Encoding]::new($false,$true))
if ($activeLines.Length -ne 1) { throw 'Active profile selection must contain exactly one profile name.' }
$profile = $activeLines[0]
if ([string]::IsNullOrWhiteSpace($profile) -or $profile -cne $profile.Trim() -or $profile -in @('.','..') -or
    $profile.EndsWith('.') -or [IO.Path]::IsPathRooted($profile) -or $profile.Contains('/') -or $profile.Contains('\') -or
    $profile.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Active profile name is unsafe.' }
$profilePath = Join-Path $aaRoot ('profiles\' + $profile + '\modconfig.json')
$selection = Read-AaveLaunchJson $profilePath 'selected profile'
$entries = @($selection.EnabledMods | Where-Object { $_.name -eq 'AAVideoExport' })
if ($entries.Count -ne 1) { throw 'The selected profile must contain exactly one AAVideoExport entry.' }
if ($entries[0].name -cne 'AAVideoExport' -or $entries[0].version -cne $Version) {
    throw "The selected profile does not enable AAVideoExport $Version. Select that version in AA first."
}

$payload = Join-Path $aaRoot ('mods\AAVideoExport\' + $Version)
$manifest = Read-AaveLaunchJson (Join-Path $payload 'manifest.json') 'AAVE manifest'
if ($manifest.name -cne 'AAVideoExport' -or $manifest.version_number -cne $Version) { throw 'AAVE manifest name/version does not match the selected payload.' }
$plugin = Join-Path $payload 'AAVideoExport.dll'
$core = Join-Path $payload 'AAVideoExport.Core.dll'
$bootstrap = Join-Path $aaRoot 'BepInEx\patchers\AAVideoExport.RuntimeBootstrap.dll'
Assert-AaveAssembly $plugin 'AAVideoExport' $Version
Assert-AaveAssembly $core 'AAVideoExport.Core' $Version
Assert-AaveAssembly $bootstrap 'AAVideoExport.RuntimeBootstrap'

if (!$DescriptorPath) { $DescriptorPath = Join-Path $aaRoot ('AAVideoExport-install-' + $Version + '.json') }
$DescriptorPath = [IO.Path]::GetFullPath($DescriptorPath)
$descriptor = Read-AaveLaunchJson $DescriptorPath 'installation descriptor'
if ($descriptor.Version -cne $Version) { throw 'Installation descriptor version differs from this launcher.' }
if ([string]::IsNullOrWhiteSpace($descriptor.AAInstallPath) -or
    ![string]::Equals([IO.Path]::GetFullPath($descriptor.AAInstallPath).TrimEnd('\'),$aaRoot,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installation descriptor belongs to another AA installation root.'
}
foreach ($item in @(
    [pscustomobject]@{Name='Plugin';Path=$plugin;Expected=$descriptor.PluginSha256},
    [pscustomobject]@{Name='Core';Path=$core;Expected=$descriptor.CoreSha256},
    [pscustomobject]@{Name='Bootstrap';Path=$bootstrap;Expected=$descriptor.BootstrapSha256}
)) {
    if ($item.Expected -notmatch '^[0-9a-fA-F]{64}$' -or
        ![string]::Equals((Get-AaveLaunchHash $item.Path),$item.Expected,[StringComparison]::OrdinalIgnoreCase)) {
        throw ($item.Name + ' SHA-256 does not match the verified installation descriptor.')
    }
}

# Construct the same child configuration for review and execution. The parent,
# user and machine environments remain untouched; no acceptance option enters
# ordinary AA startup, even when the calling shell inherited test variables.
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executable
$start.WorkingDirectory = $aaRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $false
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
$removed = @($start.Environment.Keys | Where-Object { Test-AaveTestVariable $_ } | Sort-Object)
foreach ($name in $removed) { [void]$start.Environment.Remove($name) }
$start.Environment['AA_VIDEOEXPORT_RUNTIME_RECIPES'] = '1'
$start.Environment['AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION'] = '1'
$plan = [pscustomobject][ordered]@{
    Schema = 'aave.launch-plan.v1'
    AAInstallPath = $aaRoot
    Version = $Version
    ActiveProfile = $profile
    Executable = $start.FileName
    WorkingDirectory = $start.WorkingDirectory
    Arguments = @($start.ArgumentList)
    UseShellExecute = $start.UseShellExecute
    DescriptorPath = $DescriptorPath
    EnvironmentSet = [ordered]@{
        AA_VIDEOEXPORT_RUNTIME_RECIPES = $start.Environment['AA_VIDEOEXPORT_RUNTIME_RECIPES']
        AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION = $start.Environment['AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION']
    }
    EnvironmentRemoved = $removed
    ChildHasTestEnvironment = @($start.Environment.Keys | Where-Object { Test-AaveTestVariable $_ }).Count -ne 0
}
if ($PlanOnly) { return $plan }
if (Get-Process -Name AzureArchive -ErrorAction SilentlyContinue) { throw 'AA is already running. Close it before using this launcher.' }
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'AA did not start.' }
try {
    [pscustomobject]@{Started=$true;ProcessId=$process.Id;Version=$Version;ActiveProfile=$profile;AAInstallPath=$aaRoot}
}
finally { $process.Dispose() }
