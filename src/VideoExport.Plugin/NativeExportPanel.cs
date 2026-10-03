using System.Diagnostics;
using System.Globalization;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

/// <summary>AA-native NGUI controls, created and updated only on the game thread.</summary>
public sealed partial class NativeExportPanel : IDisposable
{
    private const int Width = 980, Height = 912;
    private static readonly Color Paper = new(0.965f, 0.980f, 0.992f, 1);
    private static readonly Color Ink = new(0.176f, 0.302f, 0.439f, 1);
    private static readonly Color Muted = new(0.424f, 0.553f, 0.643f, 1);
    private static readonly Color Cyan = new(0.306f, 0.824f, 0.965f, 1);
    private static readonly Color Yellow = new(0.976f, 0.910f, 0.298f, 1);
    private static readonly Color Line = new(0.812f, 0.871f, 0.906f, 1);
    private readonly NativeFolderPicker _picker = new();
    private readonly Stopwatch _elapsed = new();
    private readonly Stopwatch _presentationClock = Stopwatch.StartNew();
    private readonly UiRefreshCadence _refreshCadence = new();
    private readonly ExportUiFlow _flow = new();
    private readonly List<object> _delegates = new();
    private readonly List<FieldBinding> _fields = new();
    private readonly List<ButtonBinding> _buttons = new();
    private readonly List<RefreshBinding> _refresh = new();
    private readonly List<object> _popupDelegates = new();
    private readonly List<UIEventListener> _popupListeners = new();
    private readonly List<PopupRow> _popupRows = new();
    private readonly Dictionary<(int Width, int Height, int Radius, int Slant, bool Decoration), Texture2D> _shapes = new();
    private ExportOptions _options = new();
    private ExportOptions? _activeOptions;
    private GameObject? _persistentUi;
    private GameObject? _root;
    private GameObject? _settingsRoot, _exportProgressRoot, _cancelConfirmRoot;
    private UIRoot? _nativeRoot;
    private Camera? _nativeCamera;
    private Font? _trueTypeFont;
    private INGUIFont? _bitmapFont;
    private FontStyle _fontStyle;
    private UITexture? _modalBlocker;
    private readonly List<Camera> _excludedCameras = new();
    private readonly List<Camera> _idleCameras = new();
    private bool _idleBudget;
    private IDisposable? _renderOwnership;
    private int _idleFps, _idleVsync;
    private UITexture? _progressWidget;
    private UITexture? _exportProgressWidget;
    private bool _visible = true, _disposed, _busy, _wasBusy, _titleEdited, _recommendBitrate = true, _progressKnown;
    private int _layer;
    private string _preset = "1080", _aspect = "16:9", _width = "1920", _height = "1080";
    private string _bitrate = VideoBitrate.FormatMbps(VideoBitrate.RecommendedKbps);
    private string _rcasSharpness = "0.87";
    private string _notice = "", _projectName = "", _gpuName = "", _status = "";
    private float _progress;
    private long _frames;
    private IReadOnlyList<EncoderCapability>? _encoders;
    private GameObject? _popupRoot;
    private DropdownBinding? _openDropdown;
    private IReadOnlyList<DropdownItem> _popupItems = Array.Empty<DropdownItem>();
    private UILabel? _popupScrollLabel;
    private int _popupOffset;
    private int _layoutWidth, _layoutHeight;
    private bool _layoutDirty = true;
    private ExportUiPhase? _displayedPhase;
    private string _displayedStage = "";
    private double _nextCameraScan;

    internal bool IsCapturing { get; set; }
    internal bool ReduceCaptureUiWork { get; set; } = true;
    internal bool ShowPlaybackButtons { get; private set; }
    private bool ReducePresentationWork => !IsCapturing || ReduceCaptureUiWork;
    internal long ControlRefreshCount { get; private set; }
    internal long CameraScanCount { get; private set; }

    internal void ResetPerformanceCounters()
    {
        ControlRefreshCount = 0;
        CameraScanCount = 0;
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            RenderControlV1.EnsureMainThread();
            // Loading and rendering stay behind a progress surface. Hiding it
            // would expose the native story that is being processed internally.
            if (!value && _busy) return;
            if (value && _renderOwnership == null)
                _renderOwnership = RenderControlV1.Acquire();
            if (_visible == value && ReducePresentationWork) return;
            if (!value)
            {
                PollInputs();
                CommitOutputDirectory();
                ReleaseIdleBudget();
                ReleaseExcludedCameras();
            }
            _visible = value;
            _refreshCadence.Invalidate();
            _layoutDirty = true;
            if (_root != null)
            {
                if (!value) { CloseDropdown(); ReleaseInputFocus(); }
                _root.SetActive(value);
            }
            if (_nativeCamera != null) _nativeCamera.gameObject.SetActive(value);
            if (!value) ReleaseRenderOwnership();
        }
    }

    public ExportOptions Options
    {
        get => _options;
        set
        {
            _options = ((value ?? throw new ArgumentNullException(nameof(value))) with { CanvasMode = "viewport" })
                .NormalizeSuperResolutionForOutput();
            _width = Number(value.Width);
            _height = Number(value.Height);
            _bitrate = VideoBitrate.FormatMbps(value.BitrateKbps);
            _rcasSharpness = value.RcasSharpness.ToString("0.00", CultureInfo.InvariantCulture);
            _recommendBitrate = value.BitrateKbps == VideoBitrate.RecommendedKbps;
            _titleEdited = !string.IsNullOrWhiteSpace(value.Title) && value.Title != "Export";
            _preset = value.Width == 1920 && value.Height == 1080 ? "1080" : "custom";
        }
    }

    public double CaptureElapsedSeconds { get; set; }
    public string ExportStage { get; set; } = "准备中";
    public long EncodedFrames { get; set; }
    public Action<ExportOptions>? ExportRequested { get; set; }
    public Action<string>? OutputDirectoryCommitted { get; set; }
    public Action? CancelRequested { get; set; }
    public Action? ProbeRequested { get; set; }

    internal void SetSourceCanvas(int width, int height)
    {
        if (_busy || width <= 0 || height <= 0) return;
        _options = _options with { SourceWidth = width, SourceHeight = height };
        if (_preset == "native" || (_aspect == "canvas" && _preset != "custom")) SetResolution(_preset);
    }

    internal void SetStoryTitle(string title)
    {
        if (_busy || string.IsNullOrWhiteSpace(title)) return;
        _options = _options with { Title = title.Trim() };
        _titleEdited = false;
        _notice = "";
        _refreshCadence.Invalidate();
    }

    private int SourceWidth => _options.SourceWidth > 0 ? _options.SourceWidth : _options.Width;
    private int SourceHeight => _options.SourceHeight > 0 ? _options.SourceHeight : _options.Height;

    // The overlay has its own camera and layer. Leave it visible on the display
    // while the story camera renders offscreen; do not blink the progress UI.
    internal void HideForCapture(bool hidden)
    {
        if (hidden) IsolateOverlayLayer();
    }

    // The host also isolates each actual capture camera immediately before it
    // renders. This must not be delayed by the presentation refresh cadence.
    internal void ExcludeFrom(Camera camera)
    {
        if (_persistentUi == null || _nativeCamera == null || camera == null || camera == _nativeCamera) return;
        int mask = 1 << _layer;
        if ((camera.cullingMask & mask) == 0) return;
        if (!_excludedCameras.Contains(camera)) _excludedCameras.Add(camera);
        camera.cullingMask &= ~mask;
    }

    internal void ReleaseIdleBudget()
    {
        if (!_idleBudget) return;
        RenderControlV1.EnsureMainThread();
        try
        {
        foreach (var camera in _idleCameras) if (camera != null) camera.enabled = true;
        _idleCameras.Clear();
        Application.targetFrameRate = _idleFps;
        QualitySettings.vSyncCount = _idleVsync;
        _idleBudget = false;
        }
        catch { RenderControlV1.RestorationFailed(); throw; }
    }

    private void ApplyIdleBudget()
    {
        RenderControlV1.EnsureMainThread();
        _renderOwnership ??= RenderControlV1.Acquire();
        if (!_idleBudget)
        {
            _idleFps = Application.targetFrameRate;
            _idleVsync = QualitySettings.vSyncCount;
            _idleBudget = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
        }
        foreach (var camera in Camera.allCameras)
        {
            if (camera == null || camera == _nativeCamera || !camera.enabled || camera.targetTexture != null) continue;
            _idleCameras.Add(camera);
            camera.enabled = false;
        }
    }

    public void Draw(string projectName, string gpuName, string status, bool busy, long frames,
        float progress, Texture? preview, IReadOnlyList<EncoderCapability>? encoders)
    {
        _projectName = projectName;
        _gpuName = gpuName;
        _status = status;
        _busy = busy;
        if (busy) ReleaseIdleBudget();
        if (busy) Visible = true;
        _frames = frames;
        _progressKnown = float.IsFinite(progress) && progress >= 0;
        _progress = float.IsFinite(progress) ? Mathf.Clamp01(progress) : 0;
        if (busy && !_wasBusy)
        {
            _activeOptions = _options;
            _elapsed.Restart();
            CloseDropdown();
            ReleaseInputFocus();
        }
        if (busy != _wasBusy) _refreshCadence.Invalidate();
        var capabilitiesChanged = !ReferenceEquals(_encoders, encoders)
            && (_encoders == null || encoders == null || !_encoders.SequenceEqual(encoders));
        _encoders = encoders;
        if (capabilitiesChanged)
        {
            CloseDropdown();
            ReconcileHardwareSelection();
        }
        if (!busy && _wasBusy) _elapsed.Stop();
        _flow.ObserveBusy(busy);
        _wasBusy = busy;
    }

    public void Tick()
    {
        if (_disposed) return;
        if (_picker.TryTakeResult(out var directory, out var error))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                _options = _options with { OutputDirectory = directory };
                CommitOutputDirectory();
            }
            if (error != null) _notice = error;
            _refreshCadence.Invalidate();
        }
        if (!Visible) return;
        if (!_busy && !_titleEdited && !string.IsNullOrWhiteSpace(_projectName) && _options.Title != _projectName)
        {
            _options = _options with { Title = _projectName };
            _refreshCadence.Invalidate();
        }
        if (_root == null || _nativeRoot == null || !_nativeRoot.gameObject.activeInHierarchy)
        {
            DestroyHierarchy();
            if (!CreateHierarchy()) return;
        }

        // Input and cancellation are serviced on every Unity update. Only the
        // presentation work below is rate-limited by unmodified wall time.
        PollInputs();
        if (_busy && !ReducePresentationWork) { CloseDropdown(); ReleaseInputFocus(); }
        if (_flow.Phase == ExportUiPhase.ConfirmCancel && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            _flow.KeepExporting();
        else if (_popupRoot != null)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) CloseDropdown();
            else
            {
                var wheel = UnityEngine.Input.mouseScrollDelta.y;
                if (wheel != 0) ScrollDropdown(wheel > 0 ? -1 : 1);
            }
        }
        else if (!_busy)
        {
            if (_settingsNavigation.IsAdvanced && UnityEngine.Input.GetKeyDown(KeyCode.Escape)) BackToExportSettings();
            else if (PointerOverSettingsContent())
            {
                float wheel = UnityEngine.Input.mouseScrollDelta.y;
                if (wheel != 0) ScrollSettings(-wheel * 64);
            }
            if (!_fields.Any(field => field.Input != null && field.Input.isSelected))
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.PageDown)) ScrollSettings(_settingsGeometry?.ViewportHeight * .8f ?? 160);
                if (UnityEngine.Input.GetKeyDown(KeyCode.PageUp)) ScrollSettings(-(_settingsGeometry?.ViewportHeight * .8f ?? 160));
            }
        }
        RefreshFlowVisibility();
        int width = Screen.width, height = Screen.height;
        if (_layoutWidth != width || _layoutHeight != height)
        {
            _layoutWidth = width; _layoutHeight = height;
            _layoutDirty = true;
            _refreshCadence.Invalidate();
        }
        if (_displayedStage != ExportStage)
        {
            _displayedStage = ExportStage;
            _refreshCadence.Invalidate();
        }
        double wallSeconds = _presentationClock.Elapsed.TotalSeconds;
        bool reduceWork = ReducePresentationWork;
        if (reduceWork && !_refreshCadence.ShouldRefresh(wallSeconds)) return;
        ControlRefreshCount++;
        if (!IsCapturing && wallSeconds >= _nextCameraScan)
        {
            IsolateOverlayLayer();
            _nextCameraScan = wallSeconds + .5;
        }
        RefreshSettingsLayout();
        if (!_busy) ApplyIdleBudget();
        if (_layoutDirty || !reduceWork)
        {
            _nativeCamera!.clearFlags = CameraClearFlags.SolidColor;
            _nativeCamera.backgroundColor = Paper;
            _modalBlocker!.color = Paper;
            var activeHeight = Math.Max(1, _nativeRoot!.activeHeight);
            var activeWidth = activeHeight * width / (float)Math.Max(1, height);
            var scale = (_settingsGeometry is not null && !_busy)
                ? _settingsGeometry.FitScale(activeWidth, activeHeight)
                : Mathf.Max(0.01f, Mathf.Min(activeWidth * 0.90f / Width, activeHeight * 0.90f / _panelHeight));
            _root!.transform.localScale = new Vector3(scale, scale, 1);
            var distance = _nativeCamera.WorldToViewportPoint(_nativeRoot.transform.position).z;
            var center = _nativeCamera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, distance));
            _root.transform.localPosition = _nativeRoot.transform.InverseTransformPoint(center)
                + new Vector3(0, (_panelHeight - Height) * .5f * scale, 0);
            _layoutDirty = false;
        }
        // Settings are inactive during export. Do not poll their widgets or
        // rebuild their dropdown option lists while presenting progress.
        if (!_busy || !reduceWork)
        {
            foreach (var field in _fields)
            {
                if (reduceWork && !field.Input.gameObject.activeInHierarchy) continue;
                // Diagnostic baseline preserves the previous per-frame writes
                // so an A/B measurement changes presentation work, not pixels.
                if (!reduceWork)
                {
                    field.Input.enabled = !_busy && (field.Enabled?.Invoke() ?? true) && IsSettingsControlVisible(field.Collider);
                    field.Collider.enabled = field.Input.enabled;
                    field.Background.color = field.Input.enabled ? Color.white : Paper;
                }
                else
                {
                    bool enabled = (field.Enabled?.Invoke() ?? true) && IsSettingsControlVisible(field.Collider);
                    if (field.Input.enabled != enabled) field.Input.enabled = enabled;
                    if (field.Collider.enabled != enabled) field.Collider.enabled = enabled;
                    var color = enabled ? Color.white : Paper;
                    if (field.Background.color != color) field.Background.color = color;
                }
                if (!field.Input.isSelected && field.Input.value != field.Read())
                    field.Input.value = field.Read();
                field.LastValue = field.Input.value;
            }
            var activeFields = _fields.Where(field => field.Input.gameObject.activeInHierarchy && field.Input.enabled).ToArray();
            for (var i = 0; i < activeFields.Length; i++)
                activeFields[i].Input.selectOnTab = activeFields[(i + 1) % activeFields.Length].Input.gameObject;
        }
        foreach (var button in _buttons)
        {
            if (reduceWork && !button.Background.gameObject.activeInHierarchy) continue;
            var enabled = (button.AllowBusy || !_busy) && (button.Enabled?.Invoke() ?? true) && IsSettingsControlVisible(button.Collider);
            button.Collider.enabled = enabled;
            var selected = button.Selected?.Invoke() ?? false;
            button.Background.color = !enabled ? Line : selected ? Cyan : button.Primary ? Cyan : Color.white;
            button.Label.color = enabled ? Ink : Muted;
            SetText(button.Label, button.Text());
        }
        foreach (var collider in _settingsSliderColliders)
            if (collider != null && collider.gameObject.activeInHierarchy)
                collider.enabled = !_busy && IsSettingsControlVisible(collider);
        foreach (var update in _refresh)
            if (!reduceWork || update.Owner.activeInHierarchy) update.Refresh();
        if (!_busy || !reduceWork)
        {
            _progressWidget!.width = Math.Max(1, (int)Math.Round(892 * _progress));
            _progressWidget.alpha = _progress > 0 ? 1 : 0;
        }
        RefreshExportProgress();
    }

    private bool CreateHierarchy()
    {
        UILabel? fontSource = null;
        foreach (var root in Object.FindObjectsOfType<UIRoot>())
        {
            if (!root.enabled || !root.gameObject.activeInHierarchy) continue;
            var camera = root.GetComponentsInChildren<UICamera>(false)
                .FirstOrDefault(candidate => candidate.enabled && candidate.gameObject.activeInHierarchy);
            if (camera == null) continue;
            // Prefer a dynamic TrueType source. A bitmap atlas copied from a small
            // native label is permanently low resolution when this panel is scaled
            // to fit a wide range of window sizes.
            var labels = root.GetComponentsInChildren<UILabel>(false)
                .Where(label => label.enabled && (label.trueTypeFont != null || label.bitmapFont != null))
                .ToArray();
            var source = labels.FirstOrDefault(label => label.trueTypeFont != null)
                ?? labels.FirstOrDefault();
            if (source == null) continue;
            if (fontSource == null || source.trueTypeFont != null) fontSource = source;
            if (source.trueTypeFont != null) break;
        }
        if (fontSource == null) return false;
        _trueTypeFont = fontSource.trueTypeFont;
        _bitmapFont = fontSource.bitmapFont;
        _fontStyle = fontSource.fontStyle;

        // Do not attach to AA's scene root: it is destroyed when the selected
        // story loads. A separate persistent root keeps progress and Cancel live.
        _layer = FindUnusedLayer();
        _persistentUi = new GameObject("AAVideoExport.PersistentUi") { layer = _layer };
        _persistentUi.SetActive(false);
        Object.DontDestroyOnLoad(_persistentUi);
        var uiRootObject = Child(_persistentUi.transform, "Export UIRoot");
        _nativeRoot = uiRootObject.AddComponent<UIRoot>();
        _nativeRoot.scalingStyle = UIRoot.Scaling.Flexible;
        _nativeRoot.minimumHeight = 320;
        _nativeRoot.maximumHeight = 4320;
        _nativeRoot.adjustByDPI = false;
        var cameraObject = Child(_persistentUi.transform, "Export display camera");
        cameraObject.transform.localPosition = new Vector3(0, 0, -10);
        _nativeCamera = cameraObject.AddComponent<Camera>();
        _nativeCamera.orthographic = true;
        _nativeCamera.orthographicSize = 1;
        _nativeCamera.nearClipPlane = 0.1f;
        _nativeCamera.farClipPlane = 20;
        _nativeCamera.cullingMask = 1 << _layer;
        _nativeCamera.depth = 10000;
        _nativeCamera.clearFlags = _busy ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
        _nativeCamera.backgroundColor = Paper;
        _nativeCamera.useOcclusionCulling = false;
        var renderData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
        renderData.renderType = CameraRenderType.Base;
        renderData.renderShadows = false;
        renderData.renderPostProcessing = false;
        renderData.requiresColorOption = CameraOverrideOption.Off;
        renderData.requiresDepthOption = CameraOverrideOption.Off;
        var events = cameraObject.AddComponent<UICamera>();
        events.eventType = UICamera.EventType.UI_3D;
        events.eventReceiverMask = 1 << _layer;
        events.useMouse = true;
        events.useTouch = true;
        events.useKeyboard = true;
        events.useController = false;

        _root = new GameObject("AAVideoExport.NativePanel");
        _root.SetActive(false);
        _root.layer = _layer;
        _root.transform.SetParent(_nativeRoot.transform, false);
        _root.transform.localPosition = Vector3.zero;
        var panel = _root.AddComponent<UIPanel>();
        panel.depth = Math.Max(20000, UIPanel.nextUnusedDepth + 100);
        panel.clipping = UIDrawCall.Clipping.None;
        var dim = Texture(_root.transform, "Modal blocker", -4400, -4400, 10000, 10000,
            _busy ? Paper : new Color(0.10f, 0.19f, 0.29f, 0.58f), 0);
        _modalBlocker = dim;
        AddCollider(dim.gameObject, 10000, 10000);
        _outerShadow = Rounded(_root.transform, "Soft outer shadow", -6, 4, Width + 12, Height + 12, 30, new Color(0.06f, 0.17f, 0.28f, 0.10f), 1);
        _panelShadow = Rounded(_root.transform, "Panel shadow", 0, 8, Width, Height, 26, new Color(0.06f, 0.17f, 0.28f, 0.18f), 2);
        _paperSurface = Rounded(_root.transform, "Paper", 0, 0, Width, Height, 24, Color.white, 3, decoration: true);
        var title = Label(_root.transform, "视频导出", 44, 24, 892, 44, 32, centered: true);
        _refresh.Add(new RefreshBinding(title.gameObject, () => SetText(title, _busy ? "导出进度" : "视频导出")));
        Rounded(_root.transform, "Title underline", 432, 76, 116, 4, 2, Yellow, 4);
        Button(_root.transform, 892, 22, 44, 42, () => "×", () => Visible = false);
        _settingsRoot = Child(_root.transform, "Export settings");
        var settings = _settingsRoot.transform;
        BuildSharedFields(settings);
        // Playback is owned by AA itself. The exporter captures the selected
        // story until its native Test.End(), so there is no separate timing,
        // branch, or maximum-duration page to configure here.
        BuildSettingsViewport(settings);
        BuildVideo(_settingsContent!.transform);
        BuildAudio(_settingsContent.transform);
        BuildAdvanced(_settingsContent!.transform);
        BuildStatus(settings);
        BuildExportProgress();
        BuildCancelConfirmation();
        // Preserve the accordion and its scroll position after a native rebuild.
        RefreshSettingsLayout();
        RefreshFlowVisibility();
        IsolateOverlayLayer();
        _persistentUi.SetActive(true);
        _root.SetActive(true);
        _layoutDirty = true;
        _refreshCadence.Invalidate();
        return true;
    }

    private static int FindUnusedLayer()
    {
        var used = new HashSet<int>();
        foreach (var item in Resources.FindObjectsOfTypeAll<GameObject>())
            if (item != null) used.Add(item.layer);
        for (int candidate = 31; candidate >= 8; candidate--)
            if (!used.Contains(candidate) && string.IsNullOrEmpty(LayerMask.LayerToName(candidate)))
                return candidate;
        throw new ExportException("export_ui_layer_unavailable", "没有可供导出面板使用的独立显示层，请重启 AA 后重试。");
    }

    private void IsolateOverlayLayer()
    {
        if (_persistentUi == null || _nativeCamera == null) return;
        CameraScanCount++;
        _excludedCameras.RemoveAll(camera => camera == null);
        foreach (var camera in Object.FindObjectsOfType<Camera>())
            ExcludeFrom(camera);
    }

    private ExportOptions ActiveOptions => _activeOptions ?? _options;

    private void BuildExportProgress()
    {
        _exportProgressRoot = Child(_root!.transform, "Export progress page");
        var parent = _exportProgressRoot.transform;
        Rounded(parent, "Progress card", 44, 111, 892, 226, 16, Paper, 4);
        DynamicLabel(parent, () => _flow.Phase == ExportUiPhase.Cancelling ? "正在取消" : ExportStage,
            72, 131, 836, 38, 27);
        DynamicLabel(parent, () => _flow.Phase == ExportUiPhase.Cancelling
            ? "正在停止编码并清理本次临时输出，请稍候。" : _status, 72, 179, 836, 32, 17, Muted);
        Rounded(parent, "Export progress track", 72, 232, 836, 10, 5, Line, 5);
        _exportProgressWidget = Texture(parent, "Export progress indicator", 72, 232, 1, 10, Cyan, 6);
        DynamicLabel(parent, () => _progressKnown ? $"已完成 {_progress:P0}"
            : _frames > 0 ? "总时长未知 · 已输出帧数实时更新" : "等待本阶段处理完成", 72, 256, 530, 26, 17, Muted);
        DynamicLabel(parent, () => $"已用时  {(int)_elapsed.Elapsed.TotalMinutes:00}:{_elapsed.Elapsed.Seconds:00}",
            666, 256, 242, 26, 17, Muted);
        DynamicLabel(parent, () => $"已渲染  {Math.Max(0, _frames):N0} 帧", 72, 298, 274, 27, 18);
        DynamicLabel(parent, () => $"已编码  {Math.Max(0, EncodedFrames):N0} 帧", 355, 298, 274, 27, 18);
        DynamicLabel(parent, () => $"视频时长  {Math.Max(0, _frames) / (double)Math.Max(1, ActiveOptions.Fps):0.0} 秒",
            638, 298, 274, 27, 18);

        Label(parent, "本次导出设置", 44, 365, 892, 31, 22);
        Rounded(parent, "Frozen settings card", 44, 405, 892, 223, 16, Paper, 4);
        DynamicLabel(parent, () => "文件：" + ActiveOptions.Title + "." + ActiveOptions.Container,
            68, 420, 844, 29, 19);
        DynamicLabel(parent, () => "保存位置：" + ActiveOptions.OutputDirectory, 68, 457, 844, 35, 16, Muted);
        Texture(parent, "Summary divider", 68, 502, 844, 1, Line, 5);
        DynamicLabel(parent, () => $"画面  {ActiveOptions.Width} × {ActiveOptions.Height}  /  {ActiveOptions.Fps} 帧/秒",
            68, 519, 420, 27, 17);
        DynamicLabel(parent, () => $"格式与编码  {ActiveOptions.Container.ToUpperInvariant()}  /  {CodecName(ActiveOptions.Codec)}",
            500, 519, 412, 27, 17);
        DynamicLabel(parent, () => $"码率  {VideoBitrate.FormatMbps(ActiveOptions.BitrateKbps)} Mbps  /  "
            + (ActiveOptions.RateControl == "cbr" ? "CBR（恒定码率）" : "VBR（可变码率）"),
            68, 565, 420, 36, 16);
        DynamicLabel(parent, () => "音频  " + AudioSummary(ActiveOptions), 500, 565, 412, 36, 16);
        DynamicLabel(parent, () => _flow.Phase == ExportUiPhase.Cancelling
            ? "取消完成后将返回设置，已填写的导出选项会保留。"
            : "正在自动处理所选剧情。完成后将保存视频；导出期间请保持 AA 运行。", 44, 660, 892, 46, 17, Muted);
        Button(parent, 352, 736, 276, 44,
            () => _flow.Phase == ExportUiPhase.Cancelling ? "正在取消…" : "中断导出",
            () => { _flow.AskToCancel(); RefreshFlowVisibility(); },
            primary: true, enabled: () => _flow.Phase == ExportUiPhase.Progress, allowBusy: true);
    }

    private static string AudioSummary(ExportOptions options)
    {
        var quality = options.AudioQuality switch
        {
            "none" => "关闭", "aac128" => "AAC 128 Kbps", "aac192" => "AAC 192 Kbps", "aac320" => "AAC 320 Kbps",
            "pcm16" => "PCM 16 位", "pcm24" => "PCM 24 位", _ => options.AudioQuality
        };
        return options.AudioQuality == "none" ? quality : quality + (options.AudioSampleRate == 44100 ? " / 44.1 kHz" : " / 48 kHz");
    }

    private void BuildCancelConfirmation()
    {
        _cancelConfirmRoot = Child(_root!.transform, "Confirm cancellation modal");
        _cancelConfirmRoot.SetActive(false);
        var panel = _cancelConfirmRoot.AddComponent<UIPanel>();
        panel.depth = Math.Max(_root.GetComponent<UIPanel>().depth + 300, UIPanel.nextUnusedDepth + 1);
        panel.clipping = UIDrawCall.Clipping.None;
        var parent = _cancelConfirmRoot.transform;
        var blocker = Texture(parent, "Confirmation click blocker", -4400, -4400, 10000, 10000,
            new Color(0.10f, 0.19f, 0.29f, 0.34f), 0);
        AddCollider(blocker.gameObject, 10000, 10000);
        Rounded(parent, "Confirmation shadow", 166, 245, 648, 310, 24, new Color(0.06f, 0.17f, 0.28f, 0.18f), 1);
        var paper = Rounded(parent, "Confirmation paper", 170, 240, 640, 304, 22, Color.white, 2, decoration: true);
        AddCollider(paper.gameObject, 640, 304);
        Label(parent, "确定中断导出？", 204, 278, 572, 43, 29, centered: true);
        Rounded(parent, "Confirmation underline", 448, 330, 84, 4, 2, Yellow, 3);
        Label(parent, "中断后将停止本次导出并返回设置。\n已填写的导出选项会保留。", 212, 360, 556, 63, 18, Muted, true);
        Button(parent, 216, 457, 258, 44, () => "继续导出",
            () => { _flow.KeepExporting(); RefreshFlowVisibility(); },
            enabled: () => _flow.Phase == ExportUiPhase.ConfirmCancel, allowBusy: true);
        Button(parent, 506, 457, 258, 44, () => "确认中断", () =>
        {
            if (!_flow.ConfirmCancel()) return;
            RefreshFlowVisibility();
            CancelRequested?.Invoke();
        }, primary: true, enabled: () => _flow.Phase == ExportUiPhase.ConfirmCancel, allowBusy: true);
    }

    private void RefreshFlowVisibility()
    {
        if (_displayedPhase == _flow.Phase && ReducePresentationWork) return;
        _displayedPhase = _flow.Phase;
        _refreshCadence.Invalidate();
        _settingsRoot?.SetActive(_flow.Phase == ExportUiPhase.Settings);
        _exportProgressRoot?.SetActive(_flow.Phase != ExportUiPhase.Settings);
        _cancelConfirmRoot?.SetActive(_flow.Phase == ExportUiPhase.ConfirmCancel);
    }

    private void RefreshExportProgress()
    {
        if (_exportProgressWidget == null || !_busy) return;
        // Unknown story duration uses a moving indicator; only measured progress gets a percentage.
        var width = _progressKnown ? Math.Max(1, (int)Math.Round(836 * _progress)) : 150;
        var offset = _progressKnown ? 0 : (int)((_elapsed.Elapsed.TotalSeconds * 170) % (836 - width));
        _exportProgressWidget.width = width;
        _exportProgressWidget.alpha = !_progressKnown || _progress > 0 ? 1 : 0;
        _exportProgressWidget.transform.localPosition = new Vector3(72 + offset - Width / 2f, Height / 2f - 232, 0);
    }

    private string UpscaleModelTitle() => _options.UpscaleAlgorithm switch
    {
        "anime4k-cnn" => "Anime4K", "anime4k-rcas" => "Anime4K → FSR RCAS", "fsr1-luma" => "FSR1",
        "bicubic" => "双三次", "lanczos" => "Lanczos", _ => "双线性"
    };

    private string SuperResolutionSummary()
    {
        if (!_options.IsSuperResolutionTierAvailable("quality"))
            return "输出短边不足 720 像素，使用原生渲染。";
        if (!_options.SuperResolutionEnabled)
            return $"原生渲染 {_options.Width} × {_options.Height}；开启后按所选档位放大。";
        var canvas = _options.GetCanvasLayout();
        return $"内部 {canvas.CaptureWidth} × {canvas.CaptureHeight} → {_options.Width} × {_options.Height}";
    }

    private void PollInputs()
    {
        if (_busy) return;
        foreach (var field in _fields)
        {
            if (field.Input == null || !field.Input.gameObject.activeInHierarchy) continue; // AA may have torn down native widgets during shutdown.
            if (!(field.Enabled?.Invoke() ?? true)) continue;
            var value = field.Input.value ?? "";
            if (value == field.LastValue) continue;
            field.Write(value);
            field.LastValue = value;
            _refreshCadence.Invalidate();
        }
    }

    private void EditDimensions()
    {
        _preset = "custom";
        if (!int.TryParse(_width, out var width) || !int.TryParse(_height, out var height) || !ValidDimensions(width, height)) return;
        _options = (_options with { Width = width, Height = height, CanvasMode = "viewport" })
            .NormalizeSuperResolutionForOutput();
        RecommendBitrate();
    }

    private void SetResolution(string preset)
    {
        _preset = preset;
        if (preset == "custom") return;
        if (preset == "native") _aspect = "canvas";
        var (width, height) = ResolutionDimensions(preset);
        _options = (_options with { Width = width, Height = height, CanvasMode = "viewport" })
            .NormalizeSuperResolutionForOutput();
        _width = Number(width);
        _height = Number(height);
        RecommendBitrate();
    }

    private void SetContainer(string value)
    {
        if (value == "mov" && _options.Codec == "av1")
        {
            var fallback = CodecItems().FirstOrDefault(item => item.Value != "av1");
            if (fallback == null)
            { _notice = "本机可用的 AV1 编码器不支持 MOV，请使用 MP4 或 MKV。"; return; }
            _options = _options with { Codec = fallback.Value, Encoder = "auto" };
            _notice = "MOV 不支持 AV1，已切换到本机可用的 " + fallback.Title + " 硬件编码。";
        }
        _options = _options with { Container = value };
        RecommendBitrate();
    }

    private void RecommendBitrate()
    {
        if (!_recommendBitrate) return;
        _options = _options with { BitrateKbps = VideoBitrate.RecommendedKbps };
        _bitrate = VideoBitrate.FormatMbps(VideoBitrate.RecommendedKbps);
    }

    private (int Width, int Height) ResolutionDimensions(string preset)
    {
        if (preset == "custom") return (_options.Width, _options.Height);
        if (preset == "native") return (Math.Max(2, SourceWidth / 2 * 2), Math.Max(2, SourceHeight / 2 * 2));
        var ratio = _aspect switch { "16:9" => 16d / 9d, "9:16" => 9d / 16d, "4:3" => 4d / 3d, "1:1" => 1d, _ => SourceWidth / (double)SourceHeight };
        var shortSide = int.Parse(preset, CultureInfo.InvariantCulture);
        var width = ratio < 1 ? shortSide : (int)Math.Round(shortSide * ratio);
        var height = ratio < 1 ? (int)Math.Round(shortSide / ratio) : shortSide;
        return (Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));
    }

    private IReadOnlyList<DropdownItem> ResolutionItems() => new[] { "480", "720", "1080", "1440", "2160", "4320", "native", "custom" }
        .Select(value =>
        {
            (int width, int height) dimensions;
            try { dimensions = ResolutionDimensions(value); }
            catch (ExportException) { return null; }
            var (width, height) = dimensions;
            var title = value switch { "1440" => "2K", "2160" => "4K", "4320" => "8K", "native" => "当前窗口", "custom" => "自定义尺寸", _ => value + "P" };
            if (value != "custom") title += $" · {width} × {height}";
            return new DropdownItem(value, title, $"{width} × {height}" + (value == "custom" ? " · 手动输入偶数宽高" : value == "native" ? " · 当前窗口尺寸" : " · 按目标画布重新排版"));
        }).Where(item => item != null).Select(item => item!).ToArray();

    private IEnumerable<EncoderCapability> AvailableHardware => _encoders?.Where(encoder => encoder.Hardware && encoder.Available
        && encoder.Codec is "h264" or "hevc" or "av1") ?? Enumerable.Empty<EncoderCapability>();

    private bool HasHardware => AvailableHardware.Any(encoder => encoder.Codec == _options.Codec
        && (_options.Encoder == "auto" || encoder.Name == _options.Encoder));

    private string HardwarePlaceholder() => _encoders == null ? "检测本机GPU..." : "无可用硬件编码器";

    private IReadOnlyList<DropdownItem> CodecItems() => new[] { "h264", "hevc", "av1" }
        .Where(codec => AvailableHardware.Any(encoder => encoder.Codec == codec))
        .Select(codec => new DropdownItem(codec, CodecName(codec), codec switch
        {
            "h264" => "通用兼容 · 本机硬件加速",
            "hevc" => "更小文件 · 本机硬件加速；播放设备需支持",
            _ => "更高压缩效率 · 播放设备需支持 AV1"
        })).ToArray();

    private IReadOnlyList<DropdownItem> EncoderItems()
    {
        var hardware = AvailableHardware.Where(encoder => encoder.Codec == _options.Codec).ToArray();
        if (hardware.Length == 0) return Array.Empty<DropdownItem>();
        return new[] { new DropdownItem("auto", "自动选择本机硬件", $"{hardware.Length} 个已通过检测的硬件编码器") }
            .Concat(hardware.Select(encoder => new DropdownItem(encoder.Name, encoder.Name,
                encoder.Name.Contains("nvenc", StringComparison.Ordinal) ? "NVIDIA NVENC · 已通过编码检测"
                : encoder.Name.Contains("qsv", StringComparison.Ordinal) ? "Intel Quick Sync · 已通过编码检测"
                : encoder.Name.Contains("amf", StringComparison.Ordinal) ? "AMD AMF · 已通过编码检测" : "硬件编码 · 已通过检测"))).ToArray();
    }

    private void ReconcileHardwareSelection()
    {
        if (_encoders == null) return;
        var codecs = CodecItems();
        if (codecs.Count == 0)
        { _notice = "未检测到可用的 GPU 硬件编码器。请检查显卡驱动与 FFmpeg，然后重新检测。"; return; }
        if (_notice.StartsWith("未检测到可用的 GPU", StringComparison.Ordinal)) _notice = "";
        if (!codecs.Any(codec => codec.Value == _options.Codec))
        {
            var codec = codecs[0];
            _options = _options with { Codec = codec.Value, Encoder = "auto", Container = codec.Value == "av1" && _options.Container == "mov" ? "mkv" : _options.Container };
            _notice = "原视频编码在本机不可用，已切换到 " + codec.Title + " 硬件编码。";
        }
        else if (_options.Encoder != "auto" && !AvailableHardware.Any(encoder => encoder.Codec == _options.Codec && encoder.Name == _options.Encoder))
        {
            _options = _options with { Encoder = "auto" };
            _notice = "原硬件编码器不可用，已切换为自动选择本机可用硬件。";
        }
    }

    private void RequestExport()
    {
        CloseDropdown();
        PollInputs();
        CommitOutputDirectory();
        if (!HasHardware) { _notice = HardwarePlaceholder() + "。完成硬件检测后才能开始导出。"; return; }
        if (string.IsNullOrWhiteSpace(_options.Title)) { _notice = "请填写作品名称。"; return; }
        if (string.IsNullOrWhiteSpace(_options.OutputDirectory)) { _notice = "请选择或填写保存位置。"; return; }
        if (_options.Title.Length > 120 || _options.Title != _options.Title.Trim() || _options.Title.EndsWith('.')
            || _options.Title.Contains("..", StringComparison.Ordinal) || _options.Title.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
        { _notice = "作品名称请使用 1–120 个有效文件名字符，首尾不能有空格，结尾不能有句点。"; return; }
        var stem = _options.Title.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$"))
        { _notice = "此作品名称被 Windows 保留，请换一个名称。"; return; }
        if (!OutputDirectoryPreference.TryNormalize(_options.OutputDirectory, out var directory))
        { _notice = "保存路径无效，请重新选择文件夹；路径必须完整且不能包含换行或非法字符。"; return; }
        _options = _options with { OutputDirectory = directory };
        if (!int.TryParse(_width, out var width) || !int.TryParse(_height, out var height) || !ValidDimensions(width, height))
        { _notice = "宽高须为偶数，至少 16 像素；最长边不超过 7680，最短边不超过 4320。"; return; }
        _options = (_options with { Width = width, Height = height }).NormalizeSuperResolutionForOutput();
        if (_options.SuperResolutionEnabled && _options.UpscaleAlgorithm == "anime4k-rcas" &&
            (!double.TryParse(_rcasSharpness, NumberStyles.Float, CultureInfo.InvariantCulture, out var sharpness)
                || !double.IsFinite(sharpness) || sharpness < 0 || sharpness > 1))
        { _notice = "RCAS 锐度须为 0–1 之间的数字。"; return; }
        RecommendBitrate();
        if (!VideoBitrate.TryParseMbps(_bitrate, out var bitrate))
        { _notice = "目标码率须为 0.1–500 Mbps，可保留三位小数。"; return; }
        _options = _options with { BitrateKbps = bitrate };
        try
        {
            _options.Validate();
            _titleEdited = true;
            _notice = "";
            ExportRequested?.Invoke(_options);
        }
        catch (Exception ex)
        {
            _notice = "无法开始导出：" + ExportErrorText.Describe(ex);
            UnityEngine.Debug.LogError("AA Video Export: start failed\n" + ex);
        }
    }

    private static bool ValidDimensions(int width, int height) => width is >= 16 and <= 7680 && height is >= 16 and <= 7680
        && Math.Min(width, height) <= 4320 && width % 2 == 0 && height % 2 == 0;

    private void CommitOutputDirectory()
    {
        if (!OutputDirectoryPreference.TryNormalize(_options.OutputDirectory, out var directory)) return;
        OutputDirectoryCommitted?.Invoke(directory);
    }
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string CodecName(string value) => value switch { "h264" => "H.264", "hevc" => "HEVC / H.265", "av1" => "AV1", _ => value };

    private GameObject Child(Transform parent, string name)
    {
        var child = new GameObject(name) { layer = _layer };
        child.transform.SetParent(parent, false);
        return child;
    }

    private UITexture Texture(Transform parent, string name, float x, float y, int width, int height, Color color, int depth)
    {
        var child = Child(parent, name);
        var widget = child.AddComponent<UITexture>();
        widget.pivot = UIWidget.Pivot.TopLeft;
        widget.mainTexture = Texture2D.whiteTexture;
        widget.width = width;
        widget.height = height;
        widget.depth = depth;
        widget.color = color;
        child.transform.localPosition = new Vector3(x - Width / 2f, Height / 2f - y, 0);
        return widget;
    }

    private UITexture Rounded(Transform parent, string name, float x, float y, int width, int height, int radius,
        Color color, int depth, int slant = 0, bool decoration = false)
    {
        var widget = Texture(parent, name, x, y, width, height, color, depth);
        widget.mainTexture = RoundedShape(width, height, radius, slant, decoration);
        return widget;
    }

    private UILabel Label(Transform parent, string text, float x, float y, int width, int height, int size,
        Color? color = null, bool centered = false, int depth = 22)
    {
        var child = Child(parent, "Label");
        var label = child.AddComponent<UILabel>();
        if (_trueTypeFont != null) label.trueTypeFont = _trueTypeFont;
        else label.bitmapFont = _bitmapFont;
        label.fontStyle = _fontStyle;
        label.fontSize = size;
        // NGUI regenerates a TrueType glyph atlas at the final size instead of
        // shrinking an existing low-resolution glyph texture. This matters because
        // the modal itself is fitted with a fractional UIRoot scale.
        label.keepCrispWhenShrunk = UILabel.Crispness.Always;
        // NGUI uses the pivot for vertical text alignment as well as geometry.
        // TopLeft leaves spare line height below every glyph, making controls
        // look top-heavy. Align the text around the control's vertical centre.
        label.pivot = centered ? UIWidget.Pivot.Center : UIWidget.Pivot.Left;
        label.width = width;
        label.height = height;
        label.depth = depth;
        label.color = color ?? Ink;
        label.supportEncoding = false;
        label.symbolStyle = NGUIText.SymbolStyle.None;
        label.alignment = centered ? NGUIText.Alignment.Center : NGUIText.Alignment.Left;
        label.overflowMethod = UILabel.Overflow.ShrinkContent;
        label.text = text;
        child.transform.localPosition = new Vector3(x - Width / 2f + (centered ? width / 2f : 0),
            Height / 2f - y - height / 2f, 0);
        return label;
    }

    private void DynamicLabel(Transform parent, Func<string> text, int x, int y, int width, int height, int size, Color? color = null)
    {
        var label = Label(parent, text(), x, y, width, height, size, color);
        _refresh.Add(new RefreshBinding(label.gameObject, () => SetText(label, text())));
    }

    private void Caption(Transform parent, string text, int x, int y) => Label(parent, text, x, y + 3, 436, 18, 15, Muted);

    private static void SetText(UILabel label, string value)
    {
        if (label.text != value) label.text = value;
    }

    private static BoxCollider AddCollider(GameObject gameObject, int width, int height)
    {
        var collider = gameObject.AddComponent<BoxCollider>();
        collider.center = new Vector3(width / 2f, -height / 2f, 0);
        collider.size = new Vector3(width, height, 1);
        return collider;
    }

    private void Button(Transform parent, int x, int y, int width, int height, Func<string> text, Action click,
        Func<bool>? selected = null, bool primary = false, Func<bool>? enabled = null, bool allowBusy = false, bool leftAligned = false)
    {
        var slant = primary ? 9 : 0;
        Rounded(parent, "Button border", x - 1, y - 1, width + 2, height + 2, 11, Line, 19, slant);
        var background = Rounded(parent, "Button", x, y, width, height, 10, primary ? Cyan : Color.white, 20, slant);
        var collider = AddCollider(background.gameObject, width, height);
        int labelInset = width < 40 ? 2 : 8;
        var label = Label(parent, text(), x + labelInset, y, width - 2 * labelInset, height, 17, centered: !leftAligned);
        Action<GameObject> handler = _ =>
        {
            if ((!allowBusy && _busy) || !(enabled?.Invoke() ?? true)) return;
            PollInputs();
            click();
            _refreshCadence.Invalidate();
        };
        var native = DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>(handler)
            ?? throw new InvalidOperationException("Cannot bind native export control.");
        UIEventListener.Get(background.gameObject).onClick = native;
        _delegates.Add(handler);
        _delegates.Add(native);
        _buttons.Add(new ButtonBinding(background, label, collider, text, selected, enabled, primary, allowBusy));
    }

    private void Dropdown(Transform parent, int x, int y, int width, Func<string> current,
        Func<IReadOnlyList<DropdownItem>> items, Action<string> change, Func<bool>? enabled = null,
        Func<string>? placeholder = null, int height = 28)
    {
        DropdownBinding? binding = null;
        Func<string> text = () => (enabled?.Invoke() ?? true)
            ? items().FirstOrDefault(item => item.Value == current())?.Title ?? current()
            : placeholder?.Invoke() ?? "暂无可用选项";
        Rounded(parent, "Dropdown border", x - 1, y - 1, width + 2, height + 2, 8, Line, 19);
        var background = Rounded(parent, "Dropdown selector", x, y, width, height, 7, Color.white, 20);
        binding = new DropdownBinding(x, y, width, height, current, items, change, background.transform);
        var collider = AddCollider(background.gameObject, width, height);
        var label = Label(parent, text(), x + 12, y, width - 52, height, 17);
        var arrow = Label(parent, "▾", x + width - 31, y, 24, height, 19, Muted, true);
        Action<GameObject> handler = _ =>
        {
            if (_busy || !(enabled?.Invoke() ?? true)) return;
            PollInputs();
            OpenDropdown(binding);
        };
        var native = DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>(handler)
            ?? throw new InvalidOperationException("Cannot bind native dropdown selector.");
        UIEventListener.Get(background.gameObject).onClick = native;
        _delegates.Add(handler);
        _delegates.Add(native);
        _buttons.Add(new ButtonBinding(background, label, collider, text, null, enabled, false, false));
        _refresh.Add(new RefreshBinding(arrow.gameObject, () => SetText(arrow, ReferenceEquals(_openDropdown, binding) ? "▴" : "▾")));
    }

    private void OpenDropdown(DropdownBinding binding)
    {
        if (ReferenceEquals(_openDropdown, binding)) { CloseDropdown(); return; }
        CloseDropdown();
        ReleaseInputFocus();
        var items = binding.Items();
        if (items.Count == 0 || _root == null) return;
        _openDropdown = binding;
        _popupItems = items;

        const int rowHeight = 52, padding = 8, scrollHeight = 28, maxRows = 6;
        var popupWidth = Math.Max(320, binding.Width);
        var anchor = _root.transform.InverseTransformPoint(binding.Anchor.position);
        int anchorX = (int)Math.Round(anchor.x + Width / 2f);
        int anchorY = (int)Math.Round(Height / 2f - anchor.y);
        var popupX = Math.Max(16, Math.Min(anchorX, Width - 16 - popupWidth));
        var below = _panelHeight - 16 - (anchorY + binding.Height + 6);
        var above = anchorY - 6 - 16;
        var preferredRows = Math.Min(maxRows, items.Count);
        var preferredHeight = preferredRows * rowHeight + padding * 2 + (items.Count > preferredRows ? scrollHeight : 0);
        var opensAbove = preferredHeight > below && above > below;
        var room = opensAbove ? above : below;
        var visibleRows = Math.Min(preferredRows, Math.Max(1, (room - padding * 2 - (items.Count > preferredRows ? scrollHeight : 0)) / rowHeight));
        var scrollable = items.Count > visibleRows;
        if (scrollable) visibleRows = Math.Min(visibleRows, Math.Max(1, (room - padding * 2 - scrollHeight) / rowHeight));
        var popupHeight = visibleRows * rowHeight + padding * 2 + (scrollable ? scrollHeight : 0);
        var popupY = opensAbove ? anchorY - popupHeight - 6 : anchorY + binding.Height + 6;
        var selectedIndex = Array.FindIndex(items.ToArray(), item => item.Value == binding.Current());
        _popupOffset = Math.Clamp(selectedIndex, 0, items.Count - visibleRows);

        _popupRoot = Child(_root.transform, "Dropdown floating list");
        _popupRoot.SetActive(false);
        var panel = _popupRoot.AddComponent<UIPanel>();
        panel.depth = Math.Max(_root.GetComponent<UIPanel>().depth + 100, UIPanel.nextUnusedDepth + 1);
        panel.clipping = UIDrawCall.Clipping.None;
        // The top-panel collider consumes the entire outside click before dismissing the list.
        var blocker = Texture(_popupRoot.transform, "Dropdown outside-click blocker", -4400, -4400, 10000, 10000,
            new Color(0, 0, 0, 0.015f), 0);
        AddCollider(blocker.gameObject, 10000, 10000);
        BindPopupClick(blocker.gameObject, CloseDropdown);
        Rounded(_popupRoot.transform, "Dropdown shadow", popupX - 3, popupY + 3, popupWidth + 6,
            popupHeight + 6, 13, new Color(0.06f, 0.17f, 0.28f, 0.16f), 1);
        Rounded(_popupRoot.transform, "Dropdown outline", popupX - 1, popupY - 1, popupWidth + 2,
            popupHeight + 2, 11, Line, 2);
        var surface = Rounded(_popupRoot.transform, "Dropdown paper", popupX, popupY, popupWidth,
            popupHeight, 10, Color.white, 3);
        AddCollider(surface.gameObject, popupWidth, popupHeight);
        BindPopupClick(surface.gameObject, () => { });
        for (var i = 0; i < visibleRows; i++)
        {
            var slot = i;
            var rowY = popupY + padding + i * rowHeight;
            var background = Rounded(_popupRoot.transform, "Dropdown option", popupX + 6, rowY,
                popupWidth - 12, rowHeight - 2, 6, Color.white, 10);
            AddCollider(background.gameObject, popupWidth - 12, rowHeight - 2);
            var title = Label(_popupRoot.transform, "", popupX + 16, rowY + 3, popupWidth - 65, 25, 18, depth: 11);
            var description = Label(_popupRoot.transform, "", popupX + 16, rowY + 28, popupWidth - 40, 20, 14, Muted, depth: 11);
            var check = Label(_popupRoot.transform, "", popupX + popupWidth - 40, rowY + 4, 26, 26, 20, Ink, true, 12);
            _popupRows.Add(new PopupRow(background, title, description, check));
            BindPopupClick(background.gameObject, () =>
            {
                if (_openDropdown == null) return;
                var item = _popupItems[_popupOffset + slot];
                if (!item.Enabled) return;
                var change = _openDropdown.Change;
                CloseDropdown();
                change(item.Value);
                _refreshCadence.Invalidate();
            });
        }
        if (scrollable)
        {
            var scrollY = popupY + padding + visibleRows * rowHeight;
            PopupScrollButton(popupX + 8, scrollY, 40, scrollHeight - 2, "▴", -1);
            PopupScrollButton(popupX + popupWidth - 48, scrollY, 40, scrollHeight - 2, "▾", 1);
            _popupScrollLabel = Label(_popupRoot.transform, "", popupX + 52, scrollY + 2,
                popupWidth - 104, 22, 13, Muted, true, 12);
        }
        RefreshDropdownRows();
        _popupRoot.SetActive(true);
    }

    private void PopupScrollButton(int x, int y, int width, int height, string text, int direction)
    {
        var background = Rounded(_popupRoot!.transform, "Dropdown scroll", x, y, width, height, 5, Paper, 10);
        AddCollider(background.gameObject, width, height);
        Label(_popupRoot.transform, text, x, y, width, height, 18, Muted, true, 12);
        BindPopupClick(background.gameObject, () => ScrollDropdown(direction));
    }

    private void ScrollDropdown(int direction)
    {
        if (_openDropdown == null) return;
        _popupOffset = Math.Clamp(_popupOffset + direction, 0, Math.Max(0, _popupItems.Count - _popupRows.Count));
        RefreshDropdownRows();
    }

    private void RefreshDropdownRows()
    {
        if (_openDropdown == null) return;
        for (var i = 0; i < _popupRows.Count; i++)
        {
            var row = _popupRows[i];
            var item = _popupItems[_popupOffset + i];
            var selected = item.Value == _openDropdown.Current();
            row.Background.color = selected ? new Color(0.88f, 0.96f, 0.99f, 1) : Color.white;
            row.Title.color = item.Enabled ? Ink : Muted;
            row.Background.alpha = item.Enabled ? 1 : 0.5f;
            SetText(row.Title, item.Title);
            SetText(row.Description, item.Description);
            SetText(row.Check, selected ? "✓" : "");
        }
        if (_popupScrollLabel != null)
            SetText(_popupScrollLabel, $"{_popupOffset + 1}–{_popupOffset + _popupRows.Count} / {_popupItems.Count}  ·  滚轮查看更多");
    }

    private void BindPopupClick(GameObject gameObject, Action click)
    {
        Action<GameObject> handler = _ => click();
        var native = DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>(handler)
            ?? throw new InvalidOperationException("Cannot bind native dropdown option.");
        var listener = UIEventListener.Get(gameObject);
        listener.onClick = native;
        _popupListeners.Add(listener);
        _popupDelegates.Add(handler);
        _popupDelegates.Add(native);
    }

    private void CloseDropdown()
    {
        foreach (var listener in _popupListeners) if (listener != null) listener.onClick = null;
        _popupListeners.Clear();
        if (_popupRoot != null)
        {
            _popupRoot.SetActive(false);
            Object.Destroy(_popupRoot);
        }
        _popupRoot = null;
        _openDropdown = null;
        _popupItems = Array.Empty<DropdownItem>();
        _popupRows.Clear();
        _popupScrollLabel = null;
        _popupDelegates.Clear();
    }

    private void Radio(Transform parent, int x, int y, int width, string text, Func<bool> selected, Action change)
    {
        Button(parent, x, y, width, 28, () => text, change, selected: selected);
        var circle = Rounded(parent, "Radio ring", x + 7, y + 7, 14, 14, 7, Muted, 23);
        Rounded(parent, "Radio center", x + 9, y + 9, 10, 10, 5, Color.white, 24);
        var dot = Rounded(parent, "Radio selected", x + 11, y + 11, 6, 6, 3, Ink, 25);
        _refresh.Add(new RefreshBinding(dot.gameObject, () => { dot.alpha = selected() ? 1 : 0; circle.color = selected() ? Ink : Muted; }));
    }

    private void Toggle(Transform parent, int x, int y, int width, string text, Func<bool> read, Action<bool> write, Func<bool>? enabled = null)
        => Button(parent, x, y, width, 36, () => (read() ? "✓  " : "○  ") + text, () => write(!read()), selected: read, enabled: enabled);

    private void Input(Transform parent, int x, int y, int width, int height, Func<string> read, Action<string> write,
        int limit, UIInput.Validation validation = UIInput.Validation.None, Func<bool>? enabled = null)
    {
        Rounded(parent, "Field border", x - 1, y - 1, width + 2, height + 2, 8, Line, 19);
        var background = Rounded(parent, "Editable field", x, y, width, height, 7, Color.white, 20);
        var label = Label(background.transform, "", Width / 2f + 10, Height / 2f,
            width - 20, height, 18);
        label.maxLineCount = 1;
        label.overflowMethod = UILabel.Overflow.ClampContent;
        var collider = AddCollider(background.gameObject, width, height);
        var input = background.gameObject.AddComponent<UIInput>();
        input.label = label;
        input.validation = validation;
        input.characterLimit = limit;
        input.inputType = UIInput.InputType.Standard;
        input.onReturnKey = UIInput.OnReturnKey.Submit;
        input.selectAllTextOnFocus = false;
        input.activeTextColor = Ink;
        input.caretColor = Ink;
        input.selectionColor = new Color(Cyan.r, Cyan.g, Cyan.b, 0.45f);
        input.savedAs = "";
        input.value = read() ?? "";
        if (_fields.Count > 0) _fields[^1].Input.selectOnTab = input.gameObject;
        _fields.Add(new FieldBinding(input, collider, background, read, write, input.value, enabled));
    }

    private Texture2D RoundedShape(int width, int height, int radius, int slant, bool decoration)
    {
        var key = (width, height, radius, slant, decoration);
        if (_shapes.TryGetValue(key, out var cached)) return cached;
        var pixels = new Color[width * height];
        var halfWidth = (width - slant) / 2f;
        var halfHeight = height / 2f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var skew = slant * ((y + 0.5f) / height - 0.5f);
                var dx = Math.Abs(x + 0.5f - width / 2f - skew) - (halfWidth - radius);
                var dy = Math.Abs(y + 0.5f - halfHeight) - (halfHeight - radius);
                var distance = Math.Min(Math.Max(dx, dy), 0) + MathF.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0)) - radius;
                var alpha = Mathf.Clamp01(0.75f - distance);
                var color = Color.white;
                if (decoration)
                {
                    color = new Color(0.978f, 0.988f, 0.996f, 1);
                    if (x + (height - y) * 1.3f < 210) color = new Color(0.913f, 0.956f, 0.989f, 1);
                    if (x + (height - y) * 0.7f < 112) color = new Color(0.942f, 0.974f, 0.997f, 1);
                    if (width - x + y * 1.6f < 260) color = new Color(0.925f, 0.964f, 0.993f, 1);
                    if (width - x + y * 0.7f < 124) color = new Color(0.951f, 0.979f, 0.997f, 1);
                }
                color.a = alpha;
                pixels[y * width + x] = color;
            }
        }
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "AA export original rounded geometry";
        // Scene loading invokes AA's unused-resource sweep. These generated
        // textures remain owned by this persistent panel until Dispose.
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        _shapes.Add(key, texture);
        return texture;
    }

    private void ReleaseInputFocus()
    {
        foreach (var field in _fields)
            if (field.Input != null && field.Input.isSelected) field.Input.isSelected = false;
    }

    private void DestroyHierarchy()
    {
        CloseDropdown();
        ReleaseInputFocus();
        if (_persistentUi != null)
        {
            _persistentUi.SetActive(false);
            Object.Destroy(_persistentUi);
        }
        ReleaseExcludedCameras();
        foreach (var shape in _shapes.Values) if (shape != null) Object.Destroy(shape);
        _shapes.Clear();
        _root = null;
        _persistentUi = null;
        _nativeRoot = null;
        _nativeCamera = null;
        _trueTypeFont = null;
        _bitmapFont = null;
        _modalBlocker = null;
        _progressWidget = null;
        _exportProgressWidget = null;
        _settingsRoot = null;
        _exportProgressRoot = null;
        _cancelConfirmRoot = null;
        _settingsGeometry = null;
        _outputSection = _customDimensionsSection = _superSection = _superControls = _sharpnessSection = null;
        _audioSection = _advancedSection = _advancedEntry = _customBitrateSection = _footerSection = null;
        _settingsContent = _settingsScrollRail = _scrollDownButton = null;
        _settingsViewport = null;
        _scrollTrack = _scrollThumb = null;
        _settingsSliderColliders.Clear();
        _paperSurface = _outerShadow = _panelShadow = _outputCard = _superCard = null;
        _sharpnessSlider = null;
        _displayedPhase = null;
        _layoutDirty = true;
        _nextCameraScan = 0;
        _refreshCadence.Invalidate();
        _fields.Clear();
        _buttons.Clear();
        _refresh.Clear();
        _delegates.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        RenderControlV1.EnsureMainThread();
        if (!_busy)
        {
            PollInputs();
            CommitOutputDirectory();
        }
        _disposed = true;
        try
        {
            ReleaseIdleBudget();
            DestroyHierarchy();
            _visible = false;
        }
        catch { RenderControlV1.RestorationFailed(); throw; }
        // A release observer error does not mean Unity restoration failed.
        // The provider already reports IsReady=false, IsRenderingOwned=false.
        ReleaseRenderOwnership();
    }

    private void ReleaseRenderOwnership()
    {
        var ownership = _renderOwnership;
        _renderOwnership = null;
        ownership?.Dispose();
    }

    private void ReleaseExcludedCameras()
    {
        try
        {
            int mask = 1 << _layer;
            foreach (var camera in _excludedCameras)
                if (camera != null) camera.cullingMask |= mask;
            _excludedCameras.Clear();
        }
        catch { RenderControlV1.RestorationFailed(); throw; }
    }

    private sealed record ButtonBinding(UITexture Background, UILabel Label, BoxCollider Collider, Func<string> Text,
        Func<bool>? Selected, Func<bool>? Enabled, bool Primary, bool AllowBusy);
    private sealed record RefreshBinding(GameObject Owner, Action Refresh);

    private sealed record DropdownItem(string Value, string Title, string Description, bool Enabled = true);
    private sealed record DropdownBinding(int X, int Y, int Width, int Height, Func<string> Current,
        Func<IReadOnlyList<DropdownItem>> Items, Action<string> Change, Transform Anchor);
    private sealed record PopupRow(UITexture Background, UILabel Title, UILabel Description, UILabel Check);

    private sealed class FieldBinding
    {
        public UIInput Input { get; }
        public BoxCollider Collider { get; }
        public UITexture Background { get; }
        public Func<string> Read { get; }
        public Action<string> Write { get; }
        public Func<bool>? Enabled { get; }
        public string LastValue { get; set; }
        public FieldBinding(UIInput input, BoxCollider collider, UITexture background, Func<string> read, Action<string> write, string value, Func<bool>? enabled)
            => (Input, Collider, Background, Read, Write, LastValue, Enabled) = (input, collider, background, read, write, value, enabled);
    }
}
