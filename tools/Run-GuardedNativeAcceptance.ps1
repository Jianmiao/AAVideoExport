param(
    [Parameter(Mandatory=$true)][string]$LabRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Mode = 'character-voice',
    [string]$StoryPath,
    [string]$VoiceIdentifier = 'aavt/voice/SE_BatSwing_01',
    [ValidateSet(24,30,60)][int]$VoiceFps = 30,
    [ValidateRange(0.0,1.0)][Nullable[double]]$TestVoiceVolume,
    [string]$FixtureWorkspaceRoot,
    [switch]$DevelopmentAudit,
    [ValidateRange(30,1800)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeAcceptance.Guard.ps1')
$lab = (Resolve-Path -LiteralPath $LabRoot).Path
$marker = Join-Path $lab 'aave-test-lab.json'
if (!(Test-Path -LiteralPath $marker)) { throw 'An explicitly marked independent lab is required.' }
$definition = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
if ($definition.Source -and [IO.Path]::GetFullPath($definition.Source).TrimEnd('\') -eq $lab.TrimEnd('\')) {
    throw 'The lab cannot be the primary installation.'
}
if (Get-Process -Name AzureArchive -ErrorAction SilentlyContinue) { throw 'Close the existing AA instance before acceptance.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stateRoot = $output + '.state'
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $stateRoot)) { throw 'Use a fresh evidence directory.' }
New-Item -ItemType Directory -Path $stateRoot | Out-Null
$settingsRoot = Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive\data\settings'
$sfxConfigPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive\SfxFolderMod\config.txt'
$snapshot = Save-NativeAcceptanceState -SettingsRoot $settingsRoot -SfxConfigPath $sfxConfigPath `
    -RegistrySubKey 'Software\foxxlight\AzureArchive' -StateRoot $stateRoot
$savedEnvironment = @{}
$environment = @{ AA_VIDEOEXPORT_TEST_VOICE_IDENTIFIER = $VoiceIdentifier }
$exitCode = 1
try {
    if ($FixtureWorkspaceRoot -or $null -ne $TestVoiceVolume) {
        $settingsPath = Join-Path $settingsRoot 'user_settings.json'
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -Depth 64
        $originalVolume = $settings.voiceVolume
        $originalWorkspace = $settings.workspacePath
        if ($FixtureWorkspaceRoot) {
            $fixture = (Resolve-Path -LiteralPath $FixtureWorkspaceRoot).Path
            $artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts')).TrimEnd('\') + '\'
            if (!$fixture.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture workspaces must remain inside this source checkout artifacts directory.' }
            $fixtureMarker = Get-Content -LiteralPath (Join-Path $fixture 'aave-fixture-workspace.json') -Raw | ConvertFrom-Json
            if ($fixtureMarker.Owner -ne 'AAVE acceptance' -or [IO.Path]::GetFullPath($originalWorkspace).TrimEnd('\') -eq $fixture.TrimEnd('\')) { throw 'A separate marked acceptance workspace is required.' }
            if ((Get-Item -LiteralPath $fixture).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $fixture -Recurse -Attributes ReparsePoint).Count) { throw 'Fixture workspace must not link back to user data.' }
            $settings.workspacePath = $fixture
            $environment['AA_VIDEOEXPORT_FIXTURE_WORKSPACE'] = $fixture
        }
        if ($null -ne $TestVoiceVolume) {
            if ($Mode -notin @('character-voice','story','prepare-controlled-story','controlled-story-playback')) { throw 'A temporary voice volume requires an explicit native voice/story fixture.' }
            if (!$settings.PSObject.Properties['voiceVolume']) { throw 'The existing voiceVolume setting is missing.' }
            $settings.voiceVolume = [double]$TestVoiceVolume
        }
        [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 64))
        @{ Scope='Temporary native fixture input, restored from original bytes after the process exits';
            OriginalVoiceVolume=$originalVolume; TestVoiceVolume=$TestVoiceVolume;
            OriginalWorkspace=$originalWorkspace; FixtureWorkspace=$FixtureWorkspaceRoot } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stateRoot 'test-inputs.json') -Encoding utf8
    }
    foreach ($name in $environment.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Run-NativeAcceptance.ps1'),
        '-LabRoot', $lab, '-OutputDirectory', $output, '-Mode', $Mode, '-TimeoutSeconds', $TimeoutSeconds,
        '-VoiceIdentifier', $VoiceIdentifier, '-VoiceFps', $VoiceFps)
    if ($StoryPath) { $arguments += @('-StoryPath', $StoryPath) }
    if ($DevelopmentAudit) { $arguments += '-DevelopmentAudit' }
    & pwsh @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if (Get-Process -Name AzureArchive -ErrorAction SilentlyContinue) {
        throw "AA is still running; settings backups are retained at $stateRoot and were not overwritten while AA is live."
    }
    $restoration = Restore-NativeAcceptanceState -Snapshot $snapshot
    foreach($message in $restoration.EvidenceErrors){Write-Warning $message}
    if(!$restoration.Restored){throw ('Global state restoration failed: ' + ($restoration.RestoreErrors -join '; '))}
    Write-Output ('AA settings, Sfx preference and typed PlayerPrefs restored: ' + $restoration.OriginalFiles.Count + ' original files; evidence ' + $stateRoot)
    # A full output disk must not prevent restoration, but incomplete evidence
    # must not be reported as an entirely successful acceptance run either.
    if($restoration.EvidenceErrors.Count){$exitCode=1}
}
exit $exitCode
