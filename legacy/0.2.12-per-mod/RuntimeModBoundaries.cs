using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Globalization;
using System.Runtime.Loader;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Compatibility is scoped to the supplied builds and inactive editor
// surfaces. The Rukari main-thread queue continues pumping native resource work.
// No third-party configuration, binaries, or playback settings are rewritten.
internal static class RuntimeModBoundaries
{
    private static readonly CompatibilityBuild[] Builds =
    {
        new("Rukari.Lib", new(0,2,0,0), new("5b0332b5-b75b-4006-aa10-6eca36b0800f"), "7413B9534A309F7B2498CA709E3BB1769FFEFECB1FBBF5F55953AE5D6C22FD45"),
        new("Rukari.Lib.Runtime", new(0,4,6,0), new("18766066-a6b8-40a3-ab67-8c4c4b4e5242"), "BC8453F8E773A27CD9F88E18E82A45668DA2EF50D8C1EDC6C24EF05F201D8A41"),
        new("AzureArchive.VideoTools.Core", new(1,0,0,0), new("dd2f9da9-3d15-4b9f-8880-b0a759c28de4"), "0117A9630160739C37559AB8FB8B2A72B5429AD1EC10C8CE0EDF235C8A751C54"),
        new("AzureArchive.VideoTools.Formats", new(1,0,0,0), new("6dc5a22c-9842-4298-8139-33a7429a19e8"), "CBE35722F4DE707FA87560D2AA597B0016E95AC96F1018D0DE108D3E9D78375A")
    };
    private static readonly HashSet<Assembly> Validated = new();
    private static readonly HashSet<MethodInfo> FrozenMethods = new();
    private static readonly MethodInfo Prefix = typeof(RuntimeModBoundaries).GetMethod(nameof(AllowSurfaceTick), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Harmony? _harmony;
    private static Action<string>? _log;
    private static bool _active;
    private static bool _restorationFailed;
    private static int _ownerThread;
    private static volatile bool _pending;
    private static Type? _themes;
    private static string _configRoot = "";
    private static EffectsSourceInput? _preparedEffectsSource;

    internal static void Initialize(Harmony harmony, Action<string> log, string configRoot = "")
    {
        if (_harmony != null) throw new InvalidOperationException("Runtime boundary is already initialized.");
        _configRoot = configRoot;
        _ownerThread = Environment.CurrentManagedThreadId;
        _harmony = harmony; _log = log; _pending = true;
        AppDomain.CurrentDomain.AssemblyLoad += Loaded;
        TryInstall();
    }
    private static void Loaded(object? sender, AssemblyLoadEventArgs args)
    { if (Builds.Any(build => build.Name == args.LoadedAssembly.GetName().Name)) _pending = true; }
    private static Assembly[] Assemblies(string name) => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == name).ToArray();
    private static bool AllowSurfaceTick() => !_active;

    // The current MoreEffects build loads its Formats dependency lazily. Resolve
    // that exact packaged image only at export preflight, before the retained
    // assembly inventory is captured. Ordinary startup does not force it in.
    internal static void PrepareExportDependencies()
    {
        if (_harmony == null || _restorationFailed || _active || Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Dependency preflight requires the idle main-thread boundary.");
        TryInstall();
        var cores = Assemblies("AzureArchive.VideoTools.Core");
        var formats = Assemblies("AzureArchive.VideoTools.Formats");
        if (cores.Length == 0 && formats.Length == 0) return;
        if (cores.Length != 1 || !Validated.Contains(cores[0]) || formats.Length > 1)
            throw new ExportException("strict_effects_dependency_unavailable", "The inspected VideoTools dependency owner is missing, duplicated or invalid.");
        if (formats.Length == 0)
        {
            var build = Builds.Single(b => b.Name == "AzureArchive.VideoTools.Formats");
            string path = Path.Combine(Path.GetDirectoryName(cores[0].Location)!, build.Name + ".dll");
            // Hold a read-only file handle across hashing and materialization;
            // no same-name resolver fallback or directory-wide trust is used.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (ExportBuildProfile.DevelopmentAuditEnabled)
            {
                using var sha = SHA256.Create();
                if (!string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), build.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ExportException("strict_effects_dependency_unavailable", "The packaged VideoTools Formats payload differs from its inspected image.");
            }
            var context = AssemblyLoadContext.GetLoadContext(cores[0])
                ?? throw new NotSupportedException("The inspected VideoTools owner has no load context.");
            var loaded = context.LoadFromAssemblyPath(path);
            build.Validate(loaded);
            _pending = true;
            TryInstall();
            formats = Assemblies(build.Name);
        }
        if (formats.Length != 1 || !Validated.Contains(formats[0]))
            throw new ExportException("strict_effects_dependency_unavailable", "VideoTools Formats is duplicated or did not pass exact build validation.");
    }

    internal static void TryInstall()
    {
        if (!_pending || _harmony == null || _restorationFailed || Environment.CurrentManagedThreadId != _ownerThread) return;
        _pending = false;
        foreach (var build in Builds)
        foreach (var assembly in Assemblies(build.Name))
        {
            try
            {
                if (Assemblies(build.Name).Length != 1) throw new NotSupportedException("同名程序集重复加载：" + build.Name);
                build.Validate(assembly); Validated.Add(assembly);
                if (build.Name == "Rukari.Lib.Runtime")
                {
                    _themes = assembly.GetType("Rukari.Lib.Runtime.Themes.StoryThemeHost", true)!;
                    if (ExportBuildProfile.DevelopmentAuditEnabled)
                    { Freeze(_themes, "Tick"); Freeze(_themes, "Draw"); }
                    Freeze(assembly.GetType("Rukari.Lib.Runtime.Tools.ToolsHost", true)!, "Tick");
                }
            }
            catch (Exception error) { Validated.Remove(assembly); _log?.Invoke("局部兼容边界未启用：" + build.Name + "：" + error.Message); }
        }
    }

    private static void Freeze(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, name);
        if (method.ReturnType != typeof(void)) throw new NotSupportedException("界面回调结构改变：" + type.FullName + "." + name);
        if (FrozenMethods.Contains(method)) { ValidatePatch(method); return; }
        if (ExportBuildProfile.DevelopmentAuditEnabled && Harmony.GetPatchInfo(method) is { } info && info.Owners.Count > 0) throw new NotSupportedException("界面回调已有其他补丁：" + type.FullName + "." + name);
        FrozenMethods.Add(method);
        try { _harmony!.Patch(method, prefix: new HarmonyMethod(Prefix)); ValidatePatch(method); }
        catch
        {
            try { RemoveOwnedPrefix(method); FrozenMethods.Remove(method); }
            catch { _restorationFailed = true; }
            throw;
        }
    }
    private static void ValidatePatch(MethodInfo method)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var info = Harmony.GetPatchInfo(method);
        if (info == null || info.Prefixes.Count != 1 || info.Prefixes[0].PatchMethod != Prefix || info.Prefixes[0].owner != _harmony?.Id
            || info.Postfixes.Count > 0 || info.Transpilers.Count > 0 || info.Finalizers.Count > 0 || info.ILManipulators.Count > 0)
            throw new NotSupportedException("局部兼容补丁发生变化：" + method.DeclaringType?.FullName + "." + method.Name);
    }

    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        string name = assembly.GetName().Name ?? "";
        level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>();
        if (!Builds.Any(build => build.Name == name)) return false;
        try
        {
            if (_restorationFailed || !Validated.Contains(assembly) || Assemblies(name).Length != 1) throw new NotSupportedException("该构建未完成局部兼容校验。");
            string boundary;
            if (name.StartsWith("Rukari.Lib", StringComparison.Ordinal)) { ValidateRukariIdle(); boundary = "RuntimeModBoundaries.rukari-idle.v1"; }
            else
            {
                ValidateEffectsDependencies(); boundary = "RuntimeModBoundaries.effects-dependency.v1";
            }
            level = StrictCapabilityLevel.Observed;
            evidence = new[] { boundary, "exact-version-mvid-sha256", "cache-reuse-disabled" };
        }
        catch (Exception error) { evidence = new[] { "scoped-boundary-unavailable", error.Message }; }
        return true;
    }

    private static object? Field(Type type, string name) =>
        (type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(type.FullName, name)).GetValue(null);

    internal static bool RequiresEffectsSourcePreparation() => ExportBuildProfile.DevelopmentAuditEnabled
        ? EffectsSourcePreparation.IsRequired() : EffectsSourceExecution.IsRequired();
    internal static EffectsSourceInput PrepareEffectsSourceInput(string selectedArchive, string expectedArchiveSha256)
    {
        RenderControlV1.EnsureMainThread();
        if (_active || _preparedEffectsSource != null) throw new InvalidOperationException("Effects input acquisition requires an idle unbound export.");
        ValidateEffectsDependencies();
        var input = ExportBuildProfile.DevelopmentAuditEnabled
            ? EffectsSourceInput.Acquire(new EffectsSourcePreparation(), selectedArchive, expectedArchiveSha256)
            : EffectsSourceInput.AcquireExecution(new EffectsSourceExecution(), selectedArchive);
        _preparedEffectsSource = input;
        return input;
    }
    internal static void RevalidateEffectsSourceInput(EffectsSourceInput input)
    {
        if (!ReferenceEquals(input, _preparedEffectsSource)) throw new InvalidOperationException("The held effects source input was replaced.");
        input.RevalidateForPass();
    }
    internal static void ReleaseEffectsSourceInput(EffectsSourceInput input)
    {
        input.Dispose();
        if (ReferenceEquals(input, _preparedEffectsSource)) _preparedEffectsSource = null;
    }
    internal static string PlaybackSettingsIdentity() => PlaybackSettingsIdentityCore(includeProject: true);
    internal static string PlaybackSettingsWithoutEffectsSource() => PlaybackSettingsIdentityCore(includeProject: false);
    private static string PlaybackSettingsIdentityCore(bool includeProject)
    {
        var values = new List<string>();
        if (ExportBuildProfile.DevelopmentAuditEnabled)
            foreach (string name in new[] { "rukari.charactervoice", "rukari.moreeffects", "rukari.lib.runtime" })
                if (_configRoot.Length > 0) values.Add(name + ":" + FileIdentity(Path.Combine(_configRoot, name + ".cfg")));
        var effects = Assemblies("Rukari.MoreEffects").SingleOrDefault();
        if (effects != null)
        {
            var runtime = effects.GetType("AzureArchive.VideoTools.Runtime.PlayerCommandObservationRuntime", true)!;
            object? options = Field(runtime, "_options");
            if (options != null)
            {
                foreach (var property in options.GetType().GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal))
                    values.Add(property.Name + "=" + Convert.ToString(property.GetValue(options), CultureInfo.InvariantCulture));
                string project = options.GetType().GetProperty("ProjectPath")!.GetValue(options) as string ?? "";
                var pairSource = effects.GetType("AzureArchive.VideoTools.Interop.ActiveProjectPairSource", true)!;
                if (pairSource.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public)!.GetValue(null) is true)
                {
                    object? pair = pairSource.GetMethod("ActivePairSnapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, null);
                    project = pair?.GetType().GetProperty("AapPath")?.GetValue(pair) as string ?? "";
                }
                if (includeProject)
                {
                    _preparedEffectsSource?.ValidateCurrent(requireActive: _active);
                    values.Add("effects-project=" + (ExportBuildProfile.DevelopmentAuditEnabled
                        ? _preparedEffectsSource?.ProjectSha256 ?? FileIdentity(project)
                        : _preparedEffectsSource?.ProjectPath ?? project));
                }
            }
        }
        return string.Join("|", values);
    }

    internal static string FileIdentity(string path)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
            throw new InvalidOperationException("Binary/config fingerprints are available only in DevelopmentAudit.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "absent";
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    internal static void ValidateRukariIdle()
    {
        foreach (string name in new[] { "Rukari.Lib", "Rukari.Lib.Runtime" })
        {
            var assemblies = Assemblies(name);
            if (assemblies.Length != 1 || !Validated.Contains(assemblies[0]))
                throw new ExportException("strict_rukari_boundary_unavailable", "RukariLib 共享库的版本或运行状态未通过检查。");
        }
        if (_themes == null) throw new NotSupportedException("共享库主题边界尚未安装。");
        object? service = Field(_themes, "_service");
        if (service != null && (service.GetType().GetProperty("ActiveThemeId")!.GetValue(service) != null
            || service.GetType().GetProperty("RegisteredThemes")!.GetValue(service) is IEnumerable themes && themes.Cast<object>().Any()))
            throw new ExportException("strict_rukari_theme_unsupported", "当前注册了额外剧情界面主题，尚无对应的逐帧适配；请使用原生剧情界面后重试。");
        foreach (var method in FrozenMethods.Where(method => method.DeclaringType?.Assembly == _themes.Assembly)) ValidatePatch(method);
    }

    internal static void BeginExport()
    {
        RenderControlV1.EnsureMainThread();
        if (_restorationFailed || !RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Runtime boundary requires a restored adapter and active render lease.");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            if (Assemblies("Rukari.Lib.Runtime").Length > 0) ValidateRukariIdle();
            if (HasEffectsDependencies()) ValidateEffectsDependencies();
        }
        _preparedEffectsSource?.ValidateCurrent(requireActive: true);
        _active = true;
    }
    internal static void ValidateActive()
    {
        if (!_active) return;
        RenderControlV1.EnsureMainThread();
        if (_restorationFailed || !RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Runtime boundary capture ownership was lost.");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            if (Assemblies("Rukari.Lib.Runtime").Length > 0) ValidateRukariIdle();
            if (HasEffectsDependencies()) ValidateEffectsDependencies();
        }
        _preparedEffectsSource?.ValidateCurrent(requireActive: true);
    }
    internal static void EndExport()
    {
        if (!_active) return;
        RenderControlV1.EnsureMainThread(); _active = false;
    }
    private static bool HasEffectsDependencies() => Assemblies("AzureArchive.VideoTools.Core").Length != 0
        || Assemblies("AzureArchive.VideoTools.Formats").Length != 0;
    private static void ValidateEffectsDependencies()
    {
        foreach (string name in new[] { "AzureArchive.VideoTools.Core", "AzureArchive.VideoTools.Formats" })
        {
            var assemblies = Assemblies(name);
            if (assemblies.Length != 1 || !Validated.Contains(assemblies[0]))
                throw new ExportException("strict_effects_dependency_unavailable", "VideoTools dependency closure is missing, duplicated or outside its inspected build.");
        }
        if (Assemblies("Rukari.MoreEffects").Length != 1 || !MoreEffectsCompatibility.FullClockCoverage)
            throw new ExportException("strict_effects_dependency_unavailable", "The loaded More Effects owner does not have full clock coverage.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidateRukariIdle();
    }
    private static void RemoveOwnedPrefix(MethodInfo method)
    {
        if (_harmony == null) return;
        _harmony.Unpatch(method, HarmonyPatchType.Prefix, _harmony.Id);
        if (Harmony.GetPatchInfo(method)?.Prefixes.Any(p => p.owner == _harmony.Id && p.PatchMethod == Prefix) == true)
            throw new InvalidOperationException("Runtime boundary prefix remains installed: " + method.Name);
    }
    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error) { _restorationFailed = true; _log?.Invoke(error.Message); return false; }
        Validated.Clear();
        List<Exception>? errors = null;
        foreach (var method in FrozenMethods.ToArray())
        {
            try { RemoveOwnedPrefix(method); FrozenMethods.Remove(method); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        if (errors != null)
        {
            _restorationFailed = true; _log?.Invoke("Runtime boundary cleanup remains pending: " + string.Join("; ", errors.Select(error => error.Message)));
            return false;
        }
        AppDomain.CurrentDomain.AssemblyLoad -= Loaded;
        _harmony = null; _log = null; _pending = false; _restorationFailed = false;
        return true;
    }
}
