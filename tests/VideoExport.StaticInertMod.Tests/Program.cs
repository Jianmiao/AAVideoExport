using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using AAVideoExport.Core;
using AAVideoExport.Plugin;

var api = typeof(Program).Assembly.GetType("AAVideoExport.Plugin.StaticInertModEvidence");
if (api == null) { Console.WriteLine("FAIL production StaticInertModEvidence is missing"); return 1; }
var create = api.GetMethod("TryCreateReceipt", BindingFlags.Static | BindingFlags.NonPublic)!;
string install = args.Length == 0 ? @"F:\AzureArchive_101" : Path.GetFullPath(args[0]);
string PluginPath(string name) => Path.Combine(install,"mods",name,"1.0.0",name+".dll");
object? Receipt(Assembly assembly, out string reason)
{
    object?[] parameters = { assembly, null, null };
    bool success = (bool)create.Invoke(null, parameters)!;
    reason = (string?)parameters[2] ?? "";
    return success ? parameters[1] : null;
}
bool Validate(object receipt, out string reason)
{
    object?[] parameters = { null };
    bool success = (bool)receipt.GetType().GetMethod("TryValidate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receipt,parameters)!;
    reason=(string?)parameters[0] ?? ""; return success;
}
void Check(bool condition,string message){if(!condition)throw new Exception(message);}
int passed=0,failed=0;
void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error);}}
var initiallyAbsentScope=StaticInertScope.Acquire();
var dependent = AssemblyLoadContext.Default.LoadFromAssemblyPath(PluginPath("ExampleDependent"));
Test("an initially absent inspected module invalidates the retained absence when loaded",()=>
{
    bool rejected=false;try{initiallyAbsentScope.ValidateFrame();}catch(ExportException){rejected=true;}
    Check(rejected,"Late exact module silently widened a retained absent scope");
    initiallyAbsentScope.Dispose();
});
Test("dependent needs the actual inspected core assembly",()=>Check(Receipt(dependent,out var reason)==null && reason.Contains("ExampleCore"),"Missing dependency received proof"));
var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(PluginPath("ExampleCore"));
var standalone = AssemblyLoadContext.Default.LoadFromAssemblyPath(PluginPath("ExampleStandalone"));
foreach(var assembly in new[]{core,dependent,standalone}) Test("actual installed payload receives only scoped proof: "+assembly.GetName().Name,()=>
{
    var receipt=Receipt(assembly,out var reason);Check(receipt!=null,reason);Check(Validate(receipt!,out reason),reason);
    Check((string)receipt!.GetType().GetProperty("Scope",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(receipt)! == "StaticInertModEvidence.empty-load.v1","Unexpected scope");
    Check((bool)receipt.GetType().GetProperty("CanReusePreflight",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(receipt)! == false,"Static proof authorized cache");
});
Test("unknown assembly has no proof",()=>Check(Receipt(typeof(Program).Assembly,out _)==null,"Unknown assembly accepted"));
Test("observed empty payload admission requires both exact and retained scope evidence",()=>
{
    foreach(string name in new[]{"ExampleCore","ExampleDependent","ExampleStandalone"})
    {
        var good=new StrictCapability(name,new string('a',64),StrictCapabilityLevel.Observed,new[]{"StaticInertModEvidence.empty-load.v1","StaticInertScope.retained.v1"});
        var snapshot=StrictCapabilitySnapshot.Create(new[]{good});Check(snapshot.CanAdmitStrictExport && !snapshot.CanReusePreflight,"Retained observed scope was not admitted without cache reuse");
        var missing=new StrictCapability(name,new string('a',64),StrictCapabilityLevel.Observed,new[]{"StaticInertModEvidence.empty-load.v1"});
        Check(!StrictCapabilitySnapshot.Create(new[]{missing}).CanAdmitStrictExport,"Static evidence alone authorized export");
    }
    foreach(string name in new[]{"Unknown.Mod","Anonymously Hosted DynamicMethods Assembly"})
        Check(!StrictCapabilitySnapshot.Create(new[]{new StrictCapability(name,new string('a',64),StrictCapabilityLevel.Observed,new[]{"StaticInertModEvidence.empty-load.v1","StaticInertScope.retained.v1"})}).CanAdmitStrictExport,"Scope admitted an unrelated module");
});
Test("real inventory grants only its retained empty modules while uncovered dynamic modules still reject",()=>
{
    var scopeType=typeof(Program).Assembly.GetType("AAVideoExport.Plugin.StaticInertScope");Check(scopeType!=null,"Production retained scope is missing");
    object scope=scopeType!.GetMethod("Acquire",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null)!;
    var capture=typeof(StrictCapabilityInventory).GetMethod("Capture",BindingFlags.Static|BindingFlags.NonPublic)!;
    StrictCapabilitySnapshot Read(object? value)=>(StrictCapabilitySnapshot)capture.Invoke(null,new object?[]{value,null,null,null})!;
    try
    {
        bool Supported(StrictCapability item)=>StaticInertModEvidence.IsSupportedName(item.ModuleId);
        Check(Read(null).Modules.Where(Supported).All(item=>item.Level==StrictCapabilityLevel.Rejected),"Inventory silently created a missing lease");
        var valid=Read(scope);var approved=valid.Modules.Where(Supported).ToArray();
        Check(approved.Length==3 && approved.All(item=>item.Level==StrictCapabilityLevel.Observed) && !valid.CanReusePreflight,"Actual exact loaded empty modules were not admitted by their retained lease");
        var uncovered=valid.Modules.Where(item=>!Supported(item)).ToArray();
        Check(uncovered.All(item=>item.Level==StrictCapabilityLevel.Opaque),"Uncovered dynamic infrastructure was silently promoted");
        if(uncovered.Length>0)Console.WriteLine("INVENTORY uncovered dynamic modules remain blocked: "+string.Join(",",uncovered.Select(item=>item.ModuleId)));
        ((IDisposable)scope).Dispose();Check(Read(scope).Modules.Where(Supported).All(item=>item.Level==StrictCapabilityLevel.Rejected),"Disposed scope still classified as observed proof");
    }
    finally { ((IDisposable)scope).Dispose(); }
});
Test("actual frame validation performs no image reads and commit rechecks them",()=>
{
    using var scope=StaticInertScope.Acquire();long before=StaticInertModEvidence.ImageReadCount;
    for(int frame=0;frame<100;frame++)scope.ValidateFrame();
    Check(StaticInertModEvidence.ImageReadCount==before,"Hot frame path reread an image");
    scope.SealForCommit();scope.ValidateForCommit();
    Check(StaticInertModEvidence.ImageReadCount>before,"Commit did not recheck payload/framework image identity");
});
Test("unsealed and disposed actual scopes cannot authorize worker commit",()=>
{
    using(var scope=StaticInertScope.Acquire())
    {
        bool rejected=false;try{scope.ValidateForCommit();}catch(ExportException){rejected=true;}
        Check(rejected,"Unsealed scope committed");
    }
    var sealedScope=StaticInertScope.Acquire();sealedScope.SealForCommit();sealedScope.Dispose();
    bool stale=false;try{sealedScope.ValidateForCommit();}catch(ExportException){stale=true;}
    Check(stale,"Released scope committed");
});
Test("actual dependency patch latches scope even after a new clean receipt exists",()=>
{
    using var scope=StaticInertScope.Acquire();var method=core.GetType("AzureArchive.ExampleMods.Core.ExampleCorePlugin",true)!.GetMethod("Load")!;
    var harmony=new Harmony("aave.test.static-inert.retained-dependency");
    try
    {
        harmony.Patch(method,prefix:new HarmonyMethod(typeof(ForeignPatch),"prefix"));
        bool rejected=false;try{scope.CaptureProof(dependent);}catch(ExportException){rejected=true;}
        Check(rejected,"Dependent retained scope ignored its patched Core");
    }
    finally{harmony.UnpatchSelf();}
    Check(Receipt(core,out var reason)!=null,"New future receipt could not recover: "+reason);
    Check(!StrictCapabilityInventory.Capture(scope).CanAdmitStrictExport,"A new receipt concealed the active export's old failure");
});
var load=core.GetType("AzureArchive.ExampleMods.Core.ExampleCorePlugin",true)!.GetMethod("Load")!;
foreach(string category in new[]{"prefix","postfix","transpiler","finalizer","ilmanipulator"}) Test("late foreign "+category+" invalidates a retained receipt",()=>
{
    var receipt=Receipt(core,out var reason);Check(receipt!=null,reason);
    var harmony=new Harmony("aave.test.static-inert."+category);
    var patch=new HarmonyMethod(typeof(ForeignPatch),category);
    try
    {
        switch(category){case "prefix":harmony.Patch(load,prefix:patch);break;case "postfix":harmony.Patch(load,postfix:patch);break;case "transpiler":harmony.Patch(load,transpiler:patch);break;case "finalizer":harmony.Patch(load,finalizer:patch);break;default:harmony.Patch(load,ilmanipulator:patch);break;}
        Check(!Validate(receipt!,out reason),"Retained receipt survived foreign patch");
        Check(Receipt(core,out _) == null,"Fresh receipt ignored a live patch");
    }
    finally { harmony.UnpatchSelf(); }
    Check(!Validate(receipt!,out _),"A failed capture receipt revived after patch removal");
    Check(Receipt(core,out reason)!=null,"A new clean scope could not acquire proof: "+reason);
});
string temporary=Path.Combine(Path.GetTempPath(),"aave-static-inert-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
try
{
    foreach(string change in new[]{"trailing-byte","version","mvid","extra-field","extra-type","initializer","load-code"}) Test("changed payload is rejected: "+change,()=>
    {
        string target=Path.Combine(temporary,change,"ExampleCore.dll");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if(change=="trailing-byte"){File.Copy(PluginPath("ExampleCore"),target);using var stream=new FileStream(target,FileMode.Append);stream.WriteByte(1);}
        else
        {
            using var metadata=AssemblyDefinition.ReadAssembly(PluginPath("ExampleCore"));
            var plugin=metadata.MainModule.Types.Single(t=>t.Name=="ExampleCorePlugin");
            switch(change)
            {
                case "version":metadata.Name.Version=new Version(1,0,1,0);break;
                case "mvid":metadata.MainModule.Mvid=Guid.NewGuid();break;
                case "extra-field":plugin.Fields.Add(new FieldDefinition("NewState",Mono.Cecil.FieldAttributes.Public|Mono.Cecil.FieldAttributes.Static,metadata.MainModule.TypeSystem.Int32));break;
                case "extra-type":metadata.MainModule.Types.Add(new TypeDefinition("Probe","ExtraExecutable",Mono.Cecil.TypeAttributes.Public,metadata.MainModule.TypeSystem.Object));break;
                case "initializer":var cctor=new MethodDefinition(".cctor",Mono.Cecil.MethodAttributes.Static|Mono.Cecil.MethodAttributes.Private|Mono.Cecil.MethodAttributes.SpecialName|Mono.Cecil.MethodAttributes.RTSpecialName,metadata.MainModule.TypeSystem.Void);cctor.Body.Instructions.Add(Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));plugin.Methods.Add(cctor);break;
                case "load-code":plugin.Methods.Single(m=>m.Name=="Load").Body.Instructions.Insert(0,Instruction.Create(Mono.Cecil.Cil.OpCodes.Nop));break;
            }
            metadata.Write(target);
        }
        var context=new AssemblyLoadContext("inert-mutant-"+change,true);
        context.Resolving+=(_,name)=>AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name.Name);
        try{var altered=context.LoadFromAssemblyPath(target);Check(Receipt(altered,out _)==null,"Altered payload received proof");}
        finally{context.Unload();}
    });
}
finally
{
    for(int attempt=0;attempt<8;attempt++)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        try { Directory.Delete(temporary,true); break; }
        catch(IOException) when(attempt<7) { Thread.Sleep(50); }
        catch(UnauthorizedAccessException) when(attempt<7) { Thread.Sleep(50); }
    }
}
RecipeInventoryChecks.Run(Test);
Console.WriteLine($"RESULT {passed} passed; {failed} failed; actual Mod.Load/constructors and AA were never executed");
return failed==0?0:1;

public static class ForeignPatch
{
    public static void prefix() { }
    public static void postfix() { }
    public static IEnumerable<CodeInstruction> transpiler(IEnumerable<CodeInstruction> instructions)=>instructions;
    public static Exception? finalizer(Exception? __exception)=>__exception;
    public static void ilmanipulator(ILContext context) { }
}
