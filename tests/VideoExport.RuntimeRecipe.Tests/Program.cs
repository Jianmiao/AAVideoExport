using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AAVideoExport.RuntimeBootstrap;
using Il2CppInterop.Runtime.InteropTypes;
using HarmonyLib;

var api=typeof(RuntimeAttributionV1).Assembly.GetType("AAVideoExport.RuntimeBootstrap.RuntimeRecipeV2");
if(api==null){Console.WriteLine("FAIL independent v2 emission recipe API is missing");return 1;}
if(Environment.Version!=new Version(6,0,7)){Console.WriteLine("FAIL tests require the actual AA .NET 6.0.7 runtime");return 1;}
if(args.Contains("--compact-status"))return CompactRecipeStatusChecks.Run();
if(args.Contains("--compact-status-benchmark"))return CompactRecipeStatusChecks.Benchmark();
if(args.Contains("--flatbuffers-metadata"))return FlatbuffersManifestChecks.Run();
if(args.Contains("--webrequest-metadata"))return WebRequestManifestChecks.Run(args.Length > 1 ? args[1] : "artifacts/runtime-recipes-webrequest-20261030/metadata-audit.json");
if(args.Contains("--cctor-patch-history"))return InitializerMutationChecks.Run();
if(args.Contains("--architecture-contract"))
{
    var evidence=api.GetProperty("EvidenceKind",BindingFlags.Public|BindingFlags.Static)?.GetValue(null) as string;
    if(evidence!="PRE_MATERIALIZATION_FINAL_IL")
    {Console.WriteLine("FAIL bootstrap does not expose the replacement final-IL strategy");return 1;}
    using var candidate=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(typeof(WrapperA)));candidate.Install();
    if(!candidate.IsInstalled){Console.WriteLine("FAIL "+candidate.Failure);return 1;}
    var own=Harmony.GetAllPatchedMethods().Where(m=>(Harmony.GetPatchInfo(m)?.Owners.Contains("com.aavideoexport.runtime-recipes.v2"))==true).ToArray();
    if(own.Any(m=>m.DeclaringType==typeof(ILGenerator)||m.DeclaringType?.FullName=="System.Reflection.Emit.DynamicILGenerator")||own.Length!=14)
    {Console.WriteLine("FAIL recipe strategy still detours emitters or lacks its exact 14 boundary hooks");return 1;}
    Console.WriteLine("PASS final-IL evidence kind and 14 boundary hooks without emitter detours");return 0;
}
if(args.Contains("--inspect-final-il-layout"))
{
    const BindingFlags fields=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
    var method=new DynamicMethod("layout",typeof(WrapperA),new[]{typeof(IntPtr)});var generator=method.GetILGenerator();
    generator.Emit(OpCodes.Ldarg_0);generator.Emit(OpCodes.Newobj,typeof(WrapperA).GetConstructor(new[]{typeof(IntPtr)})!);generator.Emit(OpCodes.Ret);
    void Dump(object instance)
    {
        for(Type? type=instance.GetType();type!=null;type=type.BaseType)
            foreach(var field in type.GetFields(fields|BindingFlags.DeclaredOnly))
            {var value=field.GetValue(instance);Console.WriteLine(type.FullName+"::"+field.Name+" type="+field.FieldType.FullName+" value="+(value is Array a?"array length="+a.Length:value?.ToString()??"null"));}
    }
    Console.WriteLine("BEFORE");Dump(method);Dump(generator);
    var result=method.CreateDelegate(typeof(Func<IntPtr,WrapperA>));
    Console.WriteLine("AFTER");Dump(method);Dump(result.Method);
    foreach(var field in typeof(DynamicMethod).GetFields(fields).Where(f=>f.Name.Contains("resolver",StringComparison.OrdinalIgnoreCase)))
        if(field.GetValue(method) is {} resolver)Dump(resolver);
    return 0;
}
if(args.Contains("--audit-current-targets"))
{
    string install=args.Length>1?args[1]:@"F:\AzureArchive_101";
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>
    {
        foreach(string folder in new[]{"BepInEx/interop","BepInEx/core"})
        {string path=Path.Combine(install,folder,(name.Name??"")+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}
        return null;
    };
    var policy=InteropRecipePolicy.ForTests();
    using var source=JsonDocument.Parse(File.ReadAllText("artifacts/acceptance-20261029-07-bootstrap-fps-handoff/bootstrap-final.json"));
    int checkedTargets=0;var proofs=new List<object>();
    foreach(var record in source.RootElement.GetProperty("records").EnumerateArray().Where(r=>r.GetProperty("holder").GetString()!.StartsWith("Anonymously Hosted")&&r.GetProperty("creatorAssembly").GetString()=="Il2CppInterop.Runtime"))
    {
        string label=record.GetProperty("name").GetString()!;string qualified=label[12..^1];
        var type=Type.GetType(qualified,true)!;var ctor=type.GetConstructor(new[]{typeof(IntPtr)})!;
        var proof=policy.AuditConstructorMetadata(ctor);checkedTargets++;
        proofs.Add(new{type=qualified,constructors=proof.Constructors,initializers=proof.Initializers});
    }
    File.WriteAllText("artifacts/runtime-recipes-20261030/current-94-constructor-metadata.json",JsonSerializer.Serialize(new{checkedTargets,scope="metadata only; no current native emission claim",proofs},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("RESULT "+checkedTargets+" current target constructor chains verified; no factories/initializers/constructors invoked");return checkedTargets==94?0:1;
}
int passed=0,failed=0;
void Check(bool value,string message){if(!value)throw new Exception(message);}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error);}}
Delegate Factory(Type type,bool fresh=false)
{
    var store=typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(type);
    if(fresh)store.GetField("_initializer",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);
    return (Delegate)store.GetProperty("Initializer",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
}
Type[] wrappers={typeof(WrapperA),typeof(WrapperB),typeof(WrapperC),typeof(WrapperMissing),typeof(BadWrapper),typeof(DerivedC),typeof(GenericWrapper<int>)};
if(args.Contains("--fingerprint-only"))
{
    using var fingerprintObserver=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));fingerprintObserver.Install();
    if(!fingerprintObserver.IsInstalled)throw new Exception(fingerprintObserver.Failure);
    if(args.Contains("--reverse-order")){_=Factory(typeof(GenericWrapper<int>));_=Factory(typeof(WrapperA));}
    else{_=Factory(typeof(WrapperA));_=Factory(typeof(GenericWrapper<int>));}
    var before=fingerprintObserver.AcquireScope();var sealedScope=fingerprintObserver.SealScope(before.Token);
    Console.WriteLine(JsonSerializer.Serialize(new{sealedScope.Token,sealedScope.Valid,sealedScope.PolicySha256,sealedScope.RecipesSha256,scope="isolated exact factory recipe stability only"}));
    return sealedScope.Valid?0:1;
}
FinalIlSnapshotChecks.Run(Test);
if(args.Contains("--final-il-only")){Console.WriteLine($"RESULT {passed} passed; {failed} failed; final IL parser only");return failed==0?0:1;}
var beforeObservation=Factory(typeof(WrapperMissing));
if(args.Contains("--with-v1")){Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION","1");RuntimeAttributionV1.InitializeFromEnvironment();}
using var observer=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));
observer.Install();
Test("all narrow materialization boundary hooks install",()=>Check(observer.IsInstalled,observer.Failure));
if(!observer.IsInstalled)return 1;
Test("replacement strategy uses exactly fourteen boundary hooks and zero emitter detours",()=>
{
    var own=Harmony.GetAllPatchedMethods().Where(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("com.aavideoexport.runtime-recipes.v2")==true).ToArray();
    Check(own.Length==14&&!own.Any(m=>m.DeclaringType==typeof(ILGenerator)||m.DeclaringType?.FullName=="System.Reflection.Emit.DynamicILGenerator"),"Unstable global emitter detours remain");
    Check(observer.Snapshot().EvidenceKind=="PRE_MATERIALIZATION_FINAL_IL"&&observer.Snapshot().Policy=="isolated-test.interop-initializer.intptr.final-il.v2","Old policy/evidence kind was reused");
});
Delegate? first=null;
Test("actual inspected Interop factory yields observed typed wrapper recipe",()=>
{
    first=Factory(typeof(WrapperA));var recipe=observer.Find(first);
    Check(recipe.Status=="QUALIFIED",JsonSerializer.Serialize(recipe));
    Check(recipe.ValidatedBeforeMaterialization,"Recipe was not checked before CreateDelegate");
    Check(recipe.Instructions.Select(i=>i.Opcode).SequenceEqual(new[]{"ldarg.0","newobj","ret"}),"Missing actual typed emissions");
    Check(recipe.Proof!.Constructors.Count==3,"Recursive wrapper/root/GC handle identities missing");
});
Test("retained recipe scope is finite and never grants global export authorization",()=>
{
    var scope=observer.AcquireScope();Check(scope.Valid,JsonSerializer.Serialize(scope));
    Check(observer.SealScope(scope.Token).Sealed,"Scope did not seal");
    Check(!scope.AllCodeCoverage&&!scope.PreexistingCoverage&&!scope.PermitsStrictExport,"Recipe broadened coverage/authorization");
    Check(observer.ValidateScope(scope.Token,true,true).Valid,"Same sealed scope did not revalidate");
    observer.ReleaseScope(scope.Token);Check(!observer.ValidateScope(scope.Token,false).Valid,"Released scope remained valid");
});
Test("same-name dynamic assembly cannot impersonate the observed holder object",()=>
{
    var scope=observer.AcquireScope();Check(scope.Valid,scope.Reason);
    Assembly actual=first!.Method.Module.Assembly;
    var impostor=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(actual.GetName().Name!),AssemblyBuilderAccess.Run);
    Check(impostor.GetName().Name==actual.GetName().Name&&!ReferenceEquals(impostor,actual),"Impostor fixture did not share the name");
    Check(observer.ValidateHolder(scope.Token,actual)&&!observer.ValidateHolder(scope.Token,impostor),"Holder admission used a name rather than observed reference identity");
    observer.ReleaseScope(scope.Token);Check(!observer.ValidateHolder(scope.Token,actual),"Released scope still admitted its holder");
});
Delegate? genericValue=null;
Test("actual generic constructor token resolves to the observed closed wrapper",()=>
{
    genericValue=Factory(typeof(GenericWrapper<int>));var recipe=observer.Find(genericValue);
    Check(recipe.Status=="QUALIFIED",JsonSerializer.Serialize(recipe));
    Check(recipe.Proof!.ReturnType.Type.Contains("GenericWrapper`1[[System.Int32"),"Closed generic identity was lost");
});
Test("preexisting cached factory delegate is not reconstructed as observed emission",()=>Check(observer.Find(beforeObservation).Status=="UNKNOWN","Missing constructor/emit observations received proof"));
Test("new anonymous generation invalidates an existing retained set",()=>
{
    var scope=observer.AcquireScope();Check(scope.Valid,scope.Reason);observer.SealScope(scope.Token);var added=Factory(typeof(WrapperB));
    Check(observer.Find(added).Status=="QUALIFIED","New exact recipe did not qualify independently");
    Check(!observer.ValidateScope(scope.Token,false).Valid,"Old scope silently expanded its observed set");observer.ReleaseScope(scope.Token);
});
Test("collecting scope accepts only completed exact-policy additions before sealing",()=>
{
    var scope=observer.AcquireScope();Check(scope.Valid,scope.Reason);var value=Factory(typeof(WrapperC));
    Check(observer.Find(value).Status=="QUALIFIED","Exact additional recipe was not observed");
    var expanded=observer.ValidateScope(scope.Token,false);Check(expanded.Valid,"Collecting scope rejected a completed checked factory recipe");
    Check(expanded.PolicySha256==scope.PolicySha256&&expanded.RecipesSha256=="","Collecting additions changed the stable policy identity");
    var sealedScope=observer.SealScope(scope.Token);Check(sealedScope.Sealed&&sealedScope.RecipesSha256.Length==64&&observer.ValidateScope(scope.Token,true,true).Valid,"Collected recipes could not seal and revalidate");observer.ReleaseScope(scope.Token);
});
Test("commit cannot consume an unsealed collecting scope",()=>
{
    var scope=observer.AcquireScope();Check(scope.Valid,scope.Reason);Check(!observer.ValidateScope(scope.Token,true,true).Valid,"Unsealed scope authorized commit");observer.ReleaseScope(scope.Token);
});
Test("mutable redirect invalidates and cannot revive the original recipe",()=>
{
    var store=typeof(Il2CppObjectBase).Assembly.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore`1",true)!.MakeGenericType(typeof(WrapperA));
    var redirect=store.GetField("CreatedTypeRedirect",BindingFlags.Public|BindingFlags.Static)!;
    try{redirect.SetValue(null,typeof(WrapperA));Check(observer.Find(first!).Status!="QUALIFIED","Changed redirect was ignored");}
    finally{redirect.SetValue(null,null);}
    Check(observer.Find(first!).Status!="QUALIFIED","Invalidated receipt healed after restoring redirect");
});
Test("late constructor prefix invalidates its exact recursively checked recipe",()=>
{
    var value=Factory(typeof(WrapperB));Check(observer.Find(value).Status=="QUALIFIED","Fixture had no initial proof");
    var patcher=new Harmony("aave.recipe.test.foreign-ctor");var ctor=typeof(WrapperB).GetConstructor(new[]{typeof(IntPtr)})!;
    try{patcher.Patch(ctor,prefix:new HarmonyMethod(typeof(Foreign),nameof(Foreign.Prefix)));Check(observer.Find(value).Status!="QUALIFIED","Late constructor patch retained proof");}
    finally{patcher.UnpatchSelf();}
    Check(observer.Find(value).Status!="QUALIFIED","Invalidated constructor recipe revived");
});
Test("factory cache replacement invalidates the actual returned delegate association",()=>
{
    Check(observer.Find(genericValue!).Status=="QUALIFIED","Generic fixture had no initial recipe");
    var store=typeof(Il2CppObjectBase).GetNestedType("InitializerStore`1",BindingFlags.NonPublic)!.MakeGenericType(typeof(GenericWrapper<int>));
    var cache=store.GetField("_initializer",BindingFlags.Static|BindingFlags.NonPublic)!;var saved=cache.GetValue(null);
    try{cache.SetValue(null,new Func<IntPtr,GenericWrapper<int>>(_=>null!));Check(observer.Find(genericValue!).Status!="QUALIFIED","Replaced cached delegate retained proof");}
    finally{cache.SetValue(null,saved);}
});
Test("actual factory with additional constructor behavior is refused without executing it",()=>
{
    var value=Factory(typeof(BadWrapper));var recipe=observer.Find(value);
    Check(recipe.Status!="QUALIFIED"&&recipe.Reason.Contains("additional",StringComparison.OrdinalIgnoreCase)==false&&recipe.Reason.Contains("behavior beyond"),JsonSerializer.Serialize(recipe));
    Check(BadWrapper.SideEffects==0,"The verifier executed an unknown constructor");
});
Test("different redirected target is bound explicitly and refused",()=>
{
    var store=typeof(Il2CppObjectBase).Assembly.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore`1",true)!.MakeGenericType(typeof(WrapperC));
    var redirect=store.GetField("CreatedTypeRedirect",BindingFlags.Public|BindingFlags.Static)!;
    try{redirect.SetValue(null,typeof(DerivedC));var value=Factory(typeof(WrapperC),true);Check(observer.Find(value).Status!="QUALIFIED","Different target was accepted by its factory name");}
    finally{redirect.SetValue(null,null);}
});
Test("spoofed factory name with the same three opcodes has no producer proof",()=>
{
    var dm=new DynamicMethod("Initializer<"+typeof(WrapperC).AssemblyQualifiedName+">",typeof(WrapperC),new[]{typeof(IntPtr)});var il=dm.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Newobj,typeof(WrapperC).GetConstructor(new[]{typeof(IntPtr)})!);il.Emit(OpCodes.Ret);
    var value=(Func<IntPtr,WrapperC>)dm.CreateDelegate(typeof(Func<IntPtr,WrapperC>));Check(observer.Find(value).Status!="QUALIFIED","Name-only producer spoof received a recipe");
});
Test("unsupported Emit and original Emit exceptions remain visible and unmodified",()=>
{
    var dm=new DynamicMethod("Unsupported",typeof(void),Type.EmptyTypes);var il=dm.GetILGenerator();il.Emit(OpCodes.Nop);il.Emit(OpCodes.Ret);
    var value=(Action)dm.CreateDelegate(typeof(Action));Check(observer.Find(value).Status!="QUALIFIED","Unsupported opcode acquired proof");
    var bad=new DynamicMethod("OriginalEmitThrows",typeof(void),Type.EmptyTypes);var broken=bad.GetILGenerator();bool original=false;
    try{broken.Emit(OpCodes.Newobj,(ConstructorInfo)null!);}catch(ArgumentNullException){original=true;}
    Check(original,"Observer swallowed or replaced the original Emit exception");
    Check(observer.Snapshot().Recipes.Any(r=>r.Name=="OriginalEmitThrows"&&r.Status!="QUALIFIED"),"Missing final materialization acquired proof");
});
observer.Dispose();
Test("qualified factory receipt latches late generator divergence and preserves its frozen hash",()=>
{
    using var current=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));current.Install();Check(current.IsInstalled,current.Failure);
    var value=Factory(typeof(WrapperA),true);var before=current.Find(value);Check(before.Status=="QUALIFIED",before.Reason);var scope=current.AcquireScope();Check(scope.Valid,scope.Reason);
    var method=(DynamicMethod)value.Method.GetType().GetField("m_owner",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value.Method)!;
    var il=(ILGenerator)typeof(DynamicMethod).GetField("m_ilGenerator",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(method)!;
    il.Emit(OpCodes.Nop);var after=current.Find(value);Check(after.Status!="QUALIFIED"&&!after.ValidatedBeforeMaterialization&&after.RawIlSha256==before.RawIlSha256,"Changed generator retained a true receipt or replaced its hash");
    typeof(ILGenerator).GetField("m_length",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(il,7);
    Check(current.Find(value).Status!="QUALIFIED"&&!current.ValidateScope(scope.Token,false).Valid,"Restoring generator bytes healed the invalidated receipt");
});
Test("qualified factory receipt detects actual resolver divergence at seal without generator changes",()=>
{
    using var current=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));current.Install();Check(current.IsInstalled,current.Failure);
    var value=Factory(typeof(WrapperA),true);Check(current.Find(value).Status=="QUALIFIED","No current factory receipt");var scope=current.AcquireScope();Check(scope.Valid,scope.Reason);
    var method=(DynamicMethod)value.Method.GetType().GetField("m_owner",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value.Method)!;
    var resolver=typeof(DynamicMethod).GetField("m_resolver",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(method)!;
    var bytes=(byte[])resolver.GetType().GetField("m_code",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(resolver)!;
    bytes[0]=0x00;Check(!current.SealScope(scope.Token).Valid,"Seal ignored changed actual resolver");bytes[0]=0x02;
    Check(current.Find(value).Status!="QUALIFIED","Restoring resolver bytes healed a failed scope");
});
Test("protected emitter mutation is rejected before detouring during publication and remains invalid",()=>
{
    using var guarded=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));guarded.Install();Check(guarded.IsInstalled,guarded.Failure);
    var value=Factory(typeof(WrapperA),true);var scope=guarded.AcquireScope();Check(scope.Valid,scope.Reason);guarded.SealScope(scope.Token);
    var target=typeof(ILGenerator).GetMethod("Emit",new[]{typeof(OpCode)})!;var patcher=new Harmony("aave.recipe.test.foreign-emitter");bool rejected=false;
    using(guarded.EnterCommitScope(scope.Token))
    {try{patcher.Patch(target,postfix:new HarmonyMethod(typeof(Foreign),nameof(Foreign.Postfix)));}catch(InvalidOperationException){rejected=true;}}
    Check(rejected&&guarded.Failure.Length>0&&guarded.Find(value).Status!="QUALIFIED","Protected emitter mutation did not latch before installation");
    Check(Harmony.GetPatchInfo(target)==null||!Harmony.GetPatchInfo(target).Owners.Contains("aave.recipe.test.foreign-emitter"),"Unsafe emitter detour was installed");
});
Test("missing constructor observation cannot be rebuilt from final body or name",()=>
{
    using var missing=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));missing.Install();Check(missing.IsInstalled,missing.Failure);
    var remover=new Harmony("aave.recipe.test.remove");var constructors=typeof(DynamicMethod).GetConstructors();
    try
    {
        foreach(var method in constructors)remover.Unpatch(method,HarmonyPatchType.Postfix,"com.aavideoexport.runtime-recipes.v2");
        var value=Factory(typeof(WrapperA),true);Check(missing.Find(value).Status=="UNKNOWN","Missing constructor provenance was inferred from final bytes");
    }
    finally
    {
        missing.Dispose();var restore=new Harmony("com.aavideoexport.runtime-recipes.v2");
        foreach(var method in constructors)restore.Patch(method,postfix:new HarmonyMethod(typeof(RuntimeRecipeObserver).GetMethod("Constructed",BindingFlags.Static|BindingFlags.NonPublic)!));
    }
});
Test("bounded record overflow invalidates every retained scope",()=>
{
    using var small=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers),maximumRecords:1);small.Install();Check(small.IsInstalled,small.Failure);
    var firstValue=Factory(typeof(WrapperA),true);Check(small.Find(firstValue).Status=="QUALIFIED","Small collector initial recipe failed");var scope=small.AcquireScope();
    _=Factory(typeof(WrapperB),true);Check(!small.ValidateScope(scope.Token,false).Valid&&small.Failure.Contains("overflow"),"Overflow preserved an apparently complete scope");
});
Test("final IL parse capacity and retained scope limits fail closed",()=>
{
    using(var small=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers),maximumInstructions:2))
    {small.Install();Check(small.IsInstalled,small.Failure);var value=Factory(typeof(WrapperA),true);Check(small.Find(value).Status!="QUALIFIED"&&small.Failure.Contains("overflow"),"Lost instruction was accepted");}
    using var scopes=new RuntimeRecipeObserver(InteropRecipePolicy.ForTests(wrappers));scopes.Install();Check(scopes.IsInstalled,scopes.Failure);_=Factory(typeof(WrapperA),true);
    RecipeScopeView? last=null;for(int i=0;i<33;i++)last=scopes.AcquireScope();Check(last!=null&&!last.Valid&&scopes.Failure.Contains("overflow"),"Unbounded scopes or silent overflow");
});
RuntimeRecipeConcurrencyChecks.Run(Test);
RuntimeCommitCompositionChecks.Run(Test);
if(args.Contains("--with-v1"))Test("coexisting v1 keeps all three global coverage flags false",()=>
{
    using var json=JsonDocument.Parse(RuntimeAttributionV1.GetSnapshotJson());var root=json.RootElement;
    Check(RuntimeAttributionV1.IsInstalled&&!root.GetProperty("preexistingCoverage").GetBoolean()&&!root.GetProperty("allCodeCoverage").GetBoolean()&&!root.GetProperty("permitsStrictExport").GetBoolean(),"Recipe coordination changed v1 truthfulness");
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed; runtime={Environment.Version}; no AA or generated native constructor was executed");
return failed==0?0:1;

public class WrapperA : Il2CppObjectBase { public WrapperA(IntPtr pointer):base(pointer){} }
public class WrapperB : Il2CppObjectBase { public WrapperB(IntPtr pointer):base(pointer){} }
public class WrapperC : Il2CppObjectBase { public WrapperC(IntPtr pointer):base(pointer){} }
public class BadWrapper : Il2CppObjectBase { public static int SideEffects; public BadWrapper(IntPtr pointer):base(pointer){SideEffects++;} }
public class WrapperMissing : Il2CppObjectBase { public WrapperMissing(IntPtr pointer):base(pointer){} }
public class GenericWrapper<T> : Il2CppObjectBase { public GenericWrapper(IntPtr pointer):base(pointer){} }
public class DerivedC : WrapperC { public DerivedC(IntPtr pointer):base(pointer){} }
public static class Foreign { public static void Prefix(){} public static void Postfix(){} }
