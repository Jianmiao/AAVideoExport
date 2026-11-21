param(
    [Parameter(Mandatory=$true)][string]$LabRoot,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
[ValidateSet('settings','paths','buttons','story','prepare-controlled-story','official-controlled-story-authoring','controlled-story-playback','clocks','audio-clock','audio-hooks','audio-command','audio-semantics','character-voice','controlled-background','background-restore','multilingual-capture','aamultilingual-boundary','aaskipintro-boundary','menubgm-boundary','sfxfolder-boundary','aamenu-boundary','startup-controller','bootstrap-attribution','runtime-recipe-probe','bgm-lifecycle','bgm-inventory')][string]$Mode = 'paths',
    [string]$StoryPath,
    [string]$ExpectedRejection,
    [string]$VoiceIdentifier,
    [ValidateSet(24,30,60)][int]$VoiceFps = 30,
    [switch]$DevelopmentAudit,
    [switch]$PlanOnly,
    [ValidateRange(30,1800)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$lab = (Resolve-Path -LiteralPath $LabRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $lab 'aave-test-lab.json'))) { throw 'Use an explicitly prepared isolated AA test copy.' }
if (!$PlanOnly -and (Get-Process -Name AzureArchive -ErrorAction SilentlyContinue)) { throw 'An AA instance is already running; no second instance was started.' }
if($Mode-in@('bootstrap-attribution','runtime-recipe-probe')-and!$DevelopmentAudit){throw 'This observer test requires explicit -DevelopmentAudit and a matching development build.'}
$bootstrap=Join-Path $lab 'BepInEx\patchers\AAVideoExport.RuntimeBootstrap.dll'
if(!$DevelopmentAudit-and(Test-Path -LiteralPath $bootstrap)){throw 'Production native acceptance requires an isolated lab without an audit bootstrap; prepare that lab explicitly.'}
if($DevelopmentAudit-and!(Test-Path -LiteralPath $bootstrap)){throw 'Development audit native acceptance requires its explicit audit bootstrap.'}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new diagnostic directory for each run.' }
$labLog = Join-Path $lab 'BepInEx\LogOutput.log'
$hadPreviousLog = Test-Path -LiteralPath $labLog
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $lab 'AzureArchive.exe'
$start.WorkingDirectory = $lab
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
. (Join-Path $PSScriptRoot 'NativeAcceptance.Environment.ps1')
$auditEnvironment=Set-AaveNativeAuditEnvironment -StartInfo $start -DevelopmentAudit:$DevelopmentAudit
if ($Mode -ne 'paths') { $start.ArgumentList.Add('--aa-export-' + $Mode) }
$start.ArgumentList.Add('-logFile')
$start.ArgumentList.Add((Join-Path $output 'player.log'))
$start.Environment['AA_VIDEOEXPORT_NATIVE_TESTS'] = '1'
$start.Environment['AA_VIDEOEXPORT_TEST_OUTPUT'] = $output
$start.Environment['AA_VIDEOEXPORT_TEST_QUIT'] = '1'
$start.Environment['AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS'] = [Math]::Max(30, $TimeoutSeconds - 20).ToString([Globalization.CultureInfo]::InvariantCulture)
if ($StoryPath) { $start.Environment['AA_VIDEOEXPORT_TEST_STORY'] = (Resolve-Path -LiteralPath $StoryPath).Path }
if ($ExpectedRejection) { $start.Environment['AA_VIDEOEXPORT_EXPECT_REJECTION'] = $ExpectedRejection }
if ($VoiceIdentifier) { $start.Environment['AA_VIDEOEXPORT_TEST_VOICE_IDENTIFIER'] = $VoiceIdentifier }
if ($Mode -eq 'character-voice') { $start.Environment['AA_VIDEOEXPORT_TEST_VOICE_FPS'] = $VoiceFps.ToString([Globalization.CultureInfo]::InvariantCulture) }
if($PlanOnly){return [pscustomobject]@{Mode=$Mode;LabRoot=$lab;Executable=$start.FileName;WorkingDirectory=$start.WorkingDirectory;Arguments=@($start.ArgumentList);OutputDirectory=$output;DevelopmentAudit=$auditEnvironment.DevelopmentAudit;ChildAuditVariables=$auditEnvironment.ChildAuditVariables;RemovedAuditVariables=$auditEnvironment.RemovedAuditVariables;ExpectedBuildMode=$start.Environment['AA_VIDEOEXPORT_TEST_DEVELOPMENT_AUDIT'];BootstrapExpected=[bool]$DevelopmentAudit}}
New-Item -ItemType Directory -Path $output | Out-Null
if ($hadPreviousLog) {
    # Preserve prior evidence while ensuring a preloader crash cannot make an
    # untouched old log appear to belong to this new process.
    Move-Item -LiteralPath $labLog -Destination (Join-Path $output 'BepInEx.previous.log')
}
. (Join-Path $PSScriptRoot 'NativeAcceptance.Guard.ps1')
# The helper owns this exact process handle and cleans it in finally even when
# the wait path or injected post-start checks throw. It never kills by name.
$processResult = Invoke-NativeAcceptanceProcess -StartInfo $start -TimeoutSeconds $TimeoutSeconds `
    -AfterStart { param($OwnedProcess) Write-Host ('Started one AA test process: ' + $OwnedProcess.Id) }
[IO.File]::WriteAllText((Join-Path $output 'stdout.log'), $processResult.StdOut)
[IO.File]::WriteAllText((Join-Path $output 'stderr.log'), $processResult.StdErr)
$currentLogPresent = Test-Path -LiteralPath $labLog
$run = [ordered]@{ mode=$Mode; developmentAudit=[bool]$DevelopmentAudit; childAuditVariables=$auditEnvironment.ChildAuditVariables; removedAuditVariables=$auditEnvironment.RemovedAuditVariables; pid=$processResult.Id; exitCode=$processResult.ExitCode; timedOut=$processResult.TimedOut; lab=$lab;
    currentBepInExLogPresent=$currentLogPresent; previousBepInExLogArchived=$hadPreviousLog }
$run | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
if ($currentLogPresent) { Copy-Item -LiteralPath $labLog -Destination (Join-Path $output 'BepInEx.log') }
$results = @(Select-String -LiteralPath (Join-Path $output 'native-checks.log') -Pattern '^PATH-CHECK RESULT' -ErrorAction SilentlyContinue)
$results | ForEach-Object Line
Write-Output ('Evidence: ' + $output)
if ($processResult.TimedOut -or $processResult.ExitCode -ne 0 -or $results.Count -ne 1 -or $results[0].Line -notmatch '; 0 failed$') { exit 1 }
