using System.Collections;
using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost : MonoBehaviour
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
    private string _probeMode = "", _capabilitiesMode = "";
    private Task<ExportSession>? _prepare;
    private Task<ExportResult>? _finalize;
    private Task? _cleanup;
    private ExportResult? _cleanupCommittedResult;
    private CancellationTokenSource? _cancellation;
    private ExportSession? _session;
    private FramePipeline? _frames;
    private NativeCaptureScope? _native;
    private NativeAutoSelection? _autoSelection;
    private Test? _player;
    private ExportOptions? _options;
    private string _status = "选择剧情和视频参数，点击导出即可自动渲染。";
    private bool _statusIsError;
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
    private Il2CppSystem.Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>? _beginFrame;
    private Il2CppSystem.Action<ScriptableRenderContext, Camera>? _beginCamera;
    private bool _automaticCaptureAvailable = true;
    private bool _failureHandling;
    private long _uiTicks, _renderTicks, _audioTicks;
    private double _lastPerfReport;
    private double _lastEncodingElapsedSeconds;
    private int _lastBufferedFrames;
    private IDisposable? _renderSessionOwnership;
    private IDisposable? _strictStartupBoundary;
    private StaticInertScope? _staticInertScope;
    private bool _strictStartupBoundaryAcquired;
    private bool _strictStartupBoundaryCancelling;
    private bool _renderRestorationFailed;
    private Exception? _characterVoiceClockFailure;
    private bool _timelineRestoring;

    internal bool Capturing => _native != null;
    internal long CapturedFrame => _captured;
    internal int CaptureFps => _options?.Fps ?? 0;
    internal bool OwnsCapturePlayer(Test player) => Capturing && _player == player;
    internal NativeAutoSelection? AutoSelection => Capturing ? _autoSelection : null;
    internal void AutoSelectionFailed(Exception error) { if (Capturing) Fail(error); }
    internal void CharacterVoiceClockFailed(Exception error)
    {
        // EndExport may report a fault after _native has been detached for
        // restoration. Retain that failure so sealing/publication still stops.
        _characterVoiceClockFailure ??= error;
        if (Capturing) Fail(error);
    }
    internal bool AllowTouch => !Capturing && !_panel.Visible;
    internal float FrameDelta => _options == null ? 0 : 1f / _options.Fps;
    internal float UiClock => _uiEpoch + _captured * FrameDelta;
    private bool Busy => _prepare != null || _finalize != null || _cleanup != null || _bgmCleanup != null || _armed || Capturing || _voiceWarmup || _bgmWarmup || _effectsSourceWaiting || _retainedPreparationRequest != null
        || _nativeLoadPending || _returningToCatalog || _strictReplayPending;

    public ExportHost(IntPtr pointer) : base(pointer) { }

    public void Awake()
    {
        RenderControlV1.BeginHostInitialization();
        Current = this;
        _gpuName = SystemInfo.graphicsDeviceName;
        _panel.Options = new ExportOptions { Title = "", OutputDirectory = Plugin.OutputDirectory, StrictSynchronization = true };
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
            _beginFrame = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>>(
                (Action<ScriptableRenderContext, Il2CppReferenceArray<Camera>>)((_, _) => BeforeFrameRender()));
            RenderPipelineManager.add_beginFrameRendering(_beginFrame);
            _beginCamera = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                (Action<ScriptableRenderContext, Camera>)((_, _) => BeforeFrameRender()));
            RenderPipelineManager.add_beginCameraRendering(_beginCamera);
        }
        catch (Exception error)
        {
            if (_endFrame != null) { try { RenderPipelineManager.remove_endFrameRendering(_endFrame); } catch { } }
            if (_beginFrame != null) { try { RenderPipelineManager.remove_beginFrameRendering(_beginFrame); } catch { } }
            if (_beginCamera != null) { try { RenderPipelineManager.remove_beginCameraRendering(_beginCamera); } catch { } }
            _beginFrame = null; _beginCamera = null;
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
        RenderControlV1.CompleteHostInitialization();
    }

    public void Update()
    {
        if (_shutdown) return;
        MoreEffectsCompatibility.TryInstall();
        FlandrePortraitClock.TryInstall();
        try
        {
            PumpBgmCleanup();
            ThrowRetainedFault();
            CharacterVoiceDelayClock.TryInstall();
            ControlledBackgroundVideoAdapter.TryInstall();
            RuntimeModBoundaries.TryInstall();
            MultilingualCaptureAdapter.TryInstall();
            RetainedMenuPlaybackBoundary.TryInstall();
            PumpRetainedPreparation();
            if (!_strictStartupBoundaryCancelling) ValidateRetainedControllers();
            ValidateFrameworkFrame();
            ValidateRecipeFrame();
            if (_strictStartupBoundaryAcquired && !_strictStartupBoundaryCancelling) AASkipIntroExportBoundary.ValidateActive();
            if (ExportBuildProfile.DevelopmentAuditEnabled && _staticInertScope != null && !_strictStartupBoundaryCancelling) _staticInertScope.ValidateFrame();
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
            if ((_returningToCatalog || _strictReplayPending) && SceneManager.GetActiveScene().name == "CatalogScene" &&
                Object.FindObjectsOfType<CatalogFileInfo>().Any(info => info != null && info.gameObject.activeInHierarchy))
            {
                _returningToCatalog = false;
                _panel.Visible = true;
                if (_strictReplayPending) LaunchStrictReplay();
                SyncNativeUi();
            }
            if (_strictReplayPending && _loadingTimer.Elapsed.TotalSeconds >= 120)
                throw new ExportException("playback_start_timeout", "音频预渲染后返回鉴赏列表超时，已取消此次导出。");
            if (_nativeLoadPending && _nativeLoadCancelled && _loadingTimer.Elapsed.TotalSeconds >= 120)
            {
                _status = "编码已取消，但 AA 的场景加载仍未结束。请重启 AA 后重试，避免重复加载剧情。";
                _statusIsError = true;
            }
            if (_cleanup?.IsCompleted == true)
            {
                try { _cleanup.GetAwaiter().GetResult(); }
                catch (Exception error) { _status = "停止导出后清理未完成，请检查输出目录。"; _statusIsError = true; UnityEngine.Debug.LogWarning("AA Video Export: cleanup failed (" + error.GetType().Name + ")."); }
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
                    var detected = _probe.GetAwaiter().GetResult();
                    if (_probeMode == _panel.Options.EncodingMode)
                    {
                        _capabilities = detected;
                        _capabilitiesMode = _probeMode;
                        _statusIsError = (_probeMode == "software" ? detected.SoftwareEncoders.Count : detected.HardwareEncoders.Count) == 0;
                        _status = _probeMode == "software"
                            ? detected.SoftwareEncoders.Count == 0
                                ? "未检测到可用 CPU 软件编码，请检查 FFmpeg 是否包含 libx264。"
                                : "CPU 检测完成，仅列出已通过实测的软件编码器；画面仍由 GPU 渲染。"
                            : detected.HardwareEncoders.Count == 0
                                ? "未检测到可用 GPU 硬件编码，可手动选择 CPU 软件编码，或检查驱动与 FFmpeg。"
                                : "GPU 检测完成，仅列出本机实测可用的硬件编码。";
                    }
                }
                catch (Exception error)
                {
                    _capabilities = null; _capabilitiesMode = "";
                    _statusIsError = true;
                    _status = _probeMode == "software" ? "CPU 编码器检测失败，请检查 FFmpeg 后重新检测。"
                        : "GPU 检测失败。可手动选择 CPU 软件编码，或检查 FFmpeg 与驱动后重新检测。";
                    UnityEngine.Debug.LogWarning("AA Video Export: " + _probeMode + " encoder probe failed (" + error.GetType().Name + ").");
                }
                _probe = null;
            }
            if (_prepare?.IsCompleted == true)
            {
                var work = _prepare; _prepare = null;
                _session = work.GetAwaiter().GetResult();
                // Effects-source completion remains on the main thread in
                // CompleteBeginCapture, after real native resource loading.
                // This worker has finished pure input hashing/encoder setup;
                // no authored frame or command can run before that completion.
                BindFrozenVoiceStory();
                ValidateFrameworkFrame(required: true);
                ValidateRecipeFrame(required: true);
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
                finally { DisposeStrictPreparation(); _session?.Dispose(); _session = null; _elapsed.Stop(); _panel.Visible = true; }
            }
            if (_voiceWarmup || _bgmWarmup || _effectsSourceWaiting)
            {
                if (_player == null) throw new ExportException("player_closed", "原生语音或效果索引准备期间剧情播放器已关闭。");
                if (_voiceWarmup && !PumpVoiceWarmup()) return;
                CompleteBeginCapture(_player);
                if (_effectsSourceWaiting || _bgmWarmup) return;
            }
            if (!Capturing) return;
            ControlledBackgroundVideoAdapter.ValidateActive();
            RuntimeModBoundaries.ValidateActive();
            MultilingualCaptureAdapter.ValidateActive();
            if (ExportBuildProfile.DevelopmentAuditEnabled && _options?.StrictSynchronization == true && _strictCapabilitySnapshot != null
                && _strictCapabilityGeneration != StrictCapabilityInventory.AssemblyGeneration)
            {
                var currentCapabilities = StrictCapabilityInventory.Capture(_staticInertScope, _voiceSession, _recipeScope, _frameworkScope);
                currentCapabilities.EnsureStrictExportAllowed();
                if (!string.Equals(currentCapabilities.Fingerprint, _strictCapabilitySnapshot.Fingerprint, StringComparison.Ordinal))
                    throw new ExportException("strict_capability_unsupported",
                        "Strict capability set changed during capture; output was refused.");
                _strictCapabilityGeneration = StrictCapabilityInventory.AssemblyGeneration;
            }
            if (_strictStartReady)
            {
                // The original effects index can finish/reload on native
                // Updates during mixer priming. Revalidate its retained proof
                // before arming authored clocks or releasing the first row.
                _effectsSourceInput?.ValidateCurrent(requireActive: true);
                _strictStartReady = false;
                _strictPriming = false;
                _strictAwaitingStart = false;
                _lastUnityFrame = -1;
                if (ExportBuildProfile.SinglePassEnabled) NativeExportClock.ResumeTimelineFrame();
                UnityEngine.Random.InitState(_strictSeed);
                BeginStrictAudioTimeline();
                ArmVoicePass();
                _bgmSession?.BeginPass(_player!);
                _player!.AdvanceScenario(false, null);
                ThrowRetainedFault();
                // A guarded native event may synchronously cancel capture.
                // Preserve that first error and do not reuse disposed state.
                if (!Capturing) return;
            }
            if (_strictFinishPending)
            {
                _strictFinishPending = false;
                // A fixed frame count is only a target. Native Test.End is
                // the authoritative story boundary; never request audio
                // drain for a replay that has not reached that boundary.
                if (!_endFence.IsComplete(_captured))
                {
                    FinishStrictPass();
                    return;
                }
                _strictDraining = true;
                _strictDrainUpdates = 0;
                _strictDrainStarted = Time.realtimeSinceStartup;
            }
            if (_strictDraining)
            {
                // Completed native readbacks are valid only through their
                // current Unity frame, even while final PCM needs more ticks.
                _frames!.Drain(false);
                if (DrainStrictAudioStep())
                {
                    _strictDraining = false;
                    SealStrictAudio();
                    FinishStrictPass();
                }
                return;
            }
            _native!.ExcludeOverlayLayer(_panel.ExcludeFrom);
            _native.PrepareAutomaticFrame();
            if (_captured == 0 && _elapsed.Elapsed.TotalSeconds > 10)
                throw new ExportException("capture_start_timeout", "逐帧渲染未启动，导出已停止。请查看 Mod 兼容性诊断。");
            _frames!.Drain(false);
            if (_player == null) throw new ExportException("player_closed", "剧情窗口已关闭，导出取消。");
            if (_endAt >= 0) return;
            _autoSelection?.Validate();
            if (ScenarioResourceManager.Instance != null && ScenarioResourceManager.Instance.Preloading)
                throw new ExportException("assets_not_ready", "剧情仍在加载素材。请先完整预加载，再从头导出，避免把加载时间混进视频。");
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            RestoreCoordinated(UpdateInputOwnership, "updating native input ownership");
            if (!Busy && !_renderRestorationFailed) ReleaseSessionOwnership();
        }
    }

    [HideFromIl2Cpp]
    private void SyncNativeUi()
    {
        try
        {
            if (_panel.Visible && (!_probeAttempted || _probeMode != _panel.Options.EncodingMode)) Probe();
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
                long encoded = _session?.FramesWritten ?? 0;
                double encodeSpeed = ExportProgressMetrics.FramesPerSecondForDisplay(encoded, _frames?.EncodingElapsedSeconds ?? _lastEncodingElapsedSeconds);
                details = ExportBuildProfile.SinglePassEnabled
                    ? (_strictPriming ? "准备离线音频时钟，随后同步生成画面与音频"
                        : $"单遍导出：渲染 {speed:0.0} 帧/秒 · 编码 {encodeSpeed:0.0} 帧/秒 · 队列 {_frames!.BufferedFrames:N0} 帧")
                    : _strictPriming ? "两遍生成第 1/2 遍：准备离线音频时钟，视频尚未编码"
                    : _strictPreparing ? $"两遍生成第 1/2 遍：预渲染音频 · {speed:0.0} 帧/秒 · 视频尚未编码"
                    : $"两遍生成第 2/2 遍：渲染 {speed:0.0} 帧/秒 · 编码 {encodeSpeed:0.0} 帧/秒 · 队列 {_frames!.BufferedFrames:N0} 帧";
            }
            IReadOnlyList<EncoderCapability>? encoders = _probe != null || !_probeAttempted
                || _probeMode != _panel.Options.EncodingMode ? null
                : _capabilitiesMode != _panel.Options.EncodingMode || _capabilities == null
                    ? Array.Empty<EncoderCapability>()
                    : _panel.Options.EncodingMode == "software" ? _capabilities.SoftwareEncoders : _capabilities.HardwareEncoders;
            long encodedFrames = _session?.FramesWritten ?? 0;
            if (_frames != null)
            {
                _lastEncodingElapsedSeconds = _frames.EncodingElapsedSeconds;
                _lastBufferedFrames = _frames.BufferedFrames;
            }
            var progress = new ExportProgressMetrics(
                _captured,
                encodedFrames,
                _elapsed.Elapsed.TotalSeconds,
                _frames?.EncodingElapsedSeconds ?? _lastEncodingElapsedSeconds,
                _frames?.BufferedFrames ?? _lastBufferedFrames,
                !ExportBuildProfile.SinglePassEnabled && (_strictPreparing || _strictPriming));
            _panel.StatusIsError = _statusIsError;
            _panel.Draw(title, _gpuName, details.Replace('\n', ' '), Busy, _captured, _strictPlan?.FrameCount ?? -1, null, encoders);
            _panel.CaptureElapsedSeconds = _elapsed.Elapsed.TotalSeconds;
            _panel.RenderFramesPerSecond = progress.RenderFramesPerSecond;
            _panel.EncodeFramesPerSecond = progress.EncodeFramesPerSecond;
            _panel.BufferedFrames = progress.BufferedFrames;
            _panel.StrictAudioPreparation = progress.StrictAudioPreparation;
            _panel.StrictVideoReplay = !ExportBuildProfile.SinglePassEnabled && Capturing && !_strictPreparing && !_strictPriming && _options?.StrictSynchronization == true;
            _panel.EncodedFrames = encodedFrames;
            _panel.ExportStage = _cleanup != null || _nativeLoadCancelled ? "正在取消" : _returningToCatalog ? (_strictReplayPending ? "准备重放视频" : "返回导出设置") : _prepare != null ? "准备编码器" : _armed ? "加载剧情与素材" : _voiceWarmup ? "等待原生语音预加载" : _effectsSourceWaiting ? "等待原生画面效果索引" : Capturing ? (_strictPriming ? "两遍生成 · 第 1/2 遍：建立音频时钟（视频暂不编码）" : _strictPreparing ? "两遍生成 · 第 1/2 遍：预渲染音频（视频暂不编码）" : "两遍生成 · 第 2/2 遍：渲染并同步编码") : _finalize != null ? "合成音视频" : "准备就绪";
            if (ExportBuildProfile.SinglePassEnabled && Capturing)
                _panel.ExportStage = _strictPriming ? "准备离线音频时钟" : _strictDraining ? "收齐末尾音频" : "单遍导出 · 生成画面与音频";
            else if (_bgmCleanupFailure != null) _panel.ExportStage = "背景音乐资源恢复失败";
            else if (_bgmCleanup != null) _panel.ExportStage = "等待背景音乐资源清理";
            else if (_bgmWarmup) _panel.ExportStage = "等待背景音乐预加载";
            _panel.Tick();
            _lastUiError = "";
        }
        catch (Exception error)
        {
            _status = "导出面板错误：" + ExportErrorText.Describe(error);
            _statusIsError = true;
            string fingerprint = error.GetType().Name + ":" + error.Message;
            if (fingerprint != _lastUiError)
                UnityEngine.Debug.LogError("AA Video Export native panel exception: " + error);
            _lastUiError = fingerprint;
        }
    }

    [HideFromIl2Cpp]
    private void Probe()
    {
        string mode = _panel.Options.EncodingMode;
        if (Busy || (_probe != null && _probeMode == mode)) return;
        if (_probe != null)
        {
            // A mode switch owns a fresh result. Cancel the old process and
            // observe faults without ever publishing its result to the panel.
            _probeCancellation?.Cancel();
            _ = _probe.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _probeAttempted = true;
        _capabilities = null;
        _capabilitiesMode = "";
        _probeMode = mode;
        _probeCancellation?.Dispose();
        _probeCancellation = new CancellationTokenSource();
        _statusIsError = false;
        _status = mode == "software" ? "正在检测 CPU 软件编码（短片测试，不读取剧情）…"
            : "正在检测本机 GPU 硬件编码（短片测试，不读取剧情）…";
        _probe = mode == "software"
            ? FfmpegCapabilities.ProbeSoftwareAsync(Plugin.FfmpegPath, _probeCancellation.Token)
            : FfmpegCapabilities.ProbeHardwareAsync(Plugin.FfmpegPath, _gpuName, _probeCancellation.Token);
    }

    [HideFromIl2Cpp]
    private void Prepare(ExportOptions options)
    {
        RenderControlV1.EnsureMainThread();
        if (Busy) return;
        if (_renderRestorationFailed) { _status = "恢复尚未完成；请先重启 AA 后再导出。"; _statusIsError = true; return; }
        _retainedFault = null;
        _statusIsError = false;
        var acquisitionAttempted = false;
        var acquired = _renderSessionOwnership != null;
        try
        {
            options = options with { RenderGpuVendorId = SystemInfo.graphicsDeviceVendorID, RenderGpuDeviceId = SystemInfo.graphicsDeviceID, Direct3DReadbackFrames = Plugin.Direct3DReadbackFrames, StrictSynchronization = true };
            options.Validate();
            // Resolve late-loaded optional adapters before any story read or
            // RenderControlV1 handoff. Reject known missing adapters; this is
            // not a certification of unobserved third-party timing paths.
            MoreEffectsCompatibility.TryInstall();
            CharacterVoiceDelayClock.TryInstall();
            ControlledBackgroundVideoAdapter.TryInstall();
            RuntimeModBoundaries.TryInstall();
            MultilingualCaptureAdapter.TryInstall();
            MoreEffectsCompatibility.EnsureOwnEffectsAllowed();
            RuntimeModBoundaries.PrepareExportDependencies();
            FlandrePortraitClock.EnsureStrictExportAllowed();
            if (ExportBuildProfile.DevelopmentAuditEnabled)
            {
                _staticInertScope ??= StaticInertScope.Acquire();
                _staticInertScope.ValidateFrame();
            }
            if (!_strictStartupBoundaryAcquired)
            {
                _strictStartupBoundary = AASkipIntroExportBoundary.TryAcquire();
                _strictStartupBoundaryAcquired = true;
                _strictStartupBoundaryCancelling = false;
            }
            if (!AcquireRetainedBoundaries(options)) return;
            AcquireFrameworkBoundary();
            AcquireRecipeBoundary();
            // Capture the loaded Mod capability set before reading the story or
            // acquiring RenderControlV1. Opaque/rejected modules must fail
            // closed; ordinary playback remains unaffected because this gate
            // is reached only by the strict export preparation path.
            if (ExportBuildProfile.DevelopmentAuditEnabled)
            {
                _strictCapabilitySnapshot = StrictCapabilityInventory.Capture(_staticInertScope, _voiceSession, _recipeScope, _frameworkScope);
                _strictCapabilitySnapshot.EnsureStrictExportAllowed();
                _strictCapabilityGeneration = StrictCapabilityInventory.AssemblyGeneration;
            }
            if (_capabilities == null || _probe != null || _capabilitiesMode != options.EncodingMode
                || _panel.Options.EncodingMode != options.EncodingMode)
            { Probe(); _status = "请先等待当前编码方式的检测完成，再点击导出。"; return; }
            string encoder = _capabilities.PickForMode(options);
            var (selected, storyKey) = ResolveStory();
            if (!File.Exists(storyKey)) throw new ExportException("story_missing", "所选剧情文件不存在，请刷新鉴赏列表。");
            if (UserSettings.Instance == null || ScenarioResourceManager.Instance == null)
                throw new ExportException("story_resources_unavailable", "AA 的剧情资源服务尚未就绪，请稍后重试。");
            if (!StorySourcePath.TryGetScenarioName(storyKey,
                name => ScenarioResourceManager.GetPersistentFilePath("saves", name, ".aas"), out string scenarioName))
                throw new ExportException("story_location_unsupported", "所选剧情与 AA 当前读取的鉴赏目录不一致，请刷新鉴赏列表后重新选择。");
            acquisitionAttempted = true;
            _renderSessionOwnership ??= RenderControlV1.Acquire();
            acquired = true;
            // The native loader accepts a basename and resolves data/saves itself.
            // Read the selected file afresh; a global table can belong to a different card.
            var save = ScenarioResourceManager.Instance.LoadGenericScenario(scenarioName);
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
            if (ExportBuildProfile.DevelopmentAuditEnabled) UnityEngine.Debug.Log("AA Video Export strict capability snapshot: fingerprint=" + _strictCapabilitySnapshot!.Fingerprint
                + "; modules=" + string.Join(",", _strictCapabilitySnapshot.Modules.Select(module => module.ModuleId + "=" + module.Level))
                + "; preflight_cache_reuse=" + _strictCapabilitySnapshot.CanReusePreflight);
            InitializeStrictPreparation(storyKey);
            // Playback timing belongs to AA. Always start from the selected
            // story's beginning and let native Auto/voice/animation timing
            // drive the run until Test.End().
            _showButtons = _panel.ShowPlaybackButtons;
            _captured = 0; _endAt = -1;
            _elapsed.Reset();
            _cancellation?.Dispose(); _cancellation = new CancellationTokenSource();
            int sampleRate = AudioSettings.outputSampleRate;
            int channels = AudioSettings.speakerMode switch { AudioSpeakerMode.Mono => 1, AudioSpeakerMode.Stereo => 2, AudioSpeakerMode.Quad => 4, AudioSpeakerMode.Surround => 5, AudioSpeakerMode.Mode5point1 => 6, AudioSpeakerMode.Mode7point1 => 8, _ => 0 };
            if (channels == 0) throw new ExportException("audio_layout_unsupported", "当前 Unity 音频输出布局不支持导出。");
            string encoderVendor = encoder.EndsWith("_nvenc", StringComparison.Ordinal) ? "NVIDIA"
                : encoder.EndsWith("_amf", StringComparison.Ordinal) ? "AMD" : "Intel";
            _status = options.EncodingMode == "software"
                ? $"正在检查 CPU 软件编码器 {encoder}，画面 {options.Width}×{options.Height}…"
                : $"正在检查 {encoderVendor} 硬件编码器，画面 {options.Width}×{options.Height}…";
            var token = _cancellation.Token;
            _prepare = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                PrepareStrictIdentity(storyKey);
                token.ThrowIfCancellationRequested();
                var session = new ExportSession(options, Plugin.FfmpegPath, Plugin.FfprobePath, encoder, sampleRate, channels, true);
                if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
                return session;
            });
        }
        catch (Exception error) when (acquisitionAttempted && !acquired)
        {
            // Participants rejected the handoff before native preparation.
            // Do not run Cancel or open a panel that would immediately try to
            // acquire again; no exporter snapshot or mutation has occurred.
            _status = "导出未开始：渲染控制权交接失败，请检查其他 Mod 的恢复状态。";
            _statusIsError = true;
            UnityEngine.Debug.LogError("AA Video Export: render acquisition rejected: " + error);
        }
        catch (Exception error) { Fail(error); }
        finally { if (!Busy && !_renderRestorationFailed) ReleaseSessionOwnership(); }
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
    internal bool BeforePlayerStart(Test player)
    {
        if (!_strictStartupBoundaryCancelling && (_armed || Capturing || _voiceWarmup || _bgmWarmup || _effectsSourceWaiting) && _retainedFault != null) return false;
        if ((_voiceWarmup || _bgmWarmup || _effectsSourceWaiting) && _player == player) return false;
        if (!_armed || _eligiblePlayer == IntPtr.Zero || player.Pointer != _eligiblePlayer || player.previewMode || player.scn == null) return true;
        try
        {
            if (!_launch.AcceptPlayback(_eligibleTicket, Path.GetFullPath(PersistentData.projectPath))) return true;
            _nativeLoadPending = false;
            Begin(player);
            _loadingCoroutine = null; _loadingStory = null; _frozenSave = null;
            return !_voiceWarmup && !_bgmWarmup && !_effectsSourceWaiting;
        }
        catch (Exception error) { Fail(error); return false; }
    }

    [HideFromIl2Cpp]
    private void Begin(Test player)
    {
        RenderControlV1.EnsureMainThread();
        if (_session == null || _options == null) throw new ExportException("session_missing", "编码任务未准备。");
        if (ExportBuildProfile.DevelopmentAuditEnabled && _options.StrictSynchronization) RequireStaticInertScope().ValidateFrame();
        ValidateFrameworkFrame(required: _options.StrictSynchronization);
        ValidateRecipeFrame(required: _options.StrictSynchronization);
        if (_strictStartupBoundaryAcquired) AASkipIntroExportBoundary.ValidateActive();
        if (ExportBuildProfile.DevelopmentAuditEnabled && _options.StrictSynchronization && _strictCapabilitySnapshot != null)
        {
            var currentCapabilities = StrictCapabilityInventory.Capture(_staticInertScope, _voiceSession, _recipeScope, _frameworkScope);
            currentCapabilities.EnsureStrictExportAllowed();
            if (!string.Equals(currentCapabilities.Fingerprint, _strictCapabilitySnapshot.Fingerprint, StringComparison.Ordinal))
                throw new ExportException("strict_capability_unsupported",
                    "Strict capability set changed after story loading; capture was refused before the first frame.");
            _strictCapabilityGeneration = StrictCapabilityInventory.AssemblyGeneration;
        }
        _renderSessionOwnership ??= RenderControlV1.Acquire();
        _armed = false;
        _eligiblePlayer = IntPtr.Zero;
        _player = player;
        if (_options.StrictSynchronization && _retainedMenuLease == null)
            throw new ExportException("strict_retained_boundary_invalid", "Strict capture has no retained menu/resource proof.");
        if (_retainedMenuLease != null) RetainedMenuPlaybackBoundary.BeginPlayback();
        BeginBgmPreparation();
        // Keep native resource coroutines and ordinary clocks running while
        // voice assets preload. No offline scope/clock exists until ready.
        if (!PrepareVoiceWarmup()) return;
        CompleteBeginCapture(player);
    }

    [HideFromIl2Cpp]
    private void CompleteBeginCapture(Test player)
    {
        if (_session == null || _options == null) throw new ExportException("session_missing", "编码任务未准备。");
        if (!PumpBgmPreparation()) return;
        ValidateRetainedControllers();
        ValidateFrameworkFrame(required: _options.StrictSynchronization);
        ValidateRecipeFrame(required: _options.StrictSynchronization);
        if (ExportBuildProfile.DevelopmentAuditEnabled && _strictCapabilitySnapshot != null)
        {
            var current = StrictCapabilityInventory.Capture(_staticInertScope, _voiceSession, _recipeScope, _frameworkScope);
            current.EnsureStrictExportAllowed();
            if (current.Fingerprint != _strictCapabilitySnapshot.Fingerprint)
                throw new ExportException("strict_capability_unsupported", "Loaded capabilities changed during native voice preloading.");
            _strictCapabilityGeneration = StrictCapabilityInventory.AssemblyGeneration;
        }
        _cancellation?.Token.ThrowIfCancellationRequested();
        if (_effectsSourceWaiting && Time.realtimeSinceStartup - _effectsSourceWaitStarted >= 120)
            throw new ExportException("strict_effects_source_timeout", "原生画面效果索引准备超时，尚未开始剧情计帧或离线音频；导出已停止。");
        try { CompleteStrictSourcePreparation(); }
        catch (ExportException error) when (error.Code == "strict_effects_source_pending")
        {
            // The original Mod consumes its background load from its native
            // Update. Keep normal clocks live and retry only on a later Update;
            // no frame ring, capture scope or authored advance exists yet.
            if (!_effectsSourceWaiting) _effectsSourceWaitStarted = Time.realtimeSinceStartup;
            _effectsSourceWaiting = true;
            _status = "正在等待原生画面效果索引准备完成；尚未开始剧情计帧或离线音频，可随时取消。";
            return;
        }
        _effectsSourceWaiting = false;
        _effectsSourceWaitStarted = 0;
        float previousUiTime = RealTime.time;
        _uiEpoch = previousUiTime;
        _panel.ReleaseIdleBudget();
        // Keep the same native 1x pixels, but always use the bounded target ring.
        // The former 1080p single-target path allowed a new readback request to
        // pile onto the same RenderTexture while the previous request was still
        // in flight. That serializes the GPU/CPU boundary on long exports and
        // is exactly the opposite of the editor-style render pipeline we want.
        _frames ??= new FramePipeline(_options, _session, Plugin.UseAsyncReadback, nativeBuffers: Plugin.UseNativeFrameBuffers);
        _native = new NativeCaptureScope(player, _options, _frames.Target, _showButtons, _automaticCaptureAvailable);
        BeginStrictPass();
        _uiEpoch = 0;
        _timelineRestoring = false;
        if (ExportBuildProfile.SinglePassEnabled)
            NativeExportClock.BeginTimeline(_options.Fps, () => _captured,
                () => !_timelineRestoring && !_strictStartupBoundaryCancelling && !_strictPriming && !_strictAwaitingStart && !_strictDraining && !_strictFinishPending);
        else
            NativeExportClock.Begin(_uiEpoch, FrameDelta, () => UiClock);
        foreach (var tween in Object.FindObjectsOfType<UITweener>())
            if (tween != null && tween.ignoreTimeScale && tween.mStarted) tween.mStartTime -= previousUiTime;
        _autoSelection = new NativeAutoSelection(player, message => UnityEngine.Debug.Log("AA Video Export: " + message));
        MoreEffectsCompatibility.BeginExport(() => ExportBuildProfile.SinglePassEnabled ? NativeExportClock.ElapsedSeconds : _captured / (double)_options.Fps);
        FlandrePortraitClock.BeginExport(() => ExportBuildProfile.SinglePassEnabled ? NativeExportClock.ElapsedSeconds : _captured / (double)_options.Fps);
        _characterVoiceClockFailure = null;
        CharacterVoiceDelayClock.BeginExport(() => ExportBuildProfile.SinglePassEnabled ? NativeExportClock.Frame : _captured, _options.Fps);
        ControlledBackgroundVideoAdapter.BeginExport(() => ExportBuildProfile.SinglePassEnabled ? NativeExportClock.Frame : _captured, _options.Fps,
            Path.Combine(Plugin.TimelineReceiptDirectory, "background-frames"), Plugin.FfmpegPath, Plugin.FfprobePath);
        RuntimeModBoundaries.BeginExport();
        MultilingualCaptureAdapter.BeginExport();
        UpdateInputOwnership();
        _lastUnityFrame = -1;
        _elapsed.Restart();
        _uiTicks = _renderTicks = _audioTicks = 0;
        _lastEncodingElapsedSeconds = 0;
        _lastBufferedFrames = 0;
        _panel.ResetPerformanceCounters();
        _lastPerfReport = 0;
        _status = "逐帧导出中…";
        _statusIsError = false;
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
    [HideFromIl2Cpp]
    internal void BeforeFrameRender()
    {
        if (!Capturing || _strictPriming || _strictDraining || _strictFinishPending) return;
        try
        {
            long frame = ExportBuildProfile.SinglePassEnabled ? NativeExportClock.Frame : _captured;
            ControlledBackgroundVideoAdapter.Pump(frame); MultilingualCaptureAdapter.Pump(frame);
        }
        catch (Exception error) { Fail(error); }
    }
    public void LateUpdate() => BeforeFrameRender();
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
            ThrowRetainedFault();
            if (_strictStartupBoundaryAcquired) AASkipIntroExportBoundary.ValidateActive();
            if (ExportBuildProfile.DevelopmentAuditEnabled && _options?.StrictSynchronization == true) RequireStaticInertScope().ValidateFrame();
            ValidateRetainedControllers();
            ValidateFrameworkFrame(required: _options?.StrictSynchronization == true);
            ValidateRecipeFrame(required: _options?.StrictSynchronization == true);
            ValidateVoiceFrame();
            long stage = Stopwatch.GetTimestamp();
            if (_strictPriming)
            {
                var primer = _native!.RenderAudio();
                ValidateFrameworkFrame(required: _options?.StrictSynchronization == true);
                if (AcceptStrictPrimer(primer.Count)) _strictStartReady = true;
                return; // No authored event has been advanced; primer PCM is not content.
            }
            if (_strictFinishPending || _strictDraining || _strictAwaitingStart) return;
            _strictPlan?.ValidateNextVideoFrame(_captured);
            var target = _frames!.RenderTarget;
            if (!frameAlreadyRendered)
            {
                long frame = ExportBuildProfile.SinglePassEnabled ? NativeExportClock.Frame : _captured;
                ControlledBackgroundVideoAdapter.Pump(frame);
                MultilingualCaptureAdapter.Pump(frame);
                _native!.ExcludeOverlayLayer(_panel.ExcludeFrom);
                // Waiting for a free GPU slot has its own metric; do not count it
                // again as camera rendering.
                stage = Stopwatch.GetTimestamp();
                _native.RenderFrame(target);
                _renderTicks += Stopwatch.GetTimestamp() - stage;
            }
            ThrowRetainedFault();
            ValidateFrameworkFrame(required: _options?.StrictSynchronization == true);
            if (!_strictPreparing) _frames!.Capture();
            if (frameAlreadyRendered && Capturing)
                _native!.SetTarget(_frames.RenderTarget);
            stage = Stopwatch.GetTimestamp();
            var audio = _native!.RenderAudio();
            ThrowRetainedFault();
            ValidateFrameworkFrame(required: _options?.StrictSynchronization == true);
            CaptureStrictAudioAndState(audio.Samples, audio.Count);
            _audioTicks += Stopwatch.GetTimestamp() - stage;
            _captured++;
            ReportPerformance(false);
            if (_captured == 1) UnityEngine.Debug.Log("AA Video Export: first frame and audio captured.");
            if (_endFence.IsComplete(_captured)) _strictFinishPending = true;
            else if (_strictPlan != null && _captured == _strictPlan.FrameCount) _strictFinishPending = true;
        }
        catch (Exception error) { Fail(error); }
    }

    [HideFromIl2Cpp]
    internal bool BeforePlayerEnd(Test player)
    {
        if ((_voiceWarmup || _bgmWarmup || _effectsSourceWaiting) && _player == player) { Cancel(); return false; }
        if (!Capturing || _player != player) return true;
        if (_strictAwaitingStart || _strictDraining) return false;
        try
        {
            _endAt = _endFence.ObserveEnd(_captured, Time.frameCount, _lastUnityFrame);
            _strictPlan?.ValidateVideoEnd(_endAt);
            if (_endFence.IsComplete(_captured)) _strictFinishPending = true;
        }
        catch (Exception error) { Fail(error); }
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
        if (ExportBuildProfile.DevelopmentAuditEnabled && _strictStartupBoundaryAcquired)
        {
            // Keep the monitor live until finalization. The worker may only
            // inspect its fault latch; Unity/patch-table checks stay above on
            // the main thread, including the last captured frame.
            var previousCommit = session.BeforeOutputCommit;
            session.BeforeOutputCommit = () =>
            {
                AASkipIntroExportBoundary.ValidateForCommit();
                previousCommit?.Invoke();
            };
            session.AcquireOutputCommitScope = AASkipIntroExportBoundary.AcquireCommitScope;
        }
        StaticInertScope? retainedInert = null;
        if (ExportBuildProfile.DevelopmentAuditEnabled && _options?.StrictSynchronization == true)
        {
            retainedInert = RequireStaticInertScope();
            var previousCommit = session.BeforeOutputCommit;
            session.BeforeOutputCommit = () => { retainedInert.ValidateForCommit(); previousCommit?.Invoke(); };
            var precedingScope = session.AcquireOutputCommitScope;
            session.AcquireOutputCommitScope = () => retainedInert.AcquireCommitScope(precedingScope);
        }
        RestoreNativeCapture();
        ThrowRetainedFault();
        retainedInert?.SealForCommit();
        if (ExportBuildProfile.DevelopmentAuditEnabled) _voiceSession?.SealForCommit();
        WireRetainedPublication(session);
        WireEffectsSourcePublication(session);
        _frames.Dispose(); _frames = null;
        _status = "画面已渲染完，正在合成音视频…";
        _panel.Visible = true;
        // End exactly once after the export hook has become inert.
        var completedPlayer = _player; _player = null;
        _returningToCatalog = completedPlayer != null;
        if (completedPlayer != null) completedPlayer.End();
        ThrowRetainedFault();
        WireFrameworkPublication(session);
        WireRecipePublication(session);
        var recipe = _recipeScope;
        var framework = _frameworkScope;
        _finalize = Task.Run(() => CompleteWithRecipe(session, writerDone, token, recipe, framework));
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
            _statusIsError = true;
            _panel.StatusIsError = true;
            _panel.Visible = true;
        }
        finally { _failureHandling = false; }
    }

    [HideFromIl2Cpp]
    private void Cancel()
    {
        // A latched boundary fault must not run again before the cancelled
        // worker and pending scene return can finish cleanup on later Updates.
        _strictStartupBoundaryCancelling = true;
        _retainedPreparationRequest = null; _retainedPreparationWait.Stop(); _voiceWarmup = _bgmWarmup = false;
        _effectsSourceWaiting = false; _effectsSourceWaitStarted = 0;
        if (_cleanup != null) return;
        try { ReportPerformance(true); }
        catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: performance report skipped during cancellation (" + error.GetType().Name + ")."); }
        _armed = false;
        _strictReplayPending = _strictFinishPending = _strictDraining = _strictPriming = _strictStartReady = _strictAwaitingStart = false;
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
        BestEffort(DisposeStrictPreparation, "cleaning prepared audio and transcript");
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
        if (!_shutdown) _panel.Visible = true;
        _status = _nativeLoadPending ? "编码已取消，正在等待 AA 完成当前场景加载并返回设置…"
            : "已取消。原工程未修改，导出设置已保留。";
        _statusIsError = false;
    }

    [HideFromIl2Cpp]
    private void ReportCompleted(ExportResult result)
    {
        _status = $"完成：{result.OutputPath}\n{result.Frames:N0} 帧 / {result.DurationSeconds:0.0} 秒；总耗时 {(_options?.StrictSynchronization == true ? _strictTotalElapsed.Elapsed.TotalSeconds : _elapsed.Elapsed.TotalSeconds):0.0} 秒";
        _statusIsError = false;
    }

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
        // Capture count includes the last completed frame; publishing it as a
        // new authored time during cleanup would advance pending native waits.
        _timelineRestoring = true;
        Exception? bgmRestoreError = null;
        RestoreCoordinated(() =>
        {
            try { _bgmSession?.EndPass(); }
            catch (Exception error) { bgmRestoreError = error; throw; }
        }, "restoring native BGM resource dispatch");
        Exception? retainedRestoreError = null;
        RestoreCoordinated(() =>
        {
            try { EndRetainedPlayback(); }
            catch (Exception error) { retainedRestoreError = error; throw; }
        }, "restoring retained menu/voice playback");
        if (_native == null)
        {
            if (bgmRestoreError != null) throw bgmRestoreError;
            if (retainedRestoreError != null) throw retainedRestoreError;
            return;
        }
        float exportClock = ExportBuildProfile.SinglePassEnabled && Capturing ? NativeExportClock.Now : UiClock;
        var scope = _native;
        _native = null; // Clock hooks must be inert before reading the restored real clock.
        _autoSelection = null;
        Exception? voiceRestoreError = null;
        RestoreCoordinated(() =>
        {
            try { CharacterVoiceDelayClock.EndExport(); }
            catch (Exception error) { voiceRestoreError = error; throw; }
        }, "restoring CharacterVoice authored delay");
        Exception? backgroundRestoreError = null;
        RestoreCoordinated(() =>
        {
            try { ControlledBackgroundVideoAdapter.EndExport(); }
            catch (Exception error) { backgroundRestoreError = error; throw; }
        }, "restoring controlled background video");
        Exception? translationRestoreError = null, dependencyRestoreError = null;
        RestoreCoordinated(() =>
        {
            try { MultilingualCaptureAdapter.EndExport(); }
            catch (Exception error) { translationRestoreError = error; throw; }
        }, "restoring original translation scheduling");
        RestoreCoordinated(() =>
        {
            try { RuntimeModBoundaries.EndExport(); }
            catch (Exception error) { dependencyRestoreError = error; throw; }
        }, "restoring idle dependency surfaces");
        RestoreCoordinated(MoreEffectsCompatibility.EndExport, "restoring adapted animation clock");
        RestoreCoordinated(FlandrePortraitClock.EndExport, "restoring portrait clock");
        RestoreCoordinated(NativeExportClock.Restore, "restoring native clock");
        if (_captureLoop != null)
        {
            try { _player?.StopCoroutine(_captureLoop); } catch { }
            _captureLoop = null;
        }
        RestoreCoordinated(scope.Dispose, "restoring native settings");
        if (scope.RestorationFailed)
        {
            _renderRestorationFailed = true;
            RenderControlV1.RestorationFailed();
        }
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
            _renderRestorationFailed = true;
            RenderControlV1.RestorationFailed();
            // Scene teardown must still release the encoder and GPU resources.
            UnityEngine.Debug.LogWarning("AA Video Export: tween clock restoration unavailable (" + error.GetType().Name + ").");
        }
        // Finish/FinishStrictPass must not continue after a restoration fault.
        // Cancel already owns cleanup; its original diagnostic stays primary.
        if (bgmRestoreError != null) throw bgmRestoreError;
        if (voiceRestoreError != null) throw voiceRestoreError;
        if (backgroundRestoreError != null) throw backgroundRestoreError;
        if (translationRestoreError != null) throw translationRestoreError;
        if (dependencyRestoreError != null) throw dependencyRestoreError;
        if (retainedRestoreError != null) throw retainedRestoreError;
        if (!_strictStartupBoundaryCancelling && _characterVoiceClockFailure != null)
            throw _characterVoiceClockFailure;
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
        if (_shutdown)
        {
            RestoreCoordinated(ReleaseRetainedBoundaries, "retrying retained controller cleanup");
            RestoreCoordinated(ReleaseStaticInertScope, "retrying retained empty-Mod cleanup");
            return;
        }
        RenderControlV1.BeginShutdown();
        _shutdown = true;
        BestEffort(() => _probeCancellation?.Cancel(), "cancelling encoder probe");
        RestoreCoordinated(Cancel, "cancelling export during shutdown");
        RestoreCoordinated(() => _catalogAction?.Dispose(), "removing catalog action");
        _catalogAction = null;
        RestoreCoordinated(_panel.Dispose, "restoring export panel");
        if (_endFrame != null)
        {
            try { RenderPipelineManager.remove_endFrameRendering(_endFrame); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: URP callback cleanup unavailable (" + error.GetType().Name + ")."); }
        }
        if (_beginFrame != null)
        {
            try { RenderPipelineManager.remove_beginFrameRendering(_beginFrame); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: URP pre-render cleanup unavailable (" + error.GetType().Name + ")."); }
        }
        if (_beginCamera != null)
        {
            try { RenderPipelineManager.remove_beginCameraRendering(_beginCamera); }
            catch (Exception error) { UnityEngine.Debug.LogWarning("AA Video Export: URP pre-camera cleanup unavailable (" + error.GetType().Name + ")."); }
        }
        RestoreCoordinated(UpdateInputOwnership, "restoring native input");
        if (!_renderRestorationFailed) ReleaseSessionOwnership();
        Current = null;
    }
    public void OnDestroy() => Shutdown();
    public void OnApplicationQuit() => Shutdown();

    [HideFromIl2Cpp]
    private void RestoreCoordinated(Action restore, string operation)
    {
        try { restore(); }
        catch (Exception error)
        {
            _renderRestorationFailed = true;
            RenderControlV1.RestorationFailed();
            UnityEngine.Debug.LogWarning("AA Video Export: " + operation + " failed (" + error.GetType().Name + "); render coordination remains unavailable.");
        }
    }

    [HideFromIl2Cpp]
    private void ReleaseSessionOwnership()
    {
        ReleaseRetainedBoundaries();
        ReleaseStaticInertScope();
        var startup = _strictStartupBoundary;
        _strictStartupBoundary = null;
        bool startupAcquired = _strictStartupBoundaryAcquired;
        _strictStartupBoundaryAcquired = false;
        _strictStartupBoundaryCancelling = false;
        try
        {
            if (startup != null) startup.Dispose();
            else if (startupAcquired) AASkipIntroExportBoundary.ReleaseAbsentSnapshot();
        }
        catch (Exception error)
        {
            // This boundary observes startup; it does not own or mutate
            // render settings. Report its fault, but still restore the
            // separately held render lease.
            _status = "导出边界检查失败：" + ExportErrorText.Describe(error);
            _statusIsError = true;
            _panel.StatusIsError = true;
            UnityEngine.Debug.LogError("AA Video Export: startup boundary release failed: " + error);
        }
        var ownership = _renderSessionOwnership;
        _renderSessionOwnership = null;
        if (ownership == null) return;
        try { ownership.Dispose(); }
        catch (Exception error)
        {
            // Unity settings are already restored. A participant's release
            // callback failure must not apply any exporter settings again.
            _renderRestorationFailed = true;
            UnityEngine.Debug.LogWarning("AA Video Export: render release observer failed (" + error.GetType().Name + ").");
        }
    }

    [HideFromIl2Cpp]
    private StaticInertScope RequireStaticInertScope() => _staticInertScope
        ?? throw new ExportException("strict_static_inert_invalid", "Strict export has no retained empty-Mod acquisition scope.");

    [HideFromIl2Cpp]
    private void ReleaseStaticInertScope()
    {
        var scope = _staticInertScope;
        scope?.Dispose();
        _staticInertScope = null;
    }
}
