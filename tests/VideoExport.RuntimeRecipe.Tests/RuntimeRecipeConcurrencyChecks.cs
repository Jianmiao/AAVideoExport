using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using AAVideoExport.RuntimeBootstrap;

internal static class RuntimeRecipeConcurrencyChecks
{
    internal static void Run(Action<string,Action> test)
    {
        test("publication serializes other-thread actual emission and Harmony patch then consumes token",()=>
        {
            using var observer=NewObserver();var original=Fresh(typeof(ConcurrentWrapperA));
            var scope=observer.AcquireScope();Check(scope.Valid,scope.Reason);observer.SealScope(scope.Token);
            using var emitAttempt=new ManualResetEventSlim();using var patchAttempt=new ManualResetEventSlim();
            using var emitDone=new ManualResetEventSlim();using var patchDone=new ManualResetEventSlim();
            Task? emit=null,patch=null;bool emitBlocked=false,patchBlocked=false;
            using(observer.EnterCommitScope(scope.Token))
            {
                emit=Task.Factory.StartNew(()=>{emitAttempt.Set();_=Fresh(typeof(ConcurrentWrapperB));emitDone.Set();},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                patch=Task.Factory.StartNew(()=>
                {
                    patchAttempt.Set();var harmony=new Harmony("aave.recipe.concurrent-unrelated");
                    try{harmony.Patch(typeof(PatchTarget).GetMethod(nameof(PatchTarget.Target))!,prefix:new HarmonyMethod(typeof(PatchTarget),nameof(PatchTarget.Prefix)));patchDone.Set();}
                    finally{harmony.UnpatchSelf();}
                },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                Check(emitAttempt.Wait(5000)&&patchAttempt.Wait(5000),"Worker attempts were not scheduled");
                emitBlocked=!emitDone.Wait(100);patchBlocked=!patchDone.Wait(100);
            }
            Check(Task.WaitAll(new[]{emit!,patch!},5000),"Publication left emit/patch workers stuck");
            Check(emitBlocked&&patchBlocked,"Emission or patch crossed the publication lock window");
            Check(!observer.ValidateScope(scope.Token,false).Valid,"Disposed publication guard did not consume its token");
            Check(observer.Find(original).Status=="QUALIFIED","Unrelated patch changed the original wrapper behavior proof");
        });
        test("same-thread anonymous emission inside publication aborts before its original constructor",()=>
        {
            using var observer=NewObserver();_=Fresh(typeof(ConcurrentWrapperA));var scope=observer.AcquireScope();observer.SealScope(scope.Token);
            bool rejected=false;
            using(observer.EnterCommitScope(scope.Token))
            {try{_=new DynamicMethod("must-not-publish",typeof(void),Type.EmptyTypes);}catch(InvalidOperationException){rejected=true;}}
            Check(rejected&&observer.Failure.Length>0,"Submission thread emitted new code inside its sealed guard");
        });
        test("waiting publication can cancel without leaking a partially acquired lock",()=>
        {
            using var observer=NewObserver();_=Fresh(typeof(ConcurrentWrapperA));var scope=observer.AcquireScope();observer.SealScope(scope.Token);
            object harmonyGate=typeof(PatchProcessor).GetField("locker",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)!.GetValue(null)!;
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var cancellation=new CancellationTokenSource();
            var blocker=Task.Factory.StartNew(()=>{lock(harmonyGate){entered.Set();release.Wait();}},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Check(entered.Wait(5000),"Blocker did not acquire actual Harmony mutex");
            var waiting=Task.Factory.StartNew(()=>{try{using var lease=observer.EnterCommitScope(scope.Token,cancellation.Token);return false;}catch(OperationCanceledException){return true;}},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            try{cancellation.CancelAfter(100);Check(waiting.Wait(3000)&&waiting.Result,"Publication wait ignored cancellation");}
            finally{release.Set();Check(blocker.Wait(3000),"Blocker did not exit");}
            using var retry=observer.EnterCommitScope(scope.Token); // Proves no leaked mutex/recipe gate.
        });
        test("patch then unpatch between polls still invalidates its constructor receipt",()=>
        {
            using var observer=NewObserver();var value=Fresh(typeof(ConcurrentWrapperA));Check(observer.Find(value).Status=="QUALIFIED","No initial recipe");
            var patcher=new Harmony("aave.recipe.transient-ctor");var ctor=typeof(ConcurrentWrapperA).GetConstructor(new[]{typeof(IntPtr)})!;
            patcher.Patch(ctor,prefix:new HarmonyMethod(typeof(PatchTarget),nameof(PatchTarget.Prefix)));patcher.UnpatchSelf();
            Check(observer.Find(value).Status!="QUALIFIED","Transient foreign patch was lost between validation polls");
        });
    }
    private static RuntimeRecipeObserver NewObserver()
    {var observer=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(typeof(ConcurrentWrapperA),typeof(ConcurrentWrapperB)));observer.Install();Check(observer.IsInstalled,observer.Failure);return observer;}
    private static Delegate Fresh(Type type)
    {var store=typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(type);store.GetField("_initializer",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,null);return (Delegate)store.GetProperty("Initializer",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public sealed class ConcurrentWrapperA:Il2CppObjectBase{public ConcurrentWrapperA(IntPtr p):base(p){}}
    public sealed class ConcurrentWrapperB:Il2CppObjectBase{public ConcurrentWrapperB(IntPtr p):base(p){}}
    public static class PatchTarget{public static void Target(){} public static void Prefix(){}}
}
