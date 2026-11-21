using AAVideoExport.Core;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private FrameworkGeneratedAssemblyScope? _frameworkScope;
    private bool _frameworkHandedToWorker;

    [HideFromIl2Cpp]
    private void AcquireFrameworkBoundary()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        try
        {
            // Its own monitor installation must precede the recipe snapshot.
            // Keep the same exact cached-Assembly proof through both passes.
            _frameworkScope ??= FrameworkGeneratedAssemblyScope.Acquire();
            _frameworkScope.Validate();
        }
        catch (Exception error) { throw FrameworkFailure(error); }
    }

    [HideFromIl2Cpp]
    private void ValidateFrameworkFrame(bool required = false, bool rehashPhysicalImages = false)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (_frameworkHandedToWorker || _strictStartupBoundaryCancelling) return;
        if (_frameworkScope == null)
        {
            if (required) throw new ExportException("strict_framework_scope_invalid", "Strict capture has no held framework bridge scope.");
            return;
        }
        try { _frameworkScope.Validate(rehashPhysicalImages); }
        catch (Exception error) { throw FrameworkFailure(error); }
    }

    [HideFromIl2Cpp]
    private void WireFrameworkPublication(ExportSession session)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (_options?.StrictSynchronization != true) return;
        var framework = _frameworkScope ?? throw new ExportException("strict_framework_scope_invalid", "Strict publication has no held framework bridge scope.");
        framework.Validate(true);
        var precedingValidation = session.BeforeOutputCommit;
        session.BeforeOutputCommit = () =>
        {
            precedingValidation?.Invoke();
            try { framework.Validate(true); }
            catch (Exception error) { throw FrameworkFailure(error); }
        };
        var precedingScope = session.AcquireOutputCommitScope;
        var cancellation = _cancellation?.Token ?? CancellationToken.None;
        session.AcquireOutputCommitScope = () =>
        {
            var scopes = new List<IDisposable>();
            try
            {
                // Controller guards -> Harmony mutex / framework gate ->
                // recipe guard (composed by WireRecipePublication).
                if (precedingScope?.Invoke() is { } outer) scopes.Add(outer);
                scopes.Add(framework.EnterCommitScope(cancellation));
                return new RetainedCommitGroup(scopes);
            }
            catch
            {
                for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
                throw;
            }
        };
        _frameworkHandedToWorker = true;
    }

    [HideFromIl2Cpp]
    private static ExportException FrameworkFailure(Exception error) => error as ExportException
        ?? new ExportException("strict_framework_scope_invalid", error.Message, error);
}
