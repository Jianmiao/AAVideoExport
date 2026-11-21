param([ValidateRange(1,30)][int]$Iterations=10)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=Join-Path $root 'artifacts\runtime-recipes-final-il-20261030'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$launcher=Join-Path $root 'tests\VideoExport.RuntimeBootstrap.Tests\bin\isolated-aa-runtime\dotnet.exe'
$assembly=Join-Path $PSScriptRoot 'bin\Release\net6.0\AAVE.RuntimeRecipe.Tests.dll'
$payload=Join-Path $PSScriptRoot 'bin\Release\net6.0\AAVideoExport.RuntimeBootstrap.dll'
$hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash
$results=@()
foreach($iteration in 1..$Iterations) {
    foreach($mode in @('recipe','coexist')) {
        $label=('{0}-{1:D2}' -f $mode,$iteration)
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=$launcher; $start.WorkingDirectory=$root
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.Environment['AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION']='0'
        $start.Environment['AA_VIDEOEXPORT_RUNTIME_RECIPES']='0'
        $start.Environment['AA_VIDEOEXPORT_STARTUP_TRACE_FILE']=Join-Path $output "$label.trace"
        foreach($argument in @('exec','--fx-version','6.0.7',$assembly)) { $start.ArgumentList.Add($argument) }
        if($mode -eq 'coexist') { $start.ArgumentList.Add('--with-v1') }
        $child=[Diagnostics.Process]::Start($start)
        try {
            $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
            $finished=$child.WaitForExit(45000)
            if(-not $finished) { $child.Kill($true); $child.WaitForExit() }
            $text=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $output "$label.stdout.log"),$text)
            [IO.File]::WriteAllText((Join-Path $output "$label.stderr.log"),$errors)
            $match=[regex]::Match($text,'RESULT (\d+) passed; (\d+) failed; runtime=6\.0\.7')
            $row=[pscustomobject]@{Label=$label;Pid=$child.Id;ExitCode=$child.ExitCode;Finished=$finished;Passed=if($match.Success){[int]$match.Groups[1].Value}else{0};Failed=if($match.Success){[int]$match.Groups[2].Value}else{-1};BootstrapSha256=$hash}
            $results+=$row; $row | ConvertTo-Json -Compress
        } finally { $child.Dispose() }
    }
}
$unchanged=(Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash -eq $hash
$summary=[pscustomobject]@{ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');Runtime='6.0.7';Iterations=$Iterations;ProcessCount=$results.Count;AllPassed=$unchanged -and @($results | Where-Object {-not $_.Finished -or $_.ExitCode -ne 0 -or $_.Failed -ne 0}).Count -eq 0;PayloadUnchanged=$unchanged;BootstrapSha256=$hash;Results=$results;Scope='Independent managed processes only. No AA startup or generated native wrapper constructor was executed.'}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'repeated-tests.json') -Encoding utf8
if(-not $summary.AllPassed){exit 1}
