using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace AAVideoExport.Plugin;

// Generic, behavior-driven managed-clock bridge. It rewrites managed call sites
// in third-party Mod assemblies; it never patches a named Mod and never patches
// the BCL/Unity method globally for DateTime or Environment. Time.* reads use
// the fixed engine hooks in GenericClockHooks as a global fallback.
internal static class GenericStopwatchBridge
{
    private const string WallTimestampSignature = "System.Int64 System.Diagnostics.Stopwatch::GetTimestamp()";
    private const string DateTimeNowSignature = "System.DateTime System.DateTime::get_Now()";
    private const string DateTimeUtcNowSignature = "System.DateTime System.DateTime::get_UtcNow()";
    private const string TickCountSignature = "System.Int32 System.Environment::get_TickCount()";
    private const string TickCount64Signature = "System.Int64 System.Environment::get_TickCount64()";

    private sealed record ClockCall(string Signature, MethodInfo Original, MethodInfo Replacement);

    private static readonly ClockCall[] ClockCalls = CreateClockCalls();
    private static readonly MethodInfo RewriteMethod = typeof(GenericStopwatchBridge).GetMethod(nameof(Rewrite), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Dictionary<string, (MethodBase Method, Dictionary<string, int> Calls)> Targets = new();
    private static readonly AnimationTimestampClock Clock = new();
    private static Harmony? _harmony;
    private static Assembly? _exporterAssembly;
    private static Action<string>? _log;
    private static Func<Assembly, bool>? _moduleSelector;
    private static long _captureReads;
    private static string _status = "stopped";

    internal static bool Active => Clock.Active;
    internal static long CaptureReadCount => _captureReads;
    internal static string Status => _status;
    internal static IReadOnlyCollection<MethodBase> PatchedMethods => Targets.Values.Select(value => value.Method).ToArray();
    internal static int PatchedCallCount(Assembly assembly) =>
        Targets.Values.Where(value => value.Method.DeclaringType?.Assembly == assembly).Sum(value => value.Calls.Values.Sum());

    private static ClockCall[] CreateClockCalls()
    {
        MethodInfo Require(Type type, string name, Type[]? parameters = null) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters ?? Type.EmptyTypes, null)
            ?? throw new MissingMethodException(type.FullName, name);
        return new[]
        {
            new ClockCall(WallTimestampSignature, Require(typeof(Stopwatch), nameof(Stopwatch.GetTimestamp)),
                Require(typeof(GenericStopwatchBridge), nameof(ReadTimestamp))),
            new ClockCall(DateTimeNowSignature, Require(typeof(DateTime), "get_Now"),
                Require(typeof(GenericStopwatchBridge), nameof(ReadDateTimeNow))),
            new ClockCall(DateTimeUtcNowSignature, Require(typeof(DateTime), "get_UtcNow"),
                Require(typeof(GenericStopwatchBridge), nameof(ReadDateTimeUtcNow))),
            new ClockCall(TickCountSignature, Require(typeof(Environment), "get_TickCount"),
                Require(typeof(GenericStopwatchBridge), nameof(ReadTickCount))),
            new ClockCall(TickCount64Signature, Require(typeof(Environment), "get_TickCount64"),
                Require(typeof(GenericStopwatchBridge), nameof(ReadTickCount64)))
        };
    }

    internal static void Initialize(Harmony harmony, Action<string> log, Assembly? exporterAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(log);
        if (_harmony != null) throw new InvalidOperationException("Generic clock bridge is already initialized.");
        _harmony = harmony;
        _exporterAssembly = exporterAssembly;
        _log = log;
        _status = "ready";
    }

    internal static void AttachLoadedModules(Func<Assembly, bool> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _moduleSelector = selector;
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => TryPatchLoaded(args.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().ToArray()) TryPatchLoaded(assembly);
    }

    private static void TryPatchLoaded(Assembly assembly)
    {
        try
        {
            if (_harmony == null || _moduleSelector?.Invoke(assembly) != true) return;
            if (assembly.IsDynamic) return;
            string? location;
            try { location = assembly.Location; }
            catch (NotSupportedException) { return; }
            // BepInEx/IL2CPP commonly loads plugin assemblies from memory
            // (byte[]), so there is no readable file for Cecil. Inspect the
            // live managed method bodies instead; a pathless Mod still has
            // observable call sites.
            PatchAssembly(assembly);
        }
        catch (Exception error)
        {
            _log?.Invoke("generic clock bridge skipped " + assembly.GetName().Name + ": " + error.Message);
        }
    }

    // Discovers call sites in one managed assembly and patches every caller
    // that contains at least one supported clock call. The caller decides which
    // plugin assemblies are in scope, not the Mod's name.
    internal static int PatchAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (_exporterAssembly != null && assembly == _exporterAssembly)
            throw new ArgumentException("The exporter's own assembly must keep raw wall time.", nameof(assembly));
        int installed = 0;
        string? location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
        {
            var live = new List<(MethodInfo Method, Dictionary<string, int> Calls)>();
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var calls = CountClockCalls(assembly.ManifestModule, method);
                    if (calls.Count > 0) live.Add((method, calls));
                }
            }
            foreach (var target in live) if (TryPatch(target.Method, target.Calls)) installed++;
            return installed;
        }
        foreach (var target in DiscoverClockCallers(assembly))
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
                int calls = method.Body.Instructions.Count(instruction => IsClockCall(instruction, WallTimestampSignature));
                if (calls == 0) continue;
                var runtime = assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32()) as MethodInfo
                    ?? throw new NotSupportedException("Clock caller did not resolve to a method: " + method.FullName);
                result.Add((runtime, calls));
            }
        }
        return result;
    }

    internal static IReadOnlyList<(MethodInfo Method, Dictionary<string, int> Calls)> DiscoverClockCallers(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new NotSupportedException("Managed Mod assembly has no readable location.");
        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        var result = new List<(MethodInfo, Dictionary<string, int>)>();
        foreach (var type in module.Types.SelectMany(Flatten))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.IsAbstract) continue;
                var calls = new Dictionary<string, int>();
                foreach (var instruction in method.Body.Instructions)
                {
                    foreach (var clock in ClockCalls)
                    {
                        if (!IsClockCall(instruction, clock.Signature)) continue;
                        calls[clock.Signature] = calls.TryGetValue(clock.Signature, out int count) ? count + 1 : 1;
                    }
                }
                if (calls.Count == 0) continue;
                var runtime = assembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32()) as MethodInfo
                    ?? throw new NotSupportedException("Clock caller did not resolve to a method: " + method.FullName);
                result.Add((runtime, calls));
            }
        }
        return result;
    }

    internal static bool TryPatch(MethodInfo method, int expectedCalls) =>
        TryPatch(method, new Dictionary<string, int> { [WallTimestampSignature] = expectedCalls });

    internal static bool TryPatch(MethodInfo method, Dictionary<string, int> expectedCalls)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(expectedCalls);
        if (_harmony == null) throw new InvalidOperationException("Generic clock bridge is not initialized.");
        if (expectedCalls.Count == 0 || expectedCalls.Values.Any(value => value <= 0) || method.GetMethodBody() == null)
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
            _log?.Invoke($"generic clock bridge: {method.DeclaringType?.Name}.{method.Name} ({expectedCalls.Values.Sum()} call sites)");
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
        string key = Key(__originalMethod);
        if (!Targets.TryGetValue(key, out var target))
            throw new NotSupportedException("Clock target was not validated: " + __originalMethod.Name
                + " key=" + key + " known=" + string.Join(",", Targets.Keys));
        return RewriteCalls(instructions, target.Calls);
    }

    internal static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions, int expectedCalls) =>
        RewriteCalls(instructions, new Dictionary<string, int> { [WallTimestampSignature] = expectedCalls });

    internal static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions, IReadOnlyDictionary<string, int> expectedCalls)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        foreach (var clock in ClockCalls)
        {
            int expected = expectedCalls.TryGetValue(clock.Signature, out int value) ? value : 0;
            var references = result.Where(i => Equals(i.operand, clock.Original)).ToArray();
            if (references.Length != expected || references.Any(i => i.opcode != OpCodes.Call))
                throw new NotSupportedException("Clock call shape changed: " + clock.Signature);
            foreach (var instruction in references) instruction.operand = clock.Replacement;
        }
        return result;
    }

    public static long ReadTimestamp()
    {
        CountCaptureRead();
        return Clock.Read();
    }

    public static DateTime ReadDateTimeNow()
    {
        CountCaptureRead();
        return VirtualTime.LocalNow;
    }

    public static DateTime ReadDateTimeUtcNow()
    {
        CountCaptureRead();
        return VirtualTime.UtcNow;
    }

    public static int ReadTickCount()
    {
        CountCaptureRead();
        return VirtualTime.TickCount;
    }

    public static long ReadTickCount64()
    {
        CountCaptureRead();
        return VirtualTime.TickCount64;
    }

    private static void CountCaptureRead()
    {
        if (Clock.Active) _captureReads++;
    }

    internal static void BeginExport(Func<double> videoTime)
    {
        if (_harmony == null) return;
        _captureReads = 0;
        VirtualTime.Bind(videoTime);
        Clock.Begin(videoTime);
        _status = "active";
    }

    internal static void EndExport()
    {
        if (!Clock.Active) return;
        Clock.End();
        VirtualTime.Unbind();
        _status = "patched";
        _log?.Invoke($"generic clock bridge: wall clock resumed; {_captureReads} export reads.");
    }

    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { _log?.Invoke("generic clock bridge end failed: " + error.Message); return false; }
        if (Clock.Offset != 0)
        {
            _log?.Invoke("generic clock bridge keeps its continuous offset until AA exits.");
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

    private static Dictionary<string, int> CountClockCalls(Module module, MethodInfo method)
    {
        var result = new Dictionary<string, int>();
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { return result; }
        byte[]? il = body?.GetILAsByteArray();
        if (il == null) return result;
        for (int index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] != 0x28) continue;
            int token = BitConverter.ToInt32(il, index + 1);
            MethodBase? resolved;
            try { resolved = module.ResolveMethod(token); }
            catch { continue; }
            if (resolved is not MethodInfo info) continue;
            string signature = SignatureOf(info);
            if (signature.Length == 0) continue;
            result[signature] = result.TryGetValue(signature, out int count) ? count + 1 : 1;
        }
        return result;
    }

    private static string SignatureOf(MethodBase method)
    {
        foreach (var clock in ClockCalls)
            if (clock.Original.DeclaringType == method.DeclaringType && clock.Original.Name == method.Name
                && clock.Original.GetParameters().Length == method.GetParameters().Length)
                return clock.Signature;
        return "";
    }

    private static string Key(MethodBase method) => method.Module.ModuleVersionId + ":" + method.MetadataToken;

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
    }

    private static bool IsClockCall(CecilInstruction instruction, string signature) =>
        instruction.OpCode == CecilOpCodes.Call
        && instruction.Operand is MethodReference reference
        && reference.FullName == signature;
}

internal static class VirtualTime
{
    private static readonly DateTime EpochUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static Func<double>? _readElapsedSeconds;

    internal static bool Active => _readElapsedSeconds != null;

    internal static void Bind(Func<double> readElapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(readElapsedSeconds);
        _readElapsedSeconds = readElapsedSeconds;
    }

    internal static void Unbind() => _readElapsedSeconds = null;

    private static double ElapsedSeconds
    {
        get
        {
            double value = _readElapsedSeconds?.Invoke() ?? 0;
            if (!double.IsFinite(value)) throw new InvalidOperationException("Virtual clock is not finite.");
            return Math.Max(0, value);
        }
    }

    internal static DateTime UtcNow => Active
        ? EpochUtc.AddSeconds(ElapsedSeconds)
        : DateTime.UtcNow;

    internal static DateTime LocalNow => Active
        ? UtcNow.ToLocalTime()
        : DateTime.Now;

    internal static int TickCount => Active
        ? unchecked((int)(ElapsedSeconds * 1000.0))
        : Environment.TickCount;

    internal static long TickCount64 => Active
        ? (long)Math.Round(ElapsedSeconds * 1000.0, MidpointRounding.AwayFromZero)
        : Environment.TickCount64;
}
