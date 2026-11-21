using System.Reflection;
using System.Reflection.Emit;
using AAVideoExport.Plugin;
using AAVideoExport.Core;
using HarmonyLib;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using MonoMod.Utils.Cil;
using COp=Mono.Cecil.Cil.OpCodes;

internal static class FrameworkGeneratedScopeChecks
{
    internal static readonly string[] Cases={"inventory-positive","inventory-impostors","inventory-late-cache","missing-cache","positive","same-name-proxy","same-name-helper","extra-method","extra-type","extra-field","extra-cctor","changed-ctor","changed-forward","wrong-runtime-reference","helper-signature","helper-extra","proxy-cache","helper-cache","delegate-cache","prefix","postfix","transpiler","finalizer","ilmanipulator","transient-history","worker-commit","same-thread-mutation","reverse-patch-attempt","cancellation","verifier-patch","verifier-name-cache","coexist-recipe-first","coexist-framework-first"};
    private static StrictCapability Inventory(Assembly assembly, FrameworkGeneratedAssemblyScope? framework)
    {
        var method = typeof(StrictCapabilityInventory).GetMethod("CreateCapability", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = new object?[] { assembly, assembly.GetName().Name!, false, null, null, null, framework };
        return (StrictCapability)method.Invoke(null, args.Take(method.GetParameters().Length).ToArray())!;
    }
    private const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    internal static void Run(string name)
    {
        if(name=="missing-cache")
        {
            GC.KeepAlive(typeof(Harmony).Assembly);GC.KeepAlive(typeof(ILGeneratorShim).Assembly);GC.KeepAlive(typeof(DetourHelper).Assembly);
            var cache=typeof(ILGeneratorShim).GetNestedType("ILGeneratorBuilder",BindingFlags.NonPublic)!.GetField("ProxyType",Hidden)!;
            Check(cache.GetValue(null)==null,"Missing-cache fixture was already generated");
            Reject(()=>{using var impossible=FrameworkGeneratedAssemblyScope.Acquire();},"has not produced");
            Check(cache.GetValue(null)==null,"Scope acquisition manufactured its missing proxy");return;
        }
        if(name.StartsWith("coexist-",StringComparison.Ordinal)){Coexist(name=="coexist-recipe-first");return;}
        _=new Harmony("aave.framework-scope.tests.warm");var proxy=ILGeneratorShim.ProxyType;var runtime=DetourHelper.Runtime;
        using var scope=FrameworkGeneratedAssemblyScope.Acquire();
        var platform=typeof(DetourHelper).Assembly.GetType("MonoMod.RuntimeDetour.Platforms.DetourRuntimeNETCore30Platform",true)!;
        var helperField=platform.GetField("_getTypeFromHandleUnsafeMethod",Hidden)!;var delegateField=platform.GetField("GetTypeFromNativeHandle",Hidden)!;
        var helper=(MethodInfo)helperField.GetValue(runtime)!;
        switch(name)
        {
            case "inventory-positive":
                var approved = scope.Assemblies.Select(a => Inventory(a, scope)).ToArray();
                Check(approved.Length == 2 && approved.All(c => c.Level == StrictCapabilityLevel.Observed && c.Fingerprint == scope.IdentitySha256.ToLowerInvariant()), "Real Inventory did not use held framework proof and stable hash");
                var snapshot = StrictCapabilitySnapshot.Create(approved); snapshot.EnsureStrictExportAllowed();
                Check(!snapshot.CanReusePreflight, "Framework proof enabled preflight reuse");
                Check(scope.Assemblies.All(a => Inventory(a, null).Level == StrictCapabilityLevel.Opaque), "Missing framework proof was implicitly acquired or admitted");
                using (var next = FrameworkGeneratedAssemblyScope.Acquire())
                    Check(snapshot.Fingerprint == StrictCapabilitySnapshot.Create(next.Assemblies.Select(a => Inventory(a, next))).Fingerprint, "Scope recreation changed capability identity");
                Console.WriteLine("INVENTORY stable=" + snapshot.Fingerprint + " scope=" + scope.IdentitySha256);
                scope.Dispose();
                Check(scope.Assemblies.All(a => Inventory(a, scope).Level == StrictCapabilityLevel.Rejected), "Disposed scope still authorized inventory");
                return;
            case "inventory-impostors":
                foreach (var fake in new[] { FakeProxy(""), FakeHelper("") })
                {
                    var capability = Inventory(fake, scope);
                    Check(capability.Level == StrictCapabilityLevel.Opaque && !StrictCapabilitySnapshot.Create(new[] { capability }).CanAdmitStrictExport,
                        "Same-name same-template counterfeit received framework marker");
                }
                return;
            case "inventory-late-cache":
                var heldField = typeof(ILGeneratorShim).GetNestedType("ILGeneratorBuilder", BindingFlags.NonPublic)!.GetField("ProxyType", Hidden)!;
                try
                {
                    heldField.SetValue(null, FakeProxy("").GetType(proxy.FullName!));
                    Check(Inventory(proxy.Assembly, scope).Level == StrictCapabilityLevel.Rejected, "Late cache mutation remained admitted");
                }
                finally { heldField.SetValue(null, proxy); }
                Check(Inventory(proxy.Assembly, scope).Level == StrictCapabilityLevel.Rejected, "Restored cache healed invalid retained proof");
                return;
            case "positive":
                scope.Validate(true);Check(scope.AuthorizesAssembly(proxy.Assembly)&&scope.AuthorizesAssembly(helper.Module.Assembly),"Actual cached objects were not authorized");
                Check(!scope.AuthorizesAssembly(typeof(FrameworkGeneratedScopeChecks).Assembly),"Framework proof spilled into arbitrary Target/source assembly");
                Check(scope.Boundary.Contains("Target instances")&&scope.Boundary.Contains("future code"),"Boundary omitted unproved behavior");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{scope.IdentitySha256,ProxyMvid=proxy.Module.ModuleVersionId,HelperMvid=helper.Module.ModuleVersionId}));
                using(scope.EnterCommitScope()){}Reject(()=>scope.Validate(),"consumed");return;
            case "same-name-proxy":
                var fakeProxy=FakeProxy("");Check(fakeProxy.GetName().Name==proxy.Assembly.GetName().Name,"Spoof name differs");
                Check(FrameworkGeneratedAssemblyScope.InspectTemplate(fakeProxy,true,typeof(ILGeneratorShim)).Hash==FrameworkGeneratedAssemblyScope.InspectTemplate(proxy.Assembly,true,typeof(ILGeneratorShim)).Hash,"Exact spoof fixture did not have the same normalized graph");
                Check(!scope.AuthorizesAssembly(fakeProxy),"Same-name same-graph counterfeit inherited actual cache identity");return;
            case "same-name-helper":
                var fakeHelper=FakeHelper("");Check(fakeHelper.GetName().Name==helper.Module.Assembly.GetName().Name,"Helper spoof name differs");
                Check(FrameworkGeneratedAssemblyScope.InspectTemplate(fakeHelper,false,typeof(ILGeneratorShim)).Hash==FrameworkGeneratedAssemblyScope.InspectTemplate(helper.Module.Assembly,false,typeof(ILGeneratorShim)).Hash,"Helper spoof template differs");
                Check(!scope.AuthorizesAssembly(fakeHelper),"Same-name helper counterfeit inherited cached Method/delegate identity");return;
            case "extra-method":case "extra-type":case "extra-field":case "extra-cctor":case "changed-ctor":case "changed-forward":case "wrong-runtime-reference":
                Reject(()=>FrameworkGeneratedAssemblyScope.InspectTemplate(FakeProxy(name),true,typeof(ILGeneratorShim)),"");return;
            case "helper-signature":case "helper-extra":
                Reject(()=>FrameworkGeneratedAssemblyScope.InspectTemplate(FakeHelper(name),false,typeof(ILGeneratorShim)),"");return;
            case "proxy-cache":
                var proxyField=typeof(ILGeneratorShim).GetNestedType("ILGeneratorBuilder",BindingFlags.NonPublic)!.GetField("ProxyType",Hidden)!;
                try{proxyField.SetValue(null,FakeProxy("").GetType(proxy.FullName!));Reject(()=>scope.Validate(),"identity changed");}finally{proxyField.SetValue(null,proxy);}
                Reject(()=>scope.Validate(),"invalid");return;
            case "helper-cache":
                try{helperField.SetValue(runtime,FakeHelper("").GetType("System.Type")!.GetMethod("GetTypeFromHandleUnsafe"));Reject(()=>scope.Validate(),"not bound together");}finally{helperField.SetValue(runtime,helper);}
                Reject(()=>scope.Validate(),"invalid");return;
            case "delegate-cache":
                var original=(Delegate)delegateField.GetValue(runtime)!;
                try{delegateField.SetValue(runtime,Delegate.CreateDelegate(original.GetType(),helper));Reject(()=>scope.Validate(),"identity changed");}finally{delegateField.SetValue(runtime,original);}
                Reject(()=>scope.Validate(),"invalid");return;
            case "prefix":case "postfix":case "transpiler":case "finalizer":case "ilmanipulator":case "transient-history":
                var patcher=new Harmony("aave.framework-scope.tests."+name);var target=Target(proxy);
                try
                {
                    Patch(patcher,target,name=="transient-history"?"prefix":name);
                    if(name=="transient-history")patcher.UnpatchSelf();
                    Reject(()=>scope.Validate(),"");
                }
                finally{patcher.UnpatchSelf();}
                Reject(()=>scope.Validate(),"invalid");Reject(()=>{using var fresh=FrameworkGeneratedAssemblyScope.Acquire();},"history");return;
            case "worker-commit":
                using(var attempted=new ManualResetEventSlim())using(var finished=new ManualResetEventSlim())
                {
                    Task? task=null;
                    using(scope.EnterCommitScope())
                    {
                        task=Task.Run(()=>{attempted.Set();var h=new Harmony("aave.framework-scope.tests.worker");try{Patch(h,Target(proxy),"prefix");}finally{h.UnpatchSelf();finished.Set();}});
                        Check(attempted.Wait(5000)&&!finished.Wait(150),"Protected worker patch crossed publication guard");
                    }
                    Check(task!.Wait(5000)&&finished.IsSet,"Worker patch did not resume after guard release");Reject(()=>scope.Validate(),"consumed");return;
                }
            case "same-thread-mutation":
                using(scope.EnterCommitScope())Reject(()=>DetourHelper.Runtime=runtime,"Protected framework mutation");
                Reject(()=>scope.Validate(),"");return;
            case "reverse-patch-attempt":
                var standin=typeof(ILGeneratorShim).GetNestedType("ILGeneratorBuilder",BindingFlags.NonPublic)!.GetMethod("GenerateProxy",BindingFlags.Public|BindingFlags.Static)!;
                using(scope.EnterCommitScope())Reject(()=>new Harmony("aave.framework-scope.tests.reverse").CreateReversePatcher(typeof(Callbacks).GetMethod(nameof(Callbacks.ReturnType))!,new HarmonyMethod(standin)).Patch(),"Protected framework mutation");
                Reject(()=>scope.Validate(),"");return;
            case "cancellation":
                var gate=typeof(PatchProcessor).GetField("locker",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
                using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var cancel=new CancellationTokenSource())
                {
                    var worker=Task.Run(()=>{lock(gate){entered.Set();release.Wait();}});Check(entered.Wait(5000),"Lock worker did not start");
                    var waiting=Task.Run(()=>{try{using var held=scope.EnterCommitScope(cancel.Token);return false;}catch(OperationCanceledException){return true;}});
                    cancel.CancelAfter(100);try{Check(waiting.Wait(5000)&&waiting.Result,"Commit wait ignored cancellation");}finally{release.Set();Check(worker.Wait(5000),"Lock worker did not exit");}
                    using(scope.EnterCommitScope()){}return;
                }
            case "verifier-patch":
                var owner=new Harmony("aave.framework-scope.tests.verifier");var verifier=typeof(FrameworkGeneratedAssemblyScope).GetMethod("CheckParameters",BindingFlags.Static|BindingFlags.NonPublic)!;
                try{owner.Patch(verifier,prefix:new HarmonyMethod(typeof(Callbacks).GetMethod(nameof(Callbacks.Noop))!));Reject(()=>scope.Validate(),"");}finally{owner.UnpatchSelf();}
                Reject(()=>{using var fresh=FrameworkGeneratedAssemblyScope.Acquire();},"history");return;
            case "verifier-name-cache":
                var cacheOwner=new Harmony("aave.framework-scope.tests.name-cache-verifier");
                var cacheVerifier=typeof(AssemblySimpleNameCache).GetMethod("Get",BindingFlags.Static|BindingFlags.NonPublic)!;
                try{cacheOwner.Patch(cacheVerifier,prefix:new HarmonyMethod(typeof(Callbacks).GetMethod(nameof(Callbacks.Noop))!));}
                finally{cacheOwner.UnpatchSelf();}
                Reject(()=>scope.Validate(),"");
                Reject(()=>{using var fresh=FrameworkGeneratedAssemblyScope.Acquire();},"history");return;
            default:throw new ArgumentException("Unknown framework test "+name);
        }
    }
    private static void Coexist(bool recipeFirst)
    {
        _=new Harmony("aave.framework-scope.tests.coexist-warm");_=ILGeneratorShim.ProxyType;_=DetourHelper.Runtime;
        var bootstrap=typeof(AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1).Assembly;
        Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION","1");
        typeof(AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1).GetMethod("InitializeFromEnvironment",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);
        var policyType=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.InteropRecipePolicy",true)!;
        var observerType=bootstrap.GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeObserver",true)!;
        var policy=policyType.GetMethod("ForTests",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{new[]{typeof(Wrapper)}})!;
        var observer=Activator.CreateInstance(observerType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{policy,2048,16},null)!;
        object Call(string name,params object[] arguments)=>observerType.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(observer,arguments)!;
        FrameworkGeneratedAssemblyScope? framework=null;
        try
        {
            if(!recipeFirst)framework=FrameworkGeneratedAssemblyScope.Acquire();Call("Install");
            Check((bool)observerType.GetProperty("IsInstalled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(observer)!,"Recipe observer failed beside framework monitor");
            framework??=FrameworkGeneratedAssemblyScope.Acquire();
            var store=typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(typeof(Wrapper));
            var value=(Delegate)store.GetProperty("Initializer",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(null)!;
            var recipe=Call("Find",value);Check((string)recipe.GetType().GetProperty("Status")!.GetValue(recipe)! == "QUALIFIED","Actual factory lost qualification beside framework monitor: "+System.Text.Json.JsonSerializer.Serialize(recipe,recipe.GetType()));
            var scope=Call("AcquireScope");Check((bool)scope.GetType().GetProperty("Valid")!.GetValue(scope)!,"Actual retained recipe scope is invalid");
            var token=(string)scope.GetType().GetProperty("Token")!.GetValue(scope)!;_=Call("SealScope",token);
            using(framework.EnterCommitScope())using((IDisposable)Call("EnterCommitScope",token,CancellationToken.None)){}
            var consumed=Call("ValidateScope",token,false,false);Check(!(bool)consumed.GetType().GetProperty("Valid")!.GetValue(consumed)!,"Recipe token was not consumed");
            Reject(()=>framework.Validate(),"consumed");
            Check(Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("com.aavideoexport.runtime-recipes.v2")==true)==14,"Framework monitor changed the exact recipe hook set");
        }
        finally{framework?.Dispose();((IDisposable)observer).Dispose();}
    }
    public sealed class Wrapper:Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase{public Wrapper(IntPtr p):base(p){}}
    private static MethodInfo Target(Type proxy)=>proxy.MakeGenericType(typeof(CecilILGenerator)).GetMethod(nameof(object.ToString),Type.EmptyTypes)!;
    private static void Patch(Harmony h,MethodInfo target,string kind)
    {
        var callback=new HarmonyMethod(typeof(Callbacks).GetMethod(kind switch{"transpiler"=>nameof(Callbacks.Transpile),"finalizer"=>nameof(Callbacks.Finalize),"ilmanipulator"=>nameof(Callbacks.Manipulate),_=>nameof(Callbacks.Noop)})!);
        h.Patch(target,prefix:kind=="prefix"?callback:null,postfix:kind=="postfix"?callback:null,transpiler:kind=="transpiler"?callback:null,finalizer:kind=="finalizer"?callback:null,ilmanipulator:kind=="ilmanipulator"?callback:null);
    }
    public static class Callbacks
    {
        public static void Noop(){}public static Exception? Finalize(Exception? __exception)=>__exception;
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)=>instructions;
        public static void Manipulate(ILContext context){}public static Type ReturnType()=>typeof(object);
    }
    private static Assembly FakeProxy(string mutation)
    {
        using var module=ModuleDefinition.CreateModule("MonoMod.Utils.Cil.ILGeneratorProxy",new ModuleParameters{Kind=ModuleKind.Dll,ReflectionImporterProvider=MMReflectionImporter.Provider});
        var attrType=typeof(ILGeneratorShim).Assembly.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",true)!;
        var attr=new CustomAttribute(module.ImportReference(attrType.GetConstructor(new[]{typeof(string)})));attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"MonoMod.Utils"));module.Assembly.CustomAttributes.Add(attr);
        var type=new TypeDefinition("MonoMod.Utils.Cil","ILGeneratorProxy",Mono.Cecil.TypeAttributes.Public){BaseType=module.ImportReference(typeof(ILGenerator))};module.Types.Add(type);
        var generic=new GenericParameter("TTarget",type);generic.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(ILGeneratorShim))));type.GenericParameters.Add(generic);
        var field=new FieldDefinition("Target",Mono.Cecil.FieldAttributes.Public,generic);type.Fields.Add(field);
        var closed=new GenericInstanceType(type);closed.GenericArguments.Add(generic);var reference=new FieldReference("Target",generic,closed);
        var ctor=new MethodDefinition(".ctor",(Mono.Cecil.MethodAttributes)6278,module.TypeSystem.Void);ctor.Parameters.Add(new ParameterDefinition(generic));type.Methods.Add(ctor);
        var il=ctor.Body.GetILProcessor();if(mutation=="changed-ctor")il.Emit(COp.Nop);il.Emit(COp.Ldarg_0);il.Emit(COp.Ldarg_1);il.Emit(COp.Stfld,reference);il.Emit(COp.Ret);
        foreach(var original in typeof(ILGenerator).GetMethods(BindingFlags.Public|BindingFlags.Instance))
        {
            var target=typeof(ILGeneratorShim).GetMethod(original.Name,original.GetParameters().Select(p=>p.ParameterType).ToArray());if(target==null)continue;
            var method=new MethodDefinition(original.Name,(Mono.Cecil.MethodAttributes)198,module.ImportReference(original.ReturnType)){HasThis=true};
            foreach(var p in original.GetParameters())method.Parameters.Add(new ParameterDefinition(module.ImportReference(p.ParameterType)));type.Methods.Add(method);
            var body=method.Body.GetILProcessor();body.Emit(mutation=="changed-forward"&&original.Name=="ToString"?COp.Nop:COp.Ldarg_0);body.Emit(COp.Ldfld,reference);
            foreach(var p in method.Parameters)body.Emit(COp.Ldarg,p);body.Emit(target.IsVirtual?COp.Callvirt:COp.Call,module.ImportReference(target));body.Emit(COp.Ret);
        }
        if(mutation=="extra-method"){var extra=new MethodDefinition("Extra",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.TypeSystem.Void);extra.Body.GetILProcessor().Emit(COp.Ret);type.Methods.Add(extra);}
        if(mutation=="extra-type")module.Types.Add(new TypeDefinition("Injected","Extra",Mono.Cecil.TypeAttributes.Public){BaseType=module.TypeSystem.Object});
        if(mutation=="extra-field")type.Fields.Add(new FieldDefinition("Extra",Mono.Cecil.FieldAttributes.Public,module.TypeSystem.Int32));
        if(mutation=="extra-cctor"){var extra=new MethodDefinition(".cctor",Mono.Cecil.MethodAttributes.Static|Mono.Cecil.MethodAttributes.SpecialName|Mono.Cecil.MethodAttributes.RTSpecialName,module.TypeSystem.Void);extra.Body.GetILProcessor().Emit(COp.Ret);type.Methods.Add(extra);}
        if(mutation=="wrong-runtime-reference"){var item=module.AssemblyReferences.Single(a=>a.Name=="MonoMod.Utils");item.Hash[^1]^=1;}
        return ReflectionHelper.Load(module);
    }
    private static Assembly FakeHelper(string mutation)
    {
        using var module=ModuleDefinition.CreateModule("MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers",new ModuleParameters{Kind=ModuleKind.Dll});
        var type=new TypeDefinition("System","Type",Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Abstract){BaseType=module.TypeSystem.Object};module.Types.Add(type);
        var method=new MethodDefinition("GetTypeFromHandleUnsafe",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.ImportReference(mutation=="helper-signature"?typeof(object):typeof(Type))){IsInternalCall=true};
        method.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(IntPtr))));type.Methods.Add(method);
        if(mutation=="helper-extra")type.Fields.Add(new FieldDefinition("Extra",Mono.Cecil.FieldAttributes.Public|Mono.Cecil.FieldAttributes.Static,module.TypeSystem.Int32));
        return ReflectionHelper.Load(module);
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Reject(Action action,string contains)
    {try{action();}catch(Exception error){if(!error.GetBaseException().Message.Contains(contains,StringComparison.Ordinal))throw new Exception("Wrong rejection: "+error);return;}throw new Exception("Expected rejection: "+contains);}
}
