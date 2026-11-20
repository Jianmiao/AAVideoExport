namespace Aave.WaitOwnership.Integration;

public sealed partial class WaitOwnership
{
    // This is an exemption for one observed original run, never a captured
    // operation, producer binding, completion permit, or allocation lifetime.
    // Removing an exemption leaves the retired epoch's denial in force.
    readonly List<UnownedRun> unownedRuns=new();
    sealed class UnownedRun
    {
        internal Epoch Previous=null!;
        internal IWeakEpochIdentity Animation=null!,Player=null!;
        internal IWeakEpochIdentity? Iterator,Handle,InitialHandle;
        internal bool InitialNull,StartActive=true,Successful,Invalid,Removed;
    }
    UnownedRun? ObserveUnownedStart(object animation,Epoch previous)
    {
        SweepUnownedRuns();
        if(!fields.TryAnimation(animation,out var f)||f is null)return null;
        var run=new UnownedRun{Previous=previous,InitialNull=f.Handle is null};
        try
        {
            run.Animation=ObserveWeak(animation)!;run.Player=ObserveWeak(f.Player)!;
            if(f.Handle!=null)run.InitialHandle=ObserveWeak(f.Handle);
            if(run.Animation is null||run.Player is null||!run.InitialNull&&run.InitialHandle is null)
            {RemoveUnownedRun(run);return null;}
        }
        catch{RemoveUnownedRun(run);throw;}
        unownedRuns.Add(run);return run;
    }
    IWeakEpochIdentity? ObserveWeak(object operand)
    {
        // Use the same proven lifetime provider while constructing the weak
        // identity. No strong lease survives this observation or roots preview.
        if(!lifetimes.TryRetain(operand,out var retained,out _)||retained is null)return null;
        IWeakEpochIdentity? observed=null;
        try{if(retained.IsAlive&&retained.Matches(operand))observed=weak.RetainWeak(operand);}
        finally
        {
            try{retained.Dispose();}
            catch{CleanupFailures++;DisposeUnownedIdentity(observed);observed=null;}
        }
        return observed;
    }
    UnownedRun? UnownedIterator(object iterator)
        =>unownedRuns.LastOrDefault(r=>!r.Removed&&r.Iterator?.Matches(iterator)==true);
    void ObserveUnownedIterator(UnownedRun run,object iterator)
    {
        if(run.Removed||run.Iterator!=null||Iterator(iterator)!=null||UnownedIterator(iterator)!=null||
            !fields.TryIteratorOwner(iterator,out var animation)||animation is null||!run.Animation.Matches(animation))
        {run.Invalid=true;return;}
        run.Iterator=ObserveWeak(iterator);if(run.Iterator is null)run.Invalid=true;
    }
    void ObserveUnownedHandle(UnownedRun run,object? handle)
    {
        if(run.Removed||run.Invalid||run.Handle!=null||handle is null||
            knownEpochs.Values.Any(e=>e.Released?e.WeakHandle?.Matches(handle)==true:e.Handle?.Matches(handle)==true)||
            unownedRuns.Any(r=>!ReferenceEquals(r,run)&&r.Handle?.Matches(handle)==true))
        {run.Invalid=true;return;}
        run.Handle=ObserveWeak(handle);if(run.Handle is null)run.Invalid=true;
    }
    bool SameUnownedSource(UnownedRun run,object animation)
        =>!run.Removed&&!run.Invalid&&run.Animation.Matches(animation)&&
          ReferenceEquals(Current(animation),run.Previous)&&run.Previous.Retired&&
          !source.IsKnownReference(animation);
    void UnownedStartReturned(UnownedRun run,object animation)
    {
        if(!SameUnownedSource(run,animation)||run.Iterator is null||run.Handle is null||
            !fields.TryAnimation(animation,out var f)||f is not {Started:true,Handle:not null}||
            !run.Player.Matches(f.Player)||!run.Handle.Matches(f.Handle)||!SameUnownedSource(run,animation))
        {RemoveUnownedRun(run);return;}
        run.Successful=true;
        // Keep nested Starts provisional until their own original return.
        // The actual successful final field store supersedes prior exemptions;
        // it never deletes any retired captured epoch or claims scheduler drain.
        foreach(var prior in unownedRuns.Where(r=>!ReferenceEquals(r,run)&&r.Successful&&r.Animation.Matches(animation)).ToArray())
            RemoveUnownedRun(prior);
    }
    bool PreserveUnownedTerminal(UnownedRun run,object animation)
    {
        if(!SameUnownedSource(run,animation)||!fields.TryAnimation(animation,out var f)||f is null||!run.Player.Matches(f.Player))return false;
        bool handleMatches=run.Successful
            ?f.Handle!=null&&run.Handle?.Matches(f.Handle)==true
            :run.StartActive&&native.Any(n=>n.IsUnownedScheduler(run))&&
             (run.InitialNull?f.Handle is null:f.Handle!=null&&run.InitialHandle?.Matches(f.Handle)==true);
        return handleMatches&&SameUnownedSource(run,animation);
    }
    void RemoveUnownedRun(UnownedRun run)
    {
        if(run.Removed)return;run.Removed=true;unownedRuns.Remove(run);
        foreach(var identity in new[]{run.Iterator,run.Handle,run.InitialHandle,run.Player,run.Animation})DisposeUnownedIdentity(identity);
        run.InitialHandle=null;
    }
    void DisposeUnownedIdentity(IWeakEpochIdentity? identity)
    {
        // This observer must never replace an original scheduler/foreign-chain
        // exception. Removed exemptions remain denied even if weak cleanup fails.
        try{identity?.Dispose();}catch{CleanupFailures++;}
    }
    void SweepUnownedRuns()
    {
        foreach(var run in unownedRuns.Where(r=>!r.StartActive&&(r.Animation.IsAlive!=true||r.Iterator?.IsAlive!=true&&r.Handle?.IsAlive!=true)).ToArray())
            RemoveUnownedRun(run);
    }
}
