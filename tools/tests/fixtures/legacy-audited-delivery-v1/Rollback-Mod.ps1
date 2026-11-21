#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReceiptPath)
$ErrorActionPreference='Stop'
if(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){throw 'Close AA before rolling back this installation; no files were changed.'}
$receipt=Get-Content -LiteralPath (Resolve-Path -LiteralPath $ReceiptPath).Path -Raw|ConvertFrom-Json -Depth 100
if($receipt.Schema -ne 'aave.install-receipt.v1'-or$receipt.Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid AAVE installation receipt.'}
$common=Join-Path $PSScriptRoot 'Deployment.Common.ps1'
if(!(Test-Path -LiteralPath $common)){$common=Join-Path $PSScriptRoot ('AAVideoExport-Deployment.Common-'+$receipt.Version+'.ps1')}
if($receipt.PSObject.Properties['RecoveryCommonSha256'] -and (Get-FileHash -LiteralPath $common -Algorithm SHA256).Hash.ToLowerInvariant() -ne $receipt.RecoveryCommonSha256){throw 'Recovery helper differs from the installation receipt; no files were changed.'}
. $common
Undo-AaveInstallReceipt -ReceiptPath $ReceiptPath|ConvertTo-Json -Depth 6
