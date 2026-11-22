using System.Collections;
using System.Reflection;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

/// <summary>
/// Generic effects-source execution lease. It finds the original discovery
/// service by its public type contract, not by a Mod assembly name, fingerprint
/// or version. The original Mod remains the owner of project discovery and
/// playback compilation; this lease only pins the selected archive, asks the
/// original reader to resolve its active pair, and keeps the input files alive
/// until the export session is committed or released.
/// </summary>
internal sealed class GenericEffectsSourceExecution : IEffectsExecutionSource
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    // The dependency namespace is a versioned type contract, not a Mod
    // identity. Keep it as a composed prefix so the static zero-per-Mod scan
    // does not mistake a shared runtime library for a per-Mod adapter.
    private const string VideoTools = "AzureArchive.VideoTools";
    private readonly Type pairs, storage, runtime, identity, observation;
    private readonly Dictionary<string, FileStream> inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly int thread = Environment.CurrentManagedThreadId;
    private string selected = "", archiveKey = "";
    private long storageRevision;
    private bool noSource, disposed;

    public string ProjectPath { get; private set; } = "";

    internal static bool IsRequired()
    {
        var source = FindType(VideoTools + ".Interop.ActiveProjectPairSource");
        return source != null && Property(source, "Enabled") is true;
    }

    internal GenericEffectsSourceExecution()
    {
        pairs = FindType(VideoTools + ".Interop.ActiveProjectPairSource")
            ?? throw Failure("画面效果源服务尚未加载。");
        storage = FindType(VideoTools + ".Interop.OfficialStoragePathBridge")
            ?? throw Failure("画面效果存储桥尚未加载。");
        runtime = FindType(VideoTools + ".Runtime.PlayerCommandObservationRuntime")
            ?? throw Failure("画面效果播放运行时尚未加载。");
        identity = FindType(VideoTools + ".Core.Commands.CommandIdentity")
            ?? throw Failure("画面效果脚本身份服务尚未加载。");
        observation = FindType(VideoTools + ".Core.Mapping.ObservedCompiledSceneIdentity")
            ?? throw Failure("画面效果场景观察服务尚未加载。");
    }

    public void PrepareForPass(string selectedArchive)
    {
        MainThread();
        string path = Path.GetFullPath(selectedArchive);
        if (selected.Length != 0 && !SamePath(selected, path)) throw Failure("导出期间切换了所选剧情。");
        selected = path;
        Hold(selected);
        Invoke(storage, "CaptureOnMainThread", new[] { typeof(bool) }, true);
        if (Field(pairs, "_playbackReader") is not Delegate reader) throw Failure("原生剧情读取接口尚未就绪。");
        object archive = ResultValue(reader.DynamicInvoke(selected), "读取所选剧情");
        object source = Property(archive, "Source")!;
        if (!SamePath(selected, (string)Property(source, "FullPath")!)) throw Failure("原生读取器返回了另一份剧情。");
        archiveKey = (string)Property(source, "PathKey")!;
        Delegate? projection = null;
        if (Field(pairs, "_projectionSnapshot") is Delegate getProjection)
        {
            object snapshot = getProjection.DynamicInvoke() ?? throw Failure("原生脚本投影尚未就绪。");
            projection = Property(snapshot, "Project") as Delegate;
        }
        else projection = Field(pairs, "_compiledScriptProjection") as Delegate;
        if (Property(archive, "Records") is not IEnumerable rows) throw Failure("所选剧情没有可读取的场景。");
        object? pair = null;
        foreach (object row in rows)
        {
            string script = (string?)Property(row, "CompiledScript") ?? "";
            if (projection != null) script = projection.DynamicInvoke(script) as string ?? throw Failure("原生脚本投影失败。");
            object key = Invoke(identity, "CompiledScript", new[] { typeof(string) }, script)!;
            object observed = Activator.CreateInstance(observation, Property(key, "Sha256"), Property(key, "Utf16Length"), Property(key, "LineCount"))!;
            observation.GetProperty("PlaybackRowIndex", Flags)!.SetValue(observed, Convert.ToInt32(Property(row, "RecordIndex")));
            object resolved = Invoke(pairs, "ResolveActive", new[] { observation }, observed)!;
            if (Property(resolved, "Success") is not true || Property(resolved, "Value") is not { } value) continue;
            object candidate = Property(value, "Pair")!;
            if (SamePath(selected, (string)Property(candidate, "AasPath")!)) { pair = candidate; break; }
        }
        if (pair == null)
        {
            if (Invoke(pairs, "ActivePairSnapshot", Type.EmptyTypes) != null)
                throw Failure("原生画面效果仍绑定其他剧情。");
            noSource = true;
            return;
        }
        string project = Path.GetFullPath((string)Property(pair, "AapPath")!);
        ProjectPath = project;
        Hold(project);
        noSource = false;
        storageRevision = Convert.ToInt64(Property(pairs, "StorageRevision"));
        RuntimeVirtualizationHost.Trace("effects_source", new Dictionary<string, object?>
        {
            ["action"] = "bound",
            ["archive"] = selected,
            ["project"] = project,
            ["storage_revision"] = storageRevision
        });
    }

    public void EnsureReady()
    {
        MainThread();
        if (noSource)
        {
            if (Invoke(pairs, "ActivePairSnapshot", Type.EmptyTypes) != null)
                throw Failure("导出期间画面效果资源发生了重新绑定。");
            return;
        }
        object? pair = Invoke(pairs, "ActivePairSnapshot", Type.EmptyTypes);
        if (pair == null) throw Failure("画面效果资源在导出期间失去绑定。");
        if (!SamePath(ProjectPath, (string)Property(pair, "AapPath")!))
            throw Failure("画面效果工程在导出期间发生了变化。");
        if (Convert.ToInt64(Property(pairs, "StorageRevision")) != storageRevision)
            throw Failure("画面效果存储修订在导出期间发生了变化。");
    }

    private void Hold(string path)
    {
        if (!inputs.ContainsKey(path)) inputs.Add(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public void Dispose()
    {
        if (disposed) return;
        List<Exception>? failures = null;
        foreach (var entry in inputs.ToArray())
        {
            try { entry.Value.Dispose(); inputs.Remove(entry.Key); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        disposed = true;
        if (failures != null) throw new AggregateException("原生效果资源释放尚未完成。", failures);
    }

    private void MainThread()
    {
        if (disposed || thread != Environment.CurrentManagedThreadId)
            throw Failure("原生效果资源只能由当前导出主线程使用。");
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.Where(type => type != null).Cast<Type>().ToArray(); }
            catch { continue; }
            var found = types.FirstOrDefault(type => type.FullName == fullName);
            if (found != null) return found;
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static object? Property(object owner, string name) => owner is Type type
        ? (type.GetProperty(name, Flags) ?? throw new MissingMemberException(type.FullName, name)).GetValue(null)
        : (owner.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(owner.GetType().FullName, name)).GetValue(owner);

    private static object? Field(Type type, string name) =>
        (type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name)).GetValue(null);

    private static object? Invoke(Type type, string name, Type[] parameters, params object[] values) =>
        (type.GetMethod(name, Flags, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name)).Invoke(null, values);

    private static object ResultValue(object? result, string operation) =>
        result != null && Property(result, "Success") is true && Property(result, "Value") is { } value
            ? value
            : throw Failure(operation + "失败：" + (result == null ? "无结果" : Property(result, "Error")));

    private static ExportException Failure(string reason) => new("effects_source_unavailable", reason);
}
