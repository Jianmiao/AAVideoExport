namespace Aave.WaitOwnership.CallerSource;
public static class CallerSourceHooks
{
    [ThreadStatic] private static SourceFrame? current;
    [ThreadStatic] private static ChainFrame? chain;
    [ThreadStatic] private static VerifiedOriginalSource? deliveringOriginal;
    [ThreadStatic] private static int sourceDeliveryDepth;
    [ThreadStatic] private static ReleaseSource? deliveringRelease;
    private static ICallerSourceSink? sink;
    private static long exitFailures;
    public static SourceFrame? CurrentSource=>current;
    public static long ExitFailures=>Interlocked.Read(ref exitFailures);
    public static bool HasChain=>chain!=null;
    public static void Bind(ICallerSourceSink value)
    {ArgumentNullException.ThrowIfNull(value);if(Interlocked.CompareExchange(ref sink,value,null)!=null)throw new InvalidOperationException("Source sink already bound");}
    // The original source/deployment API stays observational until the verified
    // full-chain transform replaces this precise original site with EnterBound.
    public static SourceFrame Enter(int planId,int kind,object? player,object? animation,string? reason)
        =>EnterCore(planId,kind,player,animation,reason,0);
    internal static SourceFrame EnterBound(int planId,int kind,object? player,object? animation,string? reason,long generation)
        =>EnterCore(planId,kind,player,animation,reason,generation);
    public static SourceFrame EnterGeneral(int planId,object? instance,FactArguments arguments)=>EnterCore(planId,2,null,null,null,0,instance,arguments);
    internal static SourceFrame EnterGeneralBound(int planId,object? instance,FactArguments arguments,long generation)=>EnterCore(planId,2,null,null,null,generation,instance,arguments);
    private static SourceFrame EnterCore(int planId,int kind,object? player,object? animation,string? reason,long generation,object? instance=null,FactArguments? arguments=null)
    {
        if(kind is not (0 or 1 or 2))throw new InvalidDataException("Unknown source kind");
        ChainFrame? bound=null;ReleaseSource? source=null;
        if(chain is {} c&&c.Generation.Token==generation&&c.Generation.PlanId==planId&&c.Generation.Kind==(CallerSourceKind)kind&&c.Generation.IsActive&&!c.BodyEntered)
        {
            c.BodyEntered=true;bound=c;
            if(kind==(int)CallerSourceKind.Release&&c.Reserved is {} p&&ReferenceEquals(p.Player,player)&&ReferenceEquals(p.Animation,animation)&&ReferenceEquals(p.Reason,reason)&&p.Parent.Generation?.IsActive==true&&ReferenceEquals(current,p.Parent))
                source=new ReleaseSource(p.Parent,p.Offset,planId,c);
        }
        if(source?.ExactCalleeBoundaryProven==false)source=null;
        var original=bound==null?null:new VerifiedOriginalSource(bound,current);
        var entry=new CallerSourceEntry(planId,(CallerSourceKind)kind,player,animation,reason){ReleaseSource=source,OriginalSource=original,Instance=instance,Arguments=Array.AsReadOnly((object?[])(arguments?.Values.Clone()??Array.Empty<object?>()))};
        ICallerSourceFrame? target;
        var previousDelivery=deliveringOriginal;deliveringOriginal=original;sourceDeliveryDepth++;
        var previousRelease=deliveringRelease;deliveringRelease=source;
        if(source!=null)source.DeliveryOpen=true;
        if(original!=null)original.DeliveryOpen=true;
        try{target=Volatile.Read(ref sink)?.Enter(entry);}finally{if(source!=null)source.DeliveryOpen=false;if(original!=null)original.DeliveryOpen=false;deliveringOriginal=previousDelivery;deliveringRelease=previousRelease;sourceDeliveryDepth--;}
        var value=new SourceFrame(entry,target,current,bound);original?.Attach(value);current=value;CompletionFactsHooks.AttachOriginal(value,bound);return value;
    }
    // This is generated as the first instruction sequence of the WHOLE chain,
    // not as a Harmony prefix. Every invocation pushes its own context, even
    // unknown/inactive/raw generations, so nested calls cannot inherit a frame.
    internal static ChainFrame EnterChain(long token,object? player,object? animation,string? reason)
    {
        var generation=ChainGeneration.Lookup(token);
        var value=new ChainFrame(generation,chain);
        var parent=current;var pending=parent?.Pending;
        if(generation.IsActive&&generation.Kind==CallerSourceKind.Release&&parent!=null&&
           parent.Generation?.IsActive==true&&ReferenceEquals(parent.Chain,chain)&&pending!=null&&!pending.Consumed&&
           pending.TargetPlanId==generation.PlanId&&pending.ExactTarget!=null&&generation.Registration!.Original.Equals(pending.ExactTarget)&&ReferenceEquals(pending.Player,player)&&ReferenceEquals(pending.Animation,animation)&&ReferenceEquals(pending.Reason,reason))
        {pending.Consumed=true;value.Reserved=pending;}
        chain=value;return value;
    }
    internal static void ExitChain(ChainFrame frame)
    {
        if(frame.Exited)return;frame.Exited=true;frame.Reserved=null;if(frame.ReservedFact!=null)frame.ReservedFact.ChainExited=true;
        if(ReferenceEquals(chain,frame))chain=frame.Parent;else Interlocked.Increment(ref exitFailures);
    }
    internal static bool IsCurrent(ChainFrame frame)=>ReferenceEquals(chain,frame)&&!frame.Exited&&frame.Thread==Environment.CurrentManagedThreadId;
    internal static bool IsCurrentDelivery(VerifiedOriginalSource value)=>ReferenceEquals(deliveringOriginal,value);
    internal static bool IsCurrentReleaseDelivery(ReleaseSource value)=>ReferenceEquals(deliveringRelease,value);
    internal static bool HasSourceDelivery=>sourceDeliveryDepth!=0;
    internal static ChainFrame EnterGeneralChain(long token,object? instance,FactArguments arguments)
    {var generation=ChainGeneration.Lookup(token);var value=new ChainFrame(generation,chain);value.ReservedFact=CompletionFactsHooks.Reserve(generation,current,chain,instance,arguments);chain=value;return value;}
    public static bool AllowOriginal(SourceFrame frame)
    {if(!ReferenceEquals(current,frame))throw new InvalidOperationException("Source scope mismatch");return frame.Entry.ReleaseSource?.CanApplyDecision==true?(frame.Target?.AllowOriginal??true):true;}
    public static void FactoryReturned(SourceFrame frame,object animation,float delay,object? iterator)=>frame.Target?.FactoryReturned(animation,delay,iterator);
    public static void SchedulerEntering(SourceFrame frame,object? player,object? iterator){if(player!=null)frame.Target?.SchedulerEntering(player,iterator!);}
    public static void SchedulerReturned(SourceFrame frame,object player,object iterator,object? handle)=>frame.Target?.SchedulerReturned(player,iterator,handle);
    public static void HandleWritten(SourceFrame frame,object animation,object? handle)=>frame.Target?.HandleWritten(animation,handle);
    public static void BeforeReleaseCall(SourceFrame frame,int offset,int targetPlanId,object? player,object? animation,string? reason)
    {if(!ReferenceEquals(current,frame))throw new InvalidOperationException("Release caller scope mismatch");frame.Pending=new PendingRelease(frame,offset,targetPlanId,player,animation,reason);
        if(frame.Generation?.Registration is {} registration&&registration.Template.TryReleaseTarget(offset,targetPlanId,out var target))frame.Pending.ExactTarget=target;}
    public static void AfterReleaseCall(SourceFrame frame)=>frame.Pending=null;
    public static void Returned(SourceFrame frame)=>ReturnedValue(frame,null);
    public static void ReturnedValue(SourceFrame frame,object? value)
    {
        if(!ReferenceEquals(current,frame)||frame.Exited||frame.Exiting||frame.Returned){CompletionFactsHooks.Invocation(frame).ObservationReliable=false;return;}
        frame.Target?.OriginalReturned();frame.Returned=true;CompletionFactsHooks.Returned(frame,value);
    }
    public static void Exit(SourceFrame frame)
    {
        if(frame.Exited||frame.Exiting)return;frame.Exiting=true;CompletionFactsHooks.Exit(frame);frame.Exited=true;frame.Pending=null;
        try{frame.Target?.Exit(frame.Returned);}catch{Interlocked.Increment(ref exitFailures);}
        finally{if(ReferenceEquals(current,frame))current=frame.Parent;else Interlocked.Increment(ref exitFailures);}
    }
    public sealed class SourceFrame
    {
        public CallerSourceEntry Entry{get;}public ICallerSourceFrame? Target{get;}public SourceFrame? Parent{get;}
        internal readonly ChainFrame? Chain;
        internal ChainGeneration? Generation=>Chain?.Generation;
        internal bool Returned,Exited,Exiting;internal PendingRelease? Pending;
        internal CompletionInvocation? Completion;internal CompletionCallToken? PendingFact;internal OriginalCallObservation? OriginalObservation;
        internal SourceFrame(CallerSourceEntry e,ICallerSourceFrame? target,SourceFrame? parent,ChainFrame? c){Entry=e;Target=target;Parent=parent;Chain=c;}
    }
    public sealed class ChainFrame
    {
        internal readonly ChainGeneration Generation;
        internal readonly ChainFrame? Parent;
        internal readonly int Thread=Environment.CurrentManagedThreadId;
        internal PendingRelease? Reserved;internal bool BodyEntered,Exited;
        internal CompletionCallToken? ReservedFact;
        internal ChainFrame(ChainGeneration g,ChainFrame? parent){Generation=g;Parent=parent;}
    }
    internal sealed class PendingRelease
    {
        internal readonly SourceFrame Parent;internal readonly int Offset,TargetPlanId;
        internal readonly object? Player,Animation;internal readonly string? Reason;internal bool Consumed;internal System.Reflection.MethodBase? ExactTarget;
        internal PendingRelease(SourceFrame parent,int offset,int target,object? player,object? animation,string? reason)
        {Parent=parent;Offset=offset;TargetPlanId=target;Player=player;Animation=animation;Reason=reason;}
    }
}




