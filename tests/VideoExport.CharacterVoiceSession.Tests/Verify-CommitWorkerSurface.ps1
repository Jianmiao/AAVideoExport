param([string]$AAInstallPath = 'F:/AzureArchive_101')
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'bin/Release/net6.0/AAVideoExport.CharacterVoiceSession.NativeCompile.dll'
Add-Type -Path (Join-Path $AAInstallPath 'BepInEx/core/Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
try {
    function AllTypes($items) { foreach ($item in $items) { $item; AllTypes $item.NestedTypes } }
    $types = @{}
    foreach ($type in @(AllTypes $assembly.MainModule.Types)) { $types[$type.FullName] = $type }
    $adapter = $types['AAVideoExport.Plugin.CharacterVoiceSessionAdapter']
    $pending = [Collections.Generic.Queue[object]]::new()
    foreach ($method in $adapter.Methods | Where-Object Name -In @('ValidateForCommit','AcquireCommitScope')) { $pending.Enqueue($method) }
    $seen = [Collections.Generic.HashSet[string]]::new()
    $violations = [Collections.Generic.List[string]]::new()
    while ($pending.Count) {
        $method = $pending.Dequeue()
        if (!$seen.Add($method.FullName) -or !$method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.Operand -is [Mono.Cecil.FieldReference]) {
                $field = $instruction.Operand
                if ($field.DeclaringType.FullName -eq $adapter.FullName -and $field.Name -in @('runtime','gate','player','audio','voice','resources','position','pins')) {
                    $violations.Add($method.FullName + ' reads live native field ' + $field.Name)
                }
            }
            if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) { continue }
            $reference = $instruction.Operand
            if ($reference -is [Mono.Cecil.MethodSpecification]) { $reference = $reference.ElementMethod }
            $typeName = $reference.DeclaringType.GetElementType().FullName
            if ($typeName -match '^(UnityEngine\.|Il2Cpp|FlatData\.)' -or
                $typeName -in @('Test','AudioManager','ScenarioResourceManager','UserSettings','CharacterManager','GenericScenarioExcel','GenericScenarioExcelTable') -or
                ($typeName -eq $adapter.FullName -and $reference.Name -in @('RequireThread','RequireRuntime','RequireUsable','ValidateController','ValidateFrame')) -or
                $typeName -eq 'AAVideoExport.Plugin.CharacterVoiceDelayClock' -or
                ($typeName -match '^System.Reflection\.(MethodBase|MethodInfo|PropertyInfo|FieldInfo)$' -and $reference.Name -in @('Invoke','GetValue','SetValue'))) {
                $violations.Add($method.FullName + ' reaches forbidden worker call ' + $reference.FullName)
            }
            if ($types.ContainsKey($typeName)) {
                $resolved = $reference.Resolve()
                if ($null -ne $resolved) { $pending.Enqueue($resolved) }
            }
        }
    }
    if ($violations.Count) { $violations | ForEach-Object { Write-Output $_ }; throw 'Voice publication entry points reach live native/main-thread operations.' }
    [pscustomobject]@{
        Passed=$true; LocalReachableMethods=$seen.Count;
        EntryPoints=@('ValidateForCommit','AcquireCommitScope');
        Scope='Explicit local IL calls, delegate targets and field reads contain no Unity/live runtime or main-thread validation access. Managed external Harmony/BCL internals are outside this static check.'
    } | ConvertTo-Json -Depth 4
}
finally { $assembly.Dispose() }
