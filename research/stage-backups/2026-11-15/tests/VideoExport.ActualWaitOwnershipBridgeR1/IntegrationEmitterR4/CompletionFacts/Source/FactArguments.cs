namespace Aave.WaitOwnership.CallerSource;
public sealed class FactArguments
{
    internal readonly object?[] Values;
    internal FactArguments(int count){if(count<0||count>16)throw new ArgumentOutOfRangeException(nameof(count));Values=new object?[count];}
}
// Unforgeable source-local handle. Only the emitted caller holds this returned
// object; sites cannot complete another call by guessing a plan/site integer.
public sealed class CompletionCallToken
{
    internal readonly CallerSourceHooks.SourceFrame Frame;
    internal readonly int Site,TargetPlan;
    internal readonly CompletionFactKind Kind;
    internal readonly object? Receiver;
    internal readonly object?[] Arguments;
    internal ChainGeneration? Expected;
    internal System.Reflection.MethodBase? Target;
    internal OriginalCallObservation? Original;
    internal Type? DeclaredValueType;
    internal bool ControlReceiverArgument;
    internal bool Reserved,ChainExited,Completed,Abandoned;
    internal readonly int Thread=Environment.CurrentManagedThreadId;
    internal CompletionCallToken(CallerSourceHooks.SourceFrame frame,int site,int kind,int target,object? receiver,FactArguments args)
    {Frame=frame;Site=site;Kind=(CompletionFactKind)kind;TargetPlan=target;Receiver=receiver;Arguments=(object?[])args.Values.Clone();}
}

