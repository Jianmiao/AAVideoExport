#requires -Version 7.2
<#
.SYNOPSIS
  G5 zero-per-Mod: read-only SHA256 snapshot of third-party Mod DLLs/configs and
  AA user settings/saves, plus baseline-vs-after diff comparison.

.DESCRIPTION
  Read-only. Scans the real AA install without launching AA or writing anything
  under the install directory. Modes:

  * Snapshot:        -Output <snapshot.json>
  * Scan + compare:  -Output <after.json> -Compare <baseline.json>
                     scans live, writes the after snapshot, then diffs baseline
                     against the new snapshot.
  * File-to-file:    -Compare <baseline.json> -AfterSnapshot <existing.json>
                     no live scan, no file written; used for self/tamper demos.

  Exit codes: 0 = no difference; 1 = ADDED/REMOVED/CHANGED present or comparison failed.

  Idempotency: in any mode that writes -Output, an existing Output file aborts
  with an error instead of being overwritten.
#>
param(
    [string]$AAInstallPath = 'F:\AzureArchive_101',
    [string]$Output = '',
    [string]$Compare = '',
    [string]$AfterSnapshot = '',
    [string]$ConfigRoot = '',
    [string]$SaveRoot = ''
)
$ErrorActionPreference = 'Stop'

# --- Exclusion rules (documented in the emitted snapshot) -------------------
$script:ExcludedModTopDirs = @('AAVideoExport')
$script:ExcludedNamePatterns = @(
    'UnknownPassiveMod',
    'UnknownClockMod',
    'UnknownRenderOwnerMod',
    'AAVideoExport.NativePath',
    'VideoExport.NativePath'
)
$script:ConfigExtensions = @('.json', '.txt', '.cfg', '.ini', '.xml', '.toml', '.yaml', '.yml')
$script:LocalLowExcludedDirs = @('com.unity.addressables', 'VideoBackup')
$script:LocalLowExcludedFiles = @('Player.log', 'Player-prev.log')

function Test-ExcludedName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    foreach ($p in $script:ExcludedNamePatterns) {
        if ($name -like "*$p*") { return $true }
    }
    if ($name -like '*.Tests') { return $true }
    return $false
}

function Get-AssemblyNameSafe([string]$path) {
    try { return [System.Reflection.AssemblyName]::GetAssemblyName($path).Name }
    catch { return '' }
}

function Add-Entry([System.Collections.Generic.List[object]]$list, [string]$category, [string]$mod, [string]$relativePath, [string]$assemblyName, [string]$fullPath) {
    $fi = Get-Item -LiteralPath $fullPath -Force
    $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    $list.Add([ordered]@{
        category      = $category
        mod           = $mod
        relativePath  = $relativePath
        assemblyName  = $assemblyName
        sizeBytes     = $fi.Length
        sha256        = $hash
        lastWriteTimeUtc = $fi.LastWriteTimeUtc.ToString('o')
    }) | Out-Null
}

function Get-Snapshot {
    param([string]$ModsRoot, [string]$LocalLowRoot, [string]$DataRoot)

    $entries = [System.Collections.Generic.List[object]]::new()
    $watch = [System.Diagnostics.Stopwatch]::StartNew()

    # --- Third-party Mod DLLs + configs under mods\ -------------------------
    if (Test-Path -LiteralPath $ModsRoot) {
        $modDirs = Get-ChildItem -LiteralPath $ModsRoot -Directory -Force
        foreach ($dir in $modDirs) {
            if ($script:ExcludedModTopDirs -contains $dir.Name) { continue }
            $modName = $dir.Name
            $files = Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Force
            foreach ($f in $files) {
                $rel = 'mods/' + $f.FullName.Substring($ModsRoot.Length).TrimStart('\','/').Replace('\','/')
                $base = [IO.Path]::GetFileNameWithoutExtension($f.Name)
                if ($f.Extension -eq '.dll') {
                    $asm = Get-AssemblyNameSafe $f.FullName
                    if ((Test-ExcludedName $base) -or (Test-ExcludedName $asm)) { continue }
                    Add-Entry $entries 'mod-dll' $modName $rel $asm $f.FullName
                }
                elseif ($script:ConfigExtensions -contains $f.Extension.ToLowerInvariant()) {
                    if (Test-ExcludedName $base) { continue }
                    Add-Entry $entries 'mod-config' $modName $rel '' $f.FullName
                }
                # Other payload assets (png/md/pdb/cs/...) are out of G5 scope;
                # see docs/zero-per-mod/G5-MOD-HASH-BASELINE.md gap section.
            }
        }
    }

    # --- AA user settings + Mod-owned config dirs under LocalLow -----------
    if ($LocalLowRoot -and (Test-Path -LiteralPath $LocalLowRoot)) {
        $settingsRoot = Join-Path $LocalLowRoot 'data\settings'
        if (Test-Path -LiteralPath $settingsRoot) {
            Get-ChildItem -LiteralPath $settingsRoot -File -Force |
                Where-Object { $_.Name -like '*user_settings*' } |
                ForEach-Object {
                    $rel = 'locallow/data/settings/' + $_.Name
                    Add-Entry $entries 'user-setting' '-' $rel '' $_.FullName
                }
        }
        $overridesRoot = Join-Path $LocalLowRoot 'data\overrides'
        if (Test-Path -LiteralPath $overridesRoot) {
            Get-ChildItem -LiteralPath $overridesRoot -Recurse -File -Force |
                ForEach-Object {
                    $rel = 'locallow/data/overrides/' + $_.FullName.Substring($overridesRoot.Length).TrimStart('\','/').Replace('\','/')
                    Add-Entry $entries 'user-setting' '-' $rel '' $_.FullName
                }
        }
        foreach ($modDir in @('MenuBgmSelector', 'SfxFolderMod')) {
            $p = Join-Path $LocalLowRoot $modDir
            if (Test-Path -LiteralPath $p) {
                Get-ChildItem -LiteralPath $p -Recurse -File -Force |
                    ForEach-Object {
                        $rel = 'locallow/' + $_.FullName.Substring($LocalLowRoot.Length).TrimStart('\','/').Replace('\','/')
                        Add-Entry $entries 'mod-config' $modDir $rel '' $_.FullName
                    }
            }
        }
    }

    # --- Story saves / projects under the AA data root ---------------------
    if ($DataRoot -and (Test-Path -LiteralPath $DataRoot)) {
        $savesRoot = Join-Path $DataRoot 'saves'
        if (Test-Path -LiteralPath $savesRoot) {
            Get-ChildItem -LiteralPath $savesRoot -File -Force -Filter '*.aas' |
                ForEach-Object {
                    $rel = 'savedata/saves/' + $_.Name
                    Add-Entry $entries 'user-save' '-' $rel '' $_.FullName
                }
        }
        $projectsRoot = Join-Path $DataRoot 'projects'
        if (Test-Path -LiteralPath $projectsRoot) {
            Get-ChildItem -LiteralPath $projectsRoot -File -Force -Filter '*.aap' |
                ForEach-Object {
                    $rel = 'savedata/projects/' + $_.Name
                    Add-Entry $entries 'user-save' '-' $rel '' $_.FullName
                }
        }
    }

    $watch.Stop()
    $sorted = @($entries | Sort-Object category, relativePath)
    $counts = @{ 'mod-dll' = 0; 'mod-config' = 0; 'user-setting' = 0; 'user-save' = 0 }
    $totalBytes = [long]0
    foreach ($e in $sorted) {
        $counts[$e.category]++
        $totalBytes += [long]$e.sizeBytes
    }
    $summary = [ordered]@{
        modDllCount     = $counts['mod-dll']
        modConfigCount  = $counts['mod-config']
        userSettingCount= $counts['user-setting']
        userSaveCount   = $counts['user-save']
        totalFiles      = $sorted.Count
        totalBytes      = $totalBytes
        elapsedMilliseconds = $watch.ElapsedMilliseconds
    }
    return [ordered]@{
        schema          = 'aave.g5-mod-hash-snapshot.v1'
        generatedAtUtc  = [DateTime]::UtcNow.ToString('o')
        aaInstallPath   = $AAInstallPath
        localLowRoot    = $LocalLowRoot
        dataRoot        = $DataRoot
        exclusions      = [ordered]@{
            modTopDirs        = $script:ExcludedModTopDirs
            namePatterns      = $script:ExcludedNamePatterns
            configExtensions  = $script:ConfigExtensions
            localLowExcludedDirs = $script:LocalLowExcludedDirs
            localLowExcludedFiles = $script:LocalLowExcludedFiles
            note              = 'AAVideoExport is the AAVE own payload; harness/fixture assemblies are excluded. Engine caches, runtime Player.log and AAVE output backups are excluded.'
        }
        summary         = $summary
        entries         = @($sorted)
    }
}

function Read-Snapshot([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { throw "Snapshot not found: $path" }
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}

function Compare-Snapshots($base, $after) {
    $baseMap = @{}
    foreach ($e in $base.entries) { $baseMap[$e.category + '|' + $e.relativePath] = $e }
    $afterMap = @{}
    foreach ($e in $after.entries) { $afterMap[$e.category + '|' + $e.relativePath] = $e }

    $diffs = [System.Collections.Generic.List[object]]::new()
    foreach ($key in ($afterMap.Keys | Sort-Object)) {
        if (-not $baseMap.ContainsKey($key)) {
            $diffs.Add([ordered]@{ kind = 'ADDED'; key = $key; beforeHash = ''; afterHash = $afterMap[$key].sha256 }) | Out-Null
        }
        else {
            $b = $baseMap[$key]; $a = $afterMap[$key]
            if ($b.sha256 -ne $a.sha256 -or $b.sizeBytes -ne $a.sizeBytes) {
                $diffs.Add([ordered]@{ kind = 'CHANGED'; key = $key; beforeHash = $b.sha256; afterHash = $a.sha256; beforeSize = $b.sizeBytes; afterSize = $a.sizeBytes }) | Out-Null
            }
        }
    }
    foreach ($key in ($baseMap.Keys | Sort-Object)) {
        if (-not $afterMap.ContainsKey($key)) {
            $diffs.Add([ordered]@{ kind = 'REMOVED'; key = $key; beforeHash = $baseMap[$key].sha256; afterHash = '' }) | Out-Null
        }
    }
    return ($diffs | Sort-Object kind, key)
}

# --- Resolve roots -----------------------------------------------------------
$modsRoot = Join-Path $AAInstallPath 'mods'
if (!(Test-Path -LiteralPath $modsRoot)) { throw "Mods root not found: $modsRoot" }

if ([string]::IsNullOrWhiteSpace($ConfigRoot)) {
    $ConfigRoot = Join-Path $env:USERPROFILE 'AppData\LocalLow\foxxlight\AzureArchive'
}
if ([string]::IsNullOrWhiteSpace($SaveRoot)) {
    $SaveRoot = 'E:\AAdata\data'
}

# --- File-to-file compare mode ---------------------------------------------
if ($AfterSnapshot) {
    if (-not $Compare) { throw '-AfterSnapshot requires -Compare <baseline.json>' }
    $base = Read-Snapshot $Compare
    $after = Read-Snapshot $AfterSnapshot
    $diffs = Compare-Snapshots $base $after
    $added = @($diffs | Where-Object kind -eq 'ADDED').Count
    $removed = @($diffs | Where-Object kind -eq 'REMOVED').Count
    $changed = @($diffs | Where-Object kind -eq 'CHANGED').Count
    Write-Output ("G5 COMPARE: baseline={0} after={1} ADDED={2} REMOVED={3} CHANGED={4}" -f $base.entries.Count, $after.entries.Count, $added, $removed, $changed)
    foreach ($d in $diffs) {
        Write-Output ("  [{0}] {1}  before={2} after={3}" -f $d.kind, $d.key, $d.beforeHash, $d.afterHash)
    }
    if ($diffs.Count -gt 0) { exit 1 }
    exit 0
}

# --- Snapshot (and optional scan + compare) mode ---------------------------
if ([string]::IsNullOrWhiteSpace($Output)) { throw '-Output <snapshot.json> is required unless -AfterSnapshot is used' }
if (Test-Path -LiteralPath $Output) { throw "Output snapshot already exists; refusing to overwrite: $Output" }

$snapshot = Get-Snapshot -ModsRoot $modsRoot -LocalLowRoot $ConfigRoot -DataRoot $SaveRoot

$outDir = Split-Path $Output -Parent
if ($outDir -and !(Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$json = $snapshot | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($Output, $json, [System.Text.UTF8Encoding]::new($false))

Write-Output ("G5 SNAPSHOT: dll={0} mod-config={1} user-setting={2} user-save={3} total={4} bytes={5} elapsedMs={6}" -f `
    $snapshot.summary.modDllCount, $snapshot.summary.modConfigCount, $snapshot.summary.userSettingCount, `
    $snapshot.summary.userSaveCount, $snapshot.summary.totalFiles, $snapshot.summary.totalBytes, $snapshot.summary.elapsedMilliseconds)

if ($Compare) {
    $base = Read-Snapshot $Compare
    $after = Read-Snapshot $Output
    $diffs = Compare-Snapshots $base $after
    $added = @($diffs | Where-Object kind -eq 'ADDED').Count
    $removed = @($diffs | Where-Object kind -eq 'REMOVED').Count
    $changed = @($diffs | Where-Object kind -eq 'CHANGED').Count
    Write-Output ("G5 COMPARE: baseline={0} after={1} ADDED={2} REMOVED={3} CHANGED={4}" -f $base.entries.Count, $after.entries.Count, $added, $removed, $changed)
    foreach ($d in $diffs) {
        Write-Output ("  [{0}] {1}  before={2} after={3}" -f $d.kind, $d.key, $d.beforeHash, $d.afterHash)
    }
    if ($diffs.Count -gt 0) { exit 1 }
    exit 0
}
exit 0
