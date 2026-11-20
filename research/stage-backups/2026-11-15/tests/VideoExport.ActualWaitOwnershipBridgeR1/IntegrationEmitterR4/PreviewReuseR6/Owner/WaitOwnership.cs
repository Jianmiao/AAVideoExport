using AAStoryAdmissionR4;
using NativeStoryR3;
using Aave.CapturedOperation.V1;
using Aave.ActualWaitOwnership.Lifetime;
using Aave.WaitOwnership.CallerSource;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace Aave.WaitOwnership.Integration;

// Shared by the actual selected-entry lease and ordinary controlled engine.
// This class neither calls Stop/Complete/Cancel nor changes original fields.
// Its only native-call action is an original-only boundary Permit/Deny.
public sealed partial class WaitOwnership : IActualWaitOwner, ICallerSourceSink
{
    readonly ICompletionAuthority authority;
    readonly ICapturedOperandSource source;
    readonly ILifetimeProvider lifetimes;
    readonly IWaitFieldReader fields;
    readonly IWeakEpochProvider weak;
    readonly ConditionalWeakTable<object,Epoch> references=new();
    readonly ConcurrentDictionary<nint,Epoch> nativeReferences=new();
    readonly ConcurrentDictionary<long,Epoch> knownEpochs=new();
    readonly int thread = Environment.CurrentManagedThreadId;
    readonly List<Epoch> epochs = new();
    readonly List<Epoch> deferredArms=new();
    readonly List<NativeCall> native = new();
    readonly List<SourceCall> callers = new();
    long sequence;
    public int CommittedArms { get; private set; }
    public int DeniedOriginals { get; private set; }
    public int ConsumedReleases { get; private set; }
    public int CleanupFailures { get; private set; }
    public int FailedArms { get; private set; }
    public sealed record EpochView(long Sequence,long CaptureId,long OperationId,bool Committed,bool Retired,bool Failed,bool Consumed,bool StrongRootsReleased){public TerminalKind Cause{get;init;}}
    public EpochView? Inspect(object animation){if(!OwnerThread)throw new InvalidOperationException("Inspection requires owner thread");var e=Current(animation);return e==null?null:new(e.Sequence,e.Operation.CaptureId,e.Operation.OperationId,e.Committed,e.Retired,e.Failed,e.Consumed,e.Released){Cause=e.Cause};}
    public int RetainedEpochs=>epochs.Count;
    // Product attachment remains disabled. The owner core is exercised directly
    // by controlled engine fixtures; it never turns a saved Root result into a
    // live source permit. Real permits come only from the current R4 chain.
    public bool CanAdmitActualOwnership => false;
    public string AdmissionBlocker => "Actual native-entry/lifetime/owner integration requires independent Root acceptance";
    public WaitOwnership(ICompletionAuthority authority, ICapturedOperandSource source,
        ILifetimeProvider lifetimes, IWaitFieldReader fields,IWeakEpochProvider weak)
    { this.authority=authority;this.source=source;this.lifetimes=lifetimes;this.fields=fields;this.weak=weak; }
    bool OwnerThread => Environment.CurrentManagedThreadId==thread;
    Epoch? Current(object animation) => epochs.Where(e=>e.Released?e.WeakAnimation?.Matches(animation)==true:e.Animation.Matches(animation)).MaxBy(e=>e.Sequence);
    Epoch? Iterator(object iterator) => epochs.Where(e=>e.Released?e.WeakIterator?.Matches(iterator)==true:e.Iterator?.Matches(iterator)==true).MaxBy(e=>e.Sequence);
    bool IsKnownReference(object value)=>references.TryGetValue(value,out _)||knownEpochs.Values.Any(e=>e.WeakAnimation?.Matches(value)==true||e.WeakIterator?.Matches(value)==true||e.WeakHandle?.Matches(value)==true)||source.IsKnownReference(value);
    void Remember(object value,Epoch e){references.Remove(value);references.Add(value,e);knownEpochs[e.Sequence]=e;}
    OriginalCallDecision Deny(){DeniedOriginals++;return OriginalCallDecision.Deny;}
    void Fault(Epoch e,string reason)
    {
        e.Failed=true;e.Retired=true;
        if(e.Cause==TerminalKind.None)e.Cause=TerminalKind.Failed;
        Retire(e,reason,true);
        if(e.Notified)return;e.Notified=true;
        try{source.Failure(e.Operation,reason);}catch{e.NotificationFailed=true;}
    }
    bool Live(Epoch e)
    {
        if(e.Retired||e.Failed||e.Consumed)return false;
        try
        {
            bool valid=e.Player.IsAlive&&e.Animation.IsAlive&&e.Iterator?.IsAlive==true&&e.Handle?.IsAlive==true&&
                fields.TryAnimation(e.AnimationOperand,out var f)&&f?.Handle!=null&&e.Player.Matches(f.Player)&&e.Handle.Matches(f.Handle)&&
                authority.ValidateCompletion(e.Binding,e.Source.ActualGate,source.ReadStamp(),out _);
            if(!valid){Fault(e,"known ownership lost original identity/field/producer validation");return false;}
            return !e.Retired&&!e.Released&&!e.Failed&&!e.Consumed;
        }
        catch(Exception error){Fault(e,"known ownership validation failed: "+error.GetType().Name);return false;}
    }
    Epoch? Begin(object player,object animation)
    {
        
        if(!OwnerThread)return null;
        SweepRetired();
        if(!source.TryResolveArm(player,animation,out var input)||input is null)
        {if(Current(animation) is{} known){Fault(known,"known animation lost its captured source");throw new InvalidOperationException("Known animation cannot return to unknown raw admission");}return null;}
        if(epochs.Count>=4096)throw new InvalidOperationException("Wait ownership retained-epoch bound reached; no unsafe reclamation");
        if(!lifetimes.TryRetain(player,out var p,out _)||p is null)return null;
        if(!lifetimes.TryRetain(animation,out var a,out _)||a is null){p.Dispose();return null;}
        Epoch e;
        try{e=new Epoch{Player=p,Animation=a,AnimationOperand=animation,WeakAnimation=weak.RetainWeak(animation),Source=input,Operation=input.Operation,Sequence=checked(++sequence),Previous=Current(animation)};}
        catch{p.Dispose();a.Dispose();throw;}
        Remember(animation,e);return e;
    }
    bool SameCurrent(Epoch e)=>ReferenceEquals(Current(e.AnimationOperand),e.Previous);
    Epoch? StartPending(object player,object animation)
    => Begin(player,animation);
    void IteratorSeen(Epoch e,object iterator)
    {
        if(e.Iterator!=null){if(!e.Iterator.Matches(iterator))e.Invalid=true;return;}
        if(!fields.TryIteratorOwner(iterator,out var owner)||owner==null||!e.Animation.Matches(owner)||
            !lifetimes.TryRetain(iterator,out var lease,out _)||lease is null){e.Invalid=true;return;}
        e.Iterator=lease;
        e.WeakIterator=weak.RetainWeak(iterator);Remember(iterator,e);
    }
    void HandleSeen(Epoch e,object? handle)
    {
        if(handle is null||e.Handle!=null||!lifetimes.TryRetain(handle,out var lease,out _)||lease is null){e.Invalid=true;return;}
        e.Handle=lease;e.WeakHandle=weak.RetainWeak(handle);Remember(handle,e);e.SchedulerReturned=true;
    }
    void Commit(Epoch e,object animation,object? handle,bool afterOuter=false)
    {
        if(e.Committed||e.Ended||e.Failed||e.Invalid||!e.SchedulerReturned||e.Iterator is null||e.Handle is null||
            handle is null||!e.Animation.Matches(animation)||!e.Handle.Matches(handle)||!SameCurrent(e))
        {Abort(e,"arm did not join exact source/scheduler/write ownership");return;}
        if(e.CommitBarrier!=null&&!afterOuter){if(!deferredArms.Contains(e))deferredArms.Add(e);return;}
        var b=authority.BindSuccessfulArm(e.Source.Operation,e.Source.Actor,e.Source.ActualGate,e.Sequence,e.Source.Plan,source.ReadStamp(),out var why);
        if(b.BindingId==0){Abort(e,"producer rejected successful arm: "+why);return;}
        // Producer callbacks may reenter/End. Publication checks the exact old
        // entry again, after the producer call, never only the receiver key.
        if(e.Ended||!SameCurrent(e)){authority.RetireCompletion(b,"arm superseded during producer bind");Abort(e,"arm superseded during producer bind");return;}
        e.Binding=b;e.Committed=true;
        if(e.Previous!=null)Retire(e.Previous,"exact successful replacement",false);
        e.Previous=null;
        epochs.Add(e);CommittedArms++;
    }
    void Abort(Epoch e,string reason)
    {
        if(e.Committed)return;
        deferredArms.Remove(e);
        if(!e.Failed){FailedArms++;Fault(e,reason);}
        // No Stop/Complete/rearm is invented on rollback. Original scheduling
        // happened; admission failed. The capture failure boundary owns recovery.
        if(!epochs.Contains(e))epochs.Add(e);
        Retire(e,reason,false);
    }
    public IActualWaitOwnerCall Enter(ActualEntryCall call)
    {
        if(!OwnerThread)return new WrongThreadCall(this,call);
        var frame=new NativeCall(this,call);native.Add(frame);return frame;
    }
    public ICallerSourceFrame? Enter(CallerSourceEntry entry)
    {
        if(!OwnerThread){if(entry.Animation!=null&&IsKnownReference(entry.Animation))throw new InvalidOperationException("Known admitted Release arrived on unsupported thread");return null;}
        var frame=new SourceCall(this,entry,CallerSourceHooks.CurrentSource);callers.Add(frame);return frame;
    }
    public void EndCapture(long captureId)
    {
        if(!OwnerThread)throw new InvalidOperationException("End requires owner thread");
        foreach(var f in native)if(f.Pending?.Operation.CaptureId==captureId)f.Pending.Ended=true;
        foreach(var f in callers)if(f.Pending?.Operation.CaptureId==captureId)f.Pending.Ended=true;
        foreach(var e in deferredArms.Where(e=>e.Operation.CaptureId==captureId).ToArray()){e.Ended=true;Abort(e,"capture End before original operation commit");}
        foreach(var e in epochs.Where(e=>e.Operation.CaptureId==captureId).ToArray())
        {e.Ended=true;Retire(e,"capture End",true);}
        SweepRetired();
    }
    void Retire(Epoch e,string reason,bool retireProducer)
    {
        e.Retired=true;
        bool notify=retireProducer&&!e.ProducerRetired;e.ProducerRetired|=retireProducer;
        bool newer=knownEpochs.Values.Any(x=>!ReferenceEquals(x,e)&&x.Sequence>e.Sequence&&x.Operation==e.Operation&&!x.Retired&&!x.Failed);
        if(!e.Released)
        {
            e.Released=true;
            foreach(var lease in new[]{e.Iterator,e.Handle,e.Player,e.Animation})
                try{lease?.Dispose();}catch(Exception error){CleanupFailure(e,error);}
            e.AnimationOperand=null!;e.Source=null!;e.Previous=null;
        }
        if(notify)
        {
            try{if(e.Binding.BindingId!=0)authority.RetireCompletion(e.Binding,reason);}catch(Exception error){CleanupFailure(e,error);}
            // Old unwind must not retire the same operation now owned by a
            // successful newer arm. The later arm will perform its own retirement.
            if(!newer)try{source.RetireOperation(e.Operation,reason);}catch(Exception error){CleanupFailure(e,error);}
        }
    }
    void CleanupFailure(Epoch e,Exception error)
    {CleanupFailures++;e.Failed=true;if(e.Notified)return;e.Notified=true;try{source.Failure(e.Operation,"exact source/resource retirement failed: "+error.GetType().Name);}catch{e.NotificationFailed=true;}}
    public void NotifySourceOperationRetired(OperationHandle operation)
    {
        if(!OwnerThread)throw new InvalidOperationException("Source retirement requires owner thread");
        foreach(var e in knownEpochs.Values.Where(e=>e.Operation==operation).ToArray())
        {e.ProducerRetired=true;if(!e.Committed)e.Ended=true;Retire(e,"original source retired operation",false);}
    }
    // Only independent native/managed GC identity death permits removal. Stop
    // return, callback return, End, and method unpatch are not drain receipts.
    public int SweepRetired()
    {
        if(!OwnerThread)throw new InvalidOperationException("Retirement sweep requires owner thread");
        SweepUnownedRuns();
        int count=0;
        foreach(var e in epochs.Where(e=>e.Released&&e.WeakAnimation?.IsAlive!=true&&e.WeakIterator?.IsAlive!=true&&e.WeakHandle?.IsAlive!=true).ToArray())
        {e.WeakAnimation?.Dispose();e.WeakIterator?.Dispose();e.WeakHandle?.Dispose();epochs.Remove(e);knownEpochs.TryRemove(e.Sequence,out _);foreach(var pair in nativeReferences.Where(x=>ReferenceEquals(x.Value,e)).ToArray())nativeReferences.TryRemove(pair.Key,out _);count++;}
        return count;
    }
    sealed class Epoch
    {
        internal ILifetimeLease Player=null!,Animation=null!;internal object AnimationOperand=null!;internal ILifetimeLease? Iterator,Handle;
        internal CapturedArmSource Source=null!;internal OperationHandle Operation;internal GateBinding Binding;internal long Sequence;
        internal TerminalKind Cause;
        internal ActualEntryCall? CommitBarrier;
        internal IWeakEpochIdentity? WeakAnimation,WeakIterator,WeakHandle;internal bool Released,ProducerRetired;
        internal Epoch? Previous;internal bool SchedulerReturned,Committed,Consumed,NativeConsumed,NativeReturned,Retired,Failed,Ended,Invalid,Notified,NotificationFailed;
    }
    sealed class PassiveCall:IActualWaitOwnerCall
    {public OriginalCallDecision BeforeOriginal(OriginalCallRequest r)=>OriginalCallDecision.PreserveOriginal;public void OriginalReturned(OriginalCallReturn r){}public void ChainExited(ManagedChainExit e){}public void ObservationFailed(Exception e){}public void Dispose(){}}
    sealed class WrongThreadCall(WaitOwnership owner,ActualEntryCall call):IActualWaitOwnerCall
    {
        public OriginalCallDecision BeforeOriginal(OriginalCallRequest r)
        {
            bool scheduler=call.Target.Label.StartsWith("MonoBehaviour.",StringComparison.Ordinal);
            bool story=call.Target.Label.StartsWith("Test.",StringComparison.Ordinal);
            bool known=scheduler?r.Arguments.Any(a=>a!=null&&owner.IsKnownReference(a)):!story&&r.Instance!=null&&owner.IsKnownReference(r.Instance);
            if(!scheduler&&!story&&!known&&r.NativeReceiver!=IntPtr.Zero&&owner.nativeReferences.TryGetValue(r.NativeReceiver,out var indexed))
            {
                // Pointer is an index only. Active strong roots prevent native
                // reuse; retired records need their independent weak identity.
                known=!indexed.Released||r.Instance!=null&&(indexed.WeakAnimation?.Matches(r.Instance)==true||indexed.WeakIterator?.Matches(r.Instance)==true);
            }
            if(known)throw new InvalidOperationException("Known admitted native operand reached a foreign thread: "+call.Target.Label);
            return OriginalCallDecision.PreserveOriginal;
        }
        public void OriginalReturned(OriginalCallReturn r){}public void ChainExited(ManagedChainExit o){}public void ObservationFailed(Exception e){}public void Dispose(){}
    }
    sealed class NativeCall:IActualWaitOwnerCall
    {
        readonly WaitOwnership w;internal readonly ActualEntryCall Call;internal Epoch? Pending,Continuation;Epoch? terminalAttempt;
        UnownedRun? unownedStart,unownedScheduler,unownedContinuation;
        bool entered,returned,disposed,outerReturned,outerFaulted;object? receiver;
        internal NativeCall(WaitOwnership w,ActualEntryCall call){this.w=w;Call=call;}
        internal bool IsUnownedScheduler(UnownedRun run)=>ReferenceEquals(unownedScheduler,run)&&entered&&!returned;
        public OriginalCallDecision BeforeOriginal(OriginalCallRequest r)
        {
            entered=true;receiver=r.Instance;
            if(receiver is null)return OriginalCallDecision.PreserveOriginal;
            string kind=Call.Target.Label;
            if(kind=="Wait.Start")
            {
                if(!w.source.IsKnownReference(receiver))
                {
                    // A fresh unknown allocation stays observation-free. Only
                    // an independently identified retired allocation needs a
                    // run witness to keep its old epoch from poisoning preview.
                    if(w.Current(receiver) is {Retired:true,Released:true} previous)
                        unownedStart=w.ObserveUnownedStart(receiver,previous);
                    return OriginalCallDecision.PreserveOriginal;
                }
                if(w.fields.TryAnimation(receiver,out var f)&&f is {Started:false,Completed:false,Cancelled:false})
                {Pending=w.StartPending(f.Player,receiver);if(Pending!=null){Pending.CommitBarrier=w.native.Take(w.native.Count-1).LastOrDefault(x=>x.Call.Target.Label=="Test.AdvanceScenario"&&x.entered&&!x.returned)?.Call;if(r.NativeReceiver!=IntPtr.Zero)w.nativeReferences[r.NativeReceiver]=Pending;}}
            }
            else if(kind=="MonoBehaviour.StartCoroutine(IEnumerator)"&&r.Arguments.Count==1&&r.Arguments[0] is{} iterator)
            {
                var parent=w.native.Take(w.native.Count-1).LastOrDefault(x=>x.entered&&x.Pending!=null);
                if(parent?.Pending is{} p&&p.Player.Matches(receiver)){Pending=p;w.IteratorSeen(p,iterator);}
                var originalStart=w.native.Take(w.native.Count-1).LastOrDefault(x=>ReferenceEquals(x.Call,Call.Parent));
                if(originalStart?.unownedStart is{} run&&run.Player.Matches(receiver))
                {unownedScheduler=run;w.ObserveUnownedIterator(run,iterator);}
            }
            else if(kind=="Wait.Iterator.MoveNext")
            {Continuation=w.Iterator(receiver)??w.native.Select(x=>x.Pending).Concat(w.callers.Select(x=>x.Pending)).LastOrDefault(e=>e?.Iterator?.Matches(receiver)==true);if(Continuation!=null){w.Remember(receiver,Continuation);if(r.NativeReceiver!=IntPtr.Zero)w.nativeReferences[r.NativeReceiver]=Continuation;}else unownedContinuation=w.UnownedIterator(receiver);}
            else if(kind is "Wait.Complete" or "ScenarioAnimation.Cancel")
            {
                // Only a caller-source Release transaction may spend its already
                // consumed permit at native Complete. Native raw timers never
                // acquire a new permit from the animation's current handle.
                var release=w.callers.LastOrDefault();
                if(kind=="Wait.Complete"&&release?.Terminal is{} terminal&&release.Allowed&&
                    ReferenceEquals(CallerSourceHooks.CurrentSource?.Target,release)&&
                    !terminal.Retired&&!terminal.Failed&&terminal.Animation.Matches(receiver)&&!terminal.NativeConsumed&&!terminal.Ended&&
                    ReferenceEquals(w.Current(receiver),terminal))
                {terminal.NativeConsumed=true;terminalAttempt=terminal;return OriginalCallDecision.Permit;}
                var continuation=w.native.Take(w.native.Count-1).LastOrDefault(x=>x.Call.Target.Label=="Wait.Iterator.MoveNext");
                if(kind=="Wait.Complete"&&continuation?.Continuation==null&&continuation?.unownedContinuation is{} run)
                    return w.PreserveUnownedTerminal(run,receiver)?OriginalCallDecision.PreserveOriginal:w.Deny();
                var known=continuation?.Continuation??w.Current(receiver);
                if(kind=="ScenarioAnimation.Cancel"&&known!=null&&w.Live(known))
                {
                    var exactActor=known.Source.Actor;
                    if(w.source.TryResolveCancellation(receiver,out var cancellation)&&cancellation!=null&&
                        cancellation.Operation==known.Operation&&cancellation.Actor==exactActor&&
                        ReferenceEquals(w.Current(receiver),known)&&w.Live(known)&&ReferenceEquals(w.Current(receiver),known))
                    {known.Cause=TerminalKind.Cancelled;known.Consumed=true;known.NativeConsumed=true;terminalAttempt=known;return OriginalCallDecision.Permit;}
                }
                if(known!=null)return w.Deny();
                // A terminal in a pending initial inline scheduler makes that
                // arm ineligible; it remains original behavior until committed.
                foreach(var f in w.native)if(f.Pending is{} p&&p.Animation.Matches(receiver))p.Invalid=true;
                foreach(var p in w.deferredArms)if(!p.Released&&p.Animation.Matches(receiver))p.Invalid=true;
            }
            return OriginalCallDecision.PreserveOriginal;
        }
        public void OriginalReturned(OriginalCallReturn result)
        {
            returned=true;
            if(terminalAttempt!=null){terminalAttempt.NativeReturned=true;if(Call.Target.Label=="ScenarioAnimation.Cancel")w.Retire(terminalAttempt,"original cancellation returned",true);}
            if(Call.Target.Label=="MonoBehaviour.StartCoroutine(IEnumerator)"&&Pending!=null)w.HandleSeen(Pending,result.Value);
            if(unownedScheduler!=null)w.ObserveUnownedHandle(unownedScheduler,result.Value);
            if(unownedStart!=null&&receiver!=null)w.UnownedStartReturned(unownedStart,receiver);
            if(Call.Target.Label=="Wait.Start"&&Pending!=null&&receiver!=null)
            {
                if(w.fields.TryAnimation(receiver,out var f)&&f is {Started:true,Completed:false,Cancelled:false}&&Pending.Player.Matches(f.Player))w.Commit(Pending,receiver,f.Handle);
                else w.Abort(Pending,"initial Start returned without exact successful field state");
            }
        }
        public void ChainExited(ManagedChainExit outcome)
        {outerReturned|=outcome==ManagedChainExit.Returned;outerFaulted|=outcome==ManagedChainExit.Faulted;if(outcome==ManagedChainExit.Faulted){if(unownedStart!=null)w.RemoveUnownedRun(unownedStart);if(unownedScheduler!=null)w.RemoveUnownedRun(unownedScheduler);if(Pending!=null){if(Pending.Committed)w.Fault(Pending,"foreign chain fault after arm");else w.Abort(Pending,"original or foreign chain arm fault");}}}
        public void ObservationFailed(Exception error){if(unownedStart!=null)w.RemoveUnownedRun(unownedStart);if(unownedScheduler!=null)w.RemoveUnownedRun(unownedScheduler);if(Pending!=null)w.Fault(Pending,"original-return observation failed: "+error.GetType().Name);}
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            if(unownedStart!=null){unownedStart.StartActive=false;w.DisposeUnownedIdentity(unownedStart.InitialHandle);unownedStart.InitialHandle=null;if(!returned||!outerReturned||outerFaulted||!unownedStart.Successful)w.RemoveUnownedRun(unownedStart);}
            foreach(var arm in w.deferredArms.Where(e=>ReferenceEquals(e.CommitBarrier,Call)).ToArray())
            {
                w.deferredArms.Remove(arm);arm.CommitBarrier=null;
                if(entered&&returned&&outerReturned&&!outerFaulted&&!arm.Ended&&!arm.Failed&&
                    w.fields.TryAnimation(arm.AnimationOperand,out var actual)&&actual is {Started:true,Completed:false,Cancelled:false}&&arm.Player.Matches(actual.Player))
                    w.Commit(arm,arm.AnimationOperand,actual.Handle,true);
                else w.Abort(arm,"original outer operation did not commit an intact pending initial arm");
            }
            if(terminalAttempt!=null&&!returned){w.CleanupFailures++;w.Fault(terminalAttempt,"native original terminal did not return even if foreign chain suppressed its exception");}
            if(Call.Target.Label=="Wait.Start"&&Pending!=null&&!returned&&!Pending.Committed)w.Abort(Pending,"Start original did not return");
            if(w.native.Count!=0&&ReferenceEquals(w.native[^1],this))w.native.RemoveAt(w.native.Count-1);
            else if(Pending!=null)w.Fault(Pending,"native source stack mismatch");
        }
    }
    sealed class SourceCall:ICallerSourceFrame
    {
        readonly WaitOwnership w;readonly CallerSourceEntry entry;readonly object? parent;internal Epoch? Pending,Terminal;
        internal bool Allowed;bool decided,exited,returned;
        internal SourceCall(WaitOwnership w,CallerSourceEntry entry,object? parent)
        {
            this.w=w;this.entry=entry;this.parent=parent;
            if(entry.Kind!=CallerSourceKind.Release||entry.Animation==null)return;
            var e=w.Current(entry.Animation);if(e==null)return;
            // DeliveryOpen exists only during sink.Enter, before SourceFrame is
            // returned. A later AllowOriginal callback is too late to consume.
            var proof=entry.ReleaseSource;
            if(!w.Live(e)||entry.Player==null||!e.Player.Matches(entry.Player)||proof is null||!proof.NormalProviderPublished||
                !w.source.TryResolveRelease(entry,parent,e.Source,out var kind)||kind==TerminalKind.None||
                !ReferenceEquals(w.Current(entry.Animation),e)||!w.Live(e)||!ReferenceEquals(w.Current(entry.Animation),e)||!proof.TryConsumeForCurrentOriginal())
                throw new InvalidOperationException("Known admitted Release lacks its current exact source/callee permission");
            e.Cause=kind;e.Consumed=true;Terminal=e;w.ConsumedReleases++;decided=true;Allowed=true;
        }
        public bool AllowOriginal
        {
            get
            {
                if(decided)return Allowed;decided=true;
                if(entry.Kind!=CallerSourceKind.Release||entry.Animation is null)return Allowed=true;
                var e=w.Current(entry.Animation);if(e is null)return Allowed=true;
                throw new InvalidOperationException("Known source cannot acquire a late Release permit");
            }
        }
        public void FactoryReturned(object animation,float delay,object? iterator)
        {
            if(entry.Kind!=CallerSourceKind.Replacement||iterator is null||Pending!=null)return;
            if(!w.source.IsKnownReference(animation))return;
            if(entry.OriginalSource?.NormalProviderPublished!=true)
            {if(w.Current(animation) is{} known){w.Fault(known,"unqualified source attempted replacement of owned animation");throw new InvalidOperationException("Replacement source lacks its live exact normal-provider original scope");}return;}
            if(w.fields.TryAnimation(animation,out var f)&&f!=null)
            {Pending=w.StartPending(f.Player,animation);if(Pending!=null)w.IteratorSeen(Pending,iterator);}
        }
        public void SchedulerEntering(object player,object iterator)
        {if(Pending is{} p&& (entry.OriginalSource?.NormalProviderPublished!=true||!p.Player.Matches(player)||p.Iterator?.Matches(iterator)!=true))p.Invalid=true;}
        public void SchedulerReturned(object player,object iterator,object? handle)
        {if(Pending is{} p){SchedulerEntering(player,iterator);w.HandleSeen(p,handle);}}
        public void HandleWritten(object animation,object? handle)
        {if(Pending is{} p){if(entry.OriginalSource?.NormalProviderPublished!=true)p.Invalid=true;w.Commit(p,animation,handle);}}
        public void OriginalReturned(){returned=true;}
        public void Exit(bool originalReturned)
        {
            if(exited)return;exited=true;
            if(Pending is{} p&&!p.Committed)w.Abort(p,"replacement source did not complete exact successful store");
            else if(Pending is{} committed&&!originalReturned)w.Fault(committed,"replacement original failed after successful write");
            if(Terminal is{} e&&(!originalReturned||!returned||!e.NativeConsumed||!e.NativeReturned))
            {w.CleanupFailures++;w.Fault(e,"consumed Release cleanup failed or native terminal not reached");}
            if(Terminal!=null)w.Retire(Terminal,"original terminal source exited",true);
            if(w.callers.Count!=0&&ReferenceEquals(w.callers[^1],this))w.callers.RemoveAt(w.callers.Count-1);
            else if(Terminal!=null)w.Fault(Terminal,"caller source stack mismatch");
        }
    }
}
