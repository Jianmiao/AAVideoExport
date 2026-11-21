#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AAInstallPath,
    [string]$PackageRoot=$PSScriptRoot,
    [string]$ExpectedExistingBootstrapSha256,
    [switch]$PlanOnly
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
if(!$PlanOnly-and(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue)){throw 'Close AA before installing this mod; no files were changed.'}
$plan=New-AaveInstallPlan -AAInstallPath $AAInstallPath -PackageRoot $PackageRoot -ExpectedExistingBootstrapSha256 $ExpectedExistingBootstrapSha256
if($PlanOnly){
    [pscustomobject]@{State='ReadOnlyPlan';Version=$plan.Version;Target=$plan.Target;Profile=$plan.Profile;OriginalSelectedVersion=$plan.OldVersion;Bootstrap=$plan.Bootstrap;ExistingBootstrapSha256=$plan.BootstrapBefore;Launcher=$plan.Launcher;LauncherCmd=$plan.LauncherCmd;PowerShellPath=$plan.PowerShellPath;Changes='New version only; owned bootstrap with exact backup; only the AAVideoExport profile selection; no other Mod/config writes'}|ConvertTo-Json -Depth 6
}else{Invoke-AaveInstallPlan $plan|ConvertTo-Json -Depth 6}
