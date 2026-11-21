using AAVideoExport.Core;
namespace AAVideoExport.Plugin;

// Native/framework proof engine is covered by the actual .NET 6.0.7 suite;
// this controllable boundary exercises the production Host lifecycle.
internal sealed class FrameworkGeneratedAssemblyScope : IDisposable
{
    internal static bool Invalidated, RefuseCommit, Unavailable;
    internal static int Acquisitions, Releases, Enters, Validations;
    internal static readonly List<string> ExitOrder = new();
    internal static readonly List<string> AcquisitionOrder = new();
    private bool disposed, consumed, failed;
    internal static void Reset() { Invalidated = RefuseCommit = Unavailable = false; Acquisitions = Releases = Enters = Validations = 0; ExitOrder.Clear(); AcquisitionOrder.Clear(); }
    internal static FrameworkGeneratedAssemblyScope Acquire()
    {
        ProductionAuditTrap.Check("Framework.Acquire");
        if (Unavailable) throw new ExportException("strict_framework_scope_invalid", "Missing cached framework proof.");
        Acquisitions++; AcquisitionOrder.Add("framework"); return new();
    }
    internal void Validate(bool rehashPhysicalImages = false)
    {
        ProductionAuditTrap.Check("Framework.Validate");
        Validations++;
        if (Invalidated) failed = true;
        if (disposed || consumed || failed) throw new ExportException("strict_framework_scope_invalid", "Held framework scope is unavailable.");
    }
    internal IDisposable EnterCommitScope(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); Validate(true);
        if (RefuseCommit) throw new ExportException("strict_framework_scope_invalid", "Framework guard refused.");
        Enters++; RuntimeAttributionClient.CommitOrder.Add("framework"); return new Guard(this);
    }
    public void Dispose() { if (disposed) return; disposed = true; Releases++; }
    private sealed class Guard(FrameworkGeneratedAssemblyScope owner) : IDisposable
    {
        public void Dispose() { owner.consumed = true; ExitOrder.Add("framework"); }
    }
}
