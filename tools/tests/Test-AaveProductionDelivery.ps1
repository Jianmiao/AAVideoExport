#requires -Version 7.2
param([string]$EvidenceRoot)
$ErrorActionPreference='Stop'
$tools=Split-Path -Parent $PSScriptRoot;$repo=Split-Path -Parent $tools
. (Join-Path $tools 'Deployment.Common.ps1')
# Only process discovery is substituted for this synthetic filesystem harness.
# Every installation below is created under this new evidence directory; no
# real executable is launched and the production process guard stays unchanged.
function Get-Process { [CmdletBinding()]param([string]$Name) if($Name-ne'AzureArchive'){throw 'Unexpected process query in synthetic fixture.'} }
if(!$EvidenceRoot){$EvidenceRoot=Join-Path $repo ('artifacts/production-no-audit-20261031/deployment-'+[guid]::NewGuid().ToString('N'))}
$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\')+'\'
if(!$evidence.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic fixtures must remain inside this checkout artifacts directory.'}
$null=Get-AaveSafePath $repo ([IO.Path]::GetRelativePath($repo,$evidence))
if(Test-Path -LiteralPath $evidence){throw 'Use a new synthetic evidence directory; never overwrite previous fixtures.'}
New-Item -ItemType Directory -Path $evidence|Out-Null
$version=(Get-Content -LiteralPath (Join-Path $repo 'manifest.json') -Raw|ConvertFrom-Json).version_number
$binary=Join-Path $evidence 'synthetic-assemblies';New-Item -ItemType Directory -Path $binary|Out-Null
foreach($kind in @('production','audit')){
    $folder=Join-Path $binary $kind;New-Item -ItemType Directory -Path $folder|Out-Null
    $literal=if($kind-eq'audit'){'true'}else{'false'}
    Add-Type -TypeDefinition ('using System.Reflection;[assembly:AssemblyVersion("'+$version+'.0")]namespace AAVideoExport.Core{public static class ExportBuildProfile{public static bool DevelopmentAuditEnabled => '+$literal+';}}') -OutputAssembly (Join-Path $folder 'AAVideoExport.Core.dll') -OutputType Library
}
Add-Type -TypeDefinition ('using System.Reflection;[assembly:AssemblyVersion("'+$version+'.0")]namespace AAVideoExport.Plugin{public sealed class SyntheticDeliveryFixture{}}') -OutputAssembly (Join-Path $binary 'AAVideoExport.dll') -OutputType Library
Add-Type -TypeDefinition 'using System.Reflection;[assembly:AssemblyVersion("0.1.0.0")]namespace AAVideoExport.RuntimeBootstrap{public sealed class SyntheticDeliveryFixture{}}' -OutputAssembly (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -OutputType Library
# Add-Type assigns random assembly simple names. Normalize only these newly
# generated structural fixtures; no installed or product assembly is rewritten.
Add-Type -Path 'F:\AzureArchive_101\BepInEx\core\Mono.Cecil.dll'
foreach($file in Get-ChildItem -LiteralPath $binary -Recurse -Filter '*.dll' -File){
    $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName)
    try{$assembly.Name.Name=$file.BaseName;$assembly.MainModule.Name=$file.Name;$assembly.Write($file.FullName+'.named')}
    finally{$assembly.Dispose()}
    Move-Item -LiteralPath ($file.FullName+'.named') -Destination $file.FullName -Force
}
function Checksums([string]$root){[IO.File]::WriteAllLines((Join-Path $root 'SHA256SUMS.txt'),@(Get-ChildItem -LiteralPath $root -Recurse -File|Where-Object{$_.FullName-ne(Join-Path $root 'SHA256SUMS.txt')}|Sort-Object FullName|ForEach-Object{(Get-AaveHash $_.FullName)+'  '+[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')}))}
function New-Package([string]$name,[bool]$audit,[bool]$legacy=$false){
    $root=Join-Path $evidence $name;$payload=Join-Path $root ('AAVideoExport/'+$version);New-Item -ItemType Directory -Path $payload -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.dll') -Destination $payload
    $mode=if($audit){'audit'}else{'production'};Copy-Item -LiteralPath (Join-Path $binary ($mode+'/AAVideoExport.Core.dll')) -Destination $payload
    foreach($file in @('Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll','icon.png','README.md')){[IO.File]::WriteAllText((Join-Path $payload $file),'SYNTHETIC FILE-TRANSACTION FIXTURE; NOT AN AA MOD')}
    Write-AaveJson (Join-Path $payload 'manifest.json') ([ordered]@{name='AAVideoExport';version_number=$version})
    foreach($file in @('Install-Mod.ps1','Rollback-Mod.ps1','Deployment.Common.ps1','Start-AA-AAVE.ps1')){Copy-Item -LiteralPath (Join-Path $tools $file) -Destination $root}
    $bootPath=$null;$bootHash=$null
    if($audit){New-Item -ItemType Directory -Path (Join-Path $root 'bootstrap')|Out-Null;$bootPath='bootstrap/AAVideoExport.RuntimeBootstrap.dll';Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -Destination (Join-Path $root $bootPath);$bootHash=Get-AaveHash (Join-Path $root $bootPath)}
    $descriptor=[ordered]@{Schema='aave.delivery.v1';Name='AAVideoExport';Version=$version;Variant='lite';PayloadRelativePath='AAVideoExport/'+$version;BootstrapRelativePath=$bootPath;BootstrapSha256=$bootHash;PluginSha256=Get-AaveHash (Join-Path $payload 'AAVideoExport.dll');CoreSha256=Get-AaveHash (Join-Path $payload 'AAVideoExport.Core.dll')}
    if(!$legacy){$descriptor.AuditMode=$audit;$descriptor.BuildConfiguration=if($audit){'DevelopmentAudit'}else{'Release'}}
    Write-AaveJson (Join-Path $root 'delivery-manifest.json') $descriptor;Checksums $root;return $root
}
$production=New-Package 'production-package' $false;$audit=New-Package 'audit-package' $true;$legacy=New-Package 'legacy-package' $true $true
function New-Installation([string]$name){
    $root=Join-Path $evidence ('fixture-'+$name+'-'+[guid]::NewGuid().ToString('N'))
    foreach($folder in @('BepInEx/patchers','profiles/Default','mods/Other/1.0.0','mods/AAVideoExport/0.2.8')){New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force|Out-Null}
    Copy-Item -LiteralPath (Get-Command dotnet -CommandType Application).Source -Destination (Join-Path $root 'AzureArchive.exe')
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/patchers/ModTheAzureArchive.dll'),'SYNTHETIC HOST STRUCTURE ONLY')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),"Default`r`n")
    [IO.File]::WriteAllText((Join-Path $root 'profiles/Default/modconfig.json'),"{`r`n `"EnabledMods`":[{`"name`":`"Other`",`"version`":`"1.0.0`"},{`"name`":`"AAVideoExport`",`"version`":`"0.2.8`"}],`"unrelated`":17`r`n}",[Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText((Join-Path $root 'mods/Other/1.0.0/preserve.bin'),'OTHER MOD UNCHANGED');return $root
}
function Check([bool]$ok,[string]$message){if(!$ok){throw $message}}
function Reject([scriptblock]$action,[string]$pattern){try{&$action|Out-Null}catch{if($_.Exception.ToString()-notmatch$pattern){throw "Wrong rejection: $_"};return};throw 'Expected rejection was absent.'}
$passed=0;$failed=0;$results=[Collections.Generic.List[object]]::new()
function Test([string]$name,[scriptblock]$action){try{&$action;$script:passed++;$results.Add(@{Name=$name;Passed=$true});'PASS '+$name}catch{$script:failed++;$results.Add(@{Name=$name;Passed=$false;Error=$_.Exception.ToString()});'FAIL '+$name+': '+$_}}
function Install([string]$aa,[string]$package,[string]$hash=''){Invoke-AaveInstallPlan (New-AaveInstallPlan $aa $package $hash)}
Test 'production descriptor accepts no audit bootstrap' {$p=Read-AaveDelivery $production;Check (!$p.AuditMode-and!$p.Bootstrap) 'Production package still requires audit bootstrap.'}
Test 'production install never adds bootstrap and rollback preserves original profile bytes' {
    $aa=New-Installation 'normal';$profile=Join-Path $aa 'profiles/Default/modconfig.json';$before=Get-AaveHash $profile;$other=Get-AaveHash (Join-Path $aa 'mods/Other/1.0.0/preserve.bin')
    $r=Install $aa $production;Check (!(Test-Path -LiteralPath $r.Bootstrap)) 'Production installed an audit bootstrap.'
    $null=Undo-AaveInstallReceipt $r.Receipt;Check ((Get-AaveHash $profile)-eq$before-and(Get-AaveHash (Join-Path $aa 'mods/Other/1.0.0/preserve.bin'))-eq$other) 'Production rollback altered protected bytes.'
}
Test 'production removes reviewed legacy bootstrap and rollback restores exact bytes' {
    $aa=New-Installation 'legacyboot';$boot=Join-Path $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll';Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -Destination $boot;$sha=Get-AaveHash $boot
    $r=Install $aa $production $sha;Check (!(Test-Path -LiteralPath $boot)) 'Production retained the legacy audit bootstrap.'
    $receipt=Get-Content -LiteralPath $r.Receipt -Raw|ConvertFrom-Json;Check ($receipt.BootstrapExisted-and$receipt.AuditMode-eq$false) 'Removal ownership receipt missing.'
    Check ((Get-AaveHash (Join-Path (Split-Path -Parent $r.Receipt) 'bootstrap.original.dll'))-eq$sha) 'Original bootstrap backup differs.'
    $null=Undo-AaveInstallReceipt $r.Receipt;Check ((Get-AaveHash $boot)-eq$sha) 'Rollback failed to restore exact owned legacy bootstrap.'
}
Test 'production refuses an unreviewed existing bootstrap before any changes' {
    $aa=New-Installation 'unreviewed';$boot=Join-Path $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll';Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -Destination $boot;$sha=Get-AaveHash $boot
    Reject {Install $aa $production} 'exact reviewed SHA';Check ((Get-AaveHash $boot)-eq$sha-and!(Test-Path -LiteralPath (Join-Path $aa ('mods/AAVideoExport/'+$version)))) 'Unreviewed bootstrap rejection changed installation.'
}
Test 'production refuses foreign assembly at bootstrap path even with exact reviewed hash' {
    $aa=New-Installation 'foreign';$boot=Join-Path $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll';Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.dll') -Destination $boot;$sha=Get-AaveHash $boot
    Reject {Install $aa $production $sha} 'owned AAVE assembly';Check ((Get-AaveHash $boot)-eq$sha) 'Foreign bootstrap was removed.'
}
Test 'production launcher strips all ambient audit and test flags without setting any' {
    $aa=New-Installation 'launch';$r=Install $aa $production;$names=@('AA_VIDEOEXPORT_RUNTIME_RECIPES','AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','AA_VIDEOEXPORT_RUNTIME_FUTURE','AA_VIDEOEXPORT_STARTUP_TRACE_FILE','AA_VIDEOEXPORT_TEST_QUIT','AA_VIDEOEXPORT_TEST_STORY','AA_VIDEOEXPORT_NATIVE_TESTS','AA_VIDEOEXPORT_FIXTURE_WORKSPACE','AA_VIDEOEXPORT_LEGACY_BENCHMARK','AA_VIDEOEXPORT_BENCHMARK_MODE','AA_VIDEOEXPORT_BENCHMARK_CONTEXT_JSON');$old=@{}
    try{foreach($n in $names){$old[$n]=[Environment]::GetEnvironmentVariable($n,'Process');[Environment]::SetEnvironmentVariable($n,'1','Process')};$plan=& $r.Launcher -PlanOnly;Check (!$plan.AuditMode-and$plan.EnvironmentSet.Count-eq0-and!$plan.ChildHasTestEnvironment-and!$plan.ChildHasAuditEnvironment) 'Ordinary child still activates runtime auditing.';foreach($n in $names){Check ($plan.EnvironmentRemoved-contains$n) ('Ambient audit/test flag retained: '+$n);Check ([Environment]::GetEnvironmentVariable($n,'Process')-eq'1') 'Launcher modified parent environment.'}}
    finally{foreach($n in $old.Keys){[Environment]::SetEnvironmentVariable($n,$old[$n],'Process')}}
    Reject {& $r.Launcher -PlanOnly -DevelopmentAudit} 'production|audit';$null=Undo-AaveInstallReceipt $r.Receipt
}
Test 'new audit package requires explicit development launcher option' {
    $aa=New-Installation 'devaudit';$r=Install $aa $audit;Reject {& $r.Launcher -PlanOnly} 'DevelopmentAudit|explicit';$plan=& $r.Launcher -PlanOnly -DevelopmentAudit
    Check ($plan.AuditMode-and$plan.EnvironmentSet.AA_VIDEOEXPORT_RUNTIME_RECIPES-eq'1'-and$plan.EnvironmentSet.AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION-eq'1') 'Explicit development audit environment missing.'
    $null=Undo-AaveInstallReceipt $r.Receipt
}
Test 'legacy audited descriptor and old receipt without AuditMode remain rollback compatible' {
    $p=Read-AaveDelivery $legacy;Check $p.AuditMode 'Legacy audited descriptor was reinterpreted as production.'
    $aa=New-Installation 'oldreceipt';$profile=Join-Path $aa 'profiles/Default/modconfig.json';$before=Get-AaveHash $profile;$r=Install $aa $legacy
    $receipt=Get-Content -LiteralPath $r.Receipt -Raw|ConvertFrom-Json;$receipt.PSObject.Properties.Remove('AuditMode');Write-AaveJson $r.Receipt $receipt
    $null=Undo-AaveInstallReceipt $r.Receipt;Check ((Get-AaveHash $profile)-eq$before-and!(Test-Path -LiteralPath $r.Bootstrap)) 'Old receipt rollback behavior changed.'
}
Test 'production descriptor refuses audited core despite no bootstrap file' {
    $root=New-Package 'mismatched-production-package' $false;$path=Join-Path $root ('AAVideoExport/'+$version+'/AAVideoExport.Core.dll');Copy-Item -LiteralPath (Join-Path $binary 'audit/AAVideoExport.Core.dll') -Destination $path -Force
    $d=Get-Content -LiteralPath (Join-Path $root 'delivery-manifest.json') -Raw|ConvertFrom-Json;$d.CoreSha256=Get-AaveHash $path;Write-AaveJson (Join-Path $root 'delivery-manifest.json') $d;Checksums $root;Reject {Read-AaveDelivery $root} 'audit|build mode'
}
Test 'production rollback refuses a newly appeared foreign bootstrap' {
    $aa=New-Installation 'latebootstrap';$r=Install $aa $production;Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.dll') -Destination $r.Bootstrap;$sha=Get-AaveHash $r.Bootstrap
    Reject {Undo-AaveInstallReceipt $r.Receipt} 'bootstrap changed';Check ((Get-AaveHash $r.Bootstrap)-eq$sha) 'Rollback deleted a foreign later bootstrap.'
}
Test 'production launcher independently refuses an audited Core re-described as production' {
    $aa=New-Installation 'launchmodemismatch';$r=Install $aa $audit;$descriptor=Join-Path $aa ('AAVideoExport-install-'+$version+'.json');$before=[IO.File]::ReadAllBytes($descriptor);$boot=[IO.File]::ReadAllBytes($r.Bootstrap)
    try{$d=Get-Content -LiteralPath $descriptor -Raw|ConvertFrom-Json;$d.AuditMode=$false;$d.BootstrapSha256=$null;Write-AaveJson $descriptor $d;Remove-Item -LiteralPath $r.Bootstrap;Reject {& $r.Launcher -PlanOnly} 'Core.*mode|audit build'}
    finally{[IO.File]::WriteAllBytes($descriptor,$before);[IO.File]::WriteAllBytes($r.Bootstrap,$boot)}
    $null=Undo-AaveInstallReceipt $r.Receipt
}
Test 'failure after legacy bootstrap removal restores bootstrap and profile transactionally' {
    $aa=New-Installation 'rollbackremoved';$boot=Join-Path $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll';Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -Destination $boot;$sha=Get-AaveHash $boot
    $plan=New-AaveInstallPlan $aa $production $sha;$profileBefore=Get-AaveHash $plan.Profile;$originalAtomic=${function:Set-AaveAtomicBytes};$state=@{Pending=$true;SawAbsent=$false}
    function Set-AaveAtomicBytes([string]$Path,[byte[]]$Bytes){
        if($Path-eq$plan.Profile-and$state.Pending){$state.Pending=$false;$state.SawAbsent=!(Test-Path -LiteralPath $boot);throw [IO.IOException]::new('injected post-bootstrap-removal profile failure')}
        & $originalAtomic $Path $Bytes
    }
    try{Reject {Invoke-AaveInstallPlan $plan} 'injected post-bootstrap-removal profile failure'}finally{Set-Item Function:\Set-AaveAtomicBytes $originalAtomic}
    Check ($state.SawAbsent-and(Get-AaveHash $boot)-eq$sha-and(Get-AaveHash $plan.Profile)-eq$profileBefore-and!(Test-Path -LiteralPath $plan.Target)) 'Post-removal failure did not restore exact owned bootstrap/profile bytes.'
}
Test 'production package rejects any hidden audit bootstrap payload' {
    $root=New-Package 'hidden-bootstrap-package' $false;Copy-Item -LiteralPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -Destination (Join-Path $root ('AAVideoExport/'+$version+'/AAVideoExport.RuntimeBootstrap.dll'));Checksums $root;Reject {Read-AaveDelivery $root} 'forbidden audit bootstrap'
}
Test 'actual production package entrypoint omits bootstrap despite inherited audit flags' {
    $out=Join-Path $evidence 'packaged-production';$oldRecipe=[Environment]::GetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_RECIPES','Process');$oldAttribution=[Environment]::GetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','Process')
    try{[Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_RECIPES','1','Process');[Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','1','Process');$result=& (Join-Path $tools 'Package-Release.ps1') -AAInstallPath 'F:\AzureArchive_101' -SkipBuild -BuiltAssembliesDirectory (Join-Path $production ('AAVideoExport/'+$version)) -OutputDirectory $out|ConvertFrom-Json}
    finally{[Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_RECIPES',$oldRecipe,'Process');[Environment]::SetEnvironmentVariable('AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION',$oldAttribution,'Process')}
    Check (!$result.AuditMode-and!$result.Bootstrap-and!(Test-Path -LiteralPath (Join-Path $result.PackageRoot 'bootstrap'))) 'Production packager emitted an audit bootstrap.'
    & (Join-Path $tools 'Test-ReleaseArchive.ps1') -Archive $result.Zip -Variant lite|Out-Null
}
Test 'actual audit package entrypoint uses separate explicit development bundle' {
    $result=& (Join-Path $tools 'Package-Release.ps1') -AAInstallPath 'F:\AzureArchive_101' -SkipBuild -DevelopmentAudit -BuiltAssembliesDirectory (Join-Path $audit ('AAVideoExport/'+$version)) -BootstrapAssemblyPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -OutputDirectory (Join-Path $evidence 'packaged-audit')|ConvertFrom-Json
    Check ($result.AuditMode-and$result.BuildConfiguration-eq'DevelopmentAudit'-and$result.Zip-like'*-development-audit-bundle.zip'-and(Test-Path -LiteralPath $result.Bootstrap)) 'Explicit development bundle lost its audit separation.'
    & (Join-Path $tools 'Test-ReleaseArchive.ps1') -Archive $result.Zip -Variant lite -DevelopmentAudit|Out-Null
}
Test 'normal package entrypoint refuses a requested bootstrap rather than silently bundling it' {
    Reject {& (Join-Path $tools 'Package-Release.ps1') -AAInstallPath 'F:\AzureArchive_101' -SkipBuild -BuiltAssembliesDirectory (Join-Path $production ('AAVideoExport/'+$version)) -BootstrapAssemblyPath (Join-Path $binary 'AAVideoExport.RuntimeBootstrap.dll') -OutputDirectory (Join-Path $evidence 'forbidden-production-bootstrap')} 'Production bundles do not accept'
}
Test 'unchanged historical audited launcher and rollback tools remain usable from an old bundle' {
    $root=New-Package 'historical-tools-package' $true $true
    foreach($file in @('Install-Mod.ps1','Rollback-Mod.ps1','Deployment.Common.ps1','Start-AA-AAVE.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('fixtures/legacy-audited-delivery-v1/'+$file)) -Destination (Join-Path $root $file) -Force};Checksums $root
    $aa=New-Installation 'historicaltools';$profile=Join-Path $aa 'profiles/Default/modconfig.json';$before=Get-AaveHash $profile;$r=Install $aa $root
    Check (![IO.File]::ReadAllText($r.LauncherCmd).Contains('-DevelopmentAudit')) 'Historical launcher received a switch its original parameter contract never supported.'
    $plan=& $r.Launcher -PlanOnly;Check ($plan.EnvironmentSet.AA_VIDEOEXPORT_RUNTIME_RECIPES-eq'1') 'Explicit legacy package lost its historical audited launch semantics.'
    $null=& $r.Rollback -ReceiptPath $r.Receipt|ConvertFrom-Json;Check ((Get-AaveHash $profile)-eq$before-and!(Test-Path -LiteralPath $r.Bootstrap)) 'Historical copied rollback tools no longer restore legacy receipts.'
}
Write-AaveJson (Join-Path $evidence 'results.json') ([ordered]@{Passed=$passed;Failed=$failed;Tests=@($results.ToArray());ProductionPackage=$production;AuditPackage=$audit;LegacyPackage=$legacy;Scope='Synthetic file transactions and plan-only launch checks. No real AA installation or native process was changed.'})
"RESULT $passed passed; $failed failed; synthetic production/audit separation"
if($failed){exit 1}
