#requires -Version 7.2
param([string]$EvidenceName='02-transaction-controls',[string[]]$Cases=@('normal','preparation-failure','unexpected-config','retained-diagnostic','missing-driver','missing-fixture','changed-hash','unknown-exit','nonzero','timeout','cancel-before-create','cancel-after-exit','cancel-unknown','changed-staged-file','changed-driver-marker','existing-story'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Adapter.ps1')
. (Join-Path $PSScriptRoot 'Adapter.Root.ps1')
. (Join-Path $PSScriptRoot '../ManagedDummy.ps1')
$evidence=Join-Path $PSScriptRoot ('Evidence\'+$EvidenceName)
Assert-CSR (!(Test-Path -LiteralPath $evidence)) 'Fresh new evidence required; never revisit restored fixtures.'
[void][IO.Directory]::CreateDirectory($evidence)
function Pin([string]$p){$p=[IO.Path]::GetFullPath($p);@{Path=$p;SHA256=(Get-CSRHash $p);Bytes=(Get-Item -LiteralPath $p).Length}}
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../VideoExport.ActualWaitActivityDriverR1'))
$observer=Pin ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../VideoExport.NormalDynamicTokenIdentityR2/Package/ActualWait10/AAVE.NormalDynamicTokenIdentity.R2.dll')))
$runtime=Pin 'C:\Program Files\dotnet\dotnet.exe'
Assert-CSR ((Get-CSRHash (Join-Path $script:AWRRoot 'ManagedDummy.ps1')) -ceq 'D04D813331BE180D9518A46541AE7E8F65BDD74C44DD01C1EE9FB890C9A45CEB' -and (Get-CSRHash (Join-Path $script:AWRRoot 'Dummy/bin/Release/net6.0/Dummy.dll')) -ceq '63F52E5CE2C120BDB9CA28C7BBC54EC4628233C1E840DD192C98BC7182193149') 'Frozen ordinary no-child fixture changed.'
$savedInputs=Get-AORPinnedActivitySources $source $true
Write-CSRJson (Join-Path $evidence 'SavedInputPins.json') @($observer,$savedInputs.Driver,$savedInputs.Handoff,$savedInputs.FixtureManifest)+$savedInputs.FixtureFiles
$runtimePins=@(foreach($p in @((Join-Path $script:AWRRoot 'Dummy/bin/Release/net6.0/Dummy.dll'),(Join-Path $script:AWRRoot 'Dummy/bin/Release/net6.0/Dummy.runtimeconfig.json'),(Join-Path $script:AWRRoot 'Dummy/bin/Release/net6.0/Dummy.deps.json'),'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36\System.Private.CoreLib.dll','C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36\coreclr.dll')){Pin $p})
$executionPins=@(Get-AORExecutionPins)+@(Pin $PSCommandPath)+@($runtime)+$runtimePins
foreach($i in 0..($executionPins.Count-1)){
 $pin=$executionPins[$i];$copy=Join-Path $evidence ('ExecutedSources\'+$i.ToString('D3')+'-'+[IO.Path]::GetFileName($pin.Path));$bytes=[IO.File]::ReadAllBytes($pin.Path)
 Assert-CSR ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ceq $pin.SHA256 -and $bytes.Length -eq $pin.Bytes) 'Executing source changed before evidence capture.'
 Write-CSRDurable $copy $bytes;$pin.CapturedPath=$copy
}
Write-CSRJson (Join-Path $evidence 'ExecutedSourcePins.json') $executionPins
$checks=[Collections.Generic.List[object]]::new();$plans=[Collections.Generic.List[object]]::new();$results=[Collections.Generic.List[object]]::new();$actualChildren=0
function Check([string]$Name,[bool]$Passed,$Detail=$null){$checks.Add(@{Name=$Name;Passed=$Passed;Detail=$Detail})}
function New-Fixture([string]$Name){
 $dir=Join-Path $evidence $Name;$lab=Join-Path $dir 'fixture';$id=[guid]::NewGuid().ToString('N')
 foreach($d in @('profiles','BepInEx\config','BepInEx\plugins','BepInEx\patchers','logs','mods\AAVideoExport\test-only','mods\Rukari.MoreEffects\test-only','mods\Rukari.CharacterVoice\test-only')){[void][IO.Directory]::CreateDirectory((Join-Path $lab $d))}
 foreach($f in @('ActiveProfile.txt','profiles\Default.json','BepInEx\config\BepInEx.cfg','logs\existing.log')){[IO.File]::WriteAllText((Join-Path $lab $f),'TEST ONLY ORIGINAL '+$f)}
 $mods=@(foreach($n in @('AAVideoExport','Rukari.MoreEffects','Rukari.CharacterVoice')){$p=Join-Path $lab ('mods\'+$n+'\test-only\'+$n+'.dll');[IO.File]::WriteAllText($p,'NOT AN ASSEMBLY. TEST ONLY '+$n);$pin=Pin $p;$pin.AssemblyName=$n;$pin})
 $key='Software\AAVE.ActualWaitRootHarnessR2-Fixture\'+$id;$r=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($key)
 try{$r.SetValue('ExactName','before',[Microsoft.Win32.RegistryValueKind]::String);$r.SetValue('Expand','%USERPROFILE%\not-expanded',[Microsoft.Win32.RegistryValueKind]::ExpandString);$r.SetValue('Binary',[byte[]]@(0,255,128),[Microsoft.Win32.RegistryValueKind]::Binary);$r.SetValue('Multi',[string[]]@('first','中文','last'),[Microsoft.Win32.RegistryValueKind]::MultiString);$r.SetValue('DWord',[int]::MinValue,[Microsoft.Win32.RegistryValueKind]::DWord);$r.SetValue('QWord',[long]::MaxValue,[Microsoft.Win32.RegistryValueKind]::QWord);$r.SetValue('',[byte[]]@(3,4),[Microsoft.Win32.RegistryValueKind]::None)}finally{$r.Dispose()}
 $stage=Join-Path $lab ('BepInEx\patchers\AAVE-ActivityOutputR5-'+$id)
 [pscustomobject]@{Schema='aave-activity-output-r5.managed-fixture-transaction.v1';Mode='ManagedFixture';RunId=$id;Lab=$lab;Output=(Join-Path $dir 'output');StateRoot=(Join-Path $dir 'state');RegistrySubKey=$key;StageRoot=$stage;OwnerMarkerPath=(Join-Path $stage 'owner.json');Observer=$observer;Activity=@{SourceRoot=$source;FixtureRoot=$lab;MaximumSeconds=10;PersistentStoryPath=(Join-Path $lab 'data\saves\AAVE-OfficialAuthoring-20261030.aas')};Executable=$runtime.Path;ExecutablePin=$runtime;ExistingMods=$mods;DiagnosticPolicy=@{Directories=@((Join-Path $lab 'logs'));MutableOriginalPaths=@((Join-Path $lab 'logs\existing.log'));MaximumBytes=1048576};AllowedOriginalReplacements=@();AllowedNewFiles=@();AllowedNewRegistryKeys=@();AllowedNewRegistryValues=@();ExpectedAbsentPaths=@();SelectedModPresencePolicySHA256=('A'*64);HarnessPath='';HarnessQuarantine='';DeclaredOutputDirectory='';ExpectedStageFiles=@{}}
}
function Copy-TestInput($Plan){
 $copy=Join-Path (Split-Path $Plan.Output -Parent) 'input-copy'
 foreach($relative in @('HANDOFF.json','Fixture.SourceManifest.json','Package\Driver\AAVE.ActualWait.ActivityDriver.R1.dll')){$p=Join-Path $copy $relative;[void][IO.Directory]::CreateDirectory((Split-Path $p -Parent));[IO.File]::Copy((Join-Path $source $relative),$p,$false)}
 $manifest=Get-Content -LiteralPath (Join-Path $source 'Fixture.SourceManifest.json') -Raw|ConvertFrom-Json
 foreach($f in $manifest.Files){$p=Join-Path $copy ('Package\Fixture\'+$f.RelativePath.Replace('/','\'));[void][IO.Directory]::CreateDirectory((Split-Path $p -Parent));[IO.File]::Copy((Join-Path $source ('Package\Fixture\'+$f.RelativePath.Replace('/','\'))),$p,$false);[IO.File]::SetLastWriteTimeUtc($p,[datetime]::new([long]$f.LastWriteUtcTicks,[DateTimeKind]::Utc))}
 $Plan.Activity.SourceRoot=$copy
}
try{foreach($case in $Cases){
 $plan=New-Fixture $case;$plans.Add($plan);$caseRoot=Split-Path $plan.Output -Parent
 if($case -cin @('missing-driver','missing-fixture','changed-hash')){
  Copy-TestInput $plan
  if($case -ceq 'missing-driver'){Remove-Item -LiteralPath (Join-Path $plan.Activity.SourceRoot 'Package\Driver\AAVE.ActualWait.ActivityDriver.R1.dll')}
  if($case -ceq 'missing-fixture'){Remove-Item -LiteralPath (Join-Path $plan.Activity.SourceRoot 'Package\Fixture\data\saves\AAVE-OfficialAuthoring-20261030.aas.lock')}
  if($case -ceq 'changed-hash'){$p=Join-Path $plan.Activity.SourceRoot 'Package\Fixture\controlled-story-recipe.json';$ticks=(Get-Item -LiteralPath $p).LastWriteTimeUtc.Ticks;$b=[IO.File]::ReadAllBytes($p);$b[$b.Length-1]=$b[$b.Length-1] -bxor 1;[IO.File]::WriteAllBytes($p,$b);[IO.File]::SetLastWriteTimeUtc($p,[datetime]::new($ticks,[DateTimeKind]::Utc))}
 }
 if($case -ceq 'existing-story'){[void][IO.Directory]::CreateDirectory((Split-Path $plan.Activity.PersistentStoryPath -Parent));[IO.File]::WriteAllText($plan.Activity.PersistentStoryPath,'TEST ONLY EXISTING ORIGINAL STORY MUST NOT OVERWRITE')}
 Write-CSRJson (Join-Path $caseRoot 'ExecutedSourcePins.json') $executionPins
 $seen=@{Staged=$false;Prepared=$false;Created=$false;Layout=$null}
 $result=Invoke-AORTransaction $plan {} {
  param($snapshot,$layout)
  $seen.Layout=$layout;Write-AORConfiguration $plan $layout $snapshot;Invoke-AORStage $plan $snapshot $layout;$seen.Staged=$true
  if($case -ceq 'unexpected-config'){[IO.File]::WriteAllText((Join-Path $plan.Lab 'BepInEx\config\unexpected.cfg'),'retain unknown');[IO.File]::WriteAllText((Join-Path $plan.Lab 'profiles\Default.json'),'unapproved original mutation')}
  if($case -ceq 'retained-diagnostic'){[IO.File]::WriteAllText((Join-Path $plan.Lab 'logs\new.log'),'retain diagnostic');[IO.File]::WriteAllText((Join-Path $plan.Lab 'logs\existing.log'),'original log activity')}
  if($case -ceq 'changed-staged-file'){[IO.File]::AppendAllText($plan.Activity.PersistentStoryPath,'changed staged bytes preserve')}
  if($case -ceq 'changed-driver-marker'){$marker=@($layout.Groups|Where-Object Role -ceq 'Driver')[0].Marker;[IO.File]::AppendAllText($marker,'changed ownership preserve')}
 }{
  $seen.Prepared=$true
  if($case -ceq 'preparation-failure'){throw 'Injected deterministic prepare failure before child creation.'}
  if($case -ceq 'cancel-before-create'){throw [OperationCanceledException]::new('Injected cancellation at pre-create boundary.')}
  [pscustomobject]@{ManagedOnly=$true}
 }{
  param($state,$prepared)
  Assert-CSR $prepared.ManagedOnly 'Fixture preparation not delivered.'
  if($case -ceq 'unknown-exit'){throw 'Opaque creation-capable callback error; parent must defer.'}
  if($case -ceq 'cancel-unknown'){throw [OperationCanceledException]::new('Injected opaque in-create cancellation; do not infer zero child.')}
  $seen.Created=$true
  $p=if($case -ceq 'nonzero'){Invoke-AWRManagedDummy $plan 100 17}elseif($case -ceq 'timeout'){Invoke-AWRManagedDummy $plan 5000 0 300}else{Invoke-AWRManagedDummy $plan 100 0}
  $state.Process=$p;$state.OwnedTreeExited=$p.AllOwnedProcessesExited
  if($case -ceq 'cancel-after-exit'){throw [OperationCanceledException]::new('Injected cancellation after actual retained parent exit proof.')}
  Assert-CSR ($p.Started -and $p.AllOwnedProcessesExited -and $p.ExitCode -eq 0 -and !$p.TimedOut -and !$p.ForcedTermination -and !$p.Failure) 'Owned ordinary child failed.'
 }
 $results.Add(@{Case=$case;Result=$result});Write-CSRJson (Join-Path $caseRoot 'OriginalResult.json') $result
 if($result.Process -and $result.Process.Started){$actualChildren++;$self=Get-Content -LiteralPath (Join-Path $plan.Output 'dummy-self.json') -Raw|ConvertFrom-Json;Check ($case+': actual ordinary runtime and retained identity') ($self.Runtime -ceq '6.0.36' -and $self.Pid -eq $result.Process.ProcessId -and $result.Process.CreationUtcTicks -eq $self.CreationUtcTicks -and $result.Process.HandleRetained -and $result.Process.HandleClosed -and $result.Process.AllOwnedProcessesExited -and !$result.Process.StillLive)}
 if($case -cin @('unknown-exit','cancel-unknown')){
  Check ($case+': opaque boundary defers restoration') (!$result.Passed -and $result.CreationHelperInvoked -and $null -eq $result.Started -and $null -eq $result.Restoration -and !$seen.Created)
  # This test's literal callback contains no process call on these branches.
  # Its known zero-child cleanup is test-only, never inferred by production.
  $snap=Import-Clixml -LiteralPath (Join-Path $plan.StateRoot 'original-state.clixml');$layout=Import-Clixml -LiteralPath (Join-Path $plan.StateRoot 'addition-layout.clixml')
  $script:AORMetadataTrace=@{Records=[Collections.Generic.List[object]]::new();Dropped=0}
  $cleanup=Restore-AORState $plan $snap $layout $true
  Write-CSRDurable (Join-Path $caseRoot 'TestOnlyCleanupMetadata.clixml') ([Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize($script:AORMetadataTrace,70)));$script:AORMetadataTrace=$null
  Write-CSRJson (Join-Path $caseRoot 'KnownZeroChildTestCleanup.json') $cleanup
  Check ($case+': explicitly test-only no-child cleanup') $cleanup.OriginalStateRestored
  continue
 }
 Check ($case+': original content registry metadata restored') ([bool]$result.Restoration.OriginalStateRestored) $result.Failures
 Check ($case+': final reads precede metadata') ($result.Restoration.VerificationOrder -match 'all-content-reads-first' -and !$result.Restoration.LiveContentsReadAfterFinalMetadata)
 Check ($case+': expected acceptance') $(if($case -cin @('normal','retained-diagnostic')){[bool]$result.Passed}else{!$result.Passed}) $result.Failures
 if($case -cin @('preparation-failure','cancel-before-create','missing-driver','missing-fixture','changed-hash','existing-story')){Check ($case+': known zero-child') (!$result.CreationHelperInvoked -and !$result.LaunchAttempted -and $result.Started -ceq $false)}
 if($seen.Staged){
  $staged=Get-Content -LiteralPath (Join-Path $plan.Output 'staging-readback.json') -Raw|ConvertFrom-Json
  Check ($case+': exact 28 additions and 23 scene records') ($staged.FileCount -eq 28 -and @($staged.Files|Where-Object {$_.Role -ceq 'Fixture' -and $null -ne $_.ExpectedLastWriteUtcTicks}).Count -eq 23 -and @($staged.Files|Where-Object Bytes -eq 0).Count -eq 2)
  $config=Get-Content -LiteralPath (Join-Path $plan.Output 'root-config.json') -Raw|ConvertFrom-Json;$doc=Get-Content -LiteralPath (Join-Path $plan.Output 'activity-driver-plan.json') -Raw|ConvertFrom-Json
  Check ($case+': test-only schema and shared run') ($doc.Schema -ceq 'aave-activity-output-r5.managed-fixture-plan.v1' -and $doc.NativeLaunchAuthorized -ceq $false -and $config.Schema -ceq 'aave-activity-output-r5.managed-fixture-observer.v1' -and $doc.RunId -ceq $config.RunId -and !$config.OwnerEnabled)
 }
 if($case -ceq 'unexpected-config'){Check 'Unexpected cfg retained without skipping known restoration' ($result.Restoration.RejectedOutputs.Count -eq 1 -and !$result.Restoration.ExactWholeInventoryRestored -and $result.Restoration.Cleanup.Errors.Count -eq 0)}
 if($case -ceq 'retained-diagnostic'){Check 'Declared new log retained and original log restored' ($result.Restoration.RetainedOutputs.Count -eq 1 -and $result.Restoration.InventoryMatchesDeclaredOutcome -and !$result.Restoration.ExactWholeInventoryRestored -and $result.Restoration.OriginalBytesMatch)}
 if($case -ceq 'timeout'){Check 'Timeout remains failure after proven forced exit' ($result.Process.TimedOut -and $result.Process.ForcedTermination -and $result.Process.AllOwnedProcessesExited -and !$result.Passed)}
 if($case -ceq 'nonzero'){Check 'Nonzero retained exactly' ($result.Process.ExitCode -eq 17 -and !$result.Process.TimedOut -and !$result.Passed)}
 if($case -cin @('changed-staged-file','changed-driver-marker')){Check ($case+': changed ownership/content preserved') ($result.Restoration.Cleanup.Errors.Count -gt 0 -and $result.Restoration.RejectedOutputs.Count -gt 0 -and !$result.Restoration.ExactWholeInventoryRestored)}
 $trace=Import-Clixml -LiteralPath (Join-Path $plan.StateRoot 'metadata-comparison-inputs.clixml')
 Check ($case+': exact expected and actual comparison diagnostics') ($trace.Records.Count -gt 0 -and $trace.Dropped -eq 0 -and @($trace.Records|Where-Object {$null -eq $_.Expected.AccessUtcTicks -or $null -eq $_.Actual.AccessUtcTicks}).Count -eq 0)
}}
catch{Check 'Control execution exception' $false $_.Exception.ToString()}
finally{foreach($plan in $plans){Assert-AWRFixtureRegistry $plan.RegistrySubKey;[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($plan.RegistrySubKey,$false)}}
$report=@{Schema='aave-activity-output-r5.managed-controls.v1';Checks=$checks.ToArray();Failed=@($checks|Where-Object {!$_.Passed}).Count;CasesExecuted=$results.Count;ActualManagedChildren=$actualChildren;ActualManagedRuntime='6.0.36';AAExecuted=$false;NativeHelperExecuted=$false;RealAARegistryAccessed=$false;OldMetadataFailureFixed=$false;ActualRootPlanAuthored=$false;ActualRootDecisionAuthored=$false;ActualAALaunchMarkerAuthored=$false;FixtureRegistryRemoved=$true}
Write-CSRJson (Join-Path $evidence 'Result.json') $report
$report|ConvertTo-Json -Depth 9
if($report.Failed){exit 1}
