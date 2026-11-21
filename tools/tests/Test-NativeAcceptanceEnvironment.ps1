#requires -Version 7.2
$ErrorActionPreference='Stop'
$tools=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $tools 'NativeAcceptance.Environment.ps1'
if(Test-Path -LiteralPath $helper){. $helper}
$passed=0;$failed=0
function Check([bool]$value,[string]$message){if(!$value){throw $message}}
function Test([string]$name,[scriptblock]$action){try{&$action;$script:passed++;'PASS '+$name}catch{$script:failed++;'FAIL '+$name+': '+$_}}
Test 'production native child drops inherited audit and startup trace flags' {
    $start=[Diagnostics.ProcessStartInfo]::new();foreach($name in @('AA_VIDEOEXPORT_RUNTIME_RECIPES','AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION','AA_VIDEOEXPORT_RUNTIME_FUTURE','AA_VIDEOEXPORT_STARTUP_TRACE_FILE')){$start.Environment[$name]='1'}
    $result=Set-AaveNativeAuditEnvironment -StartInfo $start
    Check (!$result.DevelopmentAudit-and$result.ChildAuditVariables.Count-eq0-and$start.Environment['AA_VIDEOEXPORT_TEST_DEVELOPMENT_AUDIT']-eq'0') 'Production native child retained audit flags.'
}
Test 'development audit native child requires explicit opt-in and pins exactly two audit flags' {
    $start=[Diagnostics.ProcessStartInfo]::new();$start.Environment['AA_VIDEOEXPORT_STARTUP_TRACE_FILE']='unexpected-inherited-path'
    $result=Set-AaveNativeAuditEnvironment -StartInfo $start -DevelopmentAudit
    Check ($result.DevelopmentAudit-and$result.ChildAuditVariables.Count-eq2-and$start.Environment['AA_VIDEOEXPORT_RUNTIME_RECIPES']-eq'1'-and$start.Environment['AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION']-eq'1'-and!$start.Environment.ContainsKey('AA_VIDEOEXPORT_STARTUP_TRACE_FILE')) 'Explicit audit native child contract differs.'
}
Test 'ordinary native runner plan is read-only and uses the production child contract' {
    $runner=Join-Path $tools 'Run-NativeAcceptance.ps1';$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
    Check (@($ast.ParamBlock.Parameters|Where-Object {$_.Name.VariablePath.UserPath-eq'PlanOnly'}).Count-eq1) 'Native runner lacks a read-only launch-plan seam.'
    $root=Join-Path ([IO.Path]::GetTempPath()) ('aave-native-environment-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
    try{[IO.File]::WriteAllText((Join-Path $root 'aave-test-lab.json'),'{}');$output=Join-Path $root 'not-created';$plan=& $runner -LabRoot $root -OutputDirectory $output -PlanOnly
        Check (!$plan.DevelopmentAudit-and$plan.ChildAuditVariables.Count-eq0-and!(Test-Path -LiteralPath $output)) 'Native production plan mutates files or retains audit flags.'
    }finally{if(!$root.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($root)-notlike'aave-native-environment-*'){throw 'Unsafe fixture cleanup path.'};Remove-Item -LiteralPath $root -Recurse -Force}
}
"RESULT $passed passed; $failed failed; launch plans only, no AA process started"
if($failed){exit 1}
