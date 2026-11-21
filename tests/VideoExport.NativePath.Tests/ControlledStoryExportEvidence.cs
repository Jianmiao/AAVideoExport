using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Read-only acceptance observations for the independently authored mixed story.
// Install before Host preparation freezes its patch graph. No resolver, voice
// Gate/Runtime, ready bit, native advancement or effect execution is replaced.
internal static partial class ControlledStoryExportEvidence
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static ExportHost? host;
    private static Harmony? hooks;
    private static Type? backgroundAdapter;
    private static Type? voiceAdapter;
    private static string recipePath = "", storyPath = "", storyHash = "", expectedAuto = "自动";
    private static string expectedVoiceIdentifier = "", expectedVoiceSha256 = "";
    private static int expectedRows, expectedRequests;
    private static string[] expectedPresets = Array.Empty<string>();
    private static int[] expectedVoiceRows = Array.Empty<int>();
    private static readonly Dictionary<string, Oracle> Oracles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> PresetRows = new(StringComparer.Ordinal);
    private static readonly List<Pass> Passes = new();
    private static readonly Dictionary<object, int> Attempts = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, Pass> ByScope = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<int, Cancellation> Cancellations = new();
    private static readonly List<object> Errors = new();
    private static JsonElement? trajectoryComparison;
    private static bool installed, finished, singlePass;

    internal static void Install(ExportHost exportHost, string authoredRecipePath, string expectedAutoText = "自动")
    {
        if (installed) throw new InvalidOperationException("Mixed export evidence is already installed.");
        host = exportHost ?? throw new ArgumentNullException(nameof(exportHost));
        singlePass = NativeAuditOutputContract.IsSinglePass(typeof(AAVideoExport.Core.ExportOptions).Assembly);
        recipePath = Path.GetFullPath(authoredRecipePath); expectedAuto = expectedAutoText;
        using var recipe = JsonDocument.Parse(File.ReadAllText(recipePath));
        var root = recipe.RootElement;
        storyPath = root.GetProperty("Story").GetString()!;
        storyHash = root.GetProperty("StorySha256").GetString()!;
        Require(Hash(storyPath).Equals(storyHash, StringComparison.OrdinalIgnoreCase), "Authored story hash differs from the recipe.");
        var rows = root.GetProperty("CompiledRows").EnumerateArray().ToArray();
        expectedRows = rows.Length;
        expectedVoiceRows = rows.Where(row => row.GetProperty("VoiceJp").GetString()!.StartsWith("aavt/voice/", StringComparison.Ordinal))
            .Select(row => row.GetProperty("index").GetInt32()).ToArray();
        expectedRequests = root.GetProperty("OwnedVoiceOccurrences").GetInt32();
        expectedVoiceIdentifier = root.GetProperty("VoiceIdentifier").GetString()!;
        expectedVoiceSha256 = root.GetProperty("VoiceSha256").GetString()!;
        expectedPresets = root.GetProperty("ExpectedPresets").EnumerateArray().Select(value => value.GetString()!).ToArray();
        foreach (var authored in root.GetProperty("AuthoringRows").EnumerateArray())
        {
            string kind = authored.GetProperty("ExpectedPreset").GetString()!;
            if (kind.Length == 0) continue;
            string text = authored.GetProperty("Text").GetString()!;
            PresetRows.Add(kind, rows.Single(row => row.GetProperty("TextEn").GetString() == text).GetProperty("index").GetInt32());
        }
        Require(expectedRows == 8 && expectedRequests == 3 && expectedVoiceRows.Length == 3 && expectedPresets.Length == 4,
            "This observer requires the actual eight-row official mixed fixture, not the old injected fixture.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(recipePath)!, "aave-fixture-workspace.json")));
        Require(marker.RootElement.GetProperty("Owner").GetString() == "AAVE acceptance", "Independent fixture marker is missing.");
        foreach (var item in marker.RootElement.GetProperty("Videos").EnumerateArray())
        {
            var probe = item.GetProperty("Probe");
            string video = item.GetProperty("Video").GetString()!, hash = item.GetProperty("VideoSha256").GetString()!;
            string rgba = item.GetProperty("DecodedRgba").GetString()!;
            Require(Hash(video).Equals(hash, StringComparison.OrdinalIgnoreCase), "Video source hash differs: " + video);
            Require(Hash(rgba).Equals(item.GetProperty("DecodedRgbaSha256").GetString(), StringComparison.OrdinalIgnoreCase), "RGBA oracle hash differs: " + rgba);
            string[] rate = probe.GetProperty("r_frame_rate").GetString()!.Split('/');
            var oracle = new Oracle(item.GetProperty("Name").GetString()!, hash, probe.GetProperty("width").GetInt32(),
                probe.GetProperty("height").GetInt32(), int.Parse(probe.GetProperty("nb_read_frames").GetString()!),
                int.Parse(rate[0]), int.Parse(rate[1]), File.ReadAllBytes(rgba));
            Require(oracle.Pixels.Length == checked(oracle.Width * oracle.Height * 4 * oracle.Frames), "RGBA oracle length differs.");
            Oracles.Add(hash, oracle);
        }
        Require(Oracles.Count == 2, "The mixed fixture must contain both authored background sources.");
        backgroundAdapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.ControlledBackgroundVideoAdapter", true)!;
        voiceAdapter = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.CharacterVoiceSessionAdapter", true)!;
        var effects = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Rukari.MoreEffects")
            .GetType("AzureArchive.VideoTools.Runtime.CharacterPresetRuntime", true)!;
        hooks = new Harmony("halocue.aave.controlled-story.export-evidence");
        try
        {
        Patch(typeof(ExportHost), "BeginStrictPass", after: nameof(AfterBeginPass));
        Patch(typeof(ExportHost), "CaptureFrame", before: nameof(BeforeCaptureFrame), after: nameof(AfterCaptureFrame));
        Patch(typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!, "RenderFrame", after: nameof(AfterNativeManualRender));
        Patch(typeof(ExportHost), "FinishStrictPass", before: nameof(BeforeFinishPass), after: nameof(AfterFinishPass));
        if (NativeAuditOutputContract.IsDevelopmentAudit(typeof(AAVideoExport.Core.ExportOptions).Assembly))
            Patch(voiceAdapter, "SealPass", after: nameof(AfterVoiceSeal));
        else InstallProductionObservers();
        Patch(typeof(Test), nameof(Test.AdvanceScenario), after: nameof(AfterAdvance));
        Patch(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation._TitleTask_b__8_0), after: nameof(AfterTitleReadyPredicate));
        Patch(typeof(ScenarioAnimation.TitleAnimation), nameof(ScenarioAnimation.TitleAnimation.Complete), before: nameof(BeforeTitleComplete), after: nameof(AfterTitleComplete));
        Patch(typeof(BackgroundPresenter.Layer), nameof(BackgroundPresenter.Layer.OnImageRendered), after: nameof(AfterLayerRendered));
        Patch(effects, "Begin", after: nameof(AfterPreset));
        installed = true;
        }
        catch { try { RestoreProductionObservers(); } finally { hooks.UnpatchSelf(); } throw; }
        Plugin.Report("PATH-CHECK MIXED-EXPORT installed; recipe=" + recipePath + "; observe-only; production voice events are collected by test-only original-runtime observers");
    }

    private static void Patch(Type type, string method, string? before = null, string? after = null)
        => hooks!.Patch(type.GetMethod(method, Flags) ?? throw new MissingMethodException(type.FullName, method),
            prefix: before == null ? null : new HarmonyMethod(typeof(ControlledStoryExportEvidence), before),
            postfix: after == null ? null : new HarmonyMethod(typeof(ControlledStoryExportEvidence), after));

    private static void AfterBeginPass(ExportHost __instance) => Guard("begin-pass", () =>
    {
        if (!SameHost(__instance)) return;
        var scope = Field(host!, "_native") ?? throw new InvalidOperationException("Host strict pass has no native scope.");
        var controller = Field(host!, "_voiceSession") ?? throw new InvalidOperationException("Host mixed pass has no held voice controller.");
        if (!Attempts.TryGetValue(controller, out int attempt)) Attempts.Add(controller, attempt = Attempts.Count + 1);
        var pass = new Pass { Index = Passes.Count + 1, Attempt = attempt, Preparation = Value<bool>(host!, "_strictPreparing"),
            Scope = scope, VoiceController = controller, Fps = Value<int>(Field(host!, "_options")!, "Fps") };
        ByScope.Add(scope, pass); Passes.Add(pass);
        Event(pass, "pass-begin", new { pass.Attempt, pass.Preparation, pass.Fps });
    });

    private static void BeforeCaptureFrame(ExportHost __instance, bool __0, out FrameObservation? __state)
    {
        __state = null;
        try
        {
            if (!SameHost(__instance)) return;
            var pass = Current();
            if (pass == null || Value<bool>(host!, "_strictPriming") || Value<bool>(host!, "_strictAwaitingStart")
                || Value<bool>(host!, "_strictDraining") || Value<bool>(host!, "_strictFinishPending")) return;
            var framePipeline = Field(host!, "_frames");
            // FramePipeline.RenderTarget reserves/drains a GPU slot. Observe
            // its already selected field; never call that mutating getter.
            __state = new FrameObservation(pass, Value<long>(host!, "_captured"), Player()?.cur ?? -1,
                (RenderTexture?)Field(framePipeline!, "_renderTarget"), __0);
            pass.PendingCapture = __state;
        }
        catch (Exception error) { Error("capture-prefix", error); }
    }
    private static void AfterNativeManualRender(object __instance, RenderTexture __0) => Guard("manual-render-target", () =>
    {
        var pass = Current();
        if (pass?.PendingCapture == null || !ReferenceEquals(pass.Scope, __instance)) return;
        pass.PendingCapture.Target = __0;
    });
    private static void AfterCaptureFrame(ExportHost __instance, FrameObservation? __state) => Guard("capture-postfix", () =>
    {
        if (!SameHost(__instance) || __state == null || Value<long>(host!, "_captured") != __state.Frame + 1) return;
        var pass = __state.Pass;
        if (pass.LastObservedFrame == __state.Frame) return;
        pass.LastObservedFrame = __state.Frame; pass.CapturedFrames++;
        if (__state.AlreadyRendered) pass.AutomaticFrames++; else pass.ManualFrames++;
        var player = Player(); if (player == null) return;
        pass.Rows.Add(player.cur);
        if (player.cur < 0) return;
        Require(Value<bool>(backgroundAdapter!, "Active"), "Controlled background backend is inactive during an authored frame.");
        ValidateBackgroundFrames(pass, player, __state.Frame);
        ObserveAutoLabels(pass, player, __state.Frame);
        // Sparse points are actual native render-target data, not a guarantee
        // of final encoded movie pixels, occlusion correctness, or decoded PCM.
        if (player.cur > 0 && pass.TargetRows.Add(player.cur) && pass.TargetSamples.Count < 3)
            pass.TargetSamples.Add(ReadTargetPoints(__state.Target, __state.Frame, player.cur));
    });

    private static void BeforeFinishPass(ExportHost __instance, out Pass? __state)
    {
        __state = SameHost(__instance) ? Current() : null;
        if (__state == null) return;
        var pass = __state;
        Guard("finish-pass-prefix", () =>
        {
            pass.BackgroundCommands = ProductionObservation ? JsonValue(pass.ExternalBackgroundCommands) : JsonValue(Member(backgroundAdapter!, "CommandTrace")!);
            if (ProductionObservation)
            {
                var pcm = pass.ExternalPcm ?? throw new InvalidOperationException("Original native PCM blocks were not observed.");
                pass.PcmObservation = JsonValue(pcm.Complete(pass.CapturedFrames));
                pass.PcmContent = pcm.WriteContent(pass.CapturedFrames, Path.Combine(Plugin.DiagnosticsRoot, $"original-mixed-pcm-attempt-{pass.Attempt:00}-pass-{pass.Index:00}.f32le"));
            }
        });
    }
    private static void AfterFinishPass(ExportHost __instance, Pass? __state) => Guard("finish-pass-postfix", () =>
    {
        if (!SameHost(__instance) || __state == null) return;
        __state.Sealed = true;
        if (ProductionObservation) Event(__state, "host-production-pass-finished", new { __state.CapturedFrames, hasVoiceObservation = __state.VoiceObservation != null });
        else Event(__state, "host-pass-finished", new { __state.CapturedFrames, hasVoiceReceipt = __state.VoiceReceipt != null });
    });
    private static void AfterVoiceSeal(object __instance, object __result) => Guard("voice-receipt", () =>
    {
        var pass = Current();
        if (pass == null || !ReferenceEquals(pass.VoiceController, __instance)) return;
        Require(pass.VoiceReceipt == null, "The same Host pass sealed a second voice receipt.");
        pass.VoiceReceipt = JsonValue(__result);
        ValidateVoiceReceipt(pass, pass.VoiceReceipt.Value);
        Event(pass, "voice-receipt-sealed", new { trace = pass.VoiceReceipt.Value.GetProperty("TraceSha256").GetString() });
    });
    private static void AfterAdvance(Test __instance, bool __runOriginal) => Guard("native-row", () =>
    {
        var pass = Current(__instance); if (pass == null || !__runOriginal || __instance.cur < 0) return;
        pass.Rows.Add(__instance.cur);
        Event(pass, "native-row", new { row = __instance.cur, __instance.isBackgroundLatest });
    });
    private static void AfterTitleReadyPredicate(ScenarioAnimation.TitleAnimation __instance, bool __result, bool __runOriginal)
        => Guard("title-ready", () =>
        {
            var pass = Current(__instance.owner); if (pass == null || !__runOriginal || !__result) return;
            if (!pass.TitleReady)
            {
                Require(__instance.owner.isBackgroundLatest, "Original title predicate disagrees with native background-ready field.");
                pass.TitleReady = true; pass.TitleReadyFrame = Value<long>(host!, "_captured");
                Event(pass, "original-title-ready", new { __instance.owner.isBackgroundLatest });
            }
        });
    private static void BeforeTitleComplete(ScenarioAnimation.TitleAnimation __instance, out bool __state) => __state = __instance.hasCompleted;
    private static void AfterTitleComplete(ScenarioAnimation.TitleAnimation __instance, bool __state, bool __runOriginal) => Guard("title-complete", () =>
    {
        var pass = Current(__instance.owner); if (pass == null || !__runOriginal || __state) return;
        Require(__instance.hasCompleted, "Original title completion returned without completion.");
        pass.TitleCompleted++; pass.TitleCompleteFrame = Value<long>(host!, "_captured");
        Event(pass, "original-title-complete", new { row = __instance.owner.cur, __instance.hasCompleted });
    });
    private static void AfterLayerRendered(BackgroundPresenter.Layer __instance, Material __0, bool __runOriginal) => Guard("native-background-render", () =>
    {
        var pass = Current(); var player = Player();
        if (pass == null || player == null || player.cur < 0 || Value<bool>(host!, "_strictPriming")
            || !__runOriginal || !OwnedLayer(player, __instance)) return;
        pass.LayerRenderReceipts++;
        if (player.isBackgroundLatest)
        {
            pass.ReadyRenderReceipts++;
            if (pass.FirstReadyRenderFrame < 0) pass.FirstReadyRenderFrame = Value<long>(host!, "_captured");
        }
        bool visible = __instance.Image != null && __instance.Image.gameObject.activeInHierarchy && __instance.Image.enabled
            && __instance.Image.alpha > .001f && __instance.Image.drawCall?.mRenderer?.isVisible == true;
        if (visible)
        {
            pass.VisibleRenderReceipts++;
            var bindings = (IDictionary)Field(backgroundAdapter!, "Layers")!;
            foreach (DictionaryEntry entry in bindings)
            {
                object binding = entry.Value!;
                if (((BackgroundPresenter.Layer)Field(binding, "Layer")!).Pointer != __instance.Pointer) continue;
                object? playback = Field(binding, "Playback"); if (playback == null) continue;
                var texture = Field(playback, "Texture") as Texture2D;
                if (texture == null || __instance.Image?.mainTexture?.Pointer != texture.Pointer) continue;
                string source = Value<string>(Field(Field(playback, "Asset")!, "Store")!, "SourceSha256");
                pass.VisibleSourceReceipts[source] = pass.VisibleSourceReceipts.TryGetValue(source, out long count) ? count + 1 : 1;
            }
        }
        if (pass.LayerRenderReceipts <= 4)
            Event(pass, "original-layer-rendered", new { player.isBackgroundLatest, visible,
                layer = __instance.Pointer.ToString("X"), image = __instance.Image?.Pointer.ToString("X"),
                texture = __instance.Image?.mainTexture?.name, material = __0?.name });
    });
    private static void AfterPreset(string __1, object __result) => Guard("original-preset", () =>
    {
        var pass = Current(); if (pass == null) return;
        Require(Value<bool>(__result, "Success") && Value<bool>(__result, "Value"), "Original MoreEffects.Begin rejected " + __1);
        string[] fields = __1.Split(';'); Require(fields.Length >= 3, "Preset directive has no kind.");
        string kind = fields[2].ToLowerInvariant();
        Require(expectedPresets.Contains(kind, StringComparer.Ordinal), "Unexpected preset kind: " + kind);
        Require(Player()?.cur == PresetRows[kind], "Original preset executed on a row different from the authoring recipe: " + kind);
        pass.Presets[kind] = pass.Presets.TryGetValue(kind, out int count) ? count + 1 : 1;
        Event(pass, "original-preset-success", new { kind, directive = __1, row = Player()?.cur });
    });

    private static void ValidateBackgroundFrames(Pass pass, Test player, long frame)
    {
        var snapshot = Field(backgroundAdapter!, "snapshot");
        var nativeVideo = snapshot == null ? null : Member(snapshot, "Player") as UnityEngine.Video.VideoPlayer;
        Require(nativeVideo == null || !nativeVideo.isPlaying, "Autonomous background VideoPlayer played during controlled capture.");
        pass.NativeVideoStoppedFrames++;
        var bindings = (IDictionary)Field(backgroundAdapter!, "Layers")!;
        foreach (DictionaryEntry entry in bindings)
        {
            object binding = entry.Value!;
            var layer = (BackgroundPresenter.Layer)Field(binding, "Layer")!;
            if (!OwnedLayer(player, layer)) continue;
            object? playback = Field(binding, "Playback"); if (playback == null) continue;
            var image = (UITexture?)Field(binding, "Image");
            var texture = (Texture2D?)Field(playback, "Texture");
            Require(image != null && texture != null && image.Pointer == layer.Image.Pointer
                && image.mainTexture?.Pointer == texture.Pointer, "Original layer image is not bound to its actual controlled Texture2D.");
            object store = Field(Field(playback, "Asset")!, "Store")!;
            string hash = Value<string>(store, "SourceSha256");
            Require(Oracles.TryGetValue(hash, out var oracle), "Controlled backend decoded an unexpected video source hash.");
            long startFrame = Value<long>(playback, "StartFrame"), sourceFrame = Value<long>(playback, "SourceFrame");
            long expected = checked((frame - startFrame) * oracle!.RateNumerator / (pass.Fps * (long)oracle.RateDenominator)) % oracle.Frames;
            Require(sourceFrame == expected, $"Source PTS selection differs at output frame {frame}: actual={sourceFrame}, expected={expected}.");
            Require(texture!.width == oracle.Width && texture.height == oracle.Height, "Controlled video dimensions differ from the independent oracle.");
            string key = hash + "/" + startFrame + "/" + sourceFrame;
            if (!pass.CheckedSourceFrames.Add(key)) continue;
            var pixels = texture.GetPixels32();
            int frameBase = checked((int)sourceFrame * oracle.Width * oracle.Height * 4);
            for (int y = 0; y < oracle.Height; y++)
            for (int x = 0; x < oracle.Width; x++)
            {
                var actual = pixels[y * oracle.Width + x];
                int expectedAt = frameBase + ((oracle.Height - 1 - y) * oracle.Width + x) * 4;
                Require(actual.r == oracle.Pixels[expectedAt] && actual.g == oracle.Pixels[expectedAt + 1]
                    && actual.b == oracle.Pixels[expectedAt + 2] && actual.a == oracle.Pixels[expectedAt + 3],
                    $"Controlled source texture differs from decoded oracle: {oracle.Name}, output={frame}, source={sourceFrame}, ({x},{y}).");
            }
            if (!pass.SourceFrames.TryGetValue(hash, out var frames)) pass.SourceFrames.Add(hash, frames = new HashSet<long>());
            frames.Add(sourceFrame);
            if (pass.BackgroundSamples.Count < 64) pass.BackgroundSamples.Add(new { frame, row = player.cur, source = oracle.Name,
                sourceSha256 = hash, sourceFrame, startFrame, width = texture.width, height = texture.height,
                image = image!.Pointer.ToString("X"), alpha = image.alpha, active = image.gameObject.activeInHierarchy,
                rendererVisible = image.drawCall?.mRenderer?.isVisible, originalRenderedDelegatePresent = layer.Rendered != null });
        }
    }

    private static void ObserveAutoLabels(Pass pass, Test player, long frame)
    {
        pass.AutoLabels ??= player.autoToggle?.GetComponentsInChildren<UILabel>(true).ToArray() ?? Array.Empty<UILabel>();
        foreach (var label in pass.AutoLabels)
        {
            if (label == null || !label.gameObject.activeInHierarchy) continue;
            Require(label.text == expectedAuto, "Original visible AUTO translation changed: " + label.text);
            Require(label.maxLineCount == 1 && label.overflowMethod == UILabel.Overflow.ShrinkContent, "Original AUTO label layout changed.");
            pass.AutoVisibleObservations++;
            if (pass.AutoSamples.Count < 4) pass.AutoSamples.Add(new { frame, text = label.text, label.maxLineCount,
                overflow = label.overflowMethod.ToString(), label.width, label.height, font = label.trueTypeFont?.name });
        }
    }
    private static object ReadTargetPoints(RenderTexture? target, long frame, int row)
    {
        Require(target != null && target.IsCreated(), "Captured frame has no actual native render target.");
        RenderTexture previous = RenderTexture.active;
        Texture2D? probe = null;
        var samples = new List<object>(); var bytes = new List<byte>();
        try
        {
            probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            RenderTexture.active = target;
            foreach (double y in new[] { .1, .5, .9 })
            foreach (double x in new[] { .1, .5, .9 })
            {
                int px = Math.Min(target!.width - 1, (int)(target.width * x)), py = Math.Min(target.height - 1, (int)(target.height * y));
                probe.ReadPixels(new Rect(px, py, 1, 1), 0, 0, false);
                var color = probe.GetPixels32()[0];
                bytes.AddRange(new[] { color.r, color.g, color.b, color.a });
                samples.Add(new { x = px, y = py, color.r, color.g, color.b, color.a });
            }
            return new { frame, row, target = target!.name, target.width, target.height, points = samples,
                sampleSha256 = Convert.ToHexString(SHA256.HashData(bytes.ToArray())),
                Scope = "Nine actual native render-target pixels only; final encoded pixel and PCM checks remain separate." };
        }
        finally { RenderTexture.active = previous; if (probe != null) Object.Destroy(probe); }
    }

    private static void ValidateVoiceReceipt(Pass pass, JsonElement receipt)
    {
        Require(receipt.GetProperty("StorySha256").GetString()!.Equals(storyHash, StringComparison.OrdinalIgnoreCase), "Host voice receipt belongs to another story.");
        Require(!receipt.GetProperty("NoOwnedVoiceExpected").GetBoolean(), "Mixed fixture was sealed as no-owned-voice.");
        var events = receipt.GetProperty("Events").EnumerateArray().ToArray();
        var requests = events.Where(item => item.GetProperty("Operation").GetString() == "Request").ToArray();
        Require(requests.Length == expectedRequests, "Host voice receipt must contain exactly three requests.");
        Require(requests.All(item => item.GetProperty("VoiceIdentifier").GetString() == expectedVoiceIdentifier), "Voice receipt identifiers differ from the authored fixture.");
        var assets = receipt.GetProperty("Assets").EnumerateArray().ToArray();
        Require(assets.Length == 1 && assets[0].GetProperty("Identifier").GetString() == expectedVoiceIdentifier
            && assets[0].GetProperty("FileSha256").GetString()!.Equals(expectedVoiceSha256, StringComparison.OrdinalIgnoreCase), "Actual voice receipt asset hash differs from the independent fixture.");
        Require(requests.Select(item => item.GetProperty("RequestOrdinal").GetInt32()).SequenceEqual(Enumerable.Range(0, expectedRequests)), "Voice request ordinals are not exactly once and sequential.");
        Require(requests.Select(item => item.GetProperty("Position").GetProperty("Row").GetInt32()).SequenceEqual(expectedVoiceRows), "Voice request rows differ from authored concrete VoiceJp rows.");
        foreach (var request in requests)
        {
            int ordinal = request.GetProperty("RequestOrdinal").GetInt32();
            var operations = events.Where(item => item.GetProperty("RequestOrdinal").GetInt32() == ordinal)
                .Select(item => item.GetProperty("Operation").GetString()!).ToArray();
            Require(operations.SequenceEqual(new[] { "Request", "NativeStart", "NativeEnd", "DelayComplete" }, StringComparer.Ordinal), "Voice request does not contain exactly-once native lifecycle events.");
        }
        pass.VoiceRequests = requests.Length;
    }

    internal static void DeclareCancellation(int attempt, long hostCapturedFrames)
    {
        Require(installed && !finished, "Cancellation needs an installed live mixed-story observer.");
        var pass = Current() ?? throw new InvalidOperationException("Declared cancellation has no current production Host pass.");
        Require(pass.Attempt == attempt && pass.Preparation != singlePass && !pass.Sealed && pass.VoiceReceipt == null
            && pass.CapturedFrames >= 12 && hostCapturedFrames >= 12
            && Passes.Count(item => item.Attempt == attempt) == 1,
            "Deliberate cancellation must interrupt meaningful unsealed original capture before any second playback.");
        Require(!Cancellations.ContainsKey(attempt), "The attempt already has a declared cancellation.");
        pass.BackgroundCommands = ProductionObservation ? JsonValue(pass.ExternalBackgroundCommands) : JsonValue(Member(backgroundAdapter!, "CommandTrace")!);
        pass.Cancelled = true;
        Cancellations.Add(attempt, new Cancellation(attempt, pass.Index, hostCapturedFrames, pass.CapturedFrames));
        Event(pass, "declared-cancellation", new { reason = "AA_VIDEOEXPORT_TEST_CANCEL_FIRST", hostCapturedFrames });
        WriteCancellationArchive(Cancellations[attempt], pass);
    }

    internal static void ConfirmCancellationCleanup(int attempt)
    {
        if (!Cancellations.TryGetValue(attempt, out var cancellation))
            throw new InvalidOperationException("Cleanup has no declared cancellation.");
        Require(Current() == null, "Cancelled native capture scope is still active.");
        var pass = Passes.Single(item => item.Index == cancellation.PassIndex);
        Require(pass.Cancelled && !pass.Sealed && pass.VoiceReceipt == null,
            "Cancelled preparation unexpectedly became a completed or sealed pass.");
        cancellation.CleanupVerified = true;
        WriteCancellationArchive(cancellation, pass);
    }

    private static void WriteCancellationArchive(Cancellation cancellation, Pass pass) => File.WriteAllText(
        Path.Combine(Plugin.DiagnosticsRoot, $"controlled-story-cancelled-attempt-{cancellation.Attempt:00}.json"),
        JsonSerializer.Serialize(new { DeclaredCancellation = cancellation.Summary(), Pass = pass.Summary(),
            SinglePass = singlePass, ComparisonPolicy = "This explicitly declared partial original capture pass is archived and excluded from completed-output comparison. Observer errors remain failures." },
            new JsonSerializerOptions { WriteIndented = true }));

    internal static NativePcmContent MoviePcm(int attempt)
    {
        var pass = Passes.Single(item => item.Attempt == attempt && !item.Cancelled && item.Sealed && item.Preparation != singlePass);
        return pass.PcmContent ?? throw new InvalidOperationException("Completed movie has no independently retained original mixed PCM.");
    }

    internal static bool Finish(int expectedCompletedAttempts = 1, int expectedCancelledAttempts = 0)
    {
        if (!installed) return true;
        if (finished) throw new InvalidOperationException("Mixed export evidence already finished.");
        finished = true;
        Guard("unpatch", () => { try { RestoreProductionObservers(); } finally { hooks?.UnpatchSelf(); } });
        Guard("final-checks", () =>
        {
            Require(Hash(storyPath).Equals(storyHash, StringComparison.OrdinalIgnoreCase), "Authored input changed during export.");
            var completedPasses = Passes.Where(pass => !pass.Cancelled).ToArray();
            Require(expectedCompletedAttempts > 0 && expectedCancelledAttempts is >= 0 and <= 1,
                "Expected completed and cancelled attempt counts are invalid.");
            Require(Cancellations.Count == expectedCancelledAttempts && Cancellations.Values.All(item => item.CleanupVerified),
                "A requested deliberate cancellation is missing or lacks verified cleanup.");
            Require(Attempts.Count == expectedCompletedAttempts + expectedCancelledAttempts
                && completedPasses.Length == expectedCompletedAttempts * (singlePass ? 1 : 2)
                && completedPasses.Select(pass => pass.Attempt).Distinct().Count() == expectedCompletedAttempts,
                "Observed completed Host attempts/preparation/replay count differs from the request.");
            Require(Passes.Where(pass => pass.Cancelled).All(pass => Cancellations.TryGetValue(pass.Attempt, out var declared)
                && declared.PassIndex == pass.Index && pass.Preparation != singlePass && !pass.Sealed && pass.CapturedFrames >= 12)
                && Passes.Count(pass => pass.Cancelled) == expectedCancelledAttempts,
                "Only explicitly declared partial audio-preparation passes may be excluded.");
            foreach (var pass in completedPasses)
            {
                Require(pass.Sealed && (ProductionObservation ? pass.VoiceObservation != null : pass.VoiceReceipt != null)
                    && pass.VoiceRequests == expectedRequests, "An actual Host pass lacks complete observed native voice lifecycle evidence.");
                Require(Enumerable.Range(0, expectedRows).All(pass.Rows.Contains), "An actual Host pass omitted an authored row.");
                Require(pass.TitleReady && pass.TitleCompleted == 1 && pass.ReadyRenderReceipts > 0 && pass.VisibleRenderReceipts > 0, "Original background render-ready/title progression lacks native visible receipts.");
                Require(pass.FirstReadyRenderFrame <= pass.TitleReadyFrame && pass.TitleReadyFrame <= pass.TitleCompleteFrame, "Original background-ready, predicate and title-completion frames are not ordered.");
                Require(expectedPresets.All(kind => pass.Presets.TryGetValue(kind, out int count) && count == 1), "An actual Host pass lacks exactly one successful original Begin for each authored preset.");
                Require(Oracles.All(pair => pass.SourceFrames.TryGetValue(pair.Key, out var frames) && frames.Count == pair.Value.Frames), "Both independent video oracles must cover every source frame in each pass.");
                Require(Oracles.All(pair => pass.VisibleSourceReceipts.TryGetValue(pair.Key, out long count) && count > 0), "Both controlled background sources need an actual visible native layer-render receipt.");
                Require(pass.TargetSamples.Count >= 2 && pass.AutoVisibleObservations > 0, "Actual render-target points or original visible AUTO translation evidence is missing.");
                Require(pass.BackgroundCommands != null, "Actual controlled background command trace was not retained at pass seal.");
            }
            var traces = completedPasses.Select(pass => new ControlledStoryPassTrace(
                pass.Attempt, pass.Preparation, pass.Fps, pass.CapturedFrames, JsonValue(pass.Events),
                pass.VoiceReceipt ?? default, pass.BackgroundCommands!.Value) { VoiceObservation = pass.VoiceObservation, PcmObservation = pass.PcmObservation }).ToArray();
            trajectoryComparison = singlePass ? ControlledStoryTrajectory.ValidateSinglePass(traces, expectedCompletedAttempts)
                : ControlledStoryTrajectory.Validate(traces, expectedCompletedAttempts);
        });
        bool passed = Errors.Count == 0;
        File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot, "controlled-story-export-evidence.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, SinglePass = singlePass, Recipe = recipePath, Story = storyPath, StorySha256 = storyHash, ExpectedCompletedAttempts = expectedCompletedAttempts,
            Attempts = Attempts.Count, ExpectedCancelledAttempts = expectedCancelledAttempts,
            CancelledAttempts = Cancellations.Values.Select(item => item.Summary()).ToArray(),
            CompletedAttempts = Passes.Where(pass => !pass.Cancelled).Select(pass => pass.Attempt).Distinct().ToArray(),
            Passes = Passes.Select(pass => pass.Summary()).ToArray(), TrajectoryComparison = trajectoryComparison, Errors,
            Scope = ProductionObservation
                ? "Independent native test observers collect original voice lifecycle, actual clip samples, background commands and complete authored PCM; SinglePass explicitly identifies one original playback per movie. No product audit receipt is produced or consumed. Final movie pixels/PCM also undergo independent decode comparison."
                : "Real DevelopmentAudit Host preparation/replay observations. Source-texture oracle equality and sparse native target pixels are not final encoded movie/PCM guarantees; use the independent decoded-output checks."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report($"PATH-CHECK {(passed ? "PASS" : "FAIL")} controlled-story-export-evidence attempts={Attempts.Count}; passes={Passes.Count}; errors={Errors.Count}");
        return passed;
    }

    private static Pass? Current(Test? player = null)
    {
        if (host == null) return null;
        var scope = Field(host, "_native");
        if (scope == null || !ByScope.TryGetValue(scope, out var pass)) return null;
        if (player != null && Player()?.Pointer != player.Pointer) return null;
        return pass;
    }
    private static Test? Player() => host == null ? null : Field(host, "_player") as Test;
    private static bool SameHost(ExportHost value) => host != null && value.Pointer == host.Pointer;
    private static bool OwnedLayer(Test player, BackgroundPresenter.Layer layer)
        => player.backgroundPresenter != null && (player.backgroundPresenter.main?.Pointer == layer.Pointer || player.backgroundPresenter.overlap?.Pointer == layer.Pointer);
    private static object? Field(object target, string name) => target is Type type
        ? (type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name)).GetValue(null)
        : (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);
    private static object? Member(object target, string name)
    {
        Type type = target as Type ?? target.GetType(); object? instance = target is Type ? null : target;
        return type.GetProperty(name, Flags)?.GetValue(instance) ?? type.GetField(name, Flags)?.GetValue(instance);
    }
    private static T Value<T>(object target, string name) => (T)(Member(target, name) ?? throw new MissingMemberException(target.ToString(), name));
    private static JsonElement JsonValue(object value) => JsonSerializer.SerializeToElement(value, value.GetType());
    private static string Hash(string path) { using var file = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(file)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Guard(string point, Action action) { try { action(); } catch (Exception error) { Error(point, error); } }
    private static void Error(string point, Exception error)
    {
        if (Errors.Count >= 32) return;
        // A failed observer must never throw through a native game callback.
        try
        {
            Errors.Add(new { point, error = error.ToString() });
            Plugin.Report("PATH-CHECK MIXED-EXPORT diagnostic-failed=" + point + "; " + error.Message);
        }
        catch { }
    }
    private static void Event(Pass pass, string kind, object detail)
    {
        var entry = new { kind, frame = host == null ? -1 : Value<long>(host, "_captured"), detail };
        pass.Events.Add(entry); Plugin.Report("PATH-CHECK MIXED-EXPORT " + JsonSerializer.Serialize(new { pass = pass.Index, pass.Attempt, pass.Preparation, entry }));
    }
    private sealed record Oracle(string Name, string Hash, int Width, int Height, int Frames, int RateNumerator, int RateDenominator, byte[] Pixels);
    private sealed class Cancellation(int attempt, int passIndex, long hostFrames, long observedFrames)
    {
        internal int Attempt { get; } = attempt;
        internal int PassIndex { get; } = passIndex;
        internal bool CleanupVerified;
        internal object Summary() => new { Attempt, PassIndex, HostCapturedFrames = hostFrames,
            ObservedCapturedFrames = observedFrames, CleanupVerified, Reason = "AA_VIDEOEXPORT_TEST_CANCEL_FIRST" };
    }
    private sealed class FrameObservation
    {
        internal readonly Pass Pass;
        internal readonly long Frame;
        internal readonly int Row;
        internal readonly bool AlreadyRendered;
        internal RenderTexture? Target;
        internal FrameObservation(Pass pass, long frame, int row, RenderTexture? target, bool alreadyRendered)
        { Pass = pass; Frame = frame; Row = row; Target = target; AlreadyRendered = alreadyRendered; }
    }
    private sealed class Pass
    {
        internal int Index, Attempt, Fps, TitleCompleted, VoiceRequests, AutoVisibleObservations;
        internal bool Preparation, Sealed, TitleReady, Cancelled;
        internal object Scope = null!, VoiceController = null!;
        internal long LastObservedFrame = -1, CapturedFrames, ManualFrames, AutomaticFrames, LayerRenderReceipts, ReadyRenderReceipts, VisibleRenderReceipts, NativeVideoStoppedFrames;
        internal long FirstReadyRenderFrame = -1, TitleReadyFrame = -1, TitleCompleteFrame = -1;
        internal readonly HashSet<int> Rows = new(), TargetRows = new();
        internal readonly HashSet<string> CheckedSourceFrames = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, HashSet<long>> SourceFrames = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, long> VisibleSourceReceipts = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int> Presets = new(StringComparer.Ordinal);
        internal readonly List<object> Events = new(), BackgroundSamples = new(), TargetSamples = new(), AutoSamples = new();
        internal JsonElement? VoiceReceipt, VoiceObservation, PcmObservation, BackgroundCommands;
        internal NativeVoiceObservation? ExternalVoice;
        internal NativePcmObservation? ExternalPcm;
        internal NativePcmContent? PcmContent;
        internal readonly Dictionary<string, AudioClip> ExternalVoiceClips = new(StringComparer.Ordinal);
        internal readonly List<JsonElement> ExternalBackgroundCommands = new();
        internal UILabel[]? AutoLabels;
        internal FrameObservation? PendingCapture;
        internal object Summary() => new { Index, Attempt, Preparation, Fps, Sealed, Cancelled, CapturedFrames, ManualFrames, AutomaticFrames,
            Rows = Rows.OrderBy(row => row).ToArray(), TitleReady, TitleCompleted, LayerRenderReceipts, ReadyRenderReceipts, VisibleRenderReceipts,
            FirstReadyRenderFrame, TitleReadyFrame, TitleCompleteFrame, VisibleSourceReceipts,
            Presets, VoiceRequests, VoiceReceipt, VoiceObservation, PcmObservation, PcmContent, BackgroundCommands, SourceFrames = SourceFrames.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(frame => frame).ToArray()),
            NativeVideoStoppedFrames, BackgroundSamples, TargetSamples, AutoVisibleObservations, AutoSamples, Events };
    }
}
