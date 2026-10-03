using AAVideoExport.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace AAVideoExport.Plugin;

internal sealed class NativeCaptureScope : IDisposable
{
    private readonly List<(Camera Camera, RenderTexture Target, float Aspect, Rect Rect)> _cameras = new();
    private readonly List<Camera> _targetCameras = new();
    private readonly List<(Camera Camera, bool Enabled)> _displayCameras = new();
    private readonly int _fps = Application.targetFrameRate;
    private readonly int _vsync = QualitySettings.vSyncCount;
    private readonly float _delta = Time.captureDeltaTime;
    private readonly float _scale = Time.timeScale;
    private readonly bool _background = Application.runInBackground;
    private readonly Test _player;
    private readonly bool _auto;
    private readonly bool _showButtons;
    private readonly List<OwnedVisibility> _ownedVisibility = new();
    private readonly List<UIPanel> _changedPanels = new();
    private NativeExportViewport? _viewport;
    private bool _viewportReady;
    private bool _automaticFramePrepared;
    private readonly float _sourceAspect;
    private bool _audio;
    private bool _disposed;
    private float[] _audioBuffer = Array.Empty<float>();
    private long _audioFrames;
    private long _emptyAudioFrames;
    private bool _reportedFirstAudio;
    public Camera LastCamera { get; private set; } = null!;
    public bool AutomaticCameraCapture { get; }
    public int SampleRate { get; } = AudioSettings.outputSampleRate;
    public int Channels { get; }
    internal long VisibilityTicks { get; private set; }
    internal long CameraRenderTicks { get; private set; }
    internal long PanelRefreshCount { get; private set; }

    public NativeCaptureScope(Test player, ExportOptions options, RenderTexture target, bool showButtons, bool automaticCapture = true)
    {
        _player = player;
        _auto = player.auto;
        _showButtons = showButtons;
        _sourceAspect = options.CanvasMode == "viewport" ? options.Width / (float)options.Height
            : options.SourceWidth > 0 ? options.SourceWidth / (float)options.SourceHeight : options.Width / (float)options.Height;
        Channels = AudioSettings.speakerMode switch
        {
            AudioSpeakerMode.Mono => 1, AudioSpeakerMode.Stereo => 2, AudioSpeakerMode.Quad => 4,
            AudioSpeakerMode.Surround => 5, AudioSpeakerMode.Mode5point1 => 6, AudioSpeakerMode.Mode7point1 => 8,
            _ => throw new ExportException("audio_layout_unsupported", "当前 Unity 音频布局不支持离线导出。")
        };
        try
        {
            if (ScenarioResourceManager.Instance != null && ScenarioResourceManager.Instance.Preloading)
                throw new ExportException("assets_not_ready", "请等待 AA 素材预加载结束后再导出。");
            var scene = player.gameObject.scene;
            AutomaticCameraCapture = automaticCapture;
            var cameras = Camera.allCameras.Where(c => c != null && c.enabled && c.gameObject.activeInHierarchy
                && c.gameObject.scene == scene && c.targetTexture == null).OrderBy(c => c.depth).ToArray();
            if (cameras.Length == 0) throw new ExportException("playback_camera_missing", "没有找到鉴赏场景的渲染相机。请在鉴赏模式打开导出。");
            foreach (var camera in cameras)
            {
                _cameras.Add((camera, camera.targetTexture, camera.aspect, camera.rect));
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data == null || data.renderType != CameraRenderType.Overlay)
                    _targetCameras.Add(camera);
            }
            LastCamera = cameras[^1];
            if (AutomaticCameraCapture || options.CanvasMode == "viewport")
                SetTarget(target);
            if (!AutomaticCameraCapture)
            {
                // URP callback unavailable: stop AA's automatic screen pass
                // before using the explicit fallback render. AA Update,
                // animation and audio continue running; only duplicate camera
                // submission is removed.
                foreach (var camera in cameras)
                {
                    var data = camera.GetComponent<UniversalAdditionalCameraData>();
                    if (data != null && data.renderType == CameraRenderType.Overlay) continue;
                    _displayCameras.Add((camera, camera.enabled));
                    camera.enabled = false;
                }
            }
            if (options.CanvasMode == "viewport")
                _viewport = new NativeExportViewport(player, cameras, options.Width, options.Height);
            // Let AA's own Auto/voice/animation timing advance the story. The
            // exporter only captures frames until Test.End; it does not invent
            // dialogue holds, branch routes, or a duration limit.
            player.auto = true;
            if (player.selectionManager != null) player.selectionManager.OnAutoModeChanged(true);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            Time.timeScale = 1;
            Time.captureFramerate = options.Fps;
            // Even muted exports must drive the game mixer: native voice completion waits on it.
            _audio = AudioRenderer.Start();
            if (!_audio) throw new ExportException("offline_audio_unavailable", "Unity 离线音频渲染未启动。请停止其他音频导出后重试。");
        }
        catch { Dispose(); throw; }
    }

    public void RenderFrame(RenderTexture target)
    {
        var camera = LastCamera;
        if (camera == null) throw new ExportException("playback_camera_missing", "鉴赏相机已被销毁。");
        var oldTarget = camera.targetTexture;
        var oldAspect = camera.aspect;
        var oldRect = camera.rect;
        try
        {
            EnforceCaptureVisibility();
            camera.targetTexture = target;
            // Viewport mode uses the chosen output aspect and native layout.
            // Fit/fill remain available only for legacy core callers.
            camera.aspect = _sourceAspect;
            camera.rect = new Rect(0, 0, 1, 1);
            long started = Stopwatch.GetTimestamp();
            try { camera.Render(); }
            finally { CameraRenderTicks += Stopwatch.GetTimestamp() - started; }
        }
        finally
        {
            // The controls stay in their capture state for the whole session.
            // Restoring them every frame rebuilds NGUI batches twice per draw.
            // The camera still needs immediate restoration on render failure;
            // the host disposes this scope to restore UI and playback state.
            camera.targetTexture = oldTarget;
            camera.aspect = oldAspect;
            camera.rect = oldRect;
        }
    }

    public void SetTarget(RenderTexture target)
    {
        // The participating camera list and overlay classification are fixed at
        // capture start. Reusing the cached list avoids a GetComponent and LINQ
        // enumeration on every automatic-capture frame without changing the
        // camera order or the rendered pixels.
        foreach (var entry in _cameras)
        {
            var camera = entry.Camera;
            if (_targetCameras.Contains(camera)) camera.targetTexture = target;
            camera.aspect = _sourceAspect;
            camera.rect = new Rect(0, 0, 1, 1);
        }
    }

    public void PrepareAutomaticFrame()
    {
        if (!AutomaticCameraCapture || _automaticFramePrepared) return;
        EnforceCaptureVisibility();
        _automaticFramePrepared = true;
    }

    public void RestoreAutomaticFrame()
    {
        // The host calls this after each automatic camera callback. Release the
        // frame guard only; session disposal owns restoring button visibility.
        _automaticFramePrepared = false;
    }

    // These are the actual participating story cameras captured at startup.
    // Excluding the progress layer does not require another scene-wide search.
    public void ExcludeOverlayLayer(Action<Camera> exclude)
    {
        foreach (var entry in _cameras)
            if (entry.Camera != null) exclude(entry.Camera);
    }

    private void EnforceCaptureVisibility()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            // AA Start/Auto/scenario commands can reactivate or replace these
            // references. Check every capture but mutate only actual changes.
            // Auto playback itself remains owned by Test/SelectionManager.
            // Test.menu is the shared Top container for MenuButton_Normal and
            // AutoBtn. Hiding that parent makes the two child controls
            // invisible even when their own GameObjects are enabled. Keep the
            // popup closed separately; the checkbox means the two toolbar
            // buttons, not the menu contents.
            ApplyVisibility(_player.menu, _showButtons);
            ApplyVisibility(_player.menuBtn != null ? _player.menuBtn.gameObject : null, _showButtons);
            ApplyVisibility(_player.autoToggle != null ? _player.autoToggle.gameObject : null, _showButtons);
            ApplyVisibility(_player.menuBtn != null ? _player.menuBtn.menuPopup : null, false);
            // Wait for AA's first scenario initialization before reflowing.
            // Stable captures do not rebuild the full layout every frame.
            if (_viewport != null && !_viewportReady)
            {
                _viewport.RefreshLayout();
                _viewportReady = true;
            }
            RefreshButtonGeometry();
        }
        finally { VisibilityTicks += Stopwatch.GetTimestamp() - started; }
    }

    private void ApplyVisibility(GameObject? item, bool active)
    {
        if (item == null || item.activeSelf == active) return;
        // Stable frames return above without scanning widgets or allocating.
        // Retain replaced instances until disposal: immediately reactivating a
        // retired button can put its geometry into a later captured frame.
        foreach (var owned in _ownedVisibility)
        {
            if (owned.Item != item) continue;
            owned.Apply(active, _changedPanels);
            return;
        }
        var state = new OwnedVisibility(item);
        _ownedVisibility.Add(state);
        state.Apply(active, _changedPanels);
    }

    private readonly struct OwnedVisibility
    {
        internal readonly GameObject Item;
        private readonly bool _active;
        private readonly UIWidget[] _widgets;

        internal OwnedVisibility(GameObject item)
        {
            Item = item;
            _active = item.activeSelf;
            _widgets = item.GetComponentsInChildren<UIWidget>(true).ToArray();
        }

        internal void Apply(bool active, List<UIPanel> panels)
        {
            if (Item == null || Item.activeSelf == active) return;
            CollectPanels(panels);
            Item.SetActive(active);
            // OnEnable may associate an initially inactive widget with a panel.
            CollectPanels(panels);
        }

        private void CollectPanels(List<UIPanel> panels)
        {
            foreach (var widget in _widgets)
            {
                var panel = widget != null ? widget.panel : null;
                if (panel != null && !panels.Contains(panel)) panels.Add(panel);
            }
        }

        internal void Restore(List<UIPanel> panels)
        {
            try { Apply(_active, panels); }
            catch (Exception error)
            {
                Debug.LogWarning("AA Video Export: playback control restore failed (" + error.GetType().Name + ").");
            }
        }
    }

    private void RefreshButtonGeometry(bool restoring = false)
    {
        // NGUI batches widgets during LateUpdate. Capture runs at frame end, so
        // SetActive alone can leave their previous geometry in that frame.
        // Refresh just the affected panels once when visibility changes, and
        // once on disposal. A broken panel cannot block other restore steps.
        try
        {
            foreach (var panel in _changedPanels)
            {
                if (panel == null) continue;
                PanelRefreshCount++;
                if (restoring) Restore(panel.Refresh);
                else panel.Refresh();
            }
        }
        finally { _changedPanels.Clear(); }
    }

    public unsafe (float[] Samples, int Count) RenderAudio()
    {
        int perChannel = AudioRenderer.GetSampleCountForCaptureFrame();
        // Unity Recorder renders every mixer frame, including a zero-length
        // frame. Zero is availability, not failure; do not invent silent PCM.
        // GetSampleCountForCaptureFrame counts sample frames, so multiply by
        // channels exactly once before calling the native float-buffer API.
        if (perChannel < 0 || perChannel > SampleRate)
            throw new ExportException("audio_frame_invalid", $"Unity 音频长度异常：样本帧={perChannel}，采样率={SampleRate}，声道={Channels}。");
        int length = checked(perChannel * Channels);
        if (_audioBuffer.Length != length) _audioBuffer = new float[length];
        var samples = _audioBuffer;
        fixed (float* buffer = samples)
            if (!AudioRenderer.Internal_AudioRenderer_Render(buffer, length))
                throw new ExportException("audio_render_failed", $"Unity 音频渲染失败：样本帧={perChannel}，声道={Channels}。");
        _audioFrames++;
        if (perChannel == 0)
        {
            _emptyAudioFrames++;
            if (_emptyAudioFrames == 1)
                Debug.Log($"AA Video Export: empty mixer frame accepted; capture_frame={_audioFrames}, rate={SampleRate}, channels={Channels}, capture_delta={Time.captureDeltaTime:R}.");
        }
        else if (!_reportedFirstAudio)
        {
            _reportedFirstAudio = true;
            Debug.Log($"AA Video Export: first PCM frame; capture_frame={_audioFrames}, sample_frames={perChannel}, rate={SampleRate}, channels={Channels}.");
        }
        return (samples, length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreAutomaticFrame();
        foreach (var state in _ownedVisibility) state.Restore(_changedPanels);
        RefreshButtonGeometry(restoring: true);
        _ownedVisibility.Clear();
        if (_audio) { Restore(() => AudioRenderer.Stop()); _audio = false; }
        foreach (var state in _cameras)
            Restore(() => { if (state.Camera != null) { state.Camera.targetTexture = state.Target; state.Camera.aspect = state.Aspect; state.Camera.rect = state.Rect; } });
        foreach (var state in _displayCameras)
            Restore(() => { if (state.Camera != null) state.Camera.enabled = state.Enabled; });
        if (_viewport != null) { Restore(_viewport.Dispose); _viewport = null; }
        Restore(() => { if (_player != null) { _player.auto = _auto; if (_player.selectionManager != null) _player.selectionManager.OnAutoModeChanged(_auto); } });
        Restore(() => Time.captureDeltaTime = _delta);
        Restore(() => Time.timeScale = _scale);
        Restore(() => Application.targetFrameRate = _fps);
        Restore(() => Application.runInBackground = _background);
        Restore(() => QualitySettings.vSyncCount = _vsync);
    }

    private static void Restore(Action restore)
    {
        try { restore(); }
        catch (Exception error) { Debug.LogWarning("AA Video Export: restore step failed (" + error.GetType().Name + "). Restart AA before the next export."); }
    }
}
