using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using Mono.Cecil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;

namespace AAVideoExport.Plugin;

// Generic managed-clock coverage. A Mod is admitted as a clock participant only
// when every discovered managed clock call site is covered by either the
// call-point bridge (Stopwatch/DateTime/Environment) or the fixed Time.* hooks.
internal static class GenericClockPolicy
{
    internal const string Marker = "GenericClockBridge.managed-clock.v1";
    internal const string GlobalBridge = "GenericClockHooks.global-clock.v1";
    private static readonly string[] GlobalClockSignatures =
    {
        "System.Single UnityEngine.Time::get_time()",
        "System.Single UnityEngine.Time::get_realtimeSinceStartup()",
        "System.Single UnityEngine.Time::get_unscaledTime()",
        "System.Single UnityEngine.Time::get_unscaledDeltaTime()",
        "System.Single UnityEngine.Time::get_deltaTime()"
    };

    private static readonly string[] BridgedClockSignatures =
    {
        "System.Int64 System.Diagnostics.Stopwatch::GetTimestamp()",
        "System.DateTime System.DateTime::get_Now()",
        "System.DateTime System.DateTime::get_UtcNow()",
        "System.Int32 System.Environment::get_TickCount()",
        "System.Int64 System.Environment::get_TickCount64()"
    };

    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Rejected;
        evidence = Array.Empty<string>();
        string location;
        try { location = assembly.Location ?? ""; }
        catch (NotSupportedException) { return false; }
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location)) return false;

        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        int bridged = 0, global = 0;
        foreach (var type in module.Types.SelectMany(Flatten))
        foreach (var method in type.Methods)
        {
            if (!method.HasBody || method.IsAbstract) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call || instruction.Operand is not MethodReference reference) continue;
                if (BridgedClockSignatures.Any(value => string.Equals(value, reference.FullName, StringComparison.Ordinal))) bridged++;
                else if (GlobalClockSignatures.Any(value => string.Equals(value, reference.FullName, StringComparison.Ordinal))) global++;
            }
        }
        if (bridged == 0 && global == 0) return false;

        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))).ToLowerInvariant();
        int patched = GenericStopwatchBridge.PatchedCallCount(assembly);
        if (patched != bridged)
        {
            evidence = new[] { Marker, "call-sites=" + (bridged + global), "patched=" + patched,
                "unbridged-clock-call-sites=" + (bridged - patched), "sha256=" + sha };
            return true;
        }
        level = StrictCapabilityLevel.Observed;
        evidence = new[] { Marker, GlobalBridge, "call-sites=" + (bridged + global), "patched=" + patched,
            "unbridged-clock-call-sites=0", "global-clock-call-sites=" + global, "sha256=" + sha, "cache-reuse-disabled" };
        return true;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
    }
}
