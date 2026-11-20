#requires -Version 7.2
param([string]$ExpectedHostDirectory,[string]$RootPlanPath,[string]$RootPlanSHA256,[string]$RootDecisionPath,[string]$RootDecisionSHA256,[switch]$Prepare,[switch]$Launch)
$ErrorActionPreference='Stop'
if(!$Prepare -and !$Launch){[pscustomobject]@{Mode='NO_WORK';DocumentsRead=$false;HostInventoryRead=$false;Staged=$false;CreationHelperInvoked=$false;Started=$false}|ConvertTo-Json;return}
if($Prepare -and $Launch){throw 'Choose either preparation or launch.'}
. (Join-Path $PSScriptRoot 'Adapter.ps1')
. (Join-Path $PSScriptRoot 'Adapter.Root.ps1')
$input=Read-DORRootInputs $ExpectedHostDirectory $RootPlanPath $RootPlanSHA256 $RootDecisionPath $RootDecisionSHA256
$plan=$input.Plan;$decision=$input.Decision;$context=Assert-AORRootBindings $plan $decision
$script:DORLiveContext=$context
try{
 Assert-AORPlan $plan;$layout=New-AORLayout $plan
 if(!$Launch){[pscustomobject]@{Schema='aave-activity-output-r5.planonly.v1';RunId=$plan.RunId;PlanDigest=$context.PlanDigest;BindingsValidated=$true;HostInventoryRead=$false;NativeHelperLoaded=$false;Staged=$false;Started=$false;AdditionCount=$layout.Files.Count;OwnerEnabled=$false;RuntimeReleased=$false}|ConvertTo-Json;return}
 $result=Invoke-AORTransaction $plan {
  param($snapshot,$layout)
  Assert-DORHostSnapshot $plan $snapshot;Assert-AORBaselinePins $plan $snapshot
  $null=Assert-AORRootBindings $plan $decision
  Write-CSRJson (Join-Path $plan.Output 'executed-source-pins.json') @(Get-AORExecutionPins)
 }{
  param($snapshot,$layout)
  Write-CSRJson (Join-Path $plan.Output 'reviewed-plan.json') $plan
  Write-CSRJson (Join-Path $plan.Output 'source-bindings.json') @{PlanDigest=$context.PlanDigest;DecisionSHA256=$RootDecisionSHA256;Decision=$decision;AudioPolicy=(Get-DORAudioPolicy)}
  Write-AORConfiguration $plan $layout $snapshot;Invoke-AORStage $plan $snapshot $layout
 }{New-AORLiveProcessContext $plan}{param($state,$prepared)Invoke-AORLiveProcess $plan $state $prepared}
 $result|ConvertTo-Json -Depth 35;if(!$result.Passed){exit 1}
}finally{$script:DORLiveContext=$null}
