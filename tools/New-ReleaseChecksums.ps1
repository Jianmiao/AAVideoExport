#requires -Version 7.2
<#
.SYNOPSIS
    Generates or verifies a SHA256SUMS.txt manifest for a release staging directory.

.DESCRIPTION
    Generate mode (default):
      - Recursively hashes every file under -Directory (the output sums file itself
        is excluded), writes one row per file:
            <uppercase SHA256>  <relative path, forward slashes>
      - Rows are sorted by relative path (ordinal), encoded UTF-8 without BOM.
      - Refuses to silently overwrite an existing sums file; pass -Force to
        regenerate on purpose.

    Verify mode (-Verify):
      - Reads the existing sums file, recomputes each listed file's SHA256 and
        reports OK / MISMATCH / MISSING / UNLISTED / malformed lines.
      - Exit code 0 only when every listed file matches and every on-disk file
        is listed; any problem yields exit code 1 (exit code 2 when the sums
        file itself cannot be read).

.PARAMETER Directory
    Root directory to hash (generate) or to check against (verify).

.PARAMETER OutputFile
    Path of the SHA256SUMS.txt file. Defaults to <Directory>\SHA256SUMS.txt.

.PARAMETER Verify
    Switch: verify the existing sums file instead of generating one.

.PARAMETER Force
    Switch: allow regeneration when the sums file already exists (generate mode).

.EXAMPLE
    pwsh -File tools\New-ReleaseChecksums.ps1 -Directory artifacts\release-0.3.0-draft\staging\20261122-XXXXXX-lite
    pwsh -File tools\New-ReleaseChecksums.ps1 -Directory artifacts\release-0.3.0-draft\staging\20261122-XXXXXX-lite -Verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [string]$OutputFile,
    [switch]$Verify,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

$dirResolved = Resolve-Path -LiteralPath $Directory -ErrorAction Stop
$dirPath = $dirResolved.Path.TrimEnd('\', '/')
if (-not $OutputFile) { $OutputFile = Join-Path $dirPath 'SHA256SUMS.txt' }
$outPath = [IO.Path]::GetFullPath($OutputFile)

function Get-Relative([string]$FullName) {
    return [IO.Path]::GetRelativePath($dirPath, $FullName).Replace('\', '/')
}
function Get-Sha256([string]$FullName) {
    return (Get-FileHash -LiteralPath $FullName -Algorithm SHA256 -ErrorAction Stop).Hash
}

if ($Verify) {
    if (-not (Test-Path -LiteralPath $outPath -PathType Leaf)) {
        Write-Output "FAIL  checksums file not found: $outPath"
        exit 2
    }

    $rawText = [IO.File]::ReadAllText($outPath)
    if ($rawText.Length -gt 0 -and $rawText[0] -eq [char]0xFEFF) { $rawText = $rawText.Substring(1) }
    $lines = $rawText -split "`r?`n"

    $listed = @{}
    $ok = 0
    $problems = 0

    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            Write-Output "FAIL  malformed line: $line"
            $problems++
            continue
        }
        $expected = $Matches[1].ToUpperInvariant()
        $rel = $Matches[2]
        if ($listed.ContainsKey($rel)) {
            Write-Output "FAIL  duplicate entry: $rel"
            $problems++
            continue
        }
        $listed[$rel] = $true

        $segments = $rel -split '/'
        $full = Join-Path $dirPath ($segments -join [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            Write-Output "MISSING  $rel (expected $expected)"
            $problems++
            continue
        }
        $actual = Get-Sha256 $full
        if ($actual -eq $expected) {
            Write-Output "OK  $rel"
            $ok++
        }
        else {
            Write-Output "MISMATCH  $rel"
            Write-Output "    expected $expected"
            Write-Output "    actual   $actual"
            $problems++
        }
    }

    $onDisk = @(Get-ChildItem -LiteralPath $dirPath -Recurse -File -Force |
        Where-Object { $_.FullName -ne $outPath })
    foreach ($file in $onDisk) {
        $rel = Get-Relative $file.FullName
        if (-not $listed.ContainsKey($rel)) {
            Write-Output "UNLISTED  $rel"
            $problems++
        }
    }

    Write-Output ("SUMMARY: {0} OK, {1} problem(s)" -f $ok, $problems)
    if ($problems -gt 0) { exit 1 }
    exit 0
}

# Generate mode.
if (Test-Path -LiteralPath $outPath) {
    if (-not $Force) {
        throw ("Checksums file already exists; refusing to overwrite silently: {0}. " +
               "Pass -Force only when regenerating on purpose." -f $outPath)
    }
}

$files = @(Get-ChildItem -LiteralPath $dirPath -Recurse -File -Force |
    Where-Object { $_.FullName -ne $outPath })
$rows = foreach ($file in $files) {
    [pscustomobject]@{
        Rel  = (Get-Relative $file.FullName)
        Hash = (Get-Sha256 $file.FullName)
    }
}
$rows = @($rows | Sort-Object -Property Rel -Culture ([Globalization.CultureInfo]::InvariantCulture))
$lines = foreach ($row in $rows) { '{0}  {1}' -f $row.Hash, $row.Rel }

$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines($outPath, [string[]]$lines, $utf8NoBom)
Write-Output ("Wrote {0} checksum line(s) to {1}" -f $lines.Count, $outPath)
exit 0
