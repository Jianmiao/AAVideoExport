using System;
using System.Collections.Generic;
using System.Threading;

namespace AAVideoExport.Integration;

/// <summary>
/// Versioned synchronous handoff of Unity rendering settings. Observers must
/// restore their own settings in BeforeAcquire before exporter snapshots occur.
/// Read-only properties are safe to poll; lifecycle operations run on Unity's
/// main thread. A restoration fault remains owned and unavailable until restart.
/// </summary>
public static class RenderControlV1
{
    private static int _mainThread;
    private static int _ready;
    private static int _owned;
    private static int _leases;
    private static bool _transition;
    private static bool _faulted;

    public static int ProtocolVersion => 1;
    public static bool IsReady => Volatile.Read(ref _ready) != 0;
    public static bool IsRenderingOwned => Volatile.Read(ref _owned) != 0;
    public static event Action? BeforeAcquire;
    public static event Action? AfterRelease;

    internal static void BeginHostInitialization()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_mainThread != 0 && _mainThread != thread)
            throw new InvalidOperationException("Render coordination initialized off the Unity thread.");
        if (_leases != 0 || _transition || _faulted || IsReady)
            throw new InvalidOperationException("Render coordination already active or faulted.");
        _mainThread = thread;
        Volatile.Write(ref _ready, 0);
    }

    internal static void CompleteHostInitialization()
    {
        RequireMainThread();
        if (_transition || _faulted) throw new InvalidOperationException("Render coordination cannot become ready.");
        Volatile.Write(ref _ready, 1);
    }

    internal static void BeginShutdown()
    {
        RequireMainThread();
        Volatile.Write(ref _ready, 0);
    }

    internal static void EnsureMainThread() => RequireMainThread();

    internal static void RestorationFailed()
    {
        RequireMainThread();
        _faulted = true;
        Volatile.Write(ref _ready, 0);
        Volatile.Write(ref _owned, 1);
    }

    internal static IDisposable Acquire()
    {
        RequireMainThread();
        if (_transition) throw new InvalidOperationException("Reentrant render ownership transition.");
        if (!IsReady || _faulted) throw new InvalidOperationException("Render coordination is not ready.");
        if (_leases > 0)
        {
            _leases = checked(_leases + 1);
            return new Lease();
        }
        _transition = true;
        _leases = 1;
        Volatile.Write(ref _owned, 1);
        try
        {
            var errors = InvokeAll(BeforeAcquire);
            if (errors.Count == 0) return new Lease();
            // No exporter snapshots or mutations have occurred yet.
            _leases = 0;
            Volatile.Write(ref _owned, 0);
            var releaseErrors = InvokeAll(AfterRelease);
            if (releaseErrors.Count != 0)
            {
                _faulted = true;
                Volatile.Write(ref _ready, 0);
                errors.AddRange(releaseErrors);
            }
            throw new AggregateException("Render acquisition was rejected by a participant.", errors);
        }
        finally { _transition = false; }
    }

    private static void Release()
    {
        RequireMainThread();
        if (_transition) throw new InvalidOperationException("Reentrant render ownership transition.");
        if (_leases <= 0) throw new InvalidOperationException("Render ownership lease imbalance.");
        _leases--;
        if (_leases != 0 || _faulted) return;
        _transition = true;
        Volatile.Write(ref _owned, 0);
        try
        {
            var errors = InvokeAll(AfterRelease);
            if (errors.Count != 0)
            {
                _faulted = true;
                Volatile.Write(ref _ready, 0);
                throw new AggregateException("Render settings were restored, but a release observer failed.", errors);
            }
        }
        finally { _transition = false; }
    }

    private static List<Exception> InvokeAll(Action? callbacks)
    {
        var errors = new List<Exception>();
        if (callbacks == null) return errors;
        foreach (Action callback in callbacks.GetInvocationList())
        {
            try { callback(); }
            catch (Exception error) { errors.Add(error); }
        }
        return errors;
    }

    private static void RequireMainThread()
    {
        if (_mainThread == 0 || Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("Render ownership must be changed on the Unity main thread.");
    }

    private sealed class Lease : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            if (_released) return;
            RequireMainThread();
            if (_transition) throw new InvalidOperationException("Reentrant render lease release.");
            _released = true;
            Release();
        }
    }
}
