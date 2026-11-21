using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using UnityEngine;

internal static class CommitScopeChecks
{
    internal static void InvalidatedAcquisition(Action start, Func<IDisposable> acquire)
    {
        Api.Method("AcquireCommitScope");
        start();
        var lease = acquire();
        var splash = new CreatorSplash();
        splash.Awake();
        int reads = Resources.ReadCount;
        var worker = new AsyncRun(() =>
        {
            RejectCommitScope();
        });
        worker.Join();
        Check(Resources.ReadCount == reads, "The commit worker touched Unity state.");
        Throws<InvalidOperationException>(() => lease.Dispose());
        Check(splash.NativeAwakeRan && splash.InputRestored && !splash.gameObject.activeSelf, "Invalidation suppressed the real Mod effect.");
    }

    internal static void AbsentPublication(Harmony harmony)
    {
        Api.Method("AcquireCommitScope");
        Api.Call("Initialize", harmony);
        Check(Api.Call("TryAcquire") == null, "Expected an absent optional Mod.");
        int reads = Resources.ReadCount;
        using var acquired = new ManualResetEventSlim();
        using var releaseScope = new ManualResetEventSlim();
        using var releaseStarted = new ManualResetEventSlim();
        using var releaseFinished = new ManualResetEventSlim();
        var worker = new AsyncRun(() =>
        {
            using var scope = (IDisposable)Api.Call("AcquireCommitScope")!;
            acquired.Set();
            Wait(releaseScope);
            Api.Call("ValidateForCommit");
        });
        var observer = new AsyncRun(() =>
        {
            Wait(releaseStarted);
            try { Check(!releaseFinished.Wait(150), "Absent proof was released inside a protected publication."); }
            finally { releaseScope.Set(); }
        });
        try
        {
            Wait(acquired);
            releaseStarted.Set();
            Api.Call("ReleaseAbsentSnapshot");
            releaseFinished.Set();
        }
        finally
        {
            releaseStarted.Set();
            releaseScope.Set();
            worker.Join();
            observer.Join();
        }
        Check(Resources.ReadCount == reads, "Publication or its worker inspected Unity state.");
        var lateCommit = new AsyncRun(RejectCommitScope);
        lateCommit.Join();
    }

    internal static void ValidationExcludesPublication(Action start, MethodInfo postfix)
    {
        Api.Method("AcquireCommitScope");
        start();
        int mainThread = Environment.CurrentManagedThreadId;
        int liveChecks = 0;
        bool holdValidation = false;
        using var entered = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        using var published = new ManualResetEventSlim();
        Action validate = () =>
        {
            Check(Environment.CurrentManagedThreadId == mainThread, "Live validation ran on the publication worker.");
            liveChecks++;
            Api.Call("ValidatePatchState", postfix);
            if (!holdValidation) return;
            entered.Set();
            Wait(attempted);
            Check(!published.Wait(150), "Publication crossed an in-progress main-thread validation.");
        };
        var lease = (IDisposable)Api.Call("AcquireValidatedBoundary", "commit-validation-fixture", validate)!;
        int reads = Resources.ReadCount;
        var worker = new AsyncRun(() =>
        {
            Wait(entered);
            attempted.Set();
            using var scope = (IDisposable)Api.Call("AcquireCommitScope")!;
            published.Set();
        });
        try
        {
            holdValidation = true;
            Api.Call("ValidateActive");
        }
        finally
        {
            holdValidation = false;
            entered.Set();
            worker.Join();
        }
        Check(published.IsSet && liveChecks == 2, "Worker publication re-entered live validation.");
        Check(Resources.ReadCount == reads + 1, "Worker publication performed a Unity read.");
        lease.Dispose();
    }

    internal static void WrongThreadDisposal(Harmony harmony)
    {
        Api.Method("AcquireCommitScope");
        Api.Call("Initialize", harmony);
        Check(Api.Call("TryAcquire") == null, "Expected an absent optional Mod.");
        var scope = (IDisposable)Api.Call("AcquireCommitScope")!;
        object gate = Api.Field<object>("Gate");
        try
        {
            var wrongThread = new AsyncRun(() => Throws<InvalidOperationException>(() => scope.Dispose()));
            wrongThread.Join();
            Check(Monitor.IsEntered(gate), "Wrong-thread disposal unlocked publication.");
            Api.Call("ValidateForCommit");
        }
        finally
        {
            scope.Dispose();
            bool leaked = Monitor.IsEntered(gate);
            if (leaked) Monitor.Exit(gate);
            Check(!leaked, "Wrong-thread disposal consumed the owning thread's token.");
        }
        scope.Dispose();
    }

    internal static void PendingAwake(Action start, Func<IDisposable> acquire)
    {
        Api.Method("AcquireCommitScope");
        start();
        var lease = acquire();
        var splash = new CreatorSplash();
        var scope = (IDisposable)Api.Call("AcquireCommitScope")!;
        using var attempted = new ManualResetEventSlim();
        // Only the managed fixture's Awake runs on this second thread. No
        // production Unity lifecycle/check API is called off its host thread.
        var awakeWorker = new AsyncRun(() => { attempted.Set(); splash.Awake(); });
        try
        {
            Wait(attempted);
            Check(!awakeWorker.Finished.Wait(150), "Awake crossed a protected publication.");
            Check(!splash.NativeAwakeRan, "Native Awake ran before monitor admission.");
            Api.Call("ValidateForCommit");
        }
        finally { scope.Dispose(); awakeWorker.Join(); }
        Check(splash.NativeAwakeRan && splash.IsComplete && splash.InputRestored && !splash.gameObject.activeSelf,
            "Releasing publication suppressed native Awake or the real Mod postfix.");
        Throws<InvalidOperationException>(() => Api.Call("ValidateActive"));
        var lateWorker = new AsyncRun(RejectCommitScope);
        lateWorker.Join();
        Throws<InvalidOperationException>(() => lease.Dispose());
    }

    private static void Wait(ManualResetEventSlim signal)
    {
        if (!signal.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Concurrent boundary check did not reach its expected stage.");
    }
    private static void RejectCommitScope()
    {
        IDisposable? unexpectedScope = null;
        try { Throws<InvalidOperationException>(() => unexpectedScope = (IDisposable)Api.Call("AcquireCommitScope")!); }
        finally
        {
            // Recover unexpected success or a leaked mutex on this worker so
            // a regression reports failure instead of hanging suite cleanup.
            unexpectedScope?.Dispose();
            object gate = Api.Field<object>("Gate");
            bool leaked = Monitor.IsEntered(gate);
            while (Monitor.IsEntered(gate)) Monitor.Exit(gate);
            Check(!leaked, "Failed commit acquisition retained its mutex.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }
    private sealed class AsyncRun
    {
        private readonly Thread _thread;
        private Exception? _fault;
        internal readonly ManualResetEventSlim Finished = new();
        internal AsyncRun(Action action)
        {
            _thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception error) { _fault = error; }
                finally { Finished.Set(); }
            }) { IsBackground = true };
            _thread.Start();
        }
        internal void Join()
        {
            if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Concurrent boundary worker remained blocked.");
            if (_fault != null) ExceptionDispatchInfo.Capture(_fault).Throw();
        }
    }
}
