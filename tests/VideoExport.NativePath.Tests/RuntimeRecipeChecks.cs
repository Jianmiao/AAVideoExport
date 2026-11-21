using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AAVideoExport.NativePath.Tests;

// Standalone native metadata/emission acceptance. Root must explicitly route
// a recipe-probe mode to this component; it does not launch AA or change Mods.
public sealed class RuntimeRecipeChecks : MonoBehaviour
{
    private const BindingFlags PublicStatic=BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly;
    private readonly Stopwatch wall=Stopwatch.StartNew();
    private Type? api;
    private string token="",policyHash="";
    private int stage,settle,observations,passed,failed;
    private bool finished;
    private Task? commit;
    public RuntimeRecipeChecks(IntPtr pointer):base(pointer){}
    public void Update()
    {
        if(finished)return;
        try
        {
            if(wall.Elapsed.TotalSeconds>90)throw new TimeoutException("Native recipe proof did not finish within 90 seconds.");
            if(stage==0)
            {
                if(SceneManager.GetActiveScene().name!="CatalogScene"||UserSettings.Instance?.data==null||ScenarioResourceManager.Instance==null||ScenarioResourceManager.Instance.Preloading)return;
                if(++settle<30)return;
                var bootstrap=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="AAVideoExport.RuntimeBootstrap");
                api=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeV2",true)!;
                Check(api.GetProperty("ProtocolVersion",PublicStatic)?.GetValue(null) is 2&&api.GetProperty("IsInstalled",PublicStatic)?.GetValue(null) is true,"v2 observed recipes installed before ordinary Mods");
                var legacy=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1",true)!;
                using(var v1=JsonDocument.Parse((string)legacy.GetMethod("GetSnapshotJson",PublicStatic)!.Invoke(null,null)!))
                    Check(!v1.RootElement.GetProperty("preexistingCoverage").GetBoolean()&&!v1.RootElement.GetProperty("allCodeCoverage").GetBoolean()&&!v1.RootElement.GetProperty("permitsStrictExport").GetBoolean(),"v1 keeps all global coverage/authorization flags false");
                Save("recipes-start.json",Call("GetSnapshotJson"));
                string acquired=Call("AcquireScopeJson");Save("recipe-acquired.json",acquired);
                using var scope=JsonDocument.Parse(acquired);RequireScope(scope.RootElement,false);
                token=scope.RootElement.GetProperty("token").GetString()!;policyHash=scope.RootElement.GetProperty("policySha256").GetString()!;
                Check(scope.RootElement.GetProperty("recipes").GetArrayLength()>0,"current anonymous recipes are present");
                CheckBindingsAndExpectedTargets(scope.RootElement);
                stage=1;return;
            }
            if(stage==1)
            {
                using(var live=JsonDocument.Parse(Call("ValidateScopeJson",token)))
                {RequireScope(live.RootElement,false);if(live.RootElement.GetProperty("policySha256").GetString()!=policyHash)throw new InvalidOperationException("Policy identity changed during native observation.");}
                if(++observations<15)return;
                string sealedJson=Call("SealScopeJson",token);Save("recipe-sealed.json",sealedJson);
                using(var sealedScope=JsonDocument.Parse(sealedJson))
                {RequireScope(sealedScope.RootElement,true);Check(sealedScope.RootElement.GetProperty("recipesSha256").GetString()?.Length==64,"final semantic recipe set has a separate stable digest");}
                string workerToken=token;Type workerApi=api!;string marker=Path.Combine(Plugin.DiagnosticsRoot,"recipe-publication-probe.txt");
                commit=Task.Run(()=>
                {
                    var enter=workerApi.GetMethod("EnterCommitScope",PublicStatic,null,new[]{typeof(string),typeof(CancellationToken)},null)!;
                    using var guard=(IDisposable)enter.Invoke(null,new object[]{workerToken,CancellationToken.None})!;
                    File.WriteAllText(marker,"Finite recipe publication-guard diagnostic; not a movie.\n");
                });
                stage=2;return;
            }
            if(stage==2&&commit!.IsCompleted)
            {
                commit.GetAwaiter().GetResult();
                using(var consumed=JsonDocument.Parse(Call("ValidateScopeJson",token)))
                    Check(!consumed.RootElement.GetProperty("valid").GetBoolean(),"synchronous worker guard consumed its token before unlocking");
                token="";
                string final=Call("AcquireScopeJson");Save("recipe-final-reacquired.json",final);
                using(var scope=JsonDocument.Parse(final)){RequireScope(scope.RootElement,false);CallVoid("ReleaseScope",scope.RootElement.GetProperty("token").GetString()!);}
                Check(true,"post-probe current recipes remain qualified without an output movie claim");
                Save("recipes-final.json",Call("GetSnapshotJson"));Finish();
            }
        }
        catch(Exception error)
        {
            failed++;Plugin.Report("PATH-CHECK FAIL runtime-recipe "+error.GetBaseException());
            try{if(api!=null)Save("recipes-failure.json",Call("GetSnapshotJson"));}catch{}
            Finish();
        }
    }
    [HideFromIl2Cpp]
    private void CheckBindingsAndExpectedTargets(JsonElement scope)
    {
        string path=Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RECIPE_BASELINE_TARGETS_JSON")??"";
        if(!Path.IsPathFullyQualified(path)||!File.Exists(path))throw new InvalidOperationException("Set explicit AA_VIDEOEXPORT_RECIPE_BASELINE_TARGETS_JSON to the audited current-94-constructor-metadata.json.");
        using var baseline=JsonDocument.Parse(File.ReadAllText(path));
        var expected=baseline.RootElement.GetProperty("proofs").EnumerateArray().Select(p=>p.GetProperty("type").GetString()!).ToHashSet(StringComparer.Ordinal);
        var observed=new HashSet<string>(StringComparer.Ordinal);
        var runtime=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Il2CppInterop.Runtime");
        var storeDefinition=runtime.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase+InitializerStore`1",true)!;
        int associations=0;
        foreach(var recipe in scope.GetProperty("recipes").EnumerateArray())
        {
            if(recipe.GetProperty("status").GetString()!="QUALIFIED"||!recipe.GetProperty("validatedBeforeMaterialization").GetBoolean()
                ||recipe.GetProperty("evidenceKind").GetString()!="PRE_MATERIALIZATION_FINAL_IL")throw new InvalidOperationException("A current recipe lacks final IL qualification before CreateDelegate.");
            string name=recipe.GetProperty("proof").GetProperty("originalType").GetProperty("type").GetString()!;
            observed.Add(name);Type type=Type.GetType(name,true)!;
            // Read an already-created factory cache, never call its getter or
            // fabricate a DynamicMethod to obtain a holder reference.
            var cached=storeDefinition.MakeGenericType(type).GetField("_initializer",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null) as Delegate
                ?? throw new InvalidOperationException("Observed factory cache has no actual returned delegate.");
            using var lookup=JsonDocument.Parse((string)api!.GetMethod("GetRecipeJson",PublicStatic)!.Invoke(null,new object[]{cached})!);
            if(lookup.RootElement.GetProperty("status").GetString()!="QUALIFIED"||lookup.RootElement.GetProperty("id").GetInt64()!=recipe.GetProperty("id").GetInt64())throw new InvalidOperationException("Actual cached delegate is not associated with the observed record.");
            bool holder=(bool)api.GetMethod("ValidateHolder",PublicStatic)!.Invoke(null,new object[]{token,cached.Method.Module.Assembly})!;
            if(!holder)throw new InvalidOperationException("Observed holder object is not authorized by this retained scope.");
            associations++;
        }
        var missing=expected.Except(observed,StringComparer.Ordinal).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
        Save("recipe-target-coverage.json",JsonSerializer.Serialize(new{historicalRows=baseline.RootElement.GetProperty("checkedTargets").GetInt32(),expectedTypes=expected.Count,currentRecipes=associations,missing},new JsonSerializerOptions{WriteIndented=true}));
        Check(missing.Length==0,"all explicitly audited historical target types have current positive recipes");
        Check(associations>0,"actual cached delegates and holder references match every current recipe");
    }
    [HideFromIl2Cpp]
    private void RequireScope(JsonElement root,bool sealedScope)
    {
        if(!root.GetProperty("valid").GetBoolean())throw new InvalidOperationException(root.GetProperty("reason").GetString());
        if(root.GetProperty("policy").GetString()!="aa101.interop-initializer.intptr.final-il.v2"||root.GetProperty("evidenceKind").GetString()!="PRE_MATERIALIZATION_FINAL_IL"||!root.GetProperty("knownHostBaseline").GetBoolean()
            ||root.GetProperty("preexistingCoverage").GetBoolean()||root.GetProperty("allCodeCoverage").GetBoolean()||root.GetProperty("permitsStrictExport").GetBoolean()
            ||sealedScope&&!root.GetProperty("sealed").GetBoolean())throw new InvalidOperationException("Unexpected recipe scope/baseline/global-coverage claim.");
    }
    [HideFromIl2Cpp] private string Call(string name)=> (string)api!.GetMethod(name,PublicStatic,null,Type.EmptyTypes,null)!.Invoke(null,null)!;
    [HideFromIl2Cpp] private string Call(string name,string value)=> (string)api!.GetMethod(name,PublicStatic,null,new[]{typeof(string)},null)!.Invoke(null,new object[]{value})!;
    [HideFromIl2Cpp] private void CallVoid(string name,string value)=>api!.GetMethod(name,PublicStatic,null,new[]{typeof(string)},null)!.Invoke(null,new object[]{value});
    [HideFromIl2Cpp] private static void Save(string name,string content)=>File.WriteAllText(Path.Combine(Plugin.DiagnosticsRoot,name),content);
    [HideFromIl2Cpp] private void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed++;Plugin.Report("PATH-CHECK PASS runtime-recipe "+name);}
    [HideFromIl2Cpp] private void Finish()
    {
        if(finished)return;finished=true;
        if(token.Length!=0)try{CallVoid("ReleaseScope",token);token="";}catch(Exception error){failed++;Plugin.Report("PATH-CHECK FAIL runtime-recipe release: "+error.GetBaseException().Message);}
        Plugin.Report($"PATH-CHECK RESULT {passed} passed; {failed} failed");
    }
}
