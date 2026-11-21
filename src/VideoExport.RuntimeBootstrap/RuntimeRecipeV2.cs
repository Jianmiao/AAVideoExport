using System.Text.Json;
namespace AAVideoExport.RuntimeBootstrap;

// A separate immutable recipe/retained-scope API. It never changes the truthful
// v1 diagnostic flags and never grants whole-Mod/strict movie authorization.
public static class RuntimeRecipeV2
{
    private static RuntimeRecipeObserver? observer;
    private static string unavailable="Recipe observation was not requested.";
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public static int ProtocolVersion=>2;
    public static string EvidenceKind=>InteropRecipePolicy.EvidenceKind;
    public static bool IsInstalled=>observer?.IsInstalled==true;
    public static string GetSnapshotJson()=>JsonSerializer.Serialize(observer?.Snapshot()??Unavailable(""),Json);
    public static string GetRecipeJson(Delegate value)=>JsonSerializer.Serialize(observer?.Find(value)??new RecipeView(0,"","UNKNOWN",unavailable,"",Array.Empty<string>(),Array.Empty<RecipeInstruction>(),"",null),Json);
    public static string AcquireScopeJson()=>JsonSerializer.Serialize(observer?.AcquireScope()??Unavailable(""),Json);
    public static string SealScopeJson(string token)=>JsonSerializer.Serialize(observer?.SealScope(token)??Unavailable(token),Json);
    public static string ValidateScopeJson(string token)=>JsonSerializer.Serialize(observer?.ValidateScope(token,false)??Unavailable(token),Json);
    public static string ValidateScopeStatusJson(string token)=>JsonSerializer.Serialize(observer?.ValidateScopeStatus(token)??Unavailable(token),Json);
    public static string ValidateScopeForCommitJson(string token)=>JsonSerializer.Serialize(observer?.ValidateScope(token,true,true)??Unavailable(token),Json);
    public static void ReleaseScope(string token)=>observer?.ReleaseScope(token);
    public static bool ValidateHolder(string token,System.Reflection.Assembly candidate)=>candidate!=null&&observer?.ValidateHolder(token,candidate)==true;
    public static IDisposable EnterCommitScope(string token)=>observer?.EnterCommitScope(token)??throw new InvalidOperationException(unavailable);
    public static IDisposable EnterCommitScope(string token,CancellationToken cancellationToken)=>observer?.EnterCommitScope(token,cancellationToken)??throw new InvalidOperationException(unavailable);
    private static RecipeScopeView Unavailable(string token)=>new("aave.runtime-recipes.v2",token,false,unavailable,"aa101.interop-initializer.intptr.final-il.v2","","",false,false,false,false,false,Array.Empty<RecipeView>());
    internal static void InitializeFromEnvironment()
    {
        StartupTrace.Write("v2.Initialize.enter");
        if(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES")!="1")
        {StartupTrace.Write("v2.Initialize.disabled");return;}
        if(observer!=null){StartupTrace.Write("v2.Initialize.already-created");return;}
        try
        {
            // Warm serialization before complete emission coverage starts.
            StartupTrace.Write("v2.Serialization.before");
            _=JsonSerializer.Serialize(Unavailable(""),Json);
            StartupTrace.Write("v2.Serialization.after");
            StartupTrace.Write("v2.Policy.before");
            var policy=InteropRecipePolicy.ForHost(BepInEx.Paths.GameRootPath);
            StartupTrace.Write("v2.Policy.after");
            StartupTrace.Write("v2.Observer.before");
            observer=new RuntimeRecipeObserver(policy);
            StartupTrace.Write("v2.Observer.after");
            observer.Install();
            if(!observer.IsInstalled)unavailable=observer.Failure;
            StartupTrace.Write("v2.Initialize.complete",observer.IsInstalled?"installed":"unavailable");
        }
        catch(Exception error){StartupTrace.Write("v2.Initialize.error",error.GetType().FullName??"Exception");unavailable=error.GetType().Name+": "+error.Message;}
    }
}
