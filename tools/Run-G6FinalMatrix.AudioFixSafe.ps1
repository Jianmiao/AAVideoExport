param([string]$OutputRoot = 'E:\AAVE-export-evidence\g6-audiofix-20261126')
$ErrorActionPreference = 'Stop'
$repo = 'E:\aamod\AAVE-zero-per-mod-20261121'
$lab = 'F:\AzureArchive_AAVE03Lab'
$exe = "$repo\tools\HiddenDesktopLauncher\bin\Release\net8.0\HiddenDesktopLauncher.exe"
$game = "$lab\AzureArchive.exe"
$story = 'E:\AAdata\data\saves\AAVE-OfficialAuthoring-20261030.aas'
$g6 = [IO.Path]::GetFullPath($OutputRoot)
$plugins = "$lab\BepInEx\plugins"
$log = Join-Path $g6 'matrix.log'
$errLog = Join-Path $g6 'matrix.err.txt'

function Write-Log([string]$text) {
    [IO.File]::AppendAllText($log, ('[{0}] {1}{2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text, [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Set-Fixture([string]$name, [bool]$on) {
    $dll = Join-Path $plugins "$name.dll"
    $off = Join-Path $plugins "$name.dll.off"
    if ($on) {
        if (Test-Path -LiteralPath $off -PathType Leaf) { Move-Item -LiteralPath $off -Destination $dll -Force }
    } else {
        if (Test-Path -LiteralPath $dll -PathType Leaf) { Move-Item -LiteralPath $dll -Destination $off -Force }
    }
}

function Stop-Lab {
    Get-Process AzureArchive -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $game } catch { $false }
    } | ForEach-Object {
        Write-Log ("stopping lab pid=" + $_.Id)
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Run-Export([string]$name, [string[]]$extra) {
    $out = Join-Path $g6 $name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Stop-Lab
    $env:AAVE_TRACE = Join-Path $out 'runtime-trace.jsonl'
    $launcherArgs = @($game, $lab, '--aa-export-story', $story, '--aa-export-output', $out, '--aa-export-exit') + $extra
    Write-Log ("start " + $name + " args=" + ($extra -join ' '))
    & $exe @launcherArgs 2>&1 | ForEach-Object { Write-Log ("launcher: " + $_) }
    if ($LASTEXITCODE -ne 0) { throw "export $name exited $LASTEXITCODE" }
    Write-Log ("done " + $name)
}

New-Item -ItemType Directory -Force -Path $g6 | Out-Null
Set-Content -LiteralPath $log -Value ("matrix start " + (Get-Date -Format o)) -Encoding utf8NoBOM
Set-Content -LiteralPath $errLog -Value '' -Encoding utf8NoBOM

try {
    Set-Fixture 'UnknownPassiveMod' $false
    Set-Fixture 'UnknownClockMod' $false
    Set-Fixture 'UnknownRenderOwnerMod' $false
    Set-Fixture 'AAVideoExport.NativePath.Tests' $false

    Run-Export 'product-normal' @()
    Run-Export 'product-final-double' @('--aa-export-count', '2')
    # Same-process cancel+count2 is not a supported product path; cancel cleanup
    # and a fresh-process re-export are validated separately.
    Run-Export 'product-final-cancel' @('--aa-export-cancel-frame', '300')
    Run-Export 'product-final-reexport' @()

    Set-Fixture 'UnknownPassiveMod' $true
    Run-Export 'product-final-passive' @()
    Set-Fixture 'UnknownPassiveMod' $false

    Set-Fixture 'UnknownClockMod' $true
    Run-Export 'product-final-clock' @()
    $clockLog = Join-Path $g6 'unknown-clock-events.log'
    Get-Content "$lab\BepInEx\LogOutput.log" | Select-String 'UnknownClockMod event' | ForEach-Object { $_.Line } | Set-Content -Encoding utf8NoBOM -LiteralPath $clockLog
    Set-Fixture 'UnknownClockMod' $false

    Set-Fixture 'UnknownRenderOwnerMod' $true
    Run-Export 'product-final-owner-01' @()
    Run-Export 'product-final-owner-02' @()
    Set-Fixture 'UnknownRenderOwnerMod' $false

    Write-Log 'G6 final matrix complete'
} finally {
    Set-Fixture 'UnknownPassiveMod' $false
    Set-Fixture 'UnknownClockMod' $false
    Set-Fixture 'UnknownRenderOwnerMod' $false
    Set-Fixture 'AAVideoExport.NativePath.Tests' $false
    Stop-Lab
}
