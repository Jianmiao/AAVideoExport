[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AAInstallPath,
    [ValidateSet('lite','full')][string]$Variant = 'lite',
    [string]$FFmpegDirectory,
    [string]$FFmpegNoticesDirectory,
    [string]$OutputDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.json') -Raw | ConvertFrom-Json
$version = $manifest.version_number
if ($manifest.name -ne 'AAVideoExport' -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release manifest.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output ('stage-' + $version + '-' + [guid]::NewGuid().ToString('N'))
$mod = Join-Path $stage 'AAVideoExport'
$payload = Join-Path $mod $version
$zip = Join-Path $output ('AAVideoExport-' + $version + '-win-x64-' + $Variant + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Release ZIP already exists; preserve it and use a new output directory.' }
if (!$SkipBuild) {
    dotnet build (Join-Path $repo 'src/VideoExport.Plugin/VideoExport.Plugin.csproj') -c Release --nologo ('-p:AAInstallPath=' + $AAInstallPath)
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$build = Join-Path $repo 'src/VideoExport.Plugin/bin/Release/net6.0'
$dlls = @('AAVideoExport.dll','AAVideoExport.Core.dll','Vortice.Direct3D11.dll','Vortice.DXGI.dll',
    'Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll')
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($name in $dlls) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $payload $name) }
Copy-Item -LiteralPath (Join-Path $repo 'manifest.json') -Destination (Join-Path $payload 'manifest.json')
Copy-Item -LiteralPath (Join-Path $repo ($version + '/icon.png')) -Destination (Join-Path $payload 'icon.png')
if ($Variant -eq 'full') {
    if (!$FFmpegDirectory -or !$FFmpegNoticesDirectory) { throw 'Full package requires FFmpeg binaries and notices.' }
    $tools = Join-Path $payload 'tools'
    New-Item -ItemType Directory -Path $tools | Out-Null
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        $path = Join-Path $FFmpegDirectory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing bundled encoder tool: $name" }
        Copy-Item -LiteralPath $path -Destination (Join-Path $tools $name)
    }
}
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $repo 'docs/third-party'),(Join-Path $repo 'docs/licenses') -Destination $licenses -Recurse
if ($Variant -eq 'full') {
    Copy-Item -LiteralPath $FFmpegNoticesDirectory -Destination (Join-Path $licenses 'FFmpeg') -Recurse
    if (!(Test-Path -LiteralPath (Join-Path $licenses 'FFmpeg/LICENSE.txt'))) { throw 'Missing FFmpeg license.' }
}
foreach ($file in @('README.md','THIRD_PARTY_NOTICES.md','docs/development.md','docs/render-control-v1.md','docs/moreeffects-clock-compatibility.md','docs/shader-provenance-2026-10-03.md','docs/release-0.2.1.md','docs/release-0.2.3.md','docs/release-0.2.4.md','docs/auto-selection-export.md')) {
    $destination = Join-Path $payload $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $destination
}
$hashes = Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' +
        [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $payload 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
# The archive root is AAVideoExport/, with no parent mods/ or staging folder.
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zip + '.sha256', $hash + '  ' + (Split-Path -Leaf $zip) + "`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Variant=$Variant; Zip=$zip; Payload=$payload; SHA256=$hash; Bytes=(Get-Item -LiteralPath $zip).Length } | ConvertTo-Json
