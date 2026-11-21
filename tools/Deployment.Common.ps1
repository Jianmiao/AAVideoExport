#requires -Version 7.2
Set-StrictMode -Version Latest

function Get-AaveHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() }
function Get-AaveAuditMode($Descriptor) {
    $property=$Descriptor.PSObject.Properties['AuditMode']
    if(!$property){return $true} # v1 descriptors before build separation were audited.
    if($property.Value -isnot [bool]){throw 'AuditMode must be a JSON boolean.'}
    return [bool]$property.Value
}
function Get-AaveAssemblyAuditMode([string]$CorePath) {
    # Read PE metadata only. Do not load a product assembly into PowerShell or
    # invoke a getter/module initializer while checking its build provenance.
    $stream=[IO.File]::OpenRead($CorePath);$pe=[Reflection.PortableExecutable.PEReader]::new($stream)
    try{
        $metadata=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach($handle in $metadata.TypeDefinitions){
            $type=$metadata.GetTypeDefinition($handle)
            if($metadata.GetString($type.Namespace)-cne'AAVideoExport.Core'-or$metadata.GetString($type.Name)-cne'ExportBuildProfile'){continue}
            foreach($methodHandle in $type.GetMethods()){
                $method=$metadata.GetMethodDefinition($methodHandle)
                if($metadata.GetString($method.Name)-cne'get_DevelopmentAuditEnabled'){continue}
                if([Convert]::ToHexString($metadata.GetBlobBytes($method.Signature))-ne'000002'-or!$method.Attributes.HasFlag([Reflection.MethodAttributes]::Static)-or!$method.Attributes.HasFlag([Reflection.MethodAttributes]::Public)){throw 'Core audit build mode API is invalid.'}
                $body=[Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe,$method.RelativeVirtualAddress)
                $bytes=[Convert]::ToHexString($body.GetILBytes())
                if($bytes-in@('162A','00162A')){return $false}
                if($bytes-in@('172A','00172A')){return $true}
                throw 'Core audit build mode is not a literal build-time boolean.'
            }
        }
        throw 'The new delivery lacks an explicit Core audit build mode.'
    }finally{$pe.Dispose();$stream.Dispose()}
}
function Get-AaveSafePath([string]$Root,[string]$Relative) {
    $base=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if([string]::IsNullOrWhiteSpace($Relative)-or[IO.Path]::IsPathRooted($Relative)-or$Relative.Contains(':')-or($Relative -split '[/\\]') -contains '..'){throw 'Unsafe relative deployment path.'}
    $full=[IO.Path]::GetFullPath((Join-Path $base $Relative))
    if(!$full.StartsWith($base+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Deployment path leaves its named root.'}
    $cursor=$full
    while($cursor.Length -ge $base.Length){
        if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw "Reparse point is outside the deployment contract: $cursor"}}
        if($cursor -eq $base){break};$cursor=Split-Path -Parent $cursor
    }
    return $full
}
function Write-AaveJson([string]$Path,$Value) { Set-AaveAtomicBytes $Path ([Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 100))) }
function Set-AaveAtomicBytes([string]$Path,[byte[]]$Bytes) {
    $temporary=$Path+'.aave-'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if([IO.File]::Exists($Path)){
            # Preserve a true null optional backup path; direct PowerShell
            # overload conversion can turn $null into an empty string.
            $replace=[IO.File].GetMethod('Replace',[Type[]]@([string],[string],[string],[bool]))
            $null=$replace.Invoke($null,[object[]]@($temporary,$Path,$null,$true))
        }else{[IO.File]::Move($temporary,$Path,$false)}
    }
    finally {if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
function Assert-AaveActiveProfile([string]$Root,[string]$Expected) {
    $active=[IO.File]::ReadAllText((Get-AaveSafePath $Root 'ActiveProfile.txt'),[Text.UTF8Encoding]::new($false,$true)).TrimEnd("`r","`n")
    if($active -cne $Expected){throw 'Active profile changed after installation planning; no other profile will be selected or overwritten.'}
}
function Read-AaveDelivery([string]$PackageRoot) {
    $root=(Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).Path
    $descriptor=Get-Content -LiteralPath (Get-AaveSafePath $root 'delivery-manifest.json') -Raw|ConvertFrom-Json -Depth 100
    if($descriptor.Schema -ne 'aave.delivery.v1'-or$descriptor.Name -ne 'AAVideoExport'-or$descriptor.Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid AAVE delivery descriptor.'}
    $auditMode=Get-AaveAuditMode $descriptor
    if($descriptor.PayloadRelativePath -ne ('AAVideoExport/'+$descriptor.Version)){throw 'Unexpected runtime package layout.'}
    if($auditMode){if($descriptor.BootstrapRelativePath -ne 'bootstrap/AAVideoExport.RuntimeBootstrap.dll'){throw 'Unexpected audit bootstrap package layout.'}}
    elseif($descriptor.BootstrapRelativePath-or$descriptor.BootstrapSha256){throw 'Production delivery cannot declare an audit bootstrap.'}
    $checksums=@{}
    foreach($line in [IO.File]::ReadAllLines((Get-AaveSafePath $root 'SHA256SUMS.txt'))){
        if(!$line){continue};if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw 'Invalid package checksum line.'}
        $sha=$Matches[1];$relative=$Matches[2]
        if($checksums.ContainsKey($relative)){throw 'Duplicate package checksum path.'}
        $path=Get-AaveSafePath $root $relative
        if(!(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-AaveHash $path)-ne$sha){throw "Package checksum mismatch: $relative"}
        $checksums[$relative]=$sha
    }
    $actual=@(Get-ChildItem -LiteralPath $root -File -Recurse -Force|Where-Object {$_.FullName -ne (Join-Path $root 'SHA256SUMS.txt')})
    if($actual.Count -ne $checksums.Count){throw 'Package checksum coverage is incomplete.'}
    foreach($file in $actual){$relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/');if(!$checksums.ContainsKey($relative)){throw "Unlisted package file: $relative"}}
    $payload=Get-AaveSafePath $root $descriptor.PayloadRelativePath
    $manifest=Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw|ConvertFrom-Json
    if($manifest.name -ne 'AAVideoExport'-or$manifest.version_number -ne $descriptor.Version){throw 'Payload manifest and delivery version differ.'}
    foreach($name in @('AAVideoExport.dll','AAVideoExport.Core.dll','Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.D3DCompiler.dll','Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll','icon.png')){
        if(!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)){throw "Missing runtime file: $name"}
    }
    foreach($name in @('AAVideoExport.dll','AAVideoExport.Core.dll')){
        $identity=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload $name))
        if($identity.Name -ne [IO.Path]::GetFileNameWithoutExtension($name)-or$identity.Version.ToString() -ne ($descriptor.Version+'.0')){throw "Runtime assembly version mismatch: $name"}
    }
    $bootstrap=$null
    if($auditMode){
        $bootstrap=Get-AaveSafePath $root $descriptor.BootstrapRelativePath
        if([Reflection.AssemblyName]::GetAssemblyName($bootstrap).Name -ne 'AAVideoExport.RuntimeBootstrap'){throw 'Incorrect owned early bootstrap assembly.'}
    }elseif(@($actual|Where-Object Name -eq 'AAVideoExport.RuntimeBootstrap.dll').Count){throw 'Production delivery contains a forbidden audit bootstrap.'}
    if($descriptor.PSObject.Properties['AuditMode']-and(Get-AaveAssemblyAuditMode (Join-Path $payload 'AAVideoExport.Core.dll'))-ne$auditMode){throw 'Delivery AuditMode differs from the actual Core build mode.'}
    foreach($name in @('Install-Mod.ps1','Rollback-Mod.ps1','Deployment.Common.ps1','Start-AA-AAVE.ps1')){if(!(Test-Path -LiteralPath (Join-Path $root $name))){throw "Missing delivery tool: $name"}}
    if((Get-AaveHash (Join-Path $payload 'AAVideoExport.dll')) -ne $descriptor.PluginSha256.ToLowerInvariant()-or
        (Get-AaveHash (Join-Path $payload 'AAVideoExport.Core.dll')) -ne $descriptor.CoreSha256.ToLowerInvariant()-or
        ($auditMode-and(Get-AaveHash $bootstrap) -ne $descriptor.BootstrapSha256.ToLowerInvariant())){throw 'Delivery identity hashes do not match the copied payload.'}
    return [pscustomobject]@{Root=$root;Descriptor=$descriptor;Payload=$payload;Bootstrap=$bootstrap;AuditMode=$auditMode;Checksums=$checksums}
}
function New-AaveInstallPlan([string]$AAInstallPath,[string]$PackageRoot,[string]$ExpectedExistingBootstrapSha256='') {
    $aa=(Resolve-Path -LiteralPath $AAInstallPath -ErrorAction Stop).Path
    foreach($name in @('AzureArchive.exe','BepInEx/patchers/ModTheAzureArchive.dll')){if(!(Test-Path -LiteralPath (Get-AaveSafePath $aa $name) -PathType Leaf)){throw 'Expected an existing AA/BepInEx/ModTheAzureArchive installation.'}}
    $package=Read-AaveDelivery $PackageRoot;$version=$package.Descriptor.Version
    $target=Get-AaveSafePath $aa ('mods/AAVideoExport/'+$version)
    if(Test-Path -LiteralPath $target){throw 'This version already exists. No same-version directory will be overwritten.'}
    $active=[IO.File]::ReadAllText((Get-AaveSafePath $aa 'ActiveProfile.txt'),[Text.UTF8Encoding]::new($false,$true)).TrimEnd("`r","`n")
    if(!$active-or$active -ne $active.Trim()-or$active -in @('.','..')-or$active.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0-or$active.Contains('/')-or$active.Contains('\')){throw 'Active profile name is invalid.'}
    $profile=Get-AaveSafePath $aa ('profiles/'+$active+'/modconfig.json')
    $profileBytes=[IO.File]::ReadAllBytes($profile);$profileData=[Text.Encoding]::UTF8.GetString($profileBytes).TrimStart([char]0xFEFF)|ConvertFrom-Json -Depth 100
    $entries=@($profileData.EnabledMods|Where-Object {$_.name -ieq 'AAVideoExport'})
    if($entries.Count -ne 1-or$entries[0].name -cne 'AAVideoExport'){throw 'The active profile must contain exactly one existing AAVideoExport selection.'}
    $oldVersion=[string]$entries[0].version;$entries[0].version=$version
    $newProfile=[Text.UTF8Encoding]::new($false).GetBytes(($profileData|ConvertTo-Json -Depth 100))
    $bootstrap=Get-AaveSafePath $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll'
    $bootstrapBefore=$null
    if(Test-Path -LiteralPath $bootstrap){
        $bootstrapBefore=Get-AaveHash $bootstrap
        if(!$ExpectedExistingBootstrapSha256-or$bootstrapBefore -ne $ExpectedExistingBootstrapSha256.ToLowerInvariant()){throw 'Existing owned bootstrap requires its exact reviewed SHA-256; no bootstrap was overwritten.'}
        if([Reflection.AssemblyName]::GetAssemblyName($bootstrap).Name -ne 'AAVideoExport.RuntimeBootstrap'){throw 'Existing bootstrap path is not the owned AAVE assembly.'}
    }
    $launcher=Get-AaveSafePath $aa ('Start-AA-AAVE-'+$version+'.ps1')
    $launcherCmd=Get-AaveSafePath $aa ('Start-AA-AAVE-'+$version+'.cmd')
    $powerShellPath=Join-Path $PSHOME 'pwsh.exe'
    if(!(Test-Path -LiteralPath $powerShellPath -PathType Leaf)-or$powerShellPath.IndexOfAny([char[]]'"%')-ge0){throw 'The current verified PowerShell 7 executable path cannot be embedded safely in the launcher.'}
    $rollback=Get-AaveSafePath $aa ('Rollback-AA-AAVE-'+$version+'.ps1')
    $common=Get-AaveSafePath $aa ('AAVideoExport-Deployment.Common-'+$version+'.ps1')
    $installedDescriptor=Get-AaveSafePath $aa ('AAVideoExport-install-'+$version+'.json')
    $launcherErrors=$null;$launcherTokens=$null
    $launcherAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $package.Root 'Start-AA-AAVE.ps1'),[ref]$launcherTokens,[ref]$launcherErrors)
    if($launcherErrors.Count){throw 'Package launcher syntax is invalid.'}
    $launcherSupportsDevelopmentAudit=$launcherAst.ParamBlock-and@($launcherAst.ParamBlock.Parameters|Where-Object {$_.Name.VariablePath.UserPath-eq'DevelopmentAudit'}).Count-eq1
    if($package.Descriptor.PSObject.Properties['AuditMode']-and!$launcherSupportsDevelopmentAudit){throw 'A build-separated delivery requires an explicit development-audit launcher contract.'}
    foreach($path in @($launcher,$launcherCmd,$rollback,$common,$installedDescriptor)){if(Test-Path -LiteralPath $path){throw "Owned delivery entry already exists; nothing overwritten: $path"}}
    return [pscustomobject]@{AA=$aa;Package=$package;Version=$version;AuditMode=$package.AuditMode;Target=$target;Profile=$profile;ProfileBytes=$profileBytes;ProfileSha256=Get-AaveHash $profile;NewProfileBytes=$newProfile;ActiveProfile=$active;OldVersion=$oldVersion;Bootstrap=$bootstrap;BootstrapBefore=$bootstrapBefore;Launcher=$launcher;LauncherCmd=$launcherCmd;LauncherSupportsDevelopmentAudit=[bool]$launcherSupportsDevelopmentAudit;PowerShellPath=$powerShellPath;Rollback=$rollback;Common=$common;InstalledDescriptor=$installedDescriptor}
}
function Undo-AaveInstallReceipt([string]$ReceiptPath) {
    if(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){throw 'Close AA before rollback; no files were changed.'}
    $receiptFile=(Resolve-Path -LiteralPath $ReceiptPath -ErrorAction Stop).Path
    $r=Get-Content -LiteralPath $receiptFile -Raw|ConvertFrom-Json -Depth 100
    if($r.Schema -ne 'aave.install-receipt.v1'-or$r.Version -notmatch '^\d+\.\d+\.\d+$'-or$r.State -eq 'RolledBack'){throw 'Invalid or already rolled back installation receipt.'}
    $aa=(Resolve-Path -LiteralPath $r.AAInstallPath).Path
    $target=Get-AaveSafePath $aa ('mods/AAVideoExport/'+$r.Version)
    $profile=Get-AaveSafePath $aa ('profiles/'+$r.ActiveProfile+'/modconfig.json')
    $activeNow=[IO.File]::ReadAllText((Get-AaveSafePath $aa 'ActiveProfile.txt'),[Text.UTF8Encoding]::new($false,$true)).TrimEnd("`r","`n")
    if($activeNow -cne $r.ActiveProfile){throw 'Active profile changed after installation; rollback will not alter another active setup.'}
    $bootstrap=Get-AaveSafePath $aa 'BepInEx/patchers/AAVideoExport.RuntimeBootstrap.dll'
    $backupRoot=Split-Path -Parent $receiptFile
    $allowedBackupRoot=Get-AaveSafePath $aa 'AAVideoExport-install-backups'
    if(!$backupRoot.StartsWith($allowedBackupRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Receipt is outside the owned backup directory.'}
    $originalProfile=Join-Path $backupRoot 'profile.original.json'
    if((Get-AaveHash $originalProfile)-ne$r.ProfileBeforeSha256){throw 'Original profile backup was changed.'}
    $currentProfile=Get-AaveHash $profile
    if($currentProfile -ne $r.ProfileBeforeSha256-and$currentProfile -ne $r.ProfileInstalledSha256){throw 'Active profile changed after installation; rollback will not overwrite later Mod selections.'}
    if(Test-Path -LiteralPath $bootstrap){$currentBoot=Get-AaveHash $bootstrap;if($currentBoot -ne $r.BootstrapInstalledSha256-and$currentBoot -ne $r.BootstrapBeforeSha256){throw 'Owned bootstrap changed after installation; rollback stopped.'}}
    if($r.BootstrapExisted-and(Get-AaveHash (Join-Path $backupRoot 'bootstrap.original.dll'))-ne$r.BootstrapBeforeSha256){throw 'Original bootstrap backup was changed.'}
    $expected=@{}
    foreach($item in $r.NewFiles){
        $path=Get-AaveSafePath $aa $item.RelativePath
        $allowed=$path.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)-or$path -in @((Join-Path $aa ('Start-AA-AAVE-'+$r.Version+'.ps1')),(Join-Path $aa ('Start-AA-AAVE-'+$r.Version+'.cmd')),(Join-Path $aa ('Rollback-AA-AAVE-'+$r.Version+'.ps1')),(Join-Path $aa ('AAVideoExport-Deployment.Common-'+$r.Version+'.ps1')),(Join-Path $aa ('AAVideoExport-install-'+$r.Version+'.json')))
        if(!$allowed){throw 'Receipt attempts to remove a file outside this version owned paths.'}
        if($expected.ContainsKey($path)){throw 'Duplicate owned rollback file.'};$expected[$path]=$item.Sha256
        if((Test-Path -LiteralPath $path)-and(Get-AaveHash $path)-ne$item.Sha256){throw "Installed file changed; rollback preserves it: $path"}
    }
    $temporary=@{}
    if($r.PSObject.Properties['TemporaryFiles']){foreach($item in $r.TemporaryFiles){
        $path=Get-AaveSafePath $aa $item.RelativePath;$destination=Get-AaveSafePath $aa $item.DestinationRelativePath
        if(!$expected.ContainsKey($destination)-or$path -notmatch ('^'+[regex]::Escape($destination)+'\.aave-[a-f0-9]{32}\.tmp$')){throw 'Receipt contains an unowned temporary file.'}
        $temporary[$path]=$true
    }}
    if(Test-Path -LiteralPath $target){foreach($file in Get-ChildItem -LiteralPath $target -Recurse -File -Force){$null=Get-AaveSafePath $aa ([IO.Path]::GetRelativePath($aa,$file.FullName));if(!$expected.ContainsKey($file.FullName)-and!$temporary.ContainsKey($file.FullName)){throw 'The version directory contains new untracked files; rollback will not delete them.'}}}
    Set-AaveAtomicBytes $profile ([IO.File]::ReadAllBytes($originalProfile))
    if($r.BootstrapExisted){Set-AaveAtomicBytes $bootstrap ([IO.File]::ReadAllBytes((Join-Path $backupRoot 'bootstrap.original.dll')))}
    elseif(Test-Path -LiteralPath $bootstrap){Remove-Item -LiteralPath $bootstrap -Force}
    foreach($path in $temporary.Keys){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    # Keep installed recovery scripts until runtime removal succeeds. An
    # independent verified recovery copy also remains beside this journal.
    $recoveryEntries=@((Join-Path $aa ('Rollback-AA-AAVE-'+$r.Version+'.ps1')),(Join-Path $aa ('AAVideoExport-Deployment.Common-'+$r.Version+'.ps1')))
    foreach($path in @($expected.Keys)|Where-Object {$_ -notin $recoveryEntries}|Sort-Object Length -Descending){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    if(Test-Path -LiteralPath $target){
        # The resolved exact version target and every file were checked above;
        # only now remove its remaining empty directory tree.
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    if((Get-AaveHash $profile)-ne$r.ProfileBeforeSha256){throw 'Rollback profile readback failed.'}
    foreach($path in $recoveryEntries){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    $r.State='RolledBack';$r.RolledBackUtc=[DateTimeOffset]::UtcNow.ToString('o');Write-AaveJson $receiptFile $r
    return [pscustomobject]@{State='RolledBack';Version=$r.Version;Receipt=$receiptFile;OriginalProfileRestored=$true;OriginalBootstrapRestored=[bool]$r.BootstrapExisted}
}
function Invoke-AaveInstallPlan($Plan) {
    if(Get-Process -Name AzureArchive -ErrorAction SilentlyContinue){throw 'Close AA before installation; no files were changed.'}
    $p=$Plan;$package=$p.Package;$version=$p.Version
    Assert-AaveActiveProfile $p.AA $p.ActiveProfile
    if((Get-AaveHash $p.Profile)-ne$p.ProfileSha256){throw 'Profile changed after install planning.'}
    if(Test-Path -LiteralPath $p.Target){throw 'Version target appeared after planning; nothing overwritten.'}
    $backup=Get-AaveSafePath $p.AA ('AAVideoExport-install-backups/'+$version+'-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $backup 'profile.original.json'),$p.ProfileBytes)
    if((Get-AaveHash (Join-Path $backup 'profile.original.json'))-ne$p.ProfileSha256){throw 'Original profile backup readback failed before installation.'}
    if($p.BootstrapBefore){Copy-Item -LiteralPath $p.Bootstrap -Destination (Join-Path $backup 'bootstrap.original.dll');if((Get-AaveHash (Join-Path $backup 'bootstrap.original.dll'))-ne$p.BootstrapBefore){throw 'Original bootstrap backup readback failed before installation.'}}
    foreach($name in @('Rollback-Mod.ps1','Deployment.Common.ps1')){
        Copy-Item -LiteralPath (Join-Path $package.Root $name) -Destination (Join-Path $backup $name)
        if((Get-AaveHash (Join-Path $backup $name))-ne(Get-AaveHash (Join-Path $package.Root $name))){throw 'Recovery tool backup readback failed before installation.'}
    }
    $receiptPath=Join-Path $backup 'install-receipt.json'
    $newFiles=[Collections.Generic.List[object]]::new()
    $temporaryFiles=[Collections.Generic.List[object]]::new()
    $bootstrapInstalledHash=if($p.AuditMode){$package.Descriptor.BootstrapSha256.ToLowerInvariant()}else{$null}
    $r=[ordered]@{Schema='aave.install-receipt.v1';Version=$version;AuditMode=$p.AuditMode;AAInstallPath=$p.AA;ActiveProfile=$p.ActiveProfile;OriginalSelectedVersion=$p.OldVersion;State='Installing';InstalledUtc=$null;RolledBackUtc=$null;ProfileBeforeSha256=$p.ProfileSha256;ProfileInstalledSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($p.NewProfileBytes)).ToLowerInvariant();BootstrapExisted=[bool]$p.BootstrapBefore;BootstrapBeforeSha256=$p.BootstrapBefore;BootstrapInstalledSha256=$bootstrapInstalledHash;BootstrapAction=$(if($p.AuditMode){'InstallDevelopmentAudit'}elseif($p.BootstrapBefore){'RemoveReviewedLegacyAudit'}else{'Absent'});RecoveryCommonSha256=Get-AaveHash (Join-Path $backup 'Deployment.Common.ps1');NewFiles=@();TemporaryFiles=@()}
    function Save-Receipt { $r.NewFiles=@($newFiles.ToArray());$r.TemporaryFiles=@($temporaryFiles.ToArray());Write-AaveJson $receiptPath $r }
    function Add-NewFile([string]$source,[string]$target) {
        $hash=Get-AaveHash $source
        $relative=[IO.Path]::GetRelativePath($p.AA,$target).Replace('\','/')
        $target=Get-AaveSafePath $p.AA $relative
        if(Test-Path -LiteralPath $target){throw 'An owned new file appeared during installation.'}
        $temporary=$target+'.aave-'+[guid]::NewGuid().ToString('N')+'.tmp'
        $null=$newFiles.Add([ordered]@{RelativePath=$relative;Sha256=$hash})
        $null=$temporaryFiles.Add([ordered]@{RelativePath=[IO.Path]::GetRelativePath($p.AA,$temporary).Replace('\','/');DestinationRelativePath=$relative});Save-Receipt
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
        Copy-Item -LiteralPath $source -Destination $temporary
        if((Get-AaveHash $temporary)-ne$hash){throw 'Staged new file differs from its package.'}
        [IO.File]::Move($temporary,$target,$false)
        if((Get-AaveHash $target)-ne$hash){throw 'Installed file readback differs from its package.'}
    }
    Save-Receipt
    try {
        foreach($source in Get-ChildItem -LiteralPath $package.Payload -File -Recurse){Add-NewFile $source.FullName (Get-AaveSafePath $p.Target ([IO.Path]::GetRelativePath($package.Payload,$source.FullName)))}
        Add-NewFile (Join-Path $package.Root 'Start-AA-AAVE.ps1') $p.Launcher
        $commandLauncher=Join-Path $backup 'launcher.cmd'
        $auditArgument=if($p.AuditMode-and$p.LauncherSupportsDevelopmentAudit){' -DevelopmentAudit'}else{''}
        $commandText="@echo off`r`nsetlocal DisableDelayedExpansion`r`n`""+$p.PowerShellPath+"`" -NoLogo -NoProfile -File `"%~dp0Start-AA-AAVE-"+$version+".ps1`""+$auditArgument+"`r`nif errorlevel 1 pause`r`n"
        [IO.File]::WriteAllText($commandLauncher,$commandText,[Text.UTF8Encoding]::new($false));Add-NewFile $commandLauncher $p.LauncherCmd
        Add-NewFile (Join-Path $package.Root 'Rollback-Mod.ps1') $p.Rollback
        Add-NewFile (Join-Path $package.Root 'Deployment.Common.ps1') $p.Common
        if($p.BootstrapBefore){if((Get-AaveHash $p.Bootstrap)-ne$p.BootstrapBefore){throw 'Owned bootstrap changed after planning.'}}
        elseif(Test-Path -LiteralPath $p.Bootstrap){throw 'An owned bootstrap appeared after planning.'}
        if($p.AuditMode){
            Set-AaveAtomicBytes $p.Bootstrap ([IO.File]::ReadAllBytes($package.Bootstrap))
            if((Get-AaveHash $p.Bootstrap)-ne$r.BootstrapInstalledSha256){throw 'Owned bootstrap readback failed.'}
        }elseif($p.BootstrapBefore){
            Remove-Item -LiteralPath $p.Bootstrap -Force
            if(Test-Path -LiteralPath $p.Bootstrap){throw 'Reviewed legacy audit bootstrap removal failed.'}
        }
        $installed=[ordered]@{Schema='aave.installed.v1';Version=$version;AuditMode=$p.AuditMode;AAInstallPath=$p.AA;ActiveProfile=$p.ActiveProfile;OriginalSelectedVersion=$p.OldVersion;BootstrapSha256=$bootstrapInstalledHash;PluginSha256=$package.Descriptor.PluginSha256;CoreSha256=$package.Descriptor.CoreSha256;InstallReceiptPath=$receiptPath;PowerShellPath=$p.PowerShellPath;PowerShellSha256=Get-AaveHash $p.PowerShellPath;CommandLauncher=$p.LauncherCmd;RecoveryRollback=Join-Path $backup 'Rollback-Mod.ps1'}
        $stagedDescriptor=Join-Path $backup 'descriptor.installed.json';Write-AaveJson $stagedDescriptor $installed
        Add-NewFile $stagedDescriptor $p.InstalledDescriptor
        if((Get-AaveHash $p.Profile)-ne$p.ProfileSha256){throw 'Profile changed during installation; selection was not replaced.'}
        Assert-AaveActiveProfile $p.AA $p.ActiveProfile
        Set-AaveAtomicBytes $p.Profile $p.NewProfileBytes
        if((Get-AaveHash $p.Profile)-ne$r.ProfileInstalledSha256){throw 'Installed profile selection readback failed.'}
        $r.State='Installed';$r.InstalledUtc=[DateTimeOffset]::UtcNow.ToString('o');Save-Receipt
        return [pscustomobject]@{State='Installed';Version=$version;AuditMode=$p.AuditMode;Target=$p.Target;ActiveProfile=$p.ActiveProfile;OriginalSelectedVersion=$p.OldVersion;Launcher=$p.Launcher;LauncherCmd=$p.LauncherCmd;Rollback=$p.Rollback;RecoveryRollback=Join-Path $backup 'Rollback-Mod.ps1';Receipt=$receiptPath;Bootstrap=$p.Bootstrap;BootstrapAction=$r.BootstrapAction}
    }
    catch {
        $original=$_.Exception
        try {$null=Undo-AaveInstallReceipt $receiptPath}
        catch {throw [AggregateException]::new('Installation failed and automatic rollback needs review; all recovery evidence is retained.',@($original,$_.Exception))}
        throw $original
    }
}
