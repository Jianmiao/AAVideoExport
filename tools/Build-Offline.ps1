param(
    [Parameter(Mandatory=$true)][string]$CompilerDirectory,
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$AAInstallPath,
    [string]$OutputDirectory,
    [string]$GraphicsDependencyDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop
$RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$AAInstallPath = (Resolve-Path -LiteralPath $AAInstallPath).Path
if (!(Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'System.Private.CoreLib.dll')) -or
    !(Test-Path -LiteralPath (Join-Path $AAInstallPath 'BepInEx\interop') -PathType Container)) {
    throw 'Provide a .NET 6 runtime directory and an AA installation with generated BepInEx interop references.'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\offline-build' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $CompilerDirectory 'csc.dll'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Pass the Roslyn netcore/bincore directory containing csc.dll.' }
$refs = @(Get-ChildItem -LiteralPath $RuntimeDirectory -Filter 'System*.dll' |
    Where-Object { $_.Name -notlike '*.Native.dll' } | ForEach-Object { '/reference:' + $_.FullName })
$refs += '/reference:' + (Join-Path $RuntimeDirectory 'netstandard.dll')
$refs += @(Get-ChildItem -LiteralPath $RuntimeDirectory -Filter 'Microsoft*.dll' |
    Where-Object { $_.Name -notlike '*Native*' } | ForEach-Object { '/reference:' + $_.FullName })
$globals = Join-Path $OutputDirectory 'GlobalUsings.cs'
[IO.File]::WriteAllText($globals, @'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
'@)
$friends = Join-Path $OutputDirectory 'CoreFriends.cs'
[IO.File]::WriteAllText($friends, '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VideoExport.Core.Tests")]')
function Compile-Project([string]$Project, [string]$Name, [string[]]$ExtraRefs, [string[]]$ExtraSources = @()) {
    $projectPath = Join-Path $repo $Project
    $projectDirectory = Split-Path -Parent $projectPath
    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $sources = @(Get-ChildItem -LiteralPath $projectDirectory -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { $_.FullName })
    foreach ($entry in $projectXml.SelectNodes('//Compile[@Include]')) {
        $sources += [IO.Path]::GetFullPath((Join-Path $projectDirectory $entry.Include))
    }
    $executable = $null -ne $projectXml.SelectSingleNode('//OutputType[text()="Exe"]')
    $dll = Join-Path $OutputDirectory ($Name + '.dll')
    $metadata = Join-Path $OutputDirectory ($Name + '.AssemblyInfo.cs')
    $assemblyVersion = if ($Name -in @('AAVideoExport','AAVideoExport.Core')) { '0.2.3.0' } else { '1.0.0.0' }
    [IO.File]::WriteAllText($metadata,
        '[assembly: System.Reflection.AssemblyVersion("' + $assemblyVersion + '")]' +
        '[assembly: System.Reflection.AssemblyFileVersion("' + $assemblyVersion + '")]')
    $arguments = @('/nologo','/noconfig','/nostdlib+','/unsafe+','/optimize+','/langversion:latest','/nullable:enable',
        '/define:NET6_0;NET6_0_OR_GREATER',('/target:' + $(if ($executable) { 'exe' } else { 'library' })),('/out:' + $dll))
    $arguments += $refs + $ExtraRefs + @($globals,$metadata) + $sources + $ExtraSources
    foreach ($entry in $projectXml.SelectNodes('//EmbeddedResource[@Include]')) {
        $resourcePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $entry.Include))
        $arguments += '/resource:' + $resourcePath + ',' + $entry.LogicalName
    }
    $result = & $dotnetCommand.Source $compiler @arguments 2>&1
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Name + '.compile.log')), ($result | Out-String), [Text.UTF8Encoding]::new($false))
    if ($exitCode -ne 0) { $result | ForEach-Object { Write-Host $_ }; throw "Compilation failed: $Name" }
    if ($executable) {
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($Name + '.runtimeconfig.json')),
            '{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"},"rollForward":"LatestPatch"}}')
    }
    Write-Host "Built $Name"
}
if (!$GraphicsDependencyDirectory) { $GraphicsDependencyDirectory = Join-Path $repo '0.2.3' }
$graphicsNames=@('Vortice.Direct3D11.dll','Vortice.DXGI.dll','Vortice.DirectX.dll','Vortice.D3DCompiler.dll',
    'Vortice.Mathematics.dll','SharpGen.Runtime.dll','SharpGen.Runtime.COM.dll')
$graphicsRefs=@(foreach($name in $graphicsNames) {
    $path=Join-Path $GraphicsDependencyDirectory $name
    if (!(Test-Path -LiteralPath $path)) { throw "Missing $name; provide GraphicsDependencyDirectory with the pinned .NET 6 dependencies." }
    Copy-Item -LiteralPath $path -Destination (Join-Path $OutputDirectory $name)
    '/reference:'+$path
})
Compile-Project 'src\VideoExport.Core\VideoExport.Core.csproj' 'AAVideoExport.Core' $graphicsRefs @($friends)
$coreReference = '/reference:' + (Join-Path $OutputDirectory 'AAVideoExport.Core.dll')
Compile-Project 'tests\VideoExport.Core.Tests\VideoExport.Core.Tests.csproj' 'VideoExport.Core.Tests' @($coreReference)
# The failure fixtures launch the harness as an executable. An SDK build emits
# an apphost; this offline build uses the installed Framework compiler instead.
$frameworkCompiler = if ($env:WINDIR) { Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' } else { $null }
if ($frameworkCompiler -and (Test-Path -LiteralPath $frameworkCompiler)) {
    & $frameworkCompiler /nologo /optimize+ /target:exe ('/out:'+(Join-Path $OutputDirectory 'VideoExport.Core.Tests.exe')) (Join-Path $repo 'tools\OfflineTestHost.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Offline test fixture host compilation failed.' }
}
foreach ($testName in @('FramePipeline','Host','Audio','Clock','OutputDimensions','Catalog','QueuedReadback')) {
    $testReferences = if ($testName -eq 'Audio') { @($coreReference) } else { @() }
    Compile-Project "tests\VideoExport.$testName.Tests\VideoExport.$testName.Tests.csproj" "VideoExport.$testName.Tests" $testReferences
}
Compile-Project 'tests\VideoExport.Selection.Tests\VideoExport.Selection.Tests.csproj' 'VideoExport.Selection.Tests' @('/reference:' + (Join-Path $AAInstallPath 'BepInEx\core\0Harmony.dll'))
# Selection regression tests exercise actual Harmony patches on managed native
# boundary fixtures; copy the runtime dependencies only into ignored artifacts.
foreach ($name in @('0Harmony.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll','Mono.Cecil.dll','Mono.Cecil.Pdb.dll','Mono.Cecil.Mdb.dll','Mono.Cecil.Rocks.dll','MonoMod.Backports.dll','MonoMod.ILHelpers.dll')) {
    $path = Join-Path $AAInstallPath ('BepInEx\core\' + $name)
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $OutputDirectory $name) }
}
$pluginRefs = @($coreReference)
foreach ($name in @('BepInEx.Core.dll','BepInEx.Unity.Common.dll','BepInEx.Unity.IL2CPP.dll','Il2CppInterop.Runtime.dll','0Harmony.dll')) {
    $pluginRefs += '/reference:' + (Join-Path $AAInstallPath ('BepInEx\core\' + $name))
}
$pluginRefs += @(Get-ChildItem -LiteralPath (Join-Path $AAInstallPath 'BepInEx\interop') -Filter '*.dll' |
    ForEach-Object { '/reference:' + $_.FullName })
Compile-Project 'src\VideoExport.Plugin\VideoExport.Plugin.csproj' 'AAVideoExport' $pluginRefs
Write-Output $OutputDirectory
