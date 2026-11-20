using System.Reflection;
using System.Text.Json;
using Aave.WaitOwnership.CallerSource;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using OpCodes=Mono.Cecil.Cil.OpCodes;
internal static class Program
{
    internal static readonly List<string> Trace=new();
    internal static readonly Sink Consumer=new();
    static readonly List<object> checks=new();static readonly List<object> retained=new();static int failures;
    static void Need(bool b,string s){if(!b)throw new InvalidDataException(s);}
    static void Case(string name,Action a)
    {
        Trace.Clear();Consumer.Frames.Clear();Consumer.Deny=false;Consumer.AfterConsume=null;Consumer.BeforeConsume=null;Consumer.RawReceipt=null;Consumer.Borrowed=false;Foreign.Mode="plain";Fixture.Inline=null;
        try{a();checks.Add(new{name,passed=true,trace=Trace.ToArray()});}
        catch(Exception e){failures++;checks.Add(new{name,passed=false,error=e.ToString(),trace=Trace.ToArray()});}
    }
    internal sealed record Built(ManagedChainRegistration Registration,Action<Player,Animation,string> Call,MethodInfo Method,DynamicMethodDefinition Definition);
    static Built Build(string name,CallerSourceKind kind,ManagedChainRegistration? prior=null,bool mutateAfter=false,int planId=2,bool mutateBefore=false)
    {
        var original=typeof(Fixture).GetMethod(name)!;var d=new DynamicMethodDefinition(original);retained.Add(d);
        ApplySource(d,kind,planId);var r=prior??ManagedChainRegistration.ForOrdinaryControl(original,d.Definition,kind==CallerSourceKind.Release?planId:1,kind);retained.Add(r);
        if(kind==CallerSourceKind.Replacement)r.SetOrdinaryForwarder(typeof(Fixture).GetMethod(nameof(Fixture.InvokeRelease))!,typeof(Fixture).GetMethod(nameof(Fixture.Release))!);
        var ctx=new ILContext(d.Definition);retained.Add(ctx);
        var info=new PatchInfo{
            prefixes=kind==CallerSourceKind.Release?new[]{new Patch(new HarmonyMethod(typeof(Foreign).GetMethod(nameof(Foreign.Prefix))!),0,"foreign.prefix")}:Array.Empty<Patch>(),
            postfixes=kind==CallerSourceKind.Release?new[]{new Patch(new HarmonyMethod(typeof(Foreign).GetMethod(nameof(Foreign.Postfix))!),0,"foreign.postfix")}:Array.Empty<Patch>(),
            finalizers=kind==CallerSourceKind.Release?new[]{new Patch(new HarmonyMethod(typeof(Foreign).GetMethod(nameof(Foreign.Finalizer))!),0,"foreign.finalizer")}:Array.Empty<Patch>(),
            ilmanipulators=new[]{new Patch(new HarmonyMethod(typeof(WholeChainManipulator).GetMethod(nameof(WholeChainManipulator.Apply))!),0,"own.whole-chain")}};
        if(mutateBefore)d.Definition.Body.Instructions[0].Operand=777;
        HarmonyManipulator.Manipulate(original,info,ctx);
        if(mutateAfter)d.Definition.Body.Instructions[0].Operand=-731L;
        var m=d.Generate();retained.Add(m);var callable=m.CreateDelegate<Action<Player,Animation,string>>();retained.Add(callable);
        r.PublishOrdinary(m,callable);return new(r,callable,m,d);
    }
    static void ApplySource(DynamicMethodDefinition d,CallerSourceKind kind,int planId)
    {
        var m=d.Definition;
        var sites=kind==CallerSourceKind.Release?Array.Empty<CallerSite>():m.Body.Instructions.Where(i=>i.OpCode==OpCodes.Callvirt&&i.Operand is MethodReference x&&x.Name is "Factory" or "Start" or "set_Curr")
            .Select(i=>{var x=(MethodReference)i.Operand;return new CallerSite(i.Offset,x.Name=="Factory"?CallerSiteKind.Factory:x.Name=="Start"?CallerSiteKind.Scheduler:CallerSiteKind.HandleWrite,x.FullName);}).ToArray();
        var releases=m.Body.Instructions.Where(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference x&&x.Name==nameof(Fixture.InvokeRelease)).Select(i=>new CallerReleaseSite(i.Offset,2,((MethodReference)i.Operand).FullName)).ToArray();
        var callerPlan=new CallerMethodPlan(kind==CallerSourceKind.Release?planId:1,kind,m.Module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),sites,releases);
        var facts=m.Body.Instructions.Where(i=>i.Operand is MethodReference x&&x.Name==nameof(Fixture.Probe)).Select(i=>new CompletionSitePlan(i.Offset,i.Offset,CompletionFactKind.PendingQuery,0,((MethodReference)i.Operand).FullName)).ToArray();
        CompletionFactsRewriter.Apply(m,new CompletionMethodPlan(callerPlan.PlanId,m.Module.Mvid,m.MetadataToken.ToInt32(),callerPlan.OriginalFingerprint,false,facts,callerPlan));
    }
    static int Qualified()=>Consumer.Frames.Count(f=>f.Entry.ReleaseSource?.ExactCalleeBoundaryProven==true);
    public static int Main(string[] args)
    {
        if(args.Length!=1||File.Exists(args[0]))throw new ArgumentException("fresh output.json");
        Need(Environment.Version==new Version(6,0,36),"ordinary636 only");
        foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {var k=(string)e.Key;Need(!k.StartsWith("MONOMOD",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("CORECLR_",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("COR_",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("COMPlus_",StringComparison.OrdinalIgnoreCase)&&k!="DOTNET_STARTUP_HOOKS","modified runtime environment");}
        CallerSourceHooks.Bind(Consumer);
        var release=Build(nameof(Fixture.Release),CallerSourceKind.Release);var caller=Build(nameof(Fixture.Decide),CallerSourceKind.Replacement);
        Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;
        Case("exact actual generated caller and callee materializations qualify",()=>{
            Need(release.Registration.Qualified,"callee: "+release.Registration.Reason);Need(caller.Registration.Qualified,"caller: "+caller.Registration.Reason);
        });
        Case("facts inspect only exact current Release delivery",()=>{ReleaseSource? saved=null;CompletionFact? fact=null;Consumer.BeforeConsume=receipt=>{saved=receipt;fact=CompletionFactsHooks.ForFrame(receipt.Parent)!.Facts.Single(f=>f.Kind==CompletionFactKind.PendingQuery);Need(ReferenceEquals(fact.Invocation.Frame,receipt.Parent)&&receipt.CanInspectParentFacts&&!fact.IsUsableFor(receipt),"ordinary facts mistaken for normal or parent mismatch");bool cross=true;var t=new Thread(()=>cross=receipt.CanInspectParentFacts);t.Start();t.Join();Need(!cross,"cross-thread delivery accepted");};caller.Call(new(),new(),"facts");Need(saved!=null&&!saved.CanInspectParentFacts&&fact!=null&&!fact.IsUsableFor(saved),"historical delivery accepted");});
        Case("nested raw source delivery cannot consume the outer receipt",()=>{
            Consumer.BeforeConsume=receipt=>{Consumer.BeforeConsume=null;Consumer.RawReceipt=receipt;var raw=CallerSourceHooks.Enter(919,1,null,null,null);CallerSourceHooks.Exit(raw);Consumer.RawReceipt=null;};
            caller.Call(new(),new(),"nested-raw-delivery");Need(!Consumer.Borrowed&&Consumer.Frames.Last().Consumed,"nested raw delivery borrowed outer permission");
        });        Case("source caller exact original has one delivery-window-only evidence",()=>{
            var p=new Player();var a=new Animation();caller.Call(p,a,"normal");
            Need(Qualified()==1&&Consumer.Frames.Last().Consumed,"no qualified own original source");
            Need(!Consumer.Frames.Last().Entry.ReleaseSource!.TryConsumeForCurrentOriginal(),"stored evidence reusable outside delivery window");
            Need(!Consumer.Frames.Last().Entry.ReleaseSource!.NormalProviderPublished,"ordinary controls misrepresented installed provider");
        });
        Case("same actual foreign prefix raw reentry cannot steal outer source",()=>{
            Foreign.Mode="reenter";caller.Call(new(),new(),"same");
            Need(Consumer.Frames.Count==3,"not caller/inner/outer");
            Need(Consumer.Frames[1].Entry.ReleaseSource==null&&Consumer.Frames[2].Consumed&&Qualified()==1,"same-prefix reentry stole source or outer lost exact evidence");
            Need(Trace.Count(s=>s=="postfix")==2&&Trace.Count(s=>s=="finalizer")==2,"foreign suffix/finalizer lost");
        });
        Case("foreign veto consumes no original permit and retains suffix",()=>{
            Foreign.Mode="veto";var p=new Player();caller.Call(p,new(),"normal");
            Need(Qualified()==0&&Consumer.Frames.Count==1&&p.Stops==0,"veto reached original");Need(Trace.Contains("postfix")&&Trace.Contains("finalizer"),"veto suffix absent");
        });
        Case("permit denial skips only own original body",()=>{
            Consumer.Deny=true;var p=new Player();var a=new Animation();caller.Call(p,a,"normal");
            Need(Qualified()==1&&p.Stops==0&&a.Curr!=null&&a.Completes==0,"denial performed original side effect");Need(Trace.Contains("postfix")&&Trace.Contains("finalizer"),"denial skipped whole chain");
        });
        Case("unknown raw source keeps original even when sink requests denial",()=>{
            Consumer.Deny=true;var p=new Player();var a=new Animation();release.Call(p,a,"same");
            Need(Qualified()==0&&p.Stops==1&&a.Completes==1,"unowned raw path denied");
        });
        Case("same exception survives nested cleanup and external first-pass filter",()=>{
            var p=new Player{ThrowStop=true};var a=new Animation();Exception? caught=null;
            try{caller.Call(p,a,"error");}catch(Exception e)when(RecordFilter(e)){caught=e;Trace.Add("catch");}
            Need(ReferenceEquals(caught,p.Failure),"exception instance replaced");Need(Trace.IndexOf("filter")<Trace.LastIndexOf("source-exit")&&Trace.Last()=="catch","source caller cleanup ran before first-pass external filter");
            Need(!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"exception scopes leaked");
        });
        Case("foreign prefix exception retains finalizer and no release evidence",()=>{
            Foreign.Mode="throw";Exception? caught=null;try{caller.Call(new(),new(),"prefix");}catch(Exception e){caught=e;}
            Need(ReferenceEquals(caught,Foreign.Failure)&&Qualified()==0&&Trace.Contains("finalizer"),"foreign exception semantics changed");Need(!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"prefix exception scope leaked");
        });
        Case("foreign ref-argument change preserves behavior without source authority",()=>{
            Foreign.Mode="mutateargs";var p=new Player();var a=new Animation();caller.Call(p,a,"mutated-args");
            Need(p.Stops==1&&a.Completes==0&&Qualified()==0,"changed original arguments received caller authority");
        });
        Case("foreign finalizer exception suppression retains failed original outcome",()=>{
            Foreign.Mode="suppress";var p=new Player{ThrowStop=true};caller.Call(p,new(),"suppressed");
            Need(Qualified()==1&&!Consumer.Frames.Last().Returned&&Consumer.Frames.First().Returned,"suppression changed original/caller return distinction");Need(!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"suppressed context leaked");
        });
        Case("different thread direct call cannot borrow pending source",()=>{
            Foreign.Mode="thread";caller.Call(new(),new(),"thread");Need(Qualified()==1,"cross-thread source leaked or outer evidence lost");
        });
        Case("cached old generation cannot borrow current caller context",()=>{
            var old=release;release=Build(nameof(Fixture.Release),CallerSourceKind.Release,old.Registration);
            Fixture.ReleaseRoute=release.Call;Foreign.Route=old.Call;Foreign.Mode="reenter";
            caller.Call(new(),new(),"cached");
            Need(Consumer.Frames.Count==3&&Consumer.Frames[1].Entry.ReleaseSource==null&&Consumer.Frames[2].Consumed,"old generation borrowed current evidence");
            Foreign.Route=release.Call;
        });
        Case("later final-body mutator refuses new generation and revokes old",()=>{
            var old=release;release=Build(nameof(Fixture.Release),CallerSourceKind.Release,old.Registration,true);
            Need(!release.Registration.Qualified,"post-snapshot mutator accepted");
            Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;caller.Call(new(),new(),"mutated");Need(Qualified()==0,"mutated generation produced evidence");
            Consumer.Frames.Clear();old.Call(new(),new(),"old");Need(Qualified()==0,"old generation remained active");
            release=Build(nameof(Fixture.Release),CallerSourceKind.Release,old.Registration);Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;
        });
        Case("prechange revocation does not pretend successful publication",()=>{
            release.Registration.Revoke("controlled equivalent of external prechange");caller.Call(new(),new(),"late");Need(Qualified()==0,"revoked generation admitted");
            release=Build(nameof(Fixture.Release),CallerSourceKind.Release,release.Registration);Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;Need(release.Registration.Qualified,release.Registration.Reason);
        });
        Case("revocation during sink delivery prevents stale original denial",()=>{
            Consumer.Deny=true;Consumer.AfterConsume=()=>release.Registration.Revoke("revoked while owner inspects entry");var p=new Player();caller.Call(p,new(),"revoke-in-sink");
            Need(p.Stops==1,"revoked decision still denied original");Consumer.AfterConsume=null;
            release=Build(nameof(Fixture.Release),CallerSourceKind.Release,release.Registration);Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;
        });
        Case("Dispose revokes cached body and closes all remaining contexts",()=>{
            release.Registration.Dispose();caller.Call(new(),new(),"disposed");Need(Qualified()==0&&!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"disposed generation authority or leak");
        });
        Case("nested source-backed caller receives its own distinct evidence",()=>{
            using var rr=ManagedChainRegistration.ForOrdinaryControl(typeof(Fixture).GetMethod(nameof(Fixture.Release))!,PreparedRelease(),2,CallerSourceKind.Release);
            release=Build(nameof(Fixture.Release),CallerSourceKind.Release,rr);Fixture.ReleaseRoute=release.Call;Foreign.Route=release.Call;Foreign.Caller=caller.Call;Foreign.Mode="nested-caller";
            caller.Call(new(),new(),"outer");Need(Qualified()==2&&Consumer.Frames.Count==4,"nested caller lost or borrowed evidence");
            Need(Consumer.Frames[2].Entry.ReleaseSource!.Parent!=Consumer.Frames[3].Entry.ReleaseSource!.Parent,"source parents conflated");
        });
        Case("different registered target cannot match source plan even with same operands",()=>{
            using var other=Build(nameof(Fixture.OtherRelease),CallerSourceKind.Release,planId:3).Registration;
            var otherBody=(Action<Player,Animation,string>)retained.OfType<Delegate>().Last();Fixture.ReleaseRoute=otherBody;Foreign.Route=otherBody;
            var p=new Player();caller.Call(p,new(),"other");Need(Qualified()==0&&p.Stops==1,"wrong target borrowed plan2 source");
        });
        Case("original source mutation before whole-chain stage remains observational",()=>{
            var broken=Build(nameof(Fixture.Release),CallerSourceKind.Release,mutateBefore:true);using var own=broken.Registration;
            Need(!own.Qualified&&own.Reason.Contains("absent"),"changed original template qualified");Fixture.ReleaseRoute=broken.Call;Foreign.Route=broken.Call;var p=new Player();caller.Call(p,new(),"unknown");Need(Qualified()==0&&p.Stops==1,"unknown source mutated original behavior");
        });
        Case("actual original source filter and finally retain first-pass semantics",()=>{
            var filtered=Build(nameof(Fixture.FilteredRelease),CallerSourceKind.Release);using var own=filtered.Registration;
            Need(own.Qualified,own.Reason);caller.Registration.SetOrdinaryForwarder(typeof(Fixture).GetMethod(nameof(Fixture.Release))!,typeof(Fixture).GetMethod(nameof(Fixture.FilteredRelease))!);Fixture.ReleaseRoute=filtered.Call;Foreign.Route=filtered.Call;var p=new Player{ThrowStop=true};Exception? caught=null;
            try{caller.Call(p,new(),"filtered");}catch(Exception e){caught=e;}
            Need(ReferenceEquals(caught,p.Failure),"filter path exception changed");Need(Trace.IndexOf("original-filter")<Trace.IndexOf("original-finally"),"firstpass filter ran after original finally");Need(!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"filtered context leaked");
        });
        var report=new{Runtime=Environment.Version.ToString(),ProcessId=Environment.ProcessId,Total=checks.Count,Failures=failures,Checks=checks,CallerReason=caller.Registration.Reason,CalleeReason=release.Registration.Reason,NativeExecuted=false,InstalledDetour=false,ProfilerExecuted=false,AAExecuted=false,ActualProviderExecuted=false};
        File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));GC.KeepAlive(retained);return failures==0?0:1;
    }
    static Mono.Cecil.MethodDefinition PreparedRelease()
    {var d=new DynamicMethodDefinition(typeof(Fixture).GetMethod(nameof(Fixture.Release))!);retained.Add(d);ApplySource(d,CallerSourceKind.Release,2);return d.Definition;}
    static bool RecordFilter(Exception e){Trace.Add("filter");return true;}
    internal sealed class Sink:ICallerSourceSink
    {internal bool Deny;internal Action? AfterConsume;internal Action<ReleaseSource>? BeforeConsume;internal ReleaseSource? RawReceipt;internal bool Borrowed;internal readonly List<Frame> Frames=new();public ICallerSourceFrame Enter(CallerSourceEntry e){if(e.PlanId==919&&RawReceipt!=null)Borrowed=RawReceipt.TryConsumeForCurrentOriginal();var f=new Frame(this,e);lock(Frames)Frames.Add(f);return f;}}
    internal sealed class Frame:ICallerSourceFrame
    {
        readonly Sink sink;internal readonly CallerSourceEntry Entry;internal readonly bool Consumed;internal bool Returned;
        internal Frame(Sink sink,CallerSourceEntry entry){this.sink=sink;Entry=entry;if(entry.ReleaseSource!=null)sink.BeforeConsume?.Invoke(entry.ReleaseSource);Consumed=entry.ReleaseSource?.TryConsumeForCurrentOriginal()??false;if(Consumed&&entry.ReleaseSource!.TryConsumeForCurrentOriginal())throw new Exception("double evidence consume");if(Consumed)sink.AfterConsume?.Invoke();}
        public bool AllowOriginal=>!sink.Deny;
        public void FactoryReturned(object a,float d,object? i){}public void SchedulerEntering(object p,object i){}public void SchedulerReturned(object p,object i,object? h){}public void HandleWritten(object a,object? h){}
        public void OriginalReturned(){Returned=true;Trace.Add("source-return");}public void Exit(bool returned)=>Trace.Add("source-exit");
    }
}
public sealed class Handle{}
public sealed class Iterator{}
public sealed class Player
{
    public int Stops;public bool ThrowStop;public readonly Exception Failure=new ApplicationException("Stop sentinel");
    public void Stop(Handle? h){Stops++;Program.Trace.Add("stop");if(ThrowStop)throw Failure;}
    public Handle Start(Iterator i)=>new();
}
public sealed class Animation
{
    public Handle? Curr{get;set;}=new();public int Completes;
    public Iterator Factory(float d)=>new();public void Complete(string reason){Completes++;Program.Trace.Add("complete:"+reason);Fixture.Inline?.Invoke();}
}
public static class Fixture
{
    public static Action<Player,Animation,string>? ReleaseRoute;public static Action? Inline;
    public static void Decide(Player p,Animation a,string reason){Probe();a.Curr=p.Start(a.Factory(1));InvokeRelease(p,a,reason);}
    // Only the no-detour fixture dispatches through a delegate. This forwarder
    // performs no prefix/source decision. Actual Root source calls its original
    // managed callee via the real stock normal provider instead.
    public static bool Probe()=>false;
    public static void InvokeRelease(Player p,Animation a,string reason)=>ReleaseRoute!(p,a,reason);
    public static void Release(Player p,Animation a,string reason){p.Stop(a.Curr);a.Curr=null;a.Complete(reason);}
    public static void OtherRelease(Player p,Animation a,string reason){p.Stop(a.Curr);a.Curr=null;a.Complete(reason);}
    public static void FilteredRelease(Player p,Animation a,string reason){try{p.Stop(a.Curr);a.Curr=null;a.Complete(reason);}catch(Exception e)when(Filter(e)){throw;}finally{Program.Trace.Add("original-finally");}}
    public static bool Filter(Exception e){Program.Trace.Add("original-filter");return true;}
}
public static class Foreign
{
    [ThreadStatic] static bool nested;public static string Mode="plain";public static Action<Player,Animation,string>? Route;public static Action<Player,Animation,string>? Caller;
    public static readonly Exception Failure=new ApplicationException("prefix sentinel");
    public static bool Prefix(Player p,ref Animation a,string reason)
    {
        Program.Trace.Add(nested?"prefix-inner":"prefix-outer");if(nested)return true;
        if(Mode=="mutateargs")a=new Animation();if(Mode=="veto")return false;if(Mode=="throw")throw Failure;
        if(Mode=="nested-caller"){nested=true;try{Caller!(p,a,"nested");}finally{nested=false;}}
        if(Mode=="reenter"){nested=true;try{Route!(p,a,reason);}finally{nested=false;}}
        if(Mode=="thread"){var threadAnimation=a;var t=new Thread(()=>{nested=true;try{Route!(p,threadAnimation,reason);}finally{nested=false;}});t.Start();t.Join();}
        return true;
    }
    public static void Postfix()=>Program.Trace.Add("postfix");
    public static Exception? Finalizer(Exception? __exception){Program.Trace.Add("finalizer");return Mode=="suppress"?null:__exception;}
}







