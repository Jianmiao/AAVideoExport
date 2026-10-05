param([string]$AAInstallPath, [string]$OutputDirectory, [string]$BuiltAssembliesDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$BuiltAssembliesDirectory) {
    $build = @('build',(Join-Path $repo 'src\VideoExport.Plugin\VideoExport.Plugin.csproj'),'-c','Release','--nologo','-v:q')
    if ($AAInstallPath) { $build += ('-p:AAInstallPath=' + (Resolve-Path -LiteralPath $AAInstallPath).Path) }
    & dotnet @build
    if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
}
$releaseParent = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'artifacts' }
New-Item -ItemType Directory -Path $releaseParent -Force | Out-Null
$manifestPath = Join-Path $repo 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.name -ne 'AAVideoExport' -or $manifest.version_number -ne '0.2.3') {
    throw 'Package layout expects the AAVideoExport 0.2.3 manifest.'
}
$release = Join-Path $releaseParent ('AAVideoExport-' + $manifest.version_number + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + 'Z')
if (Test-Path -LiteralPath $release) { throw 'Release path exists; no existing artifact will be overwritten.' }
$payload = Join-Path $release 'AAVideoExport\0.2.3'
New-Item -ItemType Directory -Path $payload | Out-Null
$buildOutput = if ($BuiltAssembliesDirectory) { (Resolve-Path -LiteralPath $BuiltAssembliesDirectory).Path } else { Join-Path $repo 'src\VideoExport.Plugin\bin\Release\net6.0' }
foreach($name in @('AAVideoExport.dll','AAVideoExport.Core.dll','Vortice.Direct3D11.dll','Vortice.DXGI.dll',
    'Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll')) {
    Copy-Item -LiteralPath (Join-Path $buildOutput $name) -Destination (Join-Path $payload $name)
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $payload 'manifest.json')
foreach($name in @('README.md','THIRD_PARTY_NOTICES.md','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $release $name)
}
Copy-Item -LiteralPath (Join-Path $repo 'tools\Install-Mod.ps1') -Destination (Join-Path $release 'Install-Mod.ps1')
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination (Join-Path $release 'docs') -Recurse

# Reuse the authored icon shipped in the clone-ready installation tree.
Copy-Item -LiteralPath (Join-Path $repo '0.2.3\icon.png') -Destination (Join-Path $payload 'icon.png')

$hashRows = Get-ChildItem -LiteralPath $release -Recurse -File | Sort-Object FullName | ForEach-Object {
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetRelativePath($release,$_.FullName).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'),$hashRows,[Text.UTF8Encoding]::new($false))
$zip = $release + '.zip'
Compress-Archive -LiteralPath $release -DestinationPath $zip
Write-Output $zip
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
