using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AAVideoExport.Core;
using AAVideoExport.Plugin;

internal static class RecipeInventoryChecks
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.NonPublic;
    internal static void Run(Action<string,Action> test)
    {
        var actual=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Anonymously Hosted DynamicMethods Assembly"),AssemblyBuilderAccess.Run);
        var impostor=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Anonymously Hosted DynamicMethods Assembly"),AssemblyBuilderAccess.Run);
        var systemLooking=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MonoMod.Utils.Cil.FakeGenerated"),AssemblyBuilderAccess.Run);
        test("real inventory rejects same-name holder impostors and missing retained recipe scope",()=>
        {
            using var scope=RetainedRecipeScope.Acquire(new Backend(actual));
            Check(Capability(actual,scope).Level==StrictCapabilityLevel.Observed,"actual holder object was not admitted by the held proof");
            Check(Capability(impostor,scope).Level==StrictCapabilityLevel.Opaque,"same-name holder impostor was accepted");
            Check(Capability(actual,null).Level==StrictCapabilityLevel.Opaque,"missing recipe scope was silently acquired");
        });
        test("real inventory dynamic fingerprint uses stable policy instead of MVID or token",()=>
        {
            string first;
            using(var scope=RetainedRecipeScope.Acquire(new Backend(actual))){var cap=Capability(actual,scope);first=StrictCapabilitySnapshot.Create(new[]{cap}).Fingerprint;Check(cap.Fingerprint==new string('a',64),"anonymous MVID replaced stable policy fingerprint");}
            using var next=RetainedRecipeScope.Acquire(new Backend(impostor));
            Check(StrictCapabilitySnapshot.Create(new[]{Capability(impostor,next)}).Fingerprint==first,"new actual holder MVID/token drifted model identity");
        });
        test("dynamic infrastructure-looking names never bypass inventory",()=>
        {
            bool included=(bool)typeof(StrictCapabilityInventory).GetMethod("IsModAssembly",Flags)!.Invoke(null,new object[]{systemLooking,systemLooking.GetName().Name!})!;
            using var scope=RetainedRecipeScope.Acquire(new Backend(actual));
            Check(included&&Capability(systemLooking,scope).Level==StrictCapabilityLevel.Opaque,"dynamic name prefix bypassed admission");
        });
    }
    private static StrictCapability Capability(Assembly assembly,RetainedRecipeScope? scope)=>(StrictCapability)typeof(StrictCapabilityInventory).GetMethod("CreateCapability",Flags)!
        .Invoke(null,new object?[]{assembly,assembly.GetName().Name!,null,null,scope,null})!;
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    private sealed class Backend(Assembly holder):IRecipeScopeBackend
    {
        private readonly string token=Guid.NewGuid().ToString("N");private bool released;
        private string Json()=>JsonSerializer.Serialize(new{schema="aave.runtime-recipes.v2",token,valid=!released,policy=RuntimeAttributionClient.ExpectedRecipePolicy,evidenceKind=RuntimeAttributionClient.ExpectedRecipeEvidenceKind,policySha256=new string('a',64),recipesSha256="",knownHostBaseline=true,@sealed=false,preexistingCoverage=false,allCodeCoverage=false,permitsStrictExport=false});
        public bool Acquire(out string value,out string evidence){value=token;evidence=Json();return true;}
        public bool Validate(string value,bool commit,out string evidence){evidence=Json();return value==token&&!released&&!commit;}
        public bool Holder(string value,Assembly candidate)=>value==token&&!released&&ReferenceEquals(candidate,holder);
        public bool Seal(string value,out string evidence){evidence="";return false;}
        public bool Enter(string value,CancellationToken cancellation,out IDisposable? lease){lease=null;return false;}
        public bool Release(string value){released=true;return true;}
    }
}
