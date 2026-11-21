using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;

int passed = 0, failed = 0;
void Test(string name, Action run) { try { run(); passed++; Console.WriteLine("PASS " + name); } catch(Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
var holder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Anonymously Hosted DynamicMethods Assembly"), AssemblyBuilderAccess.Run);
var impostor = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Anonymously Hosted DynamicMethods Assembly"), AssemblyBuilderAccess.Run);
Test("only the actual validated holder object receives factory-scoped evidence", () =>
{
    var backend = new Backend(holder); using var scope = RetainedRecipeScope.Acquire(backend);
    Check(scope.TryClassifyHolder(holder, out var actual) && actual!.Level == StrictCapabilityLevel.Observed, "actual holder was not classified");
    Check(!scope.TryClassifyHolder(impostor, out _), "same-name fake holder bypassed assembly-reference proof");
});
Test("collecting recipe additions preserve policy-based identity across passes and fresh tokens", () =>
{
    var backend = new Backend(holder); string identity;
    using (var scope = RetainedRecipeScope.Acquire(backend))
    {
        scope.TryClassifyHolder(holder, out var first); identity = StrictCapabilitySnapshot.Create(new[] { first! }).Fingerprint;
        backend.RecipesHash = new string('c', 64); scope.ValidateFrame(); scope.TryClassifyHolder(holder, out var later);
        Check(StrictCapabilitySnapshot.Create(new[] { later! }).Fingerprint == identity, "collecting recipes drifted preparation identity");
        var terminal = scope.SealForPublication(); Check(terminal.RecipesSha256 == new string('c',64), "sealed recipe hash was not retained");
    }
    using var next = RetainedRecipeScope.Acquire(backend); next.TryClassifyHolder(holder, out var current);
    Check(backend.Acquisitions == 2 && StrictCapabilitySnapshot.Create(new[] { current! }).Fingerprint == identity, "new scope token/MVID leaked into model identity");
});
Test("late invalidation cannot be healed by the same held scope", () =>
{
    var backend = new Backend(holder); using var scope = RetainedRecipeScope.Acquire(backend);
    backend.Valid = false; Reject(scope.ValidateFrame); backend.Valid = true;
    Reject(scope.ValidateFrame); Reject(() => scope.SealForPublication());
    Check(backend.Acquisitions == 1, "failed scope silently reacquired");
});
Test("recipe guard requires terminal seal and cancellation returns no lease", () =>
{
    var backend = new Backend(holder); using var scope = RetainedRecipeScope.Acquire(backend);
    Reject(() => scope.AcquireCommitScope(CancellationToken.None));
    using var fresh = RetainedRecipeScope.Acquire(new Backend(holder)); fresh.SealForPublication();
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Reject(() => fresh.AcquireCommitScope(cancellation.Token));
});
Test("release failure keeps retry ownership and released proof cannot authorize a frame", () =>
{
    var backend = new Backend(holder); var scope = RetainedRecipeScope.Acquire(backend);
    backend.ReleaseSucceeds = false; Reject(scope.Dispose); backend.ReleaseSucceeds = true; scope.Dispose();
    Check(backend.Releases == 2, "release retry lost the original token"); Reject(scope.ValidateFrame);
});
Test("old policy evidence and false host or broad coverage claims are rejected at acquisition", () =>
{
    foreach(string kind in new[] { "host", "preexisting", "allcode", "permission", "old-policy", "old-kind" })
    {
        var backend = new Backend(holder) { InvalidClaim = kind }; Reject(() => RetainedRecipeScope.Acquire(backend));
        Check(backend.Releases == 1, "invalid scope token was leaked");
    }
});
Console.WriteLine($"RESULT: {passed} passed; {failed} failed."); return failed == 0 ? 0 : 1;
static void Check(bool valid,string reason){if(!valid)throw new Exception(reason);}
static void Reject(Action run){try{run();}catch{return;}throw new Exception("invalid recipe proof was accepted");}
sealed class Backend(Assembly holder) : IRecipeScopeBackend
{
    internal bool Valid = true, ReleaseSucceeds = true; internal int Acquisitions, Releases; internal string InvalidClaim = "", RecipesHash = new('b',64);
    private string token = ""; private bool sealedScope, released;
    private string Json() => JsonSerializer.Serialize(new { schema="aave.runtime-recipes.v2", token, valid=Valid&&!released, reason="fixture", policy=InvalidClaim=="old-policy"?"aa101.interop-initializer.intptr.v1":RuntimeAttributionClient.ExpectedRecipePolicy,evidenceKind=InvalidClaim=="old-kind"?"EMIT_TRACE":RuntimeAttributionClient.ExpectedRecipeEvidenceKind,
        policySha256=new string('a',64), recipesSha256=sealedScope?RecipesHash:"", knownHostBaseline=InvalidClaim!="host", @sealed=sealedScope,
        preexistingCoverage=InvalidClaim=="preexisting", allCodeCoverage=InvalidClaim=="allcode", permitsStrictExport=InvalidClaim=="permission" });
    public bool Acquire(out string value,out string evidence){token="token-"+(++Acquisitions);released=false;sealedScope=false;value=token;evidence=Json();return true;}
    public bool Validate(string value,bool commit,out string evidence){evidence=Json();return value==token&&Valid&&!released&&(!commit||sealedScope);}
    public bool Seal(string value,out string evidence){sealedScope=true;evidence=Json();return value==token&&Valid&&!released;}
    public bool Holder(string value,Assembly candidate)=>value==token&&ReferenceEquals(candidate,holder)&&Valid&&!released;
    public bool Enter(string value,CancellationToken cancellation,out IDisposable? lease){lease=null;if(cancellation.IsCancellationRequested||!sealedScope||!Valid||released)return false;lease=new Guard(()=>released=true);return true;}
    public bool Release(string value){Releases++;if(!ReleaseSucceeds)return false;released=true;return true;}
    private sealed class Guard(Action release):IDisposable{public void Dispose()=>release();}
}
