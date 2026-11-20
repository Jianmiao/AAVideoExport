using System.Reflection;
using System.Text.Json;
using Aave.WaitOwnership.CallerSource;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using Code=Mono.Cecil.Cil.Code;
internal static class Program
{
    static readonly List<object> retained=new(),checks=new();static int failures;static bool observationalBaseline;
    static readonly Sink facts=new();internal static CallerSourceHooks.SourceFrame? LastTick;
    static void Need(bool b,string message){if(!b)throw new InvalidDataException(message);}
    static MethodInfo M(Type t,string n)=>t.GetMethod(n)!;
    static readonly Dictionary<string,(CompletionFactKind Kind,int Plan)> calls=new(){
        [".ctor"]=(CompletionFactKind.Constructed,0),["Add"]=(CompletionFactKind.ListAdded,0),
        ["CallArm"]=(CompletionFactKind.GateArm,11),["CallTick"]=(CompletionFactKind.GateTick,12),
        ["CallPending"]=(CompletionFactKind.PendingQuery,13),["CallDue"]=(CompletionFactKind.GateDueRead,27),["CallSetSequence"]=(CompletionFactKind.MemberCall,28)};
    sealed record Built(ManagedChainRegistration Registration,Delegate Call);
    static Built Build(MethodInfo original,int plan,Type delegateType,bool foreign=false)
    {
        bool unbound=observationalBaseline&&plan is 11 or 12 or 13 or 27 or 28;
        var d=new DynamicMethodDefinition(original);retained.Add(d);var m=d.Definition;
        var sites=m.Body.Instructions.Where(i=>i.Operand is MethodReference c&&calls.ContainsKey(c.Name)||i.Operand is FieldReference&&i.OpCode.Code is Code.Ldfld or Code.Stfld)
            .Select((i,n)=>new CompletionSitePlan(n+1,i.Offset,i.Operand is MethodReference c?calls[c.Name].Kind:i.OpCode.Code==Code.Stfld?CompletionFactKind.FieldWritten:CompletionFactKind.FieldRead,i.Operand is MethodReference c2?calls[c2.Name].Plan:0,((MemberReference)i.Operand).FullName)).ToArray();
        var receipt=CompletionFactsRewriter.Apply(m,new(plan,m.Module.Mvid,m.MetadataToken.ToInt32(),CallerSourceRewriter.Fingerprint(m),!original.IsStatic,sites,ReturnFields:original.Name=="Arm"?new[]{"Sequence","Due"}.Select((n,i)=>{var f=typeof(Gate).GetField(n)!;return new CompletionReturnField(65536+i,f.MetadataToken,m.Body.Instructions.Select(x=>x.Operand).OfType<FieldReference>().First(x=>x.Name==n).FullName);}).ToArray():null));retained.Add(receipt);
        var r=ManagedChainRegistration.ForOrdinaryControl(original,m,plan,CallerSourceKind.Observation);retained.Add(r);
        foreach(var pair in new[]{("CallArm",typeof(Gate),"Arm"),("CallTick",typeof(Gate),"Tick"),("CallPending",typeof(Fixture),"Pending"),("CallDue",typeof(Gate),"ReadDue"),("CallSetSequence",typeof(Gate),"SetSequence")})r.SetOrdinaryForwarder(M(typeof(Fixture),pair.Item1),M(pair.Item2,pair.Item3));
        var ctx=new ILContext(m);retained.Add(ctx);
        var info=new PatchInfo{
            prefixes=original.Name=="SetSequence"?new[]{new Patch(new HarmonyMethod(M(typeof(ForeignSetter),"Prefix")),0,"setter")}:foreign?new[]{new Patch(new HarmonyMethod(M(typeof(Foreign),"Prefix")),0,"foreign")}:Array.Empty<Patch>(),
            postfixes=foreign?new[]{new Patch(new HarmonyMethod(M(typeof(Foreign),"Postfix")),0,"foreign")}:Array.Empty<Patch>(),
            finalizers=foreign?new[]{new Patch(new HarmonyMethod(M(typeof(Foreign),"Finalizer")),0,"foreign")}:Array.Empty<Patch>(),
            ilmanipulators=unbound?Array.Empty<Patch>():new[]{new Patch(new HarmonyMethod(M(typeof(WholeChainManipulator),"Apply")),0,"own")}};
        HarmonyManipulator.Manipulate(original,info,ctx);var generated=d.Generate();retained.Add(generated);var callable=generated.CreateDelegate(delegateType);retained.Add(callable);r.PublishOrdinary(generated,callable);
        if(!unbound)Need(r.Qualified,original.Name+": "+r.Reason);return new(r,callable);
    }
    static void Case(string name,Action action){facts.All.Clear();facts.Live.Clear();facts.Nested.Clear();Foreign.Mode="plain";Fixture.Query=true;Fixture.ThrowPending=false;try{action();checks.Add(new{name,passed=true});}catch(Exception e){failures++;checks.Add(new{name,passed=false,error=e.ToString()});}}
    static CompletionFact One(CompletionFactKind k)=>facts.All.Single(x=>x.Kind==k);
    public static int Main(string[] args)
    {
        if(args.Length is <1 or >2||File.Exists(args[0])||args.Length==2&&args[1]!="--observational-baseline")throw new ArgumentException("fresh evidence path required");
        observationalBaseline=args.Length==2;
        Need(Environment.Version==new Version(6,0,36),"ordinary 6.0.36 only");
        foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()){var k=(string)e.Key;Need(!k.StartsWith("MONOMOD",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("CORECLR_",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("COR_",StringComparison.OrdinalIgnoreCase)&&!k.StartsWith("COMPlus_",StringComparison.OrdinalIgnoreCase)&&k!="DOTNET_STARTUP_HOOKS","modified runtime environment");}
        CompletionFactsHooks.Bind(facts);
        try{
        var setter=Build(M(typeof(Gate),"SetSequence"),28,typeof(Action<Gate,long>));Fixture.SequenceRoute=(Action<Gate,long>)setter.Call;
        var arm=Build(M(typeof(Gate),"Arm"),11,typeof(Action<Gate,long,double>));Fixture.ArmRoute=(Action<Gate,long,double>)arm.Call;
        var due=Build(M(typeof(Gate),"ReadDue"),27,typeof(Func<Gate,double>));Fixture.DueRoute=(Func<Gate,double>)due.Call;
        var tick=Build(M(typeof(Gate),"Tick"),12,typeof(Func<Gate,double,bool,Status>),true);Fixture.TickRoute=(Func<Gate,double,bool,Status>)tick.Call;
        var pending=Build(M(typeof(Fixture),"Pending"),13,typeof(Func<bool>));Fixture.PendingRoute=(Func<bool>)pending.Call;
        var parse=Build(M(typeof(Fixture),"ParsePostfix"),14,typeof(Action<Script,Gate,bool>));var parseCall=(Action<Script,Gate,bool>)parse.Call;
        var branch=Build(M(typeof(Fixture),"Branch"),25,typeof(Func<bool,bool>));var branchCall=(Func<bool,bool>)branch.Call;
        var filtered=Build(M(typeof(Fixture),"Filtered"),26,typeof(Action));var filteredCall=(Action)filtered.Call;
        var decide=Build(M(typeof(Fixture),"Decide"),15,typeof(Action<Gate>));var decideCall=(Action<Gate>)decide.Call;
        Case("actual constructor result and list-add operands",()=>{var s=new Script();var g=new Gate();parseCall(s,g,false);var ctor=One(CompletionFactKind.Constructed);var add=One(CompletionFactKind.ListAdded);Need(ReferenceEquals(ctor.EffectiveValue,s.Items.Single())&&ReferenceEquals(add.Receiver,s.Items)&&ReferenceEquals(add.Arguments.Single(),ctor.EffectiveValue),"ctor/list identities differ");Need(ctor.Original==null&&add.Original==null,"effective-only construction/list forged original");Need((bool)ctor.Invocation.Entry.Arguments[2]! ==false,"upstream __runOriginal changed");});
        Case("Arm actual original operands and fields",()=>{var g=new Gate();parseCall(new(),g,true);var f=One(CompletionFactKind.GateArm);Need(f.Original?.OriginalReturned==true&&ReferenceEquals(f.Original.Instance,g)&&(long)f.Original.Arguments[0]! ==7&&(double)f.Original.Arguments[1]! ==2,"Arm original missing");Need(g.Sequence==7&&g.Due==2,"business Arm changed");Need(f.Original!.BodyFacts.Count(x=>x.Kind==CompletionFactKind.FieldWritten)==1,"actual field writes missing");Need(facts.Live.Contains(f)&&!f.HasQualifiedOriginalReturn&&!f.HasQualifiedNormalOriginalReturn&&!f.Original.NormalProviderPublished,"live/historical/normal qualification confused");});
        Case("original enum distinct from effective postfix result",()=>{Foreign.Mode="post";decideCall(new());var f=One(CompletionFactKind.GateTick);Need(f.Original?.ReturnValue is int v&&v==3&&f.EffectiveValue is int x&&x==5&&facts.Live.Contains(f),"enum original/effective collapse");});
        Case("prefix veto cannot fabricate original",()=>{Foreign.Mode="veto";decideCall(new());var f=One(CompletionFactKind.GateTick);Need(f.Original==null&&f.EffectiveValue is int x&&x==0&&!facts.Live.Contains(f),"veto original fabricated");});
        Case("same-target raw reentry cannot steal reserved original",()=>{Foreign.Mode="reenter";decideCall(new());var f=One(CompletionFactKind.GateTick);Need(f.Original?.OriginalReturned==true&&facts.Live.Contains(f)&&facts.All.Count(x=>x.Kind==CompletionFactKind.OriginalReturned&&x.Invocation.Entry.PlanId==12)==2,"reentry linkage differs");});
        Case("prefix mutated inputs remain distinct from caller inputs",()=>{Foreign.Mode="mutate";decideCall(new());var f=One(CompletionFactKind.GateTick);Need(f.Original?.Arguments[0] is double v&&v==99&&f.Arguments[1] is double x&&x==4,"actual original inputs not captured");});
        Case("query observed false distinct from missing query",()=>{decideCall(new());Need(One(CompletionFactKind.PendingQuery).EffectiveValue is false,"observed false missing");facts.All.Clear();Fixture.Query=false;decideCall(new());Need(!facts.All.Any(x=>x.Kind==CompletionFactKind.PendingQuery),"absent query fabricated");});
        Case("suppressed original exception has no original return",()=>{Foreign.Mode="suppress";decideCall(new(){Throws=true});var f=One(CompletionFactKind.GateTick);Need(f.Original is{OriginalReturned:false}&&!facts.Live.Contains(f),"suppression fabricated original return");});
        Case("throw preserves exception and cleans source scopes",()=>{Exception? caught=null;try{decideCall(new(){Throws=true});}catch(Exception e){caught=e;}Need(ReferenceEquals(caught,Gate.Failure)&&!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"exception identity/scopes changed");});
        Case("branch target enters source spill",()=>{Need(!branchCall(false)&&One(CompletionFactKind.PendingQuery).Original?.OriginalReturned==true,"branch skipped hooks");});
        Case("caught exception abandons token and next call still qualifies",()=>{Fixture.ThrowPending=true;filteredCall();Need(facts.All.Count(x=>x.Kind==CompletionFactKind.PendingQuery)==1&&One(CompletionFactKind.PendingQuery).Original?.OriginalReturned==true&&!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"caught token leaked");});
        Case("return field snapshots observe postcondition",()=>{parseCall(new(),new(),true);var f=One(CompletionFactKind.GateArm);Need(f.Original!.BodyFacts.Count(x=>x.Kind==CompletionFactKind.FieldRead&&x.SiteId>=65536)==2,"return field snapshots absent");});
        Case("Arm return does not imply intercepted setter ran",()=>{Foreign.Mode="setter-veto";var gate=new Gate();parseCall(new(),gate,true);var armFact=One(CompletionFactKind.GateArm);var setterFact=armFact.Original!.BodyFacts.Single(f=>f.Kind==CompletionFactKind.MemberCall);Need(armFact.Original.OriginalReturned&&setterFact.Original==null&&gate.Sequence==0&&armFact.Original.BodyFacts.Single(f=>f.SiteId==65536).EffectiveValue is long sequence&&sequence==0,"setter veto hidden by Arm success");});
        Case("nested getter remains linked through exact live root",()=>{decideCall(new());var prior=facts.Nested.Single();Need(!prior.IsRecordedWithinForOrdinaryControl(One(CompletionFactKind.GateTick)),"history retained live root");facts.All.Clear();facts.Nested.Clear();decideCall(new());Need(facts.Nested.Count==1&&!ReferenceEquals(prior,facts.Nested[0]),"nested getter tree absent");});
        Case("late public return hook cannot fabricate failed original",()=>{Foreign.Mode="replay-return";decideCall(new(){Throws=true});var f=One(CompletionFactKind.GateTick);Need(f.Original is{OriginalReturned:false}&&!f.Original.Invocation.ObservationReliable&&!facts.Live.Contains(f),"stale return hook forged original success");});
        Case("observer failure invalidates whole invocation",()=>{facts.Throw=true;try{decideCall(new());}finally{facts.Throw=false;}Need(facts.All.Any(x=>!x.Invocation.ObservationReliable)&&!CallerSourceHooks.HasChain&&CallerSourceHooks.CurrentSource==null,"observer failure leaked/fabricated evidence");});
        }catch(Exception e){failures++;checks.Add(new{name="setup",passed=false,error=e.ToString()});}
        var report=new{ObservationalBaseline=observationalBaseline,Runtime=Environment.Version.ToString(),Total=checks.Count,Failures=failures,Checks=checks,NativeExecuted=false,InstalledDetour=false,ProfilerExecuted=false,AAExecuted=false,ActualProviderExecuted=false};File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));GC.KeepAlive(retained);return failures==0?0:1;
    }
    sealed class Sink:ICompletionFactsSink{public readonly List<CompletionFact> All=new(),Live=new(),Nested=new();public bool Throw;public void OnFact(CompletionFact f){All.Add(f);if(f.HasQualifiedOriginalReturn)Live.Add(f);if(f.Kind==CompletionFactKind.GateTick&&f.Original?.OriginalReturned==true)foreach(var child in f.Original.BodyFacts){if(child.Kind==CompletionFactKind.GateDueRead&&child.IsRecordedWithinForOrdinaryControl(f))Nested.Add(child);Need(!child.HasQualifiedOriginalReturnWithin(f),"ordinary nested upgraded normal");}if(Throw)throw new Exception("observer sentinel");}public void OnInvocationExit(CompletionInvocation i,bool r){if(i.Entry.PlanId==12)LastTick=i.Frame;}}
}
public enum Status{Idle=0,Done=3,Effective=5}
public sealed class Gate{public long Sequence;public double Due;public bool Throws;public static readonly Exception Failure=new ApplicationException("original sentinel");public void SetSequence(long sequence){Sequence=sequence;}public void Arm(long sequence,double now){Fixture.CallSetSequence(this,sequence);Due=now;Fixture.SequenceObserved=Sequence;}public double ReadDue()=>Due;public Status Tick(double now,bool pending){if(Throws)throw Failure;return now>=Fixture.CallDue(this)&&!pending?Status.Done:Status.Idle;}}
public sealed class Wait{public float Duration;public Gate Gate;public Wait(float duration,Gate gate){Duration=duration;Gate=gate;}}
public sealed class Script{public readonly List<Wait> Items=new();}
public static class Fixture
{
    public static Action<Gate,long,double> ArmRoute=null!;public static Action<Gate,long> SequenceRoute=null!;public static long SequenceObserved;public static Func<Gate,double,bool,Status> TickRoute=null!;public static Func<bool> PendingRoute=null!;public static Func<Gate,double> DueRoute=null!;public static bool Query=true,ThrowPending,PendingValue;public static Status Result;
    public static void CallSetSequence(Gate gate,long sequence)=>SequenceRoute(gate,sequence);
    public static void CallArm(Gate gate,long sequence,double now)=>ArmRoute(gate,sequence,now);
    public static Status CallTick(Gate gate,double now,bool pending)=>TickRoute(gate,now,pending);
    public static double CallDue(Gate gate)=>DueRoute(gate);
    public static bool CallPending()=>PendingRoute();
    public static bool Pending(){if(ThrowPending)throw Gate.Failure;return false;}
    public static void ParsePostfix(Script script,Gate gate,bool __runOriginal){script.Items.Add(new Wait(1,gate));CallArm(gate,7,2);}
    public static bool Branch(bool condition){if(condition)return CallPending();return CallPending();}
    public static void Filtered(){try{CallPending();}catch(Exception)when(Filter()){ThrowPending=false;CallPending();}finally{Query=false;}}
    public static bool Filter()=>true;
    public static void Decide(Gate gate){PendingValue=Query&&CallPending();Result=CallTick(gate,4,PendingValue);}
}
public static class Foreign
{
    public static string Mode="plain";[ThreadStatic]static bool nested;
    public static bool Prefix(Gate __instance,ref double now,bool pending){if(nested)return true;if(Mode=="veto")return false;if(Mode=="mutate")now=99;if(Mode=="reenter"){nested=true;try{Fixture.TickRoute(__instance,now,pending);}finally{nested=false;}}return true;}
    public static void Postfix(ref Status __result){if(Mode=="post")__result=Status.Effective;}
    public static Exception? Finalizer(Exception? __exception){if(Mode=="replay-return"){CallerSourceHooks.ReturnedValue(Program.LastTick!,3);return null;}return Mode=="suppress"?null:__exception;}
}





public static class ForeignSetter{public static bool Prefix()=>Foreign.Mode!="setter-veto";}


