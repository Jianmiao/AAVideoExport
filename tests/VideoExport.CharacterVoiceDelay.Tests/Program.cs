using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonoMod.Cil;
using Rukari.CharacterVoice.Core;

if (!AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled) return ProductionDelayChecks.Run();

// Real Harmony runs against the exact installed pure-managed Gate, never the
// plugin Runtime or AA. FakeRuntime supplies only its static ownership fields.
int passed = 0;
var faults = new List<Exception>();
var gateType = typeof(VoicePlaybackGate);
var observe = gateType.GetMethod(nameof(VoicePlaybackGate.ObservePlayback))!;
var expire = gateType.GetMethod(nameof(VoicePlaybackGate.Expire))!;
var deadline = gateType.GetField("_deadline", BindingFlags.Instance | BindingFlags.NonPublic)!;
var playbackDeadline = gateType.GetField("_playbackDeadline", BindingFlags.Instance | BindingFlags.NonPublic)!;
var api = new AdapterApi();
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Near(double actual, double expected, double tolerance = 0.0001) => Check(Math.Abs(actual - expected) <= tolerance, $"expected {expected:R}, got {actual:R}");
double Wall() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
void Test(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); passed++; }
    finally { Check(api.Shutdown(), "adapter cleanup failed"); }
}
void Install()
{
    faults.Clear(); FakeRuntime.Reset();
    api.Initialize(new Harmony("aave.test.character-voice-delay"), _ => { }, faults.Add);
    api.Bind(typeof(FakeRuntime));
    Check(api.Ready, "validated actual gate must install");
}
long Playing(double delay, double wall = 100)
{
    var g = FakeRuntime.Value;
    long s = g.Begin("aavt/voice/audit", wall, 10, delay);
    Check(g.MarkStarting(s, wall, 10), "start rejected");
    Check(g.ObservePlayback(s, true, wall + 1, 300), "native start rejected");
    return s;
}
void ExpectThrows(Action action, string message)
{
    bool threw = false;
    try { action(); } catch { threw = true; }
    Check(threw, message);
}

Check(api.Exists, "CharacterVoiceDelayClock is missing: authored delay cannot follow committed frames");
Test("outside capture actual gate retains ordinary wall timing", () =>
{
    Install(); var s = Playing(2);
    Check(FakeRuntime.Value.ObservePlayback(s, false, 105, 300), "end not observed");
    Near((double)deadline.GetValue(FakeRuntime.Value)!, 107);
    Check(!FakeRuntime.Value.Expire(106.9) && FakeRuntime.Value.Active, "wall delay expired early");
    Check(!FakeRuntime.Value.Expire(107) && FakeRuntime.Value.Phase == VoicePlaybackPhase.Completed, "normal completion changed Expire's return");
    Check(api.Reads == 0, "outside capture read frame clock");
});
Test("capture changes delay only while load start and playback watchdogs remain wall based", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value;
    g.Begin("aavt/voice/load", 100, 10, 2);
    Check(g.Expire(110) && g.LastReason == "loading-timeout", "loading watchdog lost wall time");
    var s = g.Begin("aavt/voice/start", 200, 10, 2); g.MarkStarting(s, 200, 1);
    Check(g.Expire(201) && g.LastReason == "playback-did-not-start", "start watchdog lost wall time");
    s = Playing(2, 300); g.ObservePlayback(s, true, 600, 300);
    Near((double)playbackDeadline.GetValue(g)!, 601);
    Check(g.Expire(601) && g.LastReason == "playback-timeout", "hard playback watchdog was refreshed or virtualized");
    s = Playing(2, 700); video = 1; g.ObservePlayback(s, false, 705, 300);
    Near((double)deadline.GetValue(g)!, 3);
    video = Math.BitDecrement(3); Check(!g.Expire(999999) && g.Active, "strict boundary expired early");
    video = 3; Check(!g.Expire(999999) && g.Phase == VoicePlaybackPhase.Completed, "frame delay did not complete normally");
});
Test("integer frames resolve normalized tenth-second deadlines without epsilon", () =>
{
    foreach (int fps in new[] { 24, 30, 60 })
    {
        Install(); long frame = 0; api.BeginFrames(() => frame, fps); var g = FakeRuntime.Value; var s = Playing(0.1);
        frame = 1; g.ObservePlayback(s, false, 105, 300);
        int waitFrames = fps == 24 ? 3 : fps == 30 ? 3 : 6;
        frame = waitFrames; Check(!g.Expire(1000) && g.Active, $"{fps}fps ended before authored duration");
        frame = 1 + waitFrames; Check(!g.Expire(1000) && g.Phase == VoicePlaybackPhase.Completed, $"{fps}fps missed exact first eligible frame");
        Check(api.Shutdown(), "loop cleanup failed");
    }
});
Test("unrelated gate stale sequence and worker calls keep original inputs", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2);
    Check(!g.ObservePlayback(s - 1, false, 105, 300) && g.Phase == VoicePlaybackPhase.Playing, "stale request altered gate");
    var unrelated = new VoicePlaybackGate(); long other = unrelated.Begin("aavt/voice/other", 100, 10, 2); unrelated.MarkStarting(other, 100, 10); unrelated.ObservePlayback(other, true, 101, 300); unrelated.ObservePlayback(other, false, 105, 300);
    Near((double)deadline.GetValue(unrelated)!, 107);
    Task.Run(() => g.ObservePlayback(s, false, 105, 300)).GetAwaiter().GetResult();
    Near((double)deadline.GetValue(g)!, 107);
    api.End(); Near((double)deadline.GetValue(g)!, 107);
    Check(api.Reads == 0, "ignored request sampled export time");
});
Test("existing delay acquisition and cancellation preserve remaining duration", () =>
{
    Install(); var s = Playing(2); double now = Wall(); FakeRuntime.Value.ObservePlayback(s, false, now, 300);
    double video = 20000; api.Begin(() => video); double converted = (double)deadline.GetValue(FakeRuntime.Value)!;
    Check(converted > 1.9 && converted <= 2, "capture entry must be relative and preserve remaining delay");
    video += 0.5; FakeRuntime.Value.Expire(999999); double before = Wall(); api.End(); double after = Wall();
    double restored = (double)deadline.GetValue(FakeRuntime.Value)!;
    Check(restored >= before + converted - 0.5 && restored <= after + converted - 0.5, "cancellation changed remaining delay");
    Check(FakeRuntime.Value.Phase == VoicePlaybackPhase.PostPlaybackDelay, "clock cleanup canceled the voice");
});
Test("new request is restored instead of a stale captured request", () =>
{
    Install(); long frame = 0; api.BeginFrames(() => frame, 30); var g = FakeRuntime.Value; long first = Playing(2);
    frame = 1; g.ObservePlayback(first, false, 105, 300); g.Cancel("manual");
    long second = Playing(0.3, 200); frame = 8; g.ObservePlayback(second, false, 205, 300); frame = 11;
    double before = Wall(); api.End(); double after = Wall(); double restored = (double)deadline.GetValue(g)!;
    Check(restored >= before + 0.2 && restored <= after + 0.2, "restored predecessor or frame-rounded remainder");
    double video = 0; api.Begin(() => video); video = 0.1; g.Expire(99999); api.End();
    Check(g.RequestSequence == second && g.Phase == VoicePlaybackPhase.PostPlaybackDelay, "retry changed identity or canceled current voice");
});
Test("manual cancellation and a fresh loading request are not resurrected during restore", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2); g.ObservePlayback(s, false, 105, 300);
    g.Cancel("manual"); g.Begin("aavt/voice/new", 500, 10, 1); api.End();
    Near((double)deadline.GetValue(g)!, 510); Check(g.Phase == VoicePlaybackPhase.Loading, "restored an invalidated delay");
});
Test("nonfinite Begin never arms mapping or changes a live deadline", () =>
{
    Install(); var s = Playing(2); FakeRuntime.Value.ObservePlayback(s, false, 105, 300);
    ExpectThrows(() => api.Begin(() => double.NaN), "NaN clock accepted"); Near((double)deadline.GetValue(FakeRuntime.Value)!, 107);
    ExpectThrows(() => api.BeginFrames(() => -1, 30), "negative frame accepted");
    ExpectThrows(() => api.BeginFrames(() => 0, 0), "zero FPS accepted");
    Check(faults.Count == 0, "pre-acquisition rejection need not notify active capture fault");
});
Test("bad active clock restores from last valid frame and notifies cancellation once", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2); video = 0.5; g.ObservePlayback(s, false, Wall(), 300);
    video = 1; g.Expire(Wall()); video = double.NaN; double before = Wall();
    Check(!g.Expire(Wall()) && g.Active, "fault must preserve original wait behavior"); double after = Wall();
    double restored = (double)deadline.GetValue(g)!; Check(restored >= before + 1.5 && restored <= after + 1.5, "fault stranded virtual deadline");
    Check(faults.Count == 1 && api.Status == "faulted", "host was not notified exactly once");
    g.Expire(Wall()); Check(faults.Count == 1, "disabled clock faulted repeatedly");
    video = 0; api.Begin(() => video); api.End(); Check(api.Ready, "safe fault recovery prevented later retry");
});
Test("backward frame input fails safely and callbacks cannot leak through Mod catch", () =>
{
    Install(); long frame = 10; api.BeginFrames(() => frame, 30); var g = FakeRuntime.Value; var s = Playing(2); frame = 20; g.ObservePlayback(s, false, Wall(), 300); frame = 19;
    g.Expire(Wall()); Check(faults.Count == 1 && g.Phase == VoicePlaybackPhase.PostPlaybackDelay, "backward frame failed through the Mod");
});
Test("failed deadline restore retains frame coordinates until restoration can retry", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2); g.ObservePlayback(s, false, Wall(), 300);
    double expected = (double)deadline.GetValue(g)!;
    deadline.SetValue(g, expected + 100); video = double.NaN;
    try
    {
        Check(!g.Expire(Wall()) && g.Phase == VoicePlaybackPhase.PostPlaybackDelay, "restoration failure passed wall time to a virtual deadline");
        Check(faults.Count == 1 && api.Status == "restoration-failed", "restoration failure was not surfaced");
    }
    finally { deadline.SetValue(g, expected); video = 0; api.End(); }
});
Test("EndExport throws while bad input and failed restoration retain a virtual deadline", () =>
{
    Install(); double video = 0; api.Begin(() => video);
    var g = FakeRuntime.Value; var s = Playing(2);
    g.ObservePlayback(s, false, Wall(), 300);
    double expected = (double)deadline.GetValue(g)!;
    deadline.SetValue(g, expected + 100); video = double.NaN;
    try
    {
        ExpectThrows(api.End, "EndExport reported successful cleanup while its deadline remained virtual");
        Check(!api.Ready && api.Status == "restoration-failed" && faults.Count == 1,
            "failed EndExport must retain recovery state and notify the host once");
        Check(!api.Shutdown(), "Shutdown removed clock hooks before deadline recovery");
        Check(Harmony.GetPatchInfo(expire)?.Owners.Contains("aave.test.character-voice-delay") == true,
            "failed cleanup removed the active delay prefix");
    }
    finally { deadline.SetValue(g, expected); video = 0; api.End(); }
    Check(api.Ready, "explicit successful retry did not restore readiness");
});
Test("native wait wrapper preserves audible true and post-delay return behavior", () =>
{
    Install(); long frame = 0; api.BeginFrames(() => frame, 30); var g = FakeRuntime.Value;
    long sequence = g.Begin("aavt/voice/wrapper", 100, 10, 0.1); g.MarkStarting(sequence, 100, 10);
    Check(g.ObserveNativeWait(true, 101, 300), "actual native true was suppressed");
    frame = 1; Check(g.ObserveNativeWait(false, 105, 300), "post-delay should hold native wait");
    frame = 3; Check(g.ObserveNativeWait(false, 99999, 300), "wrapper completed before exact frame");
    frame = 4; Check(!g.ObserveNativeWait(false, 99999, 300) && g.Phase == VoicePlaybackPhase.Completed, "wrapper did not release at exact frame");
});
Test("fault before audio-end transition preserves original wall-domain delay", () =>
{
    Install(); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2); video = double.PositiveInfinity;
    Check(g.ObservePlayback(s, false, 105, 300), "fault skipped original audio-end event");
    Near((double)deadline.GetValue(g)!, 107); Check(faults.Count == 1, "audio-end clock fault did not cancel capture");
});
Test("host cancellation callback exception cannot turn audio-end into Mod failure", () =>
{
    faults.Clear(); FakeRuntime.Reset();
    api.Initialize(new Harmony("aave.test.character-voice-delay"), _ => { }, error => { faults.Add(error); throw new InvalidOperationException("callback fixture"); });
    api.Bind(typeof(FakeRuntime)); double video = 0; api.Begin(() => video); var g = FakeRuntime.Value; var s = Playing(2); video = double.NaN;
    Check(g.ObservePlayback(s, false, 105, 300) && g.Phase == VoicePlaybackPhase.PostPlaybackDelay, "callback exception escaped into provider");
    Near((double)deadline.GetValue(g)!, 107); Check(faults.Count == 1, "callback invocation lost");
});
Test("foreign prefix and IL manipulator conflicts leave foreign patches installed", () =>
{
    foreach (bool manipulator in new[] { false, true })
    {
        var foreign = new Harmony("aave.test.character-voice-foreign");
        if (manipulator) foreign.Patch(expire, ilmanipulator: new HarmonyMethod(typeof(ForeignPatches), nameof(ForeignPatches.Manipulate)));
        else foreign.Patch(observe, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(ForeignPatches.Prefix)));
        try
        {
            faults.Clear(); FakeRuntime.Reset(); api.Initialize(new Harmony("aave.test.character-voice-delay"), _ => { }, faults.Add);
            ExpectThrows(() => api.Bind(typeof(FakeRuntime)), "foreign patch was accepted");
            Check(!api.Ready, "partial installation reported ready");
            var info = Harmony.GetPatchInfo(manipulator ? expire : observe)!;
            Check(info.Owners.Contains(foreign.Id), "rollback removed foreign owner");
        }
        finally { api.Shutdown(); foreign.UnpatchSelf(); }
    }
});
Test("second-patch failure rolls back first patch without removing another owner", () =>
{
    faults.Clear(); FakeRuntime.Reset(); api.Initialize(new Harmony("aave.test.character-voice-delay"), _ => { }, faults.Add);
    var patchMethod = typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Patch), Type.EmptyTypes)!;
    var injector = new Harmony("aave.test.character-voice-install-failure");
    injector.Patch(patchMethod, prefix: new HarmonyMethod(typeof(InstallFailure), nameof(InstallFailure.BeforePatch)));
    InstallFailure.Target = expire;
    try
    {
        ExpectThrows(() => api.Bind(typeof(FakeRuntime)), "second target failure was ignored");
        Check(!api.Ready, "partial installation is ready");
        Check(Harmony.GetPatchInfo(observe)?.Owners.Contains("aave.test.character-voice-delay") != true, "first patch survived rollback");
        Check(Harmony.GetPatchInfo(expire)?.Owners.Contains("aave.test.character-voice-delay") != true, "failed target retained owner");
        Check(Harmony.GetPatchInfo(patchMethod)?.Owners.Contains(injector.Id) == true, "rollback removed another owner");
    }
    finally { InstallFailure.Target = null; injector.UnpatchSelf(); }
});
Test("new conflict after install is rejected before capture mutation", () =>
{
    Install(); var s = Playing(2); FakeRuntime.Value.ObservePlayback(s, false, 105, 300); var foreign = new Harmony("aave.test.character-voice-late");
    foreign.Patch(expire, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(ForeignPatches.Prefix)));
    try { ExpectThrows(() => api.Begin(() => 0), "late conflict accepted"); Near((double)deadline.GetValue(FakeRuntime.Value)!, 107); }
    finally { foreign.UnpatchSelf(); }
});
Test("partial shutdown is not ready and retains a retryable cleanup path", () =>
{
    Install();
    var unpatch = typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Unpatch), new[] { typeof(HarmonyPatchType), typeof(string) })!;
    var injector = new Harmony("aave.test.character-voice-uninstall-failure");
    injector.Patch(unpatch, prefix: new HarmonyMethod(typeof(InstallFailure), nameof(InstallFailure.BeforePatch)));
    InstallFailure.Target = expire;
    try
    {
        Check(!api.Shutdown(), "partial cleanup reported success");
        Check(!api.Ready, "partial cleanup still advertises a complete adapter");
    }
    finally { InstallFailure.Target = null; injector.UnpatchSelf(); }
    Check(api.Shutdown(), "partial cleanup could not retry after failure cleared");
});
Test("per-frame binding validation rejects late IL manipulator without advancing capture clock", () =>
{
    Install(); double video = 0; api.Begin(() => video); long reads = api.Reads;
    api.ValidateBinding();
    var foreign = new Harmony("aave.test.character-voice-frame-validate");
    foreign.Patch(expire, ilmanipulator: new HarmonyMethod(typeof(ForeignPatches), nameof(ForeignPatches.Manipulate)));
    try { ExpectThrows(api.ValidateBinding, "late frame binding conflict was accepted"); Check(api.Reads == reads, "binding validation advanced the video clock"); }
    finally { foreign.UnpatchSelf(); api.End(); }
});
Test("per-frame binding validation rejects changed Gate ownership", () =>
{
    Install();
    var type = typeof(AAVideoExport.Plugin.CharacterVoiceDelayClock);
    var owned = type.GetField("_gate", BindingFlags.Static | BindingFlags.NonPublic)!;
    object? original = owned.GetValue(null);
    try { owned.SetValue(null, new object()); ExpectThrows(api.ValidateBinding, "replaced Gate ownership was accepted"); }
    finally { owned.SetValue(null, original); }
    api.ValidateBinding();
});
Test("shutdown removes only adapter owner and leaves future wall operation usable", () =>
{
    Install(); double video = 0; api.Begin(() => video); var s = Playing(2); FakeRuntime.Value.ObservePlayback(s, false, Wall(), 300); api.End();
    Check(api.Shutdown(), "shutdown refused a restored gate");
    Check(Harmony.GetPatchInfo(observe)?.Owners.Contains("aave.test.character-voice-delay") != true && Harmony.GetPatchInfo(expire)?.Owners.Contains("aave.test.character-voice-delay") != true, "owned patches remain");
    s = Playing(2); FakeRuntime.Value.ObservePlayback(s, false, 105, 300); Near((double)deadline.GetValue(FakeRuntime.Value)!, 107);
});
Console.WriteLine($"CharacterVoice delay adapter: {passed} tests passed; exact installed Gate, real Harmony; no native audio claim.");
return 0;

static class FakeRuntime
{
    private static readonly VoicePlaybackGate Gate = new();
    private static int _mainThreadId = Environment.CurrentManagedThreadId;
    internal static VoicePlaybackGate Value => Gate;
    internal static void Reset() { _mainThreadId = Environment.CurrentManagedThreadId; Gate.Cancel("test-reset"); }
}
static class ForeignPatches
{
    public static void Prefix() { }
    public static void Manipulate(ILContext context) { }
}
static class InstallFailure
{
    internal static MethodBase? Target;
    public static void BeforePatch(PatchProcessor __instance)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var harmony = (Harmony)typeof(PatchProcessor).GetField("instance", flags)!.GetValue(__instance)!;
        var original = (MethodBase)typeof(PatchProcessor).GetField("original", flags)!.GetValue(__instance)!;
        if (harmony.Id == "aave.test.character-voice-delay" && Equals(original, Target)) throw new InvalidOperationException("injected second-target failure");
    }
}
sealed class AdapterApi
{
    private readonly Type? _type = typeof(AdapterApi).Assembly.GetType("AAVideoExport.Plugin.CharacterVoiceDelayClock");
    internal bool Exists => _type != null;
    private object? Invoke(string name, Type[] types, params object?[] args)
    {
        try { return _type!.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, types, null)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private T Get<T>(string name) => (T)_type!.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
    internal void Initialize(Harmony harmony, Action<string> log, Action<Exception> fault) => Invoke("Initialize", new[]{typeof(Harmony),typeof(Action<string>),typeof(Action<Exception>)}, harmony, log, fault);
    internal void Bind(Type runtime) => Invoke("BindAndPatch", new[]{typeof(Type)}, runtime);
    internal void Begin(Func<double> time) => Invoke("BeginExport", new[]{typeof(Func<double>)}, time);
    internal void BeginFrames(Func<long> frame, int fps) => Invoke("BeginExport", new[]{typeof(Func<long>),typeof(int)}, frame, fps);
    internal void End() => Invoke("EndExport", Type.EmptyTypes);
    internal void ValidateBinding() => Invoke("ValidateBinding", Type.EmptyTypes);
    internal bool Shutdown() => _type == null || (bool)Invoke("Shutdown", Type.EmptyTypes)!;
    internal bool Ready => Get<bool>("IsReady");
    internal string Status => Get<string>("Status");
    internal long Reads => Get<long>("CaptureReads");
}
