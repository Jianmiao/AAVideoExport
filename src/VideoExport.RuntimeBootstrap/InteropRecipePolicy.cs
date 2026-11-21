using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Diagnostics;
using HarmonyLib;

namespace AAVideoExport.RuntimeBootstrap;

internal sealed record RecipeMemberIdentity(string DeclaringType, int Token, string IlSha256, string AssemblySha256, string Mvid);
internal sealed record RecipeTypeIdentity(string Type, string AssemblySha256, string Mvid);
internal sealed record InteropRecipeProof(RecipeTypeIdentity OriginalType, RecipeTypeIdentity ReturnType,
    RecipeTypeIdentity? Redirect, IReadOnlyList<RecipeMemberIdentity> Constructors,
    IReadOnlyList<RecipeMemberIdentity> TypeInitializers, string InitializationBoundary);

// Fixed runtime/preloader pins plus the separately frozen story/render interop
// manifest. Names and directory membership never grant constructor qualification.
internal sealed class InteropRecipePolicy
{
    internal const string EvidenceKind = "PRE_MATERIALIZATION_FINAL_IL";
    internal const string FactoryType = "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase+InitializerStore`1";
    internal const int FactoryToken = 0x060009BA;
    internal const string FactoryIl = "7BE08A77ADBD891CA4FE7F044F88FDFC561A65C4504569F0567C43EEBE3FA15F";
    internal const string RuntimeHash = "65AB051A681C2C1E52DF7596FF738BE401AAE72CFF882FF18241CF54A3EA38A4";
    internal const string CoreLibHash = "2CAA46D2AE726E135592033B6C682232A08698C23DBD1A9207F86C4CCA081AD7";
    private static readonly Dictionary<string,string> Pins = new(StringComparer.Ordinal)
    {
        ["System.Private.CoreLib"]=CoreLibHash,
        ["BepInEx.Core"]="352AACC2D5356489A00891F76DB28B6463C81D25BCB02F90035171A669B7E35B",
        ["BepInEx.Preloader.Core"]="EC168752C9A885D9FE035370AC2DBD8B52B91B24CB60FBC425CFCBEE5436DAE9",
        ["BepInEx.Unity.IL2CPP"]="62C8246A3076BFE459EC80C482381228D0EB1DDE3434FE97AE5255FEF406FE72",
        ["ModTheAzureArchive"]="9E3F26880208F0DAF7443ABB146D69EC6C549FCA7D60141A68B4DA8BF368FAE7",
        ["Il2CppInterop.Runtime"]=RuntimeHash,
        ["Il2CppInterop.Common"]="070E04F2276A8963A6D2A7EB52D43B644F3AE9AA3816C34541124D3862F1B37F",

    };
    private readonly Dictionary<Assembly,string> validated = new();
    private readonly Dictionary<string,string> baselineFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Type> testTypes;
    private readonly object gate = new();
    internal string PolicyId { get; }
    internal string PolicySha256 { get; }
    internal bool KnownHostBaseline { get; }
    internal MethodInfo Factory { get; }
    internal MethodInfo FactoryGetter { get; }

    private InteropRecipePolicy(string? hostRoot, IEnumerable<Type>? tests)
    {
        Require(Environment.Version == new Version(6,0,7),"Only the inspected AA .NET 6.0.7 runtime is supported.");
        Require(HashFile(typeof(object).Assembly.Location)==CoreLibHash,"System.Private.CoreLib is outside the inspected runtime.");
        testTypes = new HashSet<Type>(tests ?? Array.Empty<Type>());
        PolicyId = tests == null ? "aa101.interop-initializer.intptr.final-il.v2" : "isolated-test.interop-initializer.intptr.final-il.v2";
        KnownHostBaseline = tests == null;
        Pin(typeof(object).Assembly);
        var runtime = AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Il2CppInterop.Runtime");
        Pin(runtime);
        var factoryType=runtime.GetType(FactoryType,true)!;
        Factory=factoryType.GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic)!;
        FactoryGetter=factoryType.GetProperty("Initializer",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetMethod!;
        Require(Factory.MetadataToken==FactoryToken && HashIl(Factory)==FactoryIl,"Inspected interop factory IL changed.");
        Require(factoryType.GetGenericArguments().Length==1 && factoryType.GetGenericArguments()[0].GetGenericParameterConstraints().Length==0,"Factory generic metadata changed.");
        EnsureUnpatched(Factory);EnsureUnpatched(FactoryGetter);
        if(tests == null)
        {
            Require(!string.IsNullOrWhiteSpace(hostRoot),"A known AA host root is required.");
            string executable=Process.GetCurrentProcess().MainModule?.FileName??"";
            Require(string.Equals(Path.GetFullPath(executable),Path.GetFullPath(Path.Combine(hostRoot!,"AzureArchive.exe")),StringComparison.OrdinalIgnoreCase),"Production recipe baseline requires the actual AA host process.");
            CheckFile(hostRoot!,"AzureArchive.exe","260FC3ECC5F38DAB5230BA624A52E495708D861196DDA72486C1250760FE6EFF");
            CheckFile(hostRoot!,"GameAssembly.dll","0776516C59811C724C450E205930546C5D102975F279E33C46C96B878FA06C8B");
            CheckFile(hostRoot!,"BepInEx/patchers/ModTheAzureArchive.dll","9E3F26880208F0DAF7443ABB146D69EC6C549FCA7D60141A68B4DA8BF368FAE7");
            CheckFile(hostRoot!,"BepInEx/core/BepInEx.Preloader.Core.dll","EC168752C9A885D9FE035370AC2DBD8B52B91B24CB60FBC425CFCBEE5436DAE9");
            foreach(var image in InteropImageManifest.BaselineFiles)CheckFile(hostRoot!,image.Key,image.Value);
            foreach(string name in new[]{"BepInEx.Core","BepInEx.Preloader.Core","BepInEx.Unity.IL2CPP","ModTheAzureArchive"})
                Pin(AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name==name));
            string clr=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m=>string.Equals(m.ModuleName,"coreclr.dll",StringComparison.OrdinalIgnoreCase)).FileName
                ?? throw new NotSupportedException("Loaded CLR path is unavailable.");
            Require(HashFile(clr)=="04377968C540F704AAE92ECB0EC9BBFD5FDDFE226DFCA30AA3EEB52006A44FD8","Loaded CLR differs from the inspected host runtime.");
            baselineFiles.Add(clr,"04377968C540F704AAE92ECB0EC9BBFD5FDDFE226DFCA30AA3EEB52006A44FD8");
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if(assembly.IsDynamic) continue;
                string location=assembly.Location.Replace('/','\\');
                Require(!location.Contains("\\mods\\",StringComparison.OrdinalIgnoreCase) && !location.Contains("\\BepInEx\\plugins\\",StringComparison.OrdinalIgnoreCase),"Ordinary Mods were already loaded before recipe observation.");
            }
        }
        string policyText=PolicyId+"\nevidence="+EvidenceKind+"\nrecipe=actual-final-il-ldarg0-newobj-ret/actual-resolver/target-equals-T/constructor-chain-v2\nruntime=6.0.7\nfactory-token="+FactoryToken.ToString("X8")
            +"\nfactory-il="+FactoryIl+"\nbootstrap="+HashFile(typeof(InteropRecipePolicy).Assembly.Location)
            +"\nhost-native=0776516C59811C724C450E205930546C5D102975F279E33C46C96B878FA06C8B\nclr=04377968C540F704AAE92ECB0EC9BBFD5FDDFE226DFCA30AA3EEB52006A44FD8\n"
            +string.Join("\n",Pins.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value))
            +"\ninterop-manifest="+InteropImageManifest.IdentityText
            +string.Join("\n",testTypes.OrderBy(t=>t.AssemblyQualifiedName,StringComparer.Ordinal).Select(t=>"test="+t.AssemblyQualifiedName+"|"+HashFile(t.Assembly.Location)));
        PolicySha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policyText)));
    }
    internal static InteropRecipePolicy ForHost(string root)=>new(root,null);
    internal static InteropRecipePolicy ForTests(params Type[] types)=>new(null,types);
    internal bool IsFactory(MethodBase method)
    {
        if(method.Module!=Factory.Module || method.MetadataToken!=FactoryToken)return false;
        Require(HashIl(method)==FactoryIl,"Factory frame IL differs from inspected image.");
        EnsureUnpatched(method);return true;
    }
    internal bool IsPotentialConstructor(MethodBase method)
    {
        // MethodBase.IsConstructor excludes .cctor; both constructor kinds
        // are recorded in guarded closure evidence and must latch mutations.
        if(method is not ConstructorInfo||method.DeclaringType==null)return false;
        if(testTypes.Contains(method.DeclaringType))return true;
        string name=method.Module.Assembly.GetName().Name??"";
        return InteropImageManifest.Contains(name)||Pins.ContainsKey(name)&&name is not "System.Private.CoreLib" and not "BepInEx.Core" and not "BepInEx.Preloader.Core" and not "BepInEx.Unity.IL2CPP" and not "ModTheAzureArchive";
    }
    internal bool IsFactoryInfrastructure(MethodBase method)=>method.Module==Factory.Module&&
        (method.MetadataToken==Factory.MetadataToken||method.MetadataToken==FactoryGetter.MetadataToken||
         method.DeclaringType?.FullName?.StartsWith("Il2CppInterop.Runtime.Il2CppClassPointerStore`1",StringComparison.Ordinal)==true||
         method.DeclaringType?.FullName=="Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");

    internal InteropRecipeProof Prove(ConstructorInfo constructor, Type returnedType, Delegate result,
        out FieldInfo redirectField,out Type? redirect,out FieldInfo cachedInitializer,out MethodBase[] guarded)
        =>ProveBeforeMaterialization(constructor,returnedType,result.GetType(),result.Target,out redirectField,out redirect,out cachedInitializer,out guarded);
    internal InteropRecipeProof ProveBeforeMaterialization(ConstructorInfo constructor,Type returnedType,Type delegateType,object? delegateTarget,
        out FieldInfo redirectField,out Type? redirect,out FieldInfo cachedInitializer,out MethodBase[] guarded)
    {
        Require(delegateType.IsGenericType && delegateType.GetGenericTypeDefinition()==typeof(Func<,>) && delegateTarget==null,"Factory request is not the expected unbound Func<IntPtr,T>.");
        var args=delegateType.GetGenericArguments();Require(args[0]==typeof(IntPtr),"Factory delegate input differs from IntPtr.");
        Type original=args[1];
        // The inspected 94 definitions all have this direct wrapper path. A
        // different redirect target needs its own initializer behavior proof.
        Require(original==returnedType && constructor.DeclaringType==returnedType,"Different-target redirects are outside the initial exact wrapper scope.");
        Require(constructor.IsPublic && !constructor.IsStatic && constructor.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(IntPtr)}),"Constructor signature differs from the observed wrapper recipe.");
        var store=Factory.Module.Assembly.GetType("Il2CppInterop.Runtime.Il2CppClassPointerStore`1",true)!.MakeGenericType(original);
        redirectField=store.GetField("CreatedTypeRedirect",BindingFlags.Public|BindingFlags.Static)!;
        Require(!redirectField.IsInitOnly && redirectField.FieldType==typeof(Type),"Inspected redirect field shape changed.");
        // Create has already read this initialized store. Reading it here does
        // not call the target Mod constructor or replace the returned delegate.
        redirect=redirectField.GetValue(null) as Type;
        Require((redirect??original)==returnedType,"Redirect differs from the actually emitted return/constructor target.");
        cachedInitializer=Factory.DeclaringType!.MakeGenericType(original).GetField("_initializer",BindingFlags.NonPublic|BindingFlags.Static)!;
        var closedFactory=Factory.DeclaringType!.MakeGenericType(original);
        var closedCreate=closedFactory.GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic)!;
        var closedGetter=closedFactory.GetProperty("Initializer",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.GetMethod!;
        EnsureUnpatched(closedCreate);EnsureUnpatched(closedGetter);
        var constructorProof=AuditConstructorMetadata(constructor);
        var members=new List<MethodBase>{Factory,FactoryGetter,closedCreate,closedGetter};members.AddRange(constructorProof.Guarded);
        var initializers=constructorProof.Initializers.ToList();
        if(store.TypeInitializer is { } storeInit){EnsureUnpatched(storeInit);members.Add(storeInit);initializers.Add(Member(storeInit));}
        guarded=members.Distinct().ToArray();
        return new(TypeIdentity(original),TypeIdentity(returnedType),redirect==null?null:TypeIdentity(redirect),constructorProof.Constructors,initializers.AsReadOnly(),
            "Constructor closure proved. Type initializer identities belong to the exact host/class-pointer-store baseline; this is not preexisting/all-code execution coverage.");
    }

    // Pure metadata inspection, also exercised on the actual 94 known target
    // definitions without invoking their factory, initializers or constructors.
    internal (IReadOnlyList<RecipeMemberIdentity> Constructors,IReadOnlyList<RecipeMemberIdentity> Initializers,MethodBase[] Guarded) AuditConstructorMetadata(ConstructorInfo constructor)
    {
        var members=new List<MethodBase>();var constructors=new List<RecipeMemberIdentity>();var initializers=new List<RecipeMemberIdentity>();
        var visited=new HashSet<Type>();ConstructorInfo current=constructor;
        while(true)
        {
            Type type=current.DeclaringType!;Require(visited.Add(type)&&visited.Count<=32,"Constructor recursion exceeds the finite wrapper scope.");
            PinType(type);EnsureUnpatched(current);members.Add(current);constructors.Add(Member(current));
            if(type.TypeInitializer is { } initializer){EnsureUnpatched(initializer);members.Add(initializer);initializers.Add(Member(initializer));}
            if(type.FullName=="Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase")
            {
                Require(HashIl(current)=="8CD3F085BF06B02E0053B77D342DA32C1A8921EF92A48914250B8A8D528DCFF1","Known wrapper root constructor changed.");
                var gc=type.GetMethod("CreateGCHandle",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!;
                Require(HashIl(gc)=="6D830107AB1951AC02B60C1D70EE0B0676C5410C9EDBB0B18495F51A5F06137A","Known native GC-handle leaf changed.");
                EnsureUnpatched(gc);members.Add(gc);constructors.Add(Member(gc));break;
            }
            byte[] il=current.GetMethodBody()?.GetILAsByteArray()??Array.Empty<byte>();
            Require(il.Length==8&&il[0]==0x02&&il[1]==0x03&&il[2]==0x28&&il[7]==0x2a,"Constructor contains behavior beyond direct IntPtr base wrapping.");
            Require(current.GetMethodBody()!.LocalVariables.Count==0&&current.GetMethodBody()!.ExceptionHandlingClauses.Count==0,"Constructor contains locals/exception handling.");
            var next=type.BaseType?.GetConstructor(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(IntPtr)},null);
            Require(next!=null,"Wrapper chain does not reach the inspected native handle root.");
            var operand=current.Module.ResolveMethod(BitConverter.ToInt32(il,3),type.IsGenericType?type.GetGenericArguments():null,null);
            Require(operand is ConstructorInfo && operand.Module==next!.Module&&operand.MetadataToken==next.MetadataToken,"Constructor calls a different target than its direct wrapper base.");
            current=next!;
        }
        return (constructors.AsReadOnly(),initializers.AsReadOnly(),members.Distinct().ToArray());
    }

    internal void Validate(bool images)
    {
        EnsureUnpatched(Factory);EnsureUnpatched(FactoryGetter);
        if(images)lock(gate)
        {
            foreach(var pair in validated)Require(HashFile(pair.Key.Location)==pair.Value,"A pinned image changed after recipe observation.");
            foreach(var pair in baselineFiles)Require(HashFile(pair.Key)==pair.Value,"A known host baseline file changed after acquisition.");
        }
    }
    internal static void EnsureUnpatched(MethodBase method)
    {
        var p=Harmony.GetPatchInfo(method);
        Require(p==null||!(p.Prefixes.Any()||p.Postfixes.Any()||p.Transpilers.Any()||p.Finalizers.Any()||p.ILManipulators.Any()||p.Owners.Any()),"Foreign patch on recipe factory/constructor boundary: "+method);
    }
    private string Pin(Assembly assembly)
    {
        lock(gate)
        {
            if(validated.TryGetValue(assembly,out var old))return old;
            Require(!assembly.IsDynamic&&!string.IsNullOrWhiteSpace(assembly.Location),"Unverifiable constructor assembly.");
            string hash=HashFile(assembly.Location);
            bool testAllowed=testTypes.Any(t=>t.Assembly==assembly);
            string name=assembly.GetName().Name??"";
            bool manifestImage=InteropImageManifest.TryGet(name,out var image);
            Require(testAllowed || manifestImage&&hash==image!.Sha256 || Pins.TryGetValue(name,out string? expected)&&hash==expected,"Constructor image is outside the current exact host manifest: "+name);
            if(manifestImage&&!testAllowed)Require(assembly.ManifestModule.ModuleVersionId.ToString()==image!.Mvid&&assembly.GetName().Version?.ToString()==image.Version,"Loaded constructor module/version differs from the frozen interop manifest.");
            using var stream=File.OpenRead(assembly.Location);using var pe=new PEReader(stream);var metadata=pe.GetMetadataReader();
            Require(metadata.GetGuid(metadata.GetModuleDefinition().Mvid)==assembly.ManifestModule.ModuleVersionId,"Loaded constructor metadata differs from pinned file.");
            validated.Add(assembly,hash);return hash;
        }
    }
    private void PinType(Type type)
    {
        Require(!type.ContainsGenericParameters,"Open constructor types are not recipes.");Pin(type.Assembly);
        if(testTypes.Any(t=>t.Assembly==type.Assembly))Require(testTypes.Contains(type),"Unlisted isolated test type.");
        foreach(Type argument in type.IsGenericType?type.GetGenericArguments():Type.EmptyTypes){if(argument!=typeof(IntPtr))PinType(argument);}
    }
    private RecipeTypeIdentity TypeIdentity(Type t){PinType(t);return new(t.AssemblyQualifiedName??t.FullName??"",Pin(t.Assembly),t.Module.ModuleVersionId.ToString());}
    private RecipeMemberIdentity Member(MethodBase m)=>new(m.DeclaringType?.AssemblyQualifiedName??"",m.MetadataToken,HashIl(m),Pin(m.Module.Assembly),m.Module.ModuleVersionId.ToString());
    internal static string HashIl(MethodBase m)=>Convert.ToHexString(SHA256.HashData(m.GetMethodBody()?.GetILAsByteArray()??throw new NotSupportedException("Missing managed method body.")));
    internal static string HashFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private void CheckFile(string root,string path,string hash)
    {string full=Path.GetFullPath(Path.Combine(root,path));Require(HashFile(full)==hash,"Known host baseline differs: "+path);baselineFiles[full]=hash;}
    internal static void Require(bool value,string message){if(!value)throw new NotSupportedException(message);}
}
