#requires -Version 7.2
param([Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][ValidateSet('lite','full')][string]$Variant,[switch]$DevelopmentAudit)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('aave-release-check-'+[guid]::NewGuid().ToString('N'))
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try{
    $seen=@{}
    foreach($entry in $zip.Entries){
        $path=$entry.FullName
        if(!$path-or$path.StartsWith('/')-or$path.Contains('\')-or$path.Contains(':')-or($path -split '/') -contains '..'-or$seen.ContainsKey($path)){throw 'Unsafe or duplicate ZIP entry.'}
        $seen[$path]=$true
        if($path -match '(?i)(BepInEx/|interop/|NativePath\.Tests|local\.props|\.aap2?$|\.aas$|\.cfg$|\.pdb$|\.log$|\.sqlite|(^|/)(bin|obj)/)'){throw "Forbidden host/project/test release content: $path"}
    }
}finally{$zip.Dispose()}
try{
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Archive).Path,$temp)
    $package=Read-AaveDelivery $temp
    if($package.Descriptor.Variant -ne $Variant){throw 'Bundle variant differs from requested check.'}
    if($package.AuditMode-ne[bool]$DevelopmentAudit){throw 'Bundle audit mode differs from the explicitly requested release check.'}
    foreach($file in Get-ChildItem -LiteralPath $temp -Recurse -File){
        $relative=[IO.Path]::GetRelativePath($temp,$file.FullName).Replace('\','/')
        $allowed=$relative.StartsWith($package.Descriptor.PayloadRelativePath+'/',[StringComparison]::Ordinal)-or$relative -in @('bootstrap/AAVideoExport.RuntimeBootstrap.dll','Install-Mod.ps1','Rollback-Mod.ps1','Deployment.Common.ps1','Start-AA-AAVE.ps1','README.md','CHANGELOG.md','LICENSE','THIRD_PARTY_NOTICES.md','delivery-manifest.json','SHA256SUMS.txt','docs/development.md','docs/render-control-v1.md','docs/auto-selection-export.md','docs/moreeffects-clock-compatibility.md','docs/shader-provenance-2026-10-03.md')
        if(!$allowed){throw "Unowned bundle entry: $relative"}
    }
    if($Variant -eq 'full'){
        foreach($name in @('tools/ffmpeg.exe','tools/ffprobe.exe','licenses/FFmpeg/LICENSE.txt')){if(!(Test-Path -LiteralPath (Join-Path $package.Payload $name))){throw "Missing full bundle entry: $name"}}
    }elseif(@(Get-ChildItem -LiteralPath $package.Payload -File -Recurse|Where-Object Extension -eq '.exe').Count){throw 'Lite bundle unexpectedly contains an executable.'}
    Write-Output ('PASS '+$Variant+' versioned bundle '+$package.Descriptor.Version+'; DevelopmentAudit='+$package.AuditMode+': runtime/core build mode, conditional audit bootstrap, launcher/rollback and complete checksums verified; no host/test/project files.')
}finally{
    if(Test-Path -LiteralPath $temp){
        $resolved=[IO.Path]::GetFullPath($temp);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
        if(!$resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($resolved)-notlike'aave-release-check-*'){throw 'Unexpected release-check cleanup target.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
