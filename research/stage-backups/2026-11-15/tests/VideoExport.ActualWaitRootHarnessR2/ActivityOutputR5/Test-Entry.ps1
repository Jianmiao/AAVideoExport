#requires -Version 7.2
param([string]$EvidenceName='04-entry-controls')
$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot ('Evidence\'+$EvidenceName)
if(Test-Path -LiteralPath $out){throw 'Fresh evidence required.'}
[void][IO.Directory]::CreateDirectory($out)
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed,$Detail=$null){$checks.Add(@{Name=$Name;Passed=$Passed;Detail=$Detail})}
function Reject([string]$Name,[scriptblock]$Work,[string]$Expected){$message=$null;try{& $Work}catch{$message=$_.Exception.Message};Check $Name ($message -like $Expected) $message}
# Separate ordinary shell, no adapter import in the default entry process.
$json=& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-RootProbe.ps1');$code=$LASTEXITCODE;$value=$json|ConvertFrom-Json
Check 'Default entry is no work before import or IO' ($code -eq 0 -and $value.Mode -ceq 'NO_WORK' -and !$value.DocumentsRead -and !$value.HostInventoryRead -and !$value.Staged -and !$value.CreationHelperInvoked -and !$value.Started)
. (Join-Path $PSScriptRoot 'Adapter.ps1')
. (Join-Path $PSScriptRoot 'Adapter.Root.ps1')
$id=[guid]::NewGuid().ToString('N');$lab=Join-Path $out 'never-created-host';$stage=Join-Path $lab ('BepInEx\patchers\AAVE-ActivityOutputR5-'+$id)
$p=@{Schema='aave-activity-output-r5.managed-fixture-transaction.v1';Mode='ManagedFixture';RunId=$id;Lab=$lab;Output=(Join-Path $out 'never-created-output');StateRoot=(Join-Path $out 'never-created-state');RegistrySubKey=('Software\AAVE.ActualWaitRootHarnessR2-Fixture\'+$id);StageRoot=$stage;OwnerMarkerPath=(Join-Path $stage 'owner.json');Activity=@{FixtureRoot=$lab;PersistentStoryPath=(Join-Path $lab 'data\saves\AAVE-OfficialAuthoring-20261030.aas');MaximumSeconds=10};Observer=@{SHA256='44E7607911CA0482EA8431743EB603BDC5E3D133E71CEA475DD0D8B07108FC19'};Executable='C:\Program Files\dotnet\dotnet.exe';ExecutablePin=@{Path='C:\Program Files\dotnet\dotnet.exe'};AllowedOriginalReplacements=@();AllowedNewFiles=@();AllowedNewRegistryKeys=@();AllowedNewRegistryValues=@();ExpectedAbsentPaths=@();DiagnosticPolicy=@{Directories=@((Join-Path $lab 'logs'));MutableOriginalPaths=@();MaximumBytes=4096}}
$none=$null;try{Assert-AORPlan $p}catch{$none=$_.Exception.Message};Check 'Valid test-only lexical shape admits without reading host' ($null -eq $none) $none
Reject 'Root entry rejects fixture schema' {Assert-AORRootBindings $p @{}} '*external live transaction plan required*'
$p.Activity.FixtureRoot=Join-Path $out 'outside-host';Reject 'Fixture root outside restoration coverage rejected' {Assert-AORPlan $p} '*inside the full restoration root*';$p.Activity.FixtureRoot=$lab
$saved=$p.Activity.PersistentStoryPath;$p.Activity.PersistentStoryPath=Join-Path $lab 'other.aas';Reject 'Persistent story mismatch rejected' {Assert-AORPlan $p} '*persistent story target differs*';$p.Activity.PersistentStoryPath=$saved
$key=$p.RegistrySubKey;$p.RegistrySubKey='Software\foxxlight\AzureArchive';Reject 'Fixture cannot enter actual AA registry' {Assert-AORPlan $p} '*Isolated nonce registry required*';$p.RegistrySubKey=$key
$p.Mode='RootLive';$p.Schema='aave-activity-output-r5.root-plan.v1';Reject 'Live transaction has no fabricated bound context' {Assert-AORPlan $p} '*externally reviewed Root context required*'
Reject 'Outer documents inside host refused before document read' {Assert-DOROuterInputPaths $lab (Join-Path $lab 'plan.json') (Join-Path $out 'decision-not-created.json')} '*outside the explicit host boundary*'
Reject 'Conflicting inherited instrumentation is not silently removed' {New-DOREnvironment @{CORECLR_ENABLE_PROFILING='1'} (Join-Path $out 'config-not-created.json')} '*Conflicting inherited instrumentation*'
Check 'No native helper type loaded' (!('PrivateDesktopProcess.Runner' -as [type]))
$pins=@(Get-AORExecutionPins)+@(@{Path=$PSCommandPath;SHA256=(Get-CSRHash $PSCommandPath);Bytes=(Get-Item -LiteralPath $PSCommandPath).Length})
Write-CSRJson (Join-Path $out 'ExecutedSourcePins.json') $pins
$report=@{Schema='aave-activity-output-r5.entry-controls.v1';Checks=$checks.ToArray();Failed=@($checks|Where-Object {!$_.Passed}).Count;HostInventoryRead=$false;RegistryRead=$false;ActualRootDocumentsWritten=$false;NativeHelperExecuted=$false;AAExecuted=$false}
Write-CSRJson (Join-Path $out 'Result.json') $report;$report|ConvertTo-Json -Depth 6;if($report.Failed){exit 1}
