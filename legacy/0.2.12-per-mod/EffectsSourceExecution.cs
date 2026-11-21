using System.Collections;
using System.Reflection;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// Normal source preparation delegates discovery/compilation to the original
// Mod. This lease contains readiness and input ownership only: no code audit,
// semantic proof, recompiled-index comparison or file-content verification.
internal sealed class EffectsSourceExecution : IEffectsExecutionSource
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private readonly Type pairs,storage,runtime,identity,observation;
    private readonly Dictionary<string,FileStream> inputs=new(StringComparer.OrdinalIgnoreCase);
    private readonly int thread=Environment.CurrentManagedThreadId;
    private object? activeIndex;
    private string selected="",archiveKey="";
    private long storageRevision;
    private bool noSource,disposed;
    public string ProjectPath { get; private set; }="";

    internal static bool IsRequired()
    {
        var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Rukari.MoreEffects").ToArray();
        if(assemblies.Length==0)return false;
        if(assemblies.Length!=1)throw Failure("画面效果模块重复加载，无法取得唯一的播放接口。");
        var source=assemblies[0].GetType("AzureArchive.VideoTools.Interop.ActiveProjectPairSource",true)!;
        return Property(source,"Enabled") is true;
    }

    internal EffectsSourceExecution()
    {
        var effects=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Rukari.MoreEffects");
        var core=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="AzureArchive.VideoTools.Core");
        pairs=effects.GetType("AzureArchive.VideoTools.Interop.ActiveProjectPairSource",true)!;
        storage=effects.GetType("AzureArchive.VideoTools.Interop.OfficialStoragePathBridge",true)!;
        runtime=effects.GetType("AzureArchive.VideoTools.Runtime.PlayerCommandObservationRuntime",true)!;
        identity=core.GetType("AzureArchive.VideoTools.Core.Commands.CommandIdentity",true)!;
        observation=core.GetType("AzureArchive.VideoTools.Core.Mapping.ObservedCompiledSceneIdentity",true)!;
    }

    public void PrepareForPass(string selectedArchive)
    {
        MainThread();
        string path=Path.GetFullPath(selectedArchive);
        if(selected.Length!=0&&!SamePath(selected,path))throw Failure("导出期间切换了所选剧情。");
        selected=path; Hold(selected);
        Invoke(storage,"CaptureOnMainThread",new[]{typeof(bool)},true);
        if(Field(pairs,"_playbackReader") is not Delegate reader)throw Failure("原生剧情读取接口尚未就绪。");
        object archive=Result(reader.DynamicInvoke(selected),"读取所选剧情");
        object source=Property(archive,"Source")!;
        if(!SamePath(selected,(string)Property(source,"FullPath")!))throw Failure("原生读取器返回了另一份剧情。");
        archiveKey=(string)Property(source,"PathKey")!; // Original cache routing key, not an exporter audit digest.
        Delegate? projection=null;
        if(Field(pairs,"_projectionSnapshot") is Delegate getProjection)
        {
            object snapshot=getProjection.DynamicInvoke()??throw Failure("原生脚本投影尚未就绪。");
            projection=Property(snapshot,"Project") as Delegate;
        }
        else projection=Field(pairs,"_compiledScriptProjection") as Delegate;
        if(Property(archive,"Records") is not IEnumerable rows)throw Failure("所选剧情没有可读取的场景。");
        object? pair=null;
        foreach(object row in rows)
        {
            string script=(string?)Property(row,"CompiledScript")??"";
            if(projection!=null)script=projection.DynamicInvoke(script) as string??throw Failure("原生脚本投影失败。");
            object key=Invoke(identity,"CompiledScript",new[]{typeof(string)},script)!;
            object observed=Activator.CreateInstance(observation,Property(key,"Sha256"),Property(key,"Utf16Length"),Property(key,"LineCount"))!;
            observation.GetProperty("PlaybackRowIndex",Flags)!.SetValue(observed,Convert.ToInt32(Property(row,"RecordIndex")));
            object resolved=Invoke(pairs,"ResolveActive",new[]{observation},observed)!;
            if(Property(resolved,"Success") is not true||Property(resolved,"Value") is not { } value)continue;
            object candidate=Property(value,"Pair")!;
            if(SamePath(selected,(string)Property(candidate,"AasPath")!)){pair=candidate;break;}
        }
        if(pair==null)
        {
            // A standalone native archive need not have an authoring project.
            // Do not manufacture a project or authorize another active index.
            if(Invoke(pairs,"ActivePairSnapshot",Type.EmptyTypes)!=null)
                throw Failure("画面效果仍绑定其他剧情，原生资源未准备好。");
            noSource=true;activeIndex=null;return;
        }
        string project=Path.GetFullPath((string)Property(pair,"AapPath")!);
        if(ProjectPath.Length!=0&&!SamePath(ProjectPath,project))throw Failure("导出期间切换了画面效果工程。");
        ProjectPath=project;Hold(project);noSource=false;
        storageRevision=Convert.ToInt64(Property(pairs,"StorageRevision"));
        activeIndex=ReadyIndex(allowPending:true);
    }

    public void EnsureReady()
    {
        MainThread();
        if(noSource)
        {
            if(Invoke(pairs,"ActivePairSnapshot",Type.EmptyTypes)!=null)throw Failure("导出期间画面效果资源发生了重新绑定。");
            return;
        }
        if(!ReferenceEquals(activeIndex,ReadyIndex(allowPending:false)))throw Failure("导出期间画面效果播放队列被重新加载。");
    }

    private object ReadyIndex(bool allowPending)
    {
        object? index=Field(runtime,"_index");
        var load=Field(runtime,"_indexLoadTask") as Task;
        bool consumed=Field(runtime,"_indexLoadConsumed") is true;
        bool pending=Convert.ToInt32(Field(runtime,"_indexReloadRequested"))!=0||load!=null&&(!load.IsCompleted||!consumed)
            ||index==null&&load==null&&Field(runtime,"_lastFailedIndexLoadSignature")==null;
        if(pending&&allowPending)throw new ExportException("strict_effects_source_pending","正在等待原生画面效果资源准备完成。");
        if(pending||index==null||load==null||!load.IsCompletedSuccessfully||!consumed)
            throw Failure("原生画面效果资源尚未完成加载。");
        if((string?)Property(index,"PlaybackPathKey")!=archiveKey
            ||Convert.ToInt64(Property(pairs,"StorageRevision"))!=storageRevision
            ||Convert.ToInt64(Field(runtime,"_indexStorageRevision"))!=storageRevision)
            throw Failure("画面效果播放队列不属于当前剧情或加载状态。");
        return index;
    }

    private void Hold(string path)
    {
        if(!inputs.ContainsKey(path))inputs.Add(path,new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read));
    }
    public void Dispose()
    {
        if(disposed)return;
        List<Exception>? failures=null;
        foreach(var entry in inputs.ToArray())
        {
            try{entry.Value.Dispose();inputs.Remove(entry.Key);}catch(Exception error){(failures??=new()).Add(error);}
        }
        if(failures!=null)throw new AggregateException("原生效果资源释放尚未完成。",failures);
        disposed=true;activeIndex=null;
    }
    private void MainThread()
    {if(disposed||thread!=Environment.CurrentManagedThreadId)throw Failure("原生效果资源只能由当前导出主线程使用。");}
    private static bool SamePath(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    private static object? Property(object owner,string name)=>owner is Type type
        ?(type.GetProperty(name,Flags)??throw new MissingMemberException(type.FullName,name)).GetValue(null)
        :(owner.GetType().GetProperty(name,Flags)??throw new MissingMemberException(owner.GetType().FullName,name)).GetValue(owner);
    private static object? Field(Type type,string name)=>(type.GetField(name,Flags)??throw new MissingFieldException(type.FullName,name)).GetValue(null);
    private static object? Invoke(Type type,string name,Type[] parameters,params object[] values)=>
        (type.GetMethod(name,Flags,null,parameters,null)??throw new MissingMethodException(type.FullName,name)).Invoke(null,values);
    private static object Result(object? result,string operation)=>result!=null&&Property(result,"Success") is true&&Property(result,"Value") is { } value
        ?value:throw Failure(operation+"失败："+(result==null?"无结果":Property(result,"Error")));
    private static ExportException Failure(string reason)=>new("effects_source_unavailable",reason);
}
