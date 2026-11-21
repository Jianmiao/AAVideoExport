using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using AAVideoExport.Plugin;
using AAVideoExport.RuntimeBootstrap;

internal static class RuntimeCommitCompositionChecks
{
    private static readonly MethodInfo Target = typeof(RuntimeCommitCompositionChecks).GetMethod(nameof(VoiceTarget), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Run(Action<string, Action> test)
    {
        test("real menu voice recipe commit blocks native Harmony mutation until atomic output completes", PublicationWins);
        test("real voice DisposeBackend Unpatch completes before queued recipe publication without inverted locks", DisposalWins);
    }

    private static void PublicationWins()
    {
        string root = NewDirectory();
        var owner = new Harmony("aave.recipe.composition.voice");
        var foreign = new Harmony("aave.recipe.composition.foreign");
        owner.Patch(Target, prefix: new HarmonyMethod(typeof(RuntimeCommitCompositionChecks), nameof(Prefix)));
        var voice = ReadyVoice();
        string input = Path.Combine(root, "voice-pin.bin"); File.WriteAllText(input, "retained voice content");
        var pin = new CharacterVoiceCommitFilePin(input, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));
        var menu = new RetainedPlaybackProof(() => Harmony.GetPatchInfo(Target) == null ? "missing" : "same", () => { }, _ => { });
        using var menuLease = menu.Acquire();
        using var observer = NewObserver(typeof(CompositionWrapperA), out string token);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var disposeAttempted = new ManualResetEventSlim(); using var patchAttempted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int order = 0, publishedAt = 0, disposedAt = 0, patchedAt = 0;
        string pending = Path.Combine(root, "output.partial"), published = Path.Combine(root, "output.complete");
        File.WriteAllText(pending, "atomic publication fixture");
        Task? publisher = null, disposal = null, mutation = null;
        try
        {
            publisher = RunWorker(() =>
            {
                // Exactly Host order: existing controller scopes first; the
                // real recipe guard owns Harmony then recipe last/innermost.
                using var menuCommit = menu.AcquireCommitScope();
                using var voiceCommit = voice.AcquireCommitScope(() =>
                {
                    Check(Harmony.GetPatchInfo(Target)?.Owners.Contains(owner.Id) == true, "actual voice hook disappeared");
                    return CharacterVoiceCommitFiles.Acquire(new[] { pin });
                });
                using var recipeCommit = observer.EnterCommitScope(token, cancellation.Token);
                entered.Set();
                Check(release.Wait(TimeSpan.FromSeconds(3)), "publication fixture release was not signaled");
                File.Move(pending, published);
                publishedAt = Interlocked.Increment(ref order);
            });
            Check(entered.Wait(TimeSpan.FromSeconds(3)), "real combined publication scope did not enter");
            disposal = RunWorker(() => { disposeAttempted.Set(); voice.DisposeBackend(() =>
            {
                owner.Unpatch(Target, HarmonyPatchType.All, owner.Id); // Actual PatchProcessor.locker path.
                disposedAt = Interlocked.Increment(ref order);
            }); });
            mutation = RunWorker(() =>
            {
                patchAttempted.Set();
                foreign.Patch(Target, postfix: new HarmonyMethod(typeof(RuntimeCommitCompositionChecks), nameof(Postfix)));
                patchedAt = Interlocked.Increment(ref order);
            });
            Check(disposeAttempted.Wait(TimeSpan.FromSeconds(2)) && patchAttempted.Wait(TimeSpan.FromSeconds(2)), "competing operations never started");
            Check(!disposal.Wait(50) && !mutation.Wait(50), "Dispose/Unpatch or Harmony mutation crossed a live atomic publication scope");
            // Live state is read through the real menu gate. Once publication
            // releases, a concurrent unpatch can correctly invalidate it.
            var releaser = RunWorker(() => release.Set());
            try { menu.ValidateActive(); } catch (InvalidOperationException) { }
            releaser.GetAwaiter().GetResult();
            Check(publisher.Wait(TimeSpan.FromSeconds(3)) && disposal.Wait(TimeSpan.FromSeconds(3)) && mutation.Wait(TimeSpan.FromSeconds(3)),
                "combined publication/dispose/patch work deadlocked");
            Check(File.Exists(published) && publishedAt > 0 && disposedAt > publishedAt && patchedAt > publishedAt,
                "mutation preceded completed atomic output publication");
        }
        finally
        {
            release.Set(); cancellation.Cancel();
            WaitFinished(publisher); WaitFinished(disposal); WaitFinished(mutation);
            observer.Dispose(); owner.UnpatchSelf(); foreign.UnpatchSelf(); voice.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static void DisposalWins()
    {
        var owner = new Harmony("aave.recipe.composition.dispose-first");
        owner.Patch(Target, prefix: new HarmonyMethod(typeof(RuntimeCommitCompositionChecks), nameof(Prefix)));
        var voice = ReadyVoice();
        var menu = new RetainedPlaybackProof(() => "same", () => { }, _ => { });
        using var menuLease = menu.Acquire();
        using var observer = NewObserver(typeof(CompositionWrapperB), out string token);
        using var holdsVoice = new ManualResetEventSlim(); using var allowUnpatch = new ManualResetEventSlim();
        using var attemptsVoice = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? refused = null; bool reachedRecipe = false, unpatched = false;
        Task? disposal = null, publisher = null;
        try
        {
            disposal = RunWorker(() => voice.DisposeBackend(() =>
            {
                holdsVoice.Set();
                Check(allowUnpatch.Wait(TimeSpan.FromSeconds(3)), "dispose-first fixture did not release Unpatch");
                owner.Unpatch(Target, HarmonyPatchType.All, owner.Id);
                unpatched = true;
            }));
            Check(holdsVoice.Wait(TimeSpan.FromSeconds(2)), "actual voice DisposeBackend did not own its gate");
            publisher = RunWorker(() =>
            {
                try
                {
                    using var menuCommit = menu.AcquireCommitScope();
                    attemptsVoice.Set();
                    using var voiceCommit = voice.AcquireCommitScope(() => new Noop());
                    using var recipeCommit = observer.EnterCommitScope(token, cancellation.Token);
                    reachedRecipe = true;
                }
                catch (Exception error) { refused = error; }
            });
            Check(attemptsVoice.Wait(TimeSpan.FromSeconds(2)) && !publisher.Wait(50), "publication did not wait for the actual voice gate");
            allowUnpatch.Set();
            Check(disposal.Wait(TimeSpan.FromSeconds(3)) && publisher.Wait(TimeSpan.FromSeconds(3)),
                "voice gate to real Harmony Unpatch inverted against recipe publication");
            Check(unpatched && refused is CharacterVoiceTraceException && !reachedRecipe,
                "disposed voice proof authorized recipe/publication or failed to unpatch");
            observer.ReleaseScope(token);
        }
        finally
        {
            allowUnpatch.Set(); cancellation.Cancel(); WaitFinished(disposal); WaitFinished(publisher);
            observer.Dispose(); owner.UnpatchSelf(); voice.Dispose();
        }
    }

    private static RuntimeRecipeObserver NewObserver(Type wrapper, out string token)
    {
        var observer = new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrapper));
        try
        {
        observer.Install(); Check(observer.IsInstalled, observer.Failure);
        var store = typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1", BindingFlags.NonPublic)!.MakeGenericType(wrapper);
        store.GetField("_initializer", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        var initializer = (Delegate)store.GetProperty("Initializer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Check(observer.Find(initializer).Status == "QUALIFIED", "real observed interop recipe did not qualify");
        var scope = observer.AcquireScope(); Check(scope.Valid, "actual recipe scope was invalid");
        Check(observer.SealScope(scope.Token).Sealed, "actual recipe scope did not seal");
            token = scope.Token; return observer;
        }
        catch { observer.Dispose(); throw; }
    }
    private static CharacterVoiceCommitState ReadyVoice()
    {
        var trace = new CharacterVoiceSessionTrace(new string('a', 64), 30, 48000, "same", Array.Empty<CharacterVoiceAsset>(), noOwnedVoiceExpected: true);
        var receipt = trace.Seal(new CharacterVoicePosition(3, 4800, 0));
        var state = new CharacterVoiceCommitState();
        state.BeginPass(null); state.CompletePass(receipt); state.BeginPass(receipt); state.CompletePass(receipt); state.SealForCommit();
        return state;
    }
    private static string NewDirectory() { string path = Path.Combine(Path.GetTempPath(), "aave-commit-composition-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Task RunWorker(Action action) => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static void WaitFinished(Task? task) { if (task != null) { try { task.Wait(TimeSpan.FromSeconds(4)); } catch { } } }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class Noop : IDisposable { public void Dispose() { } }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int VoiceTarget() => 42;
    public static void Prefix() { }
    public static void Postfix() { }
}

public class CompositionWrapperA : Il2CppObjectBase { public CompositionWrapperA(IntPtr pointer) : base(pointer) { } }
public class CompositionWrapperB : Il2CppObjectBase { public CompositionWrapperB(IntPtr pointer) : base(pointer) { } }
