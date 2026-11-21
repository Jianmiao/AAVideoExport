using Mono.Cecil;

namespace RuntimeAttributionProbe;

internal static class HostSeamInspector
{
    public static object Inspect(string hostRoot)
    {
        var core = Path.Combine(hostRoot, "BepInEx", "core");
        var files = new[] { "BepInEx.Preloader.Core.dll", "BepInEx.Unity.IL2CPP.dll", "BepInEx.Core.dll" };
        return files.Select(file =>
        {
            using var module = ModuleDefinition.ReadModule(Path.Combine(core, file));
            var methods = module.Types.SelectMany(t => t.Methods)
                .Where(m => m.HasBody && ((m.DeclaringType.Name == "AssemblyPatcher" &&
                    new[] { "AddPatchersFromDirectory", "PatchAndLoad" }.Contains(m.Name)) ||
                    (m.DeclaringType.Name == "Preloader" && m.Name == "Run") ||
                    (m.DeclaringType.Name.Contains("Chainloader") &&
                    new[] { "Initialize", "Execute", "LoadPlugin", "LoadPlugins" }.Contains(m.Name))))
                .Select(m => new
                {
                    method = m.FullName,
                    calls = m.Body.Instructions.Where(i => i.Operand is MethodReference)
                        .Select(i => new { offset = $"IL_{i.Offset:X4}", opcode = i.OpCode.Name,
                            target = ((MethodReference)i.Operand).FullName }).ToArray()
                }).ToArray();
            return new { file, module.Assembly.Name.FullName, mvid = module.Mvid, methods };
        }).ToArray();
    }
}
