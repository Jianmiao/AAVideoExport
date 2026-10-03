param([Parameter(Mandatory=$true)][string]$AAInstallPath, [Parameter(Mandatory=$true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $AAInstallPath 'BepInEx\core\Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($AssemblyPath))
try {
    $type = $assembly.MainModule.GetType('AAVideoExport.Integration.RenderControlV1')
    if (!$type -or !$type.IsPublic -or !$type.IsAbstract -or !$type.IsSealed) { throw 'Public static RenderControlV1 missing.' }
    $properties = @{ ProtocolVersion = 'System.Int32'; IsReady = 'System.Boolean'; IsRenderingOwned = 'System.Boolean' }
    foreach ($name in $properties.Keys) {
        $property = $type.Properties | Where-Object Name -EQ $name
        if (!$property -or $property.PropertyType.FullName -ne $properties[$name] -or
            !$property.GetMethod.IsPublic -or !$property.GetMethod.IsStatic -or $property.GetMethod.Parameters.Count -ne 0) {
            throw "RenderControlV1 property contract changed: $name"
        }
    }
    foreach ($name in @('BeforeAcquire', 'AfterRelease')) {
        $event = $type.Events | Where-Object Name -EQ $name
        if (!$event -or $event.EventType.FullName -ne 'System.Action' -or
            !$event.AddMethod.IsPublic -or !$event.AddMethod.IsStatic -or
            !$event.RemoveMethod.IsPublic -or !$event.RemoveMethod.IsStatic) {
            throw "RenderControlV1 event contract changed: $name"
        }
    }
    Write-Output 'PASS RenderControlV1 public ABI; lifecycle behavior is checked by production-source tests.'
}
finally { $assembly.Dispose() }
