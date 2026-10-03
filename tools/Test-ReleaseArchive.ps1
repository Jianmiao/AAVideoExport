param([Parameter(Mandatory=$true)][string]$Archive, [Parameter(Mandatory=$true)][ValidateSet('lite','full')][string]$Variant)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'manifest.json') -Raw | ConvertFrom-Json
$prefix = 'AAVideoExport/' + $manifest.version_number + '/'
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        if (!$entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or
            $entry.FullName.Contains('\') -or $entry.FullName.Contains('../') -or
            $entry.FullName.StartsWith('/')) { throw "Unexpected archive root/path: $($entry.FullName)" }
        if ($entries.ContainsKey($entry.FullName)) { throw 'Duplicate ZIP entry.' }
        $entries[$entry.FullName] = $entry
    }
    $required = @('manifest.json','icon.png','AAVideoExport.dll','AAVideoExport.Core.dll',
        'Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.D3DCompiler.dll',
        'Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll','SHA256SUMS.txt')
    if ($Variant -eq 'full') { $required += @('tools/ffmpeg.exe','tools/ffprobe.exe','licenses/FFmpeg/LICENSE.txt') }
    elseif ($entries.Keys | Where-Object { $_ -match '(?i)\.exe$|/licenses/FFmpeg/' }) { throw 'Lite package must not include FFmpeg programs or their bundled notices.' }
    foreach ($name in $required) { if (!$entries.ContainsKey($prefix + $name)) { throw "Missing release entry: $name" } }
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -match '(?i)(BepInEx|interop|NativePath\.Tests|local\.props|\.aap2?$|\.aas$|\.cfg$|\.pdb$|\.log$|\.sqlite|\.db$|(^|/)(bin|obj)/)') {
            throw "Forbidden release content: $($entry.FullName)"
        }
    }
    $reader = [IO.StreamReader]::new($entries[$prefix + 'manifest.json'].Open())
    try { $actual = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($actual.name -ne 'AAVideoExport' -or $actual.version_number -ne $manifest.version_number) { throw 'ZIP manifest mismatch.' }
    $reader = [IO.StreamReader]::new($entries[$prefix + 'SHA256SUMS.txt'].Open())
    try { $lines = $reader.ReadToEnd() -split '\r?\n' } finally { $reader.Dispose() }
    $covered = @{}
    foreach ($line in $lines) {
        if (!$line) { continue }
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid checksum entry.' }
        $expected = $Matches[1]; $name = $Matches[2]; $key = $prefix + $name
        if ($covered.ContainsKey($key) -or !$entries.ContainsKey($key)) { throw 'Duplicate or missing hashed entry.' }
        $stream = $entries[$key].Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($hash -ne $expected) { throw "ZIP checksum mismatch: $name" }
        $covered[$key] = $true
    }
    if ($covered.Count -ne $zip.Entries.Count - 1) { throw 'Checksum coverage incomplete.' }
    $peFiles = @('AAVideoExport.dll')
    if ($Variant -eq 'full') { $peFiles += @('tools/ffmpeg.exe','tools/ffprobe.exe') }
    foreach ($name in $peFiles) {
        $stream = $entries[$prefix + $name].Open()
        try { if ($stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) { throw "Not a real PE binary: $name" } }
        finally { $stream.Dispose() }
    }
    Write-Output "PASS $Variant portable ZIP: $prefix; $($zip.Entries.Count) files; full checksum coverage; encoder content matches variant; no host/project/test files."
}
finally { $zip.Dispose() }
