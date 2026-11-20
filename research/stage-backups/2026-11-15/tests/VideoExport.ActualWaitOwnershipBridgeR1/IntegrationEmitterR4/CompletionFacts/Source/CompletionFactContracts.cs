using System.Reflection;
namespace Aave.WaitOwnership.CallerSource;

public enum CompletionFactKind
{
    Constructed, ListAdded, PendingQuery, GateStatusRead, GateDueRead, ClockRead,
    GateArm, GateTick, GateCancel, FieldRead, FieldWritten, OriginalReturned, MemberCall, AliveCheck
}
public interface ICompletionFactsSink
{
    void OnFact(CompletionFact fact);
    void OnInvocationExit(CompletionInvocation invocation,bool originalReturned);
}
public sealed class CompletionInvocation
{
    internal readonly List<CompletionFact> MutableFacts=new();
    public CallerSourceHooks.SourceFrame Frame{get;}
    public CallerSourceEntry Entry=>Frame.Entry;
    public IReadOnlyList<CompletionFact> Facts{get;}
    public bool ObservationReliable{get;internal set;}=true;
    public bool IsCurrentVerifiedOriginal=>ObservationReliable&&Entry.OriginalSource?.IsCurrentVerifiedOriginal==true;
    public bool NormalProviderPublished=>ObservationReliable&&Entry.OriginalSource?.NormalProviderPublished==true;
    public long Generation=>Entry.OriginalSource?.Generation??0;
    internal CompletionInvocation(CallerSourceHooks.SourceFrame frame){Frame=frame;Facts=MutableFacts.AsReadOnly();}
}
public sealed class OriginalCallObservation
{
    public CompletionInvocation Invocation{get;}
    public object? Instance=>Invocation.Entry.Instance;
    public IReadOnlyList<object?> Arguments=>Invocation.Entry.Arguments;
    public bool OriginalReturned{get;internal set;}
    public object? ReturnValue{get;internal set;}
    public IReadOnlyList<CompletionFact> BodyFacts=>Invocation.Facts;
    internal ChainGeneration? Binding;
    public long Generation=>Binding?.Token??0;
    public bool NormalProviderPublished=>Invocation.ObservationReliable&&Binding?.IsActive==true&&Binding.NormalProvider;
    internal OriginalCallObservation(CompletionInvocation invocation){Invocation=invocation;}
}
public sealed class CompletionFact
{
    public CompletionInvocation Invocation{get;}
    public int SiteId{get;}
    public CompletionFactKind Kind{get;}
    public object? Receiver{get;}
    public IReadOnlyList<object?> Arguments{get;}
    public object? EffectiveValue{get;}
    public OriginalCallObservation? Original{get;internal set;}
    public MemberInfo? Member{get;internal set;}
    public Type? DeclaredValueType{get;internal set;}
    internal Func<bool>? LinkIsValid=null;
    public bool IsCurrentSource=>Invocation.IsCurrentVerifiedOriginal;
    public bool NormalProviderPublished=>Invocation.NormalProviderPublished;
    public bool HasQualifiedOriginalReturn=>IsCurrentSource&&Original?.OriginalReturned==true&&LinkIsValid?.Invoke()==true;
    public bool HasQualifiedNormalOriginalReturn=>HasQualifiedOriginalReturn&&NormalProviderPublished&&Original?.NormalProviderPublished==true;
    public bool IsUsableFor(ReleaseSource source)=>Invocation.ObservationReliable&&Invocation.Frame.Generation?.NormalProvider==true&&ReferenceEquals(source.Parent,Invocation.Frame)&&source.NormalProviderPublished&&source.CanInspectParentFacts;
    public bool HasQualifiedOriginalReturnFor(ReleaseSource source)=>IsUsableFor(source)&&Original?.OriginalReturned==true&&Original.NormalProviderPublished&&LinkIsValid?.Invoke()==true;
    // A nested original has exited before its parent's AfterCall. Qualification
    // follows the exact recorded tree from a still-live root and checks every
    // generation/link, rather than asking an exited callee to be current.
    public bool IsUsableWithin(CompletionFact root)=>root.HasQualifiedNormalOriginalReturn&&InOriginalTree(root,0);
    public bool IsUsableWithin(CompletionFact root,ReleaseSource source)=>root.HasQualifiedOriginalReturnFor(source)&&InOriginalTree(root,0);
    public bool HasQualifiedOriginalReturnWithin(CompletionFact root)=>IsUsableWithin(root)&&QualifiedRecordedOriginal;
    public bool HasQualifiedOriginalReturnWithin(CompletionFact root,ReleaseSource source)=>IsUsableWithin(root,source)&&QualifiedRecordedOriginal;
    private bool QualifiedRecordedOriginal=>RecordedOriginal(true);
    private bool RecordedOriginal(bool normal)=>Invocation.ObservationReliable&&Original?.OriginalReturned==true&&(!normal||Original.NormalProviderPublished)&&LinkIsValid?.Invoke()==true;
    internal bool IsRecordedWithinForOrdinaryControl(CompletionFact root)=>root.HasQualifiedOriginalReturn&&InOriginalTree(root,0,false);
    private bool InOriginalTree(CompletionFact root,int depth,bool normal=true)
    {
        if(depth>=16||root.Original is not{} original||!original.Invocation.ObservationReliable)return false;
        foreach(var fact in original.BodyFacts)
        {
            if(ReferenceEquals(fact,this))return ReferenceEquals(Invocation,original.Invocation);
            if(fact.RecordedOriginal(normal)&&InOriginalTree(fact,depth+1,normal))return true;
        }
        return false;
    }
    internal CompletionFact(CompletionInvocation invocation,int site,CompletionFactKind kind,object? receiver,object?[] args,object? value)
    {Invocation=invocation;SiteId=site;Kind=kind;Receiver=receiver;Arguments=Array.AsReadOnly((object?[])args.Clone());EffectiveValue=value;}
}

