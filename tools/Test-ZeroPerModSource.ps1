#requires -Version 7.2
<#
.SYNOPSIS
  Zero-per-Mod source gate: find production source string literals that name an
  actually installed third-party Mod. The Mod list is discovered from the AA
  installation; it is not hardcoded in this script.
#>
param(
    [string]$AAInstallPath = 'F:\AzureArchive_101',
    [string]$SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'

$modsRoot = Join-Path $AAInstallPath 'mods'
if (!(Test-Path -LiteralPath $modsRoot)) { throw "Mods root not found: $modsRoot" }

# Discover actual installed Mod assembly names and DLL base names. AAVE itself is excluded.
$modNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$modIdentities = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem -LiteralPath $modsRoot -Recurse -Filter *.dll -File |
    Where-Object { $_.FullName -notlike '*\AAVideoExport\*' } |
    ForEach-Object {
        try {
            $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName).Name
            if ($assemblyName) { [void]$modNames.Add($assemblyName) }
        } catch { }
        $base = [IO.Path]::GetFileNameWithoutExtension($_.Name)
        if ($base) { [void]$modNames.Add($base) }

        # Exact module identity and file digest catch per-Mod GUID/MVID/hash
        # branches that do not spell the Mod's name in the literal.
        $owner = if ($assemblyName) { $assemblyName } else { $base }
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        if ($hash) { $modIdentities[$hash] = $owner }
        try {
            $stream = [IO.File]::OpenRead($_.FullName)
            try {
                $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                    $mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid)
                    if ($mvid -and $mvid -ne [Guid]::Empty) {
                        $dashed = $mvid.ToString()
                        $compact = $mvid.ToString('N')
                        $modIdentities[$dashed] = $owner
                        $modIdentities[$compact] = $owner
                    }
                } finally { $pe.Dispose() }
            } finally { $stream.Dispose() }
        } catch { }
    }

if ($modNames.Count -eq 0) { throw 'No third-party Mod assemblies were discovered.' }

$sourceDir = Join-Path $SourceRoot 'src'
if (!(Test-Path -LiteralPath $sourceDir)) { throw "Source dir not found: $sourceDir" }

$violations = [System.Collections.Generic.List[object]]::new()
$files = Get-ChildItem -LiteralPath $sourceDir -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object FullName
$literalPattern = [regex]'"((?:\\.|[^"\\])*)"'

foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($SourceRoot, $file.FullName).Replace('\', '/')
    $lines = [IO.File]::ReadAllLines($file.FullName, [Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        foreach ($match in $literalPattern.Matches($line)) {
            $literal = $match.Groups[1].Value
            $matched = $false
            foreach ($mod in $modNames) {
                if ($literal.IndexOf($mod, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $violations.Add([ordered]@{
                        file = $relative
                        line = $i + 1
                        mod = $mod
                        literal = $literal
                        text = $line.Trim()
                        kind = 'name'
                    })
                    $matched = $true
                    break
                }
            }
            if (!$matched) {
                foreach ($identity in $modIdentities.Keys) {
                    if ($literal.IndexOf($identity, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                        ($identity.Length -eq 32 -and $literal.Replace('-', '').IndexOf($identity, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                        $violations.Add([ordered]@{
                            file = $relative
                            line = $i + 1
                            mod = $modIdentities[$identity]
                            literal = $literal
                            text = $line.Trim()
                            kind = if ($identity.Length -eq 64) { 'sha256' } else { 'mvid' }
                        })
                        break
                    }
                }
            }
        }
    }
}

$report = [ordered]@{
    schema = 'aave.zero-per-mod.static.v2'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    aaInstallPath = (Resolve-Path -LiteralPath $AAInstallPath).Path
    sourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    discoveredModNames = @($modNames | Sort-Object)
    discoveredModIdentities = @($modIdentities.Keys | Sort-Object)
    filesScanned = $files.Count
    violations = $violations.Count
    details = $violations
}

$json = $report | ConvertTo-Json -Depth 5
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $SourceRoot 'artifacts\zero-per-mod\static-source.json'
}
$outDir = Split-Path $Output -Parent
if ($outDir -and !(Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
Set-Content -LiteralPath $Output -Value $json -Encoding utf8NoBOM

Write-Output ("ZERO-PER-MOD STATIC: files={0} mods={1} violations={2}" -f $files.Count, $modNames.Count, $violations.Count)
foreach ($v in $violations | Select-Object -First 40) {
    Write-Output ("  {0}:{1}  mod={2}  {3}" -f $v.file, $v.line, $v.mod, $v.text)
}
if ($violations.Count -gt 0) { exit 1 }
exit 0
