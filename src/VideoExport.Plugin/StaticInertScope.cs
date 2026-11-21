using System.Reflection;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// One export owns one immutable receipt set, including observed absence. This
// object never creates replacement receipts after acquisition or a failure.
internal sealed class StaticInertScope : IDisposable
{
    internal const string RetainedEvidence = "StaticInertScope.retained.v1";
    private readonly object gate = new();
    private readonly Dictionary<Assembly, StaticInertModEvidence.Receipt> receipts;
    private readonly int ownerThread;
    private ExportException? fault;
    private bool disposed, sealedForCommit;

    private StaticInertScope(Dictionary<Assembly, StaticInertModEvidence.Receipt> receipts)
        => (this.receipts, ownerThread) = (receipts, Environment.CurrentManagedThreadId);

    internal static StaticInertScope Acquire()
    {
        var receipts = new Dictionary<Assembly, StaticInertModEvidence.Receipt>();
        foreach (Assembly assembly in StaticInertModEvidence.LoadedSupportedAssemblies())
        {
            if (!StaticInertModEvidence.TryCreateReceipt(assembly, out var receipt, out string reason) || receipt == null)
                throw new ExportException("strict_static_inert_invalid", "Cannot acquire inspected empty-Mod scope: " + reason);
            receipts.Add(assembly, receipt);
        }
        var scope = new StaticInertScope(receipts);
        scope.ValidateFrame();
        return scope;
    }

    internal IReadOnlyList<string> CaptureProof(Assembly assembly)
    {
        lock (gate)
        {
            ValidateLocked(false);
            if (!receipts.TryGetValue(assembly, out var receipt))
                throw Latch(new InvalidOperationException("The loaded assembly has no retained exact receipt."));
            return new[] { StaticInertModEvidence.Scope, RetainedEvidence, "payload-sha256=" + receipt.PayloadSha256, "cache-reuse-disabled" };
        }
    }

    internal void ValidateFrame()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw Latch(new InvalidOperationException("Static empty-Mod frame validation requires the acquisition thread."));
        lock (gate) ValidateLocked(false);
    }

    internal void ValidateForSeal() => ValidateFrame();
    internal void SealForCommit()
    {
        ValidateFrame();
        lock (gate) { ValidateLocked(false); sealedForCommit = true; }
    }

    // No Unity access. The publication worker rehashes actual payload/framework
    // images and rechecks the same retained receipts, not fresh classifications.
    internal void ValidateForCommit()
    {
        lock (gate)
        {
            if (!sealedForCommit) throw Latch(new InvalidOperationException("Static empty-Mod scope has no completed seal."));
            ValidateLocked(true);
        }
    }

    internal IDisposable AcquireCommitScope(Func<IDisposable?>? preceding = null)
    {
        IDisposable? previous = preceding?.Invoke();
        System.Threading.Monitor.Enter(gate);
        try
        {
            ValidateForCommit();
            return new CommitScope(this, previous, Environment.CurrentManagedThreadId);
        }
        catch
        {
            System.Threading.Monitor.Exit(gate);
            previous?.Dispose();
            throw;
        }
    }

    private void ValidateLocked(bool images)
    {
        if (fault != null) throw fault;
        try
        {
            if (disposed) throw new InvalidOperationException("Static empty-Mod scope was released before validation/commit.");
            var loaded = StaticInertModEvidence.LoadedSupportedAssemblies();
            if (loaded.Length != receipts.Count || loaded.Any(a => !receipts.ContainsKey(a)))
                throw new InvalidOperationException("Inspected empty-Mod set changed after acquisition (including a prior absent module).");
            foreach (var receipt in receipts.Values)
            {
                string reason;
                bool valid = images ? receipt.TryValidate(out reason) : receipt.TryValidateFrame(out reason);
                if (!valid) throw new InvalidOperationException("Retained " + receipt.ModuleName + " receipt failed: " + reason);
            }
        }
        catch (Exception error) { throw Latch(error); }
    }

    private ExportException Latch(Exception error)
    {
        lock (gate) return fault ??= error as ExportException ?? new ExportException("strict_static_inert_invalid", error.Message, error);
    }

    public void Dispose() { lock (gate) { disposed = true; sealedForCommit = false; } }
    private sealed class CommitScope : IDisposable
    {
        private StaticInertScope? owner;
        private readonly IDisposable? previous;
        private readonly int thread;
        internal CommitScope(StaticInertScope owner, IDisposable? previous, int thread)
            => (this.owner, this.previous, this.thread) = (owner, previous, thread);
        public void Dispose()
        {
            if (owner == null) return;
            if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Commit scope must release on its acquiring worker.");
            var current = owner; owner = null;
            System.Threading.Monitor.Exit(current.gate);
            previous?.Dispose();
        }
    }
}
