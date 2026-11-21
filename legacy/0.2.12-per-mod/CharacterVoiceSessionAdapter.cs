using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using FlatData;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AAVideoExport.Plugin;

// Main-thread ownership and preload for native CharacterVoice playback.
// Production retains ready clips and the actual player without audit hooks or
// receipts. DevelopmentAudit adds event/provenance observation separately.
// Neither path substitutes nativePlaying or starts a replacement AudioSource.
internal sealed class CharacterVoiceSessionAdapter : IDisposable
{
    internal const string ControllerEvidenceMarker = "CharacterVoiceSessionAdapter.held-controller.v1";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ProviderName = "Rukari.CharacterVoice";
    private const string ProviderSha256 = "06C4FE4F87282258FE45A8436FCDDBDCA079C58ED6356C31391F5BC3D855C054";
    private static readonly Guid ProviderMvid = new("3c455a03-837f-40d5-a719-281df650be68");
    private static CharacterVoiceSessionAdapter? current;
    private readonly int thread;
    private string storyHash = "";
    private string[] known = Array.Empty<string>();
    private readonly CharacterVoiceStoryBinding binding = new();
    private readonly CharacterVoiceCommitState? commit = ExportBuildProfile.DevelopmentAuditEnabled ? new() : null;
    private readonly Assembly providerAssembly, controllerAssembly;
    private readonly string providerPath, controllerPath, controllerSha256;
    private readonly Guid controllerMvid;
    private readonly Action<Exception> onFault;
    private readonly Action<string> log;
    private readonly Type runtime;
    private readonly object gate;
    private readonly Harmony hooks;
    private readonly List<Hook> targets = new();
    private readonly Dictionary<string, Pin> pins = new(StringComparer.Ordinal);
    private readonly HashSet<string> requested = new(StringComparer.Ordinal);
    private Func<CharacterVoicePosition>? position;
    private CharacterVoiceSessionTrace? trace;
    private Test? player;
    private AudioManager? audio;
    private AudioSource? voice;
    private ScenarioResourceManager? resources;
    private string settings = "";
    private double preloadStarted;
    private bool ready, armed, disposed, faultReported, preloadPrepared;
    private Exception? failure;
    private CharacterVoiceCommitFilePin[]? commitFiles;
    private CommitPatchPin[]? commitPatches;

    private sealed record Pin(string Path, CharacterVoiceAsset? Asset, IntPtr ClipPointer, AudioClip Clip, int SampleFrames, int Channels, int SampleRate);
    private sealed record Hook(MethodInfo Target, MethodInfo? Prefix, MethodInfo? Postfix);
    private sealed record CommitPatchPin(MethodInfo Target, string Shape);
    internal readonly record struct Observation(bool Eligible, long Sequence, int Phase);

    internal static bool IsProviderLoaded => AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == ProviderName);
    internal bool IsAttached => ReferenceEquals(current, this) && failure == null && !binding.IsFaulted && !disposed && (commit?.IsHealthy ?? true);
    internal bool StoryBound => binding.IsBound && IsAttached;
    internal bool NoOwnedVoiceExpected => StoryBound && known.Length == 0;
    internal bool IsReady => ready && StoryBound;
    internal bool IsArmed => armed && failure == null && !disposed && (commit?.IsHealthy ?? true);
    internal string FailureReason => failure?.Message ?? commit?.FailureReason ?? "";
    internal IReadOnlyList<CharacterVoiceAsset> Assets
    { get { RequireAudit(); return Array.AsReadOnly(pins.Values.Select(pin => pin.Asset!).OrderBy(asset => asset.Identifier, StringComparer.Ordinal).ToArray()); } }

    internal static string[] CollectKnownIdentifiers(GenericScenarioExcelTable frozenSave)
    {
        if (frozenSave == null || frozenSave.ByteBuffer == null || frozenSave.DataListLength <= 0)
            throw new ArgumentException("The actual frozen native story table is required.", nameof(frozenSave));
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < frozenSave.DataListLength; index++)
        {
            var boxed = frozenSave.DataList(index) ?? throw new NotSupportedException("The frozen story contains a missing native row.");
            // This host's IScenarioScriptExcel interface dispatch is not a
            // trustworthy getter route. Use the verified concrete row only.
            var concrete = boxed.Cast<GenericScenarioExcel>();
            if (concrete.GetIl2CppType().FullName != "GenericScenarioExcel")
                throw new NotSupportedException("Known voice extraction requires actual GenericScenarioExcel rows.");
            string? identifier = concrete.VoiceJp;
            if (identifier?.StartsWith("aavt/voice/", StringComparison.Ordinal) != true) continue;
            if (!CharacterVoiceIdentity.IsIdentifier(identifier)) throw new NotSupportedException("The frozen story contains a malformed owned voice identifier.");
            identifiers.Add(identifier);
        }
        return identifiers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal static CharacterVoiceSessionAdapter Attach(Action<Exception> onFault, Action<string> log) => new(onFault, log);

    private CharacterVoiceSessionAdapter(Action<Exception> onFault, Action<string> log)
    {
        this.onFault = onFault ?? throw new ArgumentNullException(nameof(onFault));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        if (current != null) throw new InvalidOperationException("Another CharacterVoice session scope is retained.");
        thread = Environment.CurrentManagedThreadId;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == ProviderName).ToArray();
        if (assemblies.Length != 1) throw new NotSupportedException("Exactly one installed CharacterVoice runtime is required.");
        var assembly = assemblies[0];
        if (assembly.GetName().Version != new Version(1, 6, 8, 0))
            throw new NotSupportedException("CharacterVoice native API version is unsupported.");
        if (ExportBuildProfile.DevelopmentAuditEnabled && (assembly.ManifestModule.ModuleVersionId != ProviderMvid
            || !string.Equals(HashFile(assembly.Location), ProviderSha256, StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("CharacterVoice is outside the exact inspected native 1.6.8 build.");
        providerAssembly = assembly; providerPath = ExportBuildProfile.DevelopmentAuditEnabled ? Path.GetFullPath(assembly.Location) : "";
        controllerAssembly = typeof(CharacterVoiceSessionAdapter).Assembly;
        controllerPath = ExportBuildProfile.DevelopmentAuditEnabled ? Path.GetFullPath(controllerAssembly.Location) : "";
        controllerSha256 = ExportBuildProfile.DevelopmentAuditEnabled ? HashFile(controllerPath) : "";
        controllerMvid = ExportBuildProfile.DevelopmentAuditEnabled ? controllerAssembly.ManifestModule.ModuleVersionId : Guid.Empty;
        runtime = assembly.GetType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", true)!;
        gate = runtime.GetField("Gate", Static)?.GetValue(null) ?? throw new MissingFieldException(runtime.FullName, "Gate");
        RequireRuntime();
        hooks = new Harmony("halocue.aa.videoexport.character-voice-session");
        if (!ExportBuildProfile.DevelopmentAuditEnabled) { current = this; return; }
        AddTarget("SetVoicePrefix", typeof(bool), new[] { typeof(string) },
            "AE92DDFA6FFB588F4DC42C9985155D93DE211F4D7CAF70B591D7C6DD75E9C30B", null, nameof(AfterRequest));
        AddTarget("ObserveOwnedPlayback", typeof(bool), new[] { typeof(bool) },
            "7CC3076FD0E821A000A7D11B827CF4864E4CBA66BF3E72A8F9DA3215DB91F5CA", nameof(BeforeObserve), nameof(AfterObserve));
        AddTarget("TickOnMainThread", typeof(void), Type.EmptyTypes,
            "3789D8EBA2C4B3528B8ACB7B947FC1C051215F2C4F6DDB5A0C75D74552D7CFF2", null, nameof(AfterTick));
        AddTarget("ResetOnMainThread", typeof(void), new[] { typeof(string) },
            "399E8880C90887FC475F3F30A13FA79845D33E62058C0216C109F7AF86B52F1E", null, nameof(AfterReset));
        try
        {
            foreach (var target in targets)
            {
                ValidateHook(target, installed: false);
                hooks.Patch(target.Target, target.Prefix == null ? null : new HarmonyMethod(target.Prefix),
                    target.Postfix == null ? null : new HarmonyMethod(target.Postfix));
            }
            foreach (var target in targets) ValidateHook(target, installed: true);
            current = this;
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            foreach (var target in targets)
                try { hooks.Unpatch(target.Target, HarmonyPatchType.All, hooks.Id); }
                catch (Exception cleanupError) { errors.Add(cleanupError); }
            if (ReferenceEquals(current, this)) current = null;
            throw new AggregateException("Native voice observers could not be installed; owner-scoped rollback attempted.", errors);
        }
    }

    // Safe before story reads: this validates the held instance's exact runtime
    // and already-installed hooks. It does not imply a bound/preloaded story.
    internal void ValidateController() => GuardPublic(() =>
    {
        RequireUsable();
        if (!ReferenceEquals(current, this)) throw new InvalidOperationException("The retained voice backend is no longer current.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) foreach (var target in targets) ValidateHook(target, installed: true);
        return true;
    });

    internal void BindStory(string frozenStoryHash, IEnumerable<string> knownIdentifiers) => GuardPublic(() =>
    {
        ValidateController();
        binding.BindStory(frozenStoryHash, knownIdentifiers);
        var bound = binding.RequireBound(nameof(BindStory));
        storyHash = bound.StoryKey; known = bound.Identifiers.ToArray();
        return true;
    });

    // Invoke after the real selected-story resource context exists, before
    // primer/authored frame zero. Repeat for replay; only this scope's request
    // markers reset, never CharacterVoice NativeLoads or Unity's caches.
    internal void PreparePass() => GuardPublic(() => { PreparePassCore(); return true; });
    private void PreparePassCore()
    {
        RequireUsable();
        binding.RequireBound(nameof(PreparePass));
        if (armed) throw new InvalidOperationException("End the previous voice pass before preparing another.");
        resources = ScenarioResourceManager.Instance ?? throw new InvalidOperationException("Native resources are unavailable.");
        requested.Clear(); ready = false; preloadStarted = Wall(); preloadPrepared = true;
    }

    internal bool PumpPreload() => GuardPublic(PumpPreloadCore);
    private bool PumpPreloadCore()
    {
        RequireUsable();
        binding.RequireBound(nameof(PumpPreload));
        if (!preloadPrepared) throw new CharacterVoiceTraceException("voice_preload_unprepared", "PreparePass is required before pumping native voice preload.");
        if (armed) throw new InvalidOperationException("Voice warmup cannot run inside authored capture.");
        if (resources == null || ScenarioResourceManager.Instance == null || resources.Pointer != ScenarioResourceManager.Instance.Pointer)
            throw new InvalidOperationException("Native resource manager changed during this voice warmup.");
        if (resources.GlobalOverrideData == null || resources.LocalOverrideData == null || resources.Preloading)
            return NotReady("waiting for native selected-story resources");
        bool all = true;
        foreach (string identifier in known)
        {
            if (!resources.TryGetVoiceOverridePath(identifier, out string path) || !File.Exists(path))
                throw new CharacterVoiceTraceException("voice_asset_unresolved", "The original native resolver did not resolve " + identifier);
            if (resources._loadedCustomVoices == null || !resources._loadedCustomVoices.ContainsKey(identifier)
                || resources._loadedCustomVoices[identifier] == null || resources._loadedCustomVoices[identifier].loadState != AudioDataLoadState.Loaded)
            {
                all = false;
                // Respect existing provider ownership; never clear or replay an
                // unfinished request to make this scope appear ready.
                var nativeLoads = runtime.GetField("NativeLoads", Static)?.GetValue(null) as HashSet<string>;
                if (nativeLoads?.Contains(identifier) != true && requested.Add(identifier))
                    resources.StartCoroutine(resources.CoTryPreloadVoice(identifier));
                continue;
            }
            var clip = resources._loadedCustomVoices[identifier];
            RequireReadyClip(clip);
            var asset = ExportBuildProfile.DevelopmentAuditEnabled ? InspectClip(identifier, path, clip) : null;
            if (ExportBuildProfile.DevelopmentAuditEnabled && pins.TryGetValue(identifier, out var old) && old.Asset != asset)
                throw new CharacterVoiceTraceException("voice_asset_changed", "Native voice content changed across warmup/passes: " + identifier);
            pins[identifier] = new Pin(Path.GetFullPath(path), asset, clip.Pointer, clip, clip.samples, clip.channels, clip.frequency);
        }
        if (!all) return NotReady("waiting for the original native voice preload/cache");
        ready = true;
        return true;
    }

    internal void BeginPass(Test player, int fps, int sampleRate, Func<CharacterVoicePosition> position, CharacterVoiceReceipt? expected = null) =>
        GuardPublic(() => { BeginPassCore(player, fps, sampleRate, position, expected); return true; });
    private void BeginPassCore(Test player, int fps, int sampleRate, Func<CharacterVoicePosition> position, CharacterVoiceReceipt? expected)
    {
        RequireUsable();
        binding.RequireBound(nameof(BeginPass));
        if (armed || !preloadPrepared || !ready || pins.Count != known.Length) throw new InvalidOperationException("All known native voices must be ready before author frame zero.");
        if (player == null || player.previewMode || Test.Instance == null || Test.Instance.Pointer != player.Pointer)
            throw new InvalidOperationException("A real current non-preview native player is required.");
        if (GateBool("Active")) throw new InvalidOperationException("A preexisting voice request cannot be absorbed into a fresh strict pass.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) foreach (var target in targets) ValidateHook(target, installed: true);
        this.player = player; audio = AudioManager.Instance ?? throw new InvalidOperationException("Native AudioManager is unavailable.");
        voice = audio.voiceSource ?? throw new InvalidOperationException("Native voice source is unavailable.");
        this.position = position ?? throw new ArgumentNullException(nameof(position));
        var first = position();
        if (first.Frame != 0 || first.SampleFrame != 0)
            throw new InvalidOperationException("Arm the voice pass after audio timeline reset and before the first authored frame.");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            settings = SettingsIdentity();
            commit!.BeginPass(expected);
            trace = new CharacterVoiceSessionTrace(storyHash, fps, sampleRate, settings, Assets, expected, noOwnedVoiceExpected: known.Length == 0);
        }
        else if (expected != null) throw new InvalidOperationException("Production voice playback does not consume audit receipts.");
        foreach (string identifier in known) RequirePin(identifier, inspectContent: true);
        armed = true;
        SafeLog("CharacterVoice known-voice pass armed; assets=" + known.Length + "; replay=" + (expected != null));
    }

    internal void ValidateFrame() => GuardPublic(() => { ValidateFrameCore(); return true; });
    private void ValidateFrameCore()
    {
        RequireUsable();
        binding.RequireBound(nameof(ValidateFrame));
        if (!armed) throw new CharacterVoiceTraceException("voice_pass_unarmed", "No authored voice pass is armed on this retained backend.");
        RequireOwner();
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (known.Length == 0 && GateBool("Active"))
            throw new CharacterVoiceTraceException("voice_unexpected_request", "The original Runtime has an active owned request in a no-owned-voice story scope.");
        if (settings != SettingsIdentity()) throw new CharacterVoiceTraceException("voice_settings_changed", "Native user/provider voice settings changed during the pass.");
        foreach (var target in targets) ValidateHook(target, installed: true);
        ObserveTerminal();
        if (trace!.IsPlaying)
        {
            string identifier = GateText("VoiceIdentifier");
            var pin = RequirePin(identifier, inspectContent: false);
            RequireActualSource(pin);
            trace.ValidatePlayback(Sequence(), pin.Asset!.AssetId, voice!.volume, voice.pitch);
        }
    }

    internal void CompletePass(CharacterVoicePosition end) => GuardPublic(() =>
    {
        if (ExportBuildProfile.DevelopmentAuditEnabled) throw new InvalidOperationException("DevelopmentAudit completes voice passes through its receipt path.");
        ValidateFrameCore();
        if (end.Frame < 0 || end.SampleFrame < 0) throw new ArgumentOutOfRangeException(nameof(end));
        armed = false; position = null;
        return true;
    });
    internal CharacterVoiceReceipt SealPass(CharacterVoicePosition end) { RequireAudit(); return GuardPublic(() => SealPassCore(end)); }
    private CharacterVoiceReceipt SealPassCore(CharacterVoicePosition end)
    {
        RequireUsable(); ValidateFrame();
        if (!armed || trace == null) throw new InvalidOperationException("No authored voice pass is active.");
        foreach (string identifier in known) RequirePin(identifier, inspectContent: true);
        var receipt = trace.Seal(end);
        commit!.CompletePass(receipt);
        armed = false; position = null;
        SafeLog("CharacterVoice native event receipt sealed: " + receipt.TraceSha256);
        return receipt;
    }

    internal void EndPass()
    {
        RequireThread();
        void Cleanup()
        {
            if (ExportBuildProfile.DevelopmentAuditEnabled && armed) LatchFailure(new CharacterVoiceTraceException("voice_session_incomplete", "Export ended before the known voice pass sealed."));
            armed = false; position = null; player = null; audio = null; voice = null; ready = false; preloadPrepared = false;
        }
        if (ExportBuildProfile.DevelopmentAuditEnabled) commit!.Cleanup(Cleanup); else Cleanup();
        // Original Runtime reset/load ownership and the delay-clock EndExport
        // remain authoritative. This observer performs neither operation.
    }

    // Main-thread final seal only. Both successfully sealed passes must already
    // belong to this retained backend; no preflight-cache shortcut can supply it.
    internal void SealForCommit() => GuardPublic(() =>
    {
        RequireAudit();
        ValidateController();
        binding.RequireBound(nameof(SealForCommit));
        if (armed) throw new CharacterVoiceTraceException("voice_commit_incomplete", "An authored voice pass is still active.");
        var filePins = pins.Values.Select(pin => new CharacterVoiceCommitFilePin(pin.Path, pin.Asset!.FileSha256)).ToList();
        filePins.Add(new CharacterVoiceCommitFilePin(providerPath, ProviderSha256));
        filePins.Add(new CharacterVoiceCommitFilePin(controllerPath, controllerSha256));
        commitFiles = filePins.ToArray();
        var methods = targets.Select(target => target.Target).ToList();
        methods.Add(gate.GetType().GetMethod("ObservePlayback", new[] { typeof(long), typeof(bool), typeof(double), typeof(double) })!);
        methods.Add(gate.GetType().GetMethod("Expire", new[] { typeof(double) })!);
        commitPatches = methods.Select(method => new CommitPatchPin(method, PatchShape(method))).ToArray();
        using (CharacterVoiceCommitFiles.Acquire(commitFiles)) ValidateManagedCommitProof();
        commit!.SealForCommit();
        return true;
    });

    // Worker-safe: only immutable receipts, managed metadata and retained file
    // pins are read. No Runtime/Gate instance, player, AudioSource, clip, Unity
    // clock or main-thread callback is touched in these publication methods.
    internal void ValidateForCommit()
    {
        RequireAudit();
        commit!.ValidateForCommit(() => { ValidateManagedCommitProof(); using var files = CharacterVoiceCommitFiles.Acquire(commitFiles!); });
    }

    internal IDisposable AcquireCommitScope()
    {
        RequireAudit();
        return commit!.AcquireCommitScope(() => { ValidateManagedCommitProof(); return CharacterVoiceCommitFiles.Acquire(commitFiles!); });
    }

    private void ValidateManagedCommitProof()
    {
        if (disposed || failure != null || !binding.IsBound || !ReferenceEquals(current, this) || commitFiles == null || commitPatches == null)
            throw new CharacterVoiceTraceException("voice_commit_incomplete", "The retained voice backend has no immutable publication proof.");
        var provider = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == ProviderName).ToArray();
        var controller = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == controllerAssembly.GetName().Name).ToArray();
        if (provider.Length != 1 || !ReferenceEquals(provider[0], providerAssembly)
            || providerAssembly.GetName().Version != new Version(1, 6, 8, 0) || providerAssembly.ManifestModule.ModuleVersionId != ProviderMvid
            || !SamePath(providerAssembly.Location, providerPath) || controller.Length != 1 || !ReferenceEquals(controller[0], controllerAssembly)
            || controllerAssembly.ManifestModule.ModuleVersionId != controllerMvid || !SamePath(controllerAssembly.Location, controllerPath))
            throw new CharacterVoiceTraceException("voice_commit_assembly_changed", "Provider/controller assembly identity changed before publication.");
        foreach (var target in targets) ValidateHook(target, installed: true);
        foreach (var pin in commitPatches)
            if (PatchShape(pin.Target) != pin.Shape)
                throw new CharacterVoiceTraceException("voice_commit_hooks_changed", "A retained voice/delay observation target changed before publication.");
    }

    private static string PatchShape(MethodInfo target)
    {
        var info = Harmony.GetPatchInfo(target) ?? throw new CharacterVoiceTraceException("voice_commit_hooks_changed", "A retained voice hook disappeared.");
        static object Describe(Patch patch) => new
        {
            patch.owner, patch.priority, before = patch.before ?? Array.Empty<string>(), after = patch.after ?? Array.Empty<string>(),
            module = patch.PatchMethod.Module.ModuleVersionId, token = patch.PatchMethod.MetadataToken,
            type = patch.PatchMethod.DeclaringType?.FullName, method = patch.PatchMethod.Name
        };
        return CharacterVoiceIdentity.Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            prefixes = info.Prefixes.Select(Describe).ToArray(), postfixes = info.Postfixes.Select(Describe).ToArray(),
            transpilers = info.Transpilers.Select(Describe).ToArray(), finalizers = info.Finalizers.Select(Describe).ToArray(),
            ilManipulators = info.ILManipulators.Select(Describe).ToArray()
        }));
    }

    private static void AfterRequest(string __0)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var owner = current;
        if (owner?.armed != true) return;
        if (Environment.CurrentManagedThreadId != owner.thread)
        {
            if (__0?.StartsWith("aavt/voice/", StringComparison.Ordinal) == true)
                owner.LatchFailure(new CharacterVoiceTraceException("voice_off_thread_request", "An owned native voice request arrived off the main thread."));
            return;
        }
        owner.ObserveSafely(() =>
        {
            if (__0 == null || !__0.StartsWith("aavt/voice/", StringComparison.Ordinal)) return;
            if (owner.known.Length == 0) throw new CharacterVoiceTraceException("voice_unexpected_request", "A native owned voice request contradicted the frozen no-owned-voice story scope.");
            if (!CharacterVoiceIdentity.IsIdentifier(__0)) throw new CharacterVoiceTraceException("voice_asset_unknown", "A malformed owned identifier reached capture.");
            owner.RequireOwner();
            long sequence = owner.Sequence();
            if (owner.trace!.RuntimeSequence == sequence) return; // Original same-request native replay is not a second request.
            if (owner.GateText("VoiceIdentifier") != __0 || owner.Phase() is not (1 or 2))
                throw new CharacterVoiceTraceException("voice_request_rejected", "The installed Runtime did not accept the known voice request.");
            owner.RequirePin(__0, inspectContent: true);
            if (owner.resources!._loadedCustomVoices == null || !owner.resources._loadedCustomVoices.ContainsKey(__0))
                throw new CharacterVoiceTraceException("voice_not_preloaded", "A native voice cache miss reached authored capture.");
            owner.trace.Request(sequence, __0, owner.Position(), owner.GateDouble("PostPlaybackDelaySeconds"));
        });
    }

    private static void BeforeObserve(out Observation __state)
    {
        __state = default; if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var owner = current;
        __state = default;
        if (owner?.armed != true || Environment.CurrentManagedThreadId != owner.thread) return;
        Observation observed = default;
        owner.ObserveSafely(() => observed = new Observation(true, owner.Sequence(), owner.Phase()));
        __state = observed;
    }

    private static void AfterObserve(bool __0, Observation __state)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var owner = current;
        if (owner?.armed != true || !__state.Eligible || Environment.CurrentManagedThreadId != owner.thread) return;
        owner.ObserveSafely(() =>
        {
            owner.RequireOwner();
            if (__state.Sequence != owner.Sequence()) throw new CharacterVoiceTraceException("voice_request_identity", "Request changed inside native playback observation.");
            int phase = owner.Phase();
            if (__state.Phase == 2 && phase == 3 && __0)
            {
                var pin = owner.RequirePin(owner.GateText("VoiceIdentifier"), inspectContent: true);
                owner.RequireActualSource(pin);
                owner.trace!.NativeStart(__state.Sequence, owner.Position(), pin.Asset!.AssetId, owner.voice!.volume, owner.voice.pitch);
            }
            if (__state.Phase == 3 && phase is 7 or 4 && !__0)
            {
                owner.RequirePin(owner.GateText("VoiceIdentifier"), inspectContent: true);
                owner.trace!.NativeEnd(__state.Sequence, owner.Position());
            }
            owner.ObserveTerminal();
        });
    }

    private static void AfterTick()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var owner = current;
        if (owner?.armed == true && Environment.CurrentManagedThreadId == owner.thread) owner.ObserveSafely(owner.ObserveTerminal);
    }

    private static void AfterReset(string __0)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var owner = current;
        if (owner?.armed != true || Environment.CurrentManagedThreadId != owner.thread || owner.trace?.HasOpenRequest != true) return;
        owner.ObserveSafely(() => throw new CharacterVoiceTraceException("voice_native_reset", "The original Runtime reset an unfinished voice: " + __0));
    }

    private void ObserveTerminal()
    {
        if (!armed || trace == null || !trace.HasOpenRequest) return;
        RequireOwner();
        int phase = Phase();
        if (phase is 5 or 6 || Sequence() != trace.RuntimeSequence)
            throw new CharacterVoiceTraceException("voice_native_failed", "Original voice request failed/canceled: " + GateText("LastReason"));
        if (phase == 4 && trace.IsEnded) trace.DelayComplete(Sequence(), Position());
        else if (phase == 4) throw new CharacterVoiceTraceException("voice_native_observation_missing", "Runtime completed without a captured native start/end transition.");
    }

    private Pin RequirePin(string identifier, bool inspectContent)
    {
        if (!pins.TryGetValue(identifier, out var pin)) throw new CharacterVoiceTraceException("voice_asset_unknown", "Undeclared voice reached capture: " + identifier);
        if (resources == null || ScenarioResourceManager.Instance == null || resources.Pointer != ScenarioResourceManager.Instance.Pointer
            || resources._loadedCustomVoices == null || !resources._loadedCustomVoices.ContainsKey(identifier))
            throw new CharacterVoiceTraceException("voice_not_preloaded", "The pinned native voice cache is no longer ready.");
        var clip = resources._loadedCustomVoices[identifier];
        if (clip == null || clip.loadState != AudioDataLoadState.Loaded || clip.Pointer != pin.ClipPointer
            || clip.samples != pin.SampleFrames || clip.channels != pin.Channels || clip.frequency != pin.SampleRate)
            throw new CharacterVoiceTraceException("voice_clip_identity", "The native cached clip changed inside this pass.");
        if (!resources.TryGetVoiceOverridePath(identifier, out string path) || !SamePath(path, pin.Path))
            throw new CharacterVoiceTraceException("voice_asset_changed", "Original voice resolution changed inside this pass.");
        if (ExportBuildProfile.DevelopmentAuditEnabled && inspectContent && InspectClip(identifier, path, clip) != pin.Asset)
            throw new CharacterVoiceTraceException("voice_asset_changed", "Pinned file or decoded voice PCM changed inside this pass.");
        return pin;
    }

    private void RequireActualSource(Pin pin)
    {
        RequireOwner();
        if (voice!.clip == null || voice.clip.Pointer != pin.ClipPointer)
            throw new CharacterVoiceTraceException("voice_clip_identity", "Actual native playback is not using the pinned ready clip.");
    }

    private void RequireOwner()
    {
        RequireRuntime();
        if (player == null || player.previewMode || Test.Instance == null || Test.Instance.Pointer != player.Pointer
            || audio == null || AudioManager.Instance == null || AudioManager.Instance.Pointer != audio.Pointer
            || voice == null || audio.voiceSource == null || audio.voiceSource.Pointer != voice.Pointer)
            throw new CharacterVoiceTraceException("voice_owner_changed", "The actual native story player/voice channel changed.");
        if (GateBool("Active") && (ReadStatic<int>("_ownerPlayerInstanceId") != player.GetInstanceID() || ReadStatic<bool>("_ownerPreview")))
            throw new CharacterVoiceTraceException("voice_owner_changed", "CharacterVoice's real request belongs to another player or preview.");
    }

    private void RequireRuntime()
    {
        RequireThread();
        if (!ReadStatic<bool>("_installed") || ReadStatic<bool>("_pollingSuspended") || !ReadStatic<bool>("_usesNativeStartedCallback")
            || ReadStatic<int>("_mainThreadId") != thread || !ReferenceEquals(gate, runtime.GetField("Gate", Static)!.GetValue(null)))
            throw new NotSupportedException("The inspected installed main-thread native-callback CharacterVoice runtime is unavailable.");
    }

    private string SettingsIdentity()
    {
        var user = UserSettings.Instance?.data ?? throw new InvalidOperationException("Native playback settings are unavailable.");
        var native = AudioManager.Instance?.volumeInfo ?? throw new InvalidOperationException("Native volume state is unavailable.");
        double delay = Convert.ToDouble(runtime.GetProperty("PostPlaybackDelaySeconds", Static)!.GetValue(null));
        float strength = (float)runtime.GetProperty("VolumePercent", Static)!.GetValue(null)!;
        return FormattableString.Invariant($"master={user.masterVolume:R}|voice={user.voiceVolume:R}|native-master={native.masterVolume:R}|native-voice={native.voiceVolume:R}|mod-strength={strength:R}|post-delay={delay:R}");
    }

    private static void RequireReadyClip(AudioClip clip)
    {
        if (clip.loadState != AudioDataLoadState.Loaded || clip.samples <= 0 || clip.channels is < 1 or > 8 || clip.frequency <= 0)
            throw new CharacterVoiceTraceException("voice_clip_unready", "Native voice clip is not fully decoded.");
    }
    private static CharacterVoiceAsset InspectClip(string identifier, string path, AudioClip clip)
    {
        RequireReadyClip(clip);
        long count = (long)clip.samples * clip.channels;
        if (count > 16000000) throw new NotSupportedException("This finite native receipt supports at most 16000000 decoded float values per voice.");
        var native = new Il2CppStructArray<float>(new float[checked((int)count)]);
        if (!clip.GetData(native, 0)) throw new CharacterVoiceTraceException("voice_clip_unreadable", "Native decoded voice PCM could not be read without changing its load state.");
        float[] values = native.ToArray();
        if (values.Any(value => !float.IsFinite(value))) throw new CharacterVoiceTraceException("voice_clip_unreadable", "Native voice PCM contains nonfinite samples.");
        var bytes = new byte[checked(values.Length * sizeof(float))]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new CharacterVoiceAsset(identifier, HashFile(path), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), clip.samples, clip.channels, clip.frequency);
    }

    private bool NotReady(string reason)
    {
        ready = false;
        if (Wall() - preloadStarted >= 20) throw new CharacterVoiceTraceException("voice_preload_timeout", reason + "; author frame zero was not released.");
        return false;
    }

    private void AddTarget(string name, Type result, Type[] arguments, string ilHash, string? prefix, string? postfix)
    {
        var target = runtime.GetMethod(name, Static, null, arguments, null) ?? throw new MissingMethodException(runtime.FullName, name);
        if (!target.IsStatic || target.ReturnType != result || target.GetMethodBody()?.GetILAsByteArray() is not { } il
            || Convert.ToHexString(SHA256.HashData(il)) != ilHash)
            throw new NotSupportedException("Native voice observer target changed: " + name);
        targets.Add(new Hook(target, prefix == null ? null : typeof(CharacterVoiceSessionAdapter).GetMethod(prefix, Static)!,
            postfix == null ? null : typeof(CharacterVoiceSessionAdapter).GetMethod(postfix, Static)!));
    }

    private void ValidateHook(Hook hook, bool installed)
    {
        var info = Harmony.GetPatchInfo(hook.Target);
        int prefixes = installed && hook.Prefix != null ? 1 : 0, postfixes = installed && hook.Postfix != null ? 1 : 0;
        if (info == null)
        {
            if (prefixes + postfixes != 0) throw new InvalidOperationException("Native voice observation hook disappeared.");
            return;
        }
        if (info.Transpilers.Any() || info.Finalizers.Any() || info.ILManipulators.Any() || info.Prefixes.Count != prefixes || info.Postfixes.Count != postfixes
            || info.Prefixes.Any(patch => patch.owner != hooks.Id || patch.PatchMethod != hook.Prefix)
            || info.Postfixes.Any(patch => patch.owner != hooks.Id || patch.PatchMethod != hook.Postfix))
            throw new NotSupportedException("Native voice observation target has an unknown or missing patch: " + hook.Target.Name);
    }

    private void ObserveSafely(Action observe)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (failure != null || disposed) return;
        try { commit!.Run(() => { RequireUsable(); observe(); return true; }); }
        catch (Exception error)
        {
            LatchFailure(error);
            if (!faultReported)
            {
                faultReported = true;
                try { onFault(error); } catch (Exception callbackError) { SafeLog("CharacterVoice session fault callback failed: " + callbackError.Message); }
            }
        }
    }

    private T GuardPublic<T>(Func<T> operation)
    {
        try { return ExportBuildProfile.DevelopmentAuditEnabled ? commit!.Run(operation) : operation(); }
        catch (Exception error) { LatchFailure(error); throw; }
    }

    private void LatchFailure(Exception error)
    {
        void Latch() { failure ??= error; trace?.Abort(error.Message); ready = false; }
        if (ExportBuildProfile.DevelopmentAuditEnabled) commit!.Fail(error, Latch); else Latch();
    }
    private static void RequireAudit()
    { if (!ExportBuildProfile.DevelopmentAuditEnabled) throw new InvalidOperationException("Voice audit receipts are available only in DevelopmentAudit."); }

    private void RequireUsable()
    {
        RequireThread();
        if (disposed) throw new ObjectDisposedException(nameof(CharacterVoiceSessionAdapter));
        if (failure != null) throw new CharacterVoiceTraceException("voice_session_faulted", failure.Message);
        if (binding.IsFaulted) throw new CharacterVoiceTraceException("voice_binding_faulted", "This retained backend's story binding was invalidated.");
        RequireRuntime();
        CharacterVoiceDelayClock.ValidateBinding();
    }
    private void RequireThread() { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("CharacterVoice session operations require the native main thread."); }
    private T ReadStatic<T>(string name) => (T)runtime.GetField(name, Static)!.GetValue(null)!;
    private long Sequence() => (long)gate.GetType().GetProperty("RequestSequence", Instance)!.GetValue(gate)!;
    private int Phase() => Convert.ToInt32(gate.GetType().GetProperty("Phase", Instance)!.GetValue(gate));
    private bool GateBool(string name) => (bool)gate.GetType().GetProperty(name, Instance)!.GetValue(gate)!;
    private double GateDouble(string name) => (double)gate.GetType().GetProperty(name, Instance)!.GetValue(gate)!;
    private string GateText(string name) => gate.GetType().GetProperty(name, Instance)!.GetValue(gate)?.ToString() ?? "";
    private CharacterVoicePosition Position() => position?.Invoke() ?? throw new InvalidOperationException("Voice event clock is unavailable.");
    private static double Wall() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static string HashFile(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private void SafeLog(string message) { try { log(message); } catch { } }

    public void Dispose()
    {
        RequireThread();
        if (disposed) return;
        void Cleanup()
        {
            EndPass();
            var errors = new List<Exception>();
            foreach (var target in targets)
                try { hooks.Unpatch(target.Target, HarmonyPatchType.All, hooks.Id); }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) { failure = new AggregateException("Voice observation cleanup remains incomplete.", errors); throw failure; }
            disposed = true; pins.Clear(); if (ReferenceEquals(current, this)) current = null;
        }
        if (ExportBuildProfile.DevelopmentAuditEnabled) commit!.DisposeBackend(Cleanup); else Cleanup();
    }
}
