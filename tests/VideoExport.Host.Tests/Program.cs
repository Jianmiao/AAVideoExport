using System.Reflection;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

var staticInertAuditTests = new (string Name, Action Run)[]
{
    ("static inert scope acquisition rejects before native story load", () =>
    {
        var host=CreateHost();Field<NativeExportPanel>(host,"_panel").Options=Options();
        StaticInertModEvidence.Present=true;StaticInertModEvidence.RejectAcquisition=true;
        try
        {
            Call(host,"Prepare",Field<NativeExportPanel>(host,"_panel").Options);
            Check(UnityEngine.Debug.Messages.Any(m=>m.Contains("strict_static_inert_invalid")),"Prepare did not acquire real retained inert scope");
            Check(ScenarioResourceManager.Instance!.LoadedNames.Count==0 && !RenderControlV1.IsRenderingOwned,"Inert failure ran after source load/handoff");
        }
        finally { host.Shutdown();StaticInertModEvidence.Reset(); }
    }),
};

var preparationTests = new (string Name, Action Run)[]
{
    ("startup boundary acquisition failure occurs before native story loading", () =>
    {
        var host = CreateHost();
        Field<NativeExportPanel>(host, "_panel").Options = Options();
        AASkipIntroExportBoundary.Present = true;
        AASkipIntroExportBoundary.RejectAcquisition = true;
        try
        {
            Call(host, "Prepare", Field<NativeExportPanel>(host, "_panel").Options);
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("strict_startup_boundary_invalid")),
                "export preparation did not acquire the retained startup controller");
            Check(ScenarioResourceManager.Instance!.LoadedNames.Count == 0 && !RenderControlV1.IsRenderingOwned,
                "failed startup controller allowed native story load or render handoff");
        }
        finally { host.Shutdown(); AASkipIntroExportBoundary.Reset(); }
    }),
    ("strict preparation rejects incomplete More Effects before reading story", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options();
        MoreEffectsCompatibility.IsLoaded = true;
        MoreEffectsCompatibility.FullClockCoverage = false;
        MoreEffectsCompatibility.Status = "partial";
        MoreEffectsCompatibility.FailureReason = "fixture reason";
        int acquisitions = 0;
        Action onAcquire = () => acquisitions++;
        RenderControlV1.BeforeAcquire += onAcquire;
        try
        {
            Call(host, "Prepare", panel.Options);
            Check(ScenarioResourceManager.Instance!.LoadedNames.Count == 0, "strict admission ran after story loading");
            Check(Field<Task<ExportSession>?>(host, "_prepare") == null, "strict admission failure still started encoder preparation");
            Check(acquisitions == 0 && !RenderControlV1.IsRenderingOwned, "strict rejection acquired rendering ownership");
            Check(Field<string>(host, "_status").Contains("逐帧对齐", StringComparison.Ordinal), "strict admission failure lost localized guidance");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("strict_mod_clock_unsupported", StringComparison.Ordinal)
                && m.Contains("fixture reason", StringComparison.Ordinal)), "strict admission diagnostic did not reach the log");
        }
        finally
        {
            RenderControlV1.BeforeAcquire -= onAcquire;
            MoreEffectsCompatibility.ResetAdmission();
            host.Shutdown();
        }
    }),
    ("strict preparation rejects exact unsupported Character Voice before reading story", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options();
        MoreEffectsCompatibility.CharacterVoiceExactUnsupported = true;
        try
        {
            Call(host, "Prepare", panel.Options);
            Check(ScenarioResourceManager.Instance!.LoadedNames.Count == 0, "Character Voice gate ran after story loading");
            Check(Field<Task<ExportSession>?>(host, "_prepare") == null, "Character Voice gate still started encoder preparation");
            Check(Field<string>(host, "_status").Contains("人物配音支持", StringComparison.Ordinal), "Character Voice error was not localized");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("strict_character_voice_clock_unsupported", StringComparison.Ordinal)),
                "Character Voice strict diagnostic did not reach the log");
        }
        finally
        {
            MoreEffectsCompatibility.ResetAdmission();
            host.Shutdown();
        }
    }),
};

var capabilityAuditTests = new (string Name, Action Run)[]
{
    ("strict preparation rejects an opaque loaded module before story and render handoff", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options();
        StrictCapabilityInventory.Snapshot = StrictCapabilitySnapshot.Create(new[]
        {
            new StrictCapability("Unknown.Mod", new string('a', 64), StrictCapabilityLevel.Opaque,
                new[] { "assembly-not-yet-certified" })
        });
        int acquisitions = 0;
        Action onAcquire = () => acquisitions++;
        RenderControlV1.BeforeAcquire += onAcquire;
        try
        {
            Call(host, "Prepare", panel.Options);
            Check(ScenarioResourceManager.Instance!.LoadedNames.Count == 0, "opaque capability gate ran after story loading");
            Check(Field<Task<ExportSession>?>(host, "_prepare") == null, "opaque capability gate started encoder preparation");
            Check(acquisitions == 0 && !RenderControlV1.IsRenderingOwned, "opaque capability gate acquired rendering ownership");
            Check(Field<string>(host, "_status").Contains("尚未通过严格同步认证", StringComparison.Ordinal),
                "opaque capability error was not localized");
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("strict_capability_unsupported", StringComparison.Ordinal)
                && m.Contains("Unknown.Mod", StringComparison.Ordinal)), "opaque capability diagnostic did not reach the log");
        }
        finally
        {
            RenderControlV1.BeforeAcquire -= onAcquire;
            StrictCapabilityInventory.Reset();
            host.Shutdown();
        }
    }),
    ("strict capture rejects a late opaque assembly before the next frame", () =>
    {
        var player = new Test();
        var host = CapturingHost(player);
        Set(host, "_options", Options() with { StrictSynchronization = true });
        var empty = StrictCapabilitySnapshot.Create(Array.Empty<StrictCapability>());
        Set(host, "_strictCapabilitySnapshot", empty);
        StrictCapabilityInventory.Snapshot = StrictCapabilitySnapshot.Create(new[]
        {
            new StrictCapability("Late.Unknown", new string('b', 64), StrictCapabilityLevel.Opaque,
                new[] { "assembly-loaded-during-capture" })
        });
        StrictCapabilityInventory.AssemblyGeneration = 1;
        try
        {
            host.Update();
            Check(!host.Capturing, "late opaque capability did not stop capture; status=" + Field<string>(host, "_status"));
            Check(Field<NativeExportPanel>(host, "_panel").StatusIsError,
                "late capability failure was not surfaced as an error; status=" + Field<string>(host, "_status") +
                "; messages=" + string.Join("|", UnityEngine.Debug.Messages));
            Check(UnityEngine.Debug.Messages.Any(m => m.Contains("strict_capability_unsupported", StringComparison.Ordinal)
                && m.Contains("Late.Unknown", StringComparison.Ordinal)), "late capability diagnostic did not reach the log");
        }
        finally { host.Shutdown(); }
    }),
};

var commonTests = new (string Name, Action Run)[]
{
    ("active choice without AUTO stops the actual host and restores its session", () =>
    {
        var player = new Test { hasSelection = true, selectionManager = new() { isSelectionActive = true } };
        var host = CapturingHost(player);
        host.Update();
        Check(!host.Capturing && host.AutoSelection == null, "missing AUTO kept recording");
        Check(player.EndCalls == 1, "failed choice did not end playback exactly once");
        Check(UnityEngine.Debug.Messages.Any(m => m.Contains("selection_default_missing")), "missing AUTO diagnostic absent");
        host.Shutdown();
    }),
    ("valid AUTO choice stays capturing and cancellation drops its ownership", () =>
    {
        var player = new Test { hasSelection = true, selectionManager = new() { isSelectionActive = true, defaultSelectionIndex = 0 } };
        player.selectionManager.elements!.Add(new());
        var host = CapturingHost(player);
        host.Update();
        Check(host.Capturing && host.AutoSelection != null, "valid AUTO was rejected");
        host.AutoSelectionFailed(new ExportException("selection_submit_failed", "AUTO failed"));
        Check(!host.Capturing && host.AutoSelection == null && player.EndCalls == 1, "selection hook failure did not clean up host");
        host.Shutdown();
    }),
    ("encoding mode switch resets codec and encoder while preserving user output settings", () =>
    {
        var before = Options() with { Codec = "av1", Encoder = "av1_nvenc", Container = "mkv", Width = 2560,
            Height = 1440, Fps = 60, AudioQuality = "aac320", BitrateKbps = 18000, SuperResolutionEnabled = true };
        var software = EncodingModeSelection.Change(before, "software");
        Check(software.EncodingMode == "software" && software.Codec == "h264" && software.Encoder == "libx264", "CPU mode kept a GPU-only encoder or did not default to H264");
        Check(!software.SuperResolutionEnabled && software.UpscaleAlgorithm == "bilinear", "CPU mode kept incompatible shader upscaling");
        Check(software.Width == before.Width && software.Height == before.Height && software.Fps == before.Fps
            && software.BitrateKbps == before.BitrateKbps && software.AudioQuality == before.AudioQuality
            && software.OutputDirectory == before.OutputDirectory && software.Container == before.Container, "switch reset unrelated settings");
        var hardware = EncodingModeSelection.Change(software, "hardware");
        Check(hardware.EncodingMode == "hardware" && hardware.Codec == "h264" && hardware.Encoder == "auto", "returning GPU mode kept libx264");
        Check(!hardware.SuperResolutionEnabled, "mode switch silently enabled a previously disabled feature");
        Check(EncodingModeSelection.Change(software, "software") == software, "same-mode action changed options");
        var scalar = EncodingModeSelection.Change(before with { UpscaleAlgorithm = "lanczos" }, "software");
        Check(scalar.SuperResolutionEnabled && scalar.UpscaleAlgorithm == "lanczos", "CPU-capable scaler was needlessly disabled");
    }),
    ("no hardware available still permits explicit CPU probe and selection", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Visible = true;
        var available = new FfmpegCapabilities { HardwareEncoders = Array.Empty<EncoderCapability>() };
        Set(host, "_capabilities", available);
        host.Update();
        Check(panel.LastEncoders?.Count == 0, "missing GPU must not show software as hardware");
        panel.Options = EncodingModeSelection.Change(panel.Options, "software");
        panel.ProbeRequested!();
        Check(Field<string>(host, "_probeMode") == "software", "software selection did not start software probe");
        host.Update(); host.Update();
        Check(Field<string>(host, "_capabilitiesMode") == "software", "CPU result missing mode ownership");
        Check(panel.LastEncoders?.All(e => !e.Hardware) == true && panel.LastEncoders.Count > 0, "CPU mode has stale hardware or no results");
        host.Shutdown();
    }),
    ("rapid hardware software hardware switches cancel and ignore replaced probes", () =>
    {
        var pending = new List<(string Mode, CancellationToken Token, TaskCompletionSource<FfmpegCapabilities> Work)>();
        FfmpegCapabilities.ProbeOverride = (mode, token) =>
        {
            var task = new TaskCompletionSource<FfmpegCapabilities>();
            pending.Add((mode, token, task));
            return task.Task;
        };
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Visible = true;
        panel.ProbeRequested!();
        panel.Options = EncodingModeSelection.Change(panel.Options, "software"); panel.ProbeRequested!();
        panel.Options = EncodingModeSelection.Change(panel.Options, "hardware"); panel.ProbeRequested!();
        Check(pending.Count == 3 && pending[0].Token.IsCancellationRequested && pending[1].Token.IsCancellationRequested,
            "replaced probes were not cancelled");
        pending[0].Work.SetResult(new FfmpegCapabilities());
        pending[1].Work.SetResult(new FfmpegCapabilities());
        host.Update();
        Check(panel.LastEncoders == null && Field<FfmpegCapabilities?>(host, "_capabilities") == null,
            "stale completed probe enabled export before current probe completed");
        pending[2].Work.SetResult(new FfmpegCapabilities());
        host.Update(); host.Update();
        Check(Field<string>(host, "_capabilitiesMode") == "hardware" && panel.LastEncoders?.All(e => e.Hardware) == true,
            "final mode used wrong probe results");
        host.Shutdown();
    }),
    ("export preparation rejects results belonging to another encoding mode before story load", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        var software = EncodingModeSelection.Change(Options(), "software");
        panel.Options = software;
        Call(host, "Prepare", software);
        Check(ScenarioResourceManager.Instance!.LoadedNames.Count == 0 && Field<Task<ExportSession>?>(host, "_prepare") == null,
            "stale GPU results reached story or encoder preparation");
        Check(Field<string>(host, "_probeMode") == "software", "mismatched preparation did not request current probe");
        host.Shutdown();
    }),
    ("probe failure remains retryable and switching modes does not reuse failed results", () =>
    {
        FfmpegCapabilities.ProbeOverride = (_, _) => Task.FromException<FfmpegCapabilities>(new IOException("FFmpeg unavailable"));
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Visible = true;
        panel.ProbeRequested!(); host.Update(); host.Update();
        Check(panel.LastEncoders?.Count == 0 && Field<FfmpegCapabilities?>(host, "_capabilities") == null, "failed GPU probe retained an encoder");
        Check(panel.LastStatus.Contains("CPU"), "GPU failure gives no software option guidance");
        Check(panel.StatusIsError, "probe failure did not mark the displayed status as an error");
        FfmpegCapabilities.ProbeOverride = null;
        panel.Options = EncodingModeSelection.Change(panel.Options, "software"); panel.ProbeRequested!();
        host.Update(); host.Update();
        Check(panel.LastEncoders?.Any(e => e.Name == "libx264") == true, "CPU retry after failure did not recover");
        Check(!panel.StatusIsError, "successful retry kept the error color state");
        host.Shutdown();
    }),
    ("software preparation admits absent or fully covered More Effects at the actual session boundary", () =>
    {
        foreach (bool loaded in new[] { false, true })
        {
        MoreEffectsCompatibility.IsLoaded = loaded;
        MoreEffectsCompatibility.FullClockCoverage = loaded;
        MoreEffectsCompatibility.Status = loaded ? "full" : "waiting";
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        string workspace = Path.Combine(Path.GetTempPath(), "aave-software-host-" + Guid.NewGuid().ToString("N"));
        string saves = Path.Combine(workspace, "data", "saves");
        string story = Path.Combine(saves, "fixture.aas");
        Directory.CreateDirectory(saves); File.WriteAllText(story, "fixture");
        UserSettings.Instance = new() { WorkspacePath = workspace };
        var selected = new CatalogFileInfo { block = new() { path = story }, controlPanel = new() };
        Set(host, "_selectedStory", selected); Set(host, "_selectedStoryKey", story);
        panel.Options = EncodingModeSelection.Change(Options(), "software");
        Set(host, "_capabilitiesMode", "software"); Set(host, "_probeMode", "software");
        try
        {
            Call(host, "Prepare", panel.Options);
            var session = Field<Task<ExportSession>>(host, "_prepare").GetAwaiter().GetResult();
            Check(session.Encoder == "libx264" && session.Options.EncodingMode == "software", "software mode reached wrong session encoder");
            Check(Field<string>(host, "_status").Contains("CPU 软件编码器 libx264"), "software encoder was labeled as GPU vendor");
            session.Dispose(); Set(host, "_prepare", null);
        }
        finally
        {
            host.Shutdown();
            MoreEffectsCompatibility.ResetAdmission();
            File.Delete(story); Directory.Delete(saves); Directory.Delete(Path.Combine(workspace, "data")); Directory.Delete(workspace);
            UserSettings.Instance = null;
        }
        }
    }),
    ("idle shutdown marks provider unavailable without reacquiring or faulting", () =>
    {
        var host = CreateHost();
        Check(RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned, "ready boundary not initialized");
        host.Shutdown();
        Check(!RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned, "idle shutdown acquired rendering");
        var replacement = CreateHost();
        replacement.Shutdown();
    }),
    ("completed and cancelled host sessions release after busy and native cleanup", () =>
    {
        foreach (bool cancel in new[] { false, true })
        {
            var host = CreateHost();
            var ownership = RenderControlV1.Acquire();
            Set(host, "_renderSessionOwnership", ownership);
            int released = 0;
            Action after = () =>
            {
                Check(!host.Capturing && !Field<bool>(host, "_ownsInput"), "release before native restoration");
                released++;
            };
            RenderControlV1.AfterRelease += after;
            try
            {
                if (cancel)
                {
                    Set(host, "_session", new ExportSession(Options(), "", "", "fixture", 48000, 2, true));
                    Call(host, "Cancel");
                    Field<Task>(host, "_cleanup").GetAwaiter().GetResult();
                }
                host.Update();
                Check(released == 1 && !RenderControlV1.IsRenderingOwned, "finished session did not release once");
                host.Update();
                Check(released == 1, "release repeated on later Update");
            }
            finally { RenderControlV1.AfterRelease -= after; host.Shutdown(); }
        }
    }),
    ("shutdown with session ownership notifies release only after ready becomes false", () =>
    {
        var host = CreateHost();
        Set(host, "_renderSessionOwnership", RenderControlV1.Acquire());
        int released = 0;
        Action after = () => { Check(!RenderControlV1.IsReady && !RenderControlV1.IsRenderingOwned, "shutdown event state"); released++; };
        RenderControlV1.AfterRelease += after;
        try { host.Shutdown(); Check(released == 1, "shutdown session token leaked"); }
        finally { RenderControlV1.AfterRelease -= after; }
    }),
    ("export folder restores on host restart and story changes retain it", () =>
    {
        string directory = Path.GetFullPath("artifacts/remembered-export-folder");
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        Check(panel.Options.OutputDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "first launch must use Videos itself, without an AA Exports child");
        panel.OutputDirectoryCommitted!(directory);
        host.Shutdown();
        host = CreateHost();
        panel = Field<NativeExportPanel>(host, "_panel");
        Check(panel.Options.OutputDirectory == directory, "host restart discarded remembered folder");
        panel.SetStoryTitle("another-story");
        Check(panel.Options.OutputDirectory == directory, "story selection reset remembered folder");
        panel.OutputDirectoryCommitted!("relative-folder");
        Check(Plugin.OutputDirectory == directory, "invalid folder overwrote saved preference");
        host.Shutdown();
    }),
    ("remembered folder validation does not create folders and rejects invalid values", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "aa-export-preference-" + Guid.NewGuid().ToString("N"));
        Check(OutputDirectoryPreference.TryNormalize(directory, out var normalized) && normalized == directory, "absolute folder rejected");
        Check(!Directory.Exists(directory), "remembering folder created it");
        foreach (string? invalid in new string?[] { null, "", "  ", "relative-folder", directory + "\0", directory + "\n", directory + "*", directory + "?", directory + "\"" })
            Check(!OutputDirectoryPreference.TryNormalize(invalid, out _), "invalid folder accepted");
        if (OperatingSystem.IsWindows()) Check(!OutputDirectoryPreference.TryNormalize(directory + ":stream", out _), "alternate stream accepted as folder");
        Check(OutputDirectoryPreference.Resolve("relative-folder") == Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "invalid saved configuration prevented Videos default");
        string escapedSegment = Path.GetFullPath(Path.Combine("artifacts", "native-compare-final-20261002"));
        string configValue = OutputDirectoryPreference.ToPortableValue(escapedSegment);
        Check(!configValue.Contains('\\'), "portable path must not contain NGUI escape backslashes");
        Check(OutputDirectoryPreference.TryNormalize(configValue, out var roundTrip) && roundTrip == escapedSegment, "config path round-trip changed the export folder");
    }),
    ("performance reporting never acquires a target or consumes a failing readback", () =>
    {
        var host = CreateHost();
        var options = Options();
        var session = new ExportSession(options, "", "", "fixture", 48000, 2, true);
        var frames = new FramePipeline(options, session, true) { RejectTargetAccess = true };
        Set(host, "_options", options); Set(host, "_frames", frames); Set(host, "_captured", 106L);
        try
        {
            Call(host, "ReportPerformance", true);
            Check(frames.TargetReads == 0, "diagnostic sampling touched the GPU capture path");
        }
        finally { Set(host, "_frames", null); host.Shutdown(); }
    }),
    ("readback failure cancels once even if the diagnostic sink fails and allows a new capture", () =>
    {
        foreach (bool failLog in new[] { false, true })
        {
            var host = CreateHost();
            var options = Options();
            var panel = Field<NativeExportPanel>(host, "_panel");
            panel.Options = options;
            var session = new ExportSession(options, "", "", "fixture", 48000, 2, true);
            var frames = new FramePipeline(options, session, true)
            {
                RejectTargetAccess = true,
                DrainError = new ExportException("gpu_readback_failed", "fixture original readback failure")
            };
            var player = new Test { EndAction = () => { SceneManager.Active = new() { name = "CatalogScene", isLoaded = true }; new CatalogFileInfo(); } };
            var native = new NativeCaptureScope(player, options, new UnityEngine.RenderTexture(), false);
            SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
            Set(host, "_options", options); Set(host, "_frames", frames); Set(host, "_captured", 106L);
            Set(host, "_session", session); Set(host, "_native", native); Set(host, "_player", player);
            int errorsBefore = UnityEngine.Debug.Messages.Count(m => m.Contains("failure=gpu_readback_failed"));
            Plugin.FailPerformanceLog = failLog;
            try
            {
                host.Update();
                Check(!host.Capturing && session.Cancelled && frames.Disposed && native.Disposed, "failure did not release capture and cancel encoder");
                Check(frames.TargetReads == 0, "failure diagnostics acquired another target");
                Field<Task>(host, "_cleanup").GetAwaiter().GetResult();
                for (int i = 0; i < 10; i++) host.Update();
                Check(session.Disposed && player.EndCalls == 1, "cleanup or native end was skipped or repeated");
                Check(panel.Visible && !panel.LastBusy, "failed export left settings busy");
                Check(panel.StatusIsError, "failed export did not mark its displayed status as an error");
                Check(Field<string>(host, "_status").Contains("显卡读取视频帧失败"), "cleanup masked the localized original error");
                Check(UnityEngine.Debug.Messages.Any(m => m.Contains("failure=gpu_readback_failed")
                    && m.Contains("fixture original readback failure")), "localization lost the original diagnostic from the log");
                Check(UnityEngine.Debug.Messages.Count(m => m.Contains("failure=gpu_readback_failed")) - errorsBefore == 1, "failure loop flooded the log");
                Check(panel.Options == options, "failure reset user's export settings");
                Plugin.FailPerformanceLog = false;
                var nextSession = new ExportSession(options, "", "", "fixture", 48000, 2, true);
                Set(host, "_session", nextSession);
                if (ExportBuildProfile.SinglePassEnabled) Set(host, "_frozenSave", new GenericScenarioExcelTable());
                Call(host, "Begin", new Test());
                Check(host.Capturing, "failed export prevented a fresh capture");
                Call(host, "Cancel");
                Field<Task>(host, "_cleanup").GetAwaiter().GetResult();
                host.Update();
            }
            finally { Plugin.FailPerformanceLog = false; host.Shutdown(); }
        }
    }),
    ("cancel returns to retained settings without selecting a catalog card", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options() with { Width = 1280, Height = 720, BitrateKbps = 8000 };
        var player = new Test { EndAction = () => { SceneManager.Active = new() { name = "CatalogScene", isLoaded = true }; new CatalogFileInfo(); } };
        SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
        Set(host, "_player", player);
        panel.Flow.ObserveBusy(true);
        panel.Flow.AskToCancel();
        Check(panel.Flow.ConfirmCancel(), "cancel confirmation failed");
        Call(host, "Cancel");
        host.Update(); host.Update();
        Check(player.EndCalls == 1, "cancel did not end native playback exactly once");
        Check(panel.Visible, "settings were hidden after native scene returned");
        Check(!panel.LastBusy && panel.Flow.Phase == ExportUiPhase.Settings, "returned catalog without a selected block left cancellation stuck");
        Check(panel.Options.Width == 1280 && panel.Options.Height == 720 && panel.Options.BitrateKbps == 8000, "cancel reset export options");
        // The card's .aas block is intentionally absent after a catalog scene
        // rebuild. A retained stable source key must still resolve a loader
        // shell so the second export can start without reopening the settings.
        Set(host, "_selectedStoryKey", Path.GetFullPath("fixture.aas"));
        new CatalogFileInfo { block = new() { path = null! } };
        var resolved = (ValueTuple<CatalogFileInfo, string>)CallResult(host, "ResolveStory");
        Check(resolved.Item1 != null && resolved.Item2.EndsWith("fixture.aas", StringComparison.OrdinalIgnoreCase), "retry lost the retained story source");
        host.Shutdown();
    }),
    ("returning waits for the native catalog scene before allowing retry", () =>
    {
        var host = CreateHost();
        var player = new Test();
        SceneManager.Active = new() { name = "PlayScene", isLoaded = true };
        Set(host, "_player", player);
        Call(host, "Cancel");
        new CatalogFileInfo { block = new() { path = "stale.aas" } };
        host.Update(); host.Update();
        Check(Field<bool>(host, "_returningToCatalog"), "stale catalog card incorrectly released native load guard");
        SceneManager.Active = new() { name = "CatalogScene", isLoaded = true };
        host.Update(); host.Update();
        Check(!Field<NativeExportPanel>(host, "_panel").LastBusy, "catalog scene never released guard");
        host.Shutdown();
    }),
    ("newly selected story refreshes the export title instead of reusing the previous project", () =>
    {
        var host = CreateHost();
        var panel = Field<NativeExportPanel>(host, "_panel");
        panel.Options = Options() with { Title = "previous-story" };
        AAVideoExport.Plugin.ExportHost.LoadedStoryTitle = "new-story-0";
        Studio.Scripts.StudioCommon.instance = new() { projectName = "old-project-name" };
        host.Update();
        Check(panel.LastProjectName == "new-story-0", "selected story title must take precedence over the stale Studio project name");
        panel.SetStoryTitle("new-story-0");
        Check(panel.Options.Title == "new-story-0", "opening a different story must refresh the editable default filename");
        host.Shutdown();
    })
};
if (!ExportBuildProfile.DevelopmentAuditEnabled)
{
    int commonResult = RunTests(preparationTests.Concat(commonTests).ToArray(), noAudits: true);
    int timelineResult = ExportBuildProfile.SinglePassEnabled
        ? SinglePassHostRegression.Run()
        : ProductionHostRegression.Run();
    return commonResult == 0 && timelineResult == 0 ? 0 : 1;
}

return RunTests(staticInertAuditTests.Concat(preparationTests).Concat(capabilityAuditTests)
    .Concat(commonTests).Concat(StrictHostRegression.Cases)
    .Concat(StrictHostRegression.EffectsSourceReadinessCases).ToArray(), noAudits: false);

static int RunTests((string Name, Action Run)[] tests, bool noAudits)
{
    int failed = 0;
    foreach (var test in tests)
    {
        Object.ResetRegistry();
        UnityEngine.Debug.Messages.Clear();
        Plugin.SavedOutputDirectory = "";
        FfmpegCapabilities.ProbeOverride = null;
        ScenarioResourceManager.Instance = new();
        NativeCaptureScope.AudioSampleCounts.Clear();
        NativeCaptureScope.AudioBlocks.Clear();
        NativeBgmSessionAdapter.Reset();
        ProductionAuditTrap.Calls.Clear();
        ProductionAuditTrap.Enabled = noAudits;
        try
        {
            MoreEffectsCompatibility.ResetAdmission(); StrictCapabilityInventory.Reset(); StaticInertModEvidence.Reset(); AASkipIntroExportBoundary.Reset(); CharacterVoiceDelayClock.Reset(); CharacterVoiceSessionAdapter.Reset(); RetainedMenuPlaybackBoundary.Reset(); RuntimeAttributionClient.Reset(); FrameworkGeneratedAssemblyScope.Reset(); MultilingualCaptureAdapter.Reset(); RuntimeModBoundaries.EndExport();
            test.Run();
            Check(!noAudits || ProductionAuditTrap.Calls.Count == 0, "Common production lifecycle reached an audit boundary.");
            Console.WriteLine("PASS " + test.Name);
        }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
        finally { ProductionAuditTrap.Enabled = false; }
    }
    Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed (production ExportHost lifecycle; fake native/encoder boundaries; throwing audit boundaries={noAudits})");
    return failed == 0 ? 0 : 1;
}

static ExportHost CreateHost()
{
    var host = new ExportHost(new IntPtr(1)); host.Awake();
    Set(host, "_probeAttempted", true); Set(host, "_capabilities", new FfmpegCapabilities());
    Set(host, "_probeMode", "hardware"); Set(host, "_capabilitiesMode", "hardware");
    return host;
}
static ExportHost CapturingHost(Test player)
{
    var host = CreateHost();
    var options = Options();
    Set(host, "_options", options);
    Set(host, "_session", new ExportSession(options, "", "", "fixture", 48000, 2, true));
    Set(host, "_cancellation", new CancellationTokenSource());
    if (ExportBuildProfile.SinglePassEnabled) Set(host, "_frozenSave", new GenericScenarioExcelTable());
    if (ExportBuildProfile.DevelopmentAuditEnabled)
        typeof(ExportHost).GetField("_staticInertScope",BindingFlags.Instance|BindingFlags.NonPublic)?.SetValue(host,StaticInertScope.Acquire());
    Call(host, "Begin", player);
    return host;
}
static ExportOptions Options() => new() { Title = "fixture", OutputDirectory = Path.GetFullPath("artifacts/host-test") };
static T Field<T>(ExportHost host, string name) => (T)typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
static void Set(ExportHost host, string name, object? value) => typeof(ExportHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
static void Call(ExportHost host, string name, params object[] args) => typeof(ExportHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args);
static object CallResult(ExportHost host, string name, params object[] args) => typeof(ExportHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args)!;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
