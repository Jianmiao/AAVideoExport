#requires -Version 7.2
param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
$package=Read-AaveDelivery $PackageRoot
$dlls=@(Get-ChildItem -LiteralPath $package.Payload -File -Recurse -Filter '*.dll')
if($dlls.Count -ne 9){throw 'Runtime payload must contain exactly the nine reviewed DLLs; the early bootstrap belongs outside the Mod directory.'}
Write-Output ('PASS versioned delivery layout '+$package.Descriptor.Version+': nine runtime DLLs, separate owned bootstrap, launcher/rollback, exact versions and complete checksums. A source clone alone is not an installable controlled-runtime bundle.')
