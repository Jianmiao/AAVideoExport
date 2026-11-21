param()
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'NativeAcceptance.Guard.ps1'
if (!(Test-Path -LiteralPath $helper)) {
    Write-Output 'FAIL production state guard is unavailable'
    exit 1
}
. $helper
$root = Join-Path ([IO.Path]::GetTempPath()) ('aave-guard-test-' + [Guid]::NewGuid().ToString('N'))
$registrySubKey = 'Software\AAVE.Guard.Tests\' + [Guid]::NewGuid().ToString('N')
$passed = 0; $failed = 0
function Assert-Guard([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Assert-Bytes([string]$Path, [byte[]]$Expected) {
    Assert-Guard ([Convert]::ToHexString([IO.File]::ReadAllBytes($Path)) -eq [Convert]::ToHexString($Expected)) ('Bytes changed: ' + $Path)
}
function New-Fixture([string]$Name) {
    $caseRoot = Join-Path $root $Name
    $settings = Join-Path $caseRoot 'settings'; $sfx = Join-Path $caseRoot 'SfxFolderMod\config.txt'
    $state = Join-Path $caseRoot 'evidence'
    $null = New-Item -ItemType Directory -Path $settings,(Split-Path $sfx),$state
    $bytes = [byte[]](239,187,191,123,34,107,34,58,49,125,13,10)
    [IO.File]::WriteAllBytes((Join-Path $settings 'user_settings.json'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $settings 'user_settings.json.bk.1'), [byte[]](0,1,2,255))
    [IO.File]::WriteAllBytes((Join-Path $settings '.user_settings.json.original.tmp'), [byte[]](9,8,7))
    [IO.File]::WriteAllBytes($sfx, [byte[]](35,32,115,102,120,13,10))
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registrySubKey)
    try {
        $key.SetValue('ScreenWidth', 2538, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('BinaryPreference', [byte[]](1,0,255), [Microsoft.Win32.RegistryValueKind]::Binary)
        $key.SetValue('ExpandedLiteral', '%TEMP%\unchanged', [Microsoft.Win32.RegistryValueKind]::ExpandString)
    } finally { $key.Dispose() }
    $snapshot = Save-NativeAcceptanceState -SettingsRoot $settings -SfxConfigPath $sfx -RegistrySubKey $registrySubKey -StateRoot $state
    [pscustomobject]@{ Root=$caseRoot; Settings=$settings; Sfx=$sfx; State=$state; Bytes=$bytes; Snapshot=$snapshot }
}
function Mutate-Fixture($Fixture) {
    [IO.File]::WriteAllText((Join-Path $Fixture.Settings 'user_settings.json'), 'changed')
    [IO.File]::WriteAllText((Join-Path $Fixture.Settings 'user_settings.json.bk.1'), 'changed backup')
    [IO.File]::WriteAllText($Fixture.Sfx, 'changed Sfx')
    [IO.File]::WriteAllText((Join-Path $Fixture.Settings '.user_settings.json.created.tmp'), 'new temporary setting')
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrySubKey, $true)
    try {
        $key.SetValue('ScreenWidth', 640, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.DeleteValue('BinaryPreference')
        $key.SetValue('NewSession', 'only this test')
    } finally { $key.Dispose() }
}
function Assert-Fixture($Fixture) {
    Assert-Bytes (Join-Path $Fixture.Settings 'user_settings.json') $Fixture.Bytes
    Assert-Bytes (Join-Path $Fixture.Settings 'user_settings.json.bk.1') ([byte[]](0,1,2,255))
    Assert-Bytes $Fixture.Sfx ([byte[]](35,32,115,102,120,13,10))
    Assert-Bytes (Join-Path $Fixture.Settings '.user_settings.json.original.tmp') ([byte[]](9,8,7))
    $actual = Get-NativeAcceptanceRegistryState -SubKey $registrySubKey
    Assert-Guard ((ConvertTo-Json $actual -Depth 20 -Compress) -ceq (ConvertTo-Json $Fixture.Snapshot.Registry -Depth 20 -Compress)) 'Typed registry state was not restored'
}
function Run-GuardTest([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Output ('PASS ' + $Name) }
    catch { $script:failed++; Write-Output ('FAIL ' + $Name + ': ' + $_.Exception.Message) }
    finally { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registrySubKey, $false) }
}
try {
    Run-GuardTest 'restores original bytes typed PlayerPrefs and moves new temporary settings' {
        $f = New-Fixture 'normal'; Mutate-Fixture $f
        $result = Restore-NativeAcceptanceState -Snapshot $f.Snapshot
        Assert-Guard $result.Restored 'Normal restore failed'
        Assert-Fixture $f
        Assert-Guard (!(Test-Path -LiteralPath (Join-Path $f.Settings '.user_settings.json.created.tmp'))) 'New temporary settings stayed in the user scope'
        Assert-Guard ($result.ArchivedNewFiles.Count -eq 1) 'New temporary settings were not archived'
    }
    Run-GuardTest 'evidence write failure still restores every original byte and registry value' {
        $f = New-Fixture 'evidence-failure'; Mutate-Fixture $f
        $result = Restore-NativeAcceptanceState -Snapshot $f.Snapshot -EvidenceWriter { param($Path,$Bytes) throw [IO.IOException]::new('injected evidence disk failure') }
        Assert-Fixture $f
        Assert-Guard $result.Restored 'Evidence failure blocked core restoration'
        Assert-Guard ($result.EvidenceErrors.Count -gt 0) 'Evidence failure was concealed'
        Assert-Guard ($result.RestoreErrors.Count -eq 0) 'Core restore reported an unexpected failure'
    }
    Run-GuardTest 'one inaccessible original does not prevent other files or PlayerPrefs restoration' {
        $f = New-Fixture 'individual-failure'; Mutate-Fixture $f
        $blocked = Join-Path $f.Settings 'user_settings.json.bk.1'
        Remove-Item -LiteralPath $blocked
        $null = New-Item -ItemType Directory -Path $blocked
        $result = Restore-NativeAcceptanceState -Snapshot $f.Snapshot
        Assert-Guard (!$result.Restored -and $result.RestoreErrors.Count -gt 0) 'Individual restoration failure was concealed'
        Assert-Bytes (Join-Path $f.Settings 'user_settings.json') $f.Bytes
        Assert-Bytes $f.Sfx ([byte[]](35,32,115,102,120,13,10))
        $actual = Get-NativeAcceptanceRegistryState -SubKey $registrySubKey
        Assert-Guard ((ConvertTo-Json $actual -Depth 20 -Compress) -ceq (ConvertTo-Json $f.Snapshot.Registry -Depth 20 -Compress)) 'A file error blocked registry restoration'
    }
    Run-GuardTest 'deadline cleanup retains a timed-out result for acceptance evidence' {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach($arg in @('-NoProfile','-Command','[Console]::WriteLine("before timeout"); Start-Sleep -Seconds 120')) { $start.ArgumentList.Add($arg) }
        $result = Invoke-NativeAcceptanceProcess -StartInfo $start -TimeoutSeconds 1
        Assert-Guard $result.TimedOut 'Deadline was not reported'
        Assert-Guard ($result.StdOut -match 'before timeout') 'Output before the deadline was lost'
        Assert-Guard (!(Get-Process -Id $result.Id -ErrorAction SilentlyContinue)) 'Timed-out owned child survived'
    }
    Run-GuardTest 'exception after child start closes only the owned harmless process' {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach($arg in @('-NoProfile','-Command','Start-Sleep -Seconds 120')) { $start.ArgumentList.Add($arg) }
        $survivor = [Diagnostics.Process]::Start($start)
        $script:ownedPid = 0; $caught = $false
        try {
            try { $null = Invoke-NativeAcceptanceProcess -StartInfo $start -TimeoutSeconds 30 -AfterStart { param($Process) $script:ownedPid=$Process.Id; throw 'injected post-start error' } }
            catch { $caught = $_.Exception.Message -match 'injected post-start error' }
            Assert-Guard $caught 'Injected runner error was lost'
            Assert-Guard ($script:ownedPid -gt 0 -and !(Get-Process -Id $script:ownedPid -ErrorAction SilentlyContinue)) 'Owned child survived exception cleanup'
            Assert-Guard (!$survivor.HasExited) 'Cleanup touched an unrelated process'
        } finally { if(!$survivor.HasExited){$survivor.Kill();$survivor.WaitForExit()};$survivor.Dispose() }
    }
} finally {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registrySubKey, $false)
    if(Test-Path -LiteralPath $root) {
        $resolved = [IO.Path]::GetFullPath($root)
        $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('aave-guard-test-')) { throw 'Refusing cleanup outside this test fixture' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output ("RESULT $passed passed; $failed failed; real AA and real user settings were not used")
if($failed){exit 1}
