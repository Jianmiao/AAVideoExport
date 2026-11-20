param([Parameter(Mandatory=$true)][string]$EvidenceName)
$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot ('Evidence\'+$EvidenceName)
if(Test-Path -LiteralPath $out){throw 'Fresh test evidence required.'}
[void][IO.Directory]::CreateDirectory($out)
$checks=[Collections.Generic.List[object]]::new()
function Check($n,$passed,$detail=$null){$checks.Add([pscustomobject]@{Name=$n;Passed=[bool]$passed;Detail=$detail})}
$module=Join-Path $PSScriptRoot 'ActivityBinding.ps1'
Check 'Binding module exists' (Test-Path -LiteralPath $module)
if(Test-Path -LiteralPath $module){
 try{
  Copy-Item -LiteralPath $module -Destination (Join-Path $out 'ActivityBinding.executed.ps1')
  . $module
  $fixed='E:\aamod\AAVE-transparent-runtime-work-20261031\tests\VideoExport.ActualWaitActivityDriverR1'
  $bundle=Get-AORPinnedActivitySources $fixed $false
  Check 'Fixed driver and exact 23-file source close' ($bundle.Driver.Bytes -eq 115712 -and $bundle.FixtureFiles.Count -eq 23 -and $bundle.Manifest.ExpectedRows -eq 8)
  Check 'Both empty lock files remain valid pins' (@($bundle.FixtureFiles|Where-Object Bytes -eq 0).Count -eq 2)
  $target='E:\aamod\AAVE-transparent-runtime-work-20261031\tests\VideoExport.ActualWaitRootHarnessR2\ActivityOutputR5\ActivityBinding\never-created-target'
  $mapped=@(Get-AORFixtureDestinations $target $bundle)
  Check 'Pure map preserves exact records and creates nothing' ($mapped.Count -eq 23 -and @($mapped|Where-Object {$_.Role -cne 'Fixture' -or !$_.Path.StartsWith($target+'\',[StringComparison]::Ordinal)}).Count -eq 0 -and !(Test-Path -LiteralPath $target))
  $changed=$bundle|ConvertTo-Json -Depth 30|ConvertFrom-Json
  $changed.FixtureFiles[0].RelativePath='../escape'
  $reject=$false;try{Get-AORFixtureDestinations $target $changed|Out-Null}catch{$reject=$true};Check 'Escaping fixture relative path rejects' $reject
  $changed=$bundle|ConvertTo-Json -Depth 30|ConvertFrom-Json
  $changed.FixtureFiles[1].RelativePath=$changed.FixtureFiles[0].RelativePath
  $reject=$false;try{Get-AORFixtureDestinations $target $changed|Out-Null}catch{$reject=$true};Check 'Duplicate fixture destination rejects' $reject
  $changed=$bundle|ConvertTo-Json -Depth 30|ConvertFrom-Json
  $changed.FixtureFiles[0].LastWriteUtcTicks=[double]$changed.FixtureFiles[0].LastWriteUtcTicks
  $reject=$false;try{Get-AORFixtureDestinations $target $changed|Out-Null}catch{$reject=$true};Check 'Floating timestamp cannot round into a declared Int64 tick' $reject
  $changed=$bundle|ConvertTo-Json -Depth 30|ConvertFrom-Json
  $changed.FixtureFiles[0].RelativePath='Aave-fixture-workspace.json';$changed.FixtureFiles[0].Path=Join-Path (Join-Path $fixed 'Package\Fixture') 'Aave-fixture-workspace.json'
  $reject=$false;try{Get-AORFixtureDestinations $target $changed|Out-Null}catch{$reject=$true};Check 'Case-altered relative path is not the exact manifest path' $reject
  $reject=$false;try{Get-AORPinnedActivitySources (Join-Path $fixed 'Package') $false|Out-Null}catch{$reject=$true};Check 'Package child is not accepted as SourceRoot' $reject
  $reject=$false;try{Get-AORPinnedActivitySources (Join-Path $PSScriptRoot 'input-copy') $true|Out-Null}catch{$reject=$true};Check 'Managed source copy outside parent Evidence rejects' $reject
  $reject=$false;try{Read-AORActivityFilePin (Join-Path $PSScriptRoot 'not-a-source.txt') ('0'*64) 0|Out-Null}catch{$reject=$_.Exception.Message -match 'limited to saved activity source'};Check 'Low-level pin reader rejects other locations before file access' $reject
  $zero=@($bundle.FixtureFiles|Where-Object Bytes -eq 0)[0]
  $reject=$false;try{Read-AORActivityFilePin $zero.Path ('0'*64) 0 $zero.LastWriteUtcTicks|Out-Null}catch{$reject=$true};Check 'Read-only hash mismatch rejects unchanged saved empty file' $reject
  $reject=$false;try{Read-AORActivityFilePin $zero.Path $zero.SHA256 1 $zero.LastWriteUtcTicks|Out-Null}catch{$reject=$true};Check 'Read-only byte-count mismatch rejects' $reject
  $reject=$false;try{Read-AORActivityFilePin $zero.Path $zero.SHA256 0 ($zero.LastWriteUtcTicks+1)|Out-Null}catch{$reject=$true};Check 'Read-only timestamp mismatch rejects' $reject
  $fakeExe=[pscustomobject]@{Path=(Join-Path $target 'AzureArchive.exe');SHA256=('A'*64);Bytes=123L}
  $mods=@(foreach($name in @('AAVideoExport','Rukari.MoreEffects','Rukari.CharacterVoice','Optional.Existing')){[pscustomobject]@{AssemblyName=$name;Path=(Join-Path $target ($name+'.dll'));SHA256=('B'*64);Bytes=456L}})
  $plan=[pscustomobject]@{Mode='ManagedFixture';RunId=('a'*32);Activity=[pscustomobject]@{SourceRoot=$fixed;FixtureRoot=(Join-Path $target 'fixture');MaximumSeconds=60}}
  $doc=New-AORDriverDocument $plan $fakeExe $mods
  Check 'Managed document cannot be a native driver plan' ($doc.Schema -ceq 'aave-activity-output-r5.managed-fixture-plan.v1' -and $doc.NativeLaunchAuthorized -ceq $false -and $doc.ExpectedRows -eq 8 -and $doc.ExistingMods.Count -eq 4)
  Check 'Supplied pins and optional Mod order are preserved in memory' ($doc.Executable.Path -ceq $fakeExe.Path -and $doc.ExistingMods[3].AssemblyName -ceq 'Optional.Existing')
  $missing=@($mods|Where-Object AssemblyName -cne 'Rukari.CharacterVoice');$reject=$false;try{New-AORDriverDocument $plan $fakeExe $missing|Out-Null}catch{$reject=$true};Check 'Missing required Mod rejects' $reject
  $reject=$false;try{New-AORDriverDocument $plan $fakeExe ($mods+@($mods[0]))|Out-Null}catch{$reject=$true};Check 'Duplicate required Mod rejects' $reject
  $plan.Activity.MaximumSeconds=181;$reject=$false;try{New-AORDriverDocument $plan $fakeExe $mods|Out-Null}catch{$reject=$true};Check 'Out-of-range activity duration rejects' $reject
  $plan.Activity.MaximumSeconds=60;$plan.Mode='RootLive';$native=New-AORDriverDocument $plan $fakeExe $mods
  Check 'RootLive schema is constructed only as an in-memory test object' ($native.Schema -ceq 'aave-actual-wait-activity.root-plan.v1' -and $native.ObserverSHA256 -ceq '44E7607911CA0482EA8431743EB603BDC5E3D133E71CEA475DD0D8B07108FC19')
  $native=$null;$doc=$null
  Check 'Synthetic target paths were never created' (!(Test-Path -LiteralPath $target))
 }catch{Check 'Pure control execution' $false $_.Exception.ToString()}
}
$result=@{Schema='aave-r5-activity-binding-pure-controls.v1';Checks=$checks.ToArray();Failed=@($checks|Where-Object {!$_.Passed}).Count;HostPinsWereSynthetic=$true;NativePlanSerialized=$false;RootPlanOrDecisionCreated=$false;LiveHostRead=$false;RegistryOrNativeOrAAExecuted=$false}
$result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $out 'Result.json') -Encoding utf8NoBOM
[pscustomobject]@{Checks=$checks.Count;Failed=$result.Failed}|ConvertTo-Json
if($result.Failed){exit 1}
