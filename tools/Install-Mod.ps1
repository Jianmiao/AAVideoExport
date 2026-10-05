param([Parameter(Mandatory=$true)][string]$AAInstallPath)
$ErrorActionPreference = 'Stop'
$aaRoot = (Resolve-Path -LiteralPath $AAInstallPath).Path
if (!(Test-Path -LiteralPath (Join-Path $aaRoot 'AzureArchive.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $aaRoot 'BepInEx\patchers\ModTheAzureArchive.dll'))) {
    throw 'Expected an AA installation with BepInEx 6 and ModTheAzureArchive.'
}
if (Get-Process -Name AzureArchive -ErrorAction SilentlyContinue) { throw 'Close AA before installing this mod.' }
$payload = Join-Path $PSScriptRoot 'AAVideoExport\0.2.4'
$distributionRoot = $PSScriptRoot
if (!(Test-Path -LiteralPath $payload -PathType Container)) {
    $distributionRoot = Split-Path -Parent $PSScriptRoot
    $payload = Join-Path $distributionRoot '0.2.4'
}
if (!(Test-Path -LiteralPath $payload -PathType Container)) { throw 'Run this helper from a repository checkout or an extracted release package.' }
$target = [IO.Path]::GetFullPath((Join-Path $aaRoot 'mods\AAVideoExport\0.2.4'))
if (!$target.StartsWith($aaRoot.TrimEnd('\') + '\mods\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid install target.' }
if (Test-Path -LiteralPath $target) { throw 'This version already exists. No file was overwritten.' }
$allowed = @('AAVideoExport.dll','AAVideoExport.Core.dll','manifest.json','icon.png','Vortice.Direct3D11.dll','Vortice.DXGI.dll',
    'Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll')
foreach($name in $allowed) { if (!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Package missing $name" } }
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md','docs\third-party')) {
    if (!(Test-Path -LiteralPath (Join-Path $distributionRoot $name))) { throw "Package missing license notices: $name" }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach($name in $allowed) { Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $target $name) }
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $distributionRoot $name) -Destination (Join-Path $target $name)
}
$noticeTarget = Join-Path $target 'docs'
New-Item -ItemType Directory -Path $noticeTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $distributionRoot 'docs\third-party') -Destination $noticeTarget -Recurse
Write-Output 'Installed AAVideoExport 0.2.4. Enable it in the AA mod manager when ready. No profile or project settings were changed.'
