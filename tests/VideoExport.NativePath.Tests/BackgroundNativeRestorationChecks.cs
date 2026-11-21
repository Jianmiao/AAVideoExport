using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Separate opt-in native boundary fixture. It calls the original Flandre
// callbacks and production adapter lifecycle; it does not replace decoder,
// preparation, seek, playback, or restoration behavior. Real Host cancellation
// and repeated movie acceptance are covered by StoryExportChecks.
public sealed class BackgroundNativeRestorationChecks : MonoBehaviour
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<object> events = new(), cases = new();
    private readonly List<string> errors = new();
    private string[] modes = Array.Empty<string>();
    private string videoPath = "", thumbnailPath = "", sourceHash = "", thumbnailHash = "", rootChangePath = "";
    private Type? overlay, adapter;
    private MethodInfo? originalSet, originalCheckLayer, originalRootSeen;
    private object? retainedSnapshot;
    private GameObject? fixture;
    private UITexture? image;
    private Texture2D? still;
    private BackgroundPresenter.Layer? layer;
    private BackgroundPresenter.Content? content, staticContent;
    private VideoPlayer? player;
    private VideoPlayer.EventHandler? preparedHandler, seekHandler;
    private VideoPlayer.ErrorEventHandler? errorHandler;
    private StoryRenderControlEvidence? renderEvidence;
    private IDisposable? lease;
    private Baseline? before, retained;
    private Dictionary<string, string> scalarSnapshot = new(), cacheSnapshot = new();
    private int stage, caseIndex, stableFrames, quarantineFrames, thread;
    private long captureFrame, seekFrame = -1;
    private double stageStarted, releaseStarted, savedTime, seekTime, sourceFrameDuration, nativeDuration, failureCleanupStarted;
    private double nextStatePoll;
    private bool done, releaseRequested, sawPreparedAfterRelease, sawSeekSignalAfterRelease, sawSeekAfterRelease, sawPlayingProgress;
    private bool preservedPreparedFrame, continuityAtRelease, rootGapReentered;
    private bool preparedSubscribed, seekSubscribed, errorSubscribed;
    private long callbackPlayerPointer;
    private long nativeTargetPointer;
    private string retainedFrameRgbaSha256 = "", restoredFrameRgbaSha256 = "";
    private bool originalImageRebound;

    public BackgroundNativeRestorationChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (done) return;
        try
        {
            if (stage == 90) { ObserveFailureCleanup(); return; }
            if (player != null && wall.Elapsed.TotalSeconds >= nextStatePoll)
            { nextStatePoll = wall.Elapsed.TotalSeconds + .25; Record("native-state-poll"); }
            if (wall.Elapsed.TotalSeconds > 100) throw new TimeoutException("Native background restoration fixture exceeded 100 seconds.");
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
            switch (stage)
            {
                case 0:
                    if (!RenderControlV1.IsReady || RenderControlV1.IsRenderingOwned || SceneManager.GetActiveScene().name != "CatalogScene") return;
                    Initialize(); BeginCase(); break;
                case 1: WaitForOriginalPlayback(); break;
                case 2: WaitForStablePausedBaseline(); break;
                case 3: ObserveQuarantine(); break;
                case 4: ReleaseOuterLease(); break;
                case 5: ObserveRestoration(); break;
                case 6:
                    if (RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Completed case still owns rendering.");
                    if (++stableFrames >= 4) BeginCase();
                    break;
            }
        }
        catch (Exception error)
        {
            errors.Add(error.GetBaseException().ToString());
            BeginFailureCleanup();
        }
    }

    [HideFromIl2Cpp]
    private void Initialize()
    {
        thread = Environment.CurrentManagedThreadId;
        string root = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_FIXTURE_WORKSPACE") ?? "";
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("AA_VIDEOEXPORT_FIXTURE_WORKSPACE must name the explicit isolated authored fixture.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "aave-fixture-workspace.json")));
        Require(marker.RootElement.GetProperty("Owner").GetString() == "AAVE acceptance", "The independent fixture owner marker is missing.");
        var video = marker.RootElement.GetProperty("Videos").EnumerateArray().Single(item => item.GetProperty("Name").GetString() == "controlled-bg");
        videoPath = Path.GetFullPath(video.GetProperty("Video").GetString()!);
        thumbnailPath = Path.GetFullPath(video.GetProperty("Thumbnail").GetString()!);
        sourceHash = video.GetProperty("VideoSha256").GetString()!;
        thumbnailHash = video.GetProperty("ThumbnailSha256").GetString()!;
        Require(Hash(videoPath) == sourceHash && Hash(thumbnailPath) == thumbnailHash, "The video/thumbnail differs from its independent input marker.");
        string[] rate = video.GetProperty("Probe").GetProperty("r_frame_rate").GetString()!.Split('/');
        sourceFrameDuration = double.Parse(rate[1], System.Globalization.CultureInfo.InvariantCulture)
            / double.Parse(rate[0], System.Globalization.CultureInfo.InvariantCulture);
        nativeDuration = int.Parse(video.GetProperty("Probe").GetProperty("nb_read_frames").ToString()) * sourceFrameDuration;
        Require(sourceFrameDuration > 0 && nativeDuration > .25, "The source must support a genuine nonzero playhead beyond 0.25 seconds.");
        string mode = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_RESTORE_MODE") ?? "both";
        modes = mode switch { "both" => new[] { "paused", "playing" }, "paused" => new[] { "paused" }, "playing" => new[] { "playing" },
            _ => throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_BACKGROUND_RESTORE_MODE must be paused, playing, or both.") };
        var mod = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "FlandreScarletBackground");
        overlay = mod.GetType("FlandreScarletBackground.Background.FLVideoOverlay", true)!;
        adapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.ControlledBackgroundVideoAdapter", true)!;
        InvokeAdapter("TryInstall");
        Require(Read<bool>(adapter, "IsReady") && !Read<bool>(adapter, "Active"), "The exact-build production video adapter is not idle and ready.");
        originalSet = overlay.GetMethod("OnLayerSet", Flags)!;
        originalCheckLayer = overlay.GetMethod("CheckLayer", Flags) ?? throw new MissingMethodException(overlay.FullName, "CheckLayer");
        originalRootSeen = overlay.GetMethod("OnNewRootSeen", Flags) ?? throw new MissingMethodException(overlay.FullName, "OnNewRootSeen");
        var contextMethod = overlay.GetMethod("GetContext", Flags) ?? throw new MissingMethodException(overlay.FullName, "GetContext");
        using var recipe = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "controlled-story-recipe.json")));
        string? initialContext = Member(overlay, "_currentContext") as string;
        rootChangePath = new[] { recipe.RootElement.GetProperty("Project").GetString()!, recipe.RootElement.GetProperty("Story").GetString()! }
            .Where(File.Exists).FirstOrDefault(path => contextMethod.Invoke(null, new object[] { path.TrimEnd('\\', '/') }) is string context
                && context.Length != 0 && context != initialContext)
            ?? throw new InvalidOperationException("The declared fixture lacks a real different editor/runtime context for the root callback.");
        var existing = Member(overlay, "_vp") as VideoPlayer;
        Require(existing == null || !Playing(existing) && !Prepared(existing), "Refusing to replace unrelated active or prepared native background playback.");
        fixture = new GameObject("AAVE native nonzero restoration fixture");
        fixture.SetActive(false);
        image = fixture.AddComponent<UITexture>();
        still = new Texture2D(128, 64, TextureFormat.RGBA32, false);
        image.mainTexture = still;
        layer = new BackgroundPresenter.Layer(image, 0, null);
        var resource = new BackgroundResource(0, "controlled-bg", ScenarioBGType.Image,
            ScenarioResourceManager.ResourceType.LocalCustom, path: Path.GetFileName(thumbnailPath), root: Path.GetDirectoryName(thumbnailPath)!, spine: null);
        Require(SamePath(resource.FullPath, thumbnailPath), "Native resource resolution does not point to the explicit fixture thumbnail.");
        content = new BackgroundPresenter.Content { Resource = resource, Image = still };
        var staticResource = new BackgroundResource(0, "aave-restoration-static", ScenarioBGType.Image,
            ScenarioResourceManager.ResourceType.LocalCustom, path: "aave-restoration-static.png", root: Path.GetDirectoryName(thumbnailPath)!, spine: null);
        staticContent = new BackgroundPresenter.Content { Resource = staticResource, Image = still };
        renderEvidence = new StoryRenderControlEvidence("Actual Azurite consumer with production background-adapter cancellation/release under a public v1 test lease. This separate fixture does not claim a completed Host export.");
        Record("fixture-initialized");
    }

    [HideFromIl2Cpp]
    private void BeginCase()
    {
        Require(lease == null && !RenderControlV1.IsRenderingOwned && !Read<bool>(adapter!, "Active"), "A prior case retains ownership.");
        releaseRequested = sawPreparedAfterRelease = sawSeekSignalAfterRelease = sawSeekAfterRelease = sawPlayingProgress = false;
        preservedPreparedFrame = continuityAtRelease = rootGapReentered = false;
        retainedSnapshot = null;
        nativeTargetPointer = 0; retainedFrameRgbaSha256 = restoredFrameRgbaSha256 = ""; originalImageRebound = false;
        stableFrames = quarantineFrames = 0; captureFrame = 0; seekFrame = -1;
        // Switch through the original static callback so the second case cannot
        // take Flandre's same-resource rebind shortcut around native Play.
        originalSet!.Invoke(null, new object[] { layer!, staticContent! });
        image!.mainTexture = still;
        originalSet.Invoke(null, new object[] { layer!, content! });
        player = Member(overlay!, "_vp") as VideoPlayer ?? throw new InvalidOperationException("Original Flandre callback did not create its native VideoPlayer.");
        if (preparedHandler == null)
        {
            preparedHandler = (Action<VideoPlayer>)OnPrepared;
            seekHandler = (Action<VideoPlayer>)OnSeek;
            errorHandler = (Action<VideoPlayer, string>)OnPlayerError;
            // AA 101's add_/remove_ accessors are unstripping-failure stubs.
            // These generated properties access the real native callback
            // fields; typed +/- combines/removes only our observer delegates.
            // Original native Invoke*Callback_Internal remains the dispatcher.
            player.prepareCompleted += preparedHandler; preparedSubscribed = true;
            player.seekCompleted += seekHandler; seekSubscribed = true;
            player.errorReceived += errorHandler; errorSubscribed = true;
            callbackPlayerPointer = player.Pointer.ToInt64();
        }
        else Require(player.Pointer.ToInt64() == callbackPlayerPointer, "Original shared VideoPlayer changed between restoration cases.");
        stage = 1; stageStarted = wall.Elapsed.TotalSeconds;
        Record("original-background-callback");
    }

    [HideFromIl2Cpp]
    private void WaitForOriginalPlayback()
    {
        Require(wall.Elapsed.TotalSeconds - stageStarted < 15, "Original native decoder did not reach a nonzero prepared playhead within 15 seconds.");
        if (!Prepared(player!) || !Playing(player!) || player!.frame <= 0 || player.time <= .25) return;
        originalCheckLayer!.Invoke(null, new object[] { layer!, true });
        Require(!Read<bool>(overlay!, "_awaitFirstFrame"), "Original Flandre first-frame readiness did not settle.");
        Require(SamePath(Read<string>(overlay!, "_currentVideoPath"), videoPath), "Original decoder selected a source other than the fixture video.");
        if (modes[caseIndex] == "paused")
        {
            player.Pause(); stableFrames = 0; stage = 2; stageStarted = wall.Elapsed.TotalSeconds;
            Record("pause-established-original-playback");
        }
        else BeginOwnership();
    }

    [HideFromIl2Cpp]
    private void WaitForStablePausedBaseline()
    {
        Require(wall.Elapsed.TotalSeconds - stageStarted < 5, "Native paused baseline did not settle.");
        if (!Prepared(player!) || Playing(player!) || !Paused(player!) || player!.time <= .25 || player.frame <= 0) { stableFrames = 0; return; }
        if (++stableFrames >= 3) BeginOwnership();
    }

    [HideFromIl2Cpp]
    private void BeginOwnership()
    {
        before = Baseline.Capture(player!);
        double beforeOwnership = wall.Elapsed.TotalSeconds;
        var initialTarget = Member(overlay!, "_rt") as RenderTexture;
        Require(initialTarget != null && player!.targetTexture != null && image!.mainTexture != null
            && initialTarget.Pointer == player.targetTexture.Pointer && image.mainTexture.Pointer == initialTarget.Pointer,
            "The original Flandre callback has not bound its actual native video target before takeover.");
        nativeTargetPointer = initialTarget!.Pointer.ToInt64();
        Require(before.Prepared && before.Time > .25 && before.Frame > 0 && before.Active
            && before.Playing == (modes[caseIndex] == "playing"), "Actual native baseline does not match the requested nonzero case.");
        // Derive this expectation from independent native state, not a flag
        // returned by the implementation under test. The original callback
        // has already displayed a real frame and has no queued first frame.
        preservedPreparedFrame = before.Prepared && before.Frame >= 0 && before.Time > 0
            && !Read<bool>(overlay!, "_awaitFirstFrame");
        scalarSnapshot = ReadScalars(); cacheSnapshot = ReadCache();
        renderEvidence!.BeginAttempt(caseIndex + 1);
        Record("before-production-snapshot");
        lease = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", Flags)!.Invoke(null, null)!;
        var provider = typeof(AAVideoExport.Plugin.Plugin);
        string ffmpeg = (string)provider.GetField("FfmpegPath", Flags)!.GetValue(null)!;
        string ffprobe = (string)provider.GetField("FfprobePath", Flags)!.GetValue(null)!;
        InvokeAdapter("BeginExport", (Func<long>)(() => captureFrame), 30,
            Path.Combine(Plugin.DiagnosticsRoot, "background-restoration-cache-" + modes[caseIndex]), ffmpeg, ffprobe);
        retained = Baseline.Capture(player!);
        if (preservedPreparedFrame)
        {
            Require(retained.Prepared && !retained.Playing && retained.Paused && retained.Active == before.Active
                && retained.Pointer == before.Pointer && retained.Url == before.Url
                && retained.Looping == before.Looping && retained.Speed == before.Speed
                && retained.Time > .25 && retained.Frame > 0,
                "The independently observed post-Pause native frame is not a valid nonzero retained state.");
            double advance = retained.Time - before.Time;
            if (before.Looping && advance < -1e-9) advance += nativeDuration;
            Require(before.Playing
                    ? advance >= -1e-9 && advance <= (wall.Elapsed.TotalSeconds - beforeOwnership) * before.Speed + sourceFrameDuration + .0001
                    : retained.Frame == before.Frame && Math.Abs(advance) < 1e-9,
                "Acquisition changed the native playhead beyond ordinary pre-Pause playback.");
        }
        if (preservedPreparedFrame && modes[caseIndex] == "paused")
            retainedFrameRgbaSha256 = ReadNativeTargetHash();
        var snapshot = Member(adapter!, "snapshot") ?? throw new InvalidOperationException("Production adapter did not retain its native snapshot.");
        retainedSnapshot = snapshot;
        savedTime = Read<double>(snapshot, "playerTime");
        Require(Read<bool>(snapshot, "wasPrepared") && Read<bool>(snapshot, "wasPlaying") == before.Playing
            && savedTime > .25 && (preservedPreparedFrame
                ? Math.Abs(savedTime - retained.Time) < 1e-9 && Read<long>(snapshot, "playerFrame") == retained.Frame
                : Math.Abs(savedTime - before.Time) <= sourceFrameDuration + .0001),
            "Production snapshot did not retain the independently observed nonzero native state.");
        originalSet!.Invoke(null, new object[] { layer!, content! });
        InvokeAdapter("Pump", captureFrame);
        Require(image!.mainTexture != null && image.mainTexture.name.StartsWith("AAVE controlled background", StringComparison.Ordinal),
            "Production frame-controlled background did not take over the fixture layer.");
        stage = 3; stageStarted = wall.Elapsed.TotalSeconds;
        Record("owned-native-quarantine");
    }

    [HideFromIl2Cpp]
    private void ObserveQuarantine()
    {
        renderEvidence!.ObserveOwned();
        RequireQuarantinedNative();
        if (++quarantineFrames <= 12 || wall.Elapsed.TotalSeconds - stageStarted < Math.Max(.5, sourceFrameDuration * 4))
        {
            InvokeAdapter("Pump", ++captureFrame); return;
        }
        InvokeAdapter("EndExport");
        Require(!Read<bool>(adapter!, "Active") && Member(adapter!, "snapshot") != null,
            "EndExport restored the native decoder before the outer lease released.");
        RequireQuarantinedNative();
        stage = 4; stageStarted = wall.Elapsed.TotalSeconds;
        Record("cancelled-pass-awaiting-outer-release");
    }

    [HideFromIl2Cpp]
    private void ReleaseOuterLease()
    {
        RequireQuarantinedNative();
        if (!rootGapReentered)
        {
            // Invoke the actual pinned overlay callback in the gap between
            // controlled passes. The surrounding original import operation is
            // deliberately not simulated or mutated by this boundary fixture.
            originalRootSeen!.Invoke(null, new object[] { rootChangePath });
            RequireQuarantinedNative();
            Record("actual-root-callback-between-owned-passes");
            captureFrame = 0;
            var provider = typeof(AAVideoExport.Plugin.Plugin);
            InvokeAdapter("BeginExport", (Func<long>)(() => captureFrame), 30,
                Path.Combine(Plugin.DiagnosticsRoot, "background-restoration-cache-" + modes[caseIndex]),
                (string)provider.GetField("FfmpegPath", Flags)!.GetValue(null)!,
                (string)provider.GetField("FfprobePath", Flags)!.GetValue(null)!);
            Require(ReferenceEquals(retainedSnapshot, Member(adapter!, "snapshot")), "A pass restart recaptured the original native snapshot.");
            originalSet!.Invoke(null, new object[] { layer!, content! });
            InvokeAdapter("Pump", captureFrame);
            RequireQuarantinedNative();
            rootGapReentered = true; quarantineFrames = 0; stage = 3; stageStarted = wall.Elapsed.TotalSeconds;
            Record("controlled-pass-restarted-after-root-callback");
            return;
        }
        releaseRequested = true; releaseStarted = wall.Elapsed.TotalSeconds;
        var releasing = lease!; lease = null; releasing.Dispose();
        if (preservedPreparedFrame)
        {
            Require(Prepared(player!) && player!.frame == retained!.Frame && Math.Abs(player.time - savedTime) < 1e-9,
                "The independently retained nonzero frame was reset while releasing the outer lease.");
            continuityAtRelease = true; seekFrame = player!.frame; seekTime = player.time;
        }
        Require(Member(adapter!, "snapshot") == null, "Original image binding must wait for native snapshot restoration.");
        // The fixture has no native BackgroundPresenter.Update. Exercise the
        // original CheckLayer callback explicitly; never assign the texture.
        originalCheckLayer!.Invoke(null, new object[] { layer!, true });
        var restoredTarget = Member(overlay!, "_rt") as RenderTexture;
        Require(restoredTarget != null && restoredTarget.Pointer.ToInt64() == nativeTargetPointer
            && player!.targetTexture != null && player.targetTexture.Pointer == restoredTarget.Pointer
            && image!.mainTexture != null && image.mainTexture.Pointer == restoredTarget.Pointer
            && image.mainTexture.Pointer != still!.Pointer
            && !image.mainTexture.name.StartsWith("AAVE controlled background", StringComparison.Ordinal),
            "The original Flandre callback did not restore the layer's actual native video target.");
        originalImageRebound = true;
        if (preservedPreparedFrame && modes[caseIndex] == "paused")
        {
            restoredFrameRgbaSha256 = ReadNativeTargetHash();
            Require(retainedFrameRgbaSha256 == restoredFrameRgbaSha256,
                "The complete paused native RenderTexture pixels changed across takeover/restoration.");
        }
        stage = 5; stableFrames = 0;
        Record("outer-lease-released");
    }

    [HideFromIl2Cpp]
    private void ObserveRestoration()
    {
        Require(wall.Elapsed.TotalSeconds - releaseStarted < 10, "Native prepared/nonzero playhead did not restore within 10 seconds after release.");
        bool renderRestored = renderEvidence!.WaitForRestoration("cancelled");
        Require(player!.Pointer.ToInt64() == before!.Pointer && player.url == before.Url
            && player.gameObject.activeSelf == before.Active && Looping(player) == before.Looping
            && player.playbackSpeed == before.Speed, "Native player identity, source, holder, loop or speed was not restored.");
        Require(ReadScalars().OrderBy(pair => pair.Key).SequenceEqual(scalarSnapshot.OrderBy(pair => pair.Key))
            && ReadCache().OrderBy(pair => pair.Key).SequenceEqual(cacheSnapshot.OrderBy(pair => pair.Key)),
            "Original Flandre source/context/cache state was not restored.");
        if (!renderRestored || !Prepared(player)) return;
        if (preservedPreparedFrame)
            Require(continuityAtRelease, "A prepared restoration lacks independent position continuity at lease release.");
        else if (!sawPreparedAfterRelease) return;
        // seekCompleted acknowledges the request before the decoded frame is
        // published. Keep the actual signal, then independently require the
        // saved nonzero frame/time to become visible; zero is never accepted.
        if (!preservedPreparedFrame && sawSeekSignalAfterRelease && !sawSeekAfterRelease && player.time > .25
            && player.frame == before.Frame && Math.Abs(player.time - savedTime) <= sourceFrameDuration + .0001)
        {
            sawSeekAfterRelease = true; seekFrame = player.frame; seekTime = player.time;
            Record("seek-position-published");
        }
        if (modes[caseIndex] == "paused")
        {
            if (Playing(player) || !Paused(player) || !preservedPreparedFrame && !sawSeekAfterRelease
                || player.time <= .25 || player.frame != (preservedPreparedFrame ? retained!.Frame : before.Frame)
                || Math.Abs(player.time - savedTime) > sourceFrameDuration + .0001)
            { stableFrames = 0; return; }
            if (++stableFrames < 4) return;
        }
        else
        {
            // A fresh playback from zero cannot satisfy either proof: there
            // must be uninterrupted saved-frame continuity, or a real seek
            // followed by publication of that exact nonzero target.
            if ((!preservedPreparedFrame && !sawSeekAfterRelease) || !Playing(player) || Paused(player)) return;
            if (player.frame != seekFrame || Math.Abs(player.time - seekTime) > sourceFrameDuration / 2)
                sawPlayingProgress = true;
            if (!sawPlayingProgress) return;
        }
        Require(Member(adapter!, "snapshot") == null && !Read<bool>(adapter!, "Active") && !RenderControlV1.IsRenderingOwned,
            "Successful native restoration left adapter state or ownership pending.");
        cases.Add(new { Mode = modes[caseIndex], Passed = true, Before = before, Retained = retained, SnapshotTime = savedTime,
            Strategy = preservedPreparedFrame ? "continuous-prepared-frame" : "prepared-seek-publication", continuityAtRelease, rootGapReentered, rootChangePath,
            originalImageRebound, nativeTargetPointer, retainedFrameRgbaSha256, restoredFrameRgbaSha256,
            Restored = Baseline.Capture(player), QuarantineUpdates = quarantineFrames, CapturedFrames = captureFrame,
            sawPreparedAfterRelease, sawSeekSignalAfterRelease, sawSeekAfterRelease, sawPlayingProgress, RestorationSeconds = wall.Elapsed.TotalSeconds - releaseStarted });
        Record("native-nonzero-restoration-verified");
        caseIndex++;
        if (caseIndex == modes.Length) Finish(true);
        else { stableFrames = 0; stage = 6; }
    }

    [HideFromIl2Cpp]
    private void OnPrepared(VideoPlayer value) => ObserveCallback("prepare-completed", () =>
    {
        if (releaseRequested && value.Pointer == player?.Pointer && Prepared(value)) sawPreparedAfterRelease = true;
    });

    [HideFromIl2Cpp]
    private void RequireQuarantinedNative()
    {
        Require(RenderControlV1.IsRenderingOwned && !Playing(player!), "Native decoder advanced autonomously under render ownership.");
        Require(player!.Pointer.ToInt64() == before!.Pointer && player.url == before.Url,
            "Native decoder identity/source changed during quarantine.");
        if (preservedPreparedFrame)
        {
            Require(Prepared(player) && Paused(player) && player.gameObject.activeSelf == before.Active
                && player.frame == retained!.Frame && Math.Abs(player.time - retained.Time) < 1e-9
                && Looping(player) == before.Looping && player.playbackSpeed == before.Speed,
                "An independently prepared frame/clock/source did not remain unchanged throughout ownership.");
        }
        else Require(!player.gameObject.activeSelf && !Prepared(player), "Queued native playback was not stopped and deactivated.");
    }
    [HideFromIl2Cpp]
    private void OnSeek(VideoPlayer value) => ObserveCallback("seek-completed", () =>
    {
        if (!releaseRequested || value.Pointer != player?.Pointer || !sawPreparedAfterRelease || !Prepared(value)) return;
        sawSeekSignalAfterRelease = true;
    });
    [HideFromIl2Cpp]
    private void OnPlayerError(VideoPlayer value, string message) => ObserveCallback("native-decoder-error", () =>
    { if (value.Pointer == player?.Pointer) errors.Add(message); });
    [HideFromIl2Cpp]
    private void ObserveCallback(string kind, Action observation)
    {
        try { Require(Environment.CurrentManagedThreadId == thread, "Native callback arrived off the observed main thread."); observation(); Record(kind); }
        catch (Exception error) { errors.Add(error.GetBaseException().Message); }
    }

    [HideFromIl2Cpp]
    private void BeginFailureCleanup()
    {
        if (stage == 90) { Finish(false); return; }
        stage = 90; failureCleanupStarted = wall.Elapsed.TotalSeconds;
        try { if (adapter != null && Read<bool>(adapter, "Active")) InvokeAdapter("EndExport"); }
        catch (Exception error) { errors.Add("adapter-failure-cleanup: " + error.GetBaseException().Message); }
        try { var releasing = lease; lease = null; releasing?.Dispose(); }
        catch (Exception error) { errors.Add("lease-failure-cleanup: " + error.GetBaseException().Message); }
    }

    [HideFromIl2Cpp]
    private void ObserveFailureCleanup()
    {
        if (wall.Elapsed.TotalSeconds - failureCleanupStarted >= 10)
        { errors.Add("Failure cleanup exceeded 10 seconds; production state was not overridden."); Finish(false); return; }
        if (RenderControlV1.IsRenderingOwned || adapter != null && Read<bool>(adapter, "Active")) return;
        try { if (renderEvidence?.WaitForFailureRestoration() == false) return; }
        catch (Exception error) { errors.Add("failure-azurite-restoration: " + error.GetBaseException().Message); }
        Finish(false);
    }

    [HideFromIl2Cpp]
    private void Finish(bool success)
    {
        if (done) return;
        done = true;
        try { if (adapter != null && Read<bool>(adapter, "Active")) InvokeAdapter("EndExport"); }
        catch (Exception error) { errors.Add("adapter-cleanup: " + error.GetBaseException().Message); }
        try { var releasing = lease; lease = null; releasing?.Dispose(); }
        catch (Exception error) { errors.Add("lease-cleanup: " + error.GetBaseException().Message); }
        try
        {
            if (renderEvidence != null && !renderEvidence.Finish(modes.Length)) errors.Add("Actual Azurite restoration evidence is incomplete.");
            if (videoPath.Length != 0 && (Hash(videoPath) != sourceHash || Hash(thumbnailPath) != thumbnailHash)) errors.Add("Independent fixture inputs changed.");
            if (player != null)
            {
                if (preparedSubscribed) { player.prepareCompleted -= preparedHandler!; preparedSubscribed = false; }
                if (seekSubscribed) { player.seekCompleted -= seekHandler!; seekSubscribed = false; }
                if (errorSubscribed) { player.errorReceived -= errorHandler!; errorSubscribed = false; }
            }
            if (!RenderControlV1.IsRenderingOwned && originalSet != null && layer != null)
                originalSet.Invoke(null, new object[] { layer, staticContent! });
            // Evidence is already captured. Stop only this fixture's original
            // native player so a successful paused case leaves no prepared
            // decoder referencing the temporary UI that is about to go away.
            if (!RenderControlV1.IsRenderingOwned && player != null && before != null
                && player.Pointer.ToInt64() == before.Pointer && player.url == before.Url) player.Stop();
            if (fixture != null) Object.Destroy(fixture);
            if (still != null) Object.Destroy(still);
        }
        catch (Exception error) { errors.Add("fixture-cleanup: " + error.GetBaseException().Message); }
        bool passed = success && errors.Count == 0 && cases.Count == modes.Length;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "background-native-restoration.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, Modes = modes, Video = videoPath, SourceSha256 = sourceHash, SourceFrameDuration = sourceFrameDuration,
            SourceDuration = nativeDuration, Cases = cases, Events = events, Errors = errors,
            Scope = "Original Flandre native decoder and actual nonzero snapshot/quarantine/asynchronous restoration. Separate adapter boundary fixture; no decoder, seek, completion or restoration implementation is substituted."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report("PATH-CHECK " + (passed ? "PASS" : "FAIL") + " background-native-nonzero-restoration; " + string.Join("; ", errors));
        Plugin.Report($"PATH-CHECK RESULT {(passed ? cases.Count : 0)} passed; {(passed ? 0 : 1)} failed");
    }

    [HideFromIl2Cpp]
    private Dictionary<string, string> ReadScalars() => new[] { "_currentContext", "_currentImage", "_currentImageLayerId", "_currentVideoName",
        "_officialTex", "_currentUrl", "_currentVideoPath", "_awaitFirstFrame", "_loggedFirstFrame", "_firstFrameTimedOut" }
        .ToDictionary(name => name, name => Identity(Member(overlay!, name)), StringComparer.Ordinal);
    [HideFromIl2Cpp]
    private Dictionary<string, string> ReadCache()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Member(overlay!, "_basenameCache") is not IDictionary cache) throw new InvalidOperationException("Original Flandre basename cache is unavailable.");
        foreach (DictionaryEntry pair in cache) result.Add((string)pair.Key, (string)pair.Value!);
        return result;
    }
    [HideFromIl2Cpp]
    private void Record(string kind) => events.Add(new { kind, Case = caseIndex + 1,
        Mode = caseIndex < modes.Length ? modes[caseIndex] : "finished", stage, captureFrame, releaseRequested,
        Seconds = wall.Elapsed.TotalSeconds, Thread = Environment.CurrentManagedThreadId,
        Owned = RenderControlV1.IsRenderingOwned, Native = player == null ? null : Baseline.Capture(player),
        RawNativeBooleanDiagnostic = player == null ? null : NativeVideoBooleanProbe.Capture(player) });
    [HideFromIl2Cpp]
    private void InvokeAdapter(string name, params object[] args) => adapter!.GetMethod(name, Flags)!.Invoke(null, args.Length == 0 ? null : args);
    [HideFromIl2Cpp]
    private static object? Member(object target, string name)
    {
        Type type = target as Type ?? target.GetType(); object? instance = target is Type ? null : target;
        var property = type.GetProperty(name, Flags);
        return property != null ? property.GetValue(instance) : (type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name)).GetValue(instance);
    }
    [HideFromIl2Cpp]
    private static T Read<T>(object target, string name) => (T)Member(target, name)!;
    [HideFromIl2Cpp]
    private static string Identity(object? value) => value switch { null => "null", Il2CppObjectBase native => "native:" + native.Pointer.ToInt64(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null" };
    [HideFromIl2Cpp]
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [HideFromIl2Cpp]
    private static string Hash(string path) { using var file = File.OpenRead(path); using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(file)); }
    [HideFromIl2Cpp]
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    [HideFromIl2Cpp]
    private static bool Prepared(VideoPlayer value) => NativeVideoBooleanProbe.ReadState(value, "isPrepared");
    [HideFromIl2Cpp]
    private static bool Playing(VideoPlayer value) => NativeVideoBooleanProbe.ReadState(value, "isPlaying");
    [HideFromIl2Cpp]
    private static bool Paused(VideoPlayer value) => NativeVideoBooleanProbe.ReadState(value, "isPaused");
    [HideFromIl2Cpp]
    private static bool Looping(VideoPlayer value) => NativeVideoBooleanProbe.ReadState(value, "isLooping");
    [HideFromIl2Cpp]
    private string ReadNativeTargetHash()
    {
        Require(Prepared(player!) && Paused(player!) && !Playing(player!) && player!.frame == retained!.Frame,
            "Native image evidence requires the independently frozen paused frame.");
        var target = Member(overlay!, "_rt") as RenderTexture
            ?? throw new InvalidOperationException("The original native video RenderTexture is unavailable.");
        Require(target.Pointer.ToInt64() == nativeTargetPointer && target.width > 0 && target.height > 0,
            "The original native video target identity or dimensions changed.");
        RenderTexture previous = RenderTexture.active;
        Texture2D? readback = null;
        try
        {
            readback = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            var pixels = readback.GetPixels32().ToArray();
            Require(pixels.Length == checked(target.width * target.height)
                && pixels.Any(pixel => pixel.r != 0 || pixel.g != 0 || pixel.b != 0),
                "Native image evidence is empty or did not cover the full target.");
            var hash = Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan())));
            Require(player!.frame == retained!.Frame && Math.Abs(player.time - retained.Time) < 1e-9,
                "Paused native playhead moved during full image readback.");
            return hash;
        }
        finally { RenderTexture.active = previous; if (readback != null) Object.Destroy(readback); }
    }
    private sealed record Baseline(long Pointer, string Url, double Time, long Frame, bool Prepared, bool Playing, bool Paused, bool Active, bool Looping, float Speed)
    {
        internal static Baseline Capture(VideoPlayer player) => new(player.Pointer.ToInt64(), player.url, player.time, player.frame,
            BackgroundNativeRestorationChecks.Prepared(player), BackgroundNativeRestorationChecks.Playing(player),
            BackgroundNativeRestorationChecks.Paused(player), player.gameObject.activeSelf, BackgroundNativeRestorationChecks.Looping(player), player.playbackSpeed);
    }
}
