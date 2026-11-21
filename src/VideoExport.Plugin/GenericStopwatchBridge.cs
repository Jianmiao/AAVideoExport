using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace AAVideoExport.Plugin;

// Generic, behavior-driven wall-clock bridge. It patches managed call sites of
// Stopwatch.GetTimestamp, not a named Mod and not the BCL method globally.
// AAVE's own assemblies are never targets; the host keeps raw wall time.
internal static class GenericStopwatchBridge
{
    private const string WallTimestampSignature = "System.Int64 System.Diagnostics.Stopwatch::GetTimestamp()";
    private static readonly MethodInfo WallTimestamp = typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!;
    private static readonly MethodInfo AdaptedTimestamp = typeof(GenericStopwatchBridge).GetMethod(nameof(ReadTimestamp), BindingFlags.Static | BindingFlags.Public)!;
    private static readonly MethodInfo RewriteMethod = typeof(GenericStopwatchBridge).GetMethod(nameof(Rewrite), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Dictionary<string, (MethodBase Method, int Calls)> Targets = new();
    private static readonly AnimationTimestampClock Clock = new();
    private static Harmony? _harmony;
    private static Assembly? _exporterAssembly;
    private static Action<string>? _log;
    private static long _captureReads;
    private static string _status = "stopped";

    internal static bool Active => Clock.Active;
    internal static long CaptureReadCount => _captureReads;
    internal static string Status => _status;
    internal static IReadOnlyCollection<MethodBase> PatchedMethods => Targets.Values.Select(value => value.Method).ToArray();

    internal static void Initialize(Harmony harmony, Action<string> log, Assembly? exporterAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(log);
        if (_harmony != null) throw new InvalidOperationException("Generic stopwatch bridge is already initialized.");
        _harmony = harmony;
        _exporterAssembly = exporterAssembly;
        _log = log;
        _status = "ready";
    }

    // Discovers call sites in one managed assembly and patches every caller
    // that actually contains at least one Stopwatch.GetTimestamp call. This is
    // intentionally assembly-agnostic: the caller decides which plugin
    // assemblies are in scope, not the Mod's name.
    internal static int PatchAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (_exporterAssembly != null && assembly == _exporterAssembly)
            throw new ArgumentException("The exporter's own assembly must keep raw wall time.", nameof(assembly));
        string? location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new NotSupportedException("Managed Mod assembly has no readable location.");
        int installed = 0;
        foreach (var target in DiscoverTimestampCallers(assembly))
        {
            if (TryPatch(target.Method, target.Calls)) installed++;
        }
        return installed;
    }

    internal static IReadOnlyList<(MethodInfo Method, int Calls)> DiscoverTimestampCallers(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            throw new NotSupportedException("Managed Mod assembly has no readable location.");
        return DiscoverTimestampCallers(Assembly.LoadFrom(assemblyPath));
    }

    internal static IReadOnlyList<(MethodInfo Method, int Calls)> DiscoverTimestampCallers(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new NotSupportedException("Managed Mod assembly has no readable location.");
        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        var result = new List<(MethodInfo, int)>();
        foreach (var type in module.Types.SelectMany(Flatten))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.IsAbstract) continue;
                int calls = method.Body.Instructions.Count(IsWallTimestampCall);
                if (calls == 0) continue;
                var runtime = assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32()) as MethodInfo
                    ?? throw new NotSupportedException("Clock caller did not resolve to a method: " + method.FullName);
                result.Add((runtime, calls));
            }
        }
        return result;
    }

    internal static bool TryPatch(MethodInfo method, int expectedCalls)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_harmony == null) throw new InvalidOperationException("Generic stopwatch bridge is not initialized.");
        if (expectedCalls <= 0 || method.GetMethodBody() == null)
            throw new NotSupportedException("Clock target has no managed body or clock calls: " + method.Name);
        string key = Key(method);
        if (Targets.ContainsKey(key)) return false;
        if (method.DeclaringType?.Assembly == _exporterAssembly)
            throw new NotSupportedException("The exporter's own assembly must keep raw wall time.");
        // Validate the whole call shape before installing any patch.
        RewriteCalls(PatchProcessor.GetOriginalInstructions(method, (ILGenerator?)null), expectedCalls).ToList();
        Targets.Add(key, (method, expectedCalls));
        try
        {
            _harmony.Patch(method, transpiler: new HarmonyMethod(RewriteMethod));
            if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.PatchMethod == RewriteMethod) != true)
                throw new InvalidOperationException("Clock patch was not installed: " + method.Name);
            _status = "patched";
            _log?.Invoke($"generic stopwatch bridge: {method.DeclaringType?.Name}.{method.Name} ({expectedCalls} call sites)");
            return true;
        }
        catch
        {
            Targets.Remove(key);
            if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.PatchMethod == RewriteMethod) == true)
                _harmony.Unpatch(method, RewriteMethod);
            throw;
        }
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (!Targets.TryGetValue(Key(__originalMethod), out var target))
            throw new NotSupportedException("Clock target was not validated: " + __originalMethod.Name
                + " key=" + Key(__originalMethod) + " known=" + string.Join(",", Targets.Keys));
        return RewriteCalls(instructions, target.Calls);
    }

    internal static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions, int expectedCalls)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        var references = result.Where(i => Equals(i.operand, WallTimestamp)).ToArray();
        if (references.Length != expectedCalls || references.Any(i => i.opcode != OpCodes.Call))
            throw new NotSupportedException("Stopwatch.GetTimestamp call shape changed.");
        foreach (var instruction in references) instruction.operand = AdaptedTimestamp;
        return result;
    }

    public static long ReadTimestamp()
    {
        if (Clock.Active) _captureReads++;
        return Clock.Read();
    }

    internal static void BeginExport(Func<double> videoTime)
    {
        if (_harmony == null) return;
        _captureReads = 0;
        Clock.Begin(videoTime);
        _status = "active";
    }

    internal static void EndExport()
    {
        if (!Clock.Active) return;
        Clock.End();
        _status = "patched";
        _log?.Invoke($"generic stopwatch bridge: wall clock resumed; {_captureReads} export reads.");
    }

    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { _log?.Invoke("generic stopwatch bridge end failed: " + error.Message); return false; }
        if (Clock.Offset != 0)
        {
            _log?.Invoke("generic stopwatch bridge keeps its continuous offset until AA exits.");
            return false;
        }
        if (_harmony != null)
        {
            foreach (var pair in Targets.ToArray())
            {
                var method = pair.Value.Method;
                _harmony.Unpatch(method, RewriteMethod);
                if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.PatchMethod == RewriteMethod) == true)
                    return false;
                Targets.Remove(pair.Key);
            }
        }
        _harmony = null;
        _status = "stopped";
        return true;
    }

    private static string Key(MethodBase method) => method.Module.ModuleVersionId + ":" + method.MetadataToken;

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
    }

    private static bool IsWallTimestampCall(CecilInstruction instruction) =>
        instruction.OpCode == CecilOpCodes.Call
        && instruction.Operand is MethodReference reference
        && reference.FullName == WallTimestampSignature;
}
