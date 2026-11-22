param()
$ErrorActionPreference = 'Stop'
$repo = 'E:\aamod\AAVE-zero-per-mod-20261121'
$lab = 'F:\AzureArchive_AAVE03Lab'
$exe = "$repo\tools\HiddenDesktopLauncher\bin\Release\net8.0\HiddenDesktopLauncher.exe"
$game = "$lab\AzureArchive.exe"
$story = 'E:\AAdata\data\saves\AAVE-OfficialAuthoring-20261030.aas'
$g6 = "$repo\artifacts\rvl-1.0.0\g6-final"
$plugins = "$lab\BepInEx\plugins"

function Set-Fixture([string]$name, [bool]$on) {
    $dll = Join-Path $plugins "$name.dll"
    $off = Join-Path $plugins "$name.dll.off"
    if ($on) {
        if (Test-Path -LiteralPath $off) { Rename-Item -LiteralPath $off -NewName "$name.dll" }
    } else {
        if (Test-Path -LiteralPath $dll) { Rename-Item -LiteralPath $dll -NewName "$name.dll.off" }
    }
}
function Stop-Game {
    Get-Process AzureArchive -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}
function Run-Export([string]$name, [string[]]$extra) {
    $out = Join-Path $g6 $name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Stop-Game
    $env:AAVE_TRACE = Join-Path $out 'runtime-trace.jsonl'
    $args = @($game, $lab, '--aa-export-story', $story, '--aa-export-output', $out, '--aa-export-exit') + $extra
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "export $name exited $LASTEXITCODE" }
    "done $name"
}

New-Item -ItemType Directory -Force -Path $g6 | Out-Null
Set-Fixture 'UnknownPassiveMod' $false
Set-Fixture 'UnknownClockMod' $false
Set-Fixture 'UnknownRenderOwnerMod' $false
Set-Fixture 'AAVideoExport.NativePath.Tests' $false

Run-Export 'product-final-double' @('--aa-export-count', '2')
Run-Export 'product-final-cancel' @('--aa-export-cancel-frame', '300')
Run-Export 'product-final-reexport' @('--aa-export-count', '2', '--aa-export-cancel-frame', '300')

Set-Fixture 'UnknownPassiveMod' $true
Run-Export 'product-final-passive' @()
Set-Fixture 'UnknownPassiveMod' $false

Set-Fixture 'UnknownClockMod' $true
Run-Export 'product-final-clock' @()
$clockLog = Join-Path $g6 'unknown-clock-events.log'
Get-Content "$lab\BepInEx\LogOutput.log" | Select-String 'UnknownClockMod event' | ForEach-Object { $_.Line } | Set-Content -Encoding utf8NoBOM $clockLog
Set-Fixture 'UnknownClockMod' $false

Set-Fixture 'UnknownRenderOwnerMod' $true
Run-Export 'product-final-owner-01' @()
Run-Export 'product-final-owner-02' @()
Set-Fixture 'UnknownRenderOwnerMod' $false

"G6 final matrix complete"
