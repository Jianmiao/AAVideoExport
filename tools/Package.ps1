#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AAInstallPath,
    [string]$OutputDirectory,
    [string]$BuiltAssembliesDirectory,
    [string]$BootstrapAssemblyPath,
    [ValidateSet('lite','full')][string]$Variant='lite',
    [string]$FFmpegDirectory,
    [string]$FFmpegNoticesDirectory
)
$arguments=@{AAInstallPath=$AAInstallPath;Variant=$Variant}
foreach($name in @('OutputDirectory','BuiltAssembliesDirectory','BootstrapAssemblyPath','FFmpegDirectory','FFmpegNoticesDirectory')){
    $value=Get-Variable -Name $name -ValueOnly
    if($value){$arguments[$name]=$value}
}
if($BuiltAssembliesDirectory){$arguments.SkipBuild=$true}
& (Join-Path $PSScriptRoot 'Package-Release.ps1') @arguments
if(!$?){throw 'Versioned AAVE bundle packaging failed.'}
