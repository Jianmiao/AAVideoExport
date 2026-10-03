param([string]$RepositoryPath = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryPath).Path
$payload = Join-Path $root '0.2.0'
$required = @('AAVideoExport.dll', 'AAVideoExport.Core.dll', 'Vortice.Direct3D11.dll',
    'Vortice.DXGI.dll', 'Vortice.DirectX.dll', 'Vortice.D3DCompiler.dll',
    'Vortice.Mathematics.dll', 'SharpGen.Runtime.dll', 'SharpGen.Runtime.COM.dll',
    'manifest.json', 'icon.png')
$manifest = Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.name -ne 'AAVideoExport' -or $manifest.version_number -ne '0.2.0') {
    throw 'Manifest identity does not match mods/AAVideoExport/0.2.0.'
}
if ($manifest.website_url -ne 'https://github.com/Jianmiao/AAVideoExport') {
    throw 'Manifest repository URL mismatch.'
}
$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
foreach ($property in @('name', 'version_number', 'website_url', 'description')) {
    if ($sourceManifest.$property -ne $manifest.$property) { throw "Source/runtime manifest mismatch: $property" }
}
$hashes = @{}
foreach ($row in Get-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt')) {
    if ($row -notmatch '^([a-fA-F0-9]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Invalid checksum row.' }
    $name = $Matches[2]
    if ($hashes.ContainsKey($name)) { throw "Duplicate checksum: $name" }
    $hashes[$name] = $Matches[1]
}
if ($hashes.Count -ne $required.Count) { throw 'The checksum list must cover the eleven runtime files exactly.' }
foreach ($name in $required) {
    $path = Join-Path $payload $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing runtime file: $name" }
    if (!$hashes.ContainsKey($name) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$name]) {
        throw "Runtime checksum mismatch: $name"
    }
    if ($name.EndsWith('.dll')) {
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 1024 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
            throw "Expected a real compiled DLL, not a Git LFS pointer: $name"
        }
    }
}
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'AAVideoExport.dll')).Version
if ($version.ToString() -ne '0.2.0.0') { throw "Plugin assembly version mismatch: $version" }
$dlls = @(Get-ChildItem -LiteralPath $payload -Filter '*.dll' -File -Recurse)
if ($dlls.Count -ne 9) { throw 'Runtime must contain exactly the nine reviewed DLLs.' }
foreach ($file in @('LICENSE', 'THIRD_PARTY_NOTICES.md',
    'docs/third-party/SharpGen.LICENSE.txt', 'docs/third-party/Vortice.Windows.LICENSE.txt',
    'docs/third-party/Vortice.Mathematics.LICENSE.txt', 'docs/licenses/AMD-FSR1-MIT.txt',
    'docs/licenses/Anime4K-MIT.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)) { throw "Missing distribution notice: $file" }
}
if (Test-Path -LiteralPath (Join-Path $root 'mods/AAVideoExport')) { throw 'Unexpected nested mods directory.' }
Write-Output 'PASS clone-ready layout: AAVideoExport/0.2.0, 11 runtime files, 9 real DLLs, SHA-256 and notices.'
