using System.Reflection;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

internal interface IRecipeScopeBackend
{
    bool Acquire(out string token, out string evidence);
    bool Validate(string token, bool commit, out string evidence);
    bool Seal(string token, out string evidence);
    bool Holder(string token, Assembly assembly);
    bool Enter(string token, CancellationToken cancellation, out IDisposable? lease);
    bool Release(string token);
}
internal sealed record RecipeTerminalProof(string Policy, string EvidenceKind, string PolicySha256, string RecipesSha256);
internal sealed class RetainedRecipeScope : IDisposable
{
    internal const string Marker = "RetainedRecipeScope.interop-initializer-final-il.v2";
    private readonly IRecipeScopeBackend backend;
    private readonly string token;
    private readonly string policy;
    private int disposed, consumed;
    private Exception? failure;
    private RecipeTerminalProof? terminal;
    internal string PolicySha256 => policy;
    internal bool IsSealed => Volatile.Read(ref terminal) != null;
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    private RetainedRecipeScope(IRecipeScopeBackend backend, string token, string policy) { this.backend = backend; this.token = token; this.policy = policy; }
    internal static RetainedRecipeScope Acquire(Action<string> report) => Acquire(new ReflectionBackend(report));
    internal static RetainedRecipeScope Acquire(IRecipeScopeBackend backend)
    {
        string token = "";
        try
        {
            if (!backend.Acquire(out token, out string evidence)) throw Invalid("Runtime recipe observer is unavailable.");
            var view = Read(evidence, token);
            if (view.Sealed) throw Invalid("A new recipe scope was already sealed.");
            return new RetainedRecipeScope(backend, token, view.Policy);
        }
        catch
        {
            if (!string.IsNullOrEmpty(token)) backend.Release(token);
            throw;
        }
    }
    internal void ValidateFrame()
    {
        try
        {
            RequireLive();
            if (IsSealed) throw Invalid("A sealed recipe scope cannot authorize another capture frame.");
            if (!backend.Validate(token, false, out string evidence)) throw Invalid("Retained recipe observations changed or became incomplete.");
            var view = Read(evidence, token);
            if (view.Policy != policy || view.Sealed) throw Invalid("Recipe policy/collecting phase changed inside the retained scope.");
        }
        catch (Exception error) { Latch(error); throw; }
    }
    internal bool TryClassifyHolder(Assembly assembly, out StrictCapability? capability)
    {
        capability = null; ValidateFrame();
        if (!backend.Holder(token, assembly)) return false;
        capability = new StrictCapability(assembly.GetName().Name ?? "<anonymous>", policy, StrictCapabilityLevel.Observed,
            new[] { Marker, "policy=" + policy, "observed-holder-reference", "finite-interop-factory-only",
                "preexistingCoverage=false", "allCodeCoverage=false", "permitsStrictExport=false", "cache-reuse-disabled" });
        return true;
    }
    internal RecipeTerminalProof SealForPublication()
    {
        try
        {
            RequireLive();
            if (terminal != null) return terminal;
            if (!backend.Seal(token, out string evidence)) throw Invalid("Runtime recipes could not seal after playback.");
            var view = Read(evidence, token);
            if (!view.Sealed || view.Policy != policy) throw Invalid("Terminal recipe proof changed policy or did not seal.");
            RequireLive();
            terminal = new RecipeTerminalProof(RuntimeAttributionClient.ExpectedRecipePolicy, RuntimeAttributionClient.ExpectedRecipeEvidenceKind, policy, view.Recipes);
            return terminal;
        }
        catch (Exception error) { Latch(error); throw; }
    }
    internal IDisposable AcquireCommitScope(CancellationToken cancellation)
    {
        try
        {
            RequireLive(); cancellation.ThrowIfCancellationRequested();
            if (terminal == null) throw Invalid("Runtime recipes were not sealed for publication.");
            // Do not hold a local/controller gate across the remote call. The
            // caller has acquired other controllers first; backend takes the
            // real Harmony locker and recipe gate last, cancellation-aware.
            if (!backend.Enter(token, cancellation, out var lease) || lease == null)
            {
                cancellation.ThrowIfCancellationRequested();
                throw Invalid("Runtime recipe publication guard was refused.");
            }
            return new Guard(this, lease);
        }
        catch (Exception error) { Latch(error); throw; }
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        bool released = backend.Release(token);
        if (!released && Volatile.Read(ref consumed) == 0)
        {
            var error = Invalid("Runtime recipe token release remains pending; the original token is retained for retry.");
            Latch(error); throw error;
        }
        Volatile.Write(ref disposed, 1);
    }
    private void RequireLive()
    {
        if (IsDisposed || Volatile.Read(ref consumed) != 0) throw Invalid("Runtime recipe scope was released or consumed.");
        if (Volatile.Read(ref failure) is { } error) throw Invalid("Runtime recipe scope has a latched failure.", error);
    }
    private void Latch(Exception error) => Interlocked.CompareExchange(ref failure, error, null);
    private static ExportException Invalid(string message, Exception? inner = null) => new("strict_recipe_scope_invalid", message, inner);
    private static (string Policy, string Recipes, bool Sealed) Read(string json, string expectedToken)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        string policy = root.GetProperty("policySha256").GetString() ?? "";
        string recipes = root.GetProperty("recipesSha256").GetString() ?? "";
        bool sealedScope = root.GetProperty("sealed").GetBoolean();
        bool Hash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (string.IsNullOrEmpty(expectedToken) || root.GetProperty("token").GetString() != expectedToken
            || root.GetProperty("schema").GetString() != "aave.runtime-recipes.v2" || !root.GetProperty("valid").GetBoolean()
            || !root.GetProperty("knownHostBaseline").GetBoolean() || root.GetProperty("policy").GetString() != RuntimeAttributionClient.ExpectedRecipePolicy
            || root.GetProperty("evidenceKind").GetString() != RuntimeAttributionClient.ExpectedRecipeEvidenceKind
            || !Hash(policy) || (sealedScope ? !Hash(recipes) : recipes.Length != 0)
            || root.GetProperty("preexistingCoverage").GetBoolean() || root.GetProperty("allCodeCoverage").GetBoolean()
            || root.GetProperty("permitsStrictExport").GetBoolean())
            throw Invalid("Runtime recipe evidence does not match its limited host/factory contract.");
        return (policy.ToLowerInvariant(), recipes.ToLowerInvariant(), sealedScope);
    }
    private sealed class Guard(RetainedRecipeScope owner, IDisposable inner) : IDisposable
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private bool done;
        public void Dispose()
        {
            if (done) return;
            if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Recipe guard must leave on its acquiring worker.");
            inner.Dispose(); done = true; Volatile.Write(ref owner.consumed, 1);
        }
    }
    private sealed class ReflectionBackend(Action<string> report) : IRecipeScopeBackend
    {
        public bool Acquire(out string token, out string evidence) => RuntimeAttributionClient.TryAcquireRecipeScope(out token, out evidence, report);
        public bool Validate(string token, bool commit, out string evidence) => RuntimeAttributionClient.TryValidateRecipeScope(token, commit, out evidence, report);
        public bool Seal(string token, out string evidence) => RuntimeAttributionClient.TrySealRecipeScope(token, out evidence, report);
        public bool Holder(string token, Assembly assembly) => RuntimeAttributionClient.TryValidateRecipeHolder(token, assembly, report);
        public bool Enter(string token, CancellationToken cancellation, out IDisposable? lease) => RuntimeAttributionClient.TryEnterRecipeCommitScope(token, cancellation, out lease, report);
        public bool Release(string token)
        {
            bool failed = false;
            RuntimeAttributionClient.ReleaseRecipeScope(token, message => { failed = true; report(message); });
            return !failed;
        }
    }
}
