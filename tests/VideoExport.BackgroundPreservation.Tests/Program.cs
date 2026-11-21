using System.Collections;
using System.Reflection;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine.Video;

string references = args.Length > 0 ? Path.GetFullPath(args[0]) : @"F:\AzureArchive_101";
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string part in new[] { "BepInEx/core", "BepInEx/interop" })
    { string path = Path.Combine(references, part, name.Name + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path); }
    return null;
};
return Cases.Run();

internal static class Cases
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type Adapter = typeof(ControlledBackgroundVideoAdapter);
    private static readonly Type Snapshot = Adapter.GetNestedType("NativeSnapshot", BindingFlags.NonPublic)!;
    private static int passed, failed;
    private static readonly List<object> Results = new();
    internal static int Run()
    {
        Test("playing acquisition freezes the actual position after Pause rather than a stale pre-pause frame", () => WithPlayer(true, false, player =>
        {
            Check(player.frame == 4 && player.time == .4 && player.Paused && !player.Playing, "takeover did not freeze the coherent post-pause playhead");
            ControlledBackgroundVideoAdapter.Pump(0);
        }, player => Check(player.Playing && player.frame == 4 && player.time == .4, "release restored a stale pre-pause position"),
            player => player.AdvanceBeforePause = value => { value.frame = 4; value.time = .4; }));
        Test("failed pause retains ownership until release restores the original playback mode", () => WithPlayer(true, false, player =>
        {
            Check(Get("snapshot") != null && !player.Playing && RenderControlV1.IsRenderingOwned && !ControlledBackgroundVideoAdapter.IsReady,
                "failed takeover lost its retained native rollback state or resumed under ownership");
            ControlledBackgroundVideoAdapter.TryInstall();
            Check(!player.Playing, "failed acquisition resumed the decoder before outer release");
        }, player => Check(player.Playing && player.frame == 4 && player.time == .4 && Get("snapshot") == null,
            "outer release did not recover the original mode after a failed pause"),
            player => { player.AdvanceBeforePause = value => { value.frame = 4; value.time = .4; }; player.ThrowAfterPause = true; }, expectedTakeoverFailure: true));
        Test("playing prepared decoder retains nonzero frame through owned pumps and resumes only after release", () => WithPlayer(playing: true, awaiting: false, player =>
        {
            Check(player.Prepared && player.Paused && !player.Playing && player.frame == 3 && player.time == .3 && player.gameObject.activeSelf,
                "takeover discarded the prepared original playhead");
            Check(player.Calls.SequenceEqual(new[] { "Pause" }), "prepared takeover performed an operation other than Pause");
            for (int i = 0; i < 5; i++) { ControlledBackgroundVideoAdapter.Pump(i); ControlledBackgroundVideoAdapter.TryInstall(); }
            ControlledBackgroundVideoAdapter.EndExport();
            Check(!player.Playing && player.frame == 3 && player.time == .3 && Get("snapshot") != null, "EndExport resumed or lost original state before outer release");
        }, afterRelease: player =>
        {
            Check(player.Playing && !player.Paused && player.Prepared && player.frame == 3 && player.time == .3, "release did not resume original nonzero playing state");
            Check(player.Calls.SequenceEqual(new[] { "Pause", "Play" }), "release re-prepared or re-sought the original decoder");
            Check(Get("snapshot") == null, "successful continuity restoration retained a snapshot");
        }));
        Test("already paused decoder keeps its original state and receives no native playback commands", () => WithPlayer(false, false, player =>
        {
            ControlledBackgroundVideoAdapter.Pump(0); ControlledBackgroundVideoAdapter.EndExport();
            Check(player.Prepared && player.Paused && !player.Playing && player.time == .3 && player.frame == 3, "paused frame was lost during ownership");
        }, player =>
        {
            Check(player.Calls.Count == 0 && player.Prepared && player.Paused && !player.Playing && player.time == .3 && player.frame == 3,
                "restoring a paused decoder changed its state or restarted preparation");
        }));
        Test("queued first-frame playback remains stopped and inactive until release", () => WithPlayer(false, true, player =>
        {
            Check(!player.Prepared && !player.Playing && !player.gameObject.activeSelf && player.frame == -1 && player.time == 0,
                "queued native start remained live during ownership");
            Check(player.Calls.SequenceEqual(new[] { "Stop" }), "queued start was only paused instead of cancelled");
            ControlledBackgroundVideoAdapter.EndExport();
        }, player =>
        {
            Check(player.Calls.SequenceEqual(new[] { "Stop", "Stop", "Prepare" }) && !player.Playing, "queued decoder started before native preparation");
            player.CompletePreparation();
            Check(player.Playing && Get("snapshot") == null, "queued original playback did not resume after real preparation callback");
        }));
        Test("a native frame drift fails the production Pump", () => WithPlayer(true, false, player =>
        {
            player.frame = 2; player.time = .2;
            ExpectFailure(() => ControlledBackgroundVideoAdapter.Pump(0));
            player.frame = 3; player.time = .3;
        }));
        Test("a native resume fails the held non-active preparation interval", () => WithPlayer(true, false, player =>
        {
            ControlledBackgroundVideoAdapter.EndExport(); player.Playing = true;
            ExpectFailure(ControlledBackgroundVideoAdapter.TryInstall);
            player.Playing = false;
        }));
        Test("source settings drift fails even when the native frame stays still", () => WithPlayer(false, false, player =>
        {
            player.playbackSpeed = .5f;
            ExpectFailure(() => ControlledBackgroundVideoAdapter.Pump(0));
            player.playbackSpeed = 1;
        }));
        Test("an original root callback between passes resets only the owned lookup context", () => WithPlayer(true, false, player =>
        {
            ControlledBackgroundVideoAdapter.EndExport();
            var lookup = (IDictionary)Get("ResolutionCache")!; lookup.Add("stale-context", "stale-video");
            Owner.OnNewRootSeen(@"fixture\data\saves\second\bgs");
            Check(player.Prepared && !player.Playing && player.frame == 3 && player.time == .3 && Owner._currentContext as string == "editor"
                && Owner._basenameCache.Count == 1, "original context reset destroyed the retained native decoder or its restore state");
            Check(lookup.Count == 0, "owned root change retained the previous context's source cache");
            Owner.OnNewRootSeen(@"fixture\data\projects\original\bgs");
            ControlledBackgroundVideoAdapter.BeginExport(() => 0, 30, Path.Combine(Path.GetTempPath(), "aave-managed-root-cache"), "fixture-ffmpeg", "fixture-ffprobe");
            Check(lookup.Count == 0 && player.frame == 3 && player.time == .3, "new pass re-imported stale original-context cache or re-based native playback");
        }, player => Check(player.Playing && Owner._currentContext as string == "editor" && Owner._basenameCache.Count == 1,
            "outer release did not preserve the original context and native playback")));
        Test("same-context and no-context roots remain harmless during active capture", () => WithPlayer(true, false, player =>
        {
            Owner.OnNewRootSeen(@"fixture\data\projects\same\bgs");
            Owner.OnNewRootSeen(@"fixture\saves\");
            Owner.OnNewRootSeen(""); Owner.OnNewRootSeen(null!);
            ControlledBackgroundVideoAdapter.Pump(0);
            Check(player.frame == 3 && player.time == .3 && Owner.RootContexts.Count == 0, "no-op root notification changed captured native state");
        }));
        Test("a real active-pass context change is latched even when the original caller swallows it", () => WithPlayer(true, false, player =>
        {
            Owner.OnNewRootSeen(@"fixture\data\saves\new\bgs");
            ExpectFailure(() => ControlledBackgroundVideoAdapter.Pump(0));
            Check(player.Prepared && player.frame == 3 && player.time == .3 && Owner.RootContexts.Count == 0,
                "active context rejection destroyed original decoder state");
        }));
        Test("a rejected active root change cannot re-import stale original cache on the next pass", () => WithPlayer(true, false, player =>
        {
            Owner.OnNewRootSeen(@"fixture\data\saves\new\bgs");
            ExpectFailure(() => ControlledBackgroundVideoAdapter.Pump(0));
            ControlledBackgroundVideoAdapter.EndExport();
            ControlledBackgroundVideoAdapter.BeginExport(() => 0, 30, Path.Combine(Path.GetTempPath(), "aave-managed-root-retry"), "fixture-ffmpeg", "fixture-ffprobe");
            Check(((IDictionary)Get("ResolutionCache")!).Count == 0 && Owner._basenameCache.Count == 1 && player.frame == 3 && player.time == .3,
                "retry re-imported a stale original-context cache after an active root rejection");
        }));
        Test("unowned restoration root notifications replay original effects in order after detachment", () => WithPlayer(false, true,
            _ => ControlledBackgroundVideoAdapter.EndExport(), player =>
        {
            Owner.OnNewRootSeen(@"fixture\data\saves\new\bgs");
            Owner.OnNewRootSeen(@"fixture\data\projects\original\bgs");
            Check(Owner.RootContexts.Count == 0 && Get("snapshot") != null && !ControlledBackgroundVideoAdapter.IsReady,
                "outside root request ran before restoration detached its snapshot");
            player.CompletePreparation();
            Check(Owner.RootContexts.SequenceEqual(new[] { "runtime", "editor" }) && !player.Playing && Owner._basenameCache.Count == 0
                && Get("snapshot") == null && ControlledBackgroundVideoAdapter.IsReady,
                "queued original root effects were lost, reordered, or executed under a retained snapshot");
        }));
        Test("unowned root queue overflow produces an explicit retained restoration fault", () => WithPlayer(false, true,
            _ => ControlledBackgroundVideoAdapter.EndExport(), player =>
        {
            for (int i = 0; i < 33; i++) Owner.OnNewRootSeen(i % 2 == 0 ? @"fixture\data\saves\new\bgs" : @"fixture\data\projects\original\bgs");
            bool rejected = false;
            try { Snapshot.GetMethod("PollRestoration", Flags)!.Invoke(Get("snapshot"), null); }
            catch (TargetInvocationException error) when (error.GetBaseException() is ExportException { Code: "background_root_overflow" }) { rejected = true; }
            Check(rejected && Owner.RootContexts.Count == 0 && Get("snapshot") != null && !ControlledBackgroundVideoAdapter.IsReady,
                "root queue overflow silently dropped notifications or advertised completed restoration");
        }, allowPending: true));
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "preservation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Results,
            Scope = "Real production adapter source with an explicitly managed Unity boundary fixture; not native decoder acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Artifacts: " + directory); Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static void WithPlayer(bool playing, bool awaiting, Action<VideoPlayer> owned, Action<VideoPlayer>? afterRelease = null,
        Action<VideoPlayer>? beforeCapture = null, bool expectedTakeoverFailure = false, bool allowPending = false)
    {
        using var player = new VideoPlayer { Playing = playing, Paused = !playing && !awaiting, Prepared = !awaiting,
            time = awaiting ? 0 : .3, frame = awaiting ? -1 : 3 };
        beforeCapture?.Invoke(player);
        player.prepareCompleted = (Action<VideoPlayer>)(_ => { });
        player.seekCompleted = (Action<VideoPlayer>)(_ => { });
        player.errorReceived = (Action<VideoPlayer, string>)((_, _) => { });
        Owner._vp = player; Owner._awaitFirstFrame = awaiting; Owner._currentContext = "editor";
        Owner.RootContexts.Clear();
        Owner._basenameCache.Clear(); Owner._basenameCache.Add("original", "fixture-video");
        var harmony = new Harmony("aave.test.prepared-preservation");
        ControlledBackgroundVideoAdapter.Initialize(harmony, _ => { });
        Set("overlayType", typeof(Owner));
        var rootTarget = typeof(Owner).GetMethod(nameof(Owner.OnNewRootSeen), Flags)!;
        Adapter.GetField("onNewRootSeen", Flags)?.SetValue(null, rootTarget);
        Adapter.GetField("getContext", Flags)?.SetValue(null, typeof(Owner).GetMethod("GetContext", Flags));
        var rootPrefix = Adapter.GetMethod("BeforeRootSeen", Flags);
        if (rootPrefix != null) harmony.Patch(rootTarget, prefix: new HarmonyMethod(rootPrefix));
        harmony.Patch(Adapter.GetMethod("ValidatePatches", Flags)!, prefix: new HarmonyMethod(typeof(Cases), nameof(SkipNativePatchValidation)));
        Set("inspected", typeof(Cases).Assembly); Set("ownerThread", Environment.CurrentManagedThreadId); Set("frozenSettings", "absent");
        Set("fault", null); Set("lastFrame", -1L); Set("readFrame", (Func<long>)(() => 0));
        RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization();
        var lease = RenderControlV1.Acquire();
        object snapshot = Snapshot.GetMethod("Capture", Flags)!.Invoke(null, new object[] { typeof(Owner), typeof(Owner).GetField("_vp")! })!;
        Set("snapshot", snapshot);
        try
        {
            bool failedAsExpected = false;
            try { Snapshot.GetMethod("Pause", Flags)!.Invoke(snapshot, null); }
            catch (TargetInvocationException error) when (expectedTakeoverFailure && error.GetBaseException().Message == "fixture pause completion failure")
            { failedAsExpected = true; }
            Check(failedAsExpected == expectedTakeoverFailure, "takeover failure injection did not reach the production boundary");
            Set("<Active>k__BackingField", !failedAsExpected);
            owned(player); ControlledBackgroundVideoAdapter.EndExport(); lease.Dispose(); afterRelease?.Invoke(player);
            if (!allowPending) Check(player.prepareCompleted?.SubscriberCount == 1 && player.seekCompleted?.SubscriberCount == 1 && player.errorReceived?.SubscriberCount == 1,
                "production restoration removed an original subscriber or leaked one of its observers");
        }
        finally
        {
            Set("snapshot", null); Set("<Active>k__BackingField", false); Set("restorationFailed", false);
            lease.Dispose(); ControlledBackgroundVideoAdapter.Shutdown(); harmony.UnpatchSelf(); RenderControlV1.BeginShutdown(); Owner._vp = null;
        }
    }
    private static void ExpectFailure(Action action)
    { try { action(); } catch (Exception error) when (error is ExportException or InvalidOperationException) { return; } throw new Exception("native state drift was accepted"); }
    private static object? Get(string name) => Adapter.GetField(name, Flags)!.GetValue(null);
    private static void Set(string name, object? value) => Adapter.GetField(name, Flags)!.SetValue(null, value);
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static bool SkipNativePatchValidation() => false;
    private static void Test(string name, Action run)
    {
        try { run(); passed++; Results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    private static class Owner
    {
        public static VideoPlayer? _vp;
        public static bool _videoDirResolved = true;
        public static string _videoDirCache = Path.GetTempPath();
        public static bool _awaitFirstFrame;
        public static readonly IDictionary _basenameCache = new Dictionary<string, string>();
        public static readonly List<string> RootContexts = new();
        public static object? _currentContext = null, _currentImage = null, _currentImageLayerId = null, _currentVideoName = null, _officialTex = null,
            _reassertCount = null, _healthStage = null, _altPathTried = null, _currentUrl = null, _altPath = null, _currentVideoPath = null,
            _loggedFirstFrame = null, _firstFrameTimedOut = null;
        public static float _switchTime = 0, _awaitFirstFrameSince = 0;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void OnNewRootSeen(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            string? context = GetContext(root.TrimEnd('\\', '/'));
            if (context == null || Equals(context, _currentContext)) return;
            RootContexts.Add(context);
            // Model the pinned original callback's observed generated-bool
            // path: even a paused native decoder enters Stop on AA 101.
            _vp?.Stop(); _currentContext = context; _currentVideoName = null; _currentImage = null; _currentImageLayerId = 0;
            _basenameCache.Clear(); _officialTex = null; _healthStage = 0; _altPathTried = false; _currentUrl = null; _altPath = null; _reassertCount = 0;
        }
        private static string? GetContext(string path)
        {
            string value = path.Replace('/', '\\');
            if (value.Contains("\\projects\\", StringComparison.OrdinalIgnoreCase)) return "editor";
            if (value.Contains("\\saves\\", StringComparison.OrdinalIgnoreCase)) return "runtime";
            return null;
        }
    }
}
