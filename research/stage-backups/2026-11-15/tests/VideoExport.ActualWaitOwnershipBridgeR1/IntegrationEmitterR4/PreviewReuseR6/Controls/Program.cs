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
  Test("actual product owner attachment remains unavailable",()=>Need(!Owner.CanAdmitActualOwnership&&!ActualWaitObservation.ActualGuardOwnershipTransferred,"actual owner gate opened"));
  Test("actual observation lease forwards original source context facts",()=>{
   var context=new ContextSignals();var method=typeof(Program).GetMethod(nameof(Marker))!;var call=new ActualEntryCall(TargetCatalog.Entries.Single(x=>x.Label=="Test.AdvanceScenario"),method,new object(),Array.Empty<object?>(),null,Environment.CurrentManagedThreadId);
   var type=typeof(ActualWaitObservation).GetNestedType("Lease",BindingFlags.NonPublic)!;var lease=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{call,null,context},null)!;
   ((IOriginalCallBoundary)lease).BeforeOriginal(new(method,1,1,call.Instance,Array.Empty<object?>(),(nint)1,(nint)2,IntPtr.Zero));((IOriginalCallReturnObserver)lease).ObserveOriginalReturn(new(method,1,1,typeof(void),null));((IManagedBodyObserver)lease).ObserveBody(new(1,1,true,true,true,true,true,true,"delegation-only",ManagedChainExit.Returned,6,1));((IManagedChainExitObserver)lease).ObserveChainExit(ManagedChainExit.Returned,null);lease.Dispose();Need(context.Before==1&&context.Returns==1&&context.Bodies==1&&context.Exits==1&&context.Disposals==1,"original source facts were swallowed by diagnostic wrapper");
  });
  Test("story joined lease preserves converted-original return interface",()=>{
   var context=new ContextSignals();var type=typeof(StoryAdmissionBridge).GetNestedType("JoinedLease",BindingFlags.NonPublic)!;using var lease=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{context,new EmptyScope()},null)!;
   var method=typeof(Program).GetMethod(nameof(Marker))!;(lease as IOriginalCallReturnObserver)?.ObserveOriginalReturn(new(method,1,1,typeof(void),null));Need(context.Returns==1,"joined story scope dropped original return");
  });
  Test("story foreign-thread policy preserves unknown full chain and refuses only known original",()=>{
   var player=new object();var owner=new StoryOwner(player);var raw=new EmptyScope();int reported=0;var unknown=ForeignThreadStoryPolicy.Enter(owner,new object(),raw,()=>reported++);Need(ReferenceEquals(raw,unknown)&&reported==0,"unknown story inherited export veto");
   using var known=ForeignThreadStoryPolicy.Enter(owner,player,new EmptyScope(),()=>reported++);Need(known is IOriginalCallBoundary&&reported==1,"known refusal dropped entire chain");bool refused=false;try{((IOriginalCallBoundary)known).BeforeOriginal(new(typeof(Program).GetMethod(nameof(Marker))!,1,1,player,Array.Empty<object?>(),(nint)1,(nint)2,IntPtr.Zero));}catch(InvalidOperationException){refused=true;}Need(refused,"known original not refused");
  });
  Test("initial inline scheduler binds actual iterator and returned handle",()=>{var a=Source.Make();int n=Owner.CommittedArms;a.Player.Inline=it=>{Need(Owner.CommittedArms==n,"committed before handle return");it.Move(false);};a.Start();a.Player.Inline=null;Need(Owner.Inspect(a) is {Committed:true,Retired:false}&&a.FactoryCount==0,"inline initial arm missing");});
  Test("initial arm waits for exact outer operation commit",()=>{var a=new Animation(Source.Player);int n=Owner.CommittedArms;Native("Test.AdvanceScenario",Source.Player,new object?[]{false,null},()=>{var pending=Source.RegisterPending(a);a.Start();Need(Owner.CommittedArms==n,"provisional operation was prematurely owned");Source.CommitPending(pending);return 0;});Need(Owner.CommittedArms==n+1&&Owner.Inspect(a) is {Committed:true,Retired:false},"initial arm was lost before original complete-chain commit");});
  Test("known admitted native reference on another thread is refused",()=>{var a=Source.Make();a.Start();int n=a.Unlocks;bool refused=Task.Run(()=>{try{a.Complete();return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult();Need(refused&&a.Unlocks==n,"known work escaped to raw");});
  Test("unknown native reference on another thread remains raw",()=>{var a=new Animation(new Player());Task.Run(a.Complete).GetAwaiter().GetResult();Need(a.Unlocks==1,"unknown raw changed");});
  Test("unknown Start preserves original without extra field observations",()=>{var a=new Animation(new Player());int n=FieldReader.Reads;a.Start();Need(a.Started&&FieldReader.Reads==n,"unknown Start received owner field reads");});
  Test("unknown coroutine sharing captured player remains raw on foreign thread",()=>{var a=Source.Make();a.Start();int n=a.Player.Stops;Task.Run(()=>a.Player.Stop(new Handle())).GetAwaiter().GetResult();Need(a.Player.Stops==n+1,"player identity incorrectly owned unrelated work");});
  Test("known coroutine handle on foreign thread is refused",()=>{var a=Source.Make();a.Start();var h=a.Curr;int n=a.Player.Stops;bool refused=Task.Run(()=>{try{a.Player.Stop(h);return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult();Need(refused&&a.Player.Stops==n,"known handle crossed thread");});
  Test("known managed Release on another thread is refused before Stop",()=>{var a=Source.Make();a.Start();int n=a.Player.Stops;bool refused=Task.Run(()=>{try{RawRelease(a);return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult();Need(refused&&a.Player.Stops==n&&a.Curr!=null,"known wrong-thread managed Release bypass");});
  Test("unknown managed Release on another thread remains original",()=>{var a=new Animation(new Player()){Curr=new Handle()};Task.Run(()=>RawRelease(a)).GetAwaiter().GetResult();Need(a.Curr==null&&a.Unlocks==1,"unknown caller changed");});
  Test("raw five-operand source cannot authorize known terminal",()=>{var a=Source.Make();a.Start();int n=a.Player.Stops;bool refused=false;try{RawRelease(a);}catch(InvalidOperationException){refused=true;}Need(refused&&a.Player.Stops==n&&a.Curr!=null,"observational source granted terminal");});
  Test("exact cancellation source consumes once without Unlock",()=>{var a=Source.Make();a.Start();int priorStops=a.Player.Stops;using(Source.CancelScope(a)){a.Cancel();a.Cancel();}Need(a.Cancelled&&a.Unlocks==0&&a.Player.Stops==priorStops+1&&Owner.Inspect(a) is {Consumed:true,StrongRootsReleased:true},"cancel permission/semantics wrong");});
  Test("terminal retirement releases exact source-owned actor roots once",()=>{var a=Source.Make();a.Start();int n=Source.RetireCalls;using(Source.CancelScope(a))a.Cancel();Need(Source.RetireCalls==n+1&&!Source.Mapping.ContainsKey(a),"source actor retirement API not consumed");});
  Test("source retirement failure cannot replace original Stop exception",()=>{var a=Source.Make();a.Start();int n=Source.RetireCalls;Source.OnRetire=()=>throw new ApplicationException("source cleanup sentinel");a.Player.FailStop=true;bool original=false;try{using(Source.CancelScope(a))a.Cancel();}catch(ApplicationException e){original=ReferenceEquals(e,a.Player.Error);}finally{Source.OnRetire=null;a.Player.FailStop=false;}Need(original&&Source.RetireCalls==n+1&&Owner.Inspect(a) is {Failed:true,StrongRootsReleased:true},"retirement masked original or left roots alive");});
  Test("lost producer binding closes known ownership and source roots",()=>{var a=Source.Make();a.Start();using(Source.CancelScope(a)){Source.Producer.RetireOperation(Source.Mapping[a].Operation,"original authority invalidation");a.Cancel();}Need(!a.Cancelled&&Owner.Inspect(a) is {Failed:true,StrongRootsReleased:true},"lost authority retained active ownership");});
  Test("source stamp failure closes capture instead of leaving guarded work active",()=>{var a=Source.Make();a.Start();Source.OnStamp=()=>{Source.OnStamp=null;throw new ApplicationException("source stamp sentinel");};using(Source.CancelScope(a))a.Cancel();Need(!a.Cancelled&&Owner.Inspect(a) is {Failed:true,StrongRootsReleased:true},"stamp failure not closed");});
  Test("old cancellation source cannot cancel reexport epoch",()=>{var a=Source.Make();a.Start();using(Source.CancelScope(a)){Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();Source.Register(a);a.Reset();a.Start();a.Cancel();Need(!a.Cancelled&&Owner.Inspect(a) is {Committed:true,Retired:false},"old cancel borrowed new capture");}});
  Test("cancellation resolver reentry cannot stop new capture or lose exception boundary",()=>{var a=Source.Make();a.Start();using(Source.CancelScope(a)){Source.OnCancelResolve=()=>{Source.OnCancelResolve=null;Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();Source.Register(a);a.Reset();a.Start();};a.Cancel();Need(!a.Cancelled&&a.Player.Stops==0&&Owner.Inspect(a) is {Committed:true,Retired:false},"old resolver consumed new capture");}});
  Test("unproven direct Cancel of known work cannot become current source",()=>{var a=Source.Make();a.Start();a.Cancel();Need(!a.Cancelled&&a.Player.Stops==0,"direct cancel invented operation");});
  Test("retired iterator cannot stop or unlock a later epoch",()=>{var a=Source.Make();a.Start();var old=a.Player.Last!;Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();Source.Register(a);a.Reset();a.Start();var current=a.Curr;int stops=a.Player.Stops;old.Move(true);Need(a.Unlocks==0&&a.Player.Stops==stops&&ReferenceEquals(a.Curr,current),"stale iterator affected later epoch");});
  Test("scheduler exception retains exact error and failed tombstone",()=>{var a=Source.Make();a.Player.FailStart=true;bool same=false;try{a.Start();}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}a.Player.FailStart=false;Need(same&&Owner.Inspect(a) is {Failed:true,StrongRootsReleased:true},"failed arm lost failure ownership");});
  Test("End during producer stamp prevents pending commit",()=>{var a=Source.Make();int n=Owner.CommittedArms;Source.OnStamp=()=>{Source.OnStamp=null;Owner.EndCapture(Source.Capture.CaptureId);};a.Start();Need(Owner.CommittedArms==n&&Owner.Inspect(a) is {Failed:true,StrongRootsReleased:true},"ended pending arm committed");});
  Test("nested replacement before old bind does not get overwritten",()=>{Source.ResetCapture();var a=Source.Make();var oldOp=Source.Mapping[a].Operation;Source.OnStamp=()=>{Source.OnStamp=null;Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();Source.Register(a);a.Reset();a.Start();};a.Start();var view=Owner.Inspect(a);Need(view is {Committed:true,Retired:false}&&view.OperationId!=oldOp.OperationId,"old unwind overwrote new committed epoch");});
  Test("strong roots retire at End independently of method registration",()=>{var a=Source.Make();a.Start();Owner.EndCapture(Source.Capture.CaptureId);Need(Owner.Inspect(a) is {Retired:true,StrongRootsReleased:true},"End retained strong epoch roots");Source.ResetCapture();});
  Test("weak tombstone dies only with independent object lifetime",()=>{var refs=CreateRetired();for(int i=0;i<6&&(refs.Item1.IsAlive||refs.Item2.IsAlive);i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}Need(!refs.Item1.IsAlive&&!refs.Item2.IsAlive,"controlled objects remain strongly rooted");Need(Owner.SweepRetired()>0,"dead tombstones not reclaimed");});
  Test("same native index after old object death does not become old ownership",()=>{var refs=CreateRetired(991);for(int i=0;i<6&&(refs.Item1.IsAlive||refs.Item2.IsAlive);i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}var unknown=new Animation(new Player()){Address=991};Task.Run(unknown.Complete).GetAwaiter().GetResult();Need(unknown.Unlocks==1,"retired pointer index falsely denied different lifetime");Owner.SweepRetired();});
  Test("same retired allocation fresh unowned run completes; old iterator before and after stays denied",()=>{
   var a=RetiredAnimation(out var old);old.Move(true);Need(a.Unlocks==0,"old epoch escaped before fresh start");
   a.Reset();a.Start();var fresh=a.Player.Last!;var handle=a.Curr;int stops=a.Player.Stops;
   old.Move(true);Need(a.Unlocks==0&&a.Player.Stops==stops&&ReferenceEquals(a.Curr,handle),"old epoch affected fresh handle");
   fresh.Move(true);Need(a.Unlocks==1&&a.Player.Stops==stops+1,"fresh unowned completion suppressed");
   old.Move(true);Need(a.Unlocks==1&&a.Player.Stops==stops+1,"old epoch escaped after fresh completion");
  });
  Test("retired allocation without observed new Start remains guarded",()=>{var a=RetiredAnimation(out var old);a.Complete();old.Move(true);Need(a.Unlocks==0&&a.Player.Stops==0,"End granted blanket raw permission");});
  Test("inline fresh unowned terminal retains original behavior while old inline work stays denied",()=>{
   var a=RetiredAnimation(out var old);a.Reset();Iterator? fresh=null;
   a.Player.Inline=it=>{fresh=it;old.Move(true);Need(a.Unlocks==0,"old inline escaped");it.Move(true);Need(a.Unlocks==1&&a.Player.Stops==0,"fresh inline terminal not preserved before handle return");};
   try{a.Start();}finally{a.Player.Inline=null;}Need(fresh!=null&&a.Curr!=null&&a.Started&&a.Completed,"inline original state changed");old.Move(true);Need(a.Unlocks==1&&a.Player.Stops==0,"old inline later affected returned handle");
  });
  Test("failed unowned Start preserves exact error and cannot bless its escaped iterator",()=>{
   var a=RetiredAnimation(out var old);a.Reset();a.Player.FailStart=true;bool same=false;try{a.Start();}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}finally{a.Player.FailStart=false;}
   var failedIterator=a.Player.Last!;Need(same&&!a.Started&&a.Curr==null,"failed Start semantics changed");failedIterator.Move(true);old.Move(true);Need(a.Unlocks==0,"failed Start granted late completion");
   a.Reset();a.Start();var fresh=a.Player.Last!;failedIterator.Move(true);Need(a.Unlocks==0,"failed iterator borrowed later handle");fresh.Move(true);Need(a.Unlocks==1,"fresh recovery blocked by failed unowned Start");
  });
  Test("inline unowned terminal then scheduler throw preserves both side effect and original error",()=>{
   var a=RetiredAnimation(out var old);a.Reset();a.Player.Inline=it=>it.Move(true);a.Player.FailStart=true;bool same=false;
   try{a.Start();}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}finally{a.Player.FailStart=false;a.Player.Inline=null;}
   var failedIterator=a.Player.Last!;Need(same&&a.Unlocks==1&&a.Curr==null,"inline effect or original throw changed");failedIterator.Move(true);old.Move(true);Need(a.Unlocks==1,"failed inline iterator received post-exit permission");
  });
  Test("nested unowned Starts follow actual final handle store without reviving inner or old iterator",()=>{
   var a=RetiredAnimation(out var old);a.Reset();Iterator? outer=null,inner=null;Handle? innerHandle=null;
   a.Player.Inline=it=>{outer=it;a.Player.Inline=null;a.Reset();a.Start();inner=a.Player.Last;innerHandle=a.Curr;};a.Start();
   Need(outer!=null&&inner!=null&&!ReferenceEquals(outer,inner)&&!ReferenceEquals(innerHandle,a.Curr),"reentrant fixture did not write outer handle last");
   int stops=a.Player.Stops;old.Move(true);inner!.Move(true);Need(a.Unlocks==0&&a.Player.Stops==stops,"stale inner or old iterator borrowed outer handle");outer!.Move(true);Need(a.Unlocks==1&&a.Player.Stops==stops+1,"actual outer final-store run was lost");
  });
  Test("failed outer unowned Start leaves successful nested handle independently usable",()=>{
   var a=RetiredAnimation(out var old);a.Reset();Iterator? outer=null,inner=null;Handle? innerHandle=null;bool same=false;
   a.Player.Inline=it=>{outer=it;a.Player.Inline=null;a.Reset();a.Start();inner=a.Player.Last;innerHandle=a.Curr;a.Player.FailStart=true;};
   try{a.Start();}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}finally{a.Player.FailStart=false;a.Player.Inline=null;}
   Need(same&&outer!=null&&inner!=null&&ReferenceEquals(a.Curr,innerHandle),"nested successful store missing");outer!.Move(true);old.Move(true);Need(a.Unlocks==0,"failed outer borrowed inner handle");inner!.Move(true);Need(a.Unlocks==1,"outer unwind revoked successful inner original work");
  });
  Test("unowned run then same allocation re-admission has independent producer epoch",()=>{
   var a=RetiredAnimation(out var old);a.Reset();a.Start();var preview=a.Player.Last!;int n=Owner.CommittedArms;
   Source.Register(a);a.Reset();a.Start();var current=a.Curr;Need(Owner.CommittedArms==n+1&&Owner.Inspect(a) is {Committed:true,Retired:false},"same allocation was not independently admitted");
   old.Move(true);preview.Move(true);Need(a.Unlocks==0&&a.Player.Stops==0&&ReferenceEquals(a.Curr,current),"old owned or unowned iterator borrowed re-admitted handle");using(Source.CancelScope(a))a.Cancel();Need(a.Cancelled&&a.Player.Stops==1&&a.Unlocks==0,"new exact producer cancellation blocked");
  });
  Test("second unowned run denies first unowned continuation and permits its own",()=>{
   var a=RetiredAnimation(out var old);a.Reset();a.Start();var prior=a.Player.Last!;a.Reset();a.Start();var latest=a.Player.Last!;
   int beforeStops=a.Player.Stops;prior.Move(true);old.Move(true);Need(a.Unlocks==0&&a.Player.Stops==beforeStops,"prior unowned run borrowed latest handle");latest.Move(true);Need(a.Unlocks==1&&a.Player.Stops==beforeStops+1,"latest unowned run suppressed");
  });
  Test("unobserved iterator cannot impersonate observed fresh unowned run",()=>{var a=RetiredAnimation(out _);a.Reset();a.Start();var exact=a.Player.Last!;new Iterator(a).Move(true);Need(a.Unlocks==0,"unobserved iterator borrowed run");exact.Move(true);Need(a.Unlocks==1,"exact observed iterator suppressed");});
  Test("fresh unowned iterator cannot borrow a changed handle",()=>{var a=RetiredAnimation(out _);a.Reset();a.Start();var fresh=a.Player.Last!;a.Curr=new Handle();fresh.Move(true);Need(a.Unlocks==0&&a.Player.Stops==0,"new run inferred handle identity from receiver");});
  Test("fresh unowned original terminal throw remains exact",()=>{var a=RetiredAnimation(out _);a.Reset();a.Start();var fresh=a.Player.Last!;a.Player.FailStop=true;bool same=false;try{fresh.Move(true);}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}finally{a.Player.FailStop=false;}Need(same&&a.Unlocks==0,"original unowned Stop error was suppressed or changed");});
  Test("new admission during unowned inline call prevents that old unowned continuation borrowing its handle",()=>{
   var a=RetiredAnimation(out var old);a.Reset();Iterator? preview=null;
   a.Player.Inline=it=>{preview=it;a.Player.Inline=null;Source.Register(a);a.Reset();a.Start();var h=a.Curr;it.Move(true);old.Move(true);Need(a.Unlocks==0&&a.Player.Stops==0&&ReferenceEquals(a.Curr,h),"old inline work touched re-admitted handle");};
   a.Start();preview!.Move(true);Need(a.Unlocks==0&&a.Player.Stops==0,"unowned outer return overrode new admission identity");
  });
  Test("retired plus observed unowned records retain no managed operands",()=>{
   var refs=CreateRetiredPreview();for(int i=0;i<6&&(refs.Item1.IsAlive||refs.Item2.IsAlive);i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}Need(!refs.Item1.IsAlive&&!refs.Item2.IsAlive,"unowned observations retained operands");Need(Owner.SweepRetired()>0,"independently dead retired identity not reclaimed");
  });

  Test("new iterator cannot reuse retired scheduler handle as a fresh unowned run",()=>{var a=RetiredAnimation(out var old);var handle=a.Curr;a.Reset();a.Player.ReturnHandle=handle;try{a.Start();var fresh=a.Player.Last!;fresh.Move(true);old.Move(true);Need(a.Unlocks==0,"retired handle relabeled as a fresh run");}finally{a.Player.ReturnHandle=null;}});
  Test("unowned observation cleanup failure does not replace original Start error",()=>{
   var a=RetiredAnimation(out _);a.Reset();a.Player.FailStart=true;a.Player.Inline=_=>WeakProvider.ThrowOnDispose=true;bool same=false;
   try{a.Start();}catch(ApplicationException e){same=ReferenceEquals(e,a.Player.Error);}finally{WeakProvider.ThrowOnDispose=false;a.Player.FailStart=false;a.Player.Inline=null;}
   Need(same,"weak observation cleanup replaced exact original scheduler exception");
  });
  var report=new{Runtime=Environment.Version.ToString(),ProcessId=Environment.ProcessId,Checks=results,Failures=failed,Owner.CommittedArms,Owner.FailedArms,Owner.ConsumedReleases,Owner.RetainedEpochs,ActualProducer=true,ProducerMode="ControlledModel",Boundary="same owner core; direct self-owned native-call facade; no real normal provider",NativeExecuted=false,NormalProviderExecuted=false,AAExecuted=false,ActualGuardOwnershipTransferred=false,Source.Errors};
  File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));return failed==0?0:1;
 }
 [MethodImpl(MethodImplOptions.NoInlining)]static Animation RetiredAnimation(out Iterator old){var a=Source.Make();a.Start();old=a.Player.Last!;Need(Owner.Inspect(a) is {Committed:true,Retired:false},"retired fixture missing initial ownership");Owner.EndCapture(Source.Capture.CaptureId);Source.ResetCapture();Need(!Source.IsKnownReference(a)&&Owner.Inspect(a) is {Retired:true,StrongRootsReleased:true},"retired fixture still admitted");return a;}
 [MethodImpl(MethodImplOptions.NoInlining)]static Tuple<WeakReference,WeakReference> CreateRetiredPreview(){var a=RetiredAnimation(out _);a.Reset();a.Start();var it=a.Player.Last!;it.Move(true);return Tuple.Create(new WeakReference(a),new WeakReference(it));}
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
 sealed class WeakProvider:IWeakEpochProvider{internal static bool ThrowOnDispose;public IWeakEpochIdentity RetainWeak(object v)=>new Weak(v);sealed class Weak(object v):IWeakEpochIdentity{WeakReference<object>? value=new(v);public bool IsAlive=>value?.TryGetTarget(out _)==true;public bool Matches(object x)=>value?.TryGetTarget(out var actual)==true&&ReferenceEquals(x,actual);public void Dispose(){value=null;if(ThrowOnDispose)throw new ApplicationException("controlled weak cleanup failure");}}}
 sealed class FieldReader:IWaitFieldReader{internal static int Reads;public bool TryAnimation(object a,out AnimationFields? f){Reads++;f=a is Animation x?new(x.Player,x.Curr,x.Started,x.Completed,x.Cancelled):null;return f!=null;}public bool TryIteratorOwner(object i,out object? a){a=(i as Iterator)?.Animation;return a!=null;}}
}
public sealed class Handle{}
public sealed class Player
{
 public int Stops;public bool FailStart,FailStop;public readonly ApplicationException Error=new("owned-engine-failure");public Action<Iterator>? Inline;public Iterator? Last;public Handle? ReturnHandle;
 public Handle Start(Iterator i)=>Program.Native("MonoBehaviour.StartCoroutine(IEnumerator)",this,new object?[]{i},()=>{Last=i;Inline?.Invoke(i);if(FailStart)throw Error;return ReturnHandle??new Handle();});
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
