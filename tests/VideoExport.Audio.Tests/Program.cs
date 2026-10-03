using AAVideoExport.Core;
using AAVideoExport.Plugin;
using UnityEngine;

// Compile and exercise the real capture scope. Only its Unity/AA boundary is
// stubbed; this cannot establish the actual engine's sample-count semantics.
if (args.Contains("--repro"))
{
    using var capture = NewCapture();
    AudioRenderer.SampleCount = 0;
    try
    {
        var empty = capture.RenderAudio();
        Equal(0, empty.Count);
        Equal(0, empty.Samples.Length);
        Equal(0, AudioRenderer.LastLength);
        Equal(1, AudioRenderer.RenderCalls);
        AudioRenderer.SampleCount = 800;
        var normal = capture.RenderAudio();
        Equal(1600, normal.Count);
        Check(normal.Samples[0] == 0.25f && normal.Samples[1599] == 0.5f);
        Console.WriteLine("REPRO FIXED count=0 -> Render(0) once with no invented samples; next count=800 -> 1600 real interleaved values");
        return 0;
    }
    catch (ExportException error) when (error.Code == "audio_frame_invalid")
    {
        Console.WriteLine("REPRO REJECTED count=0 -> " + error.Code + ": " + error.Message);
        return 1;
    }
}

var tests = new (string Name, Action Run)[]
{
    ("disabled buttons stay invisible over 300 manual frames with one shared panel refresh", () => StableFrames(showButtons: false, automaticCapture: false)),
    ("enabled buttons stay visible over 300 manual frames with one shared panel refresh", () => StableFrames(showButtons: true, automaticCapture: false)),
    ("disabled buttons stay invisible over 300 automatic frames with one shared panel refresh", () => StableFrames(showButtons: false, automaticCapture: true)),
    ("enabled buttons stay visible over 300 automatic frames with one shared panel refresh", () => StableFrames(showButtons: true, automaticCapture: true)),
    ("disabled playback controls stay out of captured frames after AA re-enables them", () => NativeVisibilityChanges(showButtons: false)),
    ("enabled playback controls remain visible after AA hides their parent or reopens the popup", () => NativeVisibilityChanges(showButtons: true)),
    ("enabled playback option renders both controls through their shared parent while hiding the popup", () =>
    {
        using var capture = NewCapture(showButtons: true);
        var player = CurrentPlayer!;
        player.menu.SetActive(false);
        player.menuBtn.gameObject.SetActive(false);
        player.autoToggle.gameObject.SetActive(false);
        Camera.main!.OnRender = () =>
        {
            Check(player.menuBtn.gameObject.activeInHierarchy && player.autoToggle.gameObject.activeInHierarchy,
                "ShowPlaybackButtons=true must make both controls visible through Top at Camera.Render.");
            Check(!player.menuBtn.menuPopup.activeInHierarchy, "The popup must stay out of the captured frame.");
        };
        capture.RenderFrame(new RenderTexture());
        capture.Dispose();
        Check(!player.menu.activeSelf && !player.menuBtn.gameObject.activeSelf && !player.autoToggle.gameObject.activeSelf);
        Check(player.menuBtn.menuPopup.activeSelf);
    }),
    ("recreated controls stay hidden with retired controls and all owned objects restore on disposal", () =>
    {
        using var capture = NewCapture();
        var player = CurrentPlayer!;
        var retiredTop = player.menu;
        var retiredMenu = player.menuBtn.gameObject;
        var retiredAuto = player.autoToggle.gameObject;
        var retiredPopup = player.menuBtn.menuPopup;
        capture.RenderFrame(new RenderTexture());
        Check(!retiredMenu.activeInHierarchy && !retiredAuto.activeInHierarchy && !retiredPopup.activeInHierarchy);
        var replacement = new Test();
        player.menu = replacement.menu;
        player.menuBtn = replacement.menuBtn;
        player.autoToggle = replacement.autoToggle;
        Camera.main!.OnRender = () =>
        {
            AssertCapturedControls(player, showButtons: false);
            Check(!retiredMenu.activeInHierarchy && !retiredAuto.activeInHierarchy && !retiredPopup.activeInHierarchy);
        };
        capture.RenderFrame(new RenderTexture());
        capture.Dispose();
        Check(player.menu.activeSelf && player.menuBtn.gameObject.activeInHierarchy && player.autoToggle.gameObject.activeInHierarchy && player.menuBtn.menuPopup.activeInHierarchy);
        Check(retiredTop.activeSelf && retiredMenu.activeInHierarchy && retiredAuto.activeInHierarchy && retiredPopup.activeInHierarchy);
    }),
    ("disabled controls and popup restore after render failure while the camera restores immediately", () => RenderFailure(showButtons: false)),
    ("enabled controls and popup restore after render failure while the camera restores immediately", () => RenderFailure(showButtons: true)),
    ("AA reactivation refreshes geometry before the next draw without refreshing unchanged later frames", () =>
    {
        using var capture = NewCapture();
        var player = CurrentPlayer!;
        player.menuBtn.gameObject.SetActive(true);
        player.autoToggle.gameObject.SetActive(true);
        bool cachedMenu = true, cachedAuto = true;
        var panel = new UIPanel
        {
            OnRefresh = () => { cachedMenu = player.menuBtn.gameObject.activeInHierarchy; cachedAuto = player.autoToggle.gameObject.activeInHierarchy; }
        };
        player.menuBtn.gameObject.widgets = new[] { new UIWidget { panel = panel } };
        player.autoToggle.gameObject.widgets = new[] { new UIWidget { panel = panel } };
        Camera.main!.OnRender = () =>
        {
            AssertCapturedControls(player, showButtons: false);
            Check(!cachedMenu && !cachedAuto);
        };
        capture.RenderFrame(new RenderTexture());
        Equal(1, panel.RefreshCalls);
        // Model native AA re-enabling a control and NGUI publishing that state
        // before the next capture. The next draw must not retain its geometry.
        player.menuBtn.gameObject.SetActive(true);
        cachedMenu = true;
        capture.RenderFrame(new RenderTexture());
        Equal(2, panel.RefreshCalls);
        for (int frame = 0; frame < 30; frame++) capture.RenderFrame(new RenderTexture());
        Equal(2, panel.RefreshCalls);
        Equal(1, player.menuBtn.gameObject.WidgetScans);
        Equal(1, player.autoToggle.gameObject.WidgetScans);
        capture.Dispose();
        Check(cachedMenu && cachedAuto);
        Equal(3, panel.RefreshCalls);
    }),
    ("geometry refresh failure during capture restores the camera and permits full session cleanup", () =>
    {
        using var capture = NewCapture(automaticCapture: false);
        var player = CurrentPlayer!;
        var panel = new UIPanel();
        panel.OnRefresh = () =>
        {
            if (panel.RefreshCalls == 1) throw new ExportException("test_panel_failure", "Synthetic panel failure");
        };
        player.menuBtn.gameObject.widgets = new[] { new UIWidget { panel = panel } };
        var originalTarget = Camera.main!.targetTexture;
        Throws("test_panel_failure", () => capture.RenderFrame(new RenderTexture()));
        Check(ReferenceEquals(originalTarget, Camera.main.targetTexture));
        capture.Dispose();
        Check(player.menu.activeSelf && player.menuBtn.gameObject.activeSelf && player.autoToggle.gameObject.activeSelf);
        Check(Camera.main.enabled && !player.auto && !player.selectionManager.AutoEnabled);
        Equal(1, AudioRenderer.StopCalls);
    }),
    ("geometry restore failure cannot block the other panels camera audio or timing restoration", () =>
    {
        using var capture = NewCapture(automaticCapture: false);
        var player = CurrentPlayer!;
        var panel = new UIPanel();
        var otherPanel = new UIPanel();
        panel.OnRefresh = () =>
        {
            if (panel.RefreshCalls == 2) throw new ExportException("test_panel_failure", "Synthetic panel failure");
        };
        player.menuBtn.gameObject.widgets = new[] { new UIWidget { panel = panel } };
        player.autoToggle.gameObject.widgets = new[] { new UIWidget { panel = otherPanel } };
        var originalTarget = Camera.main!.targetTexture;
        capture.RenderFrame(new RenderTexture());
        capture.Dispose();
        Check(player.menuBtn.gameObject.activeSelf && Camera.main.targetTexture == null, "menu/camera restore");
        Check(player.autoToggle.gameObject.activeSelf && Camera.main.enabled && !player.auto && !player.selectionManager.AutoEnabled, "auto/timing restore");
        Equal(2, otherPanel.RefreshCalls);
        Equal(1, AudioRenderer.StopCalls);
        Equal(72, Application.targetFrameRate);
        Equal(1, QualitySettings.vSyncCount);
    }),
    ("canceling an automatic frame restores initially visible controls with the option off", () => CancelPreparedFrame(showButtons: false)),
    ("canceling an automatic frame restores initially hidden controls and parent with the option on", () => CancelPreparedFrame(showButtons: true)),
    ("missing optional playback controls do not prevent story capture", () =>
    {
        using var capture = NewCapture();
        var player = CurrentPlayer!;
        player.menu = null!;
        player.menuBtn = null!;
        player.autoToggle = null!;
        int renders = 0;
        Camera.main!.OnRender = () => renders++;
        capture.RenderFrame(new RenderTexture());
        Equal(1, renders);
    }),
    ("zero-sample mixer frame renders once without manufacturing silence then preserves subsequent audio", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 0;
        var empty = capture.RenderAudio();
        Equal(1, AudioRenderer.RenderCalls);
        Equal(0, AudioRenderer.LastLength);
        Equal(0, empty.Count);
        Equal(0, empty.Samples.Length);
        AudioRenderer.SampleCount = 800;
        var next = capture.RenderAudio();
        Equal(2, AudioRenderer.RenderCalls);
        Equal(1600, next.Count);
        Equal(1600, AudioRenderer.LastLength);
        Check(next.Samples[0] == 0.25f && next.Samples[1599] == 0.5f);
    }),
    ("zero sample frames after nonempty frames cannot return stale prior audio", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 800;
        Equal(1600, capture.RenderAudio().Count);
        AudioRenderer.SampleCount = 0;
        var empty = capture.RenderAudio();
        Equal(2, AudioRenderer.RenderCalls);
        Equal(0, AudioRenderer.LastLength);
        Equal(0, empty.Count);
        Equal(0, empty.Samples.Length);
    }),
    ("native renderer failure on a zero-sample frame still fails the export", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 0;
        AudioRenderer.RenderResult = false;
        Throws("audio_render_failed", () => capture.RenderAudio());
        Equal(1, AudioRenderer.RenderCalls);
        Equal(0, AudioRenderer.LastLength);
    }),
    ("stereo samples reach the renderer in one contiguous interleaved buffer", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 800;
        var result = capture.RenderAudio();
        Equal(1600, result.Count);
        Equal(1600, AudioRenderer.LastLength);
        Check(result.Samples[0] == 0.25f && result.Samples[1599] == 0.5f);
        var next = capture.RenderAudio();
        Check(ReferenceEquals(result.Samples, next.Samples));
    }),
    ("changing native frame sample length resizes the buffer without truncation", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 735;
        Equal(1470, capture.RenderAudio().Count);
        AudioRenderer.SampleCount = 736;
        Equal(1472, capture.RenderAudio().Count);
        Equal(1472, AudioRenderer.LastLength);
    }),
    ("negative sample counts never reach the native renderer", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = -1;
        Throws("audio_frame_invalid", () => capture.RenderAudio());
        Equal(0, AudioRenderer.RenderCalls);
    }),
    ("implausibly large audio frames never allocate or call the renderer", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 48001;
        Throws("audio_frame_invalid", () => capture.RenderAudio());
        Equal(0, AudioRenderer.RenderCalls);
    }),
    ("native audio failure cannot become a successful silent frame", () =>
    {
        using var capture = NewCapture();
        AudioRenderer.SampleCount = 800;
        AudioRenderer.RenderResult = false;
        Throws("audio_render_failed", () => capture.RenderAudio());
        Equal(1, AudioRenderer.RenderCalls);
    }),
    ("disposing capture restores timing cameras and auto mode and stops owned audio once", () =>
    {
        var capture = NewCapture();
        Check(AudioRenderer.StartCalls == 1 && Camera.main!.enabled && Camera.main.targetTexture != null);
        Check(CurrentPlayer!.auto && Time.timeScale == 1 && Application.targetFrameRate == -1);
        capture.Dispose();
        capture.Dispose();
        Equal(1, AudioRenderer.StopCalls);
        Check(Camera.main!.enabled && !CurrentPlayer.auto && CurrentPlayer.menu.activeSelf);
        Equal(72, Application.targetFrameRate);
        Equal(1, QualitySettings.vSyncCount);
        Check(Time.captureDeltaTime == 0 && Time.timeScale == 0.5f && !Application.runInBackground);
    }),
    ("manual render fallback disables duplicate base camera and restores it", () =>
    {
        var capture = NewCapture(automaticCapture: false);
        Check(!Camera.main!.enabled);
        capture.Dispose();
        Check(Camera.main.enabled);
    }),
    ("failure to start audio restores native state without stopping someone else's audio", () =>
    {
        Throws("offline_audio_unavailable", () => NewCapture(audioCanStart: false));
        Equal(0, AudioRenderer.StopCalls);
        Check(Camera.main!.enabled && !CurrentPlayer!.auto);
        Equal(72, Application.targetFrameRate);
    })
};

int passed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS " + test.Name);
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL " + test.Name + ": " + error.Message);
    }
}
Console.WriteLine($"{passed}/{tests.Length} passed (real capture-scope logic; Unity audio semantics require native acceptance)");
return passed == tests.Length ? 0 : 1;

static void StableFrames(bool showButtons, bool automaticCapture)
{
    using var capture = NewCapture(showButtons: showButtons, automaticCapture: automaticCapture);
    var player = CurrentPlayer!;
    player.menu.activeSelf = !showButtons;
    player.menuBtn.gameObject.activeSelf = !showButtons;
    player.autoToggle.gameObject.activeSelf = !showButtons;
    var original = VisibilityState(player);
    bool cachedMenu = player.menuBtn.gameObject.activeInHierarchy;
    bool cachedAuto = player.autoToggle.gameObject.activeInHierarchy;
    bool cachedPopup = player.menuBtn.menuPopup.activeInHierarchy;
    var panel = new UIPanel
    {
        OnRefresh = () =>
        {
            cachedMenu = player.menuBtn.gameObject.activeInHierarchy;
            cachedAuto = player.autoToggle.gameObject.activeInHierarchy;
            cachedPopup = player.menuBtn.menuPopup.activeInHierarchy;
        }
    };
    AddDescendantWidget(player.menuBtn.gameObject, panel);
    AddDescendantWidget(player.autoToggle.gameObject, panel);
    AddDescendantWidget(player.menuBtn.menuPopup, panel);
    int renders = 0;
    Camera.main!.OnRender = () =>
    {
        AssertCapturedControls(player, showButtons);
        Check(cachedMenu == showButtons && cachedAuto == showButtons && !cachedPopup,
            "The shared NGUI panel must contain the requested geometry before Camera.Render.");
        renders++;
    };
    var target = new RenderTexture();
    void Render()
    {
        if (automaticCapture)
        {
            capture.PrepareAutomaticFrame();
            capture.PrepareAutomaticFrame();
            Camera.main!.Render();
            capture.RestoreAutomaticFrame();
        }
        else capture.RenderFrame(target);
    }
    Render();
    Check(player.menu.transform.localPosition.x == 0f,
        "Toolbar must use native target layout, not a screen-pixel translation.");
    Equal(1, panel.RefreshCalls);
    var firstFrameWork = VisibilityWork(player);
    for (int frame = 1; frame < 300; frame++) Render();
    Equal(300, renders);
    Equal(1, panel.RefreshCalls);
    Check(firstFrameWork == VisibilityWork(player), "Stable frames must not scan widgets or set activation again.");
    Check(player.auto && player.selectionManager.AutoEnabled);
    Equal(1, player.selectionManager.AutoModeChanges);
    capture.Dispose();
    Check(VisibilityState(player) == original, "Normal completion must restore the original parent, controls and popup.");
    Equal(2, panel.RefreshCalls);
    Check(cachedMenu == player.menuBtn.gameObject.activeInHierarchy && cachedAuto == player.autoToggle.gameObject.activeInHierarchy
        && cachedPopup == player.menuBtn.menuPopup.activeInHierarchy);
    AssertSessionRestored(player);
    capture.Dispose();
    Equal(2, panel.RefreshCalls);
}

static void NativeVisibilityChanges(bool showButtons)
{
    using var capture = NewCapture(showButtons: showButtons);
    var player = CurrentPlayer!;
    player.menu.activeSelf = !showButtons;
    player.menuBtn.gameObject.activeSelf = !showButtons;
    player.autoToggle.gameObject.activeSelf = !showButtons;
    var original = VisibilityState(player);
    int renders = 0;
    Camera.main!.OnRender = () => { AssertCapturedControls(player, showButtons); renders++; };
    for (int frame = 0; frame < 3; frame++)
    {
        // Native AA can hide Top, change either control, or reopen MenuPopup.
        player.menu.SetActive(!showButtons);
        player.menuBtn.gameObject.SetActive(!showButtons);
        player.autoToggle.gameObject.SetActive(!showButtons);
        player.menuBtn.menuPopup.SetActive(true);
        capture.RenderFrame(new RenderTexture());
        Check(player.auto && player.selectionManager.AutoEnabled);
    }
    Equal(3, renders);
    capture.Dispose();
    Check(VisibilityState(player) == original);
    AssertSessionRestored(player);
}

static void RenderFailure(bool showButtons)
{
    using var capture = NewCapture(showButtons: showButtons);
    var player = CurrentPlayer!;
    player.menuBtn.gameObject.activeSelf = false;
    var original = VisibilityState(player);
    var originalTarget = Camera.main!.targetTexture;
    var originalAspect = Camera.main.aspect;
    var originalRect = Camera.main.rect;
    Camera.main.OnRender = () =>
    {
        AssertCapturedControls(player, showButtons);
        throw new ExportException("test_render_failure", "Synthetic render failure");
    };
    Throws("test_render_failure", () => capture.RenderFrame(new RenderTexture()));
    Check(ReferenceEquals(originalTarget, Camera.main.targetTexture) && originalAspect == Camera.main.aspect && originalRect == Camera.main.rect);
    capture.Dispose();
    Check(VisibilityState(player) == original, "Render failure cleanup must restore the original parent, controls and popup.");
    AssertSessionRestored(player);
}

static void CancelPreparedFrame(bool showButtons)
{
    using var capture = NewCapture(showButtons: showButtons);
    var player = CurrentPlayer!;
    player.menu.activeSelf = !showButtons;
    player.menuBtn.gameObject.activeSelf = !showButtons;
    player.autoToggle.gameObject.activeSelf = !showButtons;
    player.menuBtn.menuPopup.activeSelf = false;
    var original = VisibilityState(player);
    var panel = new UIPanel();
    AddDescendantWidget(player.menuBtn.gameObject, panel);
    AddDescendantWidget(player.autoToggle.gameObject, panel);
    int renders = 0;
    Camera.main!.OnRender = () => renders++;
    capture.PrepareAutomaticFrame();
    AssertCapturedControls(player, showButtons);
    Equal(1, panel.RefreshCalls);
    // Cancel while the frame is prepared, before the native render callback.
    capture.Dispose();
    Equal(0, renders);
    Check(VisibilityState(player) == original, "Cancellation must restore the original parent, controls and popup.");
    Equal(2, panel.RefreshCalls);
    AssertSessionRestored(player);
}

static void AssertCapturedControls(Test player, bool showButtons)
{
    Check(player.menuBtn.gameObject.activeInHierarchy == showButtons,
        $"Menu button visibility at Camera.Render must match ShowPlaybackButtons={showButtons}; Top={player.menu.activeSelf}.");
    Check(player.autoToggle.gameObject.activeInHierarchy == showButtons,
        $"Auto button visibility at Camera.Render must match ShowPlaybackButtons={showButtons}; Top={player.menu.activeSelf}.");
    Check(!player.menuBtn.menuPopup.activeInHierarchy, "MenuPopup must stay out of the captured frame.");
}

static (bool Top, bool Menu, bool Auto, bool Popup) VisibilityState(Test player) =>
    (player.menu.activeSelf, player.menuBtn.gameObject.activeSelf, player.autoToggle.gameObject.activeSelf, player.menuBtn.menuPopup.activeSelf);

static (int Scans, int Changes) VisibilityWork(Test player)
{
    var objects = new[] { player.menu, player.menuBtn.gameObject, player.autoToggle.gameObject, player.menuBtn.menuPopup };
    return (objects.Sum(item => item.WidgetScans), objects.Sum(item => item.SetActiveCalls));
}

static void AddDescendantWidget(GameObject owner, UIPanel panel) =>
    _ = new GameObject { parent = owner, widgets = new[] { new UIWidget { panel = panel } } };

static void AssertSessionRestored(Test player)
{
    Check(Camera.main!.enabled && Camera.main.targetTexture == null && !player.auto && !player.selectionManager.AutoEnabled);
    Equal(2, player.selectionManager.AutoModeChanges);
    Equal(1, AudioRenderer.StopCalls);
    Equal(72, Application.targetFrameRate);
    Equal(1, QualitySettings.vSyncCount);
    Check(Time.captureDeltaTime == 0 && Time.timeScale == 0.5f && !Application.runInBackground);
}

static NativeCaptureScope NewCapture(bool audioCanStart = true, bool showButtons = false, bool automaticCapture = true)
{
    AudioRenderer.Reset();
    AudioRenderer.StartResult = audioCanStart;
    Application.targetFrameRate = 72;
    Application.runInBackground = false;
    QualitySettings.vSyncCount = 1;
    Time.captureDeltaTime = 0;
    Time.timeScale = 0.5f;
    AudioSettings.outputSampleRate = 48000;
    AudioSettings.speakerMode = AudioSpeakerMode.Stereo;
    Screen.height = 1440;
    var camera = new Camera();
    Camera.allCameras = new[] { camera };
    Camera.main = camera;
    CurrentPlayer = new Test();
    return new NativeCaptureScope(CurrentPlayer, new ExportOptions(), new RenderTexture(), showButtons, automaticCapture);
}
static void Check(bool condition, string message = "Assertion failed.") { if (!condition) throw new Exception(message); }
static void Equal(int expected, int actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}, actual {actual}.");
}
static void Throws(string code, Action action)
{
    try { action(); }
    catch (ExportException error) when (error.Code == code) { return; }
    throw new Exception("Expected export error " + code);
}

partial class Program
{
    private static Test? CurrentPlayer;
}
