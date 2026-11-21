using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Only the authored post-playback delay is a video-clock consumer. Native
// playback, loading/start/playback watchdogs, and Runtime.Now remain untouched.
internal static class CharacterVoiceDelayClock
{
    private const string AssemblyName = "Rukari.CharacterVoice";
    private const string BinaryHash = "06C4FE4F87282258FE45A8436FCDDBDCA079C58ED6356C31391F5BC3D855C054";
    private static readonly Guid ModuleId = new("3c455a03-837f-40d5-a719-281df650be68");
    private static readonly MethodInfo ObservePrefix = typeof(CharacterVoiceDelayClock).GetMethod(nameof(BeforeObserve), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ExpirePrefix = typeof(CharacterVoiceDelayClock).GetMethod(nameof(BeforeExpire), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Harmony? _harmony;
    private static Action<string>? _log;
    private static Action<Exception>? _onFault;
    private static MethodInfo? _observe, _expire;
    private static FieldInfo? _gateField, _mainThreadField, _deadlineField, _delayField;
    private static PropertyInfo? _phaseProperty, _sequenceProperty;
    private static object? _gate;
    private static int _controlThreadId, _fps;
    private static bool _installed, _restorationFailed, _faultReported;
    private static volatile bool _pending;
    private static Func<double>? _videoTime;
    private static Func<long>? _frameIndex;
    private static double _videoOrigin, _lastVideo, _lastRelative, _mappedDeadline;
    private static long _frameOrigin, _lastRawFrame, _lastFrame, _delayStartFrame, _dueFrame;
    private static long? _mappedSequence;
    private static int? _durationTenths;

    internal static bool IsReady => _installed && !_restorationFailed;
    internal static string Status { get; private set; } = "stopped";
    internal static string FailureReason { get; private set; } = "";
    internal static long CaptureReads { get; private set; }
    private static bool Active => _videoTime != null || _frameIndex != null;

    internal static void Initialize(Harmony harmony, Action<string> log, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(onFault);
        if (_harmony != null) throw new InvalidOperationException("CharacterVoice delay clock is already initialized.");
        _harmony = harmony; _log = log; _onFault = onFault;
        _controlThreadId = Environment.CurrentManagedThreadId;
        _pending = true; Status = "waiting"; FailureReason = "";
        AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
    }

    private static void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == AssemblyName) _pending = true;
    }

    internal static void TryInstall()
    {
        if (_installed || !_pending || _harmony == null || Environment.CurrentManagedThreadId != _controlThreadId) return;
        _pending = false;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == AssemblyName).ToArray();
        if (assemblies.Length == 0) return;
        try
        {
            if (assemblies.Length != 1) throw new NotSupportedException("Multiple CharacterVoice assemblies are loaded.");
            if (ExportBuildProfile.DevelopmentAuditEnabled) ValidateAssembly(assemblies[0]);
            BindAndPatch(assemblies[0].GetType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", true)!);
        }
        catch (Exception error)
        {
            Status = "unsupported"; FailureReason = error.Message;
            Log("CharacterVoice authored-delay adapter unavailable: " + error.Message);
        }
    }

    // Kept separate from assembly discovery so binding and Harmony behavior can
    // also be exercised against the real pure-managed Gate without AA startup.
    internal static void BindAndPatch(Type runtimeType)
    {
        if (_harmony == null || _installed) throw new InvalidOperationException("CharacterVoice delay adapter cannot bind in this state.");
        RequireControlThread();
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instances = BindingFlags.Instance | BindingFlags.NonPublic;
        var gateField = runtimeType.GetField("Gate", statics) ?? throw new MissingFieldException(runtimeType.FullName, "Gate");
        var mainThread = runtimeType.GetField("_mainThreadId", statics) ?? throw new MissingFieldException(runtimeType.FullName, "_mainThreadId");
        var type = gateField.FieldType;
        if (!gateField.IsInitOnly || !gateField.IsPrivate || mainThread.FieldType != typeof(int)
            || type.FullName != "Rukari.CharacterVoice.Core.VoicePlaybackGate" || !type.IsSealed)
            throw new NotSupportedException("CharacterVoice runtime ownership shape changed.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidateAssembly(type.Assembly);
        var deadline = type.GetField("_deadline", instances);
        var duration = type.GetField("_postPlaybackDelay", instances);
        var phase = type.GetProperty("Phase");
        var sequence = type.GetProperty("RequestSequence");
        if (deadline?.FieldType != typeof(double) || duration?.FieldType != typeof(double)
            || phase?.PropertyType.IsEnum != true || sequence?.PropertyType != typeof(long)
            || phase.GetMethod == null || sequence.GetMethod == null)
            throw new NotSupportedException("CharacterVoice gate timing shape changed.");
        string[] names = { "Idle", "Loading", "Starting", "Playing", "Completed", "Failed", "Canceled", "PostPlaybackDelay" };
        if (!Enum.GetNames(phase.PropertyType).SequenceEqual(names)
            || !Enum.GetValues(phase.PropertyType).Cast<object>().Select(Convert.ToInt32).SequenceEqual(Enumerable.Range(0, 8)))
            throw new NotSupportedException("CharacterVoice gate phases changed.");
        var observe = type.GetMethod("ObservePlayback", new[] { typeof(long), typeof(bool), typeof(double), typeof(double) });
        var expire = type.GetMethod("Expire", new[] { typeof(double) });
        ValidateMethod(observe, "814858EBE2BA5DEDEAFDAC6911A110D50EF9260234DA3AF2101DC743B5CF6460");
        ValidateMethod(expire, "8798CA31FDCCF2196CE17C37C69B120DF80F98569B1B0638467029B45C643987");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        { EnsureNoConflicts(observe!, ObservePrefix, false); EnsureNoConflicts(expire!, ExpirePrefix, false); }
        var gate = gateField.GetValue(null) ?? throw new InvalidOperationException("CharacterVoice gate is unavailable.");
        _gateField = gateField; _mainThreadField = mainThread; _deadlineField = deadline; _delayField = duration;
        _phaseProperty = phase; _sequenceProperty = sequence; _gate = gate; _observe = observe; _expire = expire;
        try
        {
            _harmony.Patch(observe!, prefix: new HarmonyMethod(ObservePrefix));
            _harmony.Patch(expire!, prefix: new HarmonyMethod(ExpirePrefix));
            if (ExportBuildProfile.DevelopmentAuditEnabled)
            { EnsureNoConflicts(observe!, ObservePrefix, true); EnsureNoConflicts(expire!, ExpirePrefix, true); }
            _installed = true; _pending = false; Status = "ready"; FailureReason = "";
            Log("CharacterVoice authored-delay adapter ready; native playback and wall watchdogs remain active.");
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            foreach (var method in new[] { observe!, expire! })
            {
                try { _harmony.Unpatch(method, HarmonyPatchType.Prefix, _harmony.Id); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            _installed = false; Status = "unsupported";
            FailureReason = string.Join("; ", errors.Select(e => e.Message));
            throw new AggregateException("CharacterVoice delay patch installation failed; owner-scoped rollback attempted.", errors);
        }
    }

    private static void ValidateAssembly(Assembly assembly)
    {
        if (assembly.GetName().Name != AssemblyName || assembly.GetName().Version != new Version(1, 6, 8, 0)
            || assembly.ManifestModule.ModuleVersionId != ModuleId || string.IsNullOrEmpty(assembly.Location)
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != BinaryHash)
            throw new NotSupportedException("CharacterVoice binary is outside the exact inspected 1.6.8 build.");
    }

    private static void ValidateMethod(MethodInfo? method, string hash)
    {
        if (method == null || method.IsStatic || method.IsVirtual || method.ReturnType != typeof(bool))
            throw new NotSupportedException("CharacterVoice gate method signature is unsupported.");
        if (ExportBuildProfile.DevelopmentAuditEnabled && (method.GetMethodBody()?.GetILAsByteArray() is not { } body
            || Convert.ToHexString(SHA256.HashData(body)) != hash))
            throw new NotSupportedException("CharacterVoice gate method IL changed.");
    }

    private static void EnsureNoConflicts(MethodInfo target, MethodInfo prefix, bool expectOwn)
    {
        var info = Harmony.GetPatchInfo(target);
        if (info == null)
        {
            if (expectOwn) throw new InvalidOperationException("CharacterVoice delay prefix is missing: " + target.Name);
            return;
        }
        if (info.Postfixes.Any() || info.Transpilers.Any() || info.Finalizers.Any() || info.ILManipulators.Any()
            || info.Prefixes.Any(p => !expectOwn || p.owner != _harmony!.Id || p.PatchMethod != prefix)
            || (expectOwn && info.Prefixes.Count(p => p.owner == _harmony!.Id && p.PatchMethod == prefix) != 1))
            throw new NotSupportedException("CharacterVoice gate has conflicting patches: " + target.Name);
    }

    internal static void BeginExport(Func<double> videoTime)
    {
        ArgumentNullException.ThrowIfNull(videoTime);
        if (!PrepareBegin()) return;
        double origin = Finite(videoTime());
        BeginCore(videoTime, null, origin, 0, 0);
    }

    internal static void BeginExport(Func<long> frameIndex, int fps)
    {
        ArgumentNullException.ThrowIfNull(frameIndex);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (!PrepareBegin()) return;
        long origin = frameIndex();
        if (origin < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        BeginCore(null, frameIndex, 0, origin, fps);
    }

    private static bool PrepareBegin()
    {
        if (!_installed)
        {
            if (Status is "unsupported" or "restoration-failed")
                throw new InvalidOperationException("CharacterVoice delay adapter is unavailable: " + FailureReason);
            return false;
        }
        RequireControlThread();
        if (Active || _restorationFailed) throw new InvalidOperationException("CharacterVoice delay clock has an unfinished capture.");
        ValidateBinding();
        return true;
    }

    // Read-only retained binding check. It never polls the frame/time delegate,
    // rewrites deadlines or substitutes a native playback result.
    internal static void ValidateBinding()
    {
        if (!_installed)
        {
            if (Status is "waiting" or "stopped" && !AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == AssemblyName)) return;
            throw new InvalidOperationException("CharacterVoice delay binding is unavailable: " + FailureReason);
        }
        RequireControlThread();
        if (_restorationFailed) throw new InvalidOperationException("CharacterVoice delay restoration remains pending.");
        if (!IsRuntimeThread()) throw new InvalidOperationException("CharacterVoice playback main thread is unavailable or changed.");
        if (!ReferenceEquals(_gate, _gateField!.GetValue(null))) throw new InvalidOperationException("CharacterVoice gate identity changed.");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        { EnsureNoConflicts(_observe!, ObservePrefix, true); EnsureNoConflicts(_expire!, ExpirePrefix, true); }
    }

    private static void BeginCore(Func<double>? video, Func<long>? frames, double origin, long frameOrigin, int fps)
    {
        long sequence = Sequence();
        bool delay = Phase() == 7;
        double oldDeadline = delay ? Finite((double)_deadlineField!.GetValue(_gate)!) : 0;
        double converted = delay ? Finite(oldDeadline - Wall()) : 0;
        // All fallible clock reads/validation precede the one field write.
        if (delay) _deadlineField!.SetValue(_gate, converted);
        _videoTime = video; _frameIndex = frames; _fps = fps;
        _videoOrigin = _lastVideo = origin; _frameOrigin = _lastRawFrame = frameOrigin;
        _lastRelative = 0; _lastFrame = 0; _mappedSequence = delay ? sequence : null;
        _mappedDeadline = converted; _durationTenths = null;
        _faultReported = false; CaptureReads = 0; Status = "capturing"; FailureReason = "";
    }

    private static void BeforeObserve(object __instance, long __0, bool __1, ref double __2)
    {
        if (!ShouldAdapt(__instance)) return;
        try
        {
            if (__0 != Sequence()) return;
            int phase = Phase();
            if (phase == 7)
            {
                if (_mappedSequence != __0) return;
                __2 = ComparisonTime(ReadRelative(true));
            }
            else if (phase == 3 && !__1)
            {
                double time = ReadRelative(true);
                double duration = Finite((double)_delayField!.GetValue(_gate)!);
                double due = Finite(time + duration);
                int? tenths = null; long dueFrame = 0;
                if (_frameIndex != null)
                {
                    int units = checked((int)Math.Round(duration * 10, MidpointRounding.AwayFromZero));
                    if (units < 0 || units > 100 || duration != units / 10.0)
                        throw new InvalidOperationException("CharacterVoice delay is not an inspected normalized tenth-second duration.");
                    tenths = units;
                    dueFrame = checked(_lastFrame + ((long)units * _fps + 9) / 10);
                }
                _mappedSequence = __0; _mappedDeadline = due; _durationTenths = tenths;
                _delayStartFrame = _lastFrame; _dueFrame = dueFrame;
                __2 = time;
            }
        }
        catch (Exception error) { PrefixFault(error, ref __2); }
    }

    private static void BeforeExpire(object __instance, ref double __0)
    {
        if (!ShouldAdapt(__instance)) return;
        try
        {
            if (Phase() == 7 && _mappedSequence == Sequence()) __0 = ComparisonTime(ReadRelative(true));
        }
        catch (Exception error) { PrefixFault(error, ref __0); }
    }

    private static bool ShouldAdapt(object instance) => Active && ReferenceEquals(instance, _gate)
        && Environment.CurrentManagedThreadId == _controlThreadId;

    private static double ReadRelative(bool count)
    {
        if (!IsRuntimeThread()) throw new InvalidOperationException("CharacterVoice main-thread ownership changed during capture.");
        double relative;
        if (_frameIndex != null)
        {
            long raw = _frameIndex();
            if (raw < _lastRawFrame) throw new InvalidOperationException("CharacterVoice frame clock moved backwards.");
            long frame = checked(raw - _frameOrigin);
            if (frame < 0 || frame > 9007199254740991L) throw new ArgumentOutOfRangeException("frame", "CharacterVoice frame clock exceeds exact integer range.");
            relative = Finite(frame / (double)_fps);
            _lastRawFrame = raw; _lastFrame = frame;
        }
        else
        {
            double value = Finite(_videoTime!());
            if (value < _lastVideo) throw new InvalidOperationException("CharacterVoice video clock moved backwards.");
            relative = Finite(value - _videoOrigin);
            _lastVideo = value;
        }
        _lastRelative = relative;
        if (count) CaptureReads++;
        return relative;
    }

    private static double ComparisonTime(double relative)
    {
        double deadline = Finite((double)_deadlineField!.GetValue(_gate)!);
        if (deadline != _mappedDeadline) throw new InvalidOperationException("CharacterVoice delay deadline changed outside the adapter.");
        if (_durationTenths == null) return relative;
        // Normalize is proven to return tenths. Integer frame eligibility fixes
        // only the representation at the exact rational boundary; no epsilon
        // or guessed clip-end time is introduced. The original comparison runs.
        if (_lastFrame >= _dueFrame) return Math.Max(relative, deadline);
        return relative < deadline ? relative : Math.BitDecrement(deadline);
    }

    internal static void EndExport()
    {
        if (!Active) return;
        RequireControlThread();
        double relative;
        try { relative = ReadRelative(false); }
        catch (Exception error)
        {
            RecoverAndNotify(error);
            // Lifecycle callers use an exception to retain RenderControl
            // ownership when restoration is incomplete. A notification alone
            // is insufficient: the host may already be inside cancellation.
            if (Active || _restorationFailed)
                throw new InvalidOperationException("CharacterVoice delay restoration remains pending: " + FailureReason, error);
            return;
        }
        try { Restore(relative); }
        catch (Exception error)
        {
            _restorationFailed = true; Status = "restoration-failed"; FailureReason = error.Message;
            NotifyFault(error); throw;
        }
    }

    private static void Restore(double relative)
    {
        if (!ReferenceEquals(_gate, _gateField!.GetValue(null))) throw new InvalidOperationException("Cannot restore a replaced CharacterVoice gate.");
        if (Phase() == 7 && _mappedSequence == Sequence())
        {
            double deadline = Finite((double)_deadlineField!.GetValue(_gate)!);
            if (deadline != _mappedDeadline) throw new InvalidOperationException("Cannot restore an externally changed CharacterVoice deadline.");
            double remaining = _durationTenths.HasValue
                ? (double)((decimal)_durationTenths.Value / 10m - (decimal)(_lastFrame - _delayStartFrame) / _fps)
                : deadline - relative;
            double restored = Finite(Wall() + Finite(remaining));
            _deadlineField.SetValue(_gate, restored);
        }
        _videoTime = null; _frameIndex = null; _mappedSequence = null; _durationTenths = null;
        _restorationFailed = false; Status = "ready";
    }

    private static void PrefixFault(Exception error, ref double now)
    {
        RecoverAndNotify(error);
        // If field restoration itself failed, retain the last coherent frame
        // domain rather than pass wall seconds to a still-virtual deadline.
        if (Active && _mappedSequence.HasValue)
        {
            try
            {
                if (Phase() == 7 && _mappedSequence == Sequence())
                {
                    now = _lastRelative;
                    now = ComparisonTime(_lastRelative);
                }
            }
            catch (Exception fallback) { FailureReason += "; fallback: " + fallback.Message; Log(FailureReason); }
        }
    }

    private static void RecoverAndNotify(Exception error)
    {
        Exception fault = error;
        try { Restore(_lastRelative); }
        catch (Exception restore)
        {
            _restorationFailed = true;
            fault = new AggregateException("CharacterVoice clock input failed and restoration remains pending.", error, restore);
        }
        Status = _restorationFailed ? "restoration-failed" : "faulted";
        FailureReason = fault.Message;
        NotifyFault(fault);
    }

    private static void NotifyFault(Exception error)
    {
        Log("CharacterVoice delay capture fault: " + error.Message);
        if (_faultReported) return;
        _faultReported = true;
        try { _onFault!(error); }
        catch (Exception callback)
        {
            FailureReason += "; host cancellation callback failed: " + callback.Message;
            Log(FailureReason);
        }
    }

    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { Log("CharacterVoice delay restoration failed: " + error.Message); return false; }
        if (Active || _restorationFailed) return false;
        if (_harmony != null)
        {
            foreach (var target in new[] { _observe, _expire }.Where(m => m != null))
            {
                try
                {
                    _harmony.Unpatch(target!, HarmonyPatchType.Prefix, _harmony.Id);
                    if (ExportBuildProfile.DevelopmentAuditEnabled && Harmony.GetPatchInfo(target!)?.Prefixes.Any(p => p.owner == _harmony.Id && (p.PatchMethod == ObservePrefix || p.PatchMethod == ExpirePrefix)) == true)
                        throw new InvalidOperationException("CharacterVoice delay prefix remains installed.");
                }
                catch (Exception error)
                {
                    // Bindings stay available for another Shutdown attempt, but
                    // a partially removed pair must never advertise readiness.
                    _installed = false; Status = "restoration-failed"; FailureReason = error.Message;
                    Log(error.Message); return false;
                }
            }
        }
        AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
        _harmony = null; _log = null; _onFault = null; _installed = false; _pending = false;
        _gate = null; _gateField = _mainThreadField = _deadlineField = _delayField = null;
        _phaseProperty = _sequenceProperty = null; _observe = _expire = null;
        Status = "stopped"; FailureReason = "";
        return true;
    }

    private static int Phase() => Convert.ToInt32(_phaseProperty!.GetValue(_gate));
    private static long Sequence() => (long)_sequenceProperty!.GetValue(_gate)!;
    private static bool IsRuntimeThread() => (int)_mainThreadField!.GetValue(null)! == Environment.CurrentManagedThreadId;
    private static void RequireControlThread()
    {
        if (Environment.CurrentManagedThreadId != _controlThreadId) throw new InvalidOperationException("CharacterVoice delay lifecycle requires the export main thread.");
    }
    private static double Wall() => Finite(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), "CharacterVoice clock values must be finite.");
    private static void Log(string value) { try { _log?.Invoke(value); } catch { /* Diagnostics must not alter voice playback. */ } }
}
