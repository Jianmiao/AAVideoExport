# Narrow state and process guards used by native acceptance. Dot-sourcing does
# not read or change user state. Tests pass a temporary file and registry scope.
function Get-NativeAcceptanceSettingsFiles {
    param([string]$SettingsRoot, [string]$SfxConfigPath)
    if (Test-Path -LiteralPath $SettingsRoot) {
        Get-ChildItem -LiteralPath $SettingsRoot -File -Force | Where-Object {
            $_.Name -like 'user_settings.json*' -or $_.Name -like '.user_settings.json.*.tmp'
        }
    }
    if (Test-Path -LiteralPath $SfxConfigPath -PathType Leaf) { Get-Item -LiteralPath $SfxConfigPath }
}

function Get-NativeAcceptanceRegistryState {
    param([Parameter(Mandatory=$true)][string]$SubKey)
    $rootKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey, $false)
    $keys = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $rootKey) {
        $walk = {
            param($Key, [string]$Relative)
            $values = @($Key.GetValueNames() | Sort-Object | ForEach-Object {
                [pscustomobject]@{ Name=$_; Kind=$Key.GetValueKind($_).ToString(); Value=$Key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            })
            $keys.Add([pscustomobject]@{ Relative=$Relative; Values=$values })
            foreach ($childName in @($Key.GetSubKeyNames() | Sort-Object)) {
                $child = $Key.OpenSubKey($childName, $false)
                try { & $walk $child $(if($Relative){$Relative + '\' + $childName}else{$childName}) }
                finally { if($null -ne $child){$child.Dispose()} }
            }
        }
        try { & $walk $rootKey '' } finally { $rootKey.Dispose() }
    }
    [pscustomobject]@{ SubKey=$SubKey; Exists=($null -ne $rootKey); Keys=@($keys.ToArray()) }
}

function Restore-NativeAcceptanceRegistryState {
    param([Parameter(Mandatory=$true)]$Snapshot)
    $current = Get-NativeAcceptanceRegistryState -SubKey $Snapshot.SubKey
    if (!$Snapshot.Exists) {
        if ($current.Exists) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Snapshot.SubKey, $false) }
    } else {
        $originalKeys = @{}
        foreach($row in $Snapshot.Keys){$originalKeys[$row.Relative] = $row}
        foreach($row in @($current.Keys | Sort-Object { $_.Relative.Length } -Descending)) {
            if(!$originalKeys.ContainsKey($row.Relative)) {
                [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(($Snapshot.SubKey + '\' + $row.Relative), $false)
            }
        }
        foreach($row in $Snapshot.Keys) {
            $path = $Snapshot.SubKey + $(if($row.Relative){'\' + $row.Relative}else{''})
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($path)
            try {
                $names = @($row.Values | ForEach-Object Name)
                foreach($name in $key.GetValueNames()){if($names -notcontains $name){$key.DeleteValue($name,$false)}}
                foreach($entry in $row.Values) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::$($entry.Kind)
                    $value = $entry.Value
                    if($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary){$value = [byte[]]$value}
                    elseif($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString){$value = [string[]]$value}
                    $key.SetValue($entry.Name,$value,$kind)
                }
            } finally { $key.Dispose() }
        }
    }
    $after = Get-NativeAcceptanceRegistryState -SubKey $Snapshot.SubKey
    if ((ConvertTo-Json $after -Depth 30 -Compress) -cne (ConvertTo-Json $Snapshot -Depth 30 -Compress)) {
        throw 'PlayerPrefs restoration verification failed.'
    }
}

function Save-NativeAcceptanceState {
    param(
        [Parameter(Mandatory=$true)][string]$SettingsRoot,
        [Parameter(Mandatory=$true)][string]$SfxConfigPath,
        [Parameter(Mandatory=$true)][string]$RegistrySubKey,
        [Parameter(Mandatory=$true)][string]$StateRoot
    )
    if (!(Test-Path -LiteralPath (Join-Path $SettingsRoot 'user_settings.json') -PathType Leaf)) {
        throw 'The existing AA settings file must be present before testing.'
    }
    $files = @{}
    foreach($file in @(Get-NativeAcceptanceSettingsFiles $SettingsRoot $SfxConfigPath)) {
        $label = if($file.FullName -eq [IO.Path]::GetFullPath($SfxConfigPath)) {'sfx.config.txt'}else{'settings.' + $file.Name}
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $files[$file.FullName] = [pscustomobject]@{ Path=$file.FullName; Label=$label; Bytes=$bytes }
        [IO.File]::WriteAllBytes((Join-Path $StateRoot ($label + '.before')),$bytes)
    }
    $snapshot = [pscustomobject]@{
        SettingsRoot=[IO.Path]::GetFullPath($SettingsRoot); SfxConfigPath=[IO.Path]::GetFullPath($SfxConfigPath)
        StateRoot=[IO.Path]::GetFullPath($StateRoot); Files=$files
        Registry=(Get-NativeAcceptanceRegistryState -SubKey $RegistrySubKey)
    }
    # A durable copy allows manual recovery even if the outer shell is killed.
    $snapshot | Export-Clixml -LiteralPath (Join-Path $StateRoot 'state-snapshot.clixml') -Depth 30
    return $snapshot
}

function Restore-NativeAcceptanceState {
    param(
        [Parameter(Mandatory=$true)]$Snapshot,
        [scriptblock]$EvidenceWriter = { param($Path,$Bytes) [IO.File]::WriteAllBytes($Path,$Bytes) }
    )
    $restoreErrors = [System.Collections.Generic.List[string]]::new()
    $evidenceErrors = [System.Collections.Generic.List[string]]::new()
    $evidence = [System.Collections.Generic.List[object]]::new()
    $created = [System.Collections.Generic.List[object]]::new()
    $restored = [System.Collections.Generic.List[object]]::new()
    $archived = [System.Collections.Generic.List[string]]::new()
    # Capture changed bytes in memory. Evidence I/O never guards core recovery.
    try {
        foreach($file in @(Get-NativeAcceptanceSettingsFiles $Snapshot.SettingsRoot $Snapshot.SfxConfigPath)) {
            $label = if($file.FullName -eq $Snapshot.SfxConfigPath){'sfx.config.txt'}else{'settings.' + $file.Name}
            try {
                $evidence.Add([pscustomobject]@{ Name=$label + '.after'; Bytes=[IO.File]::ReadAllBytes($file.FullName) })
            } catch { $evidenceErrors.Add('Reading after-state ' + $file.FullName + ': ' + $_.Exception.Message) }
            if(!$Snapshot.Files.ContainsKey($file.FullName)) {
                $created.Add([pscustomobject]@{ Path=$file.FullName; Name=$label + '.created-by-test' })
            }
        }
    } catch { $evidenceErrors.Add('Enumerating changed settings: ' + $_.Exception.Message) }
    try {
        $currentRegistry = Get-NativeAcceptanceRegistryState -SubKey $Snapshot.Registry.SubKey
        $evidence.Add([pscustomobject]@{ Name='playerprefs.after.json'; Bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $currentRegistry -Depth 30)) })
    } catch { $evidenceErrors.Add('Reading PlayerPrefs after-state: ' + $_.Exception.Message) }

    foreach($entry in $Snapshot.Files.Values) {
        try {
            [IO.File]::WriteAllBytes($entry.Path,[byte[]]$entry.Bytes)
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$entry.Bytes))
            $actual = (Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash
            if($actual -ne $expected){throw ('Original-byte verification failed: ' + $entry.Path)}
            $restored.Add([pscustomobject]@{ Path=$entry.Path; SHA256=$actual; Restored=$true })
        } catch { $restoreErrors.Add('Restoring ' + $entry.Path + ': ' + $_.Exception.Message) }
    }
    $registryRestored = $false
    try { Restore-NativeAcceptanceRegistryState $Snapshot.Registry; $registryRestored=$true }
    catch { $restoreErrors.Add('Restoring PlayerPrefs: ' + $_.Exception.Message) }

    # Only newly created files inside the exact protected scope are moved.
    # Existing originals were restored already, even if archiving now fails.
    foreach($entry in $created) {
        try {
            $destination = Join-Path $Snapshot.StateRoot $entry.Name
            Move-Item -LiteralPath $entry.Path -Destination $destination -ErrorAction Stop
            $archived.Add($destination)
        } catch { $restoreErrors.Add('Archiving new setting ' + $entry.Path + ': ' + $_.Exception.Message) }
    }
    foreach($entry in $evidence) {
        try { $null = & $EvidenceWriter (Join-Path $Snapshot.StateRoot $entry.Name) $entry.Bytes }
        catch { $evidenceErrors.Add('Writing evidence ' + $entry.Name + ': ' + $_.Exception.Message) }
    }
    $result = [pscustomobject]@{
        Restored=($restoreErrors.Count -eq 0); OriginalFiles=@($restored.ToArray()); RegistryRestored=$registryRestored
        ArchivedNewFiles=@($archived.ToArray()); RestoreErrors=@($restoreErrors.ToArray()); EvidenceErrors=@($evidenceErrors.ToArray())
    }
    try { $null = & $EvidenceWriter (Join-Path $Snapshot.StateRoot 'settings-restoration.json') ([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $result -Depth 30))) }
    catch { $evidenceErrors.Add('Writing restoration report: ' + $_.Exception.Message); $result.EvidenceErrors=@($evidenceErrors.ToArray()) }
    return $result
}

function Invoke-NativeAcceptanceProcess {
    param(
        [Parameter(Mandatory=$true)][Diagnostics.ProcessStartInfo]$StartInfo,
        [Parameter(Mandatory=$true)][int]$TimeoutSeconds,
        [scriptblock]$AfterStart
    )
    $process = [Diagnostics.Process]::new(); $process.StartInfo=$StartInfo
    $started=$false; $timedOut=$false; $failure=$null; $cleanupFailure=$null; $result=$null
    try {
        $started=$process.Start()
        if(!$started){throw 'The owned native process did not start.'}
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if($AfterStart){$null = & $AfterStart $process}
        $timedOut=!$process.WaitForExit($TimeoutSeconds * 1000)
    } catch { $failure=$_.Exception }
    finally {
        if($started) {
            try {
                if(!$process.HasExited) {
                    $null=$process.CloseMainWindow()
                    if(!$process.WaitForExit(2000)) {
                        $process.Kill($true)
                        if(!$process.WaitForExit(5000)){throw 'Owned native process did not exit after termination.'}
                    }
                }
            } catch { $cleanupFailure=$_.Exception }
        }
        if($started -and !$failure -and !$cleanupFailure) {
            try {
                if(!$stdout.Wait(10000) -or !$stderr.Wait(10000)){throw 'Owned process output pipes did not close within 10 seconds.'}
                $result=[pscustomobject]@{ Id=$process.Id; ExitCode=$process.ExitCode; TimedOut=$timedOut; StdOut=$stdout.GetAwaiter().GetResult(); StdErr=$stderr.GetAwaiter().GetResult() }
            } catch { $failure=$_.Exception }
        }
        $process.Dispose()
    }
    if($cleanupFailure){throw [AggregateException]::new('Owned process cleanup failed; do not restore global state while it remains live.',[Exception[]]@($failure,$cleanupFailure).Where({$null -ne $_}))}
    if($failure){throw $failure}
    return $result
}
