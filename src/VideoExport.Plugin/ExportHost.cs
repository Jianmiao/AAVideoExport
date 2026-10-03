using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

public sealed class ExportHost : MonoBehaviour
{
    internal static ExportHost? Current;
    internal static string LoadedStoryTitle = "";
    private readonly NativeExportPanel _panel = new();
    private CatalogExportAction? _catalogAction;
    private string _lastUiError = "";
    private readonly Stopwatch _elapsed = new();
    private FfmpegCapabilities? _capabilities;
    private Task<FfmpegCapabilities>? _probe;
    private CancellationTokenSource? _probeCancellation;
    private bool _probeAttempted;
    private Task<ExportSession>? _prepare;
    private Task<ExportResult>? _finalize;
    private Task? _cleanup;
    private ExportResult? _cleanupCommittedResult;
    private CancellationTokenSource? _cancellation;
    private ExportSession? _session;
    private FramePipeline? _frames;
    private NativeCaptureScope? _native;
    private Test? _player;
    private ExportOptions? _options;
    private string _status = "选择剧情和视频参数，点击导出即可自动渲染。";
    private string _gpuName = "";
    private int _lastUnityFrame = -1;
    private long _captured;
    private long _endAt = -1;
    private float _uiEpoch;
    private bool _armed;
    private bool _shutdown;
    private bool _showButtons;
    private CatalogFileInfo? _selectedStory;
    private string _selectedStoryKey = "";
    private CatalogFileInfo? _loadingStory;
    private Coroutine? _loadingCoroutine;
    private GenericScenarioExcelTable? _frozenSave;
    private readonly PlaybackLaunchGate _launch = new();
    private long _launchTicket;
    private readonly Stopwatch _loadingTimer = new();
    private IntPtr _eligiblePlayer;
    private long _eligibleTicket;
    private bool _nativeLoadPending, _nativeLoadCancelled, _returningToCatalog;
    private Test? _cancelledPlayback;
    private bool _ownsInput;
    private bool _originalInput;
    private Coroutine? _captureLoop;
    private Il2CppSystem.Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>? _endFrame;
    private bool _automaticCaptureAvailable = true;
    private bool _failureHandling;
    private long _uiTicks, _renderTicks, _audioTicks;
    private double _lastPerfReport;

    internal bool Capturing => _native != null;
    internal bool AllowTouch => !Capturing && !_panel.Visible;
    internal float FrameDelta => _options == null ? 0 : 1f / _options.Fps;
    internal float UiClock => _uiEpoch + _captured * FrameDelta;
    private bool Busy => _prepare != null || _finalize != null || _cleanup != null || _armed || Capturing
        || _nativeLoadPending || _returningToCatalog;

    public ExportHost(IntPtr pointer) : base(pointer) { }

    public void Awake()
    {
        Current = this;
        _gpuName = SystemInfo.graphicsDeviceName;
        _panel.Options = new ExportOptions { Title = "", OutputDirectory = Plugin.OutputDirectory };
        _panel.Visible = false;
        _panel.OutputDirectoryCommitted = Plugin.RememberOutputDirectory;
        _panel.ProbeRequested = Probe;
        _panel.ExportRequested = Prepare;
        _panel.CancelRequested = Cancel;
        try
        {
            _endFrame = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>>(
                (Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>)((_, _) => AfterRenderPipelineFrame()));
            RenderPipelineManager.add_endFrameRendering(_endFrame);
        }
        catch (Exception error)
        {
            // Some AA IL2CPP builds do not expose the URP event. Built-in
            // FireOnPostRender capture and the catalog toolbar remain usable.
            _endFrame = null;
            _automaticCaptureAvailable = GraphicsSettings.currentRenderPipeline == null;
            UnityEngine.Debug.LogWarning("AA Video Export: URP frame callback unavailable (" + error.GetType().Name + "); using camera callback fallback.");
        }
        _catalogAction = new CatalogExportAction(info =>
        {
            if (!TryGetStoryPath(info, out var storyPath)) return;
            _selectedStory = info;
            _selectedStoryKey = storyPath;
            LoadedStoryTitle = Path.GetFileNameWithoutExtension(storyPath);
            _panel.SetStoryTitle(LoadedStoryTitle);
            _panel.SetSourceCanvas(Screen.width, Screen.height);
            _panel.Visible = true;
        });
    }

    public void Update()
    {
        if (_shutdown) return;
        try
        {
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.E))
            {
                if (!_panel.Visible) _panel.SetSourceCanvas(Screen.width, Screen.height);
                _panel.Visible = !_panel.Visible;
            }
            long uiStarted = Stopwatch.GetTimestamp();
            SyncNativeUi();
            if (Capturing) _uiTicks += Stopwatch.GetTimestamp() - uiStarted;
            if (_cancelledPlayback != null)
            {
                var abandonedPlayer = _cancelledPlayback; _cancelledPlayback = null;
                _nativeLoadPending = false;
                _nativeLoadCancelled = false;
                _loadingCoroutine = null; _loadingStory = null; _frozenSave = null;
                _returningToCatalog = true;
                BestEffort(() => abandonedPlayer.End(), "ending cancelled playback");
            }
            // A newly loaded catalog has no selected card yet. Waiting for a
            // populated block made cancellation depend on another user click.
            if (_returningToCatalog && SceneManager.GetActiveScene().name == "CatalogScene" &&
                Object.FindObjectsOfType<CatalogFileInfo>().Any(info => info != null && info.gameObject.activeInHierarchy))
            {
                _returningToCatalog = false;
                _panel.Visible = true;
                SyncNativeUi();
            }
            if (_nativeLoadPending && _nativeLoadCancelled && _loadingTimer.Elapsed.TotalSeconds >= 120)
                _status = "编码已取消，但 AA 的场景加载仍未结束。请重启 AA 后重试，避免重复加载剧情。";
            if (_cleanup?.IsCompleted == true)
            {
                try { _cleanup.GetAwaiter().GetResult(); }
                catch (Exception error) { _status = "停止导出后清理未完成，请检查输出目录。"; UnityEngine.Debug.LogWarning("AA Video Export: cleanup failed (" + error.GetType().Name + ")."); }
                _cleanup = null;
                if (_cleanupCommittedResult != null)
                {
                    ReportCompleted(_cleanupCommittedResult);
                    _cleanupCommittedResult = null;
                }
                _panel.Visible = true;
            }
            if (_probe?.IsCompleted == true)
            {
                try
                {
                    _capabilities = _probe.GetAwaiter().GetResult();
                    _status = _capabilities.HardwareEncoders.Count == 0
                        ? "未检测到可用 GPU 硬件编码，请检查显卡驱动和 FFmpeg。不会自动改用 CPU。"
                        : "GPU 检测完成，仅列出本机实测可用的硬件编码。";
                }
                catch (Exception error) { _capabilities = null; _status = "GPU 检测失败，请检查 FFmpeg 与驱动后重新检测。"; UnityEngine.Debug.LogWarning("AA Video Export: GPU probe failed (" + error.GetType().Name + ")."); }
                _probe = null;
            }
            if (_prepare?.IsCompleted == true)
            {
                var work = _prepare; _prepare = null;
                _session = work.GetAwaiter().GetResult();
                if (!_launch.CompletePreparation(_launchTicket, key => {
                    _armed = true;
                    _eligiblePlayer = IntPtr.Zero;
                    _panel.Visible = true;
                    _status = "正在自动加载所选剧情与素材…";
                    if (_loadingStory == null || !_loadingStory.gameObject.activeInHierarchy ||
                        !string.Equals(_selectedStoryKey, key, StringComparison.OrdinalIgnoreCase))
                        throw new ExportException("story_selection_changed", "所选剧情已变化，请返回鉴赏列表重新选择后导出。");
                    PersistentData.saveData = _frozenSave;
                    PersistentData.projectPath = key;
                    _loadingTimer.Restart();
                    _nativeLoadPending = true;
                    _nativeLoadCancelled = false;
                    try { _loadingCoroutine = _loadingStory.StartCoroutine(_loadingStory.CoLoadSave()); }
                    catch { _nativeLoadPending = false; throw; }
                })) { _session.Dispose(); _session = null; }
            }
            if (_armed) _launch.CheckTimeout(_loadingTimer.Elapsed.TotalSeconds);
            if (_finalize?.IsCompleted == true)
            {
                var work = _finalize; _finalize = null;
                try
                {
                    var result = work.GetAwaiter().GetResult();
                    ReportCompleted(result);
                }
                finally { _session?.Dispose(); _session = null; _elapsed.Stop(); _panel.Visible = true; }
            }
            if (!Capturing) return;
            _native!.ExcludeOverlayLayer(_panel.ExcludeFrom);
            _native.PrepareAutomaticFrame();
            if (_captured == 0 && _elapsed.Elapsed.TotalSeconds > 10)
                throw new ExportException("capture_start_timeout", "逐帧渲染未启动，导出已停止。请查看 Mod 兼容性诊断。");
            _frames!.Drain(false);
            if (_player == null) throw new ExportException("player_closed", "剧情窗口已关闭，导出取消。");
            if (_endAt >= 0) return;
            if (ScenarioResourceManager.Instance != null && ScenarioResourceManager.Instance.Preloading)
                throw new ExportException("assets_not_ready", "剧情仍在加载素材。请先完整预加载，再从头导出，避免把加载时间混进视频。");
        }
        catch (Exception error) { Fail(error); }
        finally { UpdateInputOwnership(); }
    }

    [HideFromIl2Cpp]
    private void SyncNativeUi()
    {
        try
        {
            if (_panel.Visible && !_probeAttempted) Probe();
            _panel.IsCapturing = Capturing;
            _panel.ReduceCaptureUiWork = Plugin.ReduceProgressUiWork;
            _catalogAction?.Tick(_panel.Visible || Capturing);
            string title = LoadedStoryTitle;
            if (string.IsNullOrWhiteSpace(title) && Studio.Scripts.StudioCommon.instance != null &&
                !string.IsNullOrWhiteSpace(Studio.Scripts.StudioCommon.instance.projectName))
                title = Studio.Scripts.StudioCommon.instance.projectName;
            if (string.IsNullOrWhiteSpace(title)) title = "AA视频";
            string details = _status;
            if (Capturing)
            {
                double speed = _captured / Math.Max(.001, _elapsed.Elapsed.TotalSeconds);
                details = $"正在渲染 · {speed:0} 帧/秒 · {speed / _options!.Fps:0.0}× 实时速度 · 缓冲 {_frames!.BufferedFrames} 帧";
            }
            IReadOnlyList<EncoderCapability>? hardware = _probe != null || !_probeAttempted
                ? null : _capabilities?.HardwareEncoders ?? Array.Empty<EncoderCapability>();
            _panel.Draw(title, _gpuName, details.Replace('\n', ' '), Busy, _captured, -1, null, hardware);
            _panel.CaptureElapsedSeconds = _elapsed.Elapsed.TotalSeconds;
            _panel.EncodedFrames = _session?.FramesWritten ?? _captured;
            _panel.ExportStage = _cleanup != null || _nativeLoadCancelled ? "正在取消" : _returningToCatalog ? "返回导出设置" : _prepare != null ? "准备编码器" : _armed ? "加载剧情与素材" : Capturing ? "渲染画面与音频" : _finalize != null ? "合成并校验视频" : "准备就绪";
            _panel.Tick();
            _lastUiError = "";
        }
        catch (Exception error)
        {
            _status = "导出面板错误：" + ExportErrorText.Describe(error);
            string fingerprint = error.GetType().Name + ":" + error.Message;
            if (fingerprint != _lastUiError)
                UnityEngine.Debug.LogError("AA Video Export native panel exception: " + error);
            _lastUiError = fingerprint;
        }
    }

    [HideFromIl2Cpp]
    private void Probe()
    {
        if (_probe != null || Busy) return;
        _probeAttempted = true;
        _capabilities = null;
        _probeCancellation?.Dispose();
        _probeCancellation = new CancellationTokenSource();
        _status = "正在检测本机 GPU 硬件编码（短片测试，不读取剧情）…";
        _probe = FfmpegCapabilities.ProbeHardwareAsync(Plugin.FfmpegPath, _gpuName, _probeCancellation.Token);
    }

    [HideFromIl2Cpp]
    private void Prepare(ExportOptions options)
    {
        if (Busy) return;
        try
        {
            options = options with { RenderGpuVendorId = SystemInfo.graphicsDeviceVendorID, RenderGpuDeviceId = SystemInfo.graphicsDeviceID, Direct3DReadbackFrames = Plugin.Direct3DReadbackFrames };
            options.Validate();
            if (_capabilities == null) { Probe(); _status = "请先等待编码器检测完成，再点击导出。"; return; }
            var (selected, storyKey) = ResolveStory();
            if (!File.Exists(storyKey)) throw new ExportException("story_missing", "所选剧情文件不存在，请刷新鉴赏列表。");
            if (UserSettings.Instance == null || ScenarioResourceManager.Instance == null)
                throw new ExportException("story_resources_unavailable", "AA 的剧情资源服务尚未就绪，请稍后重试。");
            string savesRoot = Path.GetFullPath(Path.Combine(UserSettings.Instance.WorkspacePath, "data", "saves"));
            if (!string.Equals(Path.GetDirectoryName(storyKey), Path.TrimEndingDirectorySeparator(savesRoot), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(storyKey), ".aas", StringComparison.OrdinalIgnoreCase))
                throw new ExportException("story_location_unsupported", "请选择当前 AA 工作区鉴赏列表中的剧情文件。");
            // The native loader accepts a basename and resolves data/saves itself.
            // Read the selected file afresh; a global table can belong to a different card.
            var save = ScenarioResourceManager.Instance.LoadGenericScenario(Path.GetFileNameWithoutExtension(storyKey));
            if (save == null) throw new ExportException("story_read_failed", "所选剧情无法读取，请检查文件后重试。");
            _launchTicket = _launch.Begin(storyKey);
            _selectedStory = selected;
            _selectedStoryKey = storyKey;
            _loadingStory = selected;
            _frozenSave = save;
            var canvas = options.GetCanvasLayout();
            if (canvas.CaptureWidth > SystemInfo.maxTextureSize || canvas.CaptureHeight > SystemInfo.maxTextureSize)
                throw new ExportException("gpu_resolution_limit", "所选画布超过当前显卡的纹理尺寸上限。");
            _options = options;
            // Playback timing belongs to AA. Always start from the selected
            // story's beginning and let native Auto/voice/animation timing
            // drive the run until Test.End().
            _showButtons = _panel.ShowPlaybackButtons;
            _captured = 0; _endAt = -1;
            _elapsed.Reset();
            _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
            string encoder = _capabilities.PickHardwareEncoder(options);
            int sampleRate = AudioSettings.outputSampleRate;
            int channels = AudioSettings.speakerMode switch { AudioSpeakerMode.Mono => 1, AudioSpeakerMode.Stereo => 2, AudioSpeakerMode.Quad => 4, AudioSpeakerMode.Surround => 5, AudioSpeakerMode.Mode5point1 => 6, AudioSpeakerMode.Mode7point1 => 8, _ => 0 };
            if (channels == 0) throw new ExportException("audio_layout_unsupported", "当前 Unity 音频输出布局不支持导出。");
            string encoderVendor = encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? "NVIDIA"
                : encoder.EndsWith("_amf", StringComparison.Ordinal) ? "AMD" : "Intel";
            _status = $"正在检查 {encoderVendor} 硬件编码器，画面 {options.Width}×{options.Height}…";
            var token = _cancellation.Token;
            _prepare = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var session = new ExportSession(options, Plugin.FfmpegPath, Plugin.FfprobePath, encoder, sampleRate, channels, true);
                if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
                return session;
            });
        }
        catch (Exception error) { Fail(error); }
    }

    [HideFromIl2Cpp]
    private (CatalogFileInfo Loader, string Path) ResolveStory()
    {
        var selected = _selectedStory;
        string key = _selectedStoryKey;
        if (string.IsNullOrEmpty(key) && TryGetStoryPath(selected, out var selectedPath))
            key = selectedPath;
        var available = Object.FindObjectsOfType<CatalogFileInfo>()
            .Where(info => info != null && info.gameObject.activeInHierarchy).ToArray();
        if (string.IsNullOrEmpty(key))
        {
            selected = available.FirstOrDefault(info => info.controlPanel != null &&
                info.controlPanel.activeInHierarchy && TryGetStoryPath(info, out _));
            if (TryGetStoryPath(selected, out selectedPath)) key = selectedPath;
        }
        if (string.IsNullOrEmpty(key)) throw new ExportException("story_not_selected", "请先在鉴赏列表选中要导出的剧情。");
        // Native CoLoadSave uses PersistentData.saveData; its UI card is only
        // the coroutine owner. Retain the stable source path across scene reload.
        if (selected == null || !selected.gameObject.activeInHierarchy)
            selected = available.FirstOrDefault(info => TryGetStoryPath(info, out var candidate) &&
                string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) ?? available.FirstOrDefault();
        if (selected == null) throw new ExportException("catalog_not_ready", "AA 正在恢复导出设置，请稍候重试。");
        return (selected, key);
    }

    [HideFromIl2Cpp]
    private static bool TryGetStoryPath(CatalogFileInfo? info, out string path)
    {
        path = "";
        if (info == null || info.block == null || string.IsNullOrWhiteSpace(info.block.path)) return false;
        try
        {
            path = Path.GetFullPath(info.block.path);
            return string.Equals(Path.GetExtension(path), ".aas", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { path = ""; return false; }
        catch (NotSupportedException) { path = ""; return false; }
    }

    [HideFromIl2Cpp]
    internal void MarkNewPlayback(Test player)
    {
        if (!_nativeLoadPending || player.previewMode) return;
        if (_nativeLoadCancelled) { _cancelledPlayback = player; return; }
        if (_armed) { _eligiblePlayer = player.Pointer; _eligibleTicket = _launchTicket; }
    }

    [HideFromIl2Cpp]
    internal void BeforePlayerStart(Test player)
    {
        if (!_armed || _eligiblePlayer == IntPtr.Zero || player.Pointer != _eligiblePlayer || player.previewMode || player.scn == null) return;
        try
        {
            if (!_launch.AcceptPlayback(_eligibleTicket, Path.GetFullPath(PersistentData.projectPath))) return;
            _nativeLoadPending = false;
            Begin(player);
            _loadingCoroutine = null; _loadingStory = null; _frozenSave = null;
        }
        catch (Exception error) { Fail(error); }
    }

    [HideFromIl2Cpp]
    private void Begin(Test player)
    {
        if (_session == null || _options == null) throw new ExportException("session_missing", "编码任务未准备。");
        _armed = false;
        _eligiblePlayer = IntPtr.Zero;
        _player = player;
        _uiEpoch = RealTime.time;
        _panel.ReleaseIdleBudget();
        // Keep the same native 1x pixels, but always use the bounded target ring.
        // The former 1080p single-target path allowed a new readback request to
        // pile onto the same RenderTexture while the previous request was still
        // in flight. That serializes the GPU/CPU boundary on long exports and
        // is exactly the opposite of the editor-style render pipeline we want.
        _frames = new FramePipeline(_options, _session, Plugin.UseAsyncReadback, nativeBuffers: Plugin.UseNativeFrameBuffers);
        _native = new NativeCaptureScope(player, _options, _frames.Target, _showButtons, _automaticCaptureAvailable);
        NativeExportClock.Begin(_uiEpoch, FrameDelta, () => UiClock);
        UpdateInputOwnership();
        _lastUnityFrame = -1;
        _elapsed.Restart();
        _uiTicks = _renderTicks = _audioTicks = 0;
        _panel.ResetPerformanceCounters();
        _lastPerfReport = 0;
        _status = "逐帧导出中…";
        _panel.Visible = true;
        UnityEngine.Debug.Log("AA Video Export: capture started; pipeline=" + (GraphicsSettings.currentRenderPipeline == null ? "builtin" : GraphicsSettings.currentRenderPipeline.GetType().Name));
        StartCaptureLoop(player);
    }

    [HideFromIl2Cpp]
    private void StartCaptureLoop(Test player)
    {
        var collections = typeof(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions);
        var wrap = collections.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "WrapToIl2Cpp" && !m.IsGenericMethod && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(IEnumerator));
        if (wrap == null) throw new MissingMethodException("WrapToIl2Cpp(IEnumerator)");
        object wrapped = wrap.Invoke(null, new object[] { CaptureLoopFactory.Create(this) })!;
        var il2CppEnumerator = typeof(Il2CppSystem.Collections.IEnumerator);
        var start = player.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "StartCoroutine" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == il2CppEnumerator);
        if (start == null)
            start = typeof(MonoBehaviour).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "StartCoroutine" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == il2CppEnumerator);
        if (start == null) throw new MissingMethodException("Test.StartCoroutine(Il2CppSystem.Collections.IEnumerator)");
        _captureLoop = (Coroutine)start.Invoke(player, new[] { wrapped })!;
    }

    internal void CaptureLoopFinished() => _captureLoop = null;
    internal void CaptureLoopFrame()
    {
        if (!Capturing || _native!.AutomaticCameraCapture) return;
        _native.RestoreAutomaticFrame();
        CaptureFrame(frameAlreadyRendered: false);
    }

    [HideFromIl2Cpp]
    internal void AfterCamera(Camera camera)
    {
        if (!Capturing || GraphicsSettings.currentRenderPipeline != null || !_native!.AutomaticCameraCapture || _native.LastCamera != camera) return;
        _native.RestoreAutomaticFrame();
        CaptureFrame(frameAlreadyRendered: true);
    }

    [HideFromIl2Cpp]
    internal void AfterRenderPipelineFrame()
    {
        if (!Capturing || GraphicsSettings.currentRenderPipeline == null || !_native!.AutomaticCameraCapture) return;
        _native.RestoreAutomaticFrame();
        CaptureFrame(frameAlreadyRendered: true);
    }

    [HideFromIl2Cpp]
    private void CaptureFrame(bool frameAlreadyRendered = false)
    {
        if (!Capturing || _lastUnityFrame == Time.frameCount) return;
        _lastUnityFrame = Time.frameCount;
        try
        {
            long stage = Stopwatch.GetTimestamp();
            var target = _frames!.RenderTarget;
            if (!frameAlreadyRendered)
            {
                _native!.ExcludeOverlayLayer(_panel.ExcludeFrom);
                // Waiting for a free GPU slot has its own metric; do not count it
                // again as camera rendering.
                stage = Stopwatch.GetTimestamp();
                _native.RenderFrame(target);
                _renderTicks += Stopwatch.GetTimestamp() - stage;
            }
            _frames!.Capture();
            if (frameAlreadyRendered && Capturing)
                _native!.SetTarget(_frames.RenderTarget);
            stage = Stopwatch.GetTimestamp();
            var audio = _native!.RenderAudio();
            if (_options!.AudioQuality != "none") _session!.WriteAudio(audio.Samples, audio.Count);
            _audioTicks += Stopwatch.GetTimestamp() - stage;
            _captured++;
            ReportPerformance(false);
            if (_captured == 1) UnityEngine.Debug.Log("AA Video Export: first frame and audio captured.");
            if (_endAt >= 0) Finish();
        }
        catch (Exception error) { Fail(error); }
    }

    [HideFromIl2Cpp]
    internal bool BeforePlayerEnd(Test player)
    {
        if (!Capturing || _player != player) return true;
        if (_endAt < 0) _endAt = _captured;
        return false;
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        ReportPerformance(true);
        var writerDone = _frames!.FinishFrames();
        UnityEngine.Debug.Log($"AA Video Export: rendering finished; frames={_captured}, waits={NativeExportClock.WaitPollCount}, unscaled={NativeExportClock.UnscaledDeltaReadCount}.");
        var session = _session!;
        var token = _cancellation!.Token;
        RestoreNativeCapture();
        _frames.Dispose(); _frames = null;
        _finalize = Task.Run(async () => { await writerDone.ConfigureAwait(false); return session.Complete(token); });
        _status = "画面已渲染完，正在合成音频和验证视频…";
        _panel.Visible = true;
        // End exactly once after the export hook has become inert.
        var completedPlayer = _player; _player = null;
        _returningToCatalog = completedPlayer != null;
        if (completedPlayer != null) BestEffort(() => completedPlayer.End(), "returning after playback");
    }

    [HideFromIl2Cpp]
    private void Fail(Exception error)
    {
        if (_failureHandling) return;
        _failureHandling = true;
        try
        {
            UnityEngine.Debug.LogError("AA Video Export: failure=" + (error is ExportException e ? e.Code : error.GetType().Name) + "\n" + error);
            try { Cancel(); }
            catch (Exception cleanupError) { UnityEngine.Debug.LogWarning("AA Video Export: failure cleanup failed (" + cleanupError.GetType().Name + ")."); }
            _status = "导出停止：" + ExportErrorText.Describe(error);
            _panel.Visible = true;
        }
        finally { _failureHandling = false; }
    }

    [HideFromIl2Cpp]
    private void Cancel()
    {
        if (_cleanup != null) return;
        try { ReportPerformance(true); }
        catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: performance report skipped during cancellation (" + error.GetType().Name + ")."); }
        _armed = false;
        _launch.Cancel();
        _eligiblePlayer = IntPtr.Zero;
        // Detach asynchronous work first. Native cleanup must never prevent the
        // encoder from being cancelled or a late result from being disposed.
        var preparing = _prepare; _prepare = null;
        var finishing = _finalize; _finalize = null;
        var session = _session; _session = null;
        var frames = _frames; _frames = null;
        var writer = frames?.WriterCompletion;
        var player = _player;
        _nativeLoadCancelled = _nativeLoadPending;
        BestEffort(() => _cancellation?.Cancel(), "cancelling export token");
        BestEffort(() => session?.Cancel(), "cancelling encoder");
        BestEffort(RestoreNativeCapture, "restoring native capture");
        _player = null;
        BestEffort(() => frames?.Dispose(), "disposing frame buffers");
        if (!_nativeLoadPending)
        {
            _loadingCoroutine = null; _loadingStory = null; _frozenSave = null;
            if (player != null)
            {
                _returningToCatalog = true;
                BestEffort(() => player.End(), "ending cancelled playback");
            }
        }
        // Unity LoadSceneAsync cannot be cancelled by StopCoroutine. Keep the
        // loader alive so its late Test.Start can be rejected and ended, and do
        // not allow a retry to adopt that old player as a new capture.
        _cleanupCommittedResult = null;
        if (preparing != null || finishing != null || writer != null || session != null)
        {
            _cleanup = Task.Run(async () =>
            {
                try
                {
                    if (preparing != null)
                    {
                        try { (await preparing.ConfigureAwait(false)).Dispose(); }
                        catch (Exception) { /* Failed preparation owns its rollback. */ }
                    }
                    if (writer != null) { try { await writer.ConfigureAwait(false); } catch (Exception) { } }
                    if (finishing != null)
                    {
                        try { _cleanupCommittedResult = await finishing.ConfigureAwait(false); }
                        catch (Exception) { }
                    }
                }
                finally { session?.Dispose(); }
            });
        }
        _elapsed.Stop();
        _panel.Visible = true;
        _status = _nativeLoadPending ? "编码已取消，正在等待 AA 完成当前场景加载并返回设置…"
            : "已取消。原工程未修改，导出设置已保留。";
    }

    [HideFromIl2Cpp]
    private void ReportCompleted(ExportResult result) =>
        _status = $"完成：{result.OutputPath}\n{result.Frames:N0} 帧 / {result.DurationSeconds:0.0} 秒；总耗时 {_elapsed.Elapsed.TotalSeconds:0.0} 秒";

    [HideFromIl2Cpp]
    private void ReportPerformance(bool final)
    {
        if (_frames == null || _options == null || _captured == 0) return;
        double seconds = _elapsed.Elapsed.TotalSeconds;
        if (!final && seconds - _lastPerfReport < 5) return;
        _lastPerfReport = seconds;
        double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency / _captured;
        // Aggregate counters only. No story text, resource identifiers or paths.
        // Encoder time overlaps the main thread and is not part of its sum.
        var metrics = new
        {
            final, frames = _captured, output_fps = _options.Fps,
            wall_seconds = Math.Round(seconds, 3),
            captured_fps = Math.Round(_captured / Math.Max(seconds, .001), 2),
            ui_ms = Math.Round(Ms(_uiTicks), 3), render_ms = Math.Round(Ms(_renderTicks), 3),
            visibility_ms = Math.Round(Ms(_native?.VisibilityTicks ?? 0), 3),
            camera_render_ms = Math.Round(Ms(_native?.CameraRenderTicks ?? 0), 3),
            visibility_refreshes = _native?.PanelRefreshCount ?? 0,
            readback_ms = Math.Round(Ms(_frames.ReadbackTicks), 3), raw_data_ms = Math.Round(Ms(_frames.RawDataTicks), 3),
            copy_ms = Math.Round(Ms(_frames.CopyTicks), 3), queue_ms = Math.Round(Ms(_frames.QueueTicks), 3),
            async_readback = _frames.AsyncReadback, readback_wait_ms = Math.Round(Ms(_frames.ReadbackWaitTicks), 3),
            audio_ms = Math.Round(Ms(_audioTicks), 3), encoder_worker_ms = Math.Round(Ms(_frames.EncoderTicks), 3),
            buffered_frames = _frames.BufferedFrames, peak_buffered_frames = _frames.PeakBufferedFrames,
            pending_readbacks = _frames.PendingReadbacks, peak_pending_readbacks = _frames.PeakPendingReadbacks,
            queue_capacity = _frames.QueueCapacity, render_targets = _frames.TargetCount,
            native_buffer_queue = _frames.NativeBufferQueue, native_buffers_allocated = _frames.AllocatedNativeBuffers,
            capture_bytes = _frames.CaptureWidth * _frames.CaptureHeight * 4,
            capture_mode = _options.CaptureMode,
            super_resolution_enabled = _options.SuperResolutionEnabled,
            super_resolution_tier = _options.SuperResolutionTier,
            upscale_algorithm = _options.UpscaleAlgorithm,
            upscale_execution = _session?.UpscaleExecution ?? "none",
            upscale_readback_frames_requested = _options.Direct3DReadbackFrames,
            upscale_readback_frames = _session?.UpscaleReadbackFrames ?? 0,
            accepted_frames = _session?.FramesAccepted ?? 0,
            encoded_frames = _session?.FramesWritten ?? 0,
            upscale_adapter = _session?.UpscaleAdapter ?? "",
            upscale_fallback = _session?.UpscaleFallbackReason ?? "",
            rcas_sharpness = _options.RcasSharpness,
            capture_width = _frames.CaptureWidth, capture_height = _frames.CaptureHeight,
            output_width = _options.Width, output_height = _options.Height,
            frame_limit = Application.targetFrameRate,
            render_frame_interval = OnDemandRendering.renderFrameInterval,
            unity_frame = Time.frameCount, focused = Application.isFocused,
            ui_reduced = Plugin.ReduceProgressUiWork, ui_refreshes = _panel.ControlRefreshCount, camera_scans = _panel.CameraScanCount,
            vsync = QualitySettings.vSyncCount, capture_delta = Time.captureDeltaTime,
            wait_polls = NativeExportClock.WaitPollCount, registered_waits = NativeExportClock.RegisteredWaitCount,
            unscaled_reads = NativeExportClock.UnscaledDeltaReadCount,
            active_waits = NativeExportClock.ActiveWaitCount, peak_active_waits = NativeExportClock.PeakActiveWaitCount,
            audio_sample_frames = _session?.AudioSampleFrames ?? 0
        };
        string serialized = System.Text.Json.JsonSerializer.Serialize(metrics);
        UnityEngine.Debug.Log("AA Video Export: performance=" + serialized);
        Plugin.LogPerformance("performance=" + serialized);
    }

    [HideFromIl2Cpp]
    private static void BestEffort(Action cleanup, string operation)
    {
        try { cleanup(); }
        catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: " + operation + " failed (" + error.GetType().Name + ")."); }
    }

    [HideFromIl2Cpp]
    private void RestoreNativeCapture()
    {
        if (_native == null) return;
        float exportClock = UiClock;
        var scope = _native;
        _native = null; // Clock hooks must be inert before reading the restored real clock.
        BestEffort(NativeExportClock.Restore, "restoring native clock");
        if (_captureLoop != null)
        {
            try { _player?.StopCoroutine(_captureLoop); } catch { }
            _captureLoop = null;
        }
        BestEffort(scope.Dispose, "restoring native settings");
        try
        {
            float offset = RealTime.time - exportClock;
            // Active NGUI tweens contain absolute timestamps from our virtual clock.
            // Rebase them so cancellation cannot leave them waiting for wall time to catch up.
            foreach (var tween in Object.FindObjectsOfType<UITweener>())
                if (tween != null && tween.ignoreTimeScale && tween.mStarted) tween.mStartTime += offset;
        }
        catch (Exception error)
        {
            // Scene teardown must still release the encoder and GPU resources.
            UnityEngine.Debug.LogWarning("AA Video Export: tween clock restoration unavailable (" + error.GetType().Name + ").");
        }
    }

    [HideFromIl2Cpp]
    private void UpdateInputOwnership()
    {
        // Native panel uses NGUI input and its own modal blocker. Global input
        // suppression would disable the export controls as well as AA's controls.
        bool needsInput = !_shutdown && Capturing && !_panel.Visible;
        if (needsInput && !_ownsInput)
        {
            _originalInput = UICamera.ignoreAllEvents;
            _ownsInput = true;
            UICamera.ignoreAllEvents = true;
        }
        else if (!needsInput && _ownsInput)
        {
            UICamera.ignoreAllEvents = _originalInput;
            _ownsInput = false;
        }
    }

    [HideFromIl2Cpp]
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        _probeCancellation?.Cancel();
        Cancel();
        _catalogAction?.Dispose();
        _catalogAction = null;
        _panel.Dispose();
        if (_endFrame != null)
        {
            try { RenderPipelineManager.remove_endFrameRendering(_endFrame); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: URP callback cleanup unavailable (" + error.GetType().Name + ")."); }
        }
        UpdateInputOwnership();
        Current = null;
    }
    public void OnDestroy() => Shutdown();
    public void OnApplicationQuit() => Shutdown();
}
