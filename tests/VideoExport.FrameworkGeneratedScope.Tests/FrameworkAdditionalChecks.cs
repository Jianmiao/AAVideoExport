using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using AAVideoExport.Plugin;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using MonoMod.Utils.Cil;
using AAVideoExport.Core;

internal static class FrameworkAdditionalChecks
{
    internal static void Run(string name)
    {
        AssemblyLoadContext.Default.Resolving+=(_,n)=>
        {
            string path=Path.Combine(@"F:\AzureArchive_101\BepInEx\interop",n.Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        _=new Harmony("aave.framework-additional.tests.warm");_=ILGeneratorShim.ProxyType;_=DetourHelper.Runtime;
        if(name.StartsWith("coexist-",StringComparison.Ordinal)){CoexistProducer(name[8..]);return;}
        var method=typeof(FrameworkAdditionalChecks).GetMethod(nameof(Target))!;
        if(name=="late-harmony"||name=="late-trampoline"||name=="late-size")
        {
            using var held=FrameworkGeneratedAssemblyScope.Acquire();string identity=held.IdentitySha256;
            var late=Produce(name[5..],method);held.Validate(true);
            Check(held.AuthorizesAssembly(late.Assembly)&&held.IdentitySha256==identity,"Legitimate late producer changed or escaped the stable held declaration proof");
            Inventory(late.Assembly,held).EnsureStrictExportAllowed();return;
        }
        if(name=="producer-worker"||name=="producer-same-thread")
        {
            using var held=FrameworkGeneratedAssemblyScope.Acquire();
            if(name=="producer-same-thread")
            {using(held.EnterCommitScope())Reject(()=>Produce("harmony",method));Reject(()=>held.Validate());return;}
            using var entered=new ManualResetEventSlim();using var completed=new ManualResetEventSlim();Task? worker=null;
            using(held.EnterCommitScope())
            {
                worker=Task.Run(()=>{entered.Set();_=Produce("harmony",method);completed.Set();});
                Check(entered.Wait(5000)&&!completed.Wait(150),"Other-thread declaration producer crossed publication guard");
            }
            Check(worker!.Wait(5000)&&completed.IsSet,"Declaration producer did not resume after publication");return;
        }
        string family=name.StartsWith("trampoline",StringComparison.Ordinal)||name=="inventory-uncached-trampoline"?"trampoline":name.StartsWith("size",StringComparison.Ordinal)?"size":"harmony";
        var actual=Produce(family,method);
        Console.WriteLine("ACTUAL "+actual.Assembly.FullName+" dynamic="+actual.Assembly.IsDynamic+" type="+actual.FullName);
        using var scope=FrameworkGeneratedAssemblyScope.Acquire();
        if(!scope.AuthorizesAssembly(actual.Assembly))throw new Exception("Actual cached "+name+" producer remains outside the held framework proof");
        scope.Validate(true);
        if(name=="inventory-unknown-growth")
        {
            var before=StrictCapabilityInventory.Capture(frameworkScope:scope);before.EnsureStrictExportAllowed();
            _=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(actual.Assembly.GetName().Name!),AssemblyBuilderAccess.Run);
            var after=StrictCapabilityInventory.Capture(frameworkScope:scope);
            Check(after.Fingerprint!=before.Fingerprint&&!after.CanAdmitStrictExport&&after.Modules.Any(m=>m.ModuleId==actual.Assembly.GetName().Name&&m.Level==StrictCapabilityLevel.Opaque),
                "Actual Inventory normalized away a late same-name counterfeit assembly");return;
        }
        if(name=="inventory-uncached-trampoline")
        {
            var before=StrictCapabilityInventory.Capture(frameworkScope:scope);before.EnsureStrictExportAllowed();
            var support=typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).Assembly.GetType("Il2CppInterop.Runtime.DelegateSupport",true)!;
            var module=(ModuleBuilder)support.GetField("ModuleBuilder",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            _=module.DefineType("UncachedInventoryCode",TypeAttributes.Public).CreateType();
            var after=StrictCapabilityInventory.Capture(frameworkScope:scope);
            Check(after.Fingerprint!=before.Fingerprint&&!after.CanAdmitStrictExport&&after.Modules.Any(m=>m.ModuleId==actual.Assembly.GetName().Name&&m.Level==StrictCapabilityLevel.Rejected),
                "Actual Inventory normalized away an invalidated declaration proof");return;
        }
        if(name=="inventory-stable-growth")
        {
            _=StrictCapabilityInventory.Capture(frameworkScope:scope);
            var before=StrictCapabilityInventory.Capture(frameworkScope:scope);string identity=scope.IdentitySha256;
            _=Produce("harmony",typeof(FrameworkAdditionalChecks).GetMethod(nameof(TargetLong))!);
            var after=StrictCapabilityInventory.Capture(frameworkScope:scope);
            var oldDeclarations=before.Modules.Where(m=>m.ModuleId.StartsWith("HarmonyDTFAssembly",StringComparison.Ordinal)).ToArray();
            var newDeclarations=after.Modules.Where(m=>m.ModuleId.StartsWith("HarmonyDTFAssembly",StringComparison.Ordinal)).ToArray();
            Check(oldDeclarations.Length==1&&newDeclarations.Length==2&&newDeclarations.All(m=>m.Level==StrictCapabilityLevel.Observed),"Actual Inventory fixture failed to include both qualified cached declarations");
            Check(scope.IdentitySha256==identity,"Scope policy identity changed on legitimate growth");
            Check(before.Fingerprint==after.Fingerprint,"Actual Inventory fingerprint changed on legitimate cached delegate growth: "+before.Fingerprint+" -> "+after.Fingerprint+"; DTF 1 -> 2");
            return;
        }
        if(name=="harmony"||name=="size"||name=="trampoline")
        {
            var snapshot=Inventory(actual.Assembly,scope);snapshot.EnsureStrictExportAllowed();Check(!snapshot.CanReusePreflight,"Declaration proof enabled reuse");
            Check(!Inventory(actual.Assembly,null).CanAdmitStrictExport,"Missing actual retained proof admitted a generated family");return;
        }
        if(name=="harmony-impostor"||name=="trampoline-impostor")
        {
            var fake=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(actual.Assembly.GetName().Name!),AssemblyBuilderAccess.Run);
            Check(!scope.AuthorizesAssembly(fake)&&!Inventory(fake,scope).CanAdmitStrictExport,"Same-name counterfeit inherited actual cached assembly proof");return;
        }
        if(name=="harmony-cache")
        {
            var factory=typeof(DelegateTypeFactory);var cache=(System.Collections.IDictionary)factory.GetField("TypeCache",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            var entries=(System.Collections.IList)cache[method]!;var entry=entries[0]!;var field=entry.GetType().GetField("delegateType",BindingFlags.Instance|BindingFlags.Public)!;
            try{field.SetValue(entry,typeof(Func<int,int>));Reject(()=>scope.Validate());}finally{field.SetValue(entry,actual);}
            Reject(()=>scope.Validate());return;
        }
        if(name=="size-cache")
        {
            var field=typeof(MonoMod.Utils.Extensions).GetField("_GetManagedSizeHelper",BindingFlags.Static|BindingFlags.NonPublic)!;var before=field.GetValue(null);
            try{field.SetValue(null,method);Reject(()=>scope.Validate());}finally{field.SetValue(null,before);}
            Reject(()=>scope.Validate());return;
        }
        if(name=="trampoline-extra-type")
        {
            var support=typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).Assembly.GetType("Il2CppInterop.Runtime.DelegateSupport",true)!;
            var module=(ModuleBuilder)support.GetField("ModuleBuilder",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            _=module.DefineType("UncachedUserCode",TypeAttributes.Public).CreateType();Reject(()=>scope.Validate());return;
        }
        if(name=="harmony-producer-patch")
        {
            var target=typeof(DelegateTypeFactory).GetMethod("CreateDelegateType",new[]{typeof(MethodInfo),typeof(CallingConvention?)})!;
            var patcher=new Harmony("aave.framework-additional.tests.patch");
            try{patcher.Patch(target,prefix:new HarmonyMethod(typeof(FrameworkAdditionalChecks).GetMethod(nameof(Prefix))!));}
            finally{patcher.UnpatchSelf();}
            Reject(()=>scope.Validate());Reject(()=>{using var fresh=FrameworkGeneratedAssemblyScope.Acquire();});return;
        }
        throw new ArgumentException(name);
    }
    private static StrictCapabilitySnapshot Inventory(Assembly assembly,FrameworkGeneratedAssemblyScope? scope)
    {
        var method=typeof(StrictCapabilityInventory).GetMethod("CreateCapability",BindingFlags.Static|BindingFlags.NonPublic)!;
        var capability=(StrictCapability)method.Invoke(null,new object?[]{assembly,assembly.GetName().Name!,false,null,null,null,scope})!;
        return StrictCapabilitySnapshot.Create(new[]{capability});
    }
    private static void CoexistProducer(string family)
    {
        var bootstrap=typeof(AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1).Assembly;
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION","1");
        typeof(AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1).GetMethod("InitializeFromEnvironment",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);
        var policyType=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.InteropRecipePolicy",true)!;
        var observerType=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeObserver",true)!;
        var policy=policyType.GetMethod("ForTests",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{new[]{typeof(FrameworkGeneratedScopeChecks.Wrapper)}})!;
        var observer=Activator.CreateInstance(observerType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{policy,2048,16},null)!;
        object Call(string method,params object[] args)=>observerType.GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(observer,args)!;
        using var framework=FrameworkGeneratedAssemblyScope.Acquire();
        try
        {
            Call("Install");Check((bool)observerType.GetProperty("IsInstalled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(observer)!,"Recipe observer failed beside producer monitors");
            var store=typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(typeof(FrameworkGeneratedScopeChecks.Wrapper));
            _=store.GetProperty("Initializer",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null);
            var acquired=Call("AcquireScope");var token=(string)acquired.GetType().GetProperty("Token")!.GetValue(acquired)!;
            var first=Produce(family,typeof(FrameworkAdditionalChecks).GetMethod(nameof(Target))!);
            Check(framework.AuthorizesAssembly(first.Assembly),"Legitimate producer growth was not admitted beside retained recipe scope");
            var sealedScope=Call("SealScope",token);Check((bool)sealedScope.GetType().GetProperty("Valid")!.GetValue(sealedScope)!,"Framework producer growth invalidated exact recipe evidence");
            foreach(var flag in new[]{"PreexistingCoverage","AllCodeCoverage","PermitsStrictExport"})Check(!(bool)sealedScope.GetType().GetProperty(flag)!.GetValue(sealedScope)!,"Framework extension broadened recipe coverage");
            using var started=new ManualResetEventSlim();using var completed=new ManualResetEventSlim();Task? worker=null;
            using(framework.EnterCommitScope())using((IDisposable)Call("EnterCommitScope",token,CancellationToken.None))
            {
                worker=Task.Run(()=>{started.Set();_=Produce(family,typeof(FrameworkAdditionalChecks).GetMethod(nameof(TargetLong))!);completed.Set();});
                Check(started.Wait(5000)&&!completed.Wait(150),"Producer crossed combined framework/recipe publication guard");
            }
            Check(worker!.Wait(5000)&&completed.IsSet,"Combined guard release deadlocked the waiting producer");
            Check(Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("com.aavideoexport.runtime-recipes.v2")==true)==14,"Producer observation changed the exact fourteen recipe hooks");
        }
        finally{((IDisposable)observer).Dispose();}
    }
    private static Type Produce(string family,MethodInfo method)
    {
        if(family=="harmony")return DelegateTypeFactory.instance.CreateDelegateType(method,CallingConvention.Cdecl);
        if(family=="size")
        {
            Check(MonoMod.Utils.Extensions.GetManagedSize(typeof(long))==8,"Actual managed-size producer returned the wrong size");
            return ((MethodInfo)typeof(MonoMod.Utils.Extensions).GetField("_GetManagedSizeHelper",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!).DeclaringType!;
        }
        var support=typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).Assembly.GetType("Il2CppInterop.Runtime.DelegateSupport",true)!;
        var signature=support.GetNestedType("MethodSignature",BindingFlags.NonPublic)!;
        var value=signature.GetConstructor(new[]{typeof(MethodInfo),typeof(bool)})!.Invoke(new object[]{method,false});
        return (Type)support.GetMethod("GetOrCreateDelegateType",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new[]{value,(object)method})!;
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected fail-closed rejection");}
    public static void Prefix(){}
    public static int Target(int value)=>value;
    public static long TargetLong(long value)=>value;
}
