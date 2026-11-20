namespace Aave.WaitOwnership.CallerSource;
public enum CallerSourceKind { Replacement, Release, Observation }
public sealed record CallerSourceEntry(int PlanId,CallerSourceKind Kind,object? Player,object? Animation,string? Reason)
{
    public ReleaseSource? ReleaseSource {get;internal init;}
    public VerifiedOriginalSource? OriginalSource {get;internal init;}
    public object? Instance {get;internal init;}
    public IReadOnlyList<object?> Arguments {get;internal init;}=Array.Empty<object?>();
}
// An immutable fact of exact original entry, with a separate one-use live
// consumption operation. A stored historical bool is not terminal permission.
public sealed class ReleaseSource
{
    public CallerSourceHooks.SourceFrame Parent {get;}
    public int IlOffset {get;}
    public int TargetPlanId {get;}
    public bool ExactCalleeBoundaryProven {get;}
    public long Generation => chain.Generation.Token;
    private readonly CallerSourceHooks.ChainFrame chain;
    private bool consumed;
    internal bool DeliveryOpen;
    internal bool Consumed=>consumed;
    internal bool CanApplyDecision=>consumed&&CallerSourceHooks.IsCurrent(chain)&&chain.Generation.IsActive&&Parent.Generation?.IsActive==true&&!Parent.Exited&&ReferenceEquals(CallerSourceHooks.CurrentSource?.Entry.ReleaseSource,this);
    internal bool CanInspectParentFacts=>DeliveryOpen&&CallerSourceHooks.IsCurrentReleaseDelivery(this)&&ReferenceEquals(CallerSourceHooks.CurrentSource,Parent)&&CallerSourceHooks.IsCurrent(chain)&&chain.Generation.IsActive&&Parent.Generation?.IsActive==true&&!Parent.Exited;
    public bool NormalProviderPublished=>chain.Generation.NormalProvider;
    internal ReleaseSource(CallerSourceHooks.SourceFrame parent,int offset,int target,CallerSourceHooks.ChainFrame frame)
    {Parent=parent;IlOffset=offset;TargetPlanId=target;chain=frame;ExactCalleeBoundaryProven=frame.Generation.ActualMethod!=null&&frame.Generation.Expected!=null&&frame.Generation.IsActive&&frame.BodyEntered&&CallerSourceHooks.IsCurrent(frame);}
    public bool TryConsumeForCurrentOriginal()
    {
        if(!ExactCalleeBoundaryProven||consumed||!CanInspectParentFacts)return false;
        consumed=true;return true;
    }
}
public interface ICallerSourceSink {ICallerSourceFrame? Enter(CallerSourceEntry entry);}
public interface ICallerSourceFrame
{
    bool AllowOriginal {get;}
    void FactoryReturned(object animation,float delay,object? iterator);
    void SchedulerEntering(object player,object iterator);
    void SchedulerReturned(object player,object iterator,object? handle);
    void HandleWritten(object animation,object? handle);
    void OriginalReturned();
    void Exit(bool returned);
}



