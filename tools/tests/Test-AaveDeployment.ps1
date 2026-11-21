#requires -Version 7.2
param([Parameter(Mandatory=$true)][string]$PackageRoot,[string]$EvidenceRoot,[string]$LegacyBootstrapFixture)
$ErrorActionPreference='Stop'
$tools=Split-Path -Parent $PSScriptRoot
. (Join-Path $tools 'Deployment.Common.ps1')
# The installer operates only on generated artifact fixture trees below. Mock
# process discovery in this test scope; never stop a real user AA process.
function Get-Process { [CmdletBinding()]param([string]$Name) if($Name-ne'AzureArchive'){throw 'Unexpected process query in synthetic fixture.'} }
$package=Read-AaveDelivery $PackageRoot;$version=$package.Descriptor.Version
if(!$LegacyBootstrapFixture){$LegacyBootstrapFixture=if($package.Bootstrap){$package.Bootstrap}else{Join-Path (Split-Path -Parent $tools) 'src/VideoExport.RuntimeBootstrap/bin/Release/net6.0/AAVideoExport.RuntimeBootstrap.dll'}}
$legacyBootstrap=(Resolve-Path -LiteralPath $LegacyBootstrapFixture).Path
if(!$EvidenceRoot){$EvidenceRoot=Join-Path (Split-Path -Parent $tools) 'artifacts/delivery-tools-20261030/install-tests'}
$evidence=[IO.Path]::GetFullPath($EvidenceRoot)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $tools) 'artifacts')).TrimEnd('\')+'\'
if(!$evidence.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Synthetic deployment evidence must stay inside checkout artifacts.'}
$null=Get-AaveSafePath (Split-Path -Parent $tools) ([IO.Path]::GetRelativePath((Split-Path -Parent $tools),$evidence))
New-Item -ItemType Directory -Path $evidence -Force|Out-Null
$results=[Collections.Generic.List[object]]::new();$script:passed=0;$script:failed=0
function Check([bool]$ok,[string]$message){if(!$ok){throw $message}}
function Reject([scriptblock]$action,[string]$pattern){try{&$action|Out-Null}catch{if($_.Exception.ToString()-notmatch$pattern){throw "Wrong refusal: $_"};return};throw 'Expected refusal did not occur.'}
function New-Fixture([string]$name){
    $root=Join-Path $evidence ('fixture-'+$name+'-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
    foreach($dir in @('BepInEx/patchers','profiles/Default/configs','mods/AAVideoExport/0.2.8','mods/Other/1.0.0','data')){New-Item -ItemType Directory -Path (Join-Path $root $dir) -Force|Out-Null}
    # A real system PE is a structural fixture only. It is never executed or
    # represented as an AA runtime, and no native-acceptance marker is created.
    Copy-Item -LiteralPath (Get-Command dotnet -CommandType Application).Source -Destination (Join-Path $root 'AzureArchive.exe')
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/patchers/ModTheAzureArchive.dll'),'SYNTHETIC INSTALLER STRUCTURE ONLY')
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),"Default`r`n")
    [IO.File]::WriteAllText((Join-Path $root 'profiles/Default/modconfig.json'),"{`r`n  `"EnabledMods`": [{`"name`":`"Other`",`"version`":`"1.0.0`"},{`"name`":`"AAVideoExport`",`"version`":`"0.2.8`"}],`r`n  `"UnrelatedOption`": 17`r`n}`r`n",[Text.UTF8Encoding]::new($true))
    foreach($file in @('mods/AAVideoExport/0.2.8/keep.bin','mods/Other/1.0.0/keep.bin','profiles/Default/configs/other.cfg','data/user-story.aas')){[IO.File]::WriteAllText((Join-Path $root $file),'preserve original bytes '+$file)}
    return $root
}
function Snapshot([string]$root){@('ActiveProfile.txt','profiles/Default/modconfig.json','BepInEx/patchers/ModTheAzureArchive.dll','mods/AAVideoExport/0.2.8/keep.bin','mods/Other/1.0.0/keep.bin','profiles/Default/configs/other.cfg','data/user-story.aas')|ForEach-Object{[pscustomobject]@{Path=Join-Path $root $_;Hash=Get-AaveHash (Join-Path $root $_)}}}
function Unchanged($snapshot){foreach($item in $snapshot){Check ((Get-AaveHash $item.Path)-eq$item.Hash) ('Protected fixture file changed: '+$item.Path)}}
function Install([string]$root,[string]$oldHash=''){
    $arguments=@{AAInstallPath=$root;PackageRoot=$package.Root};if($oldHash){$arguments.ExpectedExistingBootstrapSha256=$oldHash}
    $json=& (Join-Path $tools 'Install-Mod.ps1') @arguments
    return ($json|ConvertFrom-Json -Depth 100)
}
function Rollback($installed){& $installed.Rollback -ReceiptPath $installed.Receipt|ConvertFrom-Json -Depth 100}
function Test([string]$name,[scriptblock]$action){
    $root=New-Fixture ($name-replace'[^A-Za-z0-9]','')
    try{&$action $root;$script:passed++;$results.Add([pscustomobject]@{Name=$name;Passed=$true});Write-Output ('PASS '+$name)}
    catch{$script:failed++;$results.Add([pscustomobject]@{Name=$name;Passed=$false;Error=$_.Exception.ToString();Fixture=$root});Write-Output ('FAIL '+$name+': '+$_)}
    finally{
        if($script:failed -eq 0){
            $resolved=[IO.Path]::GetFullPath($root);if(!$resolved.StartsWith($evidence.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($resolved)-notlike'fixture-*'){throw 'Unsafe test cleanup path'}
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
Test 'read-only install plan leaves the synthetic tree unchanged' {
    param($root);$before=Snapshot $root;$count=@(Get-ChildItem -LiteralPath $root -File -Recurse).Count
    $plan=& (Join-Path $tools 'Install-Mod.ps1') -AAInstallPath $root -PackageRoot $package.Root -PlanOnly|ConvertFrom-Json
    Check ($plan.State-eq'ReadOnlyPlan'-and$plan.Version-eq$version) 'Read-only plan is not version aware.'
    Unchanged $before;Check (@(Get-ChildItem -LiteralPath $root -File -Recurse).Count-eq$count) 'Plan created deployment files.'
}
Test 'real install and installed rollback restore exact original profile bytes' {
    param($root);$before=Snapshot $root;$result=Install $root
    Check ($result.State-eq'Installed'-and(Test-Path -LiteralPath (Join-Path $result.Target 'AAVideoExport.dll'))) 'Runtime payload was not installed.'
    $profile=Get-Content -LiteralPath (Join-Path $root 'profiles/Default/modconfig.json') -Raw|ConvertFrom-Json
    Check ($profile.EnabledMods[0].version-eq'1.0.0'-and$profile.EnabledMods[1].version-eq$version-and$profile.UnrelatedOption-eq17) 'Profile mutation changed more than AAVE.'
    $plan=& $result.Launcher -PlanOnly -DevelopmentAudit:$package.AuditMode
    Check ($plan.Version-eq$version) 'Installed process launcher cannot validate installed descriptor.'
    $command=[IO.File]::ReadAllText($result.LauncherCmd)
    Check ($command.Contains((Join-Path $PSHOME 'pwsh.exe'))-and$command.Contains('%~dp0Start-AA-AAVE-'+$version+'.ps1')-and$command-notmatch'(?i)Bypass|ExecutionPolicy') 'Command shortcut does not use the verified interpreter and exact script safely.'
    $receipt=Get-Content -LiteralPath $result.Receipt -Raw|ConvertFrom-Json -Depth 100
    Check (@($receipt.NewFiles|Where-Object RelativePath -eq ('Start-AA-AAVE-'+$version+'.cmd')).Count-eq1) 'Command shortcut was not included in rollback/hash ownership.'
    $undo=Rollback $result;Check ($undo.State-eq'RolledBack') 'Installed rollback did not run.'
    Unchanged $before;Check (!(Test-Path -LiteralPath $result.Target)-and!(Test-Path -LiteralPath $result.Bootstrap)-and!(Test-Path -LiteralPath $result.LauncherCmd)) 'New version, command shortcut or newly added bootstrap remains.'
}
Test 'atomic journal replacement retains the old readable complete image' {
    param($root);$journal=Join-Path $root 'journal.json';Write-AaveJson $journal ([ordered]@{State='Original'})
    $reader=[IO.FileStream]::new($journal,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read-bor[IO.FileShare]::Delete)
    try{
        Write-AaveJson $journal ([ordered]@{State='Updated'})
        $text=[IO.StreamReader]::new($reader,[Text.Encoding]::UTF8,$true,1024,$true)
        try{$old=$text.ReadToEnd()|ConvertFrom-Json}finally{$text.Dispose()}
        Check ($old.State-eq'Original'-and(Get-Content -LiteralPath $journal -Raw|ConvertFrom-Json).State-eq'Updated') 'Journal replacement truncated an existing reader or published incomplete data.'
    }finally{$reader.Dispose()}
    Check (@(Get-ChildItem -LiteralPath $root -Filter '*.aave-*.tmp' -File).Count-eq0) 'Atomic journal temporary file remains.'
}
Test 'active profile switch after planning refuses before any install writes' {
    param($root);$plan=New-AaveInstallPlan $root $package.Root;$before=Get-AaveHash $plan.Profile
    [IO.File]::WriteAllText((Join-Path $root 'ActiveProfile.txt'),'OtherProfile')
    Reject {Invoke-AaveInstallPlan $plan} 'Active profile changed after installation planning'
    Check (!(Test-Path -LiteralPath $plan.Target)-and!(Test-Path -LiteralPath $plan.Bootstrap)-and(Get-AaveHash $plan.Profile)-eq$before) 'A stale active-profile plan performed writes.'
}
Test 'independent backup recovery works after installed tools are absent' {
    param($root);$before=Snapshot $root;$result=Install $root
    Remove-Item -LiteralPath $result.Rollback,(Join-Path $root ('AAVideoExport-Deployment.Common-'+$version+'.ps1')) -Force
    $undo=& $result.RecoveryRollback -ReceiptPath $result.Receipt|ConvertFrom-Json -Depth 100
    Check ($undo.State-eq'RolledBack'-and!(Test-Path -LiteralPath $result.Target)) 'Independent recovery could not finish after installed tools disappeared.'
    Unchanged $before
}
Test 'same version install refuses before changing installed files' {
    param($root);$result=Install $root;$hash=Get-AaveHash (Join-Path $result.Target 'AAVideoExport.dll')
    Reject {Install $root} 'version already exists';Check ((Get-AaveHash (Join-Path $result.Target 'AAVideoExport.dll'))-eq$hash) 'Same version overwrote runtime.'
    $null=Rollback $result
}
Test 'existing owned bootstrap requires the reviewed exact hash' {
    param($root);Copy-Item -LiteralPath $legacyBootstrap -Destination (Join-Path $root 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll')
    $before=Snapshot $root;Reject {Install $root} 'exact reviewed SHA';Unchanged $before
    Check (!(Test-Path -LiteralPath (Join-Path $root ('mods/AAVideoExport/'+$version)))) 'Rejected bootstrap check created a version directory.'
}
Test 'owned bootstrap replacement retains bytes and rollback restores them' {
    param($root);$old=$legacyBootstrap
    Copy-Item -LiteralPath $old -Destination (Join-Path $root 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll');$oldHash=Get-AaveHash $old
    $before=Snapshot $root;$result=Install $root $oldHash
    if($package.AuditMode){Check ((Get-AaveHash $result.Bootstrap)-eq$package.Descriptor.BootstrapSha256) 'Development audit bootstrap was not installed.'}
    else{Check (!(Test-Path -LiteralPath $result.Bootstrap)) 'Production retained the old audit bootstrap.'}
    $null=Rollback $result;Check ((Get-AaveHash $result.Bootstrap)-eq$oldHash) 'Old bootstrap bytes were not restored.';Unchanged $before
}
Test 'rollback preserves later unrelated profile selections' {
    param($root);$result=Install $root;$profile=Join-Path $root 'profiles/Default/modconfig.json';$installed=[IO.File]::ReadAllBytes($profile)
    $changed=Get-Content -LiteralPath $profile -Raw|ConvertFrom-Json;$changed.EnabledMods[0].version='2.0.0';Write-AaveJson $profile $changed;$sha=Get-AaveHash $profile
    Reject {Rollback $result} 'profile changed';Check ((Get-AaveHash $profile)-eq$sha) 'Rollback overwrote a later unrelated choice.'
    [IO.File]::WriteAllBytes($profile,$installed);$null=Rollback $result
}
Test 'rollback refuses altered runtime content instead of deleting it' {
    param($root);$result=Install $root;$path=Join-Path $result.Target 'README.md';$original=[IO.File]::ReadAllBytes($path);[IO.File]::AppendAllText($path,'user note');$sha=Get-AaveHash $path
    Reject {Rollback $result} 'Installed file changed';Check ((Get-AaveHash $path)-eq$sha) 'Changed user file was deleted.'
    [IO.File]::WriteAllBytes($path,$original);$null=Rollback $result
}
Test 'rollback refuses a changed active profile before removing shared runtime files' {
    param($root);$result=Install $root;$active=Join-Path $root 'ActiveProfile.txt';$original=[IO.File]::ReadAllBytes($active)
    [IO.File]::WriteAllText($active,'OtherProfile');Reject {Rollback $result} 'Active profile changed'
    Check (Test-Path -LiteralPath (Join-Path $result.Target 'AAVideoExport.dll')) 'Changed active setup lost the new runtime.'
    [IO.File]::WriteAllBytes($active,$original);$null=Rollback $result
}
Test 'rollback rejects out-of-root receipt targets before mutation' {
    param($root);$result=Install $root;$original=[IO.File]::ReadAllBytes($result.Receipt);$receipt=Get-Content -LiteralPath $result.Receipt -Raw|ConvertFrom-Json -Depth 100
    $receipt.NewFiles[0].RelativePath='../outside';Write-AaveJson $result.Receipt $receipt;$sha=Get-AaveHash (Join-Path $root 'profiles/Default/modconfig.json')
    Reject {Rollback $result} 'Unsafe relative|outside';Check ((Get-AaveHash (Join-Path $root 'profiles/Default/modconfig.json'))-eq$sha) 'Unsafe rollback changed the profile.'
    [IO.File]::WriteAllBytes($result.Receipt,$original);$null=Rollback $result
}
Test 'copy failure after real payload writes rolls back only owned files' {
    param($root);$before=Snapshot $root;$plan=New-AaveInstallPlan $root $package.Root
    function Copy-Item {param([string]$LiteralPath,[string]$Destination)
        if([IO.Path]::GetFileName($LiteralPath)-eq'Start-AA-AAVE.ps1'){[IO.File]::WriteAllText($Destination,'owned partial content');throw [IO.IOException]::new('injected owned copy failure')}
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    Reject {Invoke-AaveInstallPlan $plan} 'injected owned copy failure';Unchanged $before
    Check (!(Test-Path -LiteralPath $plan.Target)-and!(Test-Path -LiteralPath $plan.Bootstrap)) 'Failed install left a selected payload or bootstrap.'
    Check (@(Get-ChildItem -LiteralPath $root -Recurse -File|Where-Object Name -like '*.aave-*.tmp').Count-eq0) 'Failed copy left its tracked partial temporary file.'
}
$summary=[ordered]@{Passed=$passed;Failed=$failed;PackageRoot=$package.Root;Tests=@($results.ToArray());Scope='Actual file install/rollback in synthetic owned trees only. No AA process was launched, no native-lab marker was created, and primary was not written.'}
Write-AaveJson (Join-Path $evidence 'results.json') $summary
Write-Output ("RESULT $passed passed; $failed failed; actual synthetic install/rollback writes")
if($failed){exit 1}
