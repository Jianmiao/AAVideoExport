using System.Reflection;
namespace Aave.WaitOwnership.CallerSource;

public static class CompletionFactsHooks
{
    static ICompletionFactsSink? sink;static long failures;
    public static long ObservationFailures=>Interlocked.Read(ref failures);
    public static void Bind(ICompletionFactsSink value){ArgumentNullException.ThrowIfNull(value);if(Interlocked.CompareExchange(ref sink,value,null)!=null)throw new InvalidOperationException("Facts sink already bound");}
    public static CompletionInvocation? ForFrame(CallerSourceHooks.SourceFrame? frame)=>frame?.Completion;
    internal static CompletionInvocation Invocation(CallerSourceHooks.SourceFrame frame)=>frame.Completion??=new(frame);
    public static FactArguments NewArguments(int count)=>new(count);
    public static void SetArgument(FactArguments arguments,int index,object? value)=>arguments.Values[index]=value;
    public static object BoxBoolean(bool value)=>value;
    public static object BoxInt32(int value)=>value;
    public static object BoxInt64(long value)=>value;
    public static object BoxSingle(float value)=>value;
    public static object BoxDouble(double value)=>value;
    public static CompletionCallToken BeforeCall(CallerSourceHooks.SourceFrame frame,int site,int kind,int targetPlan,object? receiver,FactArguments arguments)
    {
        var invocation=Invocation(frame);var token=new CompletionCallToken(frame,site,kind,targetPlan,receiver,arguments);
        if(!ReferenceEquals(CallerSourceHooks.CurrentSource,frame)||frame.PendingFact is{Completed:false,Abandoned:false}){invocation.ObservationReliable=false;token.Abandoned=true;return token;}
        if(frame.Generation?.Registration?.Template.TryFactTarget(site,targetPlan,out var target,out var controlReceiver,out var valueType)==true)
        {
            token.Target=target;token.DeclaredValueType=valueType;token.ControlReceiverArgument=controlReceiver;
            if(targetPlan!=0&&ManagedChainRegistration.TryGet(target,out var registration)&&registration.PlanId==targetPlan)
                token.Expected=registration.ActiveGeneration;
        }
        frame.PendingFact=token;return token;
    }
    public static void AfterCall(CompletionCallToken token,object? effective)
    {
        if(token.Abandoned||token.Completed||token.Thread!=Environment.CurrentManagedThreadId||!ReferenceEquals(CallerSourceHooks.CurrentSource,token.Frame)||!ReferenceEquals(token.Frame.PendingFact,token))
        {Invocation(token.Frame).ObservationReliable=false;return;}
        token.Completed=true;token.Frame.PendingFact=null;
        var fact=new CompletionFact(Invocation(token.Frame),token.Site,token.Kind,token.Receiver,token.Arguments,effective){Original=token.Original,Member=token.Target,
            DeclaredValueType=token.DeclaredValueType};
        fact.LinkIsValid=()=>token.Reserved&&token.ChainExited&&!token.Abandoned&&token.Expected?.IsActive==true&&token.Original?.Binding==token.Expected&&token.Original.Invocation.ObservationReliable;
        Emit(fact);
    }
    public static void AbandonCall(CompletionCallToken? token)
    {if(token==null||token.Completed||token.Thread!=Environment.CurrentManagedThreadId)return;token.Abandoned=true;if(ReferenceEquals(token.Frame.PendingFact,token))token.Frame.PendingFact=null;}
    public static void FieldRead(CallerSourceHooks.SourceFrame frame,int site,object? receiver,object? value)=>Field(frame,site,receiver,value,CompletionFactKind.FieldRead);
    public static void FieldWritten(CallerSourceHooks.SourceFrame frame,int site,object? receiver,object? value)=>Field(frame,site,receiver,value,CompletionFactKind.FieldWritten);
    static void Field(CallerSourceHooks.SourceFrame frame,int site,object? receiver,object? value,CompletionFactKind kind)
    {
        var fact=new CompletionFact(Invocation(frame),site,kind,receiver,Array.Empty<object?>(),value);
        if(frame.Generation?.Registration?.Template.TryFactField(site,out var field,out var valueType)==true){fact.Member=field;fact.DeclaredValueType=valueType;}
        Emit(fact);
    }
    static void Emit(CompletionFact fact)
    {
        var invocation=fact.Invocation;
        if(!ReferenceEquals(CallerSourceHooks.CurrentSource,invocation.Frame)||invocation.MutableFacts.Count>=256){invocation.ObservationReliable=false;return;}
        invocation.MutableFacts.Add(fact);
        try{Volatile.Read(ref sink)?.OnFact(fact);}catch{invocation.ObservationReliable=false;Interlocked.Increment(ref failures);}
    }
    internal static void AttachOriginal(CallerSourceHooks.SourceFrame frame,CallerSourceHooks.ChainFrame? chain)
    {
        if(chain?.ReservedFact is{} token&&chain.Generation.IsActive&&chain.BodyEntered)
        {var observation=new OriginalCallObservation(Invocation(frame)){Binding=chain.Generation};token.Original=observation;frame.OriginalObservation=observation;}
    }
    internal static void Returned(CallerSourceHooks.SourceFrame frame,object? value)
    {
        if(frame.OriginalObservation is{} original){original.OriginalReturned=true;original.ReturnValue=value;}
        if(frame.Completion!=null||frame.Entry.Kind==CallerSourceKind.Observation)
            Emit(new CompletionFact(Invocation(frame),-1,CompletionFactKind.OriginalReturned,frame.Entry.Instance,frame.Entry.Arguments.ToArray(),value));
    }
    internal static void Exit(CallerSourceHooks.SourceFrame frame)
    {
        AbandonCall(frame.PendingFact);
        if(frame.Completion is{} invocation)
            try{Volatile.Read(ref sink)?.OnInvocationExit(invocation,frame.Returned);}catch{invocation.ObservationReliable=false;Interlocked.Increment(ref failures);}
    }
    internal static CompletionCallToken? Reserve(ChainGeneration generation,CallerSourceHooks.SourceFrame? parent,CallerSourceHooks.ChainFrame? previous,object? receiver,FactArguments arguments)
    {
        var token=parent?.PendingFact;
        if(!generation.IsActive||parent?.Generation?.IsActive!=true||parent.Completion?.ObservationReliable==false||!ReferenceEquals(parent.Chain,previous)||
            token==null||token.Reserved||token.Abandoned||token.Completed||token.Expected!=generation||token.Target==null||!generation.Registration!.Original.Equals(token.Target))return null;
        object? expectedReceiver=token.ControlReceiverArgument?token.Arguments.FirstOrDefault():token.Receiver;
        object?[] expectedArgs=token.ControlReceiverArgument?token.Arguments.Skip(1).ToArray():token.Arguments;
        if(!ReferenceEquals(receiver,expectedReceiver)||expectedArgs.Length!=arguments.Values.Length||!expectedArgs.Zip(arguments.Values).All(x=>Same(x.First,x.Second)))return null;
        token.Reserved=true;return token;
    }
    static bool Same(object? a,object? b)=>a switch
    {
        double x=>b is double y&&BitConverter.DoubleToInt64Bits(x)==BitConverter.DoubleToInt64Bits(y),
        float x=>b is float y&&BitConverter.SingleToInt32Bits(x)==BitConverter.SingleToInt32Bits(y),
        long x=>b is long y&&x==y,int x=>b is int y&&x==y,bool x=>b is bool y&&x==y,_=>ReferenceEquals(a,b)
    };
}

