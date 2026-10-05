param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\portable-tests' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
$checks = @()
foreach ($flag in @('canvas-only','anime-compute-only','d3d-policy-only','gpu-device-only','panel-layout-only','playback-launch-only','proxy-only','ui-flow-only','ui-refresh-only','encoder-path-only')) {
    $checks += @{ Name='Core-' + $flag; Project='VideoExport.Core.Tests'; Flag=$flag }
}
foreach ($name in @('Audio','Catalog','Clock','FramePipeline','Host','OutputDimensions','QueuedReadback','Compatibility','RenderControl','ToolPaths','SoftwareEncoding','StoryPath')) {
    $checks += @{ Name=$name; Project='VideoExport.' + $name + '.Tests' }
}
$results = foreach ($check in $checks) {
    $arguments = @('run','--project',(Join-Path $repo ('tests\' + $check.Project)),'-c','Release')
    if ($check.Flag) { $arguments += @('--',('--' + $check.Flag)) }
    # Pure functional branches only. No FFmpeg media or performance benchmark.
    $output = & dotnet @arguments 2>&1
    $code = $LASTEXITCODE
    $lines = @($output | ForEach-Object ToString)
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory ($check.Name + '.log')), $lines)
    if ($code -ne 0) { throw ($check.Name + ' failed: ' + ($lines -join "`n")) }
    $summary = $lines | Where-Object { $_ -match '(\d+/\d+ passed|RESULT: \d+ passed)' } | Select-Object -Last 1
    Write-Host ($check.Name + ': ' + $summary)
    [pscustomobject]@{Name=$check.Name;ExitCode=$code;Summary=$summary;Passed=@($lines | Where-Object {$_ -match '^PASS '}).Count}
}
$report = [pscustomobject]@{PerformanceTests=$false;Calls=$results.Count;Passed=($results | Measure-Object Passed -Sum).Sum;Results=$results}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'portable-results.json'), ($report | ConvertTo-Json -Depth 4))
$report | Select-Object PerformanceTests,Calls,Passed | ConvertTo-Json
