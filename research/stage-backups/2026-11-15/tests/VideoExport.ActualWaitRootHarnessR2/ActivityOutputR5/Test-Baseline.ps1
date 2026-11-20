$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot 'Evidence/01-baseline-multi-stage-red'
if(Test-Path -LiteralPath $out){throw 'Fresh baseline evidence required.'}
. (Join-Path $PSScriptRoot '../DefaultOutputR4/Adapter.ps1')
$script:DORRoot=$PSScriptRoot
$id=[guid]::NewGuid().ToString('N');$lab=Join-Path $out 'fixture'
$stage=Join-Path $lab ('BepInEx\patchers\AAVE-DefaultOutputR4-'+$id)
$marker=Join-Path $stage 'owner.json';$driver=Join-Path $lab ('BepInEx\plugins\AAVE-ActivityOutputR5-'+$id+'\driver.dll')
$plan=@{Mode='ManagedFixture';RunId=$id;Lab=$lab;Output=(Join-Path $out 'output');StateRoot=(Join-Path $out 'state');RegistrySubKey=('Software\AAVE.ActualWaitRootHarnessR2-Fixture\'+$id);StageRoot=$stage;OwnerMarkerPath=$marker;ExpectedStageFiles=@{$marker=('A'*64);$driver=('B'*64)};AllowedOriginalReplacements=@();AllowedNewFiles=@();ExpectedAbsentPaths=@();AllowedNewRegistryKeys=@();AllowedNewRegistryValues=@()}
$errorText=$null;try{Assert-DORPlan $plan}catch{$errorText=$_.Exception.Message}
[void][IO.Directory]::CreateDirectory($out)
$result=@{Schema='aave-activity-output-r5.baseline-red.v1';RequiredBehavior='Admit separately owned patcher and normal-plugin staging';BehaviorPassed=($null -eq $errorText);Failure=$errorText;ExpectedOldLimitationObserved=($errorText -ceq 'Stage must be a flat exact file set.');Source=@{Path=(Join-Path $PSScriptRoot '../DefaultOutputR4/Adapter.ps1');SHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../DefaultOutputR4/Adapter.ps1')).Hash};HostRead=$false;RegistryRead=$false;Staged=$false;ChildCreated=$false}
Write-CSRJson (Join-Path $out 'Result.json') $result
$result|ConvertTo-Json -Depth 6
if(!$result.BehaviorPassed){exit 1}
