using AAVideoExport.Core;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private RetainedRecipeScope? _recipeScope;
    private bool _recipeHandedToWorker;

    [HideFromIl2Cpp]
    private void AcquireRecipeBoundary()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        _recipeScope ??= RetainedRecipeScope.Acquire(Plugin.LogPerformance);
        _recipeScope.ValidateFrame();
    }
    [HideFromIl2Cpp]
    private void ValidateRecipeFrame(bool required = false)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (_recipeHandedToWorker || _strictStartupBoundaryCancelling) return;
        if (_recipeScope == null)
        {
            if (required) throw new ExportException("strict_recipe_scope_invalid", "Strict capture has no retained recipe scope.");
            return;
        }
        _recipeScope.ValidateFrame();
    }
    [HideFromIl2Cpp]
    private void WireRecipePublication(ExportSession session)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        if (_options?.StrictSynchronization != true) return;
        var recipes = _recipeScope ?? throw new ExportException("strict_recipe_scope_invalid", "Strict publication has no retained recipe scope.");
        recipes.ValidateFrame();
        var precedingValidation = session.BeforeOutputCommit;
        session.BeforeOutputCommit = () =>
        {
            // Native playback has ended before this worker starts. Keep
            // collecting through mux/verification and earlier receipt work;
            // seal only at terminal verification, never in preparation identity.
            precedingValidation?.Invoke();
            var terminal = recipes.SealForPublication();
            session.RuntimeFactoryRecipe = new RuntimeFactoryRecipeReceipt(terminal.Policy, terminal.EvidenceKind, terminal.PolicySha256, terminal.RecipesSha256);
        };
        var precedingScope = session.AcquireOutputCommitScope;
        var cancellation = _cancellation?.Token ?? CancellationToken.None;
        session.AcquireOutputCommitScope = () =>
        {
            var scopes = new List<IDisposable>();
            try
            {
                // Established and tested order: Startup -> StaticInert ->
                // Menu -> Voice -> Framework -> real Harmony mutex -> recipe gate.
                if (precedingScope?.Invoke() is { } outer) scopes.Add(outer);
                scopes.Add(recipes.AcquireCommitScope(cancellation));
                return new RetainedCommitGroup(scopes);
            }
            catch { for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose(); throw; }
        };
        _recipeHandedToWorker = true;
    }
    [HideFromIl2Cpp]
    private async Task<ExportResult> CompleteWithRecipe(ExportSession session, Task writerDone, CancellationToken token,
        RetainedRecipeScope? recipe, FrameworkGeneratedAssemblyScope? framework)
    {
        try { await writerDone.ConfigureAwait(false); return session.Complete(token); }
        finally
        {
            // Managed-only release also runs when media verification, sealing,
            // guard acquisition, or cancellation prevents publication.
            try { recipe?.Dispose(); }
            finally { framework?.Dispose(); }
        }
    }
}
