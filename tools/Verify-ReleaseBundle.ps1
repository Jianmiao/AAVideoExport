#requires -Version 7.2
<#
.SYNOPSIS
    Validates a staged AAVE release bundle (zip or unpacked directory) against
    the expected production release identity.

.DESCRIPTION
    Runs independent PASS/FAIL checks against a lite/full draft bundle:
      * delivery-manifest.json version matches -ExpectedVersion and variant matches -Variant;
      * the production bundle must declare AuditMode=false (no audit bootstrap);
      * payload runtime DLL set equals the fixed runtime manifest exactly;
      * actual AAVideoExport.dll / AAVideoExport.Core.dll SHA256 match the expected values;
      * no bootstrap/ directory and no RuntimeBootstrap assembly anywhere;
      * no acceptance fixture / testhost assembly in the package;
      * full bundle ships tools/ffmpeg.exe, tools/ffprobe.exe, licenses/FFmpeg/LICENSE.txt;
        lite bundle ships no tools/ directory;
      * README.md, LICENSE, THIRD_PARTY_NOTICES.md are present at the package root.

    Every check prints a PASS or FAIL line. Exit code is 0 only when all checks
    pass; otherwise exit code 1 (exit code 2 when the bundle cannot be opened).

.PARAMETER BundlePath
    Path to the release .zip, or to an already unpacked bundle directory.

.PARAMETER Variant
    Expected bundle variant: lite or full.

.PARAMETER ExpectedPluginSha256
    Expected SHA256 of AAVideoExport.dll (hex, case-insensitive).

.PARAMETER ExpectedCoreSha256
    Expected SHA256 of AAVideoExport.Core.dll (hex, case-insensitive).

.PARAMETER ExpectedVersion
    Expected release version, e.g. 0.3.0.

.EXAMPLE
    pwsh -File tools\Verify-ReleaseBundle.ps1 -BundlePath staging\xxx-lite\AAVideoExport-0.3.0-win-x64-lite-bundle.zip -Variant lite -ExpectedPluginSha256 D1E8... -ExpectedCoreSha256 E565... -ExpectedVersion 0.3.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [Parameter(Mandatory=$true)][ValidateSet('lite','full')][string]$Variant,
    [Parameter(Mandatory=$true)][string]$ExpectedPluginSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedCoreSha256,
    [Parameter(Mandatory=$true)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'

$runtimeManifest = @(
    'AAVideoExport.dll','AAVideoExport.Core.dll',
    'Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll',
    'Vortice.D3DCompiler.dll','Vortice.Mathematics.dll',
    'SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll'
)
$fixturePatterns = @(
    'UnknownPassiveMod','UnknownClockMod','UnknownRenderOwnerMod','fixture','testhost'
)

$script:failures = 0
function Add-Pass([string]$Message) { Write-Output "PASS  $Message" }
function Add-Fail([string]$Message) { Write-Output "FAIL  $Message"; $script:failures++ }

$expectedPlugin = $ExpectedPluginSha256.Trim().ToUpperInvariant()
$expectedCore = $ExpectedCoreSha256.Trim().ToUpperInvariant()

$bundleResolved = Resolve-Path -LiteralPath $BundlePath -ErrorAction Stop
$bundleItem = Get-Item -LiteralPath $bundleResolved.Path
$tempRoot = $null

if ($bundleItem.PSIsContainer) {
    $root = $bundleItem.FullName.TrimEnd('\', '/')
}
else {
    if ($bundleItem.Extension -ne '.zip') {
        Write-Output "FAIL  BundlePath must be a .zip archive or an unpacked directory: $BundlePath"
        exit 2
    }
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('aave-bundle-verify-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($bundleItem.FullName, $tempRoot)
    }
    catch {
        Add-Fail "cannot extract bundle zip: $($_.Exception.Message)"
        Write-Output "SUMMARY: $script:failures check(s) FAIL"
        exit 2
    }
    $root = $tempRoot.TrimEnd('\', '/')
}

try {
    # --- 1. Delivery manifest -------------------------------------------------
    $descriptor = $null
    $manifestPath = Join-Path $root 'delivery-manifest.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $descriptor = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 100
        }
        catch {
            Add-Fail "delivery-manifest.json is not valid JSON: $($_.Exception.Message)"
        }
    }
    else {
        Add-Fail 'delivery-manifest.json missing at bundle root'
    }

    if ($descriptor) {
        if ([string]$descriptor.Version -eq $ExpectedVersion) {
            Add-Pass "manifest version matches expected ($ExpectedVersion)"
        }
        else {
            Add-Fail "manifest version '$($descriptor.Version)' != expected '$ExpectedVersion'"
        }

        if ([string]$descriptor.Variant -eq $Variant) {
            Add-Pass "manifest variant matches requested ($Variant)"
        }
        else {
            Add-Fail "manifest variant '$($descriptor.Variant)' != requested '$Variant'"
        }

        if ($descriptor.AuditMode -eq $false) {
            Add-Pass 'manifest declares production bundle (AuditMode=false)'
        }
        else {
            Add-Fail 'bundle is marked development-audit; a production bundle is required'
        }
    }

    $payloadRel = "AAVideoExport/$ExpectedVersion"
    if ($descriptor -and $descriptor.PayloadRelativePath) {
        $payloadRel = [string]$descriptor.PayloadRelativePath
    }
    $payload = Join-Path $root $payloadRel

    # --- 2. Payload runtime DLL set ------------------------------------------
    if (Test-Path -LiteralPath $payload -PathType Container) {
        $dllNames = @(Get-ChildItem -LiteralPath $payload -File -Filter '*.dll' | ForEach-Object Name)
        $missing = @($runtimeManifest | Where-Object { $_ -notin $dllNames })
        $extra = @($dllNames | Where-Object { $_ -notin $runtimeManifest })
        if ($missing.Count -eq 0 -and $extra.Count -eq 0) {
            Add-Pass "payload runtime DLL set matches the fixed manifest ($($runtimeManifest.Count) assemblies)"
        }
        else {
            if ($missing.Count) { Add-Fail "payload runtime DLL missing: $($missing -join ', ')" }
            if ($extra.Count) { Add-Fail "unexpected DLL in payload: $($extra -join ', ')" }
        }

        # --- 3. Plugin / Core identity hashes --------------------------------
        $pluginPath = Join-Path $payload 'AAVideoExport.dll'
        $corePath = Join-Path $payload 'AAVideoExport.Core.dll'

        if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
            $actual = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash
            if ($actual -eq $expectedPlugin) {
                Add-Pass 'AAVideoExport.dll SHA256 matches expected candidate'
            }
            else {
                Add-Fail ("AAVideoExport.dll SHA256 mismatch`n    expected $expectedPlugin`n    actual   $actual")
            }
        }
        else { Add-Fail 'AAVideoExport.dll missing in payload' }

        if (Test-Path -LiteralPath $corePath -PathType Leaf) {
            $actual = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash
            if ($actual -eq $expectedCore) {
                Add-Pass 'AAVideoExport.Core.dll SHA256 matches expected candidate'
            }
            else {
                Add-Fail ("AAVideoExport.Core.dll SHA256 mismatch`n    expected $expectedCore`n    actual   $actual")
            }
        }
        else { Add-Fail 'AAVideoExport.Core.dll missing in payload' }

        if ($descriptor) {
            if ($descriptor.PluginSha256) {
                $descriptorPlugin = ([string]$descriptor.PluginSha256).ToUpperInvariant()
                if ($descriptorPlugin -eq $expectedPlugin) {
                    Add-Pass 'manifest PluginSha256 agrees with expected candidate'
                }
                else {
                    Add-Fail "manifest PluginSha256 $descriptorPlugin != expected $expectedPlugin"
                }
            }
            if ($descriptor.CoreSha256) {
                $descriptorCore = ([string]$descriptor.CoreSha256).ToUpperInvariant()
                if ($descriptorCore -eq $expectedCore) {
                    Add-Pass 'manifest CoreSha256 agrees with expected candidate'
                }
                else {
                    Add-Fail "manifest CoreSha256 $descriptorCore != expected $expectedCore"
                }
            }
        }
    }
    else {
        Add-Fail "payload directory missing: $payloadRel"
    }

    $allFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)

    # --- 4. No audit bootstrap in production package -------------------------
    $bootstrapDir = Join-Path $root 'bootstrap'
    if (Test-Path -LiteralPath $bootstrapDir) {
        Add-Fail 'production bundle contains forbidden bootstrap/ directory'
    }
    else {
        Add-Pass 'no bootstrap/ directory at bundle root'
    }
    $bootAssemblies = @($allFiles | Where-Object { $_.Name -like '*RuntimeBootstrap*' })
    if ($bootAssemblies.Count -gt 0) {
        Add-Fail "production bundle contains RuntimeBootstrap assembly: $($bootAssemblies.Name -join ', ')"
    }
    else {
        Add-Pass 'no RuntimeBootstrap assembly anywhere in bundle'
    }

    # --- 5. No acceptance fixture / testhost assemblies ----------------------
    $fixtureHits = @($allFiles | Where-Object {
            $name = $_.Name.ToLowerInvariant()
            @($fixturePatterns | Where-Object { $name.Contains($_.ToLowerInvariant()) }).Count -gt 0
        })
    if ($fixtureHits.Count -gt 0) {
        $hitRel = $fixtureHits | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') }
        Add-Fail "fixture/testhost file present: $($hitRel -join '; ')"
    }
    else {
        Add-Pass 'no acceptance fixture or testhost file in bundle'
    }

    # --- 6. Variant-specific content ----------------------------------------
    if ($Variant -eq 'full') {
        foreach ($rel in @('tools/ffmpeg.exe', 'tools/ffprobe.exe', 'licenses/FFmpeg/LICENSE.txt')) {
            $candidate = Join-Path $payload $rel
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Add-Pass "full bundle contains $rel"
            }
            else {
                Add-Fail "full bundle missing $rel"
            }
        }
    }
    else {
        $toolsEntries = @($allFiles | Where-Object {
            $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
            ($relative -split '[\\/]') -contains 'tools'
        })
        if ($toolsEntries.Count -gt 0) {
            Add-Fail "lite bundle unexpectedly contains tools/ entries: $($toolsEntries.Name -join ', ')"
        }
        else {
            Add-Pass 'lite bundle contains no tools/ directory'
        }
    }

    # --- 7. Top-level release documents --------------------------------------
    foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        if (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf) {
            Add-Pass "top-level $name present"
        }
        else {
            Add-Fail "top-level $name missing"
        }
    }

    Write-Output ("SUMMARY: " + $(if ($script:failures -eq 0) { 'all checks PASS' } else { "$script:failures check(s) FAIL" }))
    if ($script:failures -gt 0) { exit 1 }
    exit 0
}
finally {
    if ($tempRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedTemp.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedTemp) -like 'aave-bundle-verify-*') {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
        }
    }
}
