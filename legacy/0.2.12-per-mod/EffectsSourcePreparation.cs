using System.Collections;
using System.Reflection;
using System.Text.Json;
using AAVideoExport.Core;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Calls the inspected Mod's original discovery/reader/projection services. It
// never guesses an AAP filename, writes its current pair, or executes a scene.
internal sealed class EffectsSourcePreparation : IEffectsSourceService
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly CompatibilityBuild EffectsBuild = new("Rukari.MoreEffects", new(1,6,7,0),
        new("7faa0394-68bb-4307-9736-80ceb3c87b57"), "473EEC0879D4724C6E5BB3DDAC711B78F14B242DEEE62A6BAD7E7FD5D10150ED");
    private static readonly CompatibilityBuild SharedDirectiveBuild = new("Rukari.Lib", new(0,2,0,0),
        new("5b0332b5-b75b-4006-aa10-6eca36b0800f"), "7413B9534A309F7B2498CA709E3BB1769FFEFECB1FBBF5F55953AE5D6C22FD45");
    private readonly Assembly effects, core, formats;
    private readonly Type pairs, bridge, identity, observation, runtime, continueRuntime;
    private readonly MethodInfo capture, resolve, activePair, scriptIdentity, reload, loadIndex;
    private delegate object ValidateAapRead(string path, JsonElement projection, ReadOnlySpan<byte> bytes, bool requireNative, long maximumBytes, int maximumJsonDepth);
    private readonly ValidateAapRead validateAap;
    private readonly int thread;
    private string projectionKey = "";
    private object? retainedIndex, retainedIndexTask;
    private string retainedOptions = "", retainedContinues = "";
    private int archiveRecordCount;

    internal static bool IsRequired()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == EffectsBuild.Name).ToArray();
        if (loaded.Length == 0) return false;
        if (loaded.Length != 1) throw Failure("The effects source owner is duplicated.");
        EffectsBuild.Validate(loaded[0]);
        var source = loaded[0].GetType("AzureArchive.VideoTools.Interop.ActiveProjectPairSource", true)!;
        return (bool)Property(source, "Enabled")!;
    }

    internal EffectsSourcePreparation()
    {
        effects = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == EffectsBuild.Name);
        EffectsBuild.Validate(effects); thread = Environment.CurrentManagedThreadId;
        core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "AzureArchive.VideoTools.Core");
        formats = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "AzureArchive.VideoTools.Formats");
        pairs = effects.GetType("AzureArchive.VideoTools.Interop.ActiveProjectPairSource", true)!;
        bridge = effects.GetType("AzureArchive.VideoTools.Interop.OfficialStoragePathBridge", true)!;
        runtime = effects.GetType("AzureArchive.VideoTools.Runtime.PlayerCommandObservationRuntime", true)!;
        continueRuntime = effects.GetType("AzureArchive.VideoTools.Runtime.ContinueDialogueRuntime", true)!;
        identity = core.GetType("AzureArchive.VideoTools.Core.Commands.CommandIdentity", true)!;
        observation = core.GetType("AzureArchive.VideoTools.Core.Mapping.ObservedCompiledSceneIdentity", true)!;
        capture = RequireMethod(bridge, "CaptureOnMainThread", typeof(bool));
        resolve = RequireMethod(pairs, "ResolveActive", observation);
        activePair = RequireMethod(pairs, "ActivePairSnapshot");
        scriptIdentity = RequireMethod(identity, "CompiledScript", typeof(string));
        reload = RequireMethod(runtime, "RequestIndexReload");
        loadIndex = RequireMethod(runtime, "LoadIndex", effects.GetType("AzureArchive.VideoTools.Runtime.PlayerCommandObservationOptions", true)!);
        RequireUnpatched(loadIndex);
        var compatibility = core.GetType("AzureArchive.VideoTools.Core.Projects.OfficialAapCompatibilityProjection", true)!;
        validateAap = (ValidateAapRead)RequireMethod(compatibility, "ValidateRead", typeof(string), typeof(JsonElement),
            typeof(ReadOnlySpan<byte>), typeof(bool), typeof(long), typeof(int)).CreateDelegate(typeof(ValidateAapRead));
        if (capture.ReturnType != typeof(bool) || reload.ReturnType != typeof(bool)
            || observation.GetConstructor(new[] { typeof(string), typeof(int), typeof(int) }) == null)
            throw Failure("The original effects discovery API shape changed.");
    }

    public void CaptureOfficialStorage()
    {
        MainThread();
        if (!(bool)Property(pairs, "Enabled")!) throw Failure("Original effects auto discovery was disabled after acquisition.");
        _ = Invoke(capture, true);
    }
    public EffectsArchiveInput ReadArchive(string path)
    {
        MainThread();
        var reader = Field(pairs, "_playbackReader") as Delegate ?? throw Failure("Original playback archive reader is unavailable.");
        ValidateCallback(reader);
        object snapshot = ResultValue(reader.DynamicInvoke(path), "original selected archive reader");
        object source = Property(snapshot, "Source")!;
        Delegate? projection;
        if (Field(pairs, "_projectionSnapshot") is Delegate snapshotReader)
        {
            ValidateCallback(snapshotReader);
            object projectionSnapshot = snapshotReader.DynamicInvoke() ?? throw Failure("Original identity projection snapshot is unavailable.");
            projection = Property(projectionSnapshot, "Project") as Delegate ?? throw Failure("Original identity projection callback is missing.");
            projectionKey = (string?)Property(projectionSnapshot, "CacheKey") ?? throw Failure("Original projection snapshot identity is missing.");
        }
        else projection = Field(pairs, "_compiledScriptProjection") as Delegate;
        if (projection != null) ValidateCallback(projection);
        if (Property(snapshot, "Records") is not IEnumerable records) throw Failure("Original playback archive has no ordered records.");
        var rows = new List<EffectsSourceRow>();
        foreach (object record in records)
        {
            int index = Convert.ToInt32(Property(record, "RecordIndex"));
            string script = (string?)Property(record, "CompiledScript") ?? throw Failure("Original playback record has no compiled script.");
            if (projection != null) script = projection.DynamicInvoke(script) as string ?? throw Failure("Original identity projection returned null.");
            rows.Add(new(index, script));
        }
        archiveRecordCount = rows.Count;
        return new((string)Property(source, "FullPath")!, (string)Property(source, "RevisionSha256")!,
            Convert.ToInt64(Property(source, "Length")), rows.AsReadOnly());
    }
    public EffectsSourceBinding? Resolve(EffectsSourceRow row)
    {
        MainThread();
        object script = Invoke(scriptIdentity, row.Script)!;
        object observed = Activator.CreateInstance(observation, Property(script, "Sha256"), Property(script, "Utf16Length"), Property(script, "LineCount"))!;
        observation.GetProperty("PlaybackRowIndex", Flags)!.SetValue(observed, row.Index);
        object result = Invoke(resolve, observed)!;
        if (Property(result, "Success") is not true || Property(result, "Value") is not { } binding) return null;
        object pair = Property(binding, "Pair")!, resolution = Property(binding, "Resolution")!;
        object projectSource = Property(Property(binding, "Project")!, "Source")!;
        object archiveSource = Property(Property(binding, "Playback")!, "Source")!;
        string project = (string)Property(pair, "AapPath")!, archive = (string)Property(pair, "AasPath")!;
        if (!SamePath(project, (string)Property(projectSource, "FullPath")!) || !SamePath(archive, (string)Property(archiveSource, "FullPath")!))
            throw Failure("Original pair paths differ from the actual reader snapshots.");
        string projectHash = (string)Property(projectSource, "RevisionSha256")!;
        var sourceBinding = new EffectsSourceBinding(project, archive, projectHash, (string)Property(archiveSource, "RevisionSha256")!,
            Convert.ToInt64(Property(projectSource, "Length")), Convert.ToInt64(Property(archiveSource, "Length")),
            Convert.ToInt64(Property(binding, "StorageRevision")), Convert.ToInt32(Property(resolution, "PlaybackRecordIndex") ?? -1),
            Property(resolution, "Status")!.ToString() == "Mapped", Property(resolution, "SelectedSceneTrusted") is true,
            Property(resolution, "ArchivePairFresh") is true, Property(resolution, "PairingTrusted") is true)
        { AdditionalInputs = CompatibilityInputs(project, projectHash) };
        return EffectsPlaybackProofBuilder.WithRequiredProof(sourceBinding, () => CapturePlaybackProof(sourceBinding, projectSource, archiveSource));
    }

    private EffectsPlaybackProof CapturePlaybackProof(EffectsSourceBinding binding, object projectSource, object archiveSource)
    {
        // Observe readiness before invoking the original read-only loader, so
        // host retries during a native index load do not repeatedly scan files.
        object liveIndex = RequireRuntimeReady(binding.StorageRevision, allowPending: true);
        object options = Field(runtime, "_options") ?? throw Failure("Original playback options are unavailable.");
        if (Property(options, "Stage")?.ToString() != "SceneCommandDispatch"
            || Property(options, "SceneCommandDispatchEnabled") is not true
            || Property(options, "EmbeddedEditorCommandsEnabled") is not true
            || Field(runtime, "_embeddedEditorCommandsRuntimeReady") is not true)
            throw Failure("This effective source proof covers the original enabled embedded scene-command playback branch only.");
        string optionsKey = CurrentOptionsIdentity(options);
        string projectKey = (string)Property(projectSource, "PathKey")!;
        string archiveKey = (string)Property(archiveSource, "PathKey")!;
        var inputs = binding.AdditionalInputs.ToList();
        var absent = new List<string>();
        CaptureOptionalMapping(binding.ProjectPath, inputs, absent);
        CaptureAbsentWorkspace(options, projectKey, absent);
        RequireUnpatched(loadIndex);
        object loaded = Invoke(loadIndex, options) ?? throw Failure("The original effective playback loader returned no result.");
        if (Property(loaded, "Success") is not true || Property(loaded, "Index") is not { } compiledIndex)
            throw Failure("Original effective playback input is not executable: " + Property(loaded, "Reason") + "; " + Property(loaded, "Diagnostic"));
        string workspace = (string?)Property(loaded, "WorkspaceSource") ?? "";
        var continues = Property(loaded, "ContinueIdentities") is IEnumerable values
            ? values.Cast<object>().Select(value => value as string ?? throw Failure("Original continuation identity has an invalid type.")).ToArray()
            : throw Failure("Original embedded playback omitted its continuation snapshot.");
        RequireUnpatched(compiledIndex.GetType().GetMethod("ObserveCommandBatchAtRow", Flags)!);
        RequireUnpatched(liveIndex.GetType().GetMethod("ObserveCommandBatchAtRow", Flags)!);
        string expected = EffectsPlaybackProofBuilder.IndexIdentity(compiledIndex, archiveKey, binding.ArchiveSha256, archiveRecordCount);
        if (EffectsPlaybackProofBuilder.IndexIdentity(liveIndex, archiveKey, binding.ArchiveSha256, archiveRecordCount) != expected)
            throw Failure("The original active playback index differs from its current exact-input proof.");
        string liveContinues = CaptureContinueIdentity();
        if (Property(options, "ContinueDialogueEnabled") is true && liveContinues != ContinueIdentity(continues))
            throw Failure("The original active continuation identities differ from the effective playback proof.");
        if (CurrentOptionsIdentity(options) != optionsKey || !ReferenceEquals(liveIndex, RequireRuntimeReady(binding.StorageRevision, allowPending: false)))
            throw Failure("The original playback state changed during effective-input proof acquisition.");
        var proof = EffectsPlaybackProofBuilder.Create(compiledIndex, archiveKey, binding.ArchiveSha256, archiveRecordCount,
            workspace, continues, optionsKey + "|live-continue=" + liveContinues, inputs, absent);
        retainedIndex = liveIndex;
        retainedIndexTask = Field(runtime, "_indexLoadTask");
        retainedOptions = optionsKey;
        retainedContinues = liveContinues;
        return proof;
    }

    private object RequireRuntimeReady(long revision, bool allowPending)
    {
        object? index = Field(runtime, "_index");
        var task = Field(runtime, "_indexLoadTask") as Task;
        bool consumed = Field(runtime, "_indexLoadConsumed") is true;
        bool reloadPending = Convert.ToInt32(Field(runtime, "_indexReloadRequested")) != 0;
        bool awaitingFirstLoad = index == null && task == null && Field(runtime, "_lastFailedIndexLoadSignature") == null;
        bool loading = reloadPending || task != null && (!task.IsCompleted || !consumed) || awaitingFirstLoad;
        if (loading)
        {
            if (allowPending) throw new ExportException("strict_effects_source_pending", "Waiting for the original effects playback index to finish its current load.");
            throw Failure("The retained original effects playback index is loading or was invalidated.");
        }
        if (index == null || task == null || !task.IsCompletedSuccessfully || !consumed
            || Convert.ToInt64(Field(runtime, "_indexStorageRevision")) != revision)
            throw Failure("The original playback index is unavailable for the selected storage revision: " + Field(runtime, "_lastFailedIndexLoadSignature"));
        return index;
    }

    private string CurrentOptionsIdentity(object options)
    {
        if (Field(pairs, "_projectionSnapshot") is not Delegate callback)
            throw Failure("The effective source proof requires the original immutable projection snapshot.");
        ValidateCallback(callback);
        object snapshot = callback.DynamicInvoke() ?? throw Failure("The immutable projection snapshot disappeared.");
        if (Property(snapshot, "Project") is not Delegate project) throw Failure("The immutable script projection disappeared.");
        ValidateCallback(project);
        string key = (string?)Property(snapshot, "CacheKey") ?? "";
        if (key.Length == 0 || key != projectionKey) throw Failure("The original script projection changed during source acquisition.");
        return EffectsPlaybackProofBuilder.OptionsIdentity(options, key, Field(runtime, "_embeddedEditorCommandsRuntimeReady") is true)
            + "|registry=" + Convert.ToString(Field(runtime, "_directiveRegistryRevision"), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void CaptureOptionalMapping(string project, List<EffectsSourceFile> inputs, List<string> absent)
    {
        var store = formats.GetType("AzureArchive.VideoTools.Formats.Sidecar.JsonCompilationMappingEvidenceStore", true)!;
        var pathMethod = RequireMethod(store, "GetPath", typeof(string)); RequireUnpatched(pathMethod);
        string path = (string)Invoke(pathMethod, project)!;
        if (!File.Exists(path)) { absent.Add(path); return; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > 67108864) throw Failure("Original compilation mapping input is outside its reader bounds.");
        using var sha = System.Security.Cryptography.SHA256.Create();
        inputs.Add(new(path, Convert.ToHexString(sha.ComputeHash(stream)), stream.Length));
    }

    private void CaptureAbsentWorkspace(object options, string projectKey, List<string> absent)
    {
        string workspaceRoot = (string?)Property(options, "WorkspaceRoot") ?? "";
        if (!Path.IsPathFullyQualified(workspaceRoot)) throw Failure("Original workspace root is not absolute.");
        Type store = formats.GetType("AzureArchive.VideoTools.Formats.Workspace.JsonModWorkspaceStore", true)!;
        object instance = Activator.CreateInstance(store, workspaceRoot, null, null)!;
        MethodInfo pathMethod = RequireMethod(store, "ResolveManifestPath", typeof(string)); RequireUnpatched(pathMethod);
        string path = (string)ResultValue(pathMethod.Invoke(instance, new object[] { projectKey }), "original workspace manifest path");
        MethodInfo backupMethod = RequireMethod(store, "GetBackupPath", typeof(string)); RequireUnpatched(backupMethod);
        string backup = (string)Invoke(backupMethod, path)!;
        foreach (string candidate in new[] { path, backup })
        {
            if (File.Exists(candidate) || Directory.Exists(candidate)) throw Failure("External command workspaces require a separately retained source proof.");
            absent.Add(candidate);
        }
    }

    private string CaptureContinueIdentity()
    {
        object gate = Field(continueRuntime, "Gate") ?? throw Failure("Original continuation lock is unavailable.");
        lock (gate)
        {
            if (Field(continueRuntime, "ContinueIdentities") is not IEnumerable values) throw Failure("Original continuation snapshot is unavailable.");
            return ContinueIdentity(values.Cast<object>().Select(value => value as string ?? throw Failure("Original continuation identity has an invalid type.")));
        }
    }
    private static string ContinueIdentity(IEnumerable<string> values) => string.Join("|", values.Select(value =>
        value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : throw Failure("Original continuation digest is malformed."))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
    private IReadOnlyList<EffectsSourceFile> CompatibilityInputs(string project, string projectHash)
    {
        const long maximumBytes = 67108864;
        using var stream = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumBytes) throw Failure("Original project projection is outside the inspected reader bounds.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); byte[] content = bytes.ToArray();
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)), projectHash, StringComparison.OrdinalIgnoreCase))
            throw Failure("Original project bytes differ from the resolved reader snapshot.");
        using var json = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw Failure("Original project projection has no object root.");
        object compatibility = ResultValue(validateAap(project, json.RootElement, content, false, maximumBytes, 128), "original AAP compatibility validation");
        if (!SamePath(project, (string)Property(compatibility, "ProjectPath")!)) throw Failure("Original compatibility proof belongs to another project.");
        string nativePath = (string?)Property(compatibility, "NativeProjectPath") ?? "";
        if (nativePath.Length == 0) return Array.Empty<EffectsSourceFile>();
        if (!string.Equals((string?)Property(compatibility, "ProjectionRevisionSha256"), projectHash, StringComparison.OrdinalIgnoreCase))
            throw Failure("Original compatibility projection hash differs from the bound project.");
        // This path comes only from the original validated compatibility
        // binding. Never derive it by replacing the selected file extension.
        string nativeHash = (string?)Property(compatibility, "NativeRevisionSha256") ?? throw Failure("Original native-project revision is missing.");
        return new[] { new EffectsSourceFile(nativePath, nativeHash, new FileInfo(nativePath).Length) };
    }
    public void ValidateCurrent(EffectsSourceBinding expected, bool requireActive)
    {
        MainThread();
        if (!(bool)Property(pairs, "Enabled")!) throw Failure("Original source discovery changed after input freeze.");
        object? pair = Invoke(activePair);
        if (pair == null)
        {
            if (requireActive) throw Failure("Original effects source binding is absent during an authored pass.");
            return; // A Catalog cache clear does not change the held file input.
        }
        if (!SamePath(expected.ProjectPath, (string)Property(pair, "AapPath")!) || !SamePath(expected.ArchivePath, (string)Property(pair, "AasPath")!))
            throw Failure("Original effects source switched to a different project/archive.");
        if (requireActive && Convert.ToInt64(Property(pairs, "StorageRevision")) != expected.StorageRevision)
            throw Failure("Original effects storage revision changed without revalidating the held source pair.");
        if (expected.PlaybackProof != null && requireActive)
        {
            object live = RequireRuntimeReady(expected.StorageRevision, allowPending: false);
            if (!ReferenceEquals(live, retainedIndex) || !ReferenceEquals(Field(runtime, "_indexLoadTask"), retainedIndexTask)
                || Field(runtime, "_options") is not { } options || CurrentOptionsIdentity(options) != retainedOptions
                || CaptureContinueIdentity() != retainedContinues)
                throw Failure("The retained original effective playback state changed before authored execution.");
        }
    }
    public void RequestIndexReload()
    {
        MainThread();
        if (Invoke(reload) is not true) throw Failure("Original effects runtime refused its source-index refresh.");
    }
    private void ValidateCallback(Delegate callback)
    {
        if (callback.GetInvocationList().Length != 1) throw Failure("Original source callback unexpectedly contains multiple invocations.");
        Assembly owner = callback.Method.Module.Assembly;
        if (owner.IsDynamic || string.IsNullOrWhiteSpace(owner.Location)) throw Failure("Original source callback has no physical owner.");
        if (owner != effects && owner != core && owner != formats)
        {
            // Actual ConfiguredSceneResolutionService captures this immutable
            // shared-service sanitizer via ldvirtftn. This is one inspected
            // method/receiver, not permission for arbitrary Rukari callbacks.
            SharedDirectiveBuild.Validate(owner);
            Type sanitizer = owner.GetType("Rukari.Lib.Commands.EmbeddedDirectiveService+Sanitizer", true)!;
            if (callback.Method.DeclaringType != sanitizer || callback.Method.MetadataToken != 0x06000866
                || callback.Target?.GetType() != sanitizer || callback.Method.ReturnType != typeof(string)
                || !callback.Method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string) }))
                throw Failure("Shared source projection is not the exact original immutable sanitizer method.");
        }
        RequireUnpatched(callback.Method);
    }
    private static void RequireUnpatched(MethodInfo method)
    {
        var patches = Harmony.GetPatchInfo(method);
        if (patches != null && (patches.Owners.Any() || patches.Prefixes.Any() || patches.Postfixes.Any()
            || patches.Transpilers.Any() || patches.Finalizers.Any() || patches.ILManipulators.Any()))
            throw Failure("Original source callback has an uninspected patch.");
    }
    private void MainThread() { if (Environment.CurrentManagedThreadId != thread) throw Failure("Original effects discovery requires its acquiring main thread."); }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static object? Property(object value, string name) => value is Type type
        ? (type.GetProperty(name, Flags) ?? throw new MissingMemberException(type.FullName, name)).GetValue(null)
        : (value.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(value.GetType().FullName, name)).GetValue(value);
    private static object? Field(Type type, string name) => (type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name)).GetValue(null);
    private static MethodInfo RequireMethod(Type type, string name, params Type[] parameters) => type.GetMethod(name, Flags, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
    private static object? Invoke(MethodInfo method, params object[] arguments) => method.Invoke(null, arguments);
    private static object ResultValue(object? result, string scope) => result != null && Property(result, "Success") is true && Property(result, "Value") is { } value
        ? value : throw Failure(scope + " failed: " + (result == null ? "no result" : Property(result, "Error")));
    private static ExportException Failure(string message) => new("strict_effects_source_unavailable", message);
}
