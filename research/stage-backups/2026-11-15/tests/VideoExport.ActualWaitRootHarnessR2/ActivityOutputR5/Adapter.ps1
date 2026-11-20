#requires -Version 7.2
# Successor composition: frozen R4 helper, R2 snapshot/registry/final-state
# bodies, and new exact multi-location staging/cleanup. No native work on import.
$script:AORRoot=$PSScriptRoot
$r4=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../DefaultOutputR4/Adapter.ps1'))
if((Get-FileHash -LiteralPath $r4).Hash -cne 'EF87B8094BE5F7AD4CABB531CE7D3B55051714DA665BEFD5CB3046F8D3361F89'){throw 'Frozen R4 adapter changed.'}
. $r4
. (Join-Path $script:AORRoot 'ActivityBinding/ActivityBinding.ps1')
$script:AORMetadataTrace=$null
function Get-CSRMetadataChanges($Actual,$Original){
 $changes=@();$errorText=$null
 try{$changes=@(& $script:DOROriginalMetadataComparer $Actual $Original)}catch{$errorText=$_.Exception.ToString();throw}
 finally{if($script:AORMetadataTrace){
  if($script:AORMetadataTrace.Records.Count -lt 100000){
   $expected=[ordered]@{};$observed=[ordered]@{}
   foreach($n in @('Path','IsDirectory','SecuritySddl','Attributes','CreationUtcTicks','WriteUtcTicks','AccessUtcTicks')){$expected[$n]=$Original.$n;$observed[$n]=$Actual.$n}
   $script:AORMetadataTrace.Records.Add([pscustomobject]@{Sequence=$script:AORMetadataTrace.Records.Count+1;Expected=$expected;Actual=$observed;ChangedProperties=@($changes|ForEach-Object Property);Error=$errorText;Stack=@(Get-PSCallStack|Select-Object -ExpandProperty FunctionName)})
  }else{$script:AORMetadataTrace.Dropped++}
 }}
 $changes
}
function Assert-AORCanonical([string]$Path){Assert-CSR ([IO.Path]::IsPathFullyQualified($Path) -and [IO.Path]::GetFullPath($Path).TrimEnd('\') -ceq $Path -and $Path.Length -gt 3 -and $Path.Substring(2) -notmatch ':') 'Canonical non-root path without streams required.'}
function Assert-DORPlan($Plan){Assert-AORPlan $Plan}
function Assert-AWRFixturePlan($Plan){Assert-AORPlan $Plan}
function Assert-AORPlan($Plan){
 Assert-CSR ($Plan.RunId -cmatch '^[a-f0-9]{32}$') 'Exact run identity required.'
 foreach($n in @('Lab','Output','StateRoot')){Assert-AORCanonical $Plan.$n}
 $roots=@($Plan.Lab,$Plan.Output,$Plan.StateRoot)
 for($i=0;$i -lt 3;$i++){for($j=$i+1;$j -lt 3;$j++){Assert-CSR (!(Test-CSRUnder $roots[$i] $roots[$j]) -and !(Test-CSRUnder $roots[$j] $roots[$i])) 'Host, output and state must be disjoint.'}}
 if($Plan.Mode -ceq 'ManagedFixture'){
  Assert-CSR ($Plan.Schema -ceq 'aave-activity-output-r5.managed-fixture-transaction.v1') 'Explicit managed-fixture schema required.'
  foreach($p in $roots){Assert-CSR (Test-CSRUnder $p (Join-Path $script:AORRoot 'Evidence')) 'Fixture escapes new R5 evidence.'}
  Assert-CSR ($Plan.RegistrySubKey -ceq ('Software\AAVE.ActualWaitRootHarnessR2-Fixture\'+$Plan.RunId)) 'Isolated nonce registry required.'
 }else{
  Assert-CSR ($Plan.Mode -ceq 'RootLive' -and $Plan.Schema -ceq 'aave-activity-output-r5.root-plan.v1' -and $script:DORLiveContext -and $script:DORLiveContext.PlanDigest -ceq (Get-DORDigest $Plan)) 'Exact externally reviewed Root context required.'
  Assert-DORLiveRegistry $Plan.RegistrySubKey
  Assert-CSR ($Plan.Executable -ceq (Join-Path $Plan.Lab 'AzureArchive.exe') -and (Test-CSRInteger $Plan.MaximumSeconds) -and $Plan.MaximumSeconds -ge ($Plan.Activity.MaximumSeconds+30) -and $Plan.MaximumSeconds -le 600) 'Bounded parent startup/quit budget and actual executable required.'
  Assert-CSR ($Plan.ExpectedOriginalFileInventoryDigest -cmatch '^[A-F0-9]{64}$' -and $Plan.ExpectedOriginalRegistryDigest -cmatch '^[A-F0-9]{64}$') 'Actual original inventory/registry reviews required.'
 }
 Assert-CSR ($Plan.StageRoot -ceq (Join-Path $Plan.Lab ('BepInEx\patchers\AAVE-ActivityOutputR5-'+$Plan.RunId)) -and $Plan.OwnerMarkerPath -ceq (Join-Path $Plan.StageRoot 'owner.json')) 'Exact observer stage required.'
 Assert-AORCanonical $Plan.Activity.FixtureRoot
 Assert-CSR (Test-CSRUnder $Plan.Activity.FixtureRoot $Plan.Lab) 'Persistent fixture target must be inside the full restoration root.'
 Assert-CSR ($Plan.Activity.PersistentStoryPath -ceq (Join-Path $Plan.Activity.FixtureRoot 'data\saves\AAVE-OfficialAuthoring-20261030.aas')) 'Reviewed persistent story target differs.'
 Assert-CSR (!(Test-CSRUnder $Plan.Activity.FixtureRoot (Join-Path $Plan.Lab 'BepInEx'))) 'Fixture cannot overlap plugin/patcher locations.'
 Assert-CSR ((Test-CSRInteger $Plan.Activity.MaximumSeconds) -and $Plan.Activity.MaximumSeconds -ge 5 -and $Plan.Activity.MaximumSeconds -le 180) 'Bounded activity duration required.'
 foreach($name in @('AllowedOriginalReplacements','AllowedNewFiles','AllowedNewRegistryKeys','AllowedNewRegistryValues','ExpectedAbsentPaths')){Assert-CSR (@($Plan.$name).Count -eq 0) ('Unreviewed mutation allowance prohibited: '+$name)}
 Assert-CSR ($Plan.Observer.SHA256 -ceq '44E7607911CA0482EA8431743EB603BDC5E3D133E71CEA475DD0D8B07108FC19') 'Observer remains fixed PASSTHROUGH.'
 Assert-CSR ($Plan.ExecutablePin.Path -ceq $Plan.Executable) 'Executable pin path differs.'
 Assert-CSR ($Plan.DiagnosticPolicy -and (Test-CSRInteger $Plan.DiagnosticPolicy.MaximumBytes) -and $Plan.DiagnosticPolicy.MaximumBytes -ge 1 -and $Plan.DiagnosticPolicy.MaximumBytes -le 16777216) 'Bounded diagnostic policy required.'
 foreach($p in @($Plan.DiagnosticPolicy.Directories)){Assert-AORCanonical $p;Assert-CSR ((Test-CSRUnder $p $Plan.Lab) -and $p -cne $Plan.Lab -and !(Test-CSRUnder $p $Plan.StageRoot)) 'Diagnostic directory outside reviewed host scope.'}
 foreach($p in @($Plan.DiagnosticPolicy.MutableOriginalPaths)){Assert-AORCanonical $p;Assert-CSR ([IO.Path]::GetExtension($p) -ceq '.log' -and (Split-Path $p -Parent) -cin @($Plan.DiagnosticPolicy.Directories)) 'Only exact original diagnostic log paths may change.'}
}
function New-AORLayout($Plan){
 Assert-AORPlan $Plan;Assert-DORPin $Plan.Observer
 $bundle=Get-AORPinnedActivitySources $Plan.Activity.SourceRoot ($Plan.Mode -ceq 'ManagedFixture')
 $files=[Collections.Generic.List[object]]::new();$groups=[Collections.Generic.List[object]]::new()
 $driverRoot=Join-Path $Plan.Lab ('BepInEx\plugins\AAVE-ActivityOutputR5-'+$Plan.RunId)
 foreach($g in @(@{Role='Observer';Root=$Plan.StageRoot;Marker=$Plan.OwnerMarkerPath},@{Role='Driver';Root=$driverRoot;Marker=(Join-Path $driverRoot 'owner.json')},@{Role='Fixture';Root=$Plan.Activity.FixtureRoot;Marker=(Join-Path $Plan.Activity.FixtureRoot ('aave-activity-r5-owner-'+$Plan.RunId+'.json'))})){
  $text=ConvertTo-CSRCanonicalJson @{Schema=$(if($Plan.Mode -ceq 'ManagedFixture'){'aave-activity-output-r5.managed-fixture-ownership.v1'}else{'aave-activity-output-r5.staging-ownership.v1'});RunId=$Plan.RunId;Role=$g.Role;Root=$g.Root;NativeLaunchAuthorized=$false}
  $groups.Add([pscustomobject]@{Role=$g.Role;Root=$g.Root;Marker=$g.Marker;MarkerText=$text;SHA256=(Get-CSRTextHash $text)})
  $files.Add([pscustomobject]@{Path=$g.Marker;Role=$g.Role;IsMarker=$true;SourcePath=$null;Text=$text;SHA256=(Get-CSRTextHash $text);Bytes=[Text.Encoding]::UTF8.GetByteCount($text);LastWriteUtcTicks=$null})
 }
 foreach($pair in @(@{Role='Observer';Root=$Plan.StageRoot;Pin=$Plan.Observer},@{Role='Driver';Root=$driverRoot;Pin=$bundle.Driver})){$files.Add([pscustomobject]@{Path=(Join-Path $pair.Root ([IO.Path]::GetFileName($pair.Pin.Path)));Role=$pair.Role;IsMarker=$false;SourcePath=$pair.Pin.Path;Text=$null;SHA256=$pair.Pin.SHA256;Bytes=$pair.Pin.Bytes;LastWriteUtcTicks=$null})}
 foreach($f in @(Get-AORFixtureDestinations $Plan.Activity.FixtureRoot $bundle)){$files.Add([pscustomobject]@{Path=$f.Path;Role='Fixture';IsMarker=$false;SourcePath=$f.SourcePath;Text=$null;SHA256=$f.SHA256;Bytes=$f.Bytes;LastWriteUtcTicks=$f.LastWriteUtcTicks})}
 $map=@{};$directories=@{}
 foreach($f in $files){Assert-AORCanonical $f.Path;Assert-CSR ((Test-CSRUnder $f.Path $Plan.Lab) -and !$map.ContainsKey($f.Path)) 'Addition paths collide or escape snapshot.';$map[$f.Path]=$f
  for($d=Split-Path $f.Path -Parent;$d -and $d -cne $Plan.Lab;$d=Split-Path $d -Parent){Assert-CSR (Test-CSRUnder $d $Plan.Lab) 'Directory escaped snapshot.';$directories[$d]=$true}
 }
 Assert-CSR ($files.Count -eq 28 -and @($files|Where-Object {$_.Role -ceq 'Fixture' -and !$_.IsMarker}).Count -eq 23) 'Complete 28-file layout required.'
 [pscustomobject]@{Files=$files.ToArray();FileMap=$map;Groups=$groups.ToArray();Directories=@($directories.Keys|Sort-Object Length);NewDirectories=@();InitiallyAbsent=@();Bundle=$bundle;AbsenceProved=$false}
}
function Assert-AORInitialAbsence($Plan,$Snapshot,$Layout){
 Assert-CSR ($Snapshot.Durable -and $Snapshot.RunId -ceq $Plan.RunId) 'Durable exact snapshot required.'
 foreach($g in $Layout.Groups|Where-Object Role -cne 'Fixture'){Assert-CSR (!$Snapshot.Directories.ContainsKey($g.Root) -and !$Snapshot.Files.ContainsKey($g.Root) -and !(Test-Path -LiteralPath $g.Root)) 'Normal plugin/patcher stage must be fresh.'}
 foreach($f in $Layout.Files){$null=Assert-CSRPath $f.Path;Assert-CSR (!$Snapshot.Files.ContainsKey($f.Path) -and !$Snapshot.Directories.ContainsKey($f.Path) -and !(Test-Path -LiteralPath $f.Path)) ('Never overwrite original fixture or Mod: '+$f.Path)}
 foreach($d in $Layout.Directories){$null=Assert-CSRPath $d;Assert-CSR (!$Snapshot.Files.ContainsKey($d)) 'Planned parent was an original file.';if(Test-Path -LiteralPath $d){Assert-CSR ($Snapshot.Directories.ContainsKey($d)) 'New unowned parent appeared since snapshot.'}}
 foreach($d in @($Plan.DiagnosticPolicy.Directories)){Assert-CSR ($Snapshot.Directories.ContainsKey($d)) 'Diagnostic retention only in pre-existing directories.'}
 foreach($p in @($Plan.DiagnosticPolicy.MutableOriginalPaths)){Assert-CSR ($Snapshot.Files.ContainsKey($p)) 'Mutable original log missing from baseline.'}
 $Layout.NewDirectories=@($Layout.Directories|Where-Object {!$Snapshot.Directories.ContainsKey($_)})
 $Layout.InitiallyAbsent=@($Layout.Files|ForEach-Object Path);$Layout.AbsenceProved=$true
 Write-CSRDurable (Join-Path $Plan.StateRoot 'addition-layout.clixml') ([Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize($Layout,70)))
}
function Invoke-AORStage($Plan,$Snapshot,$Layout){
 Assert-CSR ($Snapshot.Durable -and $Layout.AbsenceProved) 'Complete snapshot and exact 28-path absence proof required.'
 # Repeat saved source validation immediately before writing; no historical originals.
 $null=Get-AORPinnedActivitySources $Plan.Activity.SourceRoot ($Plan.Mode -ceq 'ManagedFixture');Assert-DORPin $Plan.Observer
 foreach($d in $Layout.NewDirectories){$null=Assert-CSRPath $d;Assert-CSR (!(Test-Path -LiteralPath $d)) 'Owned directory appeared before staging.';[void][IO.Directory]::CreateDirectory($d)}
 foreach($f in @($Layout.Files|Where-Object IsMarker)+@($Layout.Files|Where-Object {!$_.IsMarker})){
  $null=Assert-CSRPath $f.Path;Assert-CSR (!(Test-Path -LiteralPath $f.Path)) 'Addition appeared before exclusive create.'
  if($f.IsMarker){$bytes=[Text.Encoding]::UTF8.GetBytes($f.Text)}else{$bytes=[IO.File]::ReadAllBytes($f.SourcePath)}
  Assert-CSR ($bytes.Length -eq $f.Bytes -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ceq $f.SHA256) 'Stage source content changed.'
  Write-CSRDurable $f.Path $bytes
  if($null -ne $f.LastWriteUtcTicks){[IO.File]::SetLastWriteTimeUtc($f.Path,[datetime]::new([long]$f.LastWriteUtcTicks,[DateTimeKind]::Utc))}
 }
 $readbacks=@(foreach($f in $Layout.Files){$item=Get-Item -LiteralPath $f.Path -Force;$hash=Get-CSRHash $f.Path;Assert-CSR ($item.Length -eq $f.Bytes -and $hash -ceq $f.SHA256 -and ($null -eq $f.LastWriteUtcTicks -or $item.LastWriteTimeUtc.Ticks -eq $f.LastWriteUtcTicks)) 'Complete staged byte/tick readback failed.';@{Path=$f.Path;Role=$f.Role;SHA256=$hash;Bytes=$item.Length;LastWriteUtcTicks=$item.LastWriteTimeUtc.Ticks;ExpectedLastWriteUtcTicks=$f.LastWriteUtcTicks}})
 Write-CSRJson (Join-Path $Plan.Output 'staging-readback.json') @{RunId=$Plan.RunId;FileCount=$readbacks.Count;Files=$readbacks;OriginalModsReplaced=0;OwnerEnabled=$false}
}
function Get-AORDiagnostics($Plan,$Snapshot,$Current,$Layout){
 $accepted=[Collections.Generic.List[object]]::new();$rejected=[Collections.Generic.List[object]]::new();$errors=[Collections.Generic.List[string]]::new()
 foreach($f in $Current.Files.Values){if($Snapshot.Files.ContainsKey($f.Path) -or ($Layout -and $Layout.FileMap.ContainsKey($f.Path))){continue}
  $ok=[IO.Path]::GetExtension($f.Path) -ceq '.log' -and (Split-Path $f.Path -Parent) -cin @($Plan.DiagnosticPolicy.Directories) -and $f.Metadata -and (Get-Item -LiteralPath $f.Path -Force).Length -le $Plan.DiagnosticPolicy.MaximumBytes
  $record=@{Path=$f.Path;SHA256=$f.SHA256;Accepted=[bool]$ok;Preserved=$true;PolicySHA256=(Get-DORDigest $Plan.DiagnosticPolicy)}
  if($ok){$accepted.Add($record)}else{$rejected.Add($record);$errors.Add('Unexpected new content preserved: '+$f.Path)}
 }
 [pscustomobject]@{Accepted=$accepted.ToArray();Rejected=$rejected.ToArray();Errors=$errors.ToArray()}
}
function Remove-AORAdditions($Plan,$Snapshot,$Layout){
 $errors=[Collections.Generic.List[string]]::new();$removed=[Collections.Generic.List[string]]::new()
 if(!$Layout -or !$Layout.AbsenceProved){return [pscustomobject]@{Removed=@();Errors=@()}}
 foreach($g in $Layout.Groups){
  $owned=@($Layout.Files|Where-Object Role -ceq $g.Role);$present=@($owned|Where-Object {Test-Path -LiteralPath $_.Path})
  if(!$present.Count){continue}
  try{
   $null=Assert-CSRPath $g.Marker;Assert-CSR ((Test-Path -LiteralPath $g.Marker -PathType Leaf) -and (Get-CSRHash $g.Marker) -ceq $g.SHA256) 'Group marker missing or changed; preserve remaining group.'
   foreach($f in $owned|Where-Object {!$_.IsMarker}){
    if(!(Test-Path -LiteralPath $f.Path)){continue}
    try{
     Assert-CSR ($f.Path -cin $Layout.InitiallyAbsent -and !$Snapshot.Files.ContainsKey($f.Path)) 'Removal lacks initial absence.'
     $null=Assert-CSRPath $f.Path;$null=Assert-CSRPath $g.Marker
     Assert-CSR ((Get-CSRHash $g.Marker) -ceq $g.SHA256 -and (Test-Path -LiteralPath $f.Path -PathType Leaf) -and (Get-CSRHash $f.Path) -ceq $f.SHA256 -and (Get-Item -LiteralPath $f.Path -Force).Length -eq $f.Bytes) 'Owned bytes/marker changed; preserve.'
     Remove-Item -LiteralPath $f.Path -ErrorAction Stop;$removed.Add($f.Path)
    }catch{$errors.Add($f.Path+': '+$_.Exception.Message)}
   }
   Assert-CSR (@($owned|Where-Object {!$_.IsMarker -and (Test-Path -LiteralPath $_.Path)}).Count -eq 0) 'Group retains changed content; preserve marker.'
   if($g.Role -cne 'Fixture'){Assert-CSR (@(Get-CSRChildren $g.Root $true|Where-Object FullName -cne $g.Marker).Count -eq 0) 'Dedicated stage has unexpected child; preserve marker.'}
   Assert-CSR ((Get-CSRHash $g.Marker) -ceq $g.SHA256) 'Marker changed before removal.'
   Remove-Item -LiteralPath $g.Marker -ErrorAction Stop;$removed.Add($g.Marker)
  }catch{$errors.Add($g.Role+': '+$_.Exception.Message)}
 }
 foreach($d in @($Layout.NewDirectories|Sort-Object Length -Descending)){if(Test-Path -LiteralPath $d){try{
  Assert-CSR (!$Snapshot.Directories.ContainsKey($d) -and !$Snapshot.Files.ContainsKey($d) -and (Test-CSRUnder $d $Plan.Lab)) 'Removal not a new exact parent.'
  $null=Assert-CSRPath $d;Assert-CSR ((Test-Path -LiteralPath $d -PathType Container) -and @(Get-ChildItem -LiteralPath $d -Force -ErrorAction Stop).Count -eq 0) 'New directory not empty; preserve.'
  Remove-Item -LiteralPath $d -ErrorAction Stop;$removed.Add($d)
 }catch{$errors.Add($d+': '+$_.Exception.Message)}}}
 [pscustomobject]@{Removed=$removed.ToArray();Errors=$errors.ToArray()}
}
function Restore-AORState($Plan,$Snapshot,$Layout,[bool]$OwnedTreeExited){
 Assert-AORPlan $Plan;Assert-CSR $OwnedTreeExited 'Retained parent exit proof missing; defer restoration.'
 if(!$Snapshot.Durable){return Restore-AWRState $Plan $Snapshot $true}
 $errors=[Collections.Generic.List[string]]::new();$current=$null;$diagnostics=$null
 try{
  $current=Get-AWRState $Plan
  $i=0;foreach($f in @($current.Files.Values|Sort-Object Path)){try{$p=Join-Path $Plan.StateRoot ('after-files\'+($i++).ToString('D5')+'.after');Write-CSRDurable $p ([IO.File]::ReadAllBytes($f.Path));Assert-CSR ((Get-CSRHash $p) -ceq $f.SHA256) 'After copy changed.';$f|Add-Member AfterEvidence $p}catch{$errors.Add('After evidence: '+$_.Exception.Message)}}
  Write-CSRDurable (Join-Path $Plan.StateRoot 'after-state.clixml') ([Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize($current,70)))
  $diagnostics=Get-AORDiagnostics $Plan $Snapshot $current $Layout
  foreach($e in $diagnostics.Errors){$errors.Add($e)}
  foreach($f in $Snapshot.Files.Values){if(!$current.Files.ContainsKey($f.Path) -or $current.Files[$f.Path].SHA256 -cne $f.SHA256){
   $allowed=$f.Path -cin @($Plan.DiagnosticPolicy.MutableOriginalPaths) -and $current.Files.ContainsKey($f.Path) -and (Get-Item -LiteralPath $f.Path -Force).Length -le $Plan.DiagnosticPolicy.MaximumBytes
   if(!$allowed){$errors.Add('Unapproved original mutation: '+$f.Path)}
  }}
 }catch{$errors.Add('After-state/classification: '+$_.Exception.Message)}
 # Always attempt every known original independently, even when classification failed.
 $actions=@(foreach($d in @($Snapshot.Directories.Values|Sort-Object {$_.Path.Length})){@{Kind='EnsureOriginalDirectory';Path=$d.Path;Record=$d}})+@(foreach($f in $Snapshot.Files.Values){@{Kind='RestoreFile';Path=$f.Path;Record=$f}})+@(foreach($key in $Snapshot.Registry.Keys){@{Kind='RestoreRegistryKey';Path=$Snapshot.Registry.SubKey;Record=$key}})
 $done=Invoke-CSRRestoreActions $actions {param($a)if($a.Kind -ceq 'RestoreRegistryKey'){Restore-AWRRegistryKey $a $Plan}else{Invoke-CSRRealRestoreAction $a $Plan $Snapshot}}
 foreach($e in $done.Errors){$errors.Add($e)}
 $cleanup=Remove-AORAdditions $Plan $Snapshot $Layout;foreach($e in $cleanup.Errors){$errors.Add($e)}
 $events=[Collections.Generic.List[object]]::new();$script:AORFinalState=$null
 $final=Complete-CSRFinalStatePass $Plan $Snapshot $null -ReadState {
  $events.Add(@{Kind='FinalCompleteContentReadBegin';Sequence=$events.Count+1});$script:AORFinalState=Get-AWRState $Plan;$events.Add(@{Kind='FinalCompleteContentReadEnd';Sequence=$events.Count+1});$script:AORFinalState
 } -ReadOutputs {param($s)$events.Add(@{Kind='ClassifyAlreadyReadInventory';Sequence=$events.Count+1});Get-AORDiagnostics $Plan $Snapshot $s $null} -FinalizeMetadata {param($r)
  Set-CSRMetadata $r;$a=Get-CSRMetadata $r.Path;Assert-CSR (@(Get-CSRMetadataChanges $a $r).Count -eq 0) 'Immediate final metadata differs.'
  $events.Add(@{Kind='FinalMetadataReadback';Sequence=$events.Count+1;Path=$r.Path;IsDirectory=$r.IsDirectory});$a
 }
 # Persist only captured objects. Nothing below reads an original path/tree.
 Write-CSRDurable (Join-Path $Plan.StateRoot 'final-state-before-metadata.clixml') ([Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize($script:AORFinalState,70)))
 foreach($e in $final.Errors){$errors.Add($e)}
 $result=[pscustomobject]@{Restored=($final.OriginalStateRestored -and $final.InventoryMatchesDeclaredOutcome -and $errors.Count -eq 0);OriginalStateRestored=$final.OriginalStateRestored;ExactWholeInventoryRestored=$final.ExactWholeInventoryRestored;InventoryMatchesDeclaredOutcome=$final.InventoryMatchesDeclaredOutcome;KnownOriginalMetadataReadbacks=$final.KnownOriginalMetadataReadbacks;RegistryComparison=$final.RegistryComparison;OriginalBytesMatch=$final.OriginalBytesMatch;UnknownContent=$diagnostics;RetainedOutputs=$final.RetainedOutputs;RejectedOutputs=$final.RejectedOutputs;Performed=$done.Actions;Cleanup=$cleanup;Errors=$errors.ToArray();VerificationOrder=$final.VerificationOrder;VerificationEvents=$events.ToArray();LiveContentsReadAfterFinalMetadata=$false}
 Write-CSRJson (Join-Path $Plan.StateRoot 'restoration-result.json') $result;$result
}
function Write-AORConfiguration($Plan,$Layout,$Snapshot){
 Assert-AORBaselinePins $Plan $Snapshot
 $doc=New-AORDriverDocument $Plan $Plan.ExecutablePin $Plan.ExistingMods
 $config=@{Schema='aave-activity-output-r5.managed-fixture-observer.v1';NativeLaunchAuthorized=$false;Mode='PASSTHROUGH';OwnerEnabled=$false;RunId=$Plan.RunId;EarlyAssemblySHA256=$Plan.Observer.SHA256;EvidenceDirectory=$Plan.Output}
 if($Plan.Mode -ceq 'RootLive'){
  $config=@{Schema='aave-actual-wait-entry-r1.root-passthrough.v1';RootProbeReleased=$true;Mode='PASSTHROUGH';OwnerEnabled=$false;RunId=$Plan.RunId;EarlyAssemblySHA256=$Plan.Observer.SHA256;InteropAssemblyPath=@($Plan.HostFiles|Where-Object Role -ceq 'InteropAssembly')[0].Path;UnityAssemblyPath=@($Plan.HostFiles|Where-Object Role -ceq 'UnityAssembly')[0].Path;EvidenceDirectory=$Plan.Output;MaximumObservationEvents=$Plan.MaximumObservationEvents}
 }
 Write-CSRJson (Join-Path $Plan.Output 'root-config.json') $config
 Write-CSRJson (Join-Path $Plan.Output 'activity-driver-plan.json') $doc
 Write-CSRJson (Join-Path $Plan.Output 'activity-bindings.json') @{RunId=$Plan.RunId;Mode=$Plan.Mode;RootConfigSHA256=(Get-CSRHash (Join-Path $Plan.Output 'root-config.json'));DriverPlanSHA256=(Get-CSRHash (Join-Path $Plan.Output 'activity-driver-plan.json'));RequiredParentOwnershipReceipt=(Join-Path $Plan.Output 'owned-process.json');OwnershipReceiptWrittenByHelperOnly=$true;FixtureManifest=$Layout.Bundle.FixtureManifest;ReviewedPersistentStoryPath=$Plan.Activity.PersistentStoryPath;DriverSelfReportProvesExit=$false;OwnerEnabled=$false}
}
function Assert-AORBaselinePins($Plan,$Snapshot){
 Assert-CSR $Snapshot.Durable 'Actual durable baseline required for executable/Mod binding.'
 foreach($pin in @($Plan.ExecutablePin)+@($Plan.ExistingMods)){
  if($Plan.Mode -ceq 'ManagedFixture' -and $pin.Path -ceq $Plan.Executable){Assert-DORPin $pin;continue}
  Assert-CSR ((Test-CSRUnder $pin.Path $Plan.Lab) -and $Snapshot.Files.ContainsKey($pin.Path) -and $Snapshot.Files[$pin.Path].SHA256 -ceq $pin.SHA256 -and (Get-Item -LiteralPath $Snapshot.Files[$pin.Path].Backup).Length -eq $pin.Bytes) 'Executable/Mod pin differs from actual full baseline.'
 }
 # Three required names are checked by the driver document builder. Actual
 # ordinary-versus-experimental assembly/dependency facts remain run-bound
 # coexistence review plus the driver's loaded-assembly checks, never GUID only.
 $null=New-AORDriverDocument $Plan $Plan.ExecutablePin $Plan.ExistingMods
}
function Invoke-AORTransaction($Plan,[scriptblock]$Validate,[scriptblock]$Stage,[scriptblock]$PrepareLaunch,[scriptblock]$Launch){
 $snapshot=$null;$layout=$null;$restoration=$null;$phase='not-started';$created=$false
 $state=@{Attempted=$false;CreationHelperInvoked=$false;OwnedTreeExited=$true;Process=$null};$failures=[Collections.Generic.List[string]]::new()
 $script:AORMetadataTrace=@{Records=[Collections.Generic.List[object]]::new();Dropped=0}
 try{
  Assert-AORPlan $Plan
  Assert-CSR (!(Test-Path -LiteralPath $Plan.Output) -and !(Test-Path -LiteralPath $Plan.StateRoot)) 'Fresh evidence/state required.'
  $null=Assert-CSRPath $Plan.Output;$null=Assert-CSRPath $Plan.StateRoot
  [void][IO.Directory]::CreateDirectory($Plan.Output);$created=$true;[void][IO.Directory]::CreateDirectory($Plan.StateRoot)
  if($Plan.Mode -ceq 'RootLive'){Assert-DORNoAA}
  $snapshot=New-AWRSnapshot $Plan;$phase='snapshot';Save-AWRState $Plan $snapshot
  $phase='source-and-layout-validation';$layout=New-AORLayout $Plan;Assert-AORInitialAbsence $Plan $snapshot $layout
  $phase='validation';& $Validate $snapshot $layout
  $phase='staging';& $Stage $snapshot $layout
  $phase='pre-create-preparation';$prepared=& $PrepareLaunch
  Assert-CSR ($null -ne $prepared -and $null -ne $Launch) 'Concrete prepared invocation required.'
  $phase='creation-helper-invocation';$state.Attempted=$true;$state.CreationHelperInvoked=$true;$state.OwnedTreeExited=$false
  & $Launch $state $prepared
  Assert-CSR ($null -ne $state.Process -and (Test-CSRBoolean $state.Process.Started $true) -and (Test-CSRBoolean $state.OwnedTreeExited $true)) 'Actual child and retained parent exit are unproved.'
 }catch{$failures.Add($phase+': '+$_.Exception.Message)}
 finally{
  if($snapshot){if($state.OwnedTreeExited){try{if($Plan.Mode -ceq 'RootLive'){Assert-DORNoAA};$restoration=Restore-AORState $Plan $snapshot $layout $true;if(!$restoration.Restored){$failures.Add('Complete original restoration or strict mutation acceptance failed.')}}catch{$failures.Add('Restoration: '+$_.Exception.Message)}}else{$failures.Add('Owned exit unproved; restoration deferred and durable originals retained.')}}
  if($created){try{Write-CSRDurable (Join-Path $Plan.StateRoot 'metadata-comparison-inputs.clixml') ([Text.Encoding]::UTF8.GetBytes([Management.Automation.PSSerializer]::Serialize($script:AORMetadataTrace,70)))}catch{$failures.Add('Metadata comparison evidence: '+$_.Exception.Message)}}
  if($script:AORMetadataTrace.Dropped -gt 0){$failures.Add('Metadata comparison trace bound exceeded.')};$script:AORMetadataTrace=$null
  $result=[pscustomobject]@{Schema='aave-activity-output-r5.transaction-result.v1';RunId=$Plan.RunId;Mode=$Plan.Mode;Phase=$phase;Passed=($failures.Count -eq 0 -and $restoration -and $restoration.Restored);AudioPolicy=(Get-DORAudioPolicy);LaunchAttempted=$state.Attempted;CreationHelperInvoked=$state.CreationHelperInvoked;Started=$(if($state.Process){$state.Process.Started}elseif($state.Attempted){$null}else{$false});Process=$state.Process;Restoration=$restoration;Failures=$failures.ToArray();DriverSelfReportProvesExit=$false;OldMetadataFailureFixed=$false;ActualHostGeneratorQualified=$false;NativeTraversalQualified=$false;ActualGuardOwnershipTransferred=$false;NativeWaitTerminalGateQualified=$false;Frame104RepairProved=$false;RuntimeReleased=$false}
  if($created){Write-CSRJson (Join-Path $Plan.Output 'process-result.json') $result}
 }
 $result
}
