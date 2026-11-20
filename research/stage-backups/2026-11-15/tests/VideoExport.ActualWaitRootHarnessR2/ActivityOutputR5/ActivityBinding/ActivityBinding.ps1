#requires -Version 7.2
# Pure binding/document helpers. Loading this file performs no source or host I/O.
$script:AORActivitySavedRoot='E:\aamod\AAVE-transparent-runtime-work-20261031\tests\VideoExport.ActualWaitActivityDriverR1'
$script:AORActivityFixtureCopyRoot='E:\aamod\AAVE-transparent-runtime-work-20261031\tests\VideoExport.ActualWaitRootHarnessR2\ActivityOutputR5\Evidence'
$script:AORActivityDriverHash='2D958E54A8D9C06F5629431CB2849FBC9C03F691D869B45D4364FBA3C329DAF4'
$script:AORActivityObserverHash='44E7607911CA0482EA8431743EB603BDC5E3D133E71CEA475DD0D8B07108FC19'
$script:AORActivityHandoffHash='238295E88CBDCBEE8E394B1B6D9DB138B760747137FB516860CF2B57E1379AA8'
$script:AORActivityManifestHash='0FCE11C61FBE7ED368D153D8085F23FEB2E7ACD245412976831A7935F2FA0876'

function Assert-AORActivityBinding([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Test-AORActivityInteger($Value){($Value -is [int] -or $Value -is [long] -or $Value -is [uint] -or $Value -is [ulong]) -and [decimal]$Value -le [long]::MaxValue}
function ConvertTo-AORActivityAbsolute([string]$Path){
 Assert-AORActivityBinding (![string]::IsNullOrWhiteSpace($Path) -and [IO.Path]::IsPathFullyQualified($Path)) 'Canonical absolute path required.'
 Assert-AORActivityBinding ($Path -notmatch '[<>"|*?\x00-\x1F]' -and $Path.Substring(2) -notmatch ':' -and $Path -notmatch '(^|[\\/])\.{1,2}([\\/]|$)' -and $Path -notmatch '[. ]([\\/]|$)') 'Alias, traversal, alternate-stream or wildcard path refused.'
 $full=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
 Assert-AORActivityBinding ($full.Length -gt [IO.Path]::GetPathRoot($full).Length -and $full -ceq $Path.Replace('/','\').TrimEnd('\')) 'Path is not canonical or is a filesystem root.'
 $full
}
function Assert-AORActivityNoReparse([string]$Path){
 $full=ConvertTo-AORActivityAbsolute $Path
 for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
  if(Test-Path -LiteralPath $cursor){$item=Get-Item -LiteralPath $cursor -Force -ErrorAction Stop;Assert-AORActivityBinding (!$item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) ('Reparse source refused: '+$cursor)}
 }
}
function ConvertTo-AORActivitySourceRoot([string]$SourceRoot,[bool]$ManagedFixture){
 $root=ConvertTo-AORActivityAbsolute $SourceRoot
 $fixed=[string]::Equals($root,$script:AORActivitySavedRoot,[StringComparison]::OrdinalIgnoreCase)
 $copy=$ManagedFixture -and $root.StartsWith($script:AORActivityFixtureCopyRoot+'\',[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($root) -ceq 'input-copy'
 Assert-AORActivityBinding ($fixed -or $copy) 'SourceRoot must be the fixed saved driver root, or an allowed managed-fixture input-copy.'
 $root
}
function ConvertTo-AORActivityRelative([string]$Relative){
 Assert-AORActivityBinding (![string]::IsNullOrWhiteSpace($Relative) -and ![IO.Path]::IsPathRooted($Relative) -and $Relative -notmatch '[\\:<>"|*?\x00-\x1F]') 'Canonical forward-slash fixture relative path required.'
 foreach($part in $Relative.Split('/')){Assert-AORActivityBinding ($part.Length -gt 0 -and $part -cnotin @('.','..') -and $part -notmatch '[. ]$') 'Unsafe or noncanonical fixture relative component.'}
 $Relative
}
function Read-AORActivityFilePin([string]$Path,[string]$SHA256,$Bytes,$LastWriteUtcTicks=$null){
 $full=ConvertTo-AORActivityAbsolute $Path
 $fixedRead=$full.StartsWith($script:AORActivitySavedRoot+'\',[StringComparison]::OrdinalIgnoreCase)
 $copyRead=$false
 if($full.StartsWith($script:AORActivityFixtureCopyRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
  $parts=$full.Substring($script:AORActivityFixtureCopyRoot.Length+1).Split('\')
  $copyIndex=[Array]::IndexOf($parts,'input-copy');$copyRead=$copyIndex -ge 0 -and $copyIndex -lt $parts.Length-1
 }
 Assert-AORActivityBinding ($fixedRead -or $copyRead) 'File pin reads are limited to saved activity source or managed input-copy trees.'
 Assert-AORActivityBinding ($SHA256 -cmatch '^[A-F0-9]{64}$' -and (Test-AORActivityInteger $Bytes) -and $Bytes -ge 0) 'Exact nonnegative length and SHA256 required; empty fixtures are valid.'
 if($null -ne $LastWriteUtcTicks){Assert-AORActivityBinding ((Test-AORActivityInteger $LastWriteUtcTicks) -and $LastWriteUtcTicks -gt 0 -and $LastWriteUtcTicks -le [datetime]::MaxValue.Ticks) 'Exact UTC ticks required.'}
 Assert-AORActivityNoReparse $full
 $before=Get-Item -LiteralPath $full -Force -ErrorAction Stop
 Assert-AORActivityBinding (!$before.PSIsContainer -and $before.Length -eq $Bytes) ('Pinned source length/type differs: '+$full)
 $initialTicks=$before.LastWriteTimeUtc.Ticks
 if($null -ne $LastWriteUtcTicks){Assert-AORActivityBinding ($initialTicks -eq $LastWriteUtcTicks) ('Pinned source UTC ticks differ: '+$full)}
 $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256 -ErrorAction Stop).Hash
 Assert-AORActivityNoReparse $full
 $after=Get-Item -LiteralPath $full -Force -ErrorAction Stop
 Assert-AORActivityBinding (!$after.PSIsContainer -and $after.Length -eq $Bytes -and $after.LastWriteTimeUtc.Ticks -eq $initialTicks -and $hash -ceq $SHA256) ('Pinned source changed or hash differs: '+$full)
 [pscustomobject]@{Path=$full;SHA256=$hash;Bytes=[long]$Bytes;LastWriteUtcTicks=[long]$initialTicks}
}
function Assert-AORActivityManifest($Manifest){
 Assert-AORActivityBinding ($Manifest.Schema -ceq 'aave-saved-owned-activity-fixture.v1' -and (Test-AORActivityInteger $Manifest.ExpectedRows) -and $Manifest.ExpectedRows -eq 8 -and $Manifest.StoryRelativePath -ceq 'data/saves/AAVE-OfficialAuthoring-20261030.aas' -and $Manifest.RecipeRelativePath -ceq 'controlled-story-recipe.json' -and @($Manifest.Files).Count -eq 23) 'Complete fixed activity manifest required.'
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($file in $Manifest.Files){
  $relative=ConvertTo-AORActivityRelative $file.RelativePath
  Assert-AORActivityBinding ($seen.Add($relative) -and $file.SHA256 -cmatch '^[A-F0-9]{64}$' -and (Test-AORActivityInteger $file.Bytes) -and $file.Bytes -ge 0 -and (Test-AORActivityInteger $file.LastWriteUtcTicks) -and $file.LastWriteUtcTicks -gt 0 -and $file.LastWriteUtcTicks -le [datetime]::MaxValue.Ticks) 'Duplicate or malformed fixture record.'
 }
}
function Get-AORPinnedActivitySources([string]$SourceRoot,[bool]$ManagedFixture){
 $root=ConvertTo-AORActivitySourceRoot $SourceRoot $ManagedFixture
 Assert-AORActivityNoReparse $root
 Assert-AORActivityBinding (Test-Path -LiteralPath $root -PathType Container) 'Saved source root is absent.'
 $handoff=Read-AORActivityFilePin (Join-Path $root 'HANDOFF.json') $script:AORActivityHandoffHash 141425
 $manifestPin=Read-AORActivityFilePin (Join-Path $root 'Fixture.SourceManifest.json') $script:AORActivityManifestHash 5936
 $driver=Read-AORActivityFilePin (Join-Path $root 'Package\Driver\AAVE.ActualWait.ActivityDriver.R1.dll') $script:AORActivityDriverHash 115712
 # Paths inside the handoff/deployment/recipe are provenance labels only.
 # In particular, never follow DEPLOYMENT-CONTENTS.Source to movie-fixture.
 Assert-AORActivityNoReparse $manifestPin.Path
 $manifestBytes=[IO.File]::ReadAllBytes($manifestPin.Path)
 Assert-AORActivityBinding ($manifestBytes.LongLength -eq 5936 -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)) -ceq $script:AORActivityManifestHash) 'Actual parsed manifest bytes are not the pinned document.'
 $manifest=[Text.Encoding]::UTF8.GetString($manifestBytes)|ConvertFrom-Json -Depth 30
 Assert-AORActivityManifest $manifest
 $packageRoot=Join-Path $root 'Package\Fixture'
 Assert-AORActivityNoReparse $packageRoot
 Assert-AORActivityBinding (Test-Path -LiteralPath $packageRoot -PathType Container) 'Saved Package/Fixture is absent.'
 $expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $expectedDirs=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($file in $manifest.Files){
  [void]$expected.Add([string]$file.RelativePath)
  $relative=[string]$file.RelativePath
  while($relative.Contains('/')){$relative=$relative.Substring(0,$relative.LastIndexOf('/'));[void]$expectedDirs.Add($relative)}
 }
 $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($packageRoot);$count=0
 while($queue.Count){
  $directory=$queue.Dequeue();Assert-AORActivityNoReparse $directory
  foreach($item in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop){
   Assert-AORActivityBinding (++$count -le 256) 'Saved fixture inventory exceeded its bound.'
   Assert-AORActivityNoReparse $item.FullName
   $relative=[IO.Path]::GetRelativePath($packageRoot,$item.FullName).Replace('\','/')
   if($item.PSIsContainer){Assert-AORActivityBinding ($expectedDirs.Contains($relative)) ('Unexpected saved fixture directory: '+$relative);$queue.Enqueue($item.FullName)}
   else{Assert-AORActivityBinding ($expected.Contains($relative) -and $actual.Add($relative)) ('Unexpected/duplicate saved fixture file: '+$relative)}
  }
 }
 Assert-AORActivityBinding ($actual.SetEquals($expected)) 'Saved fixture file set is incomplete.'
 $files=@(foreach($file in $manifest.Files){
  $pin=Read-AORActivityFilePin (Join-Path $packageRoot ($file.RelativePath.Replace('/','\'))) $file.SHA256 $file.Bytes $file.LastWriteUtcTicks
  [pscustomobject]@{RelativePath=[string]$file.RelativePath;Path=$pin.Path;SHA256=$pin.SHA256;Bytes=$pin.Bytes;LastWriteUtcTicks=$pin.LastWriteUtcTicks}
 })
 # Close the manifest byte binding after parsing and complete source reads.
 $null=Read-AORActivityFilePin $manifestPin.Path $script:AORActivityManifestHash 5936
 [pscustomobject]@{SourceRoot=$root;ManagedFixture=$ManagedFixture;Driver=$driver;Handoff=$handoff;FixtureManifest=$manifestPin;Manifest=$manifest;FixtureFiles=$files}
}
function Get-AORFixtureDestinations($FixtureRoot,$Bundle){
 # Deliberately lexical only: target existence, reparse ancestry and restore
 # coverage are checked by the parent transaction, never by this pure mapper.
 $root=ConvertTo-AORActivityAbsolute $FixtureRoot
 Assert-AORActivityBinding ($Bundle.ManagedFixture -is [bool]) 'Bundle mode is missing.'
 $sourceRoot=ConvertTo-AORActivitySourceRoot $Bundle.SourceRoot $Bundle.ManagedFixture
 Assert-AORActivityManifest $Bundle.Manifest
 Assert-AORActivityBinding (@($Bundle.FixtureFiles).Count -eq 23) 'Complete fixture bundle required.'
 $map=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal);foreach($entry in $Bundle.Manifest.Files){$map.Add([string]$entry.RelativePath,$entry)}
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 $result=@(foreach($file in $Bundle.FixtureFiles){
  $relative=ConvertTo-AORActivityRelative $file.RelativePath
  Assert-AORActivityBinding ($seen.Add($relative) -and $map.ContainsKey($relative)) 'Duplicate or undeclared fixture map entry.'
  $expected=$map[$relative]
  Assert-AORActivityBinding ((Test-AORActivityInteger $file.Bytes) -and (Test-AORActivityInteger $file.LastWriteUtcTicks) -and $file.SHA256 -ceq $expected.SHA256 -and $file.Bytes -eq $expected.Bytes -and $file.LastWriteUtcTicks -eq $expected.LastWriteUtcTicks) 'Fixture map no longer matches the typed source manifest.'
  $source=ConvertTo-AORActivityAbsolute $file.Path
  $expectedSource=Join-Path (Join-Path $sourceRoot 'Package\Fixture') ($relative.Replace('/','\'))
  Assert-AORActivityBinding ($source -ceq $expectedSource) 'Fixture source path differs from its declared relative path.'
  $target=ConvertTo-AORActivityAbsolute (Join-Path $root ($relative.Replace('/','\')))
  Assert-AORActivityBinding ($target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) 'Fixture destination escaped target root.'
  [pscustomobject]@{Path=$target;SourcePath=$source;SHA256=$file.SHA256;Bytes=[long]$file.Bytes;LastWriteUtcTicks=[long]$file.LastWriteUtcTicks;Role='Fixture'}
 })
 $result
}
function ConvertTo-AORActivitySuppliedPin($Pin){
 Assert-AORActivityBinding ($Pin.Path -is [string] -and $Pin.SHA256 -is [string] -and $Pin.SHA256 -cmatch '^[A-F0-9]{64}$' -and (Test-AORActivityInteger $Pin.Bytes) -and $Pin.Bytes -gt 0) 'Actual executable/Mod pin must have an absolute path, SHA256 and positive integer length.'
 [pscustomobject]@{Path=(ConvertTo-AORActivityAbsolute $Pin.Path);SHA256=$Pin.SHA256;Bytes=[long]$Pin.Bytes}
}
function New-AORDriverDocument($Plan,$ExecutablePin,$ExistingMods){
 Assert-AORActivityBinding ($Plan.Mode -is [string] -and $Plan.Mode -cin @('RootLive','ManagedFixture') -and $Plan.RunId -is [string] -and $Plan.RunId -cmatch '^[a-f0-9]{32}$') 'Exact parent mode and run ID required.'
 $managed=$Plan.Mode -ceq 'ManagedFixture'
 $maximum=$Plan.Activity.MaximumSeconds
 Assert-AORActivityBinding (($maximum -is [int] -or $maximum -is [long] -or $maximum -is [double] -or $maximum -is [single] -or $maximum -is [decimal]) -and [double]::IsFinite([double]$maximum) -and $maximum -ge 5 -and $maximum -le 180) 'Activity duration must be a finite number from 5 to 180 seconds.'
 $fixtureRoot=ConvertTo-AORActivityAbsolute $Plan.Activity.FixtureRoot
 $executable=ConvertTo-AORActivitySuppliedPin $ExecutablePin
 if(!$managed){Assert-AORActivityBinding ([IO.Path]::GetFileName($executable.Path) -ieq 'AzureArchive.exe') 'RootLive executable must be the actual AzureArchive.exe baseline pin.'}
 $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 $mods=@(foreach($mod in @($ExistingMods)){
  Assert-AORActivityBinding ($mod.AssemblyName -is [string] -and ![string]::IsNullOrWhiteSpace($mod.AssemblyName) -and $mod.AssemblyName -ceq $mod.AssemblyName.Trim() -and $mod.AssemblyName -notmatch '[\x00-\x1F]' -and $names.Add($mod.AssemblyName)) 'Duplicate or malformed original Mod assembly name.'
  $pin=ConvertTo-AORActivitySuppliedPin $mod
  Assert-AORActivityBinding ($paths.Add($pin.Path)) 'Two original Mod names share one payload path.'
  [pscustomobject]@{AssemblyName=$mod.AssemblyName;Path=$pin.Path;SHA256=$pin.SHA256;Bytes=$pin.Bytes}
 })
 foreach($required in @('AAVideoExport','Rukari.MoreEffects','Rukari.CharacterVoice')){Assert-AORActivityBinding (@($mods|Where-Object AssemblyName -ceq $required).Count -eq 1) ('Exactly one original Mod required: '+$required)}
 # Read only the saved package. Supplied actual host/Mod pins are never opened.
 $bundle=Get-AORPinnedActivitySources $Plan.Activity.SourceRoot $managed
 $document=[ordered]@{Schema=$(if($managed){'aave-activity-output-r5.managed-fixture-plan.v1'}else{'aave-actual-wait-activity.root-plan.v1'});RunId=$Plan.RunId;DriverSHA256=$bundle.Driver.SHA256;ObserverSHA256=$script:AORActivityObserverHash;FixtureRoot=$fixtureRoot;FixtureManifestSHA256=$bundle.FixtureManifest.SHA256;Executable=$executable;ExistingMods=$mods;ExpectedRows=[int]$bundle.Manifest.ExpectedRows;MaximumSeconds=[double]$maximum}
 if($managed){$document.NativeLaunchAuthorized=$false}
 [pscustomobject]$document
}
