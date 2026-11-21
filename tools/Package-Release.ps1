#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AAInstallPath,
    [ValidateSet('lite','full')][string]$Variant='lite',
    [string]$FFmpegDirectory,
    [string]$FFmpegNoticesDirectory,
    [string]$OutputDirectory,
    [string]$BuiltAssembliesDirectory,
    [string]$BootstrapAssemblyPath,
    [switch]$DevelopmentAudit,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')
$manifest=Get-Content -LiteralPath (Join-Path $repo 'manifest.json') -Raw|ConvertFrom-Json
$version=$manifest.version_number
$configuration=if($DevelopmentAudit){'DevelopmentAudit'}else{'Release'}
if(!$DevelopmentAudit-and$BootstrapAssemblyPath){throw 'Production bundles do not accept or ship an audit bootstrap. Use explicit -DevelopmentAudit for a development-only bundle.'}
if($manifest.name -ne 'AAVideoExport'-or$version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release manifest.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts'}
$output=[IO.Path]::GetFullPath($OutputDirectory);New-Item -ItemType Directory -Path $output -Force|Out-Null
$auditSuffix=if($DevelopmentAudit){'-development-audit'}else{''}
$zip=Join-Path $output ('AAVideoExport-'+$version+'-win-x64-'+$Variant+$auditSuffix+'-bundle.zip')
if(Test-Path -LiteralPath $zip){throw 'Release ZIP already exists; use a fresh output directory.'}
if(!$SkipBuild){
    & dotnet build (Join-Path $repo 'src/VideoExport.Plugin/VideoExport.Plugin.csproj') -c $configuration --nologo ('-p:AAInstallPath='+$AAInstallPath)
    if($LASTEXITCODE -ne 0){throw 'Plugin/Core build failed.'}
    if($DevelopmentAudit-and!$BootstrapAssemblyPath){
        & dotnet build (Join-Path $repo 'src/VideoExport.RuntimeBootstrap/VideoExport.RuntimeBootstrap.csproj') -c DevelopmentAudit --nologo ('-p:AAReferenceRoot='+$AAInstallPath)
        if($LASTEXITCODE -ne 0){throw 'Owned early bootstrap build failed.'}
    }
}
$build=if($BuiltAssembliesDirectory){(Resolve-Path -LiteralPath $BuiltAssembliesDirectory).Path}else{Join-Path $repo ('src/VideoExport.Plugin/bin/'+$configuration+'/net6.0')}
if((Get-AaveAssemblyAuditMode (Join-Path $build 'AAVideoExport.Core.dll'))-ne[bool]$DevelopmentAudit){throw 'The selected Plugin/Core build does not match the requested production/development-audit package mode.'}
$bootstrap=$null
if($DevelopmentAudit){
    if(!$BootstrapAssemblyPath){$BootstrapAssemblyPath=Join-Path $repo 'src/VideoExport.RuntimeBootstrap/bin/DevelopmentAudit/net6.0/AAVideoExport.RuntimeBootstrap.dll'}
    $bootstrap=(Resolve-Path -LiteralPath $BootstrapAssemblyPath -ErrorAction Stop).Path
}
$stage=Join-Path $output ('bundle-'+$version+'-'+[guid]::NewGuid().ToString('N'))
$payload=Join-Path $stage ('AAVideoExport/'+$version)
New-Item -ItemType Directory -Path $payload|Out-Null
$runtime=@('AAVideoExport.dll','AAVideoExport.Core.dll','Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll')
foreach($name in $runtime){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $payload $name)}
Copy-Item -LiteralPath (Join-Path $repo 'manifest.json') -Destination (Join-Path $payload 'manifest.json')
Copy-Item -LiteralPath (Join-Path $repo ($version+'/icon.png')) -Destination (Join-Path $payload 'icon.png')
if($DevelopmentAudit){
    New-Item -ItemType Directory -Path (Join-Path $stage 'bootstrap')|Out-Null
    Copy-Item -LiteralPath $bootstrap -Destination (Join-Path $stage 'bootstrap/AAVideoExport.RuntimeBootstrap.dll')
}
foreach($name in @('Install-Mod.ps1','Rollback-Mod.ps1','Deployment.Common.ps1','Start-AA-AAVE.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $stage $name)}
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $stage 'README.md')
foreach($file in @('LICENSE','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $stage $file);Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $payload $file)}
$licenses=Join-Path $payload 'licenses';New-Item -ItemType Directory -Path $licenses|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $licenses
foreach($folder in @('docs/third-party','docs/licenses')){Copy-Item -LiteralPath (Join-Path $repo $folder) -Destination $licenses -Recurse}
if($Variant -eq 'full'){
    if(!$FFmpegDirectory-or!$FFmpegNoticesDirectory){throw 'Full bundle requires exact FFmpeg/ffprobe binaries and license notices.'}
    New-Item -ItemType Directory -Path (Join-Path $payload 'tools')|Out-Null
    foreach($name in @('ffmpeg.exe','ffprobe.exe')){Copy-Item -LiteralPath (Join-Path $FFmpegDirectory $name) -Destination (Join-Path $payload ('tools/'+$name))}
    Copy-Item -LiteralPath $FFmpegNoticesDirectory -Destination (Join-Path $licenses 'FFmpeg') -Recurse
    if(!(Test-Path -LiteralPath (Join-Path $licenses 'FFmpeg/LICENSE.txt'))){throw 'Full bundle is missing the FFmpeg license.'}
}
foreach($file in @('README.md','CHANGELOG.md','docs/development.md','docs/render-control-v1.md','docs/auto-selection-export.md','docs/moreeffects-clock-compatibility.md','docs/shader-provenance-2026-10-03.md')){
    $destination=Join-Path $payload $file;New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $destination
    if($file -ne 'README.md'){$rootDestination=Join-Path $stage $file;New-Item -ItemType Directory -Path (Split-Path -Parent $rootDestination) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $rootDestination}
}
function Write-Checksums([string]$Root){
    $rows=@(Get-ChildItem -LiteralPath $Root -Recurse -File|Where-Object {$_.FullName -ne (Join-Path $Root 'SHA256SUMS.txt')}|Sort-Object FullName|ForEach-Object{(Get-AaveHash $_.FullName)+'  '+[IO.Path]::GetRelativePath($Root,$_.FullName).Replace('\','/')})
    [IO.File]::WriteAllLines((Join-Path $Root 'SHA256SUMS.txt'),$rows,[Text.UTF8Encoding]::new($false))
}
Write-Checksums $payload
$bootstrapRelative=if($DevelopmentAudit){'bootstrap/AAVideoExport.RuntimeBootstrap.dll'}else{$null}
$bootstrapHash=if($DevelopmentAudit){Get-AaveHash (Join-Path $stage $bootstrapRelative)}else{$null}
$descriptor=[ordered]@{Schema='aave.delivery.v1';Name='AAVideoExport';Version=$version;Variant=$Variant;AuditMode=[bool]$DevelopmentAudit;BuildConfiguration=$configuration;PayloadRelativePath='AAVideoExport/'+$version;BootstrapRelativePath=$bootstrapRelative;PluginSha256=Get-AaveHash (Join-Path $payload 'AAVideoExport.dll');CoreSha256=Get-AaveHash (Join-Path $payload 'AAVideoExport.Core.dll');BootstrapSha256=$bootstrapHash;Startup=$(if($DevelopmentAudit){'Development-only audited build: explicit -DevelopmentAudit launcher is required.'}else{'Production execution build: no audit bootstrap; launcher removes inherited audit and test environment flags.'});NativeAcceptance='Packaging verifies file identity only; final native acceptance is recorded separately.'}
Write-AaveJson (Join-Path $stage 'delivery-manifest.json') $descriptor
Write-Checksums $stage
$null=Read-AaveDelivery $stage
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$sha=Get-AaveHash $zip;[IO.File]::WriteAllText($zip+'.sha256',$sha+'  '+[IO.Path]::GetFileName($zip)+"`n",[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Variant=$Variant;Version=$version;AuditMode=[bool]$DevelopmentAudit;BuildConfiguration=$configuration;Zip=$zip;PackageRoot=$stage;Payload=$payload;Bootstrap=$(if($DevelopmentAudit){Join-Path $stage $bootstrapRelative}else{$null});SHA256=$sha;Bytes=(Get-Item -LiteralPath $zip).Length}|ConvertTo-Json -Depth 5
