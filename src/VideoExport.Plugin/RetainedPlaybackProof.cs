namespace AAVideoExport.Plugin;

// Shared retained-state engine for exact inspected menu/resource boundaries.
// It does not classify unknown code and never advances a media clock.
internal sealed class RetainedPlaybackProof
{
    private readonly object gate = new();
    private readonly ManualResetEventSlim released = new(true);
    private readonly Func<string> readIdentity;
    private readonly Action validatePlayback;
    private readonly Action<Exception> onFault;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<IDisposable> resources = new();
    private bool held, playback, closing, notified;
    private int writers;
    private long generation;
    private string identity = "";
    private Exception? fault;

    internal RetainedPlaybackProof(Func<string> readIdentity, Action validatePlayback, Action<Exception> onFault)
    {
        this.readIdentity = readIdentity ?? throw new ArgumentNullException(nameof(readIdentity));
        this.validatePlayback = validatePlayback ?? throw new ArgumentNullException(nameof(validatePlayback));
        this.onFault = onFault ?? throw new ArgumentNullException(nameof(onFault));
    }
    internal bool IsHeld { get { lock (gate) return held; } }
    internal bool IsPlayback { get { lock (gate) return playback; } }
    internal int ObservedWrites { get { lock (gate) return writers; } }
    internal IDisposable Acquire(IEnumerable<IDisposable>? retainedResources = null)
    {
        MainThread();
        lock (gate)
        {
            if (held || closing || writers != 0) throw new InvalidOperationException("Retained playback boundary is already owned or an observed config write is still executing.");
            string next = readIdentity();
            if (string.IsNullOrWhiteSpace(next)) throw new InvalidOperationException("Retained identity is empty.");
            resources.Clear(); if (retainedResources != null) resources.AddRange(retainedResources);
            identity = next; fault = null; notified = false; held = true; playback = false;
            released.Reset();
            return new Lease(this, ++generation);
        }
    }
    internal void BeginPlayback(Action nativeSceneExit)
    {
        MainThread(); ValidateActive();
        lock (gate) { Require(); if (playback) throw new InvalidOperationException("Playback boundary is already active."); }
        try
        {
            nativeSceneExit(); // Original Mod lifecycle, never a disabled component.
            lock (gate) { Require(); validatePlayback(); playback = true; }
        }
        catch (Exception error) { Latch(error); throw; }
    }
    internal void ValidateActive()
    {
        MainThread();
        try
        {
            lock (gate)
            {
                Require();
                if (!string.Equals(identity, readIdentity(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Retained menu/resource inputs or patch identity changed during export.");
                if (playback) validatePlayback();
            }
        }
        catch (Exception error) { Latch(error); throw; }
    }
    internal void EndPlayback() { MainThread(); lock (gate) playback = false; }
    internal string CaptureActiveProof() { ValidateActive(); lock (gate) { Require(); return identity; } }
    internal void ValidateForCommit() { lock (gate) { Require(); if (playback) throw new InvalidOperationException("Playback has not restored before publication."); } }
    internal IDisposable AcquireCommitScope()
    {
        Monitor.Enter(gate);
        try { ValidateForCommit(); return new CommitScope(gate); }
        catch { Monitor.Exit(gate); throw; }
    }
    internal void Mutation(string operation)
    {
        lock (gate) { if (!held) return; fault ??= new InvalidOperationException("Retained playback mutation: " + operation); }
        Notify(); // Original user operation still executes; invalid output cannot publish.
        if (Environment.CurrentManagedThreadId == ownerThread) ReleaseFaultedInputs();
    }
    internal void BeforeWrite()
    {
        while (true)
        {
            lock (gate)
            {
                if (!held) { writers++; return; }
                if (Environment.CurrentManagedThreadId == ownerThread)
                {
                    fault ??= new InvalidOperationException("Main-thread configuration write began during retained export.");
                    writers++; break;
                }
            }
            released.Wait(); // Resume the exact original worker write after release.
        }
        Notify();
        ReleaseFaultedInputs();
    }
    internal void AfterWrite()
    {
        lock (gate)
        {
            if (writers <= 0) throw new InvalidOperationException("Observed writer ownership is unbalanced.");
            writers--;
        }
    }
    private void Latch(Exception error) { lock (gate) { if (held) fault ??= error; } Notify(); }
    private void ReleaseFaultedInputs()
    {
        lock (gate)
        {
            if (!held || fault == null) return;
            foreach (var resource in resources.ToArray())
            {
                try { resource.Dispose(); resources.Remove(resource); }
                catch { /* Retain failed resources for the ordinary cleanup retry. */ }
            }
        }
    }
    private void Notify()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) return;
        Exception? error;
        lock (gate) { if (notified || fault == null) return; notified = true; error = fault; }
        try { onFault(error); } catch { /* The retained fault still rejects worker publication. */ }
    }
    private void Require()
    {
        if (!held || closing) throw new InvalidOperationException("No active retained playback proof is available.");
        if (fault != null) throw new InvalidOperationException("Retained playback proof has failed.", fault);
    }
    private void MainThread()
    { if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Live retained playback state requires its initialization thread."); }
    private void Release(long leaseGeneration)
    {
        MainThread();
        lock (gate)
        {
            if (!held || generation != leaseGeneration) return;
            closing = true; playback = false;
            List<Exception>? errors = null;
            foreach (var resource in resources.ToArray())
            {
                try { resource.Dispose(); resources.Remove(resource); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            }
            if (errors != null) { fault ??= new AggregateException("Retained input cleanup remains pending.", errors); throw fault; }
            held = closing = false; identity = ""; released.Set();
        }
    }
    private sealed class Lease(RetainedPlaybackProof owner, long generation) : IDisposable
    { public void Dispose() => owner.Release(generation); }
    private sealed class CommitScope(object gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; Monitor.Exit(gate); disposed = true; }
    }
}
