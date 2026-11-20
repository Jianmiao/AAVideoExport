function Get-AORExecutionPins {
 $paths=@('Adapter.ps1','Adapter.Root.ps1','Invoke-RootProbe.ps1','ActivityBinding\ActivityBinding.ps1','..\Harness.ps1','..\Shared.Imports.json','..\ManagedDummy.ps1','..\DefaultOutputR4\Adapter.ps1','..\DefaultOutputR4\HelperRepair\bin\Release\net6.0\AAVE.DefaultOutput.Process.R4.dll')
 $full=@(foreach($p in $paths){[IO.Path]::GetFullPath((Join-Path $script:AORRoot $p))})
 $imports=Get-Content -LiteralPath (Join-Path $script:AORRoot '../Shared.Imports.json') -Raw|ConvertFrom-Json
 foreach($p in $imports.Files){$full+=[IO.Path]::GetFullPath((Join-Path (Join-Path $script:AORRoot '..') $p.RelativePath))}
 $full+=[IO.Path]::GetFullPath((Join-Path $script:AORRoot '../../VideoExport.ActualWaitActivityDriverR1/Read-ActivityReceipt.ps1'))
 foreach($p in $full|Sort-Object -Unique){@{Path=$p;Bytes=(Get-Item -LiteralPath $p).Length;SHA256=(Get-CSRHash $p)}}
}
function Assert-AORRootBindings($Plan,$Decision){
 Assert-CSR ($Plan.Schema -ceq 'aave-activity-output-r5.root-plan.v1' -and $Plan.Mode -ceq 'RootLive' -and $Plan.RunId -cmatch '^[a-f0-9]{32}$') 'Exact external live transaction plan required.'
 Assert-CSR ($Decision.Schema -ceq 'aave-activity-output-r5.root-decision.v1' -and $Decision.RunId -ceq $Plan.RunId -and $Decision.PlanDigest -ceq (Get-DORDigest $Plan)) 'Decision does not bind entire plan.'
 Assert-CSR ($Decision.AudioPolicy -ceq 'default-output/user-authorized' -and $Decision.Mode -ceq 'PASSTHROUGH' -and (Test-CSRBoolean $Decision.OwnerEnabled $false)) 'Default output PASSTHROUGH with owner disabled required.'
 $reviewVerdicts=[ordered]@{ObservationReview='accepted-root-passthrough-observation';DeploymentReview='all-original-mods-profile-preserved';BackendReview='existing-route-only-blocked-installer-excluded';ActivityReview='controlled-activity-and-natural-exit-reviewed';CoexistenceReview='actual-original-mods-ordinary-aave-no-early-owner';HelperReview='actual-retained-containment-and-exit-qualified';RecoveryReview='actual-complete-metadata-recovery-qualified';HarnessReview='activity-r5-exact-transaction-source-reviewed'}
 foreach($n in @('AdapterManifest','CandidateBinding','DriverSourceReview')+@($reviewVerdicts.Keys)){
  Assert-CSR (!(Test-CSRUnder $Decision.$n.Path $Plan.Lab)) 'Review input must stay outside live host.';Assert-DORPin $Decision.$n
 }
 Assert-CSR ($Decision.DriverSourceReview.SHA256 -ceq '523CA35507287ECB85D2D8AD864574E85113970CE78CCE798BEB898D699AB9E3') 'Exact completed driver source review required; it is not AA permission.'
 $manifest=Read-DORPinnedJson $Decision.AdapterManifest.Path $Decision.AdapterManifest.SHA256
 Assert-CSR ($manifest.Schema -ceq 'aave-activity-output-r5.adapter-package.v1') 'New reviewed adapter manifest required.'
 foreach($pin in $manifest.Files){Assert-CSR (!(Test-CSRUnder $pin.Path $Plan.Lab)) 'Manifest input may not read live host.';Assert-DORPin $pin}
 foreach($pin in @(Get-AORExecutionPins)){Assert-CSR (@($manifest.Files|Where-Object {$_.Path -ceq $pin.Path -and $_.SHA256 -ceq $pin.SHA256 -and $_.Bytes -eq $pin.Bytes}).Count -eq 1) ('Missing executing closure pin: '+$pin.Path)}
 $binding=Read-DORPinnedJson $Decision.CandidateBinding.Path $Decision.CandidateBinding.SHA256
 Assert-CSR ($binding.Schema -ceq 'aave-default-output-candidate-binding.v1' -and $binding.Mode -ceq 'PASSTHROUGH' -and (Test-CSRBoolean $binding.OwnerEnabled $false) -and $binding.NormalGeneratorLineage -ceq 'independently-reviewed-successor') 'Coherent fixed observer binding required.'
 foreach($pin in $binding.Inputs){Assert-CSR (!(Test-CSRUnder $pin.Path $Plan.Lab)) 'Candidate pin outside host required.';Assert-DORPin $pin}
 foreach($role in @('Core','Witness','Bridge','ActualWait10','SourceClosure','PackageManifest','FinalHandoff','IndependentReview')){Assert-CSR (@($binding.Inputs|Where-Object Role -ceq $role).Count -eq 1) ('Missing candidate role: '+$role)}
 foreach($role in @('Core','Witness','Bridge','ActualWait10')){Assert-CSR (@($binding.Inputs|Where-Object Role -ceq $role)[0].SHA256 -ceq '44E7607911CA0482EA8431743EB603BDC5E3D133E71CEA475DD0D8B07108FC19') 'Fixed coherent observer identity changed.'}
 $observer=@($binding.Inputs|Where-Object Role -ceq 'ActualWait10')[0]
 Assert-CSR ($observer.Path -ceq $Plan.Observer.Path -and $observer.SHA256 -ceq $Plan.Observer.SHA256 -and $observer.Bytes -eq $Plan.Observer.Bytes -and $Plan.CandidateBindingSHA256 -ceq $Decision.CandidateBinding.SHA256) 'Plan observer/binding differ.'
 foreach($name in $reviewVerdicts.Keys){
  $r=Read-DORPinnedJson $Decision.$name.Path $Decision.$name.SHA256
  Assert-CSR ($r.Schema -ceq 'aave-activity-output-r5.root-bound-review.v1' -and $r.RunId -ceq $Plan.RunId -and $r.PlanDigest -ceq $Decision.PlanDigest -and $r.CandidateBindingSHA256 -ceq $Decision.CandidateBinding.SHA256 -and $r.AdapterManifestSHA256 -ceq $Decision.AdapterManifest.SHA256 -and $r.Verdict -ceq $reviewVerdicts[$name] -and $r.Rationale.Length -gt 20 -and @($r.Blockers).Count -eq 0) ('Missing actual run-bound review: '+$name)
 }
 Assert-CSR ((Test-CSRInteger $Plan.MaximumObservationEvents) -and $Plan.MaximumObservationEvents -ge 0 -and $Plan.MaximumObservationEvents -le 100000) 'Observation bound required.'
 $null=Get-AORPinnedActivitySources $Plan.Activity.SourceRoot $false
 [pscustomobject]@{PlanDigest=$Decision.PlanDigest;RunId=$Plan.RunId;Plan=$Plan;Decision=$Decision;Binding=$binding}
}
function Invoke-AORLiveProcess($Plan,$State,$Prepared){
 # The frozen helper is the only actual launch source and owns the receipt.
 # It records AllOwnedProcessesExited before any downstream assertion throws.
 Invoke-DORLiveProcess $Plan $State $Prepared
 $finals=@(Get-ChildItem -LiteralPath $Plan.Output -Filter 'activity-driver-state-*.json' -File|Where-Object {((Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json).Point) -ceq 'application-quit-callback'})
 Assert-CSR ($finals.Count -eq 1) 'Exactly one actual quit-callback receipt required.'
 $reader=[IO.Path]::GetFullPath((Join-Path $script:AORRoot '../../VideoExport.ActualWaitActivityDriverR1/Read-ActivityReceipt.ps1'))
 Assert-CSR ((Get-CSRHash $reader) -ceq '27E3D70B351C6404157784BAC7394C0433F9869545E100F6BB2DF8BB1F6E7B14') 'Frozen activity receipt reader changed.'
 $null=& $reader -VerifySaved -DriverFinalState $finals[0].FullName -OwnedHelperResult (Join-Path $Plan.Output 'owned-helper-result.json') -ExpectedRunId $Plan.RunId -ExpectedDriverSHA256 '2D958E54A8D9C06F5629431CB2849FBC9C03F691D869B45D4364FBA3C329DAF4' -ExpectedObserverSHA256 $Plan.Observer.SHA256 -ExpectedPlanSHA256 (Get-CSRHash (Join-Path $Plan.Output 'activity-driver-plan.json')) -ExpectedRootConfigSHA256 (Get-CSRHash (Join-Path $Plan.Output 'root-config.json')) -Output (Join-Path $Plan.Output 'activity-receipt-consistency.json')
}
function New-AORLiveProcessContext($Plan){
 $helper=Join-Path $script:DORRoot 'HelperRepair\bin\Release\net6.0\AAVE.DefaultOutput.Process.R4.dll'
 Assert-CSR ((Get-CSRHash $helper) -ceq '3B8EDD9DA131FD0AACAA67E58422D21FD7A7CC58663FC5658709D97194CDBC6A') 'Frozen helper changed; new source cannot inherit R4 permission.'
 New-DORLiveProcessContext $Plan
}
