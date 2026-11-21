using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using AAVideoExport.Core;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Only the seven inspected managed call sites use this clock. Unity's native
// realtime clock, decoder watchdogs, and other Mods keep their original clocks.
internal static class FlandrePortraitClock
{
    private const string AssemblyName = "FlandreScarletPortrait";
    private static readonly Version SupportedVersion = new(5, 2, 0, 0);
    private static readonly Guid SupportedModule = new("984e19fc-d376-4133-bb4b-dcf8e30eb031");
    private const string SupportedHash = "6DB74B5B48F13E2E9C81D650172A3561A41BD526EE1A0BF2D29604CCD93A2EB8";
    private static readonly MethodInfo WallGetter = typeof(UnityEngine.Time).GetProperty("realtimeSinceStartup")!.GetMethod!;
    private static readonly MethodInfo AdaptedGetter = typeof(FlandrePortraitClock).GetMethod(nameof(ReadRealtime))!;
    private static readonly MethodInfo RewriteMethod = typeof(FlandrePortraitClock).GetMethod(nameof(Rewrite), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Dictionary<MethodBase, int> Targets = new();
    private static readonly PortraitRealtimeClock Clock = new();
    private static Harmony? _harmony;
    private static Action<string>? _log;
    private static volatile bool _pending;
    private static bool _installed;
    private static long _captureReads;

    internal static bool IsLoaded => LoadedAssemblies().Length != 0;
    internal static bool IsReady => _installed;
    internal static string Status { get; private set; } = "stopped";
    internal static string FailureReason { get; private set; } = "";
    internal static long CaptureReadCount => _captureReads;

    internal static void Initialize(Harmony harmony, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(log);
        if (_harmony != null) throw new InvalidOperationException("Portrait clock is already initialized.");
        _harmony = harmony;
        _log = log;
        _pending = true;
        Status = "waiting";
        FailureReason = "";
        AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
        TryInstall();
    }

    private static Assembly[] LoadedAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name == AssemblyName).ToArray();

    private static void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        // Installation is deferred to the host thread, never assembly resolution.
        if (args.LoadedAssembly.GetName().Name == AssemblyName) _pending = true;
    }

    internal static void TryInstall()
    {
        if (_installed || !_pending || _harmony == null) return;
        _pending = false;
        var assemblies = LoadedAssemblies();
        if (assemblies.Length == 0) return;
        try
        {
            if (assemblies.Length != 1) throw new NotSupportedException("Multiple Portrait assemblies are loaded.");
            var assembly = assemblies[0];
            ValidateIdentity(assembly);
            var targets = ResolveTargets(assembly);
            PatchValidatedTargets(_harmony, targets);
            _installed = true;
            Status = "full";
            FailureReason = "";
            _log?.Invoke("Flandre Portrait 5.2.0: scoped realtime clock installed (5 methods, 7 calls, including both async state machines).");
        }
        catch (Exception error)
        {
            Status = "unsupported";
            FailureReason = error.Message;
            _log?.Invoke("Flandre Portrait strict clock unavailable: " + error.Message);
        }
    }

    internal static bool IsSupportedBuild(Version version, Guid module, string hash) =>
        version == SupportedVersion && module == SupportedModule && string.Equals(hash, SupportedHash, StringComparison.OrdinalIgnoreCase);

    private static void ValidateIdentity(Assembly assembly)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
        {
            if (assembly.GetName().Version != SupportedVersion)
                throw new NotSupportedException("Portrait clock interface version is not supported.");
            return;
        }
        string location = assembly.Location;
        if (string.IsNullOrEmpty(location)) throw new NotSupportedException("Portrait assembly location is unavailable.");
        using var stream = File.OpenRead(location);
        using var sha256 = SHA256.Create();
        string hash = Convert.ToHexString(sha256.ComputeHash(stream));
        if (!IsSupportedBuild(assembly.GetName().Version ?? new Version(0, 0), assembly.ManifestModule.ModuleVersionId, hash))
            throw new NotSupportedException("Portrait version, module identity, or file hash is not the inspected 5.2.0 build.");
    }

    private static Dictionary<MethodInfo, int> ResolveTargets(Assembly assembly)
    {
        var targets = new Dictionary<MethodInfo, int>();
        void Add(string type, string name, bool isStatic, int calls, params string[] parameters)
        {
            var owner = assembly.GetType("FlandreScarletPortrait." + type, true)!;
            var methods = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name).ToArray();
            if (methods.Length != 1) throw new NotSupportedException("Portrait target is missing or ambiguous: " + type + "." + name);
            var method = methods[0];
            if (method.IsStatic != isStatic || method.ReturnType != typeof(void) || method.ContainsGenericParameters
                || !method.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
                throw new NotSupportedException("Portrait target signature changed: " + type + "." + name);
            targets.Add(method, calls);
        }
        Add("FLRuntime", "AdvancePostfix", true, 1, "Test", "System.Boolean", "System.Object");
        Add("FLRuntime", "EnterNodeCore", true, 1, "Test", "System.Int32", "System.Object", "System.String");
        Add("FLSlots", "ApplyOne", true, 1, "Test", "System.Int32", "FlandreScarletPortrait.FLSlots+SlotState");
        Add("FLCommands+<Animate>d__10", "MoveNext", false, 2);
        Add("FLCommands+<Run>d__0", "MoveNext", false, 2);
        return targets;
    }

    // This lower seam also runs against real Harmony-managed test fixtures.
    // The production caller validates the complete assembly identity first.
    internal static void PatchValidatedTargets(Harmony harmony, IReadOnlyDictionary<MethodInfo, int> targets)
    {
        if (Targets.Count != 0) throw new InvalidOperationException("Portrait patch targets are already registered.");
        if (targets.Count == 0) throw new ArgumentException("Portrait target set is empty.", nameof(targets));
        foreach (var pair in targets)
        {
            if (pair.Value <= 0 || pair.Key.GetMethodBody() == null) throw new NotSupportedException("Portrait target has no managed body or clock calls.");
            EnsureNoConflicts(pair.Key, false);
            RewriteCalls(PatchProcessor.GetOriginalInstructions(pair.Key, (ILGenerator?)null), pair.Value).ToList();
        }
        foreach (var pair in targets) Targets.Add(pair.Key, pair.Value);
        try
        {
            foreach (var method in targets.Keys)
                harmony.Patch(method, transpiler: new HarmonyMethod(RewriteMethod));
            foreach (var method in targets.Keys) EnsureNoConflicts(method, true);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var method in targets.Keys)
            {
                try
                {
                    harmony.Unpatch(method, RewriteMethod);
                    if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.PatchMethod == RewriteMethod) != true)
                        Targets.Remove(method);
                    else failures.Add(new InvalidOperationException("Portrait rollback left a patch installed: " + method.Name));
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            throw new AggregateException("Portrait clock installation failed; all installed patches were rolled back where possible.", failures);
        }
    }

    private static void EnsureNoConflicts(MethodInfo method, bool expectOwnPatch)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var info = Harmony.GetPatchInfo(method);
        if (info == null)
        {
            if (expectOwnPatch) throw new NotSupportedException("Portrait clock patch was removed: " + method.Name);
            return;
        }
        if (info.Prefixes.Any() || info.Postfixes.Any() || info.Finalizers.Any()
            || info.Transpilers.Any(p => !expectOwnPatch || p.PatchMethod != RewriteMethod)
            || (expectOwnPatch && info.Transpilers.Count(p => p.PatchMethod == RewriteMethod) != 1))
            throw new NotSupportedException("Portrait target has conflicting patches: " + method.DeclaringType?.FullName + "." + method.Name);
    }

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (!Targets.TryGetValue(__originalMethod, out int calls)) throw new NotSupportedException("Portrait target was not validated.");
        return RewriteCalls(instructions, calls);
    }

    internal static IEnumerable<CodeInstruction> RewriteCalls(IEnumerable<CodeInstruction> instructions, int expectedCalls)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        var references = result.Where(i => Equals(i.operand, WallGetter)).ToArray();
        if (references.Length != expectedCalls || references.Any(i => i.opcode != OpCodes.Call))
            throw new NotSupportedException("Portrait realtime call shape changed.");
        foreach (var instruction in references) instruction.operand = AdaptedGetter;
        return result;
    }

    public static float ReadRealtime()
    {
        if (Clock.Active) _captureReads++;
        double value = Clock.Read();
        if (value > float.MaxValue || value < -float.MaxValue) throw new InvalidOperationException("Portrait clock exceeded Single precision range.");
        return (float)value;
    }

    internal static void EnsureStrictExportAllowed()
    {
        if (!IsLoaded) return;
        TryInstall();
        try
        {
            if (!_installed || LoadedAssemblies().Length != 1 || Status != "full")
                throw new NotSupportedException(string.IsNullOrWhiteSpace(FailureReason) ? "Portrait clock adapter is not ready." : FailureReason);
            foreach (var method in Targets.Keys.Cast<MethodInfo>()) EnsureNoConflicts(method, true);
        }
        catch (Exception error)
        {
            Status = "unsupported";
            FailureReason = error.Message;
            throw new ExportException("strict_portrait_clock_unsupported", "Flandre Portrait cannot guarantee strict frame timing: " + error.Message);
        }
    }

    internal static void BeginExport(Func<double> videoTime)
    {
        if (!_installed) return;
        EnsureStrictExportAllowed();
        Clock.Begin(videoTime);
        _captureReads = 0;
    }

    internal static void EndExport()
    {
        if (!Clock.Active) return;
        try { Clock.End(); }
        catch (Exception error)
        {
            Status = "restoration-failed";
            FailureReason = error.Message;
            throw;
        }
        _log?.Invoke($"Flandre Portrait: continuous wall clock resumed; {_captureReads} export clock reads.");
    }

    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { _log?.Invoke("Portrait clock end failed: " + error.Message); }
        // Async locals can still hold virtual timestamps. Removing the offset
        // would make those pending animations jump, so hot unload is refused.
        if (Clock.Offset != 0)
        {
            _log?.Invoke("Portrait clock keeps its continuous offset until AA exits; hot unload is unavailable after an adjusted export.");
            return false;
        }
        if (Clock.Active) return false;
        if (_harmony != null)
        {
            foreach (var method in Targets.Keys.ToArray())
            {
                try
                {
                    _harmony.Unpatch(method, RewriteMethod);
                    if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.PatchMethod == RewriteMethod) == true)
                        throw new InvalidOperationException("Portrait clock patch is still installed: " + method.Name);
                    Targets.Remove(method);
                }
                catch (Exception error)
                {
                    Status = "restoration-failed";
                    FailureReason = error.Message;
                    _log?.Invoke("Portrait clock cannot be unloaded: " + error.Message);
                    return false;
                }
            }
        }
        Targets.Clear();
        AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
        _harmony = null;
        _log = null;
        _pending = false;
        _installed = false;
        Status = "stopped";
        FailureReason = "";
        return true;
    }
}

internal sealed class PortraitRealtimeClock
{
    private readonly Func<double> _wallTime;
    private Func<double>? _videoTime;
    private double _videoStart, _lastVideo, _origin, _lastValue;

    internal PortraitRealtimeClock(Func<double>? wallTime = null) => _wallTime = wallTime ?? (() => UnityEngine.Time.realtimeSinceStartup);
    internal bool Active => _videoTime != null;
    internal double Offset { get; private set; }

    internal double Read()
    {
        if (_videoTime == null) return Finite(_wallTime() + Offset);
        double video = Finite(_videoTime());
        if (video < _lastVideo) throw new InvalidOperationException("Portrait video clock moved backwards.");
        double value = Finite(_origin + (video - _videoStart));
        _lastVideo = video;
        return _lastValue = value;
    }

    internal void Begin(Func<double> videoTime)
    {
        ArgumentNullException.ThrowIfNull(videoTime);
        if (Active) throw new InvalidOperationException("Portrait video clock is already active.");
        double start = Finite(videoTime());
        double origin = Read();
        _videoStart = _lastVideo = start;
        _origin = _lastValue = origin;
        _videoTime = videoTime;
    }

    internal void End()
    {
        if (!Active) return;
        try { Read(); }
        finally
        {
            // Even a failed video callback restores continuous ordinary play
            // from the last valid sample instead of stranding a virtual clock.
            Offset = Finite(_lastValue - Finite(_wallTime()));
            _videoTime = null;
        }
    }

    private static double Finite(double value) => double.IsFinite(value)
        ? value : throw new InvalidOperationException("Portrait clock value is not finite.");
}
