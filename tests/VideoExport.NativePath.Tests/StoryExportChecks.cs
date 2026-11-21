using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// This acceptance runner observes the real host. It never substitutes a story,
// encoder capability, strict plan, playback callback, or admission decision.
public sealed class StoryExportChecks : MonoBehaviour
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static StoryExportChecks? _active;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Stopwatch _attemptElapsed = new();
    private Harmony? _hooks;
    private ExportHost? _host;
    private NativeExportPanel? _panel;
    private ExportOptions? _originalOptions;
    private Action<string>? _originalDirectoryCommit;
    private ExportOptions? _options;
    private ExportResult? _result;
    private Task<MediaEvidence>? _verification;
    private Task<NativeBenchmarkMediaEvidence>? _benchmarkVerification;
    private MediaEvidence? _firstEvidence;
    private string _story = "", _sourceHash = "", _expectedRejection = "";
    private string _failureCode = "", _failureMessage = "", _identity = "", _firstIdentity = "";
    private string _lastPhase = "", _outputPath = "";
    private int _stage, _settleFrames, _attempt, _repeat = 2, _passed, _failed;
    private int _completedExports, _cancelledAttempts;
    private int _firstCompletedAttempt;
    private bool _cancelFirst, _cancellationRequested;
    private double _cancellationStarted;
    private long _cancellationFrame;
    private double _deadline = 1200, _cleanupDeadline, _nextHeartbeat, _selectionDeadline, _nextCatalogSnapshot;
    private double _observedFailureAt = -1;
    private long _captured, _written, _accepted, _audioSamples, _plannedFrames = -1;
    private long _performanceStartAccepted, _performanceStartWritten;
    private long _performanceStopCaptured, _performanceStopAccepted, _performanceStopWritten;
    private bool _sawAudioPass, _sawVideoPass, _submitted, _selectionRequested;
    private bool _backgroundEvidenceInstalled;
    private bool _multilingualEvidenceInstalled;
    private bool _mixedEvidenceInstalled, _originalShowPlaybackButtons;
    private string _mixedRecipe = "";
    private NativeStoryTestProfile _testProfile = null!;
    private StoryPerformanceWindow? _performanceWindow;
    private StoryPerformanceMode _performanceMode = null!;
    private string _testOptionsPath = "", _testOptionsSha256 = "", _resolvedEncoder = "";
    private ExportOptions? _effectivePerformanceOptions;
    private string _effectivePerformanceOptionsSha256 = "";
    private bool _developmentAudit, _singlePass, _wholeExportBenchmark;
    private int _observedPassBegins;
    private string _auditTimelineDirectory = "";
    private Dictionary<string, string>? _auditTimelineBefore;
    private StoryRenderControlEvidence? _renderControlEvidence;
    private readonly List<object> _panelBoundaryEvents = new();
    private readonly HashSet<string> _panelBoundaryObservedStates = new(StringComparer.Ordinal);
    private string _panelBoundary = "";
    private double _panelBoundaryStarted;

    public StoryExportChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (_stage == 99) return;
        try
        {
            if (_stage == 0) Initialize();
            if (_stage == 90)
            {
                ObserveFailureCleanup();
                return;
            }
            if (_wall.Elapsed.TotalSeconds >= _deadline)
                throw new TimeoutException("Native story acceptance exceeded its wall-clock deadline; phase=" + _lastPhase);
            if (_stage == 1) WaitForCatalog();
            else if (_stage == 2) WaitForProbe();
            else if (_stage == 3) ObserveExport();
            else if (_stage == 4) VerifyCompletedExport();
            else if (_stage == 5)
            {
                Phase("settling-before-repeat");
                if (!CatalogReady() || HostBool("Busy", property: true)) { _settleFrames = 0; return; }
                if (++_settleFrames >= 8) SubmitExport();
            }
            else if (_stage == 6)
            {
                Phase("waiting-for-matching-native-selection");
                if (_wall.Elapsed.TotalSeconds >= _selectionDeadline)
                    throw new InvalidOperationException("Native row selection did not settle within 10 seconds.");
                if (!CatalogReady()) return;
                if (++_settleFrames >= 2) SubmitExport();
            }
            else if (_stage == 7)
            {
                if (CatalogReady() && !HostBool("Busy", property: true)) SubmitExport();
            }
        }
        catch (Exception error) { FailCheck(error); }
    }

    [HideFromIl2Cpp]
    private void Initialize()
    {
        _developmentAudit = NativeAuditOutputContract.IsDevelopmentAudit(typeof(ExportOptions).Assembly);
        _singlePass = NativeAuditOutputContract.IsSinglePass(typeof(ExportOptions).Assembly);
        _performanceMode = new StoryPerformanceMode(_singlePass);
        if (_singlePass && _developmentAudit) throw new InvalidOperationException("Single-pass native acceptance must use the ordinary non-audit runtime.");
        bool requestedAudit = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_DEVELOPMENT_AUDIT") == "1";
        if (_developmentAudit != requestedAudit)
            throw new InvalidOperationException("Native runner audit mode differs from the actual production/development Core build.");
        if (!_developmentAudit)
        {
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
                if (System.Text.RegularExpressions.Regex.IsMatch((string)variable.Key, "^AA_VIDEOEXPORT_(RUNTIME_.*|STARTUP_TRACE_FILE|AUDIT.*|DEVELOPMENT_AUDIT)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("Production native acceptance inherited an audit or trace environment flag: " + variable.Key);
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "AAVideoExport.RuntimeBootstrap"))
                throw new InvalidOperationException("Production native acceptance loaded an audit bootstrap.");
        }
        string? requested = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_STORY");
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested)
            || requested.Any(char.IsControl))
            throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_STORY must name one explicit absolute existing .aas file.");
        _story = Path.GetFullPath(requested);
        if (!File.Exists(_story) || !string.Equals(Path.GetExtension(_story), ".aas", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The explicitly requested .aas file does not exist.", _story);
        _sourceHash = HashFile(_story);
        _testOptionsPath = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_OPTIONS_JSON") ?? "";
        string? optionsJson = null;
        if (_testOptionsPath.Length != 0)
        {
            if (!Path.IsPathFullyQualified(_testOptionsPath) || !File.Exists(_testOptionsPath) || new FileInfo(_testOptionsPath).Length > 65536)
                throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_OPTIONS_JSON must name one absolute existing options JSON no larger than 64 KiB.");
            optionsJson = File.ReadAllText(_testOptionsPath); _testOptionsSha256 = HashFile(_testOptionsPath);
        }
        _testProfile = NativeStoryTestProfile.ParseBenchmark(optionsJson, Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_PERF_STOP_FRAME"),
            Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_WHOLE_EXPORT_BENCHMARK") == "1");
        _wholeExportBenchmark = _testProfile.WholeExportBenchmark;
        _performanceWindow = _testProfile.CreatePerformanceWindow();
        _expectedRejection = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_EXPECT_REJECTION")?.Trim() ?? "";
        if (_expectedRejection.Length != 0) _repeat = 1;
        else if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_REPEATS") is { Length: > 0 } repeats)
        {
            if (!int.TryParse(repeats, out _repeat) || _repeat is < 1 or > 2)
                throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_REPEATS must be 1 or 2.");
        }
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS") is { Length: > 0 } timeout)
        {
            if (!double.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out _deadline)
                || _deadline is < 30 or > 7200)
                throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS must be between 30 and 7200.");
        }
        _cancelFirst = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_CANCEL_FIRST") == "1";
        if (_performanceWindow != null || _wholeExportBenchmark)
        {
            if (_cancelFirst || _expectedRejection.Length != 0
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MIXED_STORY_RECIPE"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_STORY_ASSETS"))
                || Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MULTILINGUAL") == "1"
                || Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_PREENCODE") == "1"
                || Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BGM_TRACE") == "1"
                || NativePreEncodeObservation.FontAtlasEquivalenceEnabled || _developmentAudit)
                throw new InvalidOperationException("Timed benchmark mode cannot be combined with refusal, mixed-content observers, pre-encode sampling, substituted-resource, audit or extra UI acceptance probes.");
            _repeat = 1;
        }
        if (_cancelFirst && (_expectedRejection.Length != 0 || _repeat != 2
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MIXED_STORY_RECIPE"))))
            throw new InvalidOperationException("Cancel-first acceptance requires the mixed-story recipe, no expected refusal, and two completed exports.");
        _active = this;
        _hooks = new Harmony("halocue.aa.videoexport.storyacceptance");
        _hooks.Patch(RequiredMethod("Fail"), prefix: new HarmonyMethod(typeof(StoryExportChecks), nameof(ObserveFailure)));
        StoryRejectionDiagnostics.Install(_hooks);
        _hooks.Patch(RequiredMethod("ReportCompleted"), prefix: new HarmonyMethod(typeof(StoryExportChecks), nameof(ObserveCompletion)));
        _hooks.Patch(RequiredMethod("BeginStrictPass"), postfix: new HarmonyMethod(typeof(StoryExportChecks), nameof(ObservePassBegin)));
        NativePreEncodeObservation.Install();
        NativeBgmStartObservation.Install();
        Plugin.Report("PATH-CHECK STORY START " + JsonSerializer.Serialize(new
        {
            source = _story, source_sha256_before = _sourceHash, completed_exports_requested = _performanceWindow == null ? _repeat : 0,
            deliberate_cancel_first = _cancelFirst, total_attempts_requested = _repeat + (_cancelFirst ? 1 : 0),
            expected_rejection = _expectedRejection, deadline_seconds = _deadline,
            output = Plugin.DiagnosticsRoot, provider = typeof(ExportHost).Assembly.Location,
            provider_sha256 = HashFile(typeof(ExportHost).Assembly.Location),
            harness_sha256 = HashFile(typeof(StoryExportChecks).Assembly.Location),
            options_json = _testOptionsPath, options_sha256 = _testOptionsSha256,
            performance_only = _performanceWindow != null, performance_stop_frame = _testProfile.PerformanceStopFrame,
            development_audit = _developmentAudit, single_live_pass = _singlePass, whole_export_benchmark = _wholeExportBenchmark
        }));
        _stage = 1;
        Phase("waiting-for-native-catalog");
    }

    [HideFromIl2Cpp]
    private bool CatalogReady() => SceneManager.GetActiveScene().name == "CatalogScene"
        && UserSettings.Instance?.data != null && ScenarioResourceManager.Instance != null
        && !ScenarioResourceManager.Instance.Preloading
        && Object.FindObjectsOfType<CatalogFileInfo>().Any(info => info != null && info.gameObject.activeInHierarchy);

    [HideFromIl2Cpp]
    private void WaitForCatalog()
    {
        Phase("waiting-for-native-catalog");
        ReportCatalogSnapshot();
        if (!CatalogReady()) { _settleFrames = 0; return; }
        _host = typeof(ExportHost).GetField("Current", StaticMembers)?.GetValue(null) as ExportHost;
        if (_host == null || HostBool("Busy", property: true)) { _settleFrames = 0; return; }
        if (!_developmentAudit && _auditTimelineBefore == null)
        {
            string mediaDirectory = (string)(typeof(AAVideoExport.Plugin.Plugin).GetField("TimelineReceiptDirectory", StaticMembers)?.GetValue(null)
                ?? throw new MissingFieldException("Production execution media directory is unavailable."));
            _auditTimelineDirectory = Path.Combine(Path.GetDirectoryName(mediaDirectory)!, "aave-timelines");
            _auditTimelineBefore = NativeAuditOutputContract.SnapshotAuditDirectory(_auditTimelineDirectory);
        }
        if (_expectedRejection.Length == 0 && !NativeResourcesReady())
        {
            _settleFrames = 0;
            Phase("waiting-for-native-resource-databases");
            return;
        }
        if (++_settleFrames < 8) return;
        ValidateNativeSource();
        BackgroundStoryEvidence.Install(_host);
        _backgroundEvidenceInstalled = true;
        MultilingualStoryEvidence.Install(_host);
        _multilingualEvidenceInstalled = true;
        _panel = HostField<NativeExportPanel>("_panel");
        _originalOptions = _panel.Options;
        _originalDirectoryCommit = _panel.OutputDirectoryCommitted;
        _mixedRecipe = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MIXED_STORY_RECIPE") ?? "";
        if (_mixedRecipe.Length != 0)
        {
            if (!Path.IsPathFullyQualified(_mixedRecipe) || !File.Exists(_mixedRecipe))
                throw new InvalidOperationException("Mixed story evidence requires its explicit authored recipe file.");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_STORY_ASSETS")))
                throw new InvalidOperationException("Authored mixed-story evidence cannot be combined with a substituted background resolver fixture.");
            var controls = typeof(NativeExportPanel).GetProperty("ShowPlaybackButtons", InstanceMembers)!;
            _originalShowPlaybackButtons = (bool)controls.GetValue(_panel)!;
            controls.SetValue(_panel, true);
            ControlledStoryExportEvidence.Install(_host, _mixedRecipe, "AUTO");
            _mixedEvidenceInstalled = true;
            Plugin.Report("PATH-CHECK STORY mixed-evidence=authored-input; original-controls-visible=true; recipe=" + _mixedRecipe);
        }
        // The real panel commits a directory when changing phases or disposing.
        // Test output must not become the user's remembered export directory.
        _panel.OutputDirectoryCommitted = null;
        _options = CreateOptions(1);
        _panel.Options = _options;
        if (_panel.ProbeRequested == null) throw new InvalidOperationException("The production encoder probe callback is absent.");
        _panel.ProbeRequested();
        _stage = 2;
        Phase("real-" + _options.EncodingMode + "-encoder-probe");
    }

    [HideFromIl2Cpp]
    private ExportOptions CreateOptions(int attempt)
    {
        string directory = Path.GetFullPath(Path.Combine(Plugin.DiagnosticsRoot, "story-exports"));
        Directory.CreateDirectory(directory);
        return _testProfile.CreateOptions(directory, "native-story-" + Environment.ProcessId + "-" + attempt.ToString("00", CultureInfo.InvariantCulture));
    }

    [HideFromIl2Cpp]
    private void WaitForProbe()
    {
        Phase("real-" + _options!.EncodingMode + "-encoder-probe");
        if (HostField<object?>("_probe") != null) return;
        var capabilities = HostField<FfmpegCapabilities?>("_capabilities");
        if (capabilities == null) throw new InvalidOperationException("The real encoder probe returned no capabilities; status=" + HostField<string>("_status"));
        _resolvedEncoder = _testProfile.ValidateProbe(_options, HostField<string>("_capabilitiesMode"), capabilities.Encoders, SystemInfo.graphicsDeviceName);
        if (capabilities.PickForMode(_options) != _resolvedEncoder)
            throw new InvalidOperationException("The native capability policy selected a different encoder.");
        Plugin.Report("PATH-CHECK STORY PROBE " + _resolvedEncoder + "=passed-real-encode; mode=" + _options.EncodingMode);
        SubmitExport();
    }

    [HideFromIl2Cpp]
    private void ValidateNativeSource()
    {
        string resolved = ScenarioResourceManager.GetPersistentFilePath("saves", Path.GetFileNameWithoutExtension(_story), ".aas");
        if (!SamePath(_story, resolved))
            throw new InvalidOperationException("The explicit story does not match AA's actual workspace resolution; requested="
                + _story + "; native=" + resolved);
        if (!string.Equals(HashFile(_story), _sourceHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The story source changed during acceptance.");
    }

    [HideFromIl2Cpp]
    private static bool NativeResourcesReady()
    {
        try
        {
            var resources = ScenarioResourceManager.Instance;
            var characters = CharacterManager.Instance;
            return resources != null && resources.AreDbsLoaded && resources.ExcelDb != null
                && resources.ScenarioCharacterNameQuery != null && !resources.Preloading
                && characters != null && !characters.Preloading && characters.characterHashDict != null
                && characters.characterHashDict.ByteBuffer != null && characters.characterHashDict.DataListLength > 0;
        }
        catch { return false; }
    }

    [HideFromIl2Cpp]
    private static CatalogFileItem[] NativeRows()
    {
        var rows = new List<CatalogFileItem>();
        foreach (var item in Resources.FindObjectsOfTypeAll<CatalogFileItem>())
            if (item != null && item.gameObject.scene.name == "CatalogScene") rows.Add(item);
        var items = Catalog.Instance?.items;
        if (items != null)
            for (int index = 0; index < items.Count; index++)
                if (items[index] != null && !rows.Any(row => row.Pointer == items[index].Pointer)) rows.Add(items[index]);
        return rows.OrderByDescending(row => row.gameObject.activeInHierarchy)
            .ThenByDescending(row => ParentGroup(row)?.gameObject.activeInHierarchy == true).ToArray();
    }

    [HideFromIl2Cpp]
    private void SelectExactNativeRow()
    {
        if (_selectionRequested)
        {
            if (_wall.Elapsed.TotalSeconds >= _selectionDeadline)
                throw new InvalidOperationException("Native row selection did not populate the matching story detail within 10 seconds.");
            return;
        }
        var row = NativeRows().FirstOrDefault(item => item.infoBlock != null && SamePath(item.infoBlock.path, _story));
        if (row == null)
            throw new InvalidOperationException("The requested story has no matching native catalog row/detail; arbitrary coroutine owners are not allowed.");
        if (!row.gameObject.activeInHierarchy)
        {
            var group = ParentGroup(row);
            if (group == null || !group.gameObject.activeInHierarchy || group.expanded)
                throw new InvalidOperationException("The exact source row is hidden outside an active collapsed native group; see CATALOG layout diagnostics.");
            _selectionDeadline = _wall.Elapsed.TotalSeconds + 10;
            _settleFrames = 0;
            _stage = 6;
            group.Expand();
            Plugin.Report("PATH-CHECK STORY SELECT expanded-native-source-group source=" + _story);
            return;
        }
        _selectionRequested = true;
        _selectionDeadline = _wall.Elapsed.TotalSeconds + 10;
        _settleFrames = 0;
        _stage = 6;
        bool accepted = row.Select(false);
        Plugin.Report("PATH-CHECK STORY SELECT " + JsonSerializer.Serialize(new
        {
            source = _story, native_select_accepted = accepted,
            mode = Catalog.mode.ToString(), row_active = row.gameObject.activeInHierarchy,
            row_hierarchy = Hierarchy(row.transform)
        }));
        if (!accepted) throw new InvalidOperationException("The native catalog rejected selection of the exact story row.");
    }

    [HideFromIl2Cpp]
    private void ReportCatalogSnapshot(bool force = false)
    {
        if (!force && _wall.Elapsed.TotalSeconds < _nextCatalogSnapshot) return;
        _nextCatalogSnapshot = _wall.Elapsed.TotalSeconds + 15;
        var details = Resources.FindObjectsOfTypeAll<CatalogFileInfo>()
            .Where(info => info != null && info.gameObject.scene.name == "CatalogScene")
            .Select(info => new
            {
                active = info.gameObject.activeInHierarchy,
                basename = DiagnosticRead(() => Path.GetFileName(info.block?.path ?? "")),
                extension = DiagnosticRead(() => Path.GetExtension(info.block?.path ?? "")),
                matches_source = DiagnosticRead(() => SamePath(info.block?.path, _story)),
                control_panel_active = DiagnosticRead(() => info.controlPanel != null && info.controlPanel.activeInHierarchy),
                legacy = DiagnosticRead(() => info.legacy),
                hierarchy = DiagnosticRead(() => Hierarchy(info.transform))
            }).ToArray();
        var rows = NativeRows().Select(item => new
        {
            active = DiagnosticRead(() => item.gameObject.activeInHierarchy),
            basename = DiagnosticRead(() => Path.GetFileName(item.infoBlock?.path ?? "")),
            extension = DiagnosticRead(() => Path.GetExtension(item.infoBlock?.path ?? "")),
            matches_source = DiagnosticRead(() => SamePath(item.infoBlock?.path, _story)),
            hierarchy = DiagnosticRead(() => Hierarchy(item.transform)),
            group_active = DiagnosticRead(() => ParentGroup(item)?.gameObject.activeInHierarchy),
            group_expanded = DiagnosticRead(() => ParentGroup(item)?.expanded)
        }).ToArray();
        Plugin.Report("PATH-CHECK STORY CATALOG " + JsonSerializer.Serialize(new
        {
            scene = SceneManager.GetActiveScene().name,
            mode = DiagnosticRead(() => Catalog.mode.ToString()),
            native_ui_profile = DiagnosticRead(() => Catalog.Instance?.currentProfileIndex),
            profiles = NativeProfileSnapshot(),
            native_resources_ready = NativeResourcesReady(),
            dbs_loaded = DiagnosticRead(() => ScenarioResourceManager.Instance?.AreDbsLoaded),
            excel_database_present = DiagnosticRead(() => ScenarioResourceManager.Instance?.ExcelDb != null),
            character_query_present = DiagnosticRead(() => ScenarioResourceManager.Instance?.ScenarioCharacterNameQuery != null),
            resources_preloading = DiagnosticRead(() => ScenarioResourceManager.Instance?.Preloading),
            character_manager_present = CharacterManager.Instance != null,
            characters_preloading = DiagnosticRead(() => CharacterManager.Instance?.Preloading),
            character_table_buffer_present = DiagnosticRead(() => CharacterManager.Instance?.characterHashDict?.ByteBuffer != null),
            character_table_rows = DiagnosticRead(() => CharacterManager.Instance?.characterHashDict?.DataListLength),
            details, rows
        }));
    }

    [HideFromIl2Cpp]
    private static object? DiagnosticRead(Func<object?> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable:" + error.GetType().Name; }
    }

    [HideFromIl2Cpp]
    private static EpisodeGroup? ParentGroup(CatalogFileItem item)
    {
        for (var parent = item.transform.parent; parent != null; parent = parent.parent)
        {
            var group = parent.GetComponent<EpisodeGroup>();
            if (group != null) return group;
        }
        return null;
    }

    [HideFromIl2Cpp]
    private static string Hierarchy(Transform transform)
    {
        var elements = new List<string>();
        for (var current = transform; current != null; current = current.parent)
            elements.Add(current.name + (current.gameObject.activeSelf ? "[on]" : "[off]"));
        elements.Reverse();
        return string.Join("/", elements);
    }

    [HideFromIl2Cpp]
    private static object[] NativeProfileSnapshot()
    {
        var result = new List<object>();
        var profiles = Catalog.Instance?.uiProfiles;
        if (profiles != null)
            for (int index = 0; index < profiles.Count; index++)
            {
                var root = profiles[index]?.root;
                result.Add(new { index, active = root != null && root.activeInHierarchy,
                    hierarchy = root == null ? "" : Hierarchy(root.transform) });
            }
        return result.ToArray();
    }

    [HideFromIl2Cpp]
    private void ValidateFreshNativeScenario()
    {
        var table = ScenarioResourceManager.Instance.LoadGenericScenario(Path.GetFileNameWithoutExtension(_story));
        if (table == null || table.ByteBuffer == null || table.DataListLength <= 0)
            throw new InvalidOperationException("The exact source did not produce a non-empty scenario table through native LoadGenericScenario.");
        Plugin.Report("PATH-CHECK STORY SOURCE-PREFLIGHT " + JsonSerializer.Serialize(new
        {
            source = _story, native_loader = "LoadGenericScenario", scenario_rows = table.DataListLength,
            source_sha256 = HashFile(_story)
        }));
    }

    [HideFromIl2Cpp]
    private void SubmitExport()
    {
        if (!CatalogReady() || HostBool("Busy", property: true))
            throw new InvalidOperationException("Native catalog/host is not ready for a new export.");
        if (!CloseIdlePanel("before-attempt")
            || _expectedRejection.Length == 0 && !StoryRenderControlEvidence.IsIdleConsumerReady())
        {
            if (_wall.Elapsed.TotalSeconds - _panelBoundaryStarted >= 8)
                throw new TimeoutException("Idle export panel and actual Azurite mapping did not settle before a new attempt.");
            _stage = 7; Phase("closing-idle-panel-before-attempt"); return;
        }
        ValidateNativeSource();
        ReportCatalogSnapshot(force: !_selectionRequested);
        if (_expectedRejection.Length == 0 && !NativeResourcesReady())
            throw new InvalidOperationException("AA's native resource databases/character table are not ready; see CATALOG diagnostics.");
        var cards = Object.FindObjectsOfType<CatalogFileInfo>()
            .Where(info => info != null && info.gameObject.activeInHierarchy).ToArray();
        var selected = cards.FirstOrDefault(info => info.block != null && SamePath(info.block.path, _story));
        if (selected == null)
        {
            SelectExactNativeRow();
            return;
        }
        _selectionRequested = false;
        if (_expectedRejection.Length == 0) ValidateFreshNativeScenario();
        SetHostField("_selectedStory", selected);
        SetHostField("_selectedStoryKey", _story);
        var selection = ((CatalogFileInfo Loader, string Path))RequiredMethod("ResolveStory").Invoke(_host, null)!;
        if (!SamePath(selection.Path, _story)) throw new InvalidOperationException("The production host resolved a different story.");
        _attempt++;
        if (_expectedRejection.Length == 0)
            (_renderControlEvidence ??= new StoryRenderControlEvidence()).BeginAttempt(_attempt);
        _options = CreateOptions(_attempt);
        _options.Validate();
        _outputPath = Path.Combine(_options.OutputDirectory, _options.Title + "." + _options.Container);
        if (File.Exists(_outputPath) || Directory.Exists(_outputPath))
            throw new IOException("Acceptance output already exists; refusing to overwrite " + _outputPath);
        _panel!.Options = _options;
        _result = null;
        _failureCode = _failureMessage = _identity = "";
        if (!_developmentAudit) _identity = NativeAuditOutputContract.InputIdentity(_sourceHash, _options);
        _observedFailureAt = -1;
        _captured = _written = _accepted = _audioSamples = 0;
        _performanceStartAccepted = _performanceStartWritten = 0;
        _performanceStopCaptured = _performanceStopAccepted = _performanceStopWritten = 0;
        _plannedFrames = -1;
        _sawAudioPass = _sawVideoPass = false;
        _observedPassBegins = 0;
        _cancellationRequested = false;
        _submitted = true;
        _stage = 3;
        Plugin.Report("PATH-CHECK STORY SUBMIT " + JsonSerializer.Serialize(new
        {
            attempt = _attempt, completed_exports = _completedExports, source = _story, output = _outputPath,
            intended_outcome = _performanceWindow != null ? "bounded-performance-only-no-movie" : _cancelFirst && _attempt == 1 ? (_singlePass ? "cancel-single-live-pass" : "cancel-audio-preparation") : "complete",
            single_live_pass = _singlePass,
            matched_card = selected.block != null && SamePath(selected.block.path, _story),
            strict = _options.StrictSynchronization, encoder = _resolvedEncoder, fps = _options.Fps, sample_rate = _options.AudioSampleRate,
            encoding_mode = _options.EncodingMode, width = _options.Width, height = _options.Height,
            container = _options.Container, audio_quality = _options.AudioQuality, bitrate_kbps = _options.BitrateKbps,
            rate_control = _options.RateControl, super_resolution = _options.SuperResolutionEnabled
        }));
        NativePreEncodeObservation.BeginAttempt(_attempt, _host!, _options, Plugin.DiagnosticsRoot);
        NativeBgmStartObservation.BeginAttempt(_attempt, _host!, Plugin.DiagnosticsRoot);
        _attemptElapsed.Restart();
        RequiredMethod("Prepare").Invoke(_host, new object[] { _options });
        ObserveExport();
    }

    [HideFromIl2Cpp]
    private void ObserveExport()
    {
        _renderControlEvidence?.ObserveOwned();
        ReadCounters();
        bool busy = HostBool("Busy", property: true);
        string phase = HostField<object?>("_cleanup") != null ? "cleanup"
            : HostField<object?>("_prepare") != null ? "preparing-encoder"
            : HostField<object?>("_finalize") != null ? "final-media-verification"
            : HostBool("_strictReplayPending") ? "returning-for-video-replay"
            : HostBool("Capturing", property: true) ? (HostBool("_strictPriming") ? "mixer-primer"
                : HostBool("_strictPreparing") ? "audio-preparation" : _singlePass ? "single-live-capture" : "video-replay")
            : HostBool("_armed") || HostBool("_nativeLoadPending") ? "native-story-load"
            : busy ? "returning-to-catalog" : "idle";
        Phase(phase);
        if (_failureCode.Length != 0)
        {
            if (_expectedRejection.Length == 0 || _failureCode != _expectedRejection)
                throw new InvalidOperationException("Unexpected export rejection: " + _failureCode + ": " + _failureMessage);
            if (busy)
            {
                if (_wall.Elapsed.TotalSeconds - _observedFailureAt >= 10)
                    throw new InvalidOperationException("Expected refusal did not finish native cleanup within 10 seconds: " + _failureCode);
                return;
            }
            if (_result != null || File.Exists(_outputPath) || _sawAudioPass || _sawVideoPass || _captured != 0)
                throw new InvalidOperationException("Expected strict refusal occurred after playback/output had already begun.");
            if (!HostBool("_statusIsError")) throw new InvalidOperationException("Strict refusal was not presented as an error.");
            if (!CloseIdlePanel("expected-refusal")) return;
            _passed++;
            Plugin.Report("PATH-CHECK PASS expected-strict-refusal code=" + _failureCode);
            Finish();
            return;
        }
        if (_cancellationRequested) { ObserveCancelledExport(busy); return; }
        if (_wholeExportBenchmark && HostBool("Capturing", property: true) && !HostBool("_strictPriming")) ObservePerformanceOptions();
        if (_performanceWindow != null)
        {
            _performanceMode.ValidateObservedPass(_sawAudioPass, _sawVideoPass, HostBool("_strictReplayPending"), _result != null, _observedPassBegins);
            if (HostBool("Capturing", property: true) && HostBool("_strictPreparing") != _singlePass && !HostBool("_strictPriming"))
            {
                ObservePerformanceOptions();
                bool wasStarted = _performanceWindow.HasStarted;
                bool finishedWindow = _performanceWindow.Observe(_captured, HostField<Stopwatch>("_elapsed").Elapsed.TotalSeconds);
                if (!wasStarted && _performanceWindow.HasStarted) { _performanceStartAccepted = _accepted; _performanceStartWritten = _written; }
                if (finishedWindow)
                {
                    _performanceMode.ValidateStoppedWindow(_captured, _accepted, _written);
                    _performanceStopCaptured = _captured; _performanceStopAccepted = _accepted; _performanceStopWritten = _written;
                    ObservePerformanceOptions(validateAgain: true);
                    _cancellationRequested = true; _cancellationFrame = _captured; _cancellationStarted = _wall.Elapsed.TotalSeconds;
                    Plugin.Report("PATH-CHECK PERF WINDOW " + JsonSerializer.Serialize(new
                    { scope = "bounded-performance-only", phase = _performanceMode.Phase, measurement = _performanceWindow.Snapshot(),
                        accepted_frames = _accepted, encoded_frames = _written, accepted_in_window = _accepted - _performanceStartAccepted,
                        encoded_in_window = _written - _performanceStartWritten, options_sha256 = _effectivePerformanceOptionsSha256 }));
                    RequiredMethod("Cancel").Invoke(_host, null);
                    Phase("bounded-performance-cancellation-cleanup");
                    return;
                }
            }
        }
        if (_cancelFirst && _attempt == 1 && (_singlePass ? _sawVideoPass && !_sawAudioPass : _sawAudioPass && !_sawVideoPass) && _captured >= 12
            && HostBool("Capturing", property: true) && HostBool("_strictPreparing") != _singlePass && !HostBool("_strictPriming"))
        {
            ControlledStoryExportEvidence.DeclareCancellation(_attempt, _captured);
            _cancellationRequested = true; _cancellationFrame = _captured;
            _cancellationStarted = _wall.Elapsed.TotalSeconds;
            Plugin.Report("PATH-CHECK STORY DELIBERATE-CANCEL " + JsonSerializer.Serialize(new
            { attempt = _attempt, preparation_frames = _cancellationFrame, output = _outputPath }));
            RequiredMethod("Cancel").Invoke(_host, null);
            Phase("deliberate-cancellation-cleanup");
            return;
        }
        if (_result != null && !busy)
        {
            if (!CloseIdlePanel("completed-export"))
            { Phase("closing-idle-panel-after-completion"); return; }
            if (_renderControlEvidence?.WaitForRestoration() == false)
            { Phase("waiting-for-azurite-restoration"); return; }
            NativePreEncodeObservation.FinishAttempt("completed");
            NativeBgmStartObservation.FinishAttempt("completed");
            if (_expectedRejection.Length != 0)
                throw new InvalidOperationException("Expected strict refusal " + _expectedRejection + " but export completed.");
            if (_singlePass ? _sawAudioPass || !_sawVideoPass || _observedPassBegins != 1 : !_sawAudioPass || !_sawVideoPass || _observedPassBegins != 2)
                throw new InvalidOperationException("The actual playback/pass count differs from the loaded single/two-pass build profile.");
            if (_result.Frames <= 0 || _result.Frames != _captured || _result.Frames != _written
                || _plannedFrames != _result.Frames || !SamePath(_result.OutputPath, _outputPath) || !File.Exists(_outputPath))
                throw new InvalidOperationException("Completed output/counters do not match the strict plan and requested path.");
            string ffprobe = (string)(typeof(AAVideoExport.Plugin.Plugin).GetField("FfprobePath", StaticMembers)?.GetValue(null)
                ?? throw new MissingFieldException("The production ffprobe path is unavailable."));
            string ffmpeg = (string)(typeof(AAVideoExport.Plugin.Plugin).GetField("FfmpegPath", StaticMembers)?.GetValue(null)
                ?? throw new MissingFieldException("The production ffmpeg path is unavailable."));
            var result = _result;
            string evidencePath = Path.Combine(Plugin.DiagnosticsRoot, "story-media-" + _attempt.ToString("00", CultureInfo.InvariantCulture) + ".json");
            if (_wholeExportBenchmark)
            {
                var options = _options!;
                _benchmarkVerification = Task.Run(async () => {
                    string json = await ProbeMovie(ffprobe, result.OutputPath, evidencePath).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    return NativeBenchmarkMedia.Inspect(document.RootElement, options, result.Frames);
                });
            }
            else
            {
                NativePcmContent? originalPcm = _mixedEvidenceInstalled && !_developmentAudit ? ControlledStoryExportEvidence.MoviePcm(_attempt) : null;
                _verification = Task.Run(() => VerifyMedia(ffmpeg, ffprobe, result, evidencePath, originalPcm));
            }
            _stage = 4;
            Phase("independent-media-inspection");
        }
        else if (!busy && _result == null)
            throw new InvalidOperationException("Export returned idle without a completion or explicit refusal; status=" + HostField<string>("_status"));
    }

    [HideFromIl2Cpp]
    private void ObserveCancelledExport(bool busy)
    {
        if (_wall.Elapsed.TotalSeconds - _cancellationStarted >= 30)
            throw new TimeoutException("Deliberately cancelled original capture did not restore within 30 seconds.");
        if (_result != null || File.Exists(_outputPath) || Directory.Exists(_outputPath)
            || File.Exists(_outputPath + ".runtime-recipes.json") || Directory.Exists(_outputPath + ".runtime-recipes.json"))
            throw new InvalidOperationException("Cancelled attempt published a final movie or runtime-recipe sidecar.");
        if (busy || !CatalogReady()) { Phase("deliberate-cancellation-cleanup"); return; }
        if (!CloseIdlePanel("cancelled-export"))
        { Phase("closing-idle-panel-after-cancellation"); return; }
        if (!(_renderControlEvidence ?? throw new InvalidOperationException("Actual Azurite observation was not installed.")).WaitForRestoration("cancelled"))
        { Phase("waiting-for-cancelled-azurite-restoration"); return; }
        if ((_singlePass ? _sawAudioPass || !_sawVideoPass || _observedPassBegins != 1 : !_sawAudioPass || _sawVideoPass)
            || _cancellationFrame < 12 || HostBool("_statusIsError"))
            throw new InvalidOperationException("Deliberate cancellation did not stop meaningful original capture cleanly.");
        RequireCancellationScopesReleased();
        NativePreEncodeObservation.FinishAttempt("cancelled");
        NativeBgmStartObservation.FinishAttempt("cancelled");
        ValidateNativeSource();
        if (_performanceWindow != null)
        {
            _cancelledAttempts++;
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "story-performance-only.json"), JsonSerializer.Serialize(new
            {
                Scope = "Bounded " + _performanceMode.Phase + " performance only; no movie published and no full-content equivalence claim.",
                Phase = _performanceMode.Phase, Measurement = _performanceWindow.Snapshot(),
                CapturedFrames = _performanceStopCaptured, AcceptedFrames = _performanceStopAccepted, EncodedFrames = _performanceStopWritten,
                AcceptedInWindow = _performanceStopAccepted - _performanceStartAccepted, EncodedInWindow = _performanceStopWritten - _performanceStartWritten,
                ObservedPassBegins = _observedPassBegins,
                RequestedOptions = _options, EffectiveOptions = _effectivePerformanceOptions, EffectiveOptionsSha256 = _effectivePerformanceOptionsSha256,
                InputOptionsPath = _testOptionsPath, InputOptionsSha256 = _testOptionsSha256, Encoder = _resolvedEncoder,
                HarnessSha256 = HashFile(typeof(StoryExportChecks).Assembly.Location), ProviderSha256 = HashFile(typeof(ExportHost).Assembly.Location),
                Source = _story, SourceSha256 = HashFile(_story), CompletedMovies = 0, NativeScopesReleased = true, ActualAzuriteRestored = true,
                Movie = _outputPath, MovieExists = File.Exists(_outputPath), SidecarExists = File.Exists(_outputPath + ".runtime-recipes.json"),
                CleanupSeconds = _wall.Elapsed.TotalSeconds - _cancellationStarted
            }, new JsonSerializerOptions { WriteIndented = true }));
            _passed++; _submitted = false;
            Plugin.Report("PATH-CHECK PASS bounded-performance-" + _performanceMode.Phase + "-cancelled-cleanly; completed_movies=0");
            Finish();
            return;
        }
        ControlledStoryExportEvidence.ConfirmCancellationCleanup(_attempt);
        _cancelledAttempts++;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, $"story-cancelled-attempt-{_attempt:00}.json"), JsonSerializer.Serialize(new
        {
            Attempt = _attempt, Outcome = _singlePass ? "declared-cancelled-single-live-pass" : "declared-cancelled-audio-preparation", CapturedFrames = _cancellationFrame,
            Movie = _outputPath, MovieExists = File.Exists(_outputPath), SidecarExists = File.Exists(_outputPath + ".runtime-recipes.json"),
            NativeScopesReleased = true, ActualAzuriteRestored = true, SourceSha256 = HashFile(_story),
            CleanupSeconds = _wall.Elapsed.TotalSeconds - _cancellationStarted
        }, new JsonSerializerOptions { WriteIndented = true }));
        _passed++;
        Plugin.Report("PATH-CHECK PASS deliberate-cancel-cleanup-before-real-retry attempt=" + _attempt);
        _submitted = false; _settleFrames = 0; _stage = 5;
        Phase("settling-before-retry-after-cancellation");
    }

    [HideFromIl2Cpp]
    private void RequireCancellationScopesReleased()
    {
        if (!HeldScopesReleased(out string held)) throw new InvalidOperationException("Cancelled attempt retains production scope " + held);
        foreach (var surface in new[] { ("NativeExportClock", "IsActive"), ("CharacterVoiceDelayClock", "Active"),
            ("ControlledBackgroundVideoAdapter", "Active"), ("MultilingualCaptureAdapter", "Active") })
        {
            var type = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin." + surface.Item1, true)!;
            if ((bool)type.GetProperty(surface.Item2, StaticMembers)!.GetValue(null)!)
                throw new InvalidOperationException("Cancelled attempt retains active clock/adapter " + surface.Item1);
        }
        foreach (var clockField in new[] { ("MoreEffectsCompatibility", "_clock"), ("FlandrePortraitClock", "Clock") })
        {
            var type = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin." + clockField.Item1, true)!;
            var clock = type.GetField(clockField.Item2, StaticMembers)!.GetValue(null)!;
            if ((bool)clock.GetType().GetProperty("Active", InstanceMembers)!.GetValue(clock)!)
                throw new InvalidOperationException("Cancelled attempt retains adapted animation time " + clockField.Item1);
        }
        var boundaries = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.RuntimeModBoundaries", true)!;
        if ((bool)boundaries.GetField("_active", StaticMembers)!.GetValue(null)!
            || HostBool("_strictRandomOwned") || HostBool("_strictStartupBoundaryAcquired"))
            throw new InvalidOperationException("Cancelled attempt retains dependency/startup/random scope ownership.");
    }

    [HideFromIl2Cpp]
    private void ObservePerformanceOptions(bool validateAgain = false)
    {
        if (_effectivePerformanceOptions != null && !validateAgain) return;
        var effective = HostField<ExportOptions>("_options");
        var session = HostField<ExportSession?>("_session") ?? throw new InvalidOperationException("Performance sample has no prepared production encoder session.");
        string actualEncoder = (string)(typeof(ExportSession).GetField("encoder", InstanceMembers)?.GetValue(session)
            ?? throw new MissingFieldException("The actual prepared production encoder is unavailable."));
        _testProfile.ValidateEffectiveOptions(_options!, effective, _resolvedEncoder, actualEncoder, session.OutputPath);
        string json = JsonSerializer.Serialize(effective);
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        if (_effectivePerformanceOptions != null && hash != _effectivePerformanceOptionsSha256)
            throw new InvalidOperationException("Production export options changed inside the measured performance attempt.");
        if (_effectivePerformanceOptions == null)
        {
            _effectivePerformanceOptions = effective; _effectivePerformanceOptionsSha256 = hash;
            Plugin.Report("PATH-CHECK PERF EFFECTIVE " + JsonSerializer.Serialize(new
            { options = effective, options_sha256 = hash, encoder = actualEncoder, output = session.OutputPath, phase = _performanceMode.Phase }));
        }
    }

    [HideFromIl2Cpp]
    private bool HeldScopesReleased(out string held)
    {
        held = "";
        if (_host == null) return true;
        foreach (string field in new[] { "_native", "_captureLoop", "_session", "_frames", "_voiceSession",
            "_retainedMenuLease", "_staticInertScope", "_recipeScope", "_frameworkScope", "_effectsSourceInput", "_strictStartupBoundary", "_renderSessionOwnership",
            "_preparedAudio", "_strictLedger", "_strictPlan", "_strictSandboxTrace" })
            if (HostField<object?>(field) != null) { held = field; return false; }
        return true;
    }

    [HideFromIl2Cpp]
    private bool CloseIdlePanel(string boundary)
    {
        string context = _attempt + ":" + boundary;
        if (_panelBoundary != context)
        {
            _panelBoundary = context; _panelBoundaryStarted = _wall.Elapsed.TotalSeconds;
            _panelBoundaryObservedStates.Clear();
        }
        bool hostBusy = _host != null && HostBool("Busy", property: true);
        bool scopesReleased = HeldScopesReleased(out string held);
        if (_panel == null && _host != null) _panel = HostField<NativeExportPanel>("_panel");
        if (hostBusy || !scopesReleased)
        {
            RecordPanelBoundary(boundary, "waiting-for-host-work", hostBusy, held);
            if (_wall.Elapsed.TotalSeconds - _panelBoundaryStarted >= 8)
                throw new TimeoutException("Host work/scopes did not release before normal modal close: " + context);
            return false;
        }
        RecordPanelBoundary(boundary, "host-idle-before-modal-close", false, "");
        if (_panel != null && (_panel.Visible || PanelOwnsRendering()))
        {
            // Exercise the normal user-facing close path. Its busy guard may
            // still reflect the prior Draw; reobserve on the next Update.
            // Never dispose a private lease or force FPS/clock settings.
            _panel.Visible = false;
            RecordPanelBoundary(boundary, "normal-visible-false-requested", false, "");
        }
        bool closed = (_panel == null || !_panel.Visible && !PanelOwnsRendering())
            && !AAVideoExport.Integration.RenderControlV1.IsRenderingOwned;
        if (closed) RecordPanelBoundary(boundary, "modal-boundary-released", false, "");
        else if (_wall.Elapsed.TotalSeconds - _panelBoundaryStarted >= 8)
            throw new TimeoutException("Normal idle export-panel close did not release its modal boundary: " + context);
        return closed;
    }

    [HideFromIl2Cpp]
    private bool PanelOwnsRendering() => _panel != null
        && typeof(NativeExportPanel).GetField("_renderOwnership", InstanceMembers)!.GetValue(_panel) != null;

    [HideFromIl2Cpp]
    private void RecordPanelBoundary(string boundary, string action, bool hostBusy, string held)
    {
        bool visible = _panel?.Visible ?? false;
        bool panelBusy = _panel != null && (bool)typeof(NativeExportPanel).GetField("_busy", InstanceMembers)!.GetValue(_panel)!;
        bool panelOwned = PanelOwnsRendering();
        bool renderingOwned = AAVideoExport.Integration.RenderControlV1.IsRenderingOwned;
        string state = string.Join("|", _attempt, boundary, action, hostBusy, held, visible, panelBusy, panelOwned, renderingOwned);
        if (!_panelBoundaryObservedStates.Add(state)) return;
        var entry = new { attempt = _attempt, boundary, action, host_busy = hostBusy, held_host_scope = held,
            panel_visible = visible, panel_busy = panelBusy, panel_owns_rendering = panelOwned,
            rendering_owned = renderingOwned, wall_seconds = _wall.Elapsed.TotalSeconds,
            normal_visible_setter_only = true };
        _panelBoundaryEvents.Add(entry);
        Plugin.Report("PATH-CHECK STORY PANEL-BOUNDARY " + JsonSerializer.Serialize(entry));
    }

    [HideFromIl2Cpp]
    private void ObserveFailureCleanup()
    {
        bool scopesReleased = HeldScopesReleased(out string held);
        bool owned = AAVideoExport.Integration.RenderControlV1.IsRenderingOwned;
        bool busy = _host != null && HostBool("Busy", property: true);
        if (_wall.Elapsed.TotalSeconds >= _cleanupDeadline)
        {
            Plugin.Report("PATH-CHECK STORY CLEANUP bounded-wait-expired; " + JsonSerializer.Serialize(new
            { busy, render_owned = owned, held_scope = held, production_state_overridden = false }));
            Finish(); return;
        }
        // Host.Busy excludes held scopes; its idle panel also owns a legitimate
        // nested lease. Wait for Host cleanup, then exercise normal modal close.
        if (busy || !scopesReleased) return;
        try
        {
            if (!CloseIdlePanel("failed-export")) return;
            if (_renderControlEvidence?.WaitForFailureRestoration() == false) return;
            Plugin.Report(_renderControlEvidence == null
                ? "PATH-CHECK STORY CLEANUP production-scopes-released; failure preceded render observation"
                : "PATH-CHECK STORY CLEANUP production-scopes-released; actual-render-restoration-settled");
        }
        catch (Exception error)
        {
            _failed++;
            Plugin.Report("PATH-CHECK FAIL failure-path-render-restoration: " + error.GetBaseException().Message);
        }
        Finish();
    }

    [HideFromIl2Cpp]
    private void ReadCounters()
    {
        bool capture = HostBool("Capturing", property: true);
        if (capture && !HostBool("_strictPriming"))
        {
            if (HostBool("_strictPreparing")) _sawAudioPass = true;
            else _sawVideoPass = true;
        }
        _captured = Math.Max(_captured, HostField<long>("_captured"));
        var session = HostField<ExportSession?>("_session");
        if (session != null)
        {
            _written = Math.Max(_written, session.FramesWritten);
            _accepted = Math.Max(_accepted, session.FramesAccepted);
            _audioSamples = Math.Max(_audioSamples, session.AudioSampleFrames);
        }
        var plan = HostField<object?>("_strictPlan");
        if (plan != null) _plannedFrames = (long)plan.GetType().GetProperty("FrameCount")!.GetValue(plan)!;
        if (!_developmentAudit && capture)
        {
            foreach (string field in new[] { "_staticInertScope", "_frameworkScope", "_recipeScope", "_strictCapabilitySnapshot", "_strictLedger",
                "_strictSandboxTrace", "_repeatBaseline", "_strictPreflightReceipt", "_strictPreflightFiles", "_voicePreparedReceipt", "_preparedBackgroundCommands" })
                if (HostField<object?>(field) != null) throw new InvalidOperationException("Production capture retained an audit-only object: " + field);
        }
        if (_singlePass && (capture || _submitted && HostBool("Busy", property: true)))
        {
            if (HostBool("_strictPreparing") || HostBool("_strictReplayPending"))
                throw new InvalidOperationException("Single-pass execution entered preparation or requested replay.");
            foreach (string field in new[] { "_preparedAudio", "_preparedFrameSchedule", "_strictLedger" })
                if (HostField<object?>(field) != null) throw new InvalidOperationException("Single-pass execution created prepared replay state: " + field);
        }
        string identity = _developmentAudit ? HostField<string>("_strictIdentity") : _identity;
        if (identity.Length != 0) _identity = identity;
    }

    [HideFromIl2Cpp]
    private void VerifyCompletedExport()
    {
        Phase("independent-media-inspection");
        if (_wholeExportBenchmark) { VerifyCompletedBenchmark(); return; }
        if (_verification == null || !_verification.IsCompleted) return;
        var evidence = _verification.GetAwaiter().GetResult();
        _verification = null;
        ValidateNativeSource();
        if (_identity.Length == 0) throw new InvalidOperationException("No strict export identity was observed.");
        if (_firstEvidence != null && (_identity != _firstIdentity
            || evidence.Frames != _firstEvidence.Frames || evidence.AudioSampleFrames != _firstEvidence.AudioSampleFrames
            || evidence.VideoDuration != _firstEvidence.VideoDuration || evidence.AudioDuration != _firstEvidence.AudioDuration
            || evidence.AudioChannels != _firstEvidence.AudioChannels || evidence.RecipePolicySha256 != _firstEvidence.RecipePolicySha256))
            throw new InvalidOperationException("Repeated export changed the strict identity, frame count, sample count, or exact duration.");
        if (_firstEvidence != null)
        {
            if (!NativePreEncodeObservation.FontAtlasEquivalenceEnabled)
                DecodedMediaFingerprint.RequireEquivalent(_firstEvidence.Decoded, evidence.Decoded);
            else
            {
                DecodedMediaFingerprint.RequireSameTimingAndAudio(_firstEvidence.Decoded, evidence.Decoded);
                bool decodedPixelsExact = _firstEvidence.Decoded.VideoSha256 == evidence.Decoded.VideoSha256;
                var glyph = NativePreEncodeObservation.CompareCompletedAttempts(_firstCompletedAttempt, _attempt);
                DecodedMediaFingerprint.RequireOriginalPixelsToDecodedConsistency(glyph.RawExact, decodedPixelsExact);
                var comparison = new {
                    Contract = "authored-render equivalence with font-atlas exception", BaselineAttempt = _firstCompletedAttempt,
                    CurrentAttempt = _attempt, ExactDecodedPixels = decodedPixelsExact, ExactDecodedAudio = true,
                    ExactFrameCountsAndPts = true, glyph.RawExact, glyph.Frames, glyph.ChangedPixels, glyph.ChangedChannels, glyph.MaxChannelDifference,
                    RawComparisonContract = glyph.Contract,
                    Scope = "Explicit test-only native glyph projection/atlas rule. Raw pixels outside eligible changed-UV glyph triangles remain exact; same text/font/mesh/colors/transforms and integral atlas moves are required. This is not a byte-identical movie claim."
                };
                string path = Path.Combine(Plugin.DiagnosticsRoot, $"story-authored-render-equivalence-{_attempt:00}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true }));
                Plugin.Report("PATH-CHECK STORY AUTHORED-RENDER " + JsonSerializer.Serialize(new {
                    comparison.Contract, comparison.RawExact, comparison.ExactDecodedPixels, comparison.ChangedPixels,
                    comparison.ChangedChannels, comparison.MaxChannelDifference, Evidence = path
                }));
            }
        }
        _firstEvidence ??= evidence;
        if (_firstCompletedAttempt == 0) _firstCompletedAttempt = _attempt;
        if (_firstIdentity.Length == 0) _firstIdentity = _identity;
        _completedExports++;
        _passed++;
        Plugin.Report("PATH-CHECK STORY MEDIA " + JsonSerializer.Serialize(new
        {
            attempt = _attempt, completed_export = _completedExports, output = _result!.OutputPath, identity = _identity,
            source_sha256_after = HashFile(_story), observed_capture_frames = _captured,
            observed_encoded_frames = _written, observed_capture_audio_samples = _audioSamples,
            strict_planned_frames = _plannedFrames, single_live_pass = _singlePass, observed_pass_begins = _observedPassBegins, media = evidence
        }));
        Plugin.Report("PATH-CHECK PASS real-" + (_singlePass ? "single-pass" : "strict") + "-story-export-" + _attempt);
        if (_completedExports < _repeat) { _settleFrames = 0; _stage = 5; Phase("settling-before-repeat"); }
        else
        {
            if (_repeat == 2) { _passed++; Plugin.Report(NativePreEncodeObservation.FontAtlasEquivalenceEnabled
                ? "PATH-CHECK PASS repeated-exact-audio-events-pts-and-authored-render-equivalence-with-font-atlas-exception"
                : "PATH-CHECK PASS repeated-strict-identity-frame-sample-duration-decoded-content-match"); }
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private void VerifyCompletedBenchmark()
    {
        if (_benchmarkVerification == null || !_benchmarkVerification.IsCompleted) return;
        var media = _benchmarkVerification.GetAwaiter().GetResult(); _benchmarkVerification = null;
        ValidateNativeSource();
        NativeAuditOutputContract.VerifyRecipeOutput(_developmentAudit, _result!);
        if (_effectivePerformanceOptions == null || _attemptElapsed.IsRunning || _attemptElapsed.Elapsed <= TimeSpan.Zero)
            throw new InvalidOperationException("Whole-export benchmark lacks effective options or a completed Prepare-to-ReportCompleted timer.");
        _completedExports++; _passed++;
        var summary = new {
            Schema = "aave.native-whole-export-benchmark.v1", Scope = "Actual export total from Prepare invocation through ReportCompleted, including load, render, encode, mux and production completion checks. Independent inspection follows outside that timer; no lossy-content equivalence claim.",
            SinglePass = _singlePass, ObservedPassBegins = _observedPassBegins, ElapsedSeconds = _attemptElapsed.Elapsed.TotalSeconds,
            Frames = _result!.Frames, ExportFramesPerSecond = _result.Frames / _attemptElapsed.Elapsed.TotalSeconds,
            CapturedFrames = _captured, AcceptedFrames = _accepted, EncodedFrames = _written, NativeAudioSampleFrames = _audioSamples,
            RequestedOptions = _options, EffectiveOptions = _effectivePerformanceOptions, EffectiveOptionsSha256 = _effectivePerformanceOptionsSha256,
            Encoder = _resolvedEncoder, Source = _story, SourceSha256 = HashFile(_story), OutputPath = _result.OutputPath,
            OutputBytes = new FileInfo(_result.OutputPath).Length, OutputSha256 = HashFile(_result.OutputPath), Media = media,
            ActualAzuriteRestored = true, HarnessSha256 = HashFile(typeof(StoryExportChecks).Assembly.Location), ProviderSha256 = HashFile(typeof(ExportHost).Assembly.Location)
        };
        string path = Path.Combine(Plugin.DiagnosticsRoot, "story-whole-export-benchmark.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report("PATH-CHECK BENCHMARK TOTAL " + JsonSerializer.Serialize(new { summary.ElapsedSeconds, summary.Frames,
            summary.ExportFramesPerSecond, summary.Encoder, summary.SinglePass, summary.ObservedPassBegins, evidence = path }));
        Plugin.Report("PATH-CHECK PASS whole-export-benchmark-format-count-pts-and-restoration");
        Finish();
    }

    [HideFromIl2Cpp]
    private static async Task<MediaEvidence> VerifyMedia(string ffmpeg, string ffprobe, ExportResult result, string evidencePath, NativePcmContent? originalPcm)
    {
        string json = await ProbeMovie(ffprobe, result.OutputPath, evidencePath).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => Text(stream, "codec_type") == "video");
        var audio = streams.Single(stream => Text(stream, "codec_type") == "audio");
        if (Text(video, "codec_name") != "h264" || Number(video, "width") != 640 || Number(video, "height") != 360
            || Text(audio, "codec_name") != "pcm_s16le" || Number(audio, "sample_rate") != 48000)
            throw new InvalidOperationException("Completed media does not match requested H.264 640x360 / 48 kHz PCM16 format.");
        var videoTime = Rational(Text(video, "time_base"));
        var audioTime = Rational(Text(audio, "time_base"));
        long frames = 0, samples = 0;
        foreach (var frame in document.RootElement.GetProperty("frames").EnumerateArray())
        {
            if (Text(frame, "media_type") == "video")
            {
                if (checked(Number(frame, "pts") * videoTime.Numerator * 30) != checked(frames * videoTime.Denominator))
                    throw new InvalidOperationException("Video presentation timestamps do not form an exact 30 fps sequence starting at zero.");
                frames++;
            }
            else if (Text(frame, "media_type") == "audio")
            {
                if (checked(Number(frame, "pts") * audioTime.Numerator * 48000) != checked(samples * audioTime.Denominator))
                    throw new InvalidOperationException("PCM presentation timestamps contain a gap, overlap, or nonzero start.");
                samples = checked(samples + Number(frame, "nb_samples"));
            }
        }
        if (frames != result.Frames || samples != checked(frames * 1600))
            throw new InvalidOperationException("Decoded video frame count or PCM sample count differs from the exact 30 fps / 48 kHz timeline.");
        long videoTicks = Number(video, "duration_ts"), audioTicks = Number(audio, "duration_ts");
        if (Number(video, "start_pts") != 0 || Number(audio, "start_pts") != 0
            || checked(videoTicks * videoTime.Numerator * 30) != checked(frames * videoTime.Denominator)
            || checked(audioTicks * audioTime.Numerator * 48000) != checked(samples * audioTime.Denominator))
            throw new InvalidOperationException("Stream start/end timestamps do not match decoded frame/sample counts.");
        bool audit = NativeAuditOutputContract.IsDevelopmentAudit(typeof(ExportOptions).Assembly);
        var runtimeProof = NativeAuditOutputContract.VerifyRecipeOutput(audit, result);
        var decodedLayout = new DecodedMediaLayout(640, 360, 30, 48000, checked((int)Number(audio, "channels")), frames, samples, Text(video, "pix_fmt"));
        var decoded = await DecodedMediaFingerprint.CaptureAsync(ffmpeg, result.OutputPath, decodedLayout,
            evidencePath + ".decoded", TimeSpan.FromSeconds(180)).ConfigureAwait(false);
        if (originalPcm != null && (originalPcm.Observation.Frames != frames || originalPcm.Observation.Fps != 30
            || originalPcm.Observation.SampleRate != 48000 || originalPcm.Observation.SampleFrames != samples
            || originalPcm.Observation.Channels != decodedLayout.Channels))
            throw new InvalidDataException("Original native mixed PCM and independently decoded movie have different timeline/channel layouts.");
        var comparison = originalPcm == null ? null : NativePcmObservation.ComparePcm16(originalPcm, decoded.AudioPcm16Path, evidencePath + ".original-pcm-comparison.json");
        return new MediaEvidence(frames, samples, frames + "/30", samples + "/48000", Number(audio, "channels"),
            new FileInfo(result.OutputPath).Length, HashFile(result.OutputPath), evidencePath, decoded,
            result.RuntimeRecipeReceiptPath, runtimeProof?.PolicySha256, runtimeProof?.RecipesSha256) { NativePcmComparison = comparison == null ? null : new {
                comparison.Schema, comparison.Passed, comparison.Frames, comparison.SampleFrames, comparison.Channels, comparison.ToleranceLsb,
                comparison.MaximumErrorLsb, comparison.MismatchedSamples, comparison.FirstMismatchedSample,
                EvidencePath = evidencePath + ".original-pcm-comparison.json" } };
    }

    [HideFromIl2Cpp]
    private static async Task<string> ProbeMovie(string ffprobe, string movie, string evidencePath)
    {
        var start = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-show_streams", "-show_frames", "-show_entries",
            "stream=index,codec_type,codec_name,width,height,pix_fmt,time_base,start_pts,duration_ts,sample_rate,channels:frame=media_type,pts,nb_samples", "-of", "json", movie })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start ffprobe for independent media inspection.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180)).ConfigureAwait(false); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        string json = await stdout.ConfigureAwait(false), errors = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(errors))
            throw new InvalidOperationException("ffprobe could not decode the completed media: exit=" + process.ExitCode + "; " + errors);
        await File.WriteAllTextAsync(evidencePath, json).ConfigureAwait(false);
        return json;
    }

    [HideFromIl2Cpp]
    private static void ObserveFailure(ExportHost __instance, Exception error)
    {
        var runner = _active;
        if (runner == null || !runner._submitted || runner._host?.Pointer != __instance.Pointer
            || runner._failureCode.Length != 0) return;
        runner._failureCode = error is ExportException export ? export.Code : error.GetType().Name;
        runner._failureMessage = error.Message;
        runner._observedFailureAt = runner._wall.Elapsed.TotalSeconds;
        Plugin.Report("PATH-CHECK STORY REJECTION " + JsonSerializer.Serialize(new { code = runner._failureCode, message = error.Message }));
        if (runner._failureCode == "timeline_source_changed") StoryRejectionDiagnostics.WriteTimelineFailure(__instance);
        if (runner._failureCode.StartsWith("strict_", StringComparison.Ordinal))
        {
            StoryRejectionDiagnostics.Write(__instance);
            try
            {
                var bootstrap = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AAVideoExport.RuntimeBootstrap");
                var api = bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeV2", true)!;
                string snapshot = (string)api.GetMethod("GetSnapshotJson", StaticMembers)!.Invoke(null, null)!;
                File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "story-recipe-failure.json"), snapshot);
            }
            catch (Exception diagnostic) { Plugin.Report("PATH-CHECK STORY recipe-diagnostic-unavailable=" + diagnostic.GetBaseException().Message); }
        }
    }

    [HideFromIl2Cpp]
    private static void ObserveCompletion(ExportHost __instance, ExportResult result)
    {
        var runner = _active;
        if (runner == null || !runner._submitted || runner._host?.Pointer != __instance.Pointer) return;
        runner._attemptElapsed.Stop();
        runner.ReadCounters();
        runner._result = result;
    }

    [HideFromIl2Cpp]
    private static void ObservePassBegin(ExportHost __instance)
    {
        var runner = _active;
        if (runner == null || !runner._submitted || runner._host?.Pointer != __instance.Pointer) return;
        runner._observedPassBegins++;
    }

    [HideFromIl2Cpp]
    private void Phase(string phase)
    {
        if (_lastPhase == phase && _wall.Elapsed.TotalSeconds < _nextHeartbeat) return;
        _lastPhase = phase;
        _nextHeartbeat = _wall.Elapsed.TotalSeconds + 15;
        Plugin.Report("PATH-CHECK STORY PHASE " + JsonSerializer.Serialize(new
        {
            attempt = _attempt, phase, wall_seconds = Math.Round(_wall.Elapsed.TotalSeconds, 2),
            captured_frames = _captured, encoded_frames = _written, planned_frames = _plannedFrames,
            scene = SceneManager.GetActiveScene().name,
            status = _host == null ? "" : HostField<string>("_status")
        }));
    }

    [HideFromIl2Cpp]
    private void FailCheck(Exception error)
    {
        if (_stage is 90 or 99) return;
        while (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException;
        _failed++;
        _stage = 90;
        _cleanupDeadline = _wall.Elapsed.TotalSeconds + 10;
        Plugin.Report("PATH-CHECK FAIL native-story-export: " + error.GetType().Name + ": " + error.Message);
        if (_host != null && _submitted)
        {
            try { RequiredMethod("Cancel").Invoke(_host, null); }
            catch (Exception cleanup) { Plugin.Report("PATH-CHECK STORY cleanup-error=" + cleanup.GetType().Name); }
        }
        Phase("waiting-for-production-cleanup");
    }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (_stage == 99) return;
        _stage = 99;
        try
        {
            File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "story-panel-boundary-evidence.json"), JsonSerializer.Serialize(new
            {
                Scope = "Production Host work cleanup followed by normal NativeExportPanel.Visible=false modal close, then actual Azurite restoration. No private lease disposal or forced rendering settings.",
                Events = _panelBoundaryEvents, PanelVisible = _panel?.Visible ?? false,
                PanelOwnsRendering = PanelOwnsRendering(), RenderOwned = AAVideoExport.Integration.RenderControlV1.IsRenderingOwned
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL panel-boundary-evidence: " + error.GetBaseException().Message); }
        try
        {
            string after = _story.Length != 0 && File.Exists(_story) ? HashFile(_story) : "missing";
            Plugin.Report("PATH-CHECK STORY SOURCE " + JsonSerializer.Serialize(new { source = _story, sha256_before = _sourceHash, sha256_after = after }));
            if (_sourceHash.Length != 0 && after == _sourceHash) { _passed++; Plugin.Report("PATH-CHECK PASS story-source-unchanged"); }
            else { _failed++; Plugin.Report("PATH-CHECK FAIL story-source-not-verified-unchanged"); }
        }
        catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL source-recheck: " + error.GetType().Name); }
        if (!_developmentAudit && _auditTimelineBefore != null)
        {
            try { NativeAuditOutputContract.RequireAuditDirectoryUnchanged(_auditTimelineDirectory, _auditTimelineBefore); _passed++; Plugin.Report("PATH-CHECK PASS production-created-no-audit-timeline-or-baseline-files"); }
            catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL production-audit-output: " + error.Message); }
        }
        try
        {
            if (_panel != null && _originalOptions != null)
            {
                _panel.Options = _originalOptions;
                if (_mixedRecipe.Length != 0)
                    typeof(NativeExportPanel).GetProperty("ShowPlaybackButtons", InstanceMembers)!.SetValue(_panel, _originalShowPlaybackButtons);
                // Automatic shutdown disposes native widgets after this result.
                // Keep their stale displayed test path from being persisted.
                if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_QUIT") != "1")
                    _panel.OutputDirectoryCommitted = _originalDirectoryCommit;
            }
            _hooks?.UnpatchSelf();
            NativePreEncodeObservation.Shutdown();
            NativeBgmStartObservation.Shutdown();
        }
        catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL test-observer-cleanup: " + error.GetType().Name); }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BACKGROUND_STORY_ASSETS")))
        {
            try
            {
                bool backgroundPassed = BackgroundStoryEvidence.Finish();
                if (_backgroundEvidenceInstalled && backgroundPassed) _passed++;
                else { _failed++; Plugin.Report("PATH-CHECK FAIL background-story-evidence incomplete-or-failed"); }
            }
            catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL background-story-cleanup: " + error); }
        }
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_MULTILINGUAL") == "1")
        {
            try
            {
                bool multilingualPassed = MultilingualStoryEvidence.Finish();
                if (_multilingualEvidenceInstalled && multilingualPassed) _passed++;
                else { _failed++; Plugin.Report("PATH-CHECK FAIL multilingual-story-evidence incomplete-or-failed"); }
            }
            catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL multilingual-story-cleanup: " + error); }
        }
        if (_mixedRecipe.Length != 0)
        {
            try
            {
                if (_mixedEvidenceInstalled && ControlledStoryExportEvidence.Finish(_repeat, _cancelFirst ? 1 : 0)) _passed++;
                else { _failed++; Plugin.Report("PATH-CHECK FAIL authored-mixed-story-evidence incomplete-or-failed"); }
            }
            catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL authored-mixed-story-evidence: " + error); }
        }
        if (_renderControlEvidence != null)
        {
            try
            {
                if (_renderControlEvidence.Finish(_performanceWindow != null ? 1 : _repeat + (_cancelFirst ? 1 : 0)))
                { _passed++; Plugin.Report("PATH-CHECK PASS actual-azurite-render-control-restoration"); }
                else { _failed++; Plugin.Report("PATH-CHECK FAIL actual-azurite-render-control-restoration"); }
            }
            catch (Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL render-control-evidence: " + error); }
        }
        if (_expectedRejection.Length == 0 && (_completedExports != (_performanceWindow != null ? 0 : _repeat)
            || _cancelledAttempts != (_performanceWindow != null || _cancelFirst ? 1 : 0)))
        { _failed++; Plugin.Report("PATH-CHECK FAIL completed/cancelled attempt totals differ from the explicit request"); }
        Plugin.Report("PATH-CHECK STORY ATTEMPTS " + JsonSerializer.Serialize(new
        { total_attempts = _attempt, completed_exports = _completedExports, cancelled_attempts = _cancelledAttempts,
            requested_completed_exports = _performanceWindow != null ? 0 : _repeat,
            requested_cancelled_attempts = _performanceWindow != null || _cancelFirst ? 1 : 0,
            performance_only = _performanceWindow != null, single_live_pass = _singlePass, last_attempt_pass_begins = _observedPassBegins }));
        _active = null;
        Plugin.Report($"PATH-CHECK RESULT {_passed} passed; {_failed} failed");
    }

    [HideFromIl2Cpp]
    private T HostField<T>(string name) => (T)(typeof(ExportHost).GetField(name, InstanceMembers)
        ?? throw new MissingFieldException(typeof(ExportHost).FullName, name)).GetValue(_host)!;
    [HideFromIl2Cpp]
    private void SetHostField(string name, object value) => (typeof(ExportHost).GetField(name, InstanceMembers)
        ?? throw new MissingFieldException(typeof(ExportHost).FullName, name)).SetValue(_host, value);
    [HideFromIl2Cpp]
    private bool HostBool(string name, bool property = false) => property
        ? (bool)(typeof(ExportHost).GetProperty(name, InstanceMembers) ?? throw new MissingMemberException(name)).GetValue(_host)!
        : HostField<bool>(name);
    [HideFromIl2Cpp]
    private static MethodInfo RequiredMethod(string name) => typeof(ExportHost).GetMethod(name, InstanceMembers)
        ?? throw new MissingMethodException(typeof(ExportHost).FullName, name);
    [HideFromIl2Cpp]
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }
    [HideFromIl2Cpp]
    private static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
    [HideFromIl2Cpp]
    private static string Text(JsonElement element, string name) => element.GetProperty(name).ToString();
    [HideFromIl2Cpp]
    private static long Number(JsonElement element, string name) => long.Parse(Text(element, name), CultureInfo.InvariantCulture);
    [HideFromIl2Cpp]
    private static (long Numerator, long Denominator) Rational(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long numerator) || !long.TryParse(parts[1], out long denominator)
            || numerator <= 0 || denominator <= 0) throw new InvalidDataException("Invalid media time base: " + value);
        return (numerator, denominator);
    }

    // FileSha256 is provenance only. Repeat acceptance compares the decoded
    // frame/PCM fingerprints, independently validated PTS and exact counts.
    private sealed record MediaEvidence(long Frames, long AudioSampleFrames, string VideoDuration, string AudioDuration,
        long AudioChannels, long Bytes, string FileSha256, string ProbeEvidencePath, DecodedMediaFingerprintResult Decoded,
        string? RecipeEvidencePath, string? RecipePolicySha256, string? RecipesSha256)
    {
        public object? NativePcmComparison { get; init; }
    }
}
