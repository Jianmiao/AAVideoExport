param(
    [string] $AAReferenceRoot = 'F:\AzureArchive_101',
    [ValidateRange(5, 300)] [int] $TimeoutSeconds = 30,
    [switch] $SkipBuild
)
$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$evidence = Join-Path $probeRoot 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$systemDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$systemRoot = Split-Path -Parent $systemDotnet
$hostDotnet = Join-Path $AAReferenceRoot 'dotnet'
$runtimeVersion = (Get-Content -LiteralPath (Join-Path $hostDotnet '.version') | Select-Object -Skip 1 -First 1).Trim()
if ($runtimeVersion -notmatch '^6\.0\.\d+$') { throw "Expected a net6 runtime, got '$runtimeVersion'." }
$resolver = Get-ChildItem -LiteralPath (Join-Path $systemRoot 'host\fxr') -Directory |
    Where-Object Name -Match '^6\.0\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $resolver) { throw 'An installed .NET 6 host resolver is required.' }

function Invoke-Bounded([string] $Executable, [string[]] $Arguments, [string] $Label, [int] $Limit) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $probeRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit($Limit * 1000)) {
            $child.Kill($true)
            $child.WaitForExit()
            throw "$Label exceeded $Limit seconds; only the isolated probe process tree was stopped."
        }
        [IO.File]::WriteAllText((Join-Path $evidence "$Label.stdout.json"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $evidence "$Label.stderr.txt"), $stderr.GetAwaiter().GetResult())
        return $child.ExitCode
    }
    finally { $child.Dispose() }
}

if (-not $SkipBuild) {
    $code = Invoke-Bounded $systemDotnet @('build', (Join-Path $probeRoot 'RuntimeAttributionProbe.csproj'),
        '-c', 'Release', "-p:AAReferenceRoot=$AAReferenceRoot", '-v', 'minimal') 'build' 120
    if ($code -ne 0) { throw "Build failed ($code); inspect evidence/build.stderr.txt and build.stdout.json." }
}

# A private host layout executes AA's unchanged runtime files. The junction is created
# inside this tool's bin directory; no files are written under the AA installation.
$runtimeRoot = Join-Path $probeRoot 'bin\isolated-aa-runtime'
$fxrDestination = Join-Path $runtimeRoot "host\fxr\$($resolver.Name)"
$frameworkParent = Join-Path $runtimeRoot 'shared\Microsoft.NETCore.App'
New-Item -ItemType Directory -Path $fxrDestination -Force | Out-Null
New-Item -ItemType Directory -Path $frameworkParent -Force | Out-Null
Copy-Item -LiteralPath $systemDotnet -Destination (Join-Path $runtimeRoot 'dotnet.exe') -Force
Copy-Item -LiteralPath (Join-Path $resolver.FullName 'hostfxr.dll') -Destination $fxrDestination -Force
$runtimeLink = Join-Path $frameworkParent $runtimeVersion
if (Test-Path -LiteralPath $runtimeLink) {
    $existing = Get-Item -LiteralPath $runtimeLink
    if ($existing.LinkType -ne 'Junction' -or [string]$existing.Target -ne $hostDotnet) {
        throw "Refusing to replace an unexpected runtime path: $runtimeLink"
    }
} else {
    New-Item -ItemType Junction -Path $runtimeLink -Target $hostDotnet | Out-Null
}
$launcher = Join-Path $runtimeRoot 'dotnet.exe'
$assembly = Join-Path $probeRoot 'bin\Release\net6.0\AAVE.RuntimeAttributionProbe.dll'
$sourceFiles = @('dotnet\coreclr.dll', 'dotnet\System.Private.CoreLib.dll',
    'BepInEx\core\0Harmony.dll', 'BepInEx\core\MonoMod.RuntimeDetour.dll',
    'BepInEx\core\MonoMod.Utils.dll', 'BepInEx\core\BepInEx.Preloader.Core.dll',
    'BepInEx\core\BepInEx.Unity.IL2CPP.dll', 'BepInEx\core\BepInEx.Core.dll')
$before = @($sourceFiles | ForEach-Object { Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $AAReferenceRoot $_) })
$controlExit = Invoke-Bounded $launcher @('exec', '--fx-version', $runtimeVersion, $assembly, '--without-hooks') 'control' $TimeoutSeconds
$probeExit = Invoke-Bounded $launcher @('exec', '--fx-version', $runtimeVersion, $assembly) 'instrumented' $TimeoutSeconds
$seamExit = Invoke-Bounded $launcher @('exec', '--fx-version', $runtimeVersion, $assembly, '--inspect-host', $AAReferenceRoot) 'host-seam' $TimeoutSeconds
$after = @($sourceFiles | ForEach-Object { Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $AAReferenceRoot $_) })
$control = Get-Content -Raw -LiteralPath (Join-Path $evidence 'control.stdout.json') | ConvertFrom-Json
$probe = Get-Content -Raw -LiteralPath (Join-Path $evidence 'instrumented.stdout.json') | ConvertFrom-Json
$unchanged = @($before.Hash) -join ',' -eq (@($after.Hash) -join ',')
$passed = $controlExit -eq 1 -and $control.verdict -eq 'FAIL_ATTRIBUTION_PROBE' -and
    $probeExit -eq 0 -and $probe.failed -eq 0 -and $probe.runtimeVersion -eq $runtimeVersion -and
    $seamExit -eq 0 -and $unchanged
$summary = [ordered]@{
    timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    aaReferenceRoot = $AAReferenceRoot
    runtimeVersion = $runtimeVersion
    hostResolverVersion = $resolver.Name
    controlExit = $controlExit
    controlFailures = $control.failed
    instrumentedExit = $probeExit
    instrumentedChecks = $probe.checks.Count
    instrumentedFailures = $probe.failed
    hostSeamExit = $seamExit
    referenceFilesUnchanged = $unchanged
    hashes = @($after | Select-Object Path, Hash)
    passed = $passed
    scope = 'Isolated fixtures only; not an AA launch, Mod certification, or export-effects proof.'
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
if (-not $passed) { exit 1 }
