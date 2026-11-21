using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// First positive controlled-Mod adapter. Translation and layout remain in the
// original Mod; AAVE owns its deferred callback schedule while exporting.
// This does not claim arbitrary-Mod sandboxing or authorize cache reuse.
internal static class MultilingualCaptureAdapter
{
    private const string ModName = "AAMultilingual";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly CompatibilityBuild Build = new(ModName, new(0, 0, 0, 0),
        new("9e577f42-6683-4079-a7a7-0860caaa5a74"),
        "B62F46FAD43082F1CD1DE2909B6DB55BB7ACA34C6A989F93D1C0289558B208B3");
    private static readonly string[] PausedCallers =
    {
        "SurfaceLifecyclePostfix", "SettingPanelLifecyclePostfix", "PopupLifecyclePostfix",
        "TranslateSystemKey", "LabelTextPostfix", "Tick", "RefreshAll", "RefreshTree",
        "RefreshSettingModManagerEntry", "ObserveLabel"
    };
    private static readonly MethodInfo Rewriter = typeof(MultilingualCaptureAdapter).GetMethod(nameof(RewritePausedReads), Flags)!;
    private static readonly MethodInfo ClockedPaused = typeof(MultilingualCaptureAdapter).GetMethod(nameof(ReadPaused), Flags)!;
    private static readonly MethodInfo DriverPrefix = typeof(MultilingualCaptureAdapter).GetMethod(nameof(AllowDriver), Flags)!;
    private static readonly MethodInfo TickPrefix = typeof(MultilingualCaptureAdapter).GetMethod(nameof(AllowTick), Flags)!;
    private static readonly List<MethodInfo> Targets = new();
    private static MethodInfo? pausedGetter, driverMethod, tickMethod;
    private static PropertyInfo? currentProperty;
    private static FieldInfo? pendingLabels, bindings, deferredRefresh, pendingSettings;
    private static Func<bool>? originalPaused;
    private static Func<string>? readLocale;
    private static Action? tick;
    private static Action<string>? refresh;
    private static object? plugin;
    private static Assembly? installedAssembly;
    private static Harmony? harmony;
    private static Action<string>? log;
    private static volatile bool pending;
    private static bool pumping, restorationFailed;
    private static int ownerThread;
    private static long lastFrame = -1;
    private static string frozenLocale = "";
    internal static bool Active { get; private set; }
    internal static bool IsReady => installedAssembly != null && !restorationFailed;
    internal static long PumpCount { get; private set; }
    internal static long TranslationReadCount { get; private set; }
    internal static string FailureReason { get; private set; } = "";

    internal static void Initialize(Harmony hooks, Action<string> logger)
    {
        if (harmony != null) throw new InvalidOperationException("Translation adapter already initialized.");
        harmony = hooks; log = logger; pending = true; ownerThread = Environment.CurrentManagedThreadId;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
    }
    private static void OnAssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    { if (args.LoadedAssembly.GetName().Name == ModName) pending = true; }

    internal static void TryInstall()
    {
        if (!pending || harmony == null || installedAssembly != null || restorationFailed || Environment.CurrentManagedThreadId != ownerThread) return;
        var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == ModName).ToArray();
        if (candidates.Length == 0) return;
        try
        {
            if (candidates.Length != 1) throw new NotSupportedException("Duplicate multilingual assemblies.");
            var assembly = candidates[0];
            Build.Validate(assembly);
            var pluginType = assembly.GetType(ModName + ".Plugin", true)!;
            var translator = assembly.GetType(ModName + ".SystemUiTranslator", true)!;
            var driver = assembly.GetType(ModName + ".LanguageDriver", true)!;
            BindAndPatch(pluginType, translator, driver);
        }
        catch (Exception error)
        {
            FailureReason = error.Message; pending = false;
            Unpatch();
            log?.Invoke("Controlled translation adapter unavailable: " + error.Message);
        }
    }

    // Production calls this only after exact assembly validation. Managed
    // fixtures share this binding/patch engine without pretending to be an
    // admitted AAMultilingual build or starting native UI.
    internal static bool BindAndPatch(Type pluginType, Type translator, Type driver)
    {
        if (harmony == null || installedAssembly != null || restorationFailed)
            throw new InvalidOperationException("Translation adapter cannot bind in this state.");
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Translation binding requires its initialization thread.");
        currentProperty = pluginType.GetProperty("Current", Flags)!;
        plugin = currentProperty.GetValue(null);
        if (plugin == null) return false;
        pausedGetter = pluginType.GetProperty("IsTranslationPaused", Flags)!.GetMethod!;
        if (pausedGetter.IsStatic || pausedGetter.ReturnType != typeof(bool)) throw new NotSupportedException("Pause getter signature changed.");
        originalPaused = (Func<bool>)pausedGetter.CreateDelegate(typeof(Func<bool>), plugin);
        readLocale = (Func<string>)pluginType.GetProperty("LocaleId", Flags)!.GetMethod!.CreateDelegate(typeof(Func<string>), plugin);
        tickMethod = translator.GetMethod("Tick", Flags, null, Type.EmptyTypes, null)!;
        driverMethod = driver.GetMethod("Update", Flags, null, Type.EmptyTypes, null)!;
        tick = (Action)tickMethod.CreateDelegate(typeof(Action));
        refresh = (Action<string>)translator.GetMethod("RefreshAll", Flags, null, new[] { typeof(string) }, null)!.CreateDelegate(typeof(Action<string>));
        pendingLabels = translator.GetField("Pending", Flags)!;
        bindings = translator.GetField("Bound", Flags)!;
        deferredRefresh = translator.GetField("_deferredRefresh", Flags)!;
        pendingSettings = translator.GetField("PendingSettings", Flags)!;
        if (pendingLabels == null || bindings == null) throw new MissingFieldException("Translation queue binding missing.");
        var methods = PausedCallers.Select(name => translator.GetMethods(Flags | BindingFlags.DeclaredOnly).Single(m => m.Name == name)).ToArray();
        foreach (var method in methods)
        {
            EnsureUnpatched(method);
            RewritePausedReads(PatchProcessor.GetOriginalInstructions(method, (ILGenerator?)null)).ToArray();
        }
        EnsureUnpatched(driverMethod);
        try
        {
            foreach (var method in methods)
            {
                Targets.Add(method);
                harmony.Patch(method, transpiler: new HarmonyMethod(Rewriter));
            }
            harmony.Patch(driverMethod, prefix: new HarmonyMethod(DriverPrefix));
            harmony.Patch(tickMethod, prefix: new HarmonyMethod(TickPrefix));
            installedAssembly = pluginType.Assembly;
            pending = false;
            if (ExportBuildProfile.DevelopmentAuditEnabled) ValidatePatches();
            log?.Invoke("AAMultilingual controlled translation installed: 10 pause-call sites; original text/font/layout retained; callback queue owned per capture frame.");
            return true;
        }
        catch { Unpatch(); throw; }
    }
    private static IEnumerable<CodeInstruction> RewritePausedReads(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToArray();
        var reads = code.Where(i => Equals(i.operand, pausedGetter)).ToArray();
        if (reads.Length != 1 || reads.Any(i => i.opcode != OpCodes.Call && i.opcode != OpCodes.Callvirt))
            throw new NotSupportedException("Translation pause call-site coverage changed.");
        reads[0].opcode = OpCodes.Call;
        reads[0].operand = ClockedPaused; // Original instance remains the first object argument.
        return code;
    }
    private static bool ReadPaused(object instance)
    {
        if (!ReferenceEquals(instance, plugin)) throw new InvalidOperationException("Translation plugin instance changed.");
        if (Active && Environment.CurrentManagedThreadId == ownerThread)
        {
            ValidateLocale();
            TranslationReadCount++;
            return false;
        }
        return originalPaused!();
    }
    private static bool AllowDriver() => !Active;
    private static bool AllowTick() => !Active || pumping && Environment.CurrentManagedThreadId == ownerThread;

    internal static string PlaybackSettingsIdentity()
    {
        TryInstall();
        return IsReady ? "controlled-translation-v1|" + readLocale!() : "controlled-translation-unavailable";
    }
    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>();
        if (assembly.GetName().Name != ModName) return false;
        try
        {
            TryInstall();
            if (assembly != installedAssembly || !IsReady) throw new NotSupportedException(FailureReason.Length == 0 ? "Translation adapter is not ready." : FailureReason);
            ValidatePatches();
            level = StrictCapabilityLevel.Observed;
            evidence = new[] { "MultilingualCaptureAdapter.frame-owned.v1", "exact-version-mvid-sha256", "original-translation-preserved", "cache-reuse-disabled" };
        }
        catch (Exception error) { evidence = new[] { "controlled-translation-unavailable", error.Message }; }
        return true;
    }

    internal static void BeginExport()
    {
        if (installedAssembly == null) return;
        if (Active) throw new InvalidOperationException("Translation pass already active.");
        RenderControlV1.EnsureMainThread();
        if (!RenderControlV1.IsRenderingOwned || !IsReady) throw new InvalidOperationException("Translation capture requires the render lease.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidatePatches();
        ownerThread = Environment.CurrentManagedThreadId; frozenLocale = readLocale!();
        lastFrame = -1; PumpCount = TranslationReadCount = 0;
        Active = true; pumping = true;
        try { refresh!(frozenLocale); RefreshBoundPanels(); }
        catch { Active = false; throw; }
        finally { pumping = false; }
    }
    internal static void ValidateActive()
    {
        if (!Active) return;
        RenderControlV1.EnsureMainThread();
        if (!RenderControlV1.IsRenderingOwned || !IsReady) throw new InvalidOperationException("Translation capture ownership was lost.");
        ValidateLocale();
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidatePatches();
    }
    private static void ValidateLocale()
    {
        if (!ReferenceEquals(currentProperty!.GetValue(null), plugin) || readLocale!() != frozenLocale)
            throw new ExportException("timeline_source_changed", "Language or translation plugin changed during export.");
    }
    internal static void Pump(long frame)
    {
        if (!Active) return;
        RenderControlV1.EnsureMainThread();
        ValidateLocale();
        if (frame < 0 || frame < lastFrame) throw new InvalidOperationException("Translation frame moved backwards.");
        if (frame == lastFrame && !HasPendingTranslation()) return;
        var panels = PendingPanels();
        bool fullRefresh = deferredRefresh?.GetValue(null) is true || PendingSettingCount() > 0;
        pumping = true;
        try
        {
            tick!();
            foreach (var panel in panels) if (panel != null) panel.Refresh();
            if (fullRefresh) RefreshBoundPanels();
            lastFrame = frame; PumpCount++;
        }
        finally { pumping = false; }
    }
    private static bool HasPendingTranslation()
    {
        if (pendingLabels?.GetValue(null) is IDictionary labels && labels.Count != 0) return true;
        if (deferredRefresh?.GetValue(null) is true) return true;
        return PendingSettingCount() > 0;
    }
    private static int PendingSettingCount()
    {
        object? settings = pendingSettings?.GetValue(null);
        return settings?.GetType().GetProperty("Count")?.GetValue(settings) is int count ? count : 0;
    }
    private static HashSet<UIPanel> PendingPanels()
    {
        var result = new HashSet<UIPanel>();
        if (pendingLabels?.GetValue(null) is IDictionary labels)
            foreach (var value in labels.Values)
                if (value is UILabel label && label != null && label.panel != null) result.Add(label.panel);
        return result;
    }
    private static void RefreshBoundPanels()
    {
        var panels = new HashSet<UIPanel>();
        if (bindings?.GetValue(null) is IDictionary bound)
            foreach (var value in bound.Values)
            {
                if (value?.GetType().GetField("Label", Flags)?.GetValue(value) is UILabel label && label != null && label.panel != null)
                    panels.Add(label.panel);
            }
        foreach (var panel in panels) panel.Refresh();
    }
    internal static void EndExport()
    {
        if (!Active) return;
        RenderControlV1.EnsureMainThread();
        Active = false; pumping = false; lastFrame = -1; frozenLocale = "";
        // Getter call-sites now delegate to the original pause reader. The
        // existing AfterRelease observer resumes ordinary UI translation.
    }

    private static void EnsureUnpatched(MethodInfo method)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (method.GetMethodBody() == null) throw new NotSupportedException("Translation target has no managed body.");
        var info = Harmony.GetPatchInfo(method);
        if (info != null && (info.Prefixes.Any() || info.Postfixes.Any() || info.Transpilers.Any() || info.Finalizers.Any() || info.ILManipulators.Any()))
            throw new NotSupportedException("Conflicting translation callback patch: " + method.Name);
    }
    private static void ValidatePatches()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        foreach (var method in Targets)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null || info.Transpilers.Count(p => p.PatchMethod == Rewriter && p.owner == harmony?.Id) != 1 ||
                info.Transpilers.Any(p => p.PatchMethod != Rewriter || p.owner != harmony?.Id) || info.Postfixes.Any() || info.Finalizers.Any() || info.ILManipulators.Any() ||
                info.Prefixes.Any(p => method != tickMethod || p.PatchMethod != TickPrefix || p.owner != harmony?.Id))
                throw new NotSupportedException("Controlled translation patch removed/changed: " + method.Name);
        }
        var driverInfo = Harmony.GetPatchInfo(driverMethod!);
        if (driverInfo == null || driverInfo.Prefixes.Count != 1 || driverInfo.Prefixes[0].PatchMethod != DriverPrefix || driverInfo.Prefixes[0].owner != harmony?.Id ||
            driverInfo.Postfixes.Any() || driverInfo.Transpilers.Any() || driverInfo.Finalizers.Any() || driverInfo.ILManipulators.Any() ||
            Harmony.GetPatchInfo(tickMethod!)?.Prefixes.Count(p => p.PatchMethod == TickPrefix && p.owner == harmony?.Id) != 1)
            throw new NotSupportedException("Controlled translation scheduling patch missing.");
    }
    private static bool Unpatch()
    {
        List<Exception>? errors = null;
        void Remove(MethodInfo target, MethodInfo patch, HarmonyPatchType kind)
        {
            if (harmony == null) return;
            try
            {
                harmony.Unpatch(target, kind, harmony.Id);
                var info = Harmony.GetPatchInfo(target);
                var remaining = kind == HarmonyPatchType.Transpiler ? info?.Transpilers : info?.Prefixes;
                if (remaining?.Any(p => p.owner == harmony.Id && p.PatchMethod == patch) == true)
                    throw new InvalidOperationException("Translation patch remains installed: " + target.Name);
            }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        foreach (var method in Targets) Remove(method, Rewriter, HarmonyPatchType.Transpiler);
        if (driverMethod != null) Remove(driverMethod, DriverPrefix, HarmonyPatchType.Prefix);
        if (tickMethod != null) Remove(tickMethod, TickPrefix, HarmonyPatchType.Prefix);
        installedAssembly = null;
        if (errors != null)
        {
            restorationFailed = true; FailureReason = string.Join("; ", errors.Select(error => error.Message));
            return false;
        }
        Targets.Clear();
        return true;
    }
    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { restorationFailed = true; FailureReason = error.Message; return false; }
        if (!Unpatch()) { log?.Invoke("Translation cleanup remains pending: " + FailureReason); return false; }
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
        harmony = null; log = null; pending = false;
        restorationFailed = false; FailureReason = "";
        driverMethod = tickMethod = pausedGetter = null;
        originalPaused = null; readLocale = null; tick = null; refresh = null; plugin = null;
        return true;
    }
}
