using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.Plugin;

// Original menu callbacks retain control of scene exit, BGM selection and Sfx
// filtering. Production retains only functional resources; DevelopmentAudit
// additionally observes code/configuration provenance and publication proofs.
internal static class RetainedMenuPlaybackBoundary
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Definition[] Definitions =
    {
        new("MenuBgmSelector", new("MenuBgmSelector", new(1,1,4,0), new("4e7ca964-4de8-4d46-8301-03a0b29cb1cb"),
            "5C56D01047FC6CABE7DAD3D51D95D733A8620FD03DC5A50041BC61ACC5CB98D4"), "foxxlight.azurearchive.menubgmselector", "menu-bgm"),
        new("AAMenuPrototype", new("AAMenuPrototype", new(0,0,19,0), new("47739a97-91ed-4450-9928-e6b53bf3619e"),
            "1610E896FDD738DAED88EF418B5D037118D7B4A2D02B1149C7CFC4F4BFF1A589"), "sakura.aa.menuprototype.entry", "menu-scene"),
        new("SfxFolderMod", new("SfxFolderMod", new(1,7,3,0), new("5da3b0cd-f4f3-421e-bc88-3ee5d6206c87"),
            "29CA0E83813704E07FE610060B177A85AB6B28EAB7FD9805E796D85B5D8B25D3"), "foxxlight.azurearchive.sfxfolders", "sfx-map")
    };
    private static readonly Dictionary<string, Binding> Bindings = new(StringComparer.Ordinal);
    private static readonly Dictionary<MethodBase, bool> Observed = new(); // true = explicit user configuration mutation
    private static readonly MethodInfo Before = Method(nameof(BeforeObserved));
    private static readonly MethodInfo After = Method(nameof(AfterObserved));
    private static readonly MethodInfo BeforeWrite = Method(nameof(BeforeConfigWrite));
    private static readonly MethodInfo FinishWrite = Method(nameof(AfterConfigWrite));
    private static RetainedInstallSchedule installSchedule = new();
    private static readonly System.Diagnostics.Stopwatch InstallClock = System.Diagnostics.Stopwatch.StartNew();
    private static bool bindingsReady, renameAttemptObserved;
    private static (long Length, DateTime Written)? configStamp;
    private static (bool Found, string Name) configName;
    internal static long InstallationAttempts { get; private set; }
    internal static long PayloadChecks { get; private set; }
    internal static long ReadinessProbes { get; private set; }
    private static RetainedPatchGuard? guards;
    private static RetainedPlaybackProof? proof;
    private static RetainedPlaybackController? execution;
    private static Harmony? harmony;
    private static Action<string>? log;
    private static int mainThread;
    private static string configRoot = "";
    private static bool cleanupFailed;
    private static IDisposable? retained;
    internal static string Status { get; private set; } = "stopped";
    internal static string FailureReason { get; private set; } = "";
    internal static bool IsHeld => proof?.IsHeld == true || execution?.IsHeld == true;
    internal static bool IsPlayback => proof?.IsPlayback == true || execution?.IsPlayback == true;
    internal static long Validations { get; private set; }

    internal static void Initialize(Harmony owner, Action<string> logger, Action<Exception> onFault, string activeConfigRoot)
    {
        if (harmony != null) throw new InvalidOperationException("Retained menu boundary is already initialized.");
        mainThread = Environment.CurrentManagedThreadId; configRoot = Path.GetFullPath(activeConfigRoot);
        harmony = new Harmony(owner.Id + ".retained-menu-playback"); log = logger;
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            guards = new RetainedPatchGuard(harmony);
            proof = new RetainedPlaybackProof(ReadIdentity, ValidatePlaybackState, onFault);
        }
        else execution = new RetainedPlaybackController(ValidatePlaybackState, onFault);
        installSchedule = new RetainedInstallSchedule(); bindingsReady = renameAttemptObserved = false; configStamp = null;
        InstallationAttempts = PayloadChecks = ReadinessProbes = 0;
        Status = "waiting"; FailureReason = "";
    }
    internal static void TryInstall()
    {
        MainThread();
        if (!ExportBuildProfile.DevelopmentAuditEnabled) { TryInstallExecution(); return; }
        if (harmony == null || cleanupFailed || IsHeld) return;
        double now = InstallClock.Elapsed.TotalSeconds;
        if (!installSchedule.ShouldProbe(now)) return;
        ReadinessProbes++;
        string state;
        try { state = InstallState(); }
        catch (Exception error)
        {
            string errorState = "config-invalid:" + error.Message;
            if (installSchedule.ShouldAttempt(now, errorState))
                InstallFailed(now, "Menu configuration input: " + error.Message);
            return;
        }
        if (!installSchedule.ShouldAttempt(now, state)) return;
        InstallationAttempts++;
        foreach (var definition in Definitions)
        {
            var candidates = Loaded(definition.Name);
            if (candidates.Length == 0) continue;
            if (Bindings.TryGetValue(definition.Name, out var existing))
            {
                try { if (definition.Name == "MenuBgmSelector") ValidateRenameCoverage(existing); }
                catch (Exception error) { InstallFailed(now, definition.Name + ": " + error.Message); return; }
                continue;
            }
            int checkpoint = guards!.Count;
            var observedBefore = Observed.Keys.ToHashSet();
            try
            {
                if (candidates.Length != 1) throw new NotSupportedException("Duplicate menu/resource assembly: " + definition.Name);
                definition.Build.Validate(candidates[0]);
                PayloadChecks++;
                var binding = new Binding(definition, candidates[0]);
                Bind(binding); Bindings.Add(definition.Name, binding);
                if (definition.Name == "MenuBgmSelector") ValidateRenameCoverage(binding);
            }
            catch (Exception error)
            {
                // Keep a completed binding and its writer observers when only
                // admission readiness failed; do not rehash/repatch it per tick.
                if (!Bindings.ContainsKey(definition.Name))
                {
                    if (!guards.RollbackFrom(checkpoint)) cleanupFailed = true;
                    foreach (var method in Observed.Keys.Where(key=>!observedBefore.Contains(key)).ToArray()) Observed.Remove(method);
                }
                InstallFailed(now, definition.Name + ": " + error.Message);
                return;
            }
        }
        bindingsReady = true; installSchedule.Succeeded(); Status = "ready"; FailureReason = "";
    }
    internal static bool TryAcquireRetained(out IDisposable? lease)
    {
        MainThread(); lease = null; TryInstall();
        if (cleanupFailed || !bindingsReady) throw new InvalidOperationException("Retained menu boundary unavailable: " + FailureReason);
        if (proof == null && execution == null || IsHeld) throw new InvalidOperationException("Retained menu boundary is not initialized or already held.");
        if (proof?.ObservedWrites > 0) { Status = "waiting-write"; return false; }
        if (Bindings.TryGetValue("MenuBgmSelector", out var bgm) && !PrepareBgm(bgm))
        { Status = "waiting-audio"; return false; }
        var inputs = new List<IDisposable>();
        try
        {
            foreach (var binding in Bindings.Values)
            {
                // A retained read handle prevents payload changes between live
                // checks without re-hashing large DLLs on every output frame.
                if (ExportBuildProfile.DevelopmentAuditEnabled)
                {
                    inputs.Add(new FileStream(binding.Assembly.Location, FileMode.Open, FileAccess.Read, FileShare.Read));
                    binding.Definition.Build.Validate(binding.Assembly);
                }
                if (binding.Definition.Name == "MenuBgmSelector") RetainCustomBgm(binding, inputs);
            }
            retained = ExportBuildProfile.DevelopmentAuditEnabled ? RequiredProof().Acquire(inputs) : execution!.Acquire(inputs);
            lease = new BoundaryLease(retained);
            Status = "retained"; Validations = 0; return true;
        }
        catch
        {
            foreach (var input in inputs) input.Dispose();
            foreach (var binding in Bindings.Values) binding.ClearRetained();
            throw;
        }
    }
    internal static void BeginPlayback()
    {
        MainThread(); RenderControlV1.EnsureMainThread();
        if (!RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Menu playback requires actual render ownership.");
        void ExitMenu()
        {
            if (SceneManager.GetActiveScene().name != "MainScene") throw new InvalidOperationException("Menu playback entry is not in the inspected MainScene.");
            if (Bindings.TryGetValue("AAMenuPrototype", out var menu))
            {
                // The inspected Mod's own Update performs DestroySceneObjects,
                // StopMenuSpeech and camera/input restoration on scene exit.
                Call(menu.Type("AAMenuPrototype.MenuBehaviour"), "Update", menu.MenuOwner);
            }
        }
        if (ExportBuildProfile.DevelopmentAuditEnabled) RequiredProof().BeginPlayback(ExitMenu);
        else execution!.BeginPlayback(ExitMenu);
        Status = "playback";
    }
    internal static void ValidateActive() { if (ExportBuildProfile.DevelopmentAuditEnabled) RequiredProof().ValidateActive(); else execution!.ValidateActive(); Validations++; }
    internal static void EndPlayback() { if (proof?.IsHeld == true) proof.EndPlayback(); if (execution?.IsHeld == true) execution.EndPlayback(); if (IsHeld) Status = "retained"; }
    internal static string CaptureActiveProof() => RequiredProof().CaptureActiveProof();
    internal static void ValidateForCommit() => RequiredProof().ValidateForCommit();
    internal static IDisposable AcquireCommitScope() => RequiredProof().AcquireCommitScope();
    internal static string PlaybackSettingsIdentity() => IsHeld ? CaptureActiveProof() : "retained-menu-not-acquired";
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        string name = AssemblySimpleNameCache.Get(assembly) ?? "";
        level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>();
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return false;
        var definition = Definitions.SingleOrDefault(item => item.Name == name);
        if (definition == null) return false;
        try
        {
            if (!Bindings.TryGetValue(name, out var binding) || binding.Assembly != assembly)
                throw new InvalidOperationException("Exact retained menu binding is missing.");
            string live = CaptureActiveProof();
            level = StrictCapabilityLevel.Observed;
            evidence = new[] { "RetainedMenuPlaybackBoundary." + definition.Marker + ".v1", live,
                "exact-version-mvid-sha256", "original-effects-retained", "cache-reuse-disabled" };
        }
        catch (Exception error) { evidence = new[] { "retained-menu-proof-unavailable", error.Message }; }
        return true;
    }
    internal static bool Shutdown()
    {
        MainThread();
        try { EndPlayback(); retained?.Dispose(); retained = null; }
        catch (Exception error) { FailureReason = error.Message; Status = "cleanup-pending"; return false; }
        if (proof?.ObservedWrites > 0) { Status = "cleanup-pending"; FailureReason = "An observed original config write is still completing."; return false; }
        if (guards != null && !guards.RemoveAll()) { cleanupFailed = true; Status = "cleanup-pending"; FailureReason = guards.FailureReason; return false; }
        Bindings.Clear(); Observed.Clear(); guards = null; harmony = null; proof = null; execution = null; log = null;
        cleanupFailed = false; Status = "stopped"; FailureReason = ""; return true;
    }

    private static void TryInstallExecution()
    {
        if (harmony == null || cleanupFailed || IsHeld) return;
        double now = InstallClock.Elapsed.TotalSeconds;
        if (!installSchedule.ShouldProbe(now)) return;
        ReadinessProbes++;
        try
        {
            foreach (var definition in Definitions)
            {
                var candidates = Loaded(definition.Name);
                if (candidates.Length == 0) continue;
                if (candidates.Length != 1) throw new InvalidOperationException("The menu/resource API has multiple loaded providers: " + definition.Name);
                if (Bindings.ContainsKey(definition.Name)) continue;
                var binding = new Binding(definition, candidates[0]);
                if (definition.Name == "AAMenuPrototype")
                    binding.MenuOwner = Static(binding.Type("AAMenuPrototype.MenuEntryHooks"), "Owner")
                        ?? throw new InvalidOperationException("The original menu resource controller is not ready.");
                Bindings.Add(definition.Name, binding);
            }
            bindingsReady = true; installSchedule.Succeeded(); Status = "ready"; FailureReason = "";
        }
        catch (Exception error) { InstallFailed(now, error.Message); }
    }
    private static void Bind(Binding binding)
    {
        switch (binding.Definition.Name)
        {
            case "MenuBgmSelector":
                binding.RequireOriginal("AzureArchive.MenuBgmSelector.BgmPatches.PlayBgmPrefix", "prefix", "AudioManager", "PlayBGM");
                binding.RequireOriginal("AzureArchive.MenuBgmSelector.BgmPatches.SetBgmPrefix", "prefix", "AudioManager", "SetBGM");
                binding.RequireOriginal("AzureArchive.MenuBgmSelector.BgmPatches.PlayBgmTaskPrefix", "prefix", "AudioManager", "PlayBGMTask");
                binding.RequireOriginal("AzureArchive.MenuBgmSelector.BgmPatches.OnClickPrefix", "prefix", "UIEventListener", "OnClick");
                binding.RequireOriginal("AzureArchive.MenuBgmSelector.BgmPatches.FrameTick", "postfix", null, null);
                foreach (var member in new[] { "BgmPatches.HandleId", "BgmPatches.PlayBgmTaskPrefix", "AudioFileLoader.Request", "AudioFileLoader.Poll", "BgmApplier.OnGameLobbyToggled" })
                    Guard(binding, "AzureArchive.MenuBgmSelector." + member, false);
                foreach (var member in new[] { "BgmStore.Save", "BgmStore.Clear", "BgmSelectorPanel.Open", "BgmSelectorPanel.OnDeleteClicked", "AudioFilePicker.ShowDialog", "ModNameSwitch.Apply" })
                    Guard(binding, "AzureArchive.MenuBgmSelector." + member, true);
                var write = binding.Method("AzureArchive.MenuBgmSelector.ModNameSwitch.WriteTextPreserving");
                guards!.Add(write, BeforeWrite, finalizer: FinishWrite);
                break;
            case "AAMenuPrototype":
                binding.RequireOriginal("AAMenuPrototype.MenuEntryHooks.BeforeCatalogStart", "prefix", "Catalog", "Start");
                binding.MenuOwner = Static(binding.Type("AAMenuPrototype.MenuEntryHooks"), "Owner")
                    ?? throw new InvalidOperationException("Original menu controller has not initialized.");
                Guard(binding, "AAMenuPrototype.MenuBehaviour.Update", false);
                Guard(binding, "AAMenuPrototype.MenuBehaviour.LateUpdate", false);
                break;
            case "SfxFolderMod":
                binding.RequireOriginal("AzureArchive.SfxFolderMod.Patch_SoundExplorerStart.Postfix", "postfix", "SoundExplorer", "Start");
                binding.RequireOriginal("AzureArchive.SfxFolderMod.Patch_SoundItemInit.Postfix", "postfix", "SoundItem", "Init");
                binding.RequireOriginal("AzureArchive.SfxFolderMod.Patch_GetSoundNames.Postfix", "postfix", "ScenarioResourceManager", "GetSoundNames");
                binding.RequireOriginal("AzureArchive.SfxFolderMod.Patch_SoundExplorerFilter.Prefix", "prefix", "SoundExplorer", "Filter");
                Guard(binding, "AzureArchive.SfxFolderMod.SfxPatches.OnNames", false);
                Guard(binding, "AzureArchive.SfxFolderMod.SfxPatches.Decorate", false);
                Guard(binding, "AzureArchive.SfxFolderMod.SfxData.LoadConfig", true);
                break;
        }
        binding.OriginalGraph();
    }
    private static void Guard(Binding binding, string name, bool mutation)
    {
        var target = binding.Method(name); guards!.Add(target, Before, After); Observed.Add(target, mutation);
    }
    private static void BeforeObserved(MethodBase __originalMethod)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (__originalMethod.DeclaringType?.FullName == "AzureArchive.MenuBgmSelector.ModNameSwitch" && __originalMethod.Name == "Apply")
            renameAttemptObserved = true;
        if (proof?.IsHeld != true) return;
        if (Observed.TryGetValue(__originalMethod, out bool mutation) && mutation) { proof.Mutation(__originalMethod.DeclaringType?.Name + "." + __originalMethod.Name); return; }
        if (Environment.CurrentManagedThreadId == mainThread)
            try { proof.ValidateActive(); } catch { /* Fault is retained and the original callback still runs after cancellation. */ }
    }
    private static void AfterObserved(MethodBase __originalMethod)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (proof?.IsHeld == true && Environment.CurrentManagedThreadId == mainThread)
            try { proof.ValidateActive(); } catch { }
    }
    private static void BeforeConfigWrite(out RetainedPlaybackProof? __state)
    {
        __state = null; if (!ExportBuildProfile.DevelopmentAuditEnabled) return; var current = proof; if (current == null) return;
        current.BeforeWrite(); __state = current;
    }
    private static Exception? AfterConfigWrite(Exception? __exception, RetainedPlaybackProof? __state)
    {
        __state?.AfterWrite(); return __exception;
    }

    private static string ReadIdentity()
    {
        MainThread(); guards?.Validate();
        var parts = new List<string>();
        foreach (var definition in Definitions)
        {
            var assemblies = Loaded(definition.Name);
            if (assemblies.Length == 0) { if (Bindings.ContainsKey(definition.Name)) throw new InvalidOperationException("Retained module disappeared."); parts.Add(definition.Name + "=absent"); continue; }
            if (assemblies.Length != 1 || !Bindings.TryGetValue(definition.Name, out var binding) || binding.Assembly != assemblies[0])
                throw new InvalidOperationException("Late, duplicate or replaced retained module: " + definition.Name);
            parts.Add(definition.Name + "|" + definition.Build.Sha256 + "|" + binding.OriginalGraph());
            parts.Add(definition.Name switch
            {
                "MenuBgmSelector" => ReadBgm(binding),
                "AAMenuPrototype" => ReadMenu(binding),
                "SfxFolderMod" => ReadSfx(binding),
                _ => throw new InvalidOperationException("Unknown retained binding.")
            });
        }
        return Hash(string.Join("\n", parts));
    }
    private static void ValidatePlaybackState()
    {
        MainThread();
        if (SceneManager.GetActiveScene().name != "MainScene" || !RenderControlV1.IsRenderingOwned)
            throw new InvalidOperationException("Retained playback left the inspected scene or render lease.");
        if (Bindings.TryGetValue("AAMenuPrototype", out var menu))
        {
            object owner = menu.MenuOwner!;
            foreach (string name in new[] { "_menu", "_animation", "_backgroundCamera", "_target", "_portrait", "_voiceSource", "_voiceRequest", "_directBackgroundPanel" })
                if (Alive(Field(owner, name))) throw new InvalidOperationException("Original menu scene exit retained active resource: " + name);
            foreach (string name in new[] { "_voiceQueue", "_dialogueQueue", "_activeFx", "_callbacks", "_entryCameras", "_restoreNative", "_directMaterials" })
                if (Count(Field(owner, name)) != 0) throw new InvalidOperationException("Original menu scene exit retained pending work: " + name);
            if ((bool)Field(owner, "_dialogueRunning")! || (bool)Field(owner, "_directRendering")!)
                throw new InvalidOperationException("Original menu speech/rendering is still running in story playback.");
        }
        if (Bindings.TryGetValue("SfxFolderMod", out var sfx)
            && Alive(Static(sfx.Type("AzureArchive.SfxFolderMod.SfxSearch"), "Explorer")))
            throw new InvalidOperationException("SoundExplorer remains alive in story playback; its search path is not an idle editor boundary.");
    }
    private static bool PrepareBgm(Binding binding)
    {
        var applier = binding.Type("AzureArchive.MenuBgmSelector.BgmApplier");
        Call(applier, "PrewarmIfCustom", null);
        Call(binding.Type("AzureArchive.MenuBgmSelector.AudioFileLoader"), "Poll", null);
        if (Count(Static(binding.Type("AzureArchive.MenuBgmSelector.AudioFileLoader"), "_pending")) != 0) return false;
        object selection = Property(binding.Type("AzureArchive.MenuBgmSelector.BgmStore"), "Current", null)!;
        if (Field(selection, "Kind")!.ToString() == "Custom" && !Alive(Call(applier, "CurrentCustomClip", null))) return false;
        return true;
    }
    private static void RetainCustomBgm(Binding binding, List<IDisposable> inputs)
    {
        object selection = Property(binding.Type("AzureArchive.MenuBgmSelector.BgmStore"), "Current", null)!;
        if (Field(selection, "Kind")!.ToString() != "Custom") return;
        string path = Path.GetFullPath((string)Field(selection, "FilePath")!);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); inputs.Add(file);
        binding.CustomPath = path;
        if (ExportBuildProfile.DevelopmentAuditEnabled)
            using (var hash = SHA256.Create()) binding.CustomHash = Convert.ToHexString(hash.ComputeHash(file));
        binding.CustomClip = Call(binding.Type("AzureArchive.MenuBgmSelector.BgmApplier"), "CurrentCustomClip", null) as AudioClip
            ?? throw new InvalidOperationException("Custom menu BGM did not finish its original preload.");
    }
    private static string ReadBgm(Binding binding)
    {
        var store = binding.Type("AzureArchive.MenuBgmSelector.BgmStore");
        if (Static(store, "_loaded") is not true) throw new InvalidOperationException("Menu BGM store has not initialized.");
        object current = Property(store, "Current", null) ?? throw new InvalidOperationException("Menu BGM selection is missing.");
        if (Property(binding.Type("AzureArchive.MenuBgmSelector.BgmSelectorPanel"), "IsOpen", null) is true)
            throw new InvalidOperationException("Menu BGM selector is still open.");
        if (Count(Static(binding.Type("AzureArchive.MenuBgmSelector.AudioFileLoader"), "_pending")) != 0)
            throw new InvalidOperationException("Menu BGM has an unfinished native audio load.");
        string kind = Field(current, "Kind")!.ToString()!;
        string path = (string)Field(current, "FilePath")!;
        string clip = "";
        if (kind == "Custom")
        {
            var value = Call(binding.Type("AzureArchive.MenuBgmSelector.BgmApplier"), "CurrentCustomClip", null) as AudioClip;
            if (value == null || value.loadState != AudioDataLoadState.Loaded || value.samples <= 0 || value.frequency <= 0 || value.channels <= 0)
                throw new InvalidOperationException("Custom BGM native clip is not ready.");
            if (binding.CustomClip != null && (value.Pointer != binding.CustomClip.Pointer || Path.GetFullPath(path) != binding.CustomPath))
                throw new InvalidOperationException("Retained custom BGM clip/source identity changed.");
            clip = $"{binding.CustomHash}|{value.samples}|{value.frequency}|{value.channels}";
        }
        ValidateRenameCoverage(binding);
        string storePath = (string)Property(binding.Type("AzureArchive.MenuBgmSelector.ModPaths"), "StoreFile", null)!;
        return "bgm|" + kind + "|" + Field(current, "Id") + "|" + path + "|" + Field(current, "Display") + "|" + clip
            + "|lobby=" + Call(binding.Type("AzureArchive.MenuBgmSelector.GameLobbySetting"), "IsLobbyBgmEnabled", null)
            + "|store=" + FileHash(storePath) + "|config=no-rename:" + Static(binding.Type("AzureArchive.MenuBgmSelector.ModNameSwitch"), "_currentName");
    }
    private static string ReadMenu(Binding binding)
    {
        object? owner = Static(binding.Type("AAMenuPrototype.MenuEntryHooks"), "Owner");
        if (!ReferenceEquals(owner, binding.MenuOwner) || !Alive(owner) || owner is not MonoBehaviour component || !component.isActiveAndEnabled)
            throw new InvalidOperationException("The original menu controller was replaced, removed or disabled.");
        if (Field(owner!, "_done") is true) throw new InvalidOperationException("Original menu controller has faulted/stopped.");
        if (Static(binding.Type("AAMenuPrototype.MenuBehaviour"), "Probe") is true)
            throw new InvalidOperationException("Development menu probe cannot be admitted as ordinary playback.");
        return "menu|original-controller-active|probe=false";
    }
    private static string ReadSfx(Binding binding)
    {
        var data = binding.Type("AzureArchive.SfxFolderMod.SfxData");
        var patches = binding.Type("AzureArchive.SfxFolderMod.SfxPatches");
        if (Static(data, "_loaded") is not true || Static(patches, "_installed") is not true
            || Static(patches, "_h") is not Harmony original || original.Id != binding.Definition.Owner)
            throw new InvalidOperationException("Original Sfx map or patch set has not initialized.");
        if (Static(data, "_byKey") is not IDictionary map || map.Count == 0) throw new InvalidOperationException("Original Sfx map is empty.");
        var rows = new List<string>();
        foreach (DictionaryEntry entry in map)
            rows.Add(entry.Key + "|" + Field(entry.Value!, "Id") + "|" + Field(entry.Value!, "Cn"));
        rows.Sort(StringComparer.Ordinal);
        string directory = Static(data, "_dir") as string ?? throw new InvalidOperationException("Sfx configuration directory is uninitialized.");
        return "sfx|" + Static(data, "ShowId") + "|" + Hash(string.Join("\n", rows)) + "|" + FileHash(Path.Combine(directory, "config.txt"));
    }
    private static void ValidateRenameCoverage(Binding binding)
    {
        string path = Path.Combine(configRoot, "foxxlight.azurearchive.menubgmselector.cfg");
        if (renameAttemptObserved) throw new InvalidOperationException("An actual Menu BGM rename Apply was observed; restart is required for a fresh startup proof.");
        binding.StartupName ??= MenuStartupNameProof.ReadEarly(path);
        binding.BoundNameEntry ??= ResolveBoundNameEntry(binding, path);
        var current = MenuStartupNameProof.ReadCurrent(path);
        var names = binding.Type("AzureArchive.MenuBgmSelector.ModNameSwitch");
        MenuStartupNameProof.Validate(binding.StartupName, path, binding.Assembly.Location,
            (string?)Static(names, "_srcDir") ?? "", (string?)Static(names, "_modsRoot") ?? "", (string?)Static(names, "_version") ?? "",
            (string?)Static(names, "_currentName") ?? "", (string?)Property(binding.BoundNameEntry.GetType(), "BoxedValue", binding.BoundNameEntry) ?? "",
            current.Found, current.Name, DateTime.MinValue);
    }
    private static string FileHash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static void InstallFailed(double now, string reason)
    {
        bindingsReady = false; FailureReason = reason; Status = "unavailable";
        if (installSchedule.Failed(now, reason)) log?.Invoke("Retained menu boundary: " + reason);
    }
    private static string InstallState()
    {
        string path = Path.Combine(configRoot, "foxxlight.azurearchive.menubgmselector.cfg");
        var info = new FileInfo(path); var stamp = (info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        if (configStamp != stamp) { configName = MenuStartupNameProof.ReadCurrent(path); configStamp = stamp; }
        var modules = AppDomain.CurrentDomain.GetAssemblies().Select(a=>(Assembly:a,Name:AssemblySimpleNameCache.Get(a)))
            .Where(item=>Definitions.Any(d=>d.Name==item.Name))
            .Select(item=>item.Name+":"+System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Assembly)).OrderBy(x=>x,StringComparer.Ordinal);
        return string.Join("|", modules) + "|name=" + configName.Found + ":" + configName.Name + "|rename=" + renameAttemptObserved;
    }
    private static object ResolveBoundNameEntry(Binding binding, string expectedPath)
    {
        var chainAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a=>AssemblySimpleNameCache.Get(a)=="BepInEx.Unity.IL2CPP");
        var chainType = chainAssembly.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader", true)!;
        var chain = Property(chainType, "Instance", null) ?? throw new InvalidOperationException("Native chainloader is not initialized.");
        var plugins = Property(chain.GetType(), "Plugins", chain) as IDictionary ?? throw new InvalidOperationException("Native loaded-plugin inventory is unavailable.");
        var info = plugins[binding.Definition.Owner] ?? throw new InvalidOperationException("Original menu plugin has no loaded instance.");
        var instance = Property(info.GetType(), "Instance", info) ?? throw new InvalidOperationException("Original menu Plugin.Load has not completed.");
        if (instance.GetType().Assembly != binding.Assembly) throw new InvalidOperationException("Loaded menu plugin instance belongs to another assembly.");
        var config = Property(instance.GetType(), "Config", instance) ?? throw new InvalidOperationException("Original menu ConfigFile is unavailable.");
        string actual = (string)Property(config.GetType(), "ConfigFilePath", config)!;
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Menu ConfigFile path is not the captured active profile.");
        var indexer = config.GetType().GetProperty("Item", new[]{typeof(string),typeof(string)}) ?? throw new MissingMemberException("ConfigFile string indexer is unavailable.");
        object entry = indexer.GetValue(config, new object[]{"Name","DisplayName"}) ?? throw new InvalidOperationException("Menu DisplayName has not been bound.");
        string runtimeName = (string?)Static(binding.Type("AzureArchive.MenuBgmSelector.ModNameSwitch"), "_currentName") ?? "";
        if (Property(entry.GetType(), "DefaultValue", entry) is not string defaultName || defaultName != runtimeName)
            throw new InvalidOperationException("Menu DisplayName entry default does not match the original install-name binding.");
        return entry;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Count(object? value) => value is ICollection collection ? collection.Count
        : value == null ? throw new InvalidOperationException("Required collection is missing.")
        : Convert.ToInt32(value.GetType().GetProperty("Count", Flags)?.GetValue(value) ?? throw new MissingMemberException(value.GetType().FullName, "Count"));
    private static bool Alive(object? value) => value is UnityEngine.Object native ? native != null : value != null;
    private static object? Static(Type type, string name) => type.GetField(name, Flags)?.GetValue(null) ??
        (type.GetField(name, Flags) == null ? throw new MissingFieldException(type.FullName, name) : null);
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ??
        (value.GetType().GetField(name, Flags) == null ? throw new MissingFieldException(value.GetType().FullName, name) : null);
    private static object? Property(Type type, string name, object? instance) => (type.GetProperty(name, Flags)
        ?? throw new MissingMemberException(type.FullName, name)).GetValue(instance);
    private static object? Call(Type type, string name, object? instance)
    {
        try { return (type.GetMethod(name, Flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, name)).Invoke(instance, null); }
        catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static MethodInfo Method(string name) => typeof(RetainedMenuPlaybackBoundary).GetMethod(name, Flags)!;
    private static Assembly[] Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => AssemblySimpleNameCache.Get(assembly) == name).ToArray();
    private static RetainedPlaybackProof RequiredProof() => ExportBuildProfile.DevelopmentAuditEnabled
        ? proof ?? throw new InvalidOperationException("Retained menu boundary is not initialized.")
        : throw new InvalidOperationException("Menu provenance proofs are available only in DevelopmentAudit.");
    private static void MainThread() { if (mainThread != 0 && Environment.CurrentManagedThreadId != mainThread) throw new InvalidOperationException("Retained menu binding requires Unity's main thread."); }
    private sealed record Definition(string Name, CompatibilityBuild Build, string Owner, string Marker);
    private sealed class BoundaryLease(IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            if (!IsHeld) { retained = null; foreach (var binding in Bindings.Values) binding.ClearRetained(); Status = "ready"; }
        }
    }
    private sealed class Binding(Definition definition, Assembly assembly)
    {
        internal readonly Definition Definition = definition;
        internal readonly Assembly Assembly = assembly;
        private readonly List<(string Callback, string Kind, string? TargetType, string? TargetName)> required = new();
        internal object? MenuOwner;
        internal MenuStartupConfigEvidence? StartupName;
        internal object? BoundNameEntry;
        internal AudioClip? CustomClip;
        internal string CustomPath = "", CustomHash = "";
        internal Type Type(string name) => Assembly.GetType(name, true)!;
        internal MethodInfo Method(string name)
        {
            int dot = name.LastIndexOf('.');
            var candidates = Type(name[..dot]).GetMethods(Flags | BindingFlags.DeclaredOnly).Where(method => method.Name == name[(dot + 1)..]).ToArray();
            if (candidates.Length != 1) throw new MissingMethodException("Inspected callback shape is ambiguous: " + name);
            return candidates[0];
        }
        internal void RequireOriginal(string callback, string kind, string? targetType, string? targetName) => required.Add((callback, kind, targetType, targetName));
        internal void ClearRetained() { CustomClip = null; CustomPath = CustomHash = ""; }
        internal string OriginalGraph()
        {
            var targets = new List<MethodBase>(); var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(target); if (info == null) continue;
                foreach (var (kind, patches) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers), ("ilmanipulator", info.ILManipulators) })
                foreach (var patch in patches)
                {
                    if (patch.PatchMethod.DeclaringType?.Assembly != Assembly) continue;
                    targets.Add(target);
                    if (patch.owner != Definition.Owner) throw new NotSupportedException("Original Mod callback has another owner: " + patch.owner);
                    string callback = patch.PatchMethod.DeclaringType.FullName + "." + patch.PatchMethod.Name;
                    foreach (var requirement in required)
                        if (callback == requirement.Callback && kind == requirement.Kind && target.DeclaringType is { } declaring && AssemblySimpleNameCache.Get(declaring.Assembly) == "Assembly-CSharp"
                            && (requirement.TargetType == null || target.DeclaringType?.Name == requirement.TargetType)
                            && (requirement.TargetName == null || target.Name == requirement.TargetName)) found.Add(callback);
                }
            }
            foreach (var requirement in required)
                if (!found.Contains(requirement.Callback)) throw new NotSupportedException("Original native Mod dispatch is missing: " + requirement.Callback);
            return RetainedPatchGuard.Graph(targets);
        }
    }
}
