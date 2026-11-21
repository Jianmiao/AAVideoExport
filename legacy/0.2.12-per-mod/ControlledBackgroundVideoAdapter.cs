using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace AAVideoExport.Plugin;

// Shared by the native adapter and its file-only tests; no Unity calls.
internal static class ControlledBackgroundSourceResolver
{
    internal static string ResolveVideoDirectory(string configured, string gameRoot, string defaultDirectory)
    {
        string cleaned = configured.Replace("\"", "").Replace("'", "").Trim().TrimEnd('\\', '/');
        if (cleaned.Length == 0) return defaultDirectory;
        try
        {
            string path = Path.GetFullPath(Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(gameRoot, cleaned));
            // Original GetVideoDir falls back when CreateDirectory fails. Do
            // not create anything; recognize invalid/file ancestors directly.
            for (string? parent = path; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) return defaultDirectory;
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return defaultDirectory; }
    }
    internal static string? Resolve(string fullPath, string resourceName, string videoDirectory,
        IReadOnlyDictionary<string, string>? originalCache = null)
    {
        string basename = string.IsNullOrEmpty(fullPath) ? resourceName : Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrEmpty(basename)) return null;
        if (basename != Path.GetFileName(basename) || basename.Any(char.IsControl))
            throw new ExportException("background_source_invalid", "Background resource name is not a file basename.");
        if (originalCache != null && originalCache.TryGetValue(basename, out var cached) && File.Exists(cached))
            return Path.GetFullPath(cached);
        if (!string.IsNullOrEmpty(fullPath))
        {
            var near = FindInDirectory(Path.GetDirectoryName(fullPath), basename);
            if (near != null) return near;
        }
        var (baseRoot, category, projectName, currentRoot) = ProjectParts(fullPath);
        if (currentRoot != null)
        {
            var first = FindImports(new[] { currentRoot }, basename);
            if (first != null) return first;
            string paired = Path.Combine(baseRoot!, category == "projects" ? "saves" : "projects", projectName!);
            var second = FindImports(new[] { paired }, basename);
            if (second != null) return second;
            var others = new List<string>();
            foreach (string group in new[] { category!, category == "projects" ? "saves" : "projects" })
            {
                string parent = Path.Combine(baseRoot!, group);
                if (!Directory.Exists(parent)) continue;
                others.AddRange(Directory.GetDirectories(parent).Where(path =>
                    !SamePath(path, currentRoot) && !SamePath(path, paired)));
            }
            var fallback = FindImports(others, basename);
            if (fallback != null) return fallback;
        }
        if (string.IsNullOrWhiteSpace(videoDirectory) || !Directory.Exists(videoDirectory)) return null;
        if (!string.IsNullOrEmpty(projectName))
        {
            var current = FindInDirectory(Path.Combine(videoDirectory, projectName), basename);
            if (current != null) return current;
        }
        return Unique(Directory.GetDirectories(videoDirectory).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => FindInDirectory(path, basename)).Where(path => path != null).Cast<string>(), basename);
    }

    private static readonly string[] Extensions = { ".mp4", ".webm", ".mov", ".avi", ".mkv" };
    private static string? FindInDirectory(string? directory, string basename)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        foreach (string extension in Extensions)
        {
            string candidate = Path.Combine(directory, basename + extension);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
    private static string? FindImports(IEnumerable<string> roots, string basename)
    {
        var paths = new List<string>();
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string imports = Path.Combine(root, "bgs", ".imports");
            if (!Directory.Exists(imports)) continue;
            foreach (string folder in Directory.GetDirectories(imports).OrderBy(path => path, StringComparer.Ordinal))
            {
                string? candidate = FindInDirectory(folder, basename);
                if (candidate != null) paths.Add(candidate);
            }
        }
        return Unique(paths, basename);
    }
    private static string? Unique(IEnumerable<string> candidates, string basename)
    {
        string[] matches = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length > 1)
            throw new ExportException("background_source_ambiguous", "Multiple fallback videos match background '" + basename + "'. AAVE will not substitute one arbitrarily.");
        return matches.SingleOrDefault();
    }
    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static (string? BaseRoot, string? Category, string? ProjectName, string? CurrentRoot) ProjectParts(string fullPath)
    {
        string normalized = (fullPath ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (string category in new[] { "projects", "saves" })
        {
            string separator = Path.DirectorySeparatorChar.ToString();
            string marker = separator + category + separator;
            int at = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string after = normalized[(at + marker.Length)..];
            int slash = after.IndexOf(Path.DirectorySeparatorChar);
            if (slash <= 0) continue;
            string baseRoot = normalized[..at], name = after[..slash];
            return (baseRoot, category, name, Path.Combine(baseRoot, category, name));
        }
        return (null, null, null, null);
    }

    internal static void CopyTopDownToBottomUp(byte[] source, byte[] destination, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(destination);
        if (width <= 0 || height <= 0 || source.Length != checked(width * height * 4) || destination.Length != source.Length ||
            ReferenceEquals(source, destination)) throw new ArgumentException("RGBA row conversion requires separate exact-sized buffers.");
        int stride = checked(width * 4);
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(source, row * stride, destination, (height - row - 1) * stride, stride);
    }
}

internal sealed record ControlledBackgroundCommand(long Sequence, long Frame, string Operation,
    int Layer, string Resource, string SourceSha256, long SourceFrame);

// The pinned AA 101 interop getters marshal native one-byte Boolean results as
// four-byte WinBOOL values. The real paused decoder demonstrated isPlaying=true
// through that wrapper while this exact native function returned byte zero.
// Keep this correction local: do not replace Unity delegates or other Mods.
internal static class ControlledBackgroundNativeState
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, IntPtr> Functions = new(StringComparer.Ordinal);
    internal static bool IsPlaying(VideoPlayer player) => Read(player, "isPlaying");
    internal static bool IsPrepared(VideoPlayer player) => Read(player, "isPrepared");
    internal static bool IsPaused(VideoPlayer player) => Read(player, "isPaused");
    internal static bool CanSetTime(VideoPlayer player) => Read(player, "canSetTime");
    internal static bool IsLooping(VideoPlayer player) => Read(player, "isLooping");

    private static unsafe bool Read(VideoPlayer player, string state)
    {
        IntPtr native = Object.MarshalledUnityObject.MarshalNotNull(player);
        if (native == IntPtr.Zero) throw new InvalidOperationException("Background VideoPlayer has no native engine pointer.");
        if (!Functions.TryGetValue(state, out IntPtr function))
        {
            string getter = "get_" + state + "_Injected";
            function = IL2CPP.il2cpp_resolve_icall("UnityEngine.Video.VideoPlayer::" + getter);
            if (function == IntPtr.Zero) throw new MissingMethodException("Pinned background native getter is unavailable: " + getter);
            var field = typeof(VideoPlayer).GetField(getter + "DelegateField", Flags)
                ?? throw new MissingFieldException(typeof(VideoPlayer).FullName, getter + "DelegateField");
            if (field.GetValue(null) is not Delegate generated || Marshal.GetFunctionPointerForDelegate(generated) != function)
                throw new NotSupportedException("Background native getter differs from its exact generated binding: " + getter);
            Functions.Add(state, function);
        }
        byte result = ((delegate* unmanaged[Cdecl]<IntPtr, byte>)function)(native);
        return result switch { 0 => false, 1 => true,
            _ => throw new NotSupportedException("Background native getter returned an invalid Boolean byte: " + state) };
    }
}

/// <summary>
/// AAVE-owned video frames for one inspected Flandre background build. Original
/// Layer.Set/alpha/overlap behavior remains native. Only FLVideoOverlay's video
/// playback boundary is replaced during export. This is not an arbitrary Mod
/// sandbox. New sources are synchronously predecoded with bounded tool calls
/// while the capture thread (and hence virtual video frame) is held.
/// </summary>
internal static class ControlledBackgroundVideoAdapter
{
    private const string ModName = "FlandreScarletBackground";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly CompatibilityBuild Build = new(ModName, new(5, 2, 0, 0),
        new("544d35a8-fc4a-434b-9a5f-fb68fcc7564a"),
        "779484939C616E22E77DD925880DD3EC0E47ED28643CE333D51ACC335C3313B1");
    private static readonly MethodInfo SetPrefix = typeof(ControlledBackgroundVideoAdapter).GetMethod(nameof(BeforeLayerSet), Flags)!;
    private static readonly MethodInfo TickPrefix = typeof(ControlledBackgroundVideoAdapter).GetMethod(nameof(BeforeTick), Flags)!;
    private static readonly MethodInfo RootPrefix = typeof(ControlledBackgroundVideoAdapter).GetMethod(nameof(BeforeRootSeen), Flags)!;
    private static readonly Dictionary<IntPtr, LayerBinding> Layers = new();
    private static readonly Dictionary<string, Asset> Assets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Playback> Playbacks = new();
    private static readonly List<ControlledBackgroundCommand> Commands = new();
    private static readonly Dictionary<string, string> ResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private static Harmony? hooks;
    private static Action<string>? logger;
    private static Assembly? inspected;
    private static Type? overlayType, videoModuleType;
    private static MethodInfo? onLayerSet, tick, onNewRootSeen, getContext;
    private static FieldInfo? nativePlayerField;
    private static volatile bool pending;
    private static bool restorationFailed;
    private static bool replayingRootNotifications;
    private static Exception? fault;
    private static Func<long>? readFrame;
    private static int ownerThread, fps;
    private static string cacheRoot = "", ffmpeg = "", ffprobe = "", frozenSettings = "", frozenVideoRoot = "";
    private static long lastFrame = -1;
    private static Playback? current;
    private static NativeSnapshot? snapshot;
    internal static bool Active { get; private set; }
    internal static bool IsReady => inspected != null && !restorationFailed && !replayingRootNotifications && snapshot?.TakeoverFailed != true && (snapshot == null || RenderControlV1.IsRenderingOwned);
    internal static long FrameUploadCount { get; private set; }
    internal static string FailureReason { get; private set; } = "";
    internal static IReadOnlyList<ControlledBackgroundCommand> CommandTrace => Array.AsReadOnly(Commands.ToArray());
    // Optional external development observer. Ordinary execution installs no
    // sink and creates no command trace; native tests collect their own data.
    internal static Action<ControlledBackgroundCommand>? CommandObserved { get; set; }
    private static long observedCommandSequence;

    internal static void Initialize(Harmony harmony, Action<string> log)
    {
        if (hooks != null) throw new InvalidOperationException("Background video adapter is already initialized.");
        hooks = harmony ?? throw new ArgumentNullException(nameof(harmony));
        logger = log ?? throw new ArgumentNullException(nameof(log));
        pending = true;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        RenderControlV1.BeforeAcquire += RejectPendingNativeRestoration;
        RenderControlV1.AfterRelease += RestoreDeferredNativeState;
    }
    private static void OnAssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    { if (args.LoadedAssembly.GetName().Name == ModName) pending = true; }

    internal static void TryInstall()
    {
        if (snapshot != null && RenderControlV1.IsRenderingOwned)
        { if (!snapshot.TakeoverFailed) snapshot.ValidateQuarantine(); }
        else RestoreDeferredNativeState();
        if (!pending || hooks == null || inspected != null || restorationFailed) return;
        var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == ModName).ToArray();
        if (candidates.Length == 0) return;
        pending = false;
        try
        {
            if (candidates.Length != 1) throw new NotSupportedException("Multiple background Mod assemblies are loaded.");
            var assembly = candidates[0];
            Build.Validate(assembly);
            overlayType = assembly.GetType(ModName + ".Background.FLVideoOverlay", true)!;
            videoModuleType = assembly.GetType(ModName + ".Background.FLVideoModule", true)!;
            onLayerSet = overlayType.GetMethod("OnLayerSet", Flags, null,
                new[] { typeof(BackgroundPresenter.Layer), typeof(BackgroundPresenter.Content) }, null);
            tick = overlayType.GetMethod("Tick", Flags, null, new[] { typeof(BackgroundPresenter) }, null);
            onNewRootSeen = overlayType.GetMethod("OnNewRootSeen", Flags, null, new[] { typeof(string) }, null);
            getContext = overlayType.GetMethod("GetContext", Flags, null, new[] { typeof(string) }, null);
            nativePlayerField = overlayType.GetField("_vp", Flags);
            if (onLayerSet == null || tick == null || onNewRootSeen == null || getContext == null || nativePlayerField?.FieldType != typeof(VideoPlayer))
                throw new NotSupportedException("Background callback or native player signature changed.");
            EnsurePatch(onLayerSet, null); EnsurePatch(tick, null); EnsurePatch(onNewRootSeen, null); ValidateContextMethod();
            hooks.Patch(onLayerSet, prefix: new HarmonyMethod(SetPrefix));
            hooks.Patch(tick, prefix: new HarmonyMethod(TickPrefix));
            hooks.Patch(onNewRootSeen, prefix: new HarmonyMethod(RootPrefix));
            EnsurePatch(onLayerSet, SetPrefix); EnsurePatch(tick, TickPrefix); EnsurePatch(onNewRootSeen, RootPrefix);
            inspected = assembly; FailureReason = "";
            logger?.Invoke("Flandre background video controlled boundary installed; original layer transitions retained; source PTS frames replace native autonomous playback during capture.");
        }
        catch (Exception error)
        {
            FailureReason = error.Message;
            Unpatch();
            logger?.Invoke("Controlled background video adapter unavailable: " + error.Message);
        }
    }

    internal static bool TryClassify(Assembly assembly, out StrictCapabilityLevel level, out IReadOnlyList<string> evidence)
    {
        level = StrictCapabilityLevel.Rejected; evidence = Array.Empty<string>();
        if (assembly.GetName().Name != ModName) return false;
        try
        {
            TryInstall();
            if (!IsReady || inspected != assembly) throw new NotSupportedException(FailureReason.Length == 0 ? "Background adapter is not installed." : FailureReason);
            ValidatePatches();
            level = StrictCapabilityLevel.Observed;
            evidence = new[] { "ControlledBackgroundVideoAdapter.pts-frames.v1", "exact-version-mvid-sha256",
                "source-decoded-frame-store", "original-layer-alpha-retained", "scoped-root-reset", "cache-reuse-disabled" };
        }
        catch (Exception error) { evidence = new[] { "controlled-background-unavailable", error.Message }; }
        return true;
    }

    internal static string PlaybackSettingsIdentity()
    {
        TryInstall();
        return IsReady ? "controlled-background-v1|" + ReadSettings() : "controlled-background-unavailable";
    }

    internal static void BeginExport(Func<long> frameReader, int framesPerSecond, string decodedCacheRoot, string ffmpegPath, string ffprobePath)
    {
        if (inspected == null) return;
        RenderControlV1.EnsureMainThread();
        if (!RenderControlV1.IsRenderingOwned || !IsReady) throw new InvalidOperationException("Background takeover requires active render ownership.");
        if (Active) throw new InvalidOperationException("Background capture pass is already active.");
        ArgumentNullException.ThrowIfNull(frameReader);
        if (framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (string.IsNullOrWhiteSpace(decodedCacheRoot) || string.IsNullOrWhiteSpace(ffmpegPath) || string.IsNullOrWhiteSpace(ffprobePath))
            throw new ArgumentException("Explicit background decode cache and encoder paths are required.");
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidatePatches();
        ownerThread = Environment.CurrentManagedThreadId; fps = framesPerSecond;
        readFrame = frameReader; cacheRoot = Path.GetFullPath(decodedCacheRoot);
        ffmpeg = ffmpegPath; ffprobe = ffprobePath;
        frozenSettings = ReadSettings(); frozenVideoRoot = ReadVideoRoot();
        fault = null; current = null; lastFrame = -1; FrameUploadCount = 0;
        Commands.Clear(); observedCommandSequence = 0; ResolutionCache.Clear(); Layers.Clear(); Playbacks.Clear(); Assets.Clear();
        snapshot ??= NativeSnapshot.Capture(overlayType!, nativePlayerField!);
        SnapshotOriginalCache();
        try
        {
            snapshot.Pause();
            Active = true;
            // A story may have assigned its opening background before the
            // first scenario advance enters AAVE. Adopt those real layers
            // before output frame zero instead of waiting for a later switch.
            foreach (var presenter in Object.FindObjectsOfType<BackgroundPresenter>())
            {
                if (presenter == null) continue;
                ObserveLayer(presenter.main); ObserveLayer(presenter.overlap);
            }
        }
        catch
        {
            try { if (Active) EndExport(); else RestoreDeferredNativeState(); }
            catch { restorationFailed = true; }
            readFrame = null;
            throw;
        }
    }

    private static bool BeforeLayerSet(BackgroundPresenter.Layer layer, BackgroundPresenter.Content content)
    {
        if (!Active) return snapshot == null;
        try { ApplyLayer(layer, content); }
        catch (Exception error) { Latch(error); }
        return false;
    }
    private static bool BeforeTick(BackgroundPresenter presenter)
    {
        if (!Active) return snapshot == null;
        try
        {
            RequireActive();
            if (presenter != null)
            {
                ObserveLayer(presenter.main);
                ObserveLayer(presenter.overlap);
            }
            Pump(readFrame!());
        }
        catch (Exception error) { Latch(error); }
        return false;
    }
    private static bool BeforeRootSeen(string root)
    {
        var retained = snapshot;
        if (retained == null) return true;
        try
        {
            RenderControlV1.EnsureMainThread();
            if (string.IsNullOrEmpty(root)) return false;
            string? context = getContext!.Invoke(null, new object[] { root.TrimEnd('\\', '/') }) as string;
            if (string.IsNullOrEmpty(context)) return false;
            if (RenderControlV1.IsRenderingOwned) retained.OnOwnedRoot(context);
            else retained.DeferUnownedRoot(root, context);
        }
        catch (Exception error)
        {
            // The original import prefix catches callback exceptions. Retain
            // the failure so the next controlled Pump cannot silently proceed.
            Latch(error.GetBaseException());
        }
        return false;
    }
    private static void Latch(Exception error)
    {
        // The original Mod's native postfix catches exceptions. Retain the
        // failure for AAVE Pump/ValidateActive so no static substitute is
        // silently accepted as a completed frame.
        if (fault != null) return;
        fault = error;
        FailureReason = error.Message;
        logger?.Invoke("Controlled background frame failed: " + error.Message);
    }
    internal static void ValidateActive()
    {
        if (!Active) return;
        RequireActive();
        if (ExportBuildProfile.DevelopmentAuditEnabled) ValidatePatches();
    }
    private static void RequireActive()
    {
        if (!Active || ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Background video callback is outside its owner thread.");
        if (fault != null) throw new ExportException("background_frame_failed", "Controlled background frame could not be delivered.", fault);
        if (!RenderControlV1.IsRenderingOwned || !IsReady) throw new InvalidOperationException("Background capture ownership was lost.");
        if (ReadSettings() != frozenSettings) throw new ExportException("timeline_source_changed", "Background video settings changed during export.");
        snapshot?.ValidateQuarantine();
    }
    private static void ApplyLayer(BackgroundPresenter.Layer? layer, BackgroundPresenter.Content? content)
    {
        RequireActive();
        if (layer == null || layer.Image == null) return;
        long frame = readFrame!();
        ValidateFrame(frame);
        var image = layer.Image;
        IntPtr key = layer.Pointer;
        if (!Layers.TryGetValue(key, out var binding))
        {
            binding = new LayerBinding(Layers.Count, layer, image, image.mainTexture);
            Layers.Add(key, binding);
        }
        else RefreshLayerImage(binding);
        binding.Content = content;
        if (content?.Image != null) binding.NativeTexture = content.Image;
        else if (!IsOwnedTexture(image.mainTexture)) binding.NativeTexture = image.mainTexture;
        if (content?.Resource == null)
        {
            // Native layers own transition alpha and may temporarily have no
            // resource. Do not reassert another layer's video onto that surface.
            RestoreLayer(binding);
            binding.Playback = null;
            Record(frame, "Transition", binding, "", "", -1);
            return;
        }
        string name = content.Resource.Name ?? "";
        string fullPath = content.Resource.FullPath ?? "";
        string? videoPath = ControlledBackgroundSourceResolver.Resolve(fullPath, name, frozenVideoRoot, ResolutionCache);
        if (videoPath == null)
        {
            RestoreLayer(binding); binding.Playback = null; current = null;
            Record(frame, "Static", binding, name, "", -1);
            return;
        }
        Asset asset = GetAsset(videoPath);
        string basename = string.IsNullOrEmpty(fullPath) ? name : Path.GetFileNameWithoutExtension(fullPath);
        ResolutionCache[basename] = videoPath;
        string resource = name;
        // A rebinding outgoing layer must keep its own playhead even when a
        // newer overlapping layer has become the most recent resource.
        var reusable = binding.Playback != null && binding.Playback.Resource == resource &&
            ReferenceEquals(binding.Playback.Asset, asset) ? binding.Playback :
            current != null && current.Resource == resource && ReferenceEquals(current.Asset, asset) ? current : null;
        bool rebind = reusable != null;
        if (!rebind)
        {
            current = new Playback(resource, asset, frame);
            Playbacks.Add(current);
        }
        binding.Playback = reusable ?? current;
        Upload(binding.Playback!, frame);
        BindTexture(binding);
        Record(frame, rebind ? "Rebind" : "Start", binding, resource, asset.Store.SourceSha256, binding.Playback!.SourceFrame);
    }
    private static void ObserveLayer(BackgroundPresenter.Layer? layer)
    {
        if (layer == null || layer.Image == null) return;
        if (!Layers.TryGetValue(layer.Pointer, out var binding))
        {
            ApplyLayer(layer, layer.Content);
            return;
        }
        RefreshLayerImage(binding);
        if (!SameContent(binding.Content, layer.Content)) { ApplyLayer(layer, layer.Content); return; }
        if (binding.Playback != null) BindTexture(binding);
    }
    private static bool SameContent(BackgroundPresenter.Content? left, BackgroundPresenter.Content? right) =>
        left == null ? right == null : right != null && left.Pointer == right.Pointer;

    internal static void Pump(long frame)
    {
        if (!Active) return;
        RequireActive(); ValidateFrame(frame);
        foreach (var binding in Layers.Values)
        {
            RefreshLayerImage(binding);
            if (binding.Image == null || binding.Playback == null) continue;
            Upload(binding.Playback, frame);
            BindTexture(binding);
        }
        lastFrame = frame;
    }
    private static void ValidateFrame(long frame)
    {
        if (frame < 0 || frame < lastFrame) throw new ExportException("background_frame_order", "Controlled background frame moved backwards.");
    }
    private static Asset GetAsset(string source)
    {
        string path = Path.GetFullPath(source);
        if (Assets.TryGetValue(path, out var asset)) return asset;
        // No Unity state or global media time is advanced while waiting for this
        // finite decode. Async preparation can replace this first slice later.
        var store = ControlledVideoFrameStore.PrepareAsync(path, ffmpeg, ffprobe, cacheRoot,
            processTimeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        try
        {
            var readLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using var sha = SHA256.Create();
                string hash = Convert.ToHexString(sha.ComputeHash(readLock));
                readLock.Position = 0;
                if (hash != store.SourceSha256) throw new ExportException("video_source_changed", "Background changed after predecode.");
                asset = new Asset(store, readLock);
                Assets.Add(path, asset);
                return asset;
            }
            catch { readLock.Dispose(); throw; }
        }
        catch { store.Dispose(); throw; }
    }
    private static unsafe void Upload(Playback playback, long frame)
    {
        long relative = checked(frame - playback.StartFrame);
        if (relative < 0) throw new ExportException("background_frame_order", "Background started after the requested output frame.");
        var chosen = playback.Asset.Store.SelectFrame(relative, fps, loop: true);
        if (playback.Texture != null && playback.SourceFrame == chosen.Index) return;
        if (playback.Texture == null)
        {
            playback.Texture = new Texture2D(playback.Asset.Store.Width, playback.Asset.Store.Height, TextureFormat.RGBA32, false);
            playback.Texture.name = "AAVE controlled background " + Playbacks.IndexOf(playback);
            playback.Texture.wrapMode = TextureWrapMode.Clamp;
            playback.Texture.filterMode = FilterMode.Bilinear;
        }
        playback.Asset.Store.CopyFrameForOutput(relative, fps, true, playback.SourcePixels);
        ControlledBackgroundSourceResolver.CopyTopDownToBottomUp(playback.SourcePixels, playback.UploadPixels,
            playback.Asset.Store.Width, playback.Asset.Store.Height);
        fixed (byte* pixels = playback.UploadPixels)
            playback.Texture.LoadRawTextureData((IntPtr)pixels, playback.UploadPixels.Length);
        playback.Texture.Apply(false, false);
        playback.SourceFrame = chosen.Index;
        FrameUploadCount++;
    }
    private static void BindTexture(LayerBinding binding)
    {
        if (binding.Image == null || binding.Playback?.Texture == null) return;
        if (binding.Image.mainTexture != binding.Playback.Texture)
        {
            binding.Image.mainTexture = binding.Playback.Texture;
            binding.Image.MarkAsChanged();
            if (binding.Image.panel != null) binding.Image.panel.Refresh();
        }
    }
    private static void RefreshLayerImage(LayerBinding binding)
    {
        var image = binding.Layer.Image;
        if (binding.Image == image) return;
        // A native layer/content can survive UITexture recreation. Release the
        // old image before transferring the playhead to the replacement.
        RestoreLayer(binding);
        binding.Image = image;
        binding.NativeTexture = image == null ? null :
            binding.Content?.Image != null ? binding.Content.Image :
            !IsOwnedTexture(image.mainTexture) ? image.mainTexture : null;
    }
    private static bool IsOwnedTexture(Texture? texture) => texture != null && Playbacks.Any(p => p.Texture != null && p.Texture == texture);
    private static void RestoreLayer(LayerBinding binding)
    {
        if (binding.Image == null || !IsOwnedTexture(binding.Image.mainTexture)) return;
        binding.Image.mainTexture = binding.NativeTexture;
        binding.Image.MarkAsChanged();
        if (binding.Image.panel != null) binding.Image.panel.Refresh();
    }
    private static void Record(long frame, string operation, LayerBinding binding, string resource, string hash, long sourceFrame)
    {
        var observer = CommandObserved;
        if (!ExportBuildProfile.DevelopmentAuditEnabled && observer == null) return;
        var item = new ControlledBackgroundCommand(observedCommandSequence++, frame, operation, binding.Ordinal, resource, hash, sourceFrame);
        if (ExportBuildProfile.DevelopmentAuditEnabled) Commands.Add(item);
        observer?.Invoke(item);
    }

    internal static void EndExport()
    {
        if (!Active) return;
        RenderControlV1.EnsureMainThread();
        List<Exception>? errors = null;
        void Restore(Action action)
        {
            try { action(); } catch (Exception error) { (errors ??= new()).Add(error); }
        }
        foreach (var binding in Layers.Values) Restore(() => RestoreLayer(binding));
        foreach (var playback in Playbacks)
            Restore(() => { if (playback.Texture != null) Object.Destroy(playback.Texture); playback.Texture = null; });
        foreach (var asset in Assets.Values) Restore(asset.Dispose);
        Active = false; current = null; readFrame = null; lastFrame = -1;
        // Keep the original native decoder quarantined across preparation,
        // catalog reload and replay. Its original state resumes only after the
        // outer render lease (including the settings panel) is released.
        if (!RenderControlV1.IsRenderingOwned) Restore(RestoreDeferredNativeState);
        Layers.Clear(); Playbacks.Clear(); Assets.Clear(); ResolutionCache.Clear();
        if (errors != null)
        {
            restorationFailed = true; FailureReason = "Controlled background restoration failed.";
            RenderControlV1.RestorationFailed();
            throw new AggregateException(FailureReason, errors);
        }
    }
    private static void RejectPendingNativeRestoration()
    {
        if (snapshot != null || replayingRootNotifications)
            throw new InvalidOperationException("Native background restoration remains pending; wait for its prepared playhead before another export.");
    }
    private static void RestoreDeferredNativeState()
    {
        if (snapshot == null || RenderControlV1.IsRenderingOwned) return;
        var retained = snapshot;
        try
        {
            retained.Restore();
            retained.PollRestoration();
            if (retained.RestorationComplete && ReferenceEquals(snapshot, retained)) snapshot = null;
        }
        catch (Exception error)
        {
            Exception failure = error;
            try { retained.RemoveRestoreObservers(); }
            catch (Exception cleanup) { failure = new AggregateException(error, cleanup); }
            restorationFailed = true;
            FailureReason = "Controlled background native restoration failed: " + failure.Message;
            RenderControlV1.RestorationFailed();
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }
    private static void SnapshotOriginalCache()
    {
        if (snapshot?.ScopeCacheInvalidated == true) return;
        if (overlayType?.GetField("_basenameCache", Flags)?.GetValue(null) is IDictionary cache)
            foreach (DictionaryEntry item in cache)
                if (item.Key is string key && item.Value is string value) ResolutionCache[key] = value;
    }
    private static string ReadSettings()
    {
        if (videoModuleType == null) return "absent";
        object? entry = videoModuleType.GetField("CfgVideoDir", Flags)?.GetValue(null);
        return Convert.ToString(entry?.GetType().GetProperty("Value")?.GetValue(entry), CultureInfo.InvariantCulture) ?? "";
    }
    private static string ReadVideoRoot()
    {
        // The original Mod caches the resolved video root. Respect that live
        // root, but do not invoke GetVideoDir because it creates directories.
        if (overlayType?.GetField("_videoDirResolved", Flags)?.GetValue(null) is true)
            return overlayType.GetField("_videoDirCache", Flags)?.GetValue(null) as string ?? "";
        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultDirectory = Path.Combine(homeDirectory, "AppData", "LocalLow", "foxxlight", "AzureArchive", "VideoBackup");
        return ControlledBackgroundSourceResolver.ResolveVideoDirectory(ReadSettings(), BepInEx.Paths.GameRootPath, defaultDirectory);
    }
    private static void ValidatePatches()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (inspected == null || restorationFailed) throw new NotSupportedException("Controlled background adapter is unavailable.");
        EnsurePatch(onLayerSet!, SetPrefix); EnsurePatch(tick!, TickPrefix); EnsurePatch(onNewRootSeen!, RootPrefix); ValidateContextMethod();
        EnsureOriginalHook(typeof(BackgroundPresenter.Layer), "Set", "Patch_LayerSet");
        EnsureOriginalHook(typeof(BackgroundPresenter), "LateUpdate", "Patch_PresenterLateUpdate");
        EnsureOriginalHook(typeof(ScenarioResourceManager), "LoadLocalResourceOverrides", "Patch_LoadLocalResourceOverrides", prefix: true);
        if (videoModuleType?.GetField("CfgVideoDir", Flags)?.GetValue(null) == null)
            throw new NotSupportedException("Background Mod runtime configuration has not initialized.");
    }
    private static void EnsureOriginalHook(Type hostType, string methodName, string patchType, bool prefix = false)
    {
        var target = hostType.GetMethod(methodName, Flags);
        var info = target == null ? null : Harmony.GetPatchInfo(target);
        if (info == null || !(prefix ? info.Prefixes : info.Postfixes).Any(p => p.PatchMethod.DeclaringType?.Assembly == inspected &&
            p.PatchMethod.DeclaringType?.FullName == ModName + ".Background." + patchType))
            throw new NotSupportedException("Original native background dispatch hook is missing: " + hostType.Name + "." + methodName);
    }
    private static void ValidateContextMethod()
    {
        if (getContext == null || !getContext.IsStatic || getContext.ReturnType != typeof(string))
            throw new NotSupportedException("Original background context parser changed.");
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (getContext.GetMethodBody() == null) throw new NotSupportedException("Original background context parser has no inspectable body.");
        var patch = Harmony.GetPatchInfo(getContext);
        if (patch != null && (patch.Prefixes.Any() || patch.Postfixes.Any() || patch.Transpilers.Any() || patch.Finalizers.Any() || patch.ILManipulators.Any()))
            throw new NotSupportedException("Original background context parser is patched.");
    }
    private static void EnsurePatch(MethodInfo method, MethodInfo? expected)
    {
        if (!method.IsStatic || method.ReturnType != typeof(void))
            throw new NotSupportedException("Controlled background callback signature changed.");
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (method.GetMethodBody() == null) throw new NotSupportedException("Background callback has no inspectable body.");
        var patch = Harmony.GetPatchInfo(method);
        if (expected == null)
        {
            if (patch != null && (patch.Prefixes.Any() || patch.Postfixes.Any() || patch.Transpilers.Any() || patch.Finalizers.Any() || patch.ILManipulators.Any()))
                throw new NotSupportedException("Background callback is already patched.");
            return;
        }
        if (patch == null || patch.Prefixes.Count != 1 || patch.Prefixes[0].PatchMethod != expected || patch.Prefixes[0].owner != hooks?.Id ||
            patch.Postfixes.Any() || patch.Transpilers.Any() || patch.Finalizers.Any() || patch.ILManipulators.Any())
            throw new NotSupportedException("Controlled background callback patch changed.");
    }
    private static bool Unpatch()
    {
        List<Exception>? failures = null;
        if (hooks != null)
        {
            foreach (var (target, prefix) in new[] { (onLayerSet, SetPrefix), (tick, TickPrefix), (onNewRootSeen, RootPrefix) })
            {
                if (target == null) continue;
                try
                {
                    hooks.Unpatch(target, HarmonyPatchType.Prefix, hooks.Id);
                    if (Harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.owner == hooks.Id && p.PatchMethod == prefix) == true)
                        throw new InvalidOperationException("Controlled background prefix remains installed: " + target.Name);
                }
                catch (Exception error) { (failures ??= new()).Add(error); }
            }
        }
        inspected = null;
        if (failures != null)
        {
            restorationFailed = true;
            FailureReason = "Controlled background unpatch remains pending: " + string.Join("; ", failures.Select(error => error.Message));
            return false;
        }
        return true;
    }
    internal static bool Shutdown()
    {
        try
        {
            EndExport();
            // The host may still hold a faulted outer lease because another
            // restoration failed. Do not restart its quarantined decoder.
            if (snapshot != null && RenderControlV1.IsRenderingOwned)
                throw new InvalidOperationException("Controlled background native restoration is waiting for the outer render lease.");
            RestoreDeferredNativeState();
            if (snapshot != null || replayingRootNotifications)
            {
                FailureReason = "Controlled background native restoration is still awaiting native completion.";
                return false;
            }
        }
        catch (Exception error)
        {
            restorationFailed = true; FailureReason = error.Message;
            logger?.Invoke("Controlled background cleanup remains pending: " + error.Message);
            return false;
        }
        if (!Unpatch()) { logger?.Invoke(FailureReason); return false; }
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
        RenderControlV1.BeforeAcquire -= RejectPendingNativeRestoration;
        RenderControlV1.AfterRelease -= RestoreDeferredNativeState;
        hooks = null; logger = null; pending = false;
        onLayerSet = tick = onNewRootSeen = getContext = null; inspected = null;
        restorationFailed = false; FailureReason = "";
        return true;
    }

    private sealed class Asset : IDisposable
    {
        internal readonly ControlledVideoFrameStore Store;
        private readonly FileStream sourceLock;
        internal Asset(ControlledVideoFrameStore store, FileStream sourceLock) { Store = store; this.sourceLock = sourceLock; }
        public void Dispose() { try { Store.Dispose(); } finally { sourceLock.Dispose(); } }
    }
    private sealed class Playback
    {
        internal readonly string Resource;
        internal readonly Asset Asset;
        internal readonly long StartFrame;
        internal readonly byte[] SourcePixels, UploadPixels;
        internal Texture2D? Texture;
        internal long SourceFrame = -1;
        internal Playback(string resource, Asset asset, long startFrame)
        {
            Resource = resource; Asset = asset; StartFrame = startFrame;
            SourcePixels = new byte[asset.Store.FrameByteCount]; UploadPixels = new byte[SourcePixels.Length];
        }
    }
    private sealed class LayerBinding
    {
        internal readonly int Ordinal;
        internal readonly BackgroundPresenter.Layer Layer;
        internal UITexture? Image;
        internal Texture? NativeTexture;
        internal BackgroundPresenter.Content? Content;
        internal Playback? Playback;
        internal LayerBinding(int ordinal, BackgroundPresenter.Layer layer, UITexture image, Texture? nativeTexture)
        { Ordinal = ordinal; Layer = layer; Image = image; NativeTexture = nativeTexture; }
    }
    private sealed class NativeSnapshot
    {
        private static readonly string[] ScalarFields =
        {
            "_currentContext", "_currentImage", "_currentImageLayerId", "_currentVideoName", "_officialTex",
            "_reassertCount", "_switchTime", "_healthStage", "_altPathTried", "_currentUrl", "_altPath",
            "_currentVideoPath", "_awaitFirstFrame", "_awaitFirstFrameSince", "_loggedFirstFrame", "_firstFrameTimedOut"
        };
        private readonly List<(FieldInfo Field, object? Value)> values = new();
        private readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Type owner;
        private readonly bool wasPlaying;
        private readonly bool wasPaused;
        private readonly bool wasAwaitingFirstFrame;
        private readonly bool wasPrepared;
        private readonly bool holderWasActive;
        private double playerTime;
        private readonly float wallStart;
        private readonly string playerUrl = "";
        private long playerFrame;
        private readonly bool preservePreparedDecoder;
        private readonly bool playerLooping;
        private readonly float playerSpeed;
        private readonly VideoSource playerSource;
        private readonly IntPtr playerTarget;
        private readonly int captureThread;
        private RestoreStage restoreStage;
        private float restoreStarted;
        private Exception? restoreError;
        private VideoPlayer.EventHandler? preparedObserver, seekObserver;
        private VideoPlayer.ErrorEventHandler? errorObserver;
        private bool preparedSubscribed, seekSubscribed, errorSubscribed;
        private bool seekCompletedObserved;
        private bool quarantineEstablished;
        private Exception? takeoverFailure;
        private string? scopeContext, deferredRootContext;
        private readonly List<string> unownedRootNotifications = new();
        private Exception? rootBoundaryFailure;
        internal VideoPlayer? Player { get; }
        internal bool RestorationComplete => restoreStage == RestoreStage.Complete;
        internal bool TakeoverFailed => takeoverFailure != null;
        internal bool ScopeCacheInvalidated { get; private set; }
        private enum RestoreStage { Quarantined, Preparing, Seeking, Resuming, Complete, Failed }
        private NativeSnapshot(Type owner, FieldInfo playerField)
        {
            this.owner = owner;
            scopeContext = deferredRootContext = owner.GetField("_currentContext", Flags)?.GetValue(null) as string;
            captureThread = Environment.CurrentManagedThreadId;
            Player = playerField.GetValue(null) as VideoPlayer;
            wasAwaitingFirstFrame = owner.GetField("_awaitFirstFrame", Flags)?.GetValue(null) is true;
            wallStart = Time.realtimeSinceStartup;
            if (Player != null)
            {
                wasPlaying = ControlledBackgroundNativeState.IsPlaying(Player);
                wasPrepared = ControlledBackgroundNativeState.IsPrepared(Player);
                wasPaused = ControlledBackgroundNativeState.IsPaused(Player);
                playerTime = wasPrepared ? Player.time : 0;
                playerFrame = wasPrepared ? Player.frame : -1;
                playerUrl = Player.url;
                holderWasActive = Player.gameObject.activeSelf;
                playerLooping = ControlledBackgroundNativeState.IsLooping(Player);
                playerSpeed = Player.playbackSpeed;
                playerSource = Player.source;
                playerTarget = Player.targetTexture?.Pointer ?? IntPtr.Zero;
                preservePreparedDecoder = wasPrepared && !wasAwaitingFirstFrame && holderWasActive &&
                    playerFrame >= 0 && double.IsFinite(playerTime) && playerTime >= 0;
            }
            foreach (string name in ScalarFields)
            {
                var field = owner.GetField(name, Flags) ?? throw new MissingFieldException(owner.FullName, name);
                values.Add((field, field.GetValue(null)));
            }
            if (owner.GetField("_basenameCache", Flags)?.GetValue(null) is IDictionary original)
                foreach (DictionaryEntry item in original)
                    if (item.Key is string key && item.Value is string value) cache[key] = value;
        }
        internal static NativeSnapshot Capture(Type owner, FieldInfo player) => new(owner, player);
        internal void Pause()
        {
            if (Player == null) return;
            if (takeoverFailure != null) throw new ExportException("background_takeover_failed", "Native background takeover failed and is awaiting outer release.", takeoverFailure);
            if (quarantineEstablished) { ValidateQuarantine(); return; }
            try
            {
                if (preservePreparedDecoder)
                {
                    // Preserve the original mode, but linearize the frozen
                    // position after native Pause. A playing decoder can
                    // legitimately advance while scalar/cache fields are read.
                    if (wasPlaying) Player.Pause();
                    if (ControlledBackgroundNativeState.IsPlaying(Player) || !ControlledBackgroundNativeState.IsPrepared(Player))
                        throw new InvalidOperationException("Native background did not reach a prepared paused state.");
                    long before = Player.frame;
                    double time = Player.time;
                    long after = Player.frame;
                    if (before != after || after < 0 || !double.IsFinite(time) || time < 0)
                        throw new InvalidOperationException("Native background did not publish a coherent frozen playhead.");
                    playerFrame = after; playerTime = time;
                }
                else
                {
                    // An unprepared start cannot be paused reliably: Stop
                    // cancels the pending Prepare/Play and the holder is kept
                    // inactive through every pass and outer lease boundary.
                    Player.Stop();
                    Player.gameObject.SetActive(false);
                }
                ValidateQuarantine();
                quarantineEstablished = true;
            }
            catch (Exception error)
            {
                // The snapshot remains retained. Rollback of the original
                // playback mode is deferred to AfterRelease; do not resume a
                // native decoder while the outer render lease is still held.
                takeoverFailure = error;
                throw;
            }
        }
        internal void ValidateQuarantine()
        {
            if (rootBoundaryFailure != null) throw new ExportException("background_root_boundary", "Background root notifications exceeded their controlled boundary.", rootBoundaryFailure);
            if (Player == null) return;
            ValidatePlayerIdentity();
            if (ControlledBackgroundNativeState.IsPlaying(Player))
                throw new ExportException("background_native_playback", "An unowned native background player resumed while capture owns rendering.");
            if (preservePreparedDecoder)
            {
                if (!ControlledBackgroundNativeState.IsPrepared(Player) || Player.frame != playerFrame ||
                    !double.IsFinite(Player.time) || Math.Abs(Player.time - playerTime) > .0001 ||
                    ControlledBackgroundNativeState.IsPaused(Player) != (wasPlaying || wasPaused) ||
                    Player.gameObject.activeSelf != holderWasActive)
                    throw new ExportException("background_native_state_changed", $"Paused native background drifted from its captured state: saved={playerTime:R}/{playerFrame}, actual={Player.time:R}/{Player.frame}.");
            }
            else if (Player.gameObject.activeSelf || ControlledBackgroundNativeState.IsPrepared(Player))
                throw new ExportException("background_native_state_changed", "Stopped native background decoder escaped its inactive, unprepared quarantine.");
        }
        internal void Restore()
        {
            if (restoreStage != RestoreStage.Quarantined) return;
            RenderControlV1.EnsureMainThread();
            if (RenderControlV1.IsRenderingOwned) throw new InvalidOperationException("Native background restoration cannot start while rendering remains owned.");
            if (rootBoundaryFailure != null) throw rootBoundaryFailure;
            if (!TakeoverFailed) ValidateQuarantine();
            restoreStarted = Time.realtimeSinceStartup;
            restoreStage = RestoreStage.Preparing;
            if (Player != null) Player.gameObject.SetActive(holderWasActive);
            float elapsed = Time.realtimeSinceStartup - wallStart;
            foreach (var entry in values)
            {
                object? value = entry.Value;
                if (entry.Field.Name is "_switchTime" or "_awaitFirstFrameSince" && value is float timestamp)
                    value = timestamp + elapsed;
                entry.Field.SetValue(null, value);
            }
            if (owner.GetField("_basenameCache", Flags)?.GetValue(null) is IDictionary original)
            {
                original.Clear();
                foreach (var pair in cache) original.Add(pair.Key, pair.Value);
            }
            if (Player == null || !wasPlaying && !wasAwaitingFirstFrame && !wasPrepared)
            { CompleteRestoration(); return; }
            ValidatePlayerIdentity();
            if (preservePreparedDecoder)
            {
                // No seek or preparation is necessary: the same actual
                // displayed frame survived every owned update and release.
                ResumeCapturedPlayback();
                return;
            }
            // SetActive can enqueue playOnAwake. Cancel that start before
            // preparing and seeking so restoration cannot briefly run at zero.
            Player.Stop();
            preparedObserver = (Action<VideoPlayer>)OnRestorePrepared;
            seekObserver = (Action<VideoPlayer>)OnRestoreSeek;
            errorObserver = (Action<VideoPlayer, string>)OnRestoreError;
            // These generated properties access actual callback fields. The
            // add_/remove_ event accessors are unsupported unstripping stubs in
            // AA 101. Combine only our observers; keep foreign delegates intact.
            Player.prepareCompleted += preparedObserver; preparedSubscribed = true;
            Player.seekCompleted += seekObserver; seekSubscribed = true;
            Player.errorReceived += errorObserver; errorSubscribed = true;
            Player.Prepare();
        }

        internal void PollRestoration()
        {
            if (restoreStage is RestoreStage.Quarantined or RestoreStage.Complete) return;
            RenderControlV1.EnsureMainThread();
            if (rootBoundaryFailure != null) throw rootBoundaryFailure;
            if (restoreError != null) throw new ExportException("background_native_restore", "Native background restoration callback failed.", restoreError);
            if (Time.realtimeSinceStartup - restoreStarted > 15)
                throw new TimeoutException($"Native background restoration did not publish its captured playhead within 15 seconds: stage={restoreStage}, seekCompleted={seekCompletedObserved}, saved={playerTime:R}/{playerFrame}, actual={Player?.time:R}/{Player?.frame}.");
            ValidatePlayerIdentity();
            if (restoreStage == RestoreStage.Seeking)
            {
                if (!seekCompletedObserved || !ControlledBackgroundNativeState.IsPrepared(Player!)) return;
                double actual = Player!.time;
                double tolerance = Player.frameRate > 0 ? 1d / Player.frameRate + .0001 : .0001;
                if (!double.IsFinite(actual) || Math.Abs(actual - playerTime) > tolerance ||
                    playerTime > 0 && actual <= 0 || playerFrame >= 0 && Player.frame != playerFrame) return;
                ResumeCapturedPlayback();
                return;
            }
            if (restoreStage != RestoreStage.Resuming) return;
            if (!ControlledBackgroundNativeState.IsPrepared(Player!)) return;
            bool playing = ControlledBackgroundNativeState.IsPlaying(Player!);
            bool paused = ControlledBackgroundNativeState.IsPaused(Player!);
            if (wasPlaying || wasAwaitingFirstFrame)
            { if (!playing) return; }
            else if (playing || paused != wasPaused) return;
            CompleteRestoration();
        }

        private void OnRestorePrepared(VideoPlayer player) => ObserveRestoreCallback(player, () =>
        {
            if (restoreStage != RestoreStage.Preparing) return;
            if (!ControlledBackgroundNativeState.IsPrepared(player))
                throw new InvalidOperationException("Native background prepareCompleted arrived before the decoder was prepared.");
            if (wasPrepared && playerTime > 0)
            {
                // A time assignment made immediately after Prepare was lost
                // by the actual native decoder (run 42 seek completed at zero).
                // Issue the seek only from native preparation. A first-frame
                // Pause must follow the requested time: pausing at zero first
                // can leave the actual decoder at frame -1 after seekCompleted.
                if (!wasPlaying && !wasPaused && !wasAwaitingFirstFrame && ControlledBackgroundNativeState.IsPlaying(player))
                    throw new InvalidOperationException("A prepared-idle background unexpectedly started during restoration.");
                if (!ControlledBackgroundNativeState.CanSetTime(player))
                    throw new NotSupportedException("Native background decoder cannot seek to its captured nonzero playhead.");
                seekCompletedObserved = false;
                restoreStage = RestoreStage.Seeking;
                player.time = playerTime;
                if (wasPlaying || wasPaused || wasAwaitingFirstFrame) player.Pause();
            }
            else ResumeCapturedPlayback();
        });

        private void OnRestoreSeek(VideoPlayer player) => ObserveRestoreCallback(player, () =>
        {
            if (restoreStage != RestoreStage.Seeking) return;
            // Unity raises this before it publishes the decoded frame/time.
            // Run 45 and Unity's documented seek ordering both show zero/-1
            // here. The main-thread poll still requires the actual saved
            // nonzero playhead before it permits Play or completes restoration.
            seekCompletedObserved = true;
        });

        private void ResumeCapturedPlayback()
        {
            restoreStage = RestoreStage.Resuming;
            if (wasPlaying || wasAwaitingFirstFrame) Player!.Play();
            else if (wasPaused && !preservePreparedDecoder) Player!.Pause();
            PollRestoration();
        }

        private void OnRestoreError(VideoPlayer player, string message) => ObserveRestoreCallback(player,
            () => throw new InvalidOperationException("Native background decoder error while restoring: " + message));

        private void ObserveRestoreCallback(VideoPlayer player, Action action)
        {
            try
            {
                if (Player == null || player.Pointer != Player.Pointer) return;
                if (Environment.CurrentManagedThreadId != captureThread)
                    throw new InvalidOperationException("Native background restoration callback arrived off the captured main thread.");
                ValidatePlayerIdentity();
                action();
            }
            catch (Exception error)
            {
                restoreError ??= error;
                restoreStage = RestoreStage.Failed;
                // Do not throw across Unity's native callback dispatcher.
                // TryInstall polls this retained fault on the host main thread.
            }
        }

        private void ValidatePlayerIdentity()
        {
            if (Player == null || owner.GetField("_vp", Flags)?.GetValue(null) is not VideoPlayer original || original.Pointer != Player.Pointer ||
                Player.url != playerUrl || Player.source != playerSource || Player.playbackSpeed != playerSpeed ||
                ControlledBackgroundNativeState.IsLooping(Player) != playerLooping || (Player.targetTexture?.Pointer ?? IntPtr.Zero) != playerTarget)
                throw new InvalidOperationException("Original native background player, source, loop, speed or render target changed.");
        }

        private void CompleteRestoration()
        {
            RemoveRestoreObservers();
            restoreStage = RestoreStage.Complete;
            if (!ReferenceEquals(snapshot, this)) return;
            snapshot = null;
            // These notifications arrived after ownership ended. Restore the
            // saved decoder first, then honor the real root changes through
            // the original method, in order, with no retained-snapshot prefix.
            replayingRootNotifications = true;
            try
            {
                foreach (string root in unownedRootNotifications)
                    onNewRootSeen!.Invoke(null, new object[] { root });
                unownedRootNotifications.Clear();
            }
            catch (Exception error)
            {
                snapshot = this; restoreStage = RestoreStage.Failed; restoreError = error.GetBaseException(); throw;
            }
            finally { replayingRootNotifications = false; }
        }

        internal void OnOwnedRoot(string context)
        {
            if (string.Equals(scopeContext, context, StringComparison.Ordinal)) return;
            scopeContext = context; ScopeCacheInvalidated = true;
            ResolutionCache.Clear();
            if (Active) throw new ExportException("timeline_source_changed", "Background root context changed during an active capture pass.");
            // The original native context/cache belongs to the retained
            // decoder. Internal export scene changes reset only this scope's
            // lookup state; BeginExport must never re-import the old cache.
        }

        internal void DeferUnownedRoot(string root, string context)
        {
            if (string.Equals(deferredRootContext, context, StringComparison.Ordinal)) return;
            if (unownedRootNotifications.Count >= 32)
            {
                rootBoundaryFailure = new ExportException("background_root_overflow", "Too many native background root changes arrived during restoration.");
                throw rootBoundaryFailure;
            }
            unownedRootNotifications.Add(root); deferredRootContext = context;
        }

        internal void RemoveRestoreObservers()
        {
            if (Player == null) return;
            List<Exception>? errors = null;
            void Remove(Action action)
            { try { action(); } catch (Exception error) { (errors ??= new()).Add(error); } }
            if (preparedSubscribed) Remove(() => { Player.prepareCompleted -= preparedObserver!; preparedSubscribed = false; });
            if (seekSubscribed) Remove(() => { Player.seekCompleted -= seekObserver!; seekSubscribed = false; });
            if (errorSubscribed) Remove(() => { Player.errorReceived -= errorObserver!; errorSubscribed = false; });
            if (errors != null) throw new AggregateException("Native background restore observers could not be removed.", errors);
        }
    }
}
