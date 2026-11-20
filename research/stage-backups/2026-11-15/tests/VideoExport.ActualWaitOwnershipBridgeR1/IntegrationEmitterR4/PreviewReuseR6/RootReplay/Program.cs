using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AAStoryAdmissionR4;
using NativeStoryR3;
using Aave.WaitOwnership.Integration;
using Aave.ActualWaitOwnership.Lifetime;
using Aave.WaitOwnership.CallerSource;
using Aave.CapturedOperation.V1;
using Aave.CapturedOperation.Core;

internal static class Program
{
 internal static WaitOwnership Owner=null!;internal static SourceData Source=null!;
 [ThreadStatic]static ActualEntryCall? currentNative;
 static readonly List<object> results=new();static int failed;
 public static void Marker(){}
 static void Need(bool v,string message){if(!v)throw new InvalidOperationException(message);}
 static void Test(string name,Action action){try{action();results.Add(new{Name=name,Passed=true});}catch(Exception e){failed++;results.Add(new{Name=name,Passed=false,Error=e.ToString()});Source.OnStamp=null;Source.OnFailure=null;}}
 static int Main(string[] args)
 {
  if(args.Length!=1||File.Exists(args[0]))throw new ArgumentException("Fresh output required");
  if(Environment.Version!=new Version(6,0,36))throw new InvalidOperationException("ordinary636 only");
  foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())if(((string)e.Key).StartsWith("MONOMOD",StringComparison.OrdinalIgnoreCase)||((string)e.Key).StartsWith("CORECLR_",StringComparison.OrdinalIgnoreCase)||((string)e.Key).StartsWith("COR_",StringComparison.OrdinalIgnoreCase)||(string)e.Key=="DOTNET_STARTUP_HOOKS")throw new InvalidOperationException("runtime override present");
  Source=new();Owner=new(Source.Producer,Source,new StrongProvider(),new FieldReader(),new WeakProvider());CallerSourceHooks.Bind(Owner);
  var observations=new List<object>();
  Test("new unowned preview control",()=>{
   var a=new Animation(new Player());a.Start();var fresh=a.Player.Last!;fresh.Move(true);
   observations.Add(new{Case="new-unowned",a.Unlocks,a.Player.Stops});
   Need(a.Unlocks==1&&a.Player.Stops==1,"new unowned preview failed");
  });
  Test("same retired object new unowned iterator preserves preview while old iterator stays refused",()=>{
   var a=Source.Make();a.Start();var old=a.Player.Last!;var oldHandle=a.Curr;
   Need(Owner.Inspect(a) is {Committed:true,Retired:false},"initial owned arm missing");
   Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();
   Need(!Source.IsKnownReference(a),"fixture did not clear source admission");
   a.Reset();a.Start();var fresh=a.Player.Last!;
   Need(!ReferenceEquals(fresh,old)&&!ReferenceEquals(a.Curr,oldHandle),"fresh original work did not start");
   int before=a.Unlocks;fresh.Move(true);int afterFresh=a.Unlocks;old.Move(true);int afterOld=a.Unlocks;
   observations.Add(new{Case="same-instance-unowned-restart",Before=before,AfterFresh=afterFresh,AfterOld=afterOld,FreshIteratorDistinct=!ReferenceEquals(fresh,old),Epoch=Owner.Inspect(a),a.Player.Stops});
   Need(afterFresh==before+1,"fresh unowned preview was suppressed by a retired epoch");
   Need(afterOld==afterFresh,"old iterator advanced the new preview");
  });
  var report=new{Runtime=Environment.Version.ToString(),ProcessId=Environment.ProcessId,Observations=observations,Checks=results,Failures=failed,Owner.CommittedArms,Owner.FailedArms,Owner.ConsumedReleases,Owner.RetainedEpochs,ActualProducer=true,ProducerMode="ControlledModel",Boundary="same owner core; direct self-owned native-call facade; no real normal provider",NativeExecuted=false,NormalProviderExecuted=false,AAExecuted=false,ActualGuardOwnershipTransferred=false,Source.Errors};
  File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));return failed==0?0:1;
 }
 [MethodImpl(MethodImplOptions.NoInlining)]static Tuple<WeakReference,WeakReference> CreateRetired(nint address=0){var a=Source.Make();if(address!=0)a.Address=address;a.Start();var it=a.Player.Last!;var refs=Tuple.Create(new WeakReference(a),new WeakReference(it));Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();return refs;}
 static void RawRelease(Animation a){var f=CallerSourceHooks.Enter(2,1,a.Player,a,"normal");try{if(CallerSourceHooks.AllowOriginal(f)){a.Player.Stop(a.Curr);a.Curr=null;a.Complete();CallerSourceHooks.Returned(f);}}finally{CallerSourceHooks.Exit(f);}}
 internal static T Native<T>(string label,object receiver,object?[] args,Func<T> body,nint address=0)
 {
  var method=typeof(Program).GetMethod(nameof(Marker))!;var parent=currentNative;var call=new ActualEntryCall(TargetCatalog.Entries.Single(t=>t.Label==label),method,receiver,args,parent,Environment.CurrentManagedThreadId);currentNative=call;
  using var lease=Owner.Enter(call);try{var d=lease.BeforeOriginal(new(method,1,1,receiver,args,IntPtr.Zero,address,IntPtr.Zero));T result=default!;if(d!=OriginalCallDecision.Deny){result=body();lease.OriginalReturned(new(method,1,1,typeof(T),result));}lease.ChainExited(ManagedChainExit.Returned);return result;}catch{lease.ChainExited(ManagedChainExit.Faulted);throw;}finally{currentNative=parent;}
 }
 internal sealed class SourceData:ICapturedOperandSource
 {
  internal readonly CapturedOperationProducer Producer=new(new Reject());internal readonly Dictionary<Animation,CapturedArmSource> Mapping=new();internal Player Player=new();internal CaptureHandle Capture;internal Action? OnStamp,OnFailure,OnCancelResolve,OnRetire;internal int RetireCalls;internal readonly List<string> Errors=new();CapturedArmSource? cancellation;
  AuthorityStamp stamp=new(Guid.NewGuid(),1,1,1,1,0,Environment.CurrentManagedThreadId,1,true,true,false,false,false);readonly CompletionCodePlan plan;
  internal SourceData(){var m=typeof(Program).GetMethod(nameof(Marker))!;var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.Module.Assembly.Location)));var id=new CodeIdentity(hash,m.Module.ModuleVersionId,m.MetadataToken,hash,1);plan=new(hash,new(id,0),new(id,1),new(id,2),new(id,3),new[]{new CodeSite(id,4)},true,CompletionContract.Epoch,"self-owned-engine-lineage");if(!Producer.RegisterCompletionPlan(plan,new Verifier(plan),out var why))throw new Exception(why);Capture=Producer.BindCapture(new(Player,stamp,IssuerMode.ControlledModel,ContractIdentity.PolicySha256));}
  internal Animation Make(){var a=new Animation(Player);Register(a);return a;}
  internal void Register(Animation a){a.Player=Player;var s=Producer.EnterStory(new(Player,false,null,"AdvanceScenario",true,true,1),stamp);var op=Producer.ObserveParse(s,new(Player,new object(),"controlled original parse",a,1,true),stamp);Producer.ReportOutcome(s,new(WholeCallOutcome.Returned,true,false,true,"controlled-original"));Producer.CloseStory(s);var actor=Producer.BindActor(op,new(Player,a,0,a,a,plan.ActorLineage,LifetimeQualification.ManagedObjectIdentity,true),stamp);if(op.OperationId==0||actor.ActorId==0)throw new Exception("invalid controlled producer setup");Mapping[a]=new(op,actor,a,plan);}
  internal StoryScopeHandle RegisterPending(Animation a){a.Player=Player;var s=Producer.EnterStory(new(Player,false,null,"AdvanceScenario",true,true,1),stamp);var op=Producer.ObserveParse(s,new(Player,new object(),"controlled pending parse",a,1,true),stamp);var actor=Producer.BindActor(op,new(Player,a,0,a,a,plan.ActorLineage,LifetimeQualification.ManagedObjectIdentity,true),stamp);if(op.OperationId==0||actor.ActorId==0)throw new Exception("pending setup invalid");Mapping[a]=new(op,actor,a,plan);return s;}
  internal void CommitPending(StoryScopeHandle s){Producer.ReportOutcome(s,new(WholeCallOutcome.Returned,true,false,true,"controlled-source-complete"));Producer.CloseStory(s);}
  internal void ResetCapture(){Producer.RetireCapture(Capture,"explicit controlled capture end");Mapping.Clear();Player=new();stamp=stamp with{Generation=stamp.Generation+1,PublicationRevision=stamp.PublicationRevision+1};Capture=Producer.BindCapture(new(Player,stamp,IssuerMode.ControlledModel,ContractIdentity.PolicySha256));}
  public bool TryResolveArm(object p,object a,out CapturedArmSource? value){value=null;return ReferenceEquals(p,Player)&&a is Animation anim&&Mapping.TryGetValue(anim,out value);}
  public AuthorityStamp ReadStamp(){OnStamp?.Invoke();return stamp;}
  public bool TryResolveRelease(object e,object? p,CapturedArmSource s,out TerminalKind kind){kind=TerminalKind.None;return false;}
  public bool TryResolveCancellation(object a,out CapturedArmSource? value){value=cancellation;OnCancelResolve?.Invoke();return value!=null&&ReferenceEquals(value.ActualGate,a);}
  public bool IsKnownReference(object a){lock(Mapping)return a is Animation x&&Mapping.ContainsKey(x);}
  public void RetireOperation(OperationHandle o,string r){RetireCalls++;Producer.RetireOperation(o,r);foreach(var a in Mapping.Where(x=>x.Value.Operation==o).Select(x=>x.Key).ToArray())Mapping.Remove(a);OnRetire?.Invoke();}
  public void Failure(OperationHandle o,string r){Errors.Add(r);OnFailure?.Invoke();}
  internal IDisposable CancelScope(Animation a){var prior=cancellation;cancellation=Mapping[a];return new ActionScope(()=>cancellation=prior);}
  sealed class Reject:IFamilyCertificateVerifier{public bool Verify(FamilyCertificate c,out string r){r="";return false;}}
  sealed class Verifier(CompletionCodePlan expected):ICompletionPlanVerifier{public bool Verify(CompletionCodePlan p,out string r){r="";return p==expected;}}
 }
 sealed class ActionScope(Action end):IDisposable{public void Dispose()=>end();}
 sealed class EmptyScope:IDisposable{public void Dispose(){}}
 sealed class StoryOwner(object expected):IExportStoryOwner,IKnownStoryOperand{public IDisposable? Enter(StoryMethod m,object p,bool preview,object? script)=>new EmptyScope();public void BoundaryFailed(Exception e){}public bool IsKnownStoryOperand(object p)=>ReferenceEquals(p,expected);}
 sealed class ContextSignals:IDisposable,IOriginalCallBoundary,IOriginalCallReturnObserver,IManagedBodyObserver,IManagedChainExitObserver
 {internal int Before,Returns,Bodies,Exits,Disposals;public OriginalCallDecision BeforeOriginal(OriginalCallRequest r){Before++;return OriginalCallDecision.PreserveOriginal;}public void ObserveOriginalReturn(OriginalCallReturn r)=>Returns++;public void ObserveBody(ManagedBodyObservation b)=>Bodies++;public void ObserveChainExit(ManagedChainExit o,Exception? e)=>Exits++;public void ObservationFailed(Exception e){}public void Dispose()=>Disposals++;}
 sealed class StrongProvider:ILifetimeProvider{public bool TryRetain(object v,out ILifetimeLease? l,out string r){l=new Strong(v);r="";return true;}sealed class Strong(object v):ILifetimeLease{object? value=v;public object Key{get;}=new();public bool IsAlive=>value!=null;public bool Matches(object x)=>ReferenceEquals(value,x);public void Dispose()=>value=null;}}
 sealed class WeakProvider:IWeakEpochProvider{public IWeakEpochIdentity RetainWeak(object v)=>new Weak(v);sealed class Weak(object v):IWeakEpochIdentity{WeakReference<object>? value=new(v);public bool IsAlive=>value?.TryGetTarget(out _)==true;public bool Matches(object x)=>value?.TryGetTarget(out var actual)==true&&ReferenceEquals(x,actual);public void Dispose()=>value=null;}}
 sealed class FieldReader:IWaitFieldReader{internal static int Reads;public bool TryAnimation(object a,out AnimationFields? f){Reads++;f=a is Animation x?new(x.Player,x.Curr,x.Started,x.Completed,x.Cancelled):null;return f!=null;}public bool TryIteratorOwner(object i,out object? a){a=(i as Iterator)?.Animation;return a!=null;}}
}
public sealed class Handle{}
public sealed class Player
{
 public int Stops;public bool FailStart,FailStop;public readonly ApplicationException Error=new("owned-engine-failure");public Action<Iterator>? Inline;public Iterator? Last;
 public Handle Start(Iterator i)=>Program.Native("MonoBehaviour.StartCoroutine(IEnumerator)",this,new object?[]{i},()=>{Last=i;Inline?.Invoke(i);if(FailStart)throw Error;return new Handle();});
 public void Stop(Handle? h){Program.Native("MonoBehaviour.StopCoroutine(Coroutine)",this,new object?[]{h},()=>{if(FailStop)throw Error;if(h!=null)Stops++;return 0;});}
}
public sealed class Animation
{
 static long next=100;public nint Address=(nint)Interlocked.Increment(ref next);public Player Player;public Handle? Curr;public bool Started,Completed,Cancelled;public int FactoryCount,Unlocks;
 public Animation(Player p){Player=p;}
 public void Reset(){Curr=null;Started=Completed=Cancelled=false;}
 public void Start(){Program.Native("Wait.Start",this,Array.Empty<object?>(),()=>{Curr=Player.Start(new Iterator(this));Started=true;return 0;},Address);}
 public void Complete(){Program.Native("Wait.Complete",this,new object?[]{false},()=>{Player.Stop(Curr);Unlocks++;Completed=true;return 0;},Address);}
 public void Cancel(){Program.Native("ScenarioAnimation.Cancel",this,new object?[]{false},()=>{Player.Stop(Curr);Cancelled=true;return 0;},Address);}
}
public sealed class Iterator
{
 static long next=10000;public readonly nint Address=(nint)Interlocked.Increment(ref next);public readonly Animation Animation;public Iterator(Animation a){Animation=a;}
 public bool Move(bool terminal)=>Program.Native("Wait.Iterator.MoveNext",this,Array.Empty<object?>(),()=>{if(terminal)Animation.Complete();return !terminal;},Address);
}
