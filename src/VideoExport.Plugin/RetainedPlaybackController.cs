namespace AAVideoExport.Plugin;

// Functional resource ownership without provenance readers or commit audits.
internal sealed class RetainedPlaybackController(Action validatePlayback, Action<Exception> onFault)
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly List<IDisposable> resources = new();
    private bool held, playback, closing, notified;
    private long generation;
    private Exception? failure;
    internal bool IsHeld => held;
    internal bool IsPlayback => playback;
    internal IDisposable Acquire(IEnumerable<IDisposable>? retainedResources = null)
    {
        MainThread(); if (held || closing) throw new InvalidOperationException("Playback resources are already retained.");
        resources.Clear(); if (retainedResources != null) resources.AddRange(retainedResources);
        held = true; playback = closing = notified = false; failure = null;
        return new Lease(this, ++generation);
    }
    internal void BeginPlayback(Action nativeSceneExit)
    {
        MainThread(); Require(); if (playback) throw new InvalidOperationException("Playback resources are already active.");
        try { nativeSceneExit(); validatePlayback(); playback = true; } catch (Exception error) { Fail(error); throw; }
    }
    internal void ValidateActive()
    {
        MainThread(); Require(); try { if (playback) validatePlayback(); } catch (Exception error) { Fail(error); throw; }
    }
    internal void EndPlayback() { MainThread(); playback = false; }
    private void MainThread() { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Playback resource lifecycle requires its owning thread."); }
    private void Require()
    {
        if (!held || closing) throw new InvalidOperationException("Playback resources are not acquired.");
        if (failure != null) throw new InvalidOperationException("Playback resource operation failed.", failure);
    }
    private void Fail(Exception error)
    {
        failure ??= error; if (!notified) { notified = true; try { onFault(error); } catch { } }
    }
    private void Release(long leaseGeneration)
    {
        MainThread(); if (!held || generation != leaseGeneration) return;
        closing = true; playback = false; List<Exception>? errors = null;
        foreach (var resource in resources.ToArray())
            try { resource.Dispose(); resources.Remove(resource); } catch (Exception error) { (errors ??= new()).Add(error); }
        if (errors != null) { var error = new AggregateException("Playback resource cleanup remains pending.", errors); Fail(error); throw error; }
        held = closing = false;
    }
    private sealed class Lease(RetainedPlaybackController owner, long generation) : IDisposable
    { public void Dispose() => owner.Release(generation); }
}
