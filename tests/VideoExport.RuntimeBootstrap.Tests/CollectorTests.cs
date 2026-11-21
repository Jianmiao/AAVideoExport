using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using AAVideoExport.RuntimeBootstrap;
using HarmonyLib;

internal sealed record TestResult(string Name, bool Passed, object? Detail = null);

internal static class CollectorTests
{
    internal static IEnumerable<TestResult> Run()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("partial real Harmony installation rolls back every owned patch", PartialRollback),
            ("rollback failure is explicit and capture remains disabled", RollbackFailure),
            ("record and marker caps report exact dropped counts", RecordCap),
            ("delegate cap preserves known associations and reports dropped new ones", DelegateCap),
            ("live weak keys do not keep methods or delegates alive", WeakKeys),
            ("parallel creation and snapshots preserve distinct identity and output", ParallelCapture),
            ("reentrant observer callbacks are counted and never modify outputs", ReentrantCallbacks),
            ("callback failures are counted and do not escape", CallbackFailures),
            ("original reflection invocation and delegate exceptions are preserved", OriginalExceptions),
            ("immutable snapshots retain pre-mutation event counts", SnapshotCopies),
            ("removing observer hooks leaves original behavior intact", Removal),
            ("bounded text and stack evidence reports truncation", EvidenceCaps),
            ("startup assembly snapshot has a bounded retained size", StartupAssemblyCap)
        };
        foreach (var test in tests)
        {
            TestResult result;
            try { test.Body(); result = new(test.Name, true); }
            catch(Exception ex) { result = new(test.Name, false, ex.ToString()); }
            yield return result;
        }
    }

    private static void Check(bool result, string message = "Assertion failed")
    { if (!result) throw new InvalidOperationException(message); }
    private static RuntimeObserver Observe(ObserverLimits? limits = null)
    {
        var observer = new RuntimeObserver(limits);
        observer.Install();
        Check(observer.IsInstalled, observer.Snapshot().InstallationError);
        return observer;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DynamicMethod Method, Func<int,int> Call) Build(int offset, string name = "SameName")
    {
        var method = new DynamicMethod(name, typeof(int), new[]{typeof(int)});
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, offset);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        return (method, (Func<int,int>)method.CreateDelegate(typeof(Func<int,int>)));
    }

    private static void PartialRollback()
    {
        var backend = new FailingBackend(3, false);
        using var observer = new RuntimeObserver(hooks: backend);
        observer.Install();
        var snapshot = observer.Snapshot();
        Check(!observer.IsInstalled && observer.CoverageStartedUtc == "");
        Check(snapshot.LossCounters.InstallFailures == 1 && snapshot.LossCounters.RollbackFailures == 0);
        Check(backend.OwnedPatchCount == 0 && snapshot.Targets.Count == 3 && snapshot.Targets.All(t=>!t.Installed));
        var item = Build(40);
        Check(item.Call(2)==42 && observer.FindDelegate(item.Call) is null);
    }

    private static void RollbackFailure()
    {
        var backend = new FailingBackend(3, true);
        try
        {
            using var observer = new RuntimeObserver(hooks: backend);
            observer.Install();
            Check(!observer.IsInstalled && observer.Snapshot().LossCounters.RollbackFailures == 1);
            var item = Build(40);
            Check(item.Call(2)==42 && observer.FindDelegate(item.Call) is null);
        }
        finally { backend.Cleanup(); }
    }

    private static void RecordCap()
    {
        using var observer = Observe(new ObserverLimits(Records:2, Delegates:8, Markers:2));
        var first = Build(1);
        var second = Build(2);
        var third = Build(3);
        observer.Mark("first"); observer.Mark("second"); observer.Mark("third");
        var snapshot=observer.Snapshot();
        Check(snapshot.Records.Count==2 && snapshot.Markers.Count==2 && snapshot.LossCounters.DroppedMarkers==1);
        Check(snapshot.LossCounters.DroppedRecordEvents==2 && snapshot.LossCounters.DroppedDelegateAssociations==1,
            $"Expected constructor+CreateDelegate drops; got {snapshot.LossCounters}");
        Check(observer.FindDelegate(first.Call) is not null && observer.FindDelegate(second.Call) is not null &&
            observer.FindDelegate(third.Call) is null && third.Call(39)==42);
    }

    private static void DelegateCap()
    {
        using var observer = Observe(new ObserverLimits(Records:8, Delegates:2));
        var first = Build(1);
        var other = (Func<int,int>)first.Method.CreateDelegate(typeof(Func<int,int>));
        var overflow = (Func<int,int>)first.Method.CreateDelegate(typeof(Func<int,int>));
        Check(observer.FindDelegate(first.Call)?.Id == observer.FindDelegate(other)?.Id);
        Check(observer.FindDelegate(overflow) is null && overflow(41)==42);
        var snapshot=observer.Snapshot();
        Check(snapshot.LossCounters.DroppedDelegateAssociations==1 && snapshot.Records.Count==1 &&
            snapshot.Records[0].CreateDelegateEvents==3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Method, WeakReference Call) MakeWeak(RuntimeObserver observer)
    {
        var item=Build(40);
        Check(observer.FindDelegate(item.Call) is not null && item.Call(2)==42);
        return (new WeakReference(item.Method), new WeakReference(item.Call));
    }
    private static void WeakKeys()
    {
        using var observer=Observe();
        var pair=MakeWeak(observer);
        for(var i=0;i<8 && (pair.Method.IsAlive||pair.Call.IsAlive);i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!pair.Method.IsAlive && !pair.Call.IsAlive, "Observer must not retain method/delegate keys strongly.");
        Check(observer.Snapshot().Records.Count==1);
    }

    private static void ParallelCapture()
    {
        using var observer=Observe();
        var items = new (DynamicMethod Method,Func<int,int> Call)[96];
        Parallel.For(0,items.Length,i=>{ items[i]=Build(i); _=observer.Snapshot(); });
        var records=items.Select(i=>observer.FindDelegate(i.Call)).ToArray();
        Check(records.All(r=>r is not null) && records.Select(r=>r!.Id).Distinct().Count()==96);
        Check(items.Select((v,i)=>v.Call(42-i)==42).All(v=>v));
        Check(observer.Snapshot().LossCounters.CallbackFailures==0);
    }

    private static void ReentrantCallbacks()
    {
        using var observer=Observe();
        var guard=typeof(RuntimeObserver).GetField("insideCallback",BindingFlags.Static|BindingFlags.NonPublic)!;
        try
        {
            guard.SetValue(null,true);
            var item=Build(40);
            Check(item.Call(2)==42 && observer.FindDelegate(item.Call) is null);
        }
        finally { guard.SetValue(null,false); }
        Check(observer.Snapshot().LossCounters.ReentrantCallbacksSkipped==2);
    }

    private static void CallbackFailures()
    {
        using var observer=Observe();
        // Feed malformed callback data at the observer boundary, without changing a patched original.
        var callback=typeof(RuntimeObserver).GetMethod("ConstructorPostfix",BindingFlags.Static|BindingFlags.NonPublic)!;
        callback.Invoke(null,new object?[]{null});
        Check(observer.Snapshot().LossCounters.CallbackFailures==1);
        var valid=Build(40);
        Check(valid.Call(2)==42 && observer.FindDelegate(valid.Call) is not null);
    }

    private static void OriginalExceptions()
    {
        using var observer=Observe();
        var method=new DynamicMethod("Throws",typeof(void),Type.EmptyTypes);
        var il=method.GetILGenerator();
        il.Emit(OpCodes.Ldstr,"fixture exception");
        il.Emit(OpCodes.Newobj,typeof(InvalidOperationException).GetConstructor(new[]{typeof(string)})!);
        il.Emit(OpCodes.Throw);
        try { method.Invoke(null,Array.Empty<object>()); throw new Exception("Expected original invocation failure."); }
        catch(TargetInvocationException ex) { Check(ex.InnerException is InvalidOperationException && ex.InnerException.Message=="fixture exception"); }
        var action=(Action)method.CreateDelegate(typeof(Action));
        try { action(); throw new Exception("Expected original delegate failure."); }
        catch(InvalidOperationException ex) { Check(ex.Message=="fixture exception"); }
        Check(observer.FindDelegate(action)?.InvokeEvents==1);
        try { method.CreateDelegate(typeof(Func<int>)); throw new Exception("Expected incompatible delegate failure."); }
        catch(ArgumentException) { }
        Check(observer.FindDelegate(action)?.CreateDelegateEvents==1);
    }

    private static void SnapshotCopies()
    {
        using var observer=Observe();
        var item=Build(40);
        var snapshot=observer.Snapshot();
        item.Method.CreateDelegate(typeof(Func<int,int>));
        Check(snapshot.Records[0].CreateDelegateEvents==1 && observer.Snapshot().Records[0].CreateDelegateEvents==2);
        Check(snapshot.Records[0].CreationStack is System.Collections.ObjectModel.ReadOnlyCollection<string>);
    }

    private static void Removal()
    {
        var observer=Observe();
        var item=Build(40);
        observer.Dispose();
        observer.Dispose();
        var after=Build(39);
        Check(!observer.IsInstalled && observer.Snapshot().Targets.All(t=>!t.Installed));
        Check(observer.FindDelegate(after.Call) is null && item.Call(2)==42 && after.Call(3)==42);
        Check(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("com.aavideoexport.runtime-attribution.v1")==true));
    }

    private static void EvidenceCaps()
    {
        using var observer=Observe(new ObserverLimits(StackFrames:2,TextCharacters:16));
        var item=Build(40,new string('x',1000));
        var snapshot=observer.Snapshot();
        Check(observer.FindDelegate(item.Call)?.Name.Length==16);
        Check(snapshot.Records.All(r=>r.CreationStack.Count<=2 && r.MaterializationStack.Count<=2));
        Check(snapshot.LossCounters.TruncatedStacks>=2 && snapshot.LossCounters.TruncatedText>0 && item.Call(2)==42);
    }

    private static void StartupAssemblyCap()
    {
        for(var i=0;i<520;i++)
            AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AAVE.BoundedStart."+i),AssemblyBuilderAccess.Run);
        using var observer=Observe();
        var snapshot=observer.Snapshot();
        Check(snapshot.StartAssemblies.Count<=512, $"Startup snapshot retained {snapshot.StartAssemblies.Count} assemblies above the 512 limit.");
        Check(snapshot.LossCounters.DroppedStartAssemblies==AppDomain.CurrentDomain.GetAssemblies().Length-512);
    }

    private sealed class FailingBackend : IRuntimeHooks
    {
        private readonly HarmonyRuntimeHooks real=new();
        private readonly int failAt;
        private readonly bool failRollback;
        private int count;
        internal FailingBackend(int failAt,bool failRollback)=>(this.failAt,this.failRollback)=(failAt,failRollback);
        public int OwnedPatchCount=>real.OwnedPatchCount;
        public void Patch(MethodBase target,MethodInfo callback,bool prefix)
        {
            real.Patch(target,callback,prefix);
            if(++count==failAt)throw new InvalidOperationException("Injected failure after real patch installation.");
        }
        public void UnpatchAll()
        {
            if(failRollback)throw new InvalidOperationException("Injected rollback failure.");
            real.UnpatchAll();
        }
        internal void Cleanup()=>real.UnpatchAll();
    }
}
