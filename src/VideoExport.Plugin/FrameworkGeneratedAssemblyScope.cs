using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Exact current MonoMod cache/metadata proof. This does not authorize arbitrary
// ILGeneratorShim.Target behavior, arbitrary native handles or future assemblies.
internal sealed partial class FrameworkGeneratedAssemblyScope : IDisposable
{
    internal const string Marker="FrameworkGeneratedAssemblyScope.cached-bridges.v1";
    internal const string PolicyId="net607.monomod22731.cached-framework-bridges.v1";
    internal const string EvidenceKind="PINNED_CURRENT_FRAMEWORK_BRIDGE_METADATA";
    private const string Owner="com.aavideoexport.framework-generated-scope.v1";
    private const string ProxyName="MonoMod.Utils.Cil.ILGeneratorProxy";
    private const string HelperName="MonoMod.RuntimeDetour.Runtime.NETCore3+Helpers";
    private const string ProxyGraphPin="30038949F4846F8F841203B1DEB877683DF98B1D177C2798320EF2F9981CDF9A";
    private const string HelperGraphPin="78303FCA42BBF693CF1B87846F1128EE447C154A69DDFC45A8281D3503698B3B";
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    private static readonly object Gate=new();
    private static readonly List<WeakReference<FrameworkGeneratedAssemblyScope>> Scopes=new();
    private static readonly HashSet<(Module Module,int Token)> KnownMethods=new(),MutationHistory=new();
    private static readonly HashSet<Assembly> KnownGeneratedAssemblies=new();
    private static readonly MethodBase[] VerifierMethods=VerifierTypes(typeof(FrameworkGeneratedAssemblyScope))
        .Concat(VerifierTypes(typeof(AssemblySimpleNameCache)))
        .SelectMany(t=>t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))).ToArray();
    private static bool runtimeReplaced;
    private static readonly Dictionary<string,string> Pins=new(StringComparer.Ordinal)
    {
        ["MonoMod.Utils"]="72A5B4249985873D7ECB160CBBED7A1E6540A38EE7618C72C64B9CB068C1438A",
        ["MonoMod.RuntimeDetour"]="1B3D55F53CB9F9F2550DAED4DFD300B247D71698A0ACE82BC7B9FF38D563594F",
        ["0Harmony"]="6C898933B52149E8BCF6722305C7E4D94ADD47C9E53B364096F91721624C0877",
        ["Il2CppInterop.Runtime"]="65AB051A681C2C1E52DF7596FF738BE401AAE72CFF882FF18241CF54A3EA38A4",
        ["System.Private.CoreLib"]="2CAA46D2AE726E135592033B6C682232A08698C23DBD1A9207F86C4CCA081AD7"
    };
    private const string ClrPin="04377968C540F704AAE92ECB0EC9BBFD5FDDFE226DFCA30AA3EEB52006A44FD8";
    private static object? harmonyGate;
    private static MethodInfo? updateWrapper,reversePatch,runtimeSetter;
    private static bool hooksInstalled;
    private static string installationFailure="";
    private readonly PhysicalPin[] images;
    private readonly CacheBinding binding;
    private readonly string proxyGraph,helperGraph;
    private readonly HashSet<MethodBase> guarded;
    private readonly AdditionalProof additional=new();
    private bool disposed,consumed;
    private int commitThread;
    private string failure="";
    internal string IdentitySha256 {get;}
    internal string Boundary => "Current cached proxy forwarding glue, pinned CoreCLR helper, exact cached runtime delegate declarations and sizeof<T> only; Target instances, arbitrary handle inputs and future code are not certified.";

    private FrameworkGeneratedAssemblyScope(PhysicalPin[] images,CacheBinding binding,Template proxy,Template helper)
    {
        this.images=images;this.binding=binding;proxyGraph=proxy.Hash;helperGraph=helper.Hash;
        guarded=new(proxy.Methods.Concat(helper.Methods).Concat(binding.Producers).Concat(VerifierMethods));
        IdentitySha256=Hash(PolicyId+"\n"+EvidenceKind+"\n"+proxy.Hash+"\n"+helper.Hash+"\n"+AdditionalGrammar+"\n"+string.Join("\n",Pins.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))+"\ncoreclr="+ClrPin);
        RefreshAdditional();
    }
    internal static FrameworkGeneratedAssemblyScope Acquire()
    {
        var images=PinCurrentRuntime();
        // Check existing authoritative caches before installing our own
        // monitors: Harmony setup must not manufacture missing scope evidence.
        var existing=ReadCurrentBinding();
        EnsureMonitors();
        lock(HarmonyGate)lock(Gate)
        {
            ValidateMonitors();var binding=ReadCurrentBinding();
            Require(ReferenceEquals(existing.Proxy,binding.Proxy)&&ReferenceEquals(existing.Runtime,binding.Runtime)
                &&ReferenceEquals(existing.Helper,binding.Helper)&&ReferenceEquals(existing.NativeHandleDelegate,binding.NativeHandleDelegate),"Framework cache identity changed during monitor setup.");
            var proxy=InspectTemplate(binding.Proxy.Assembly,true,binding.Shim);
            var helper=InspectTemplate(binding.Helper.Module.Assembly,false,binding.Shim);
            Require(proxy.Hash==ProxyGraphPin&&helper.Hash==HelperGraphPin,"Generated metadata differs from the inspected normalized template pins: "+proxy.Hash+" / "+helper.Hash);
            var methods=binding.Producers.Concat(proxy.Methods).Concat(helper.Methods).Concat(VerifierMethods).ToArray();
            Require(!runtimeReplaced&&!methods.Any(m=>MutationHistory.Contains(Key(m))),"Protected framework mutation history cannot be reset by acquiring another scope.");
            foreach(var method in methods){EnsureUnpatched(method);KnownMethods.Add(Key(method));}
            Require(KnownGeneratedAssemblies.Count<4096||KnownGeneratedAssemblies.Contains(binding.Proxy.Assembly),"Framework generated assembly history capacity overflow.");
            KnownGeneratedAssemblies.Add(binding.Proxy.Assembly);KnownGeneratedAssemblies.Add(binding.Helper.Module.Assembly);
            Scopes.RemoveAll(w=>!w.TryGetTarget(out var s)||s.disposed||s.consumed);
            Require(Scopes.Count<32,"Framework retained scope capacity overflow.");
            var result=new FrameworkGeneratedAssemblyScope(images,binding,proxy,helper);Scopes.Add(new(result));return result;
        }
    }
    internal void Validate(bool rehashPhysicalImages=false)
    {
        lock(HarmonyGate)lock(Gate)ValidateLocked(rehashPhysicalImages);
    }
    internal bool AuthorizesAssembly(Assembly candidate)
    {
        lock(HarmonyGate)lock(Gate)
        {
            ValidateLocked(false);
            return ReferenceEquals(candidate,binding.Proxy.Assembly)||ReferenceEquals(candidate,binding.Helper.Module.Assembly)||additional.Assemblies.Contains(candidate);
        }
    }
    internal IReadOnlyList<Assembly> Assemblies => Array.AsReadOnly(new[]{binding.Proxy.Assembly,binding.Helper.Module.Assembly}.Concat(additional.Assemblies).Distinct().ToArray());
    private void ValidateLocked(bool rehash)
    {
        try
        {
            Require(!disposed&&!consumed&&failure.Length==0,"Framework scope is released, consumed or invalid: "+failure);
            Require(!runtimeReplaced&&!guarded.Any(m=>MutationHistory.Contains(Key(m))),"Protected framework patch/runtime history changed.");
            ValidateMonitors();foreach(var pin in images)pin.Validate(rehash);
            var now=ReadCurrentBinding();
            Require(ReferenceEquals(now.Proxy,binding.Proxy)&&ReferenceEquals(now.Runtime,binding.Runtime)&&ReferenceEquals(now.Helper,binding.Helper)
                &&ReferenceEquals(now.NativeHandleDelegate,binding.NativeHandleDelegate),"Actual MonoMod cache/runtime/delegate identity changed.");
            Require(InspectTemplate(now.Proxy.Assembly,true,now.Shim).Hash==proxyGraph&&InspectTemplate(now.Helper.Module.Assembly,false,now.Shim).Hash==helperGraph,"Current framework metadata or IL changed.");
            RefreshAdditional(rehash);
            foreach(var method in guarded)EnsureUnpatched(method);
        }
        catch(Exception error){Latch(error.Message);throw;}
    }
    internal IDisposable EnterCommitScope(CancellationToken cancellation=default)
    {
        bool h=false,g=false;
        try
        {
            Enter(HarmonyGate,cancellation);h=true;Enter(Gate,cancellation);g=true;
            cancellation.ThrowIfCancellationRequested();ValidateLocked(true);
            Require(commitThread==0,"Framework publication guard is already held.");commitThread=Environment.CurrentManagedThreadId;
            return new CommitLease(this,commitThread);
        }
        catch{if(g)Monitor.Exit(Gate);if(h)Monitor.Exit(HarmonyGate);throw;}
    }
    public void Dispose(){lock(HarmonyGate)lock(Gate){disposed=true;Scopes.RemoveAll(w=>!w.TryGetTarget(out var s)||ReferenceEquals(s,this));}}
    private void Latch(string error){if(failure.Length==0)failure=error;}
    private bool Protects(MethodBase method)=>ReferenceEquals(method.Module.Assembly,binding.Proxy.Assembly)||ReferenceEquals(method.Module.Assembly,binding.Helper.Module.Assembly)
        ||additional.Assemblies.Contains(method.Module.Assembly)||GeneratedProducers.Contains(method)
        ||guarded.Any(m=>m.Module==method.Module&&m.MetadataToken==method.MetadataToken)||method==updateWrapper||method==reversePatch||method==runtimeSetter
        ||method.DeclaringType==typeof(FrameworkGeneratedAssemblyScope);
    private static object HarmonyGate=>harmonyGate??=typeof(PatchProcessor).GetField("locker",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Enter(object gate,CancellationToken token){token.ThrowIfCancellationRequested();while(!Monitor.TryEnter(gate,25))token.ThrowIfCancellationRequested();}
    private sealed class CommitLease(FrameworkGeneratedAssemblyScope scope,int thread):IDisposable
    {
        private bool done;
        public void Dispose()
        {
            if(done)return;Require(Environment.CurrentManagedThreadId==thread,"Framework publication guard changed threads.");done=true;
            scope.consumed=true;scope.commitThread=0;Monitor.Exit(Gate);Monitor.Exit(HarmonyGate);
        }
    }

    // High-level Harmony boundaries are independent of the bootstrap's exact
    // PatchProcessor hook set. The pinned binary routes all five patch kinds
    // and both Unpatch overloads through UpdateWrapper. ReversePatch is separate.
    private static void EnsureMonitors()
    {
        lock(HarmonyGate)lock(Gate)
        {
            Require(installationFailure.Length==0,"Framework mutation monitor unavailable: "+installationFailure);
            if(hooksInstalled){ValidateMonitors();return;}
            try
            {
                var functions=typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions",true)!;
                updateWrapper=functions.GetMethod("UpdateWrapper",All)!;reversePatch=functions.GetMethod("ReversePatch",All)!;
                var detour=Loaded("MonoMod.RuntimeDetour");runtimeSetter=detour.GetType("MonoMod.RuntimeDetour.DetourHelper",true)!.GetProperty("Runtime")!.SetMethod!;
                foreach(var method in new[]{updateWrapper,reversePatch,runtimeSetter})EnsureUnpatched(method);
                var harmony=new Harmony(Owner);var flags=BindingFlags.Static|BindingFlags.NonPublic;
                foreach(var pair in new[]{(updateWrapper,nameof(BeforeUpdate)),(reversePatch,nameof(BeforeReverse)),(runtimeSetter,nameof(BeforeRuntimeSet))})
                    harmony.Patch(pair.Item1,prefix:new HarmonyMethod(typeof(FrameworkGeneratedAssemblyScope).GetMethod(pair.Item2,flags)),
                        finalizer:new HarmonyMethod(typeof(FrameworkGeneratedAssemblyScope).GetMethod(nameof(AfterMutation),flags)));
                InstallGeneratedProducerMonitors(harmony);
                hooksInstalled=true;ValidateMonitors();
            }
            catch(Exception error){installationFailure=error.GetType().Name+": "+error.Message;throw;}
        }
    }
    private static void ValidateMonitors()
    {
        Require(hooksInstalled,"Framework mutation monitor is not installed.");
        Require(!new[]{updateWrapper,reversePatch,runtimeSetter}.Any(m=>MutationHistory.Contains(Key(m!))),"Framework mutation monitor has a permanent mutation history.");
        foreach(var pair in new[]{(updateWrapper,nameof(BeforeUpdate)),(reversePatch,nameof(BeforeReverse)),(runtimeSetter,nameof(BeforeRuntimeSet))})
        {
            var info=Harmony.GetPatchInfo(pair.Item1!)??throw new InvalidOperationException("Framework mutation monitor disappeared.");
            bool Exact(IEnumerable<Patch> patches,string expected)=>patches.Count()==1&&patches.Single().owner==Owner
                &&patches.Single().PatchMethod==typeof(FrameworkGeneratedAssemblyScope).GetMethod(expected,BindingFlags.Static|BindingFlags.NonPublic);
            Require(Exact(info.Prefixes,pair.Item2)&&Exact(info.Finalizers,nameof(AfterMutation))&&!info.Postfixes.Any()&&!info.Transpilers.Any()&&!info.ILManipulators.Any(),"Framework mutation monitor owner/category changed.");
        }
        ValidateGeneratedProducerMonitors();
    }
    private static void BeforeUpdate(MethodBase? __0,out MutationLease? __state)=>__state=__0==null?null:Mutation(__0,false);
    private static void BeforeReverse(HarmonyMethod? __0,out MutationLease? __state)=>__state=__0?.method is {} target?Mutation(target,false):null;
    private static void BeforeRuntimeSet(out MutationLease? __state)=>__state=Mutation(runtimeSetter!,true);
    private static MutationLease Mutation(MethodBase target,bool all)
    {
        Monitor.Enter(HarmonyGate);
        try
        {
            lock(Gate)
            {
                if(hooksInstalled&&(all||KnownMethods.Contains(Key(target))||KnownGeneratedAssemblies.Contains(target.Module.Assembly)
                    ||target==updateWrapper||target==reversePatch||target==runtimeSetter||GeneratedProducers.Contains(target)))
                {MutationHistory.Add(Key(target));if(all)runtimeReplaced=true;}
                foreach(var weak in Scopes)
                    if(weak.TryGetTarget(out var scope)&&!scope.disposed&&!scope.consumed&&(all||scope.Protects(target)))
                    {
                        scope.Latch("Protected framework mutation: "+target.DeclaringType?.FullName+"."+target.Name);
                        if(scope.commitThread==Environment.CurrentManagedThreadId)throw new InvalidOperationException(scope.failure);
                    }
            }
            return new MutationLease();
        }
        catch{Monitor.Exit(HarmonyGate);throw;}
    }
    private static Exception? AfterMutation(Exception? __exception,MutationLease? __state){__state?.Dispose();return __exception;}
    private sealed class MutationLease:IDisposable{private bool done;public void Dispose(){if(done)return;done=true;Monitor.Exit(HarmonyGate);}}

    private sealed record CacheBinding(Type Proxy,Type Shim,object Runtime,MethodInfo Helper,Delegate NativeHandleDelegate,MethodBase[] Producers);
    private static CacheBinding ReadCurrentBinding()
    {
        var utils=Loaded("MonoMod.Utils");var detour=Loaded("MonoMod.RuntimeDetour");
        var shim=utils.GetType("MonoMod.Utils.Cil.ILGeneratorShim",true)!;
        var builder=shim.GetNestedType("ILGeneratorBuilder",BindingFlags.NonPublic)!;
        var proxy=builder.GetField("ProxyType",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null) as Type;
        Require(proxy!=null,"The real ILGeneratorBuilder cache has not produced its current proxy.");
        var detourHelper=detour.GetType("MonoMod.RuntimeDetour.DetourHelper",true)!;
        var runtime=detourHelper.GetField("_Runtime",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
        Require(runtime!=null&&runtime.GetType()==detour.GetType("MonoMod.RuntimeDetour.Platforms.DetourRuntimeNET60Platform",true),"Actual cached MonoMod runtime is not the pinned NET60 platform.");
        var platform=detour.GetType("MonoMod.RuntimeDetour.Platforms.DetourRuntimeNETCore30Platform",true)!;
        var method=platform.GetField("_getTypeFromHandleUnsafeMethod",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime) as MethodInfo;
        var call=platform.GetField("GetTypeFromNativeHandle",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runtime) as Delegate;
        Require(method!=null&&call!=null&&call.Target==null&&call.GetInvocationList().Length==1&&call.Method==method
            &&call.GetType()==platform.GetNestedType("d_GetTypeFromNativeHandle",BindingFlags.NonPublic),"Actual helper method and cached native-handle delegate are not bound together.");
        Require(ReferenceEquals(call!.Method.Module.Assembly,method!.Module.Assembly),"Actual helper delegate belongs to another assembly.");
        Require(ReadSystemAssemblyFlag(method.Module.Assembly),"Current helper lacks the pinned CoreCLR system-assembly boundary flag.");
        var producers=new List<MethodBase>{builder.GetMethod("GenerateProxy",All)!,platform.GetMethod("GetOrCreateGetTypeFromHandleUnsafe",All)!,
            platform.GetMethod("SetupJitHookHelpers",All)!,platform.GetMethod("MakeAssemblySystemAssembly",All)!};
        producers.AddRange(shim.GetMethods(All).Where(m=>m.Name is "get_ProxyType" or "GetProxy" or "GetProxyType"));
        return new(proxy!,shim,runtime!,method,call,producers.ToArray());
    }
    private static PhysicalPin[] PinCurrentRuntime()
    {
        Require(OperatingSystem.IsWindows()&&Environment.Is64BitProcess&&Environment.Version==new Version(6,0,7),"Only the pinned Windows x64 .NET 6.0.7 framework boundary is supported.");
        var pins=Pins.Select(p=>PhysicalPin.Create(Loaded(p.Key),p.Value)).ToList();
        using var process=Process.GetCurrentProcess();var clr=process.Modules.Cast<ProcessModule>().Single(m=>string.Equals(m.ModuleName,"coreclr.dll",StringComparison.OrdinalIgnoreCase));
        pins.Add(PhysicalPin.Create(clr.FileName??throw new InvalidOperationException("Loaded CoreCLR path is unavailable."),ClrPin));return pins.ToArray();
    }
    private sealed record PhysicalPin(Assembly? Assembly,string Path,string Sha,Guid Mvid,long Length,DateTime Modified)
    {
        internal static PhysicalPin Create(Assembly assembly,string sha)
        {
            Require(!assembly.IsDynamic&&!string.IsNullOrWhiteSpace(assembly.Location),"Pinned framework image has no physical location.");
            var result=Create(assembly.Location,sha);using var stream=File.OpenRead(result.Path);using var pe=new PEReader(stream);
            var metadata=pe.GetMetadataReader();Require(metadata.GetGuid(metadata.GetModuleDefinition().Mvid)==assembly.ManifestModule.ModuleVersionId,"Loaded framework metadata differs from its pinned physical image.");
            return result with{Assembly=assembly,Mvid=assembly.ManifestModule.ModuleVersionId};
        }
        internal static PhysicalPin Create(string path,string sha)
        {var file=new FileInfo(path);Require(HashFile(path)==sha,"Framework physical image SHA-256 changed: "+file.Name);return new(null,path,sha,Guid.Empty,file.Length,file.LastWriteTimeUtc);}
        internal void Validate(bool rehash)
        {
            var file=new FileInfo(Path);Require(file.Exists&&file.Length==Length&&file.LastWriteTimeUtc==Modified,"Pinned framework file metadata changed.");
            if(Assembly!=null)Require(Assembly.Location==Path&&Assembly.ManifestModule.ModuleVersionId==Mvid,"Loaded physical framework identity changed.");
            if(rehash)Require(HashFile(Path)==Sha,"Pinned framework image bytes changed before publication.");
        }
    }
    private static bool ReadSystemAssemblyFlag(Assembly assembly)
    {
        var field=typeof(object).Assembly.GetType("System.Reflection.RuntimeAssembly",true)!.GetField("m_assembly",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var start=(IntPtr)field.GetValue(assembly)!;
        IntPtr file=ReadPointer(IntPtr.Add(start,104));IntPtr pe=ReadPointer(IntPtr.Add(file,32));
        return (BitConverter.ToInt32(ReadMemory(IntPtr.Add(pe,68),4),0)&1)!=0;
    }
    private static IntPtr ReadPointer(IntPtr address){var value=new IntPtr(BitConverter.ToInt64(ReadMemory(address,8),0));Require(value!=IntPtr.Zero,"Pinned runtime native metadata pointer is null.");return value;}
    private static byte[] ReadMemory(IntPtr address,int count)
    {var bytes=new byte[count];Require(address!=IntPtr.Zero&&ReadProcessMemory(new IntPtr(-1),address,bytes,(UIntPtr)(uint)count,out var read)&&read==(UIntPtr)(uint)count,"Pinned runtime metadata is unreadable.");return bytes;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] buffer,UIntPtr size,out UIntPtr read);

    internal sealed record Template(string Hash,string Canonical,MethodBase[] Methods);
    internal static Template InspectTemplate(Assembly assembly,bool proxy,Type shim)
    {
        Require(!assembly.IsDynamic&&assembly.Location.Length==0&&assembly.EntryPoint==null&&assembly.GetModules().Length==1,"Generated framework assembly has an unsupported physical/dynamic/module shape.");
        string expectedName=proxy?ProxyName:HelperName;var name=assembly.GetName();
        Require(name.Name==expectedName&&name.Version==new Version(0,0,0,0)&&string.IsNullOrEmpty(name.CultureName)&&(name.GetPublicKeyToken()?.Length??0)==0,"Generated framework assembly identity changed.");
        Require(assembly.GetManifestResourceNames().Length==0&&assembly.GetForwardedTypes().Length==0,"Extra generated resources or forwarded types are unsupported.");
        var types=assembly.GetTypes();Require(types.Length==1,"Extra generated executable types are unsupported.");var type=types[0];
        Require(type.FullName==(proxy?ProxyName:"System.Type")&&type.Attributes==(proxy?TypeAttributes.Public:TypeAttributes.Public|TypeAttributes.Abstract)
            &&type.BaseType==(proxy?typeof(ILGenerator):typeof(object))&&type.GetNestedTypes(All).Length==0&&type.GetInterfaces().Length==0
            &&type.TypeInitializer==null&&type.GetProperties(All).Length==0&&type.GetEvents(All).Length==0&&type.GetCustomAttributesData().Count==0,"Generated type/base/initializer/member metadata changed.");
        var declared=type.GetMethods(All);var constructors=type.GetConstructors(All);var fields=type.GetFields(All);var guarded=new List<MethodBase>();
        var graph=new List<string>{PolicyId,expectedName};var usedReferences=new HashSet<int>();
        if(proxy)
        {
            Require(type.IsGenericTypeDefinition&&type.GetGenericArguments().Length==1,"Proxy generic arity changed.");var generic=type.GetGenericArguments()[0];
            Require(generic.Name=="TTarget"&&generic.GenericParameterAttributes==GenericParameterAttributes.None&&generic.GetGenericParameterConstraints().SequenceEqual(new[]{shim})&&generic.GetCustomAttributesData().Count==0,"Proxy generic target constraint changed.");
            Require(fields.Length==1&&fields[0].Name=="Target"&&fields[0].Attributes==FieldAttributes.Public&&fields[0].FieldType==generic&&fields[0].GetCustomAttributesData().Count==0,"Proxy target field changed.");
            Require(constructors.Length==1&&constructors[0].Attributes==(MethodAttributes)6278&&constructors[0].GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{generic}),"Proxy constructor shape changed.");
            CheckManagedBody(constructors[0]);var ctor=constructors[0].GetMethodBody()!.GetILAsByteArray()!;
            Require(ctor.Length==8&&ctor[0]==0x02&&ctor[1]==0x03&&ctor[2]==0x7d&&ctor[7]==0x2a,"Proxy constructor is not the fixed field store template.");
            CheckTargetField(constructors[0],BitConverter.ToInt32(ctor,3),fields[0],usedReferences);
            graph.Add("ctor|arg0|arg1|stfld:Target<!0>|ret");guarded.Add(constructors[0]);
            var expected=typeof(ILGenerator).GetMethods(BindingFlags.Public|BindingFlags.Instance).Select(m=>(Original:m,Target:shim.GetMethod(m.Name,m.GetParameters().Select(p=>p.ParameterType).ToArray())))
                .Where(x=>x.Target!=null).ToArray();Require(expected.Length==42&&declared.Length==expected.Length,"Proxy forwarding surface changed.");
            foreach(var pair in expected)
            {
                var method=declared.SingleOrDefault(m=>m.Name==pair.Original.Name&&m.ReturnType==pair.Original.ReturnType&&m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(pair.Original.GetParameters().Select(p=>p.ParameterType)));
                Require(method!=null&&method.Attributes==(MethodAttributes)198&&!method.IsGenericMethod,"Proxy forward signature/attributes changed.");CheckManagedBody(method!);
                var bytes=method!.GetMethodBody()!.GetILAsByteArray()!;int count=method.GetParameters().Length;
                Require(bytes.Length==12+4*count&&bytes[0]==0x02&&bytes[1]==0x7b&&bytes[^1]==0x2a,"Proxy forward contains additional or missing IL.");
                CheckTargetField(method,BitConverter.ToInt32(bytes,2),fields[0],usedReferences);int offset=6;
                for(int i=1;i<=count;i++,offset+=4)Require(bytes[offset]==0xfe&&bytes[offset+1]==0x09&&BitConverter.ToUInt16(bytes,offset+2)==i,"Proxy forward argument order changed.");
                Require(bytes[offset]==(pair.Target!.IsVirtual?0x6f:0x28),"Proxy dispatch kind changed.");int token=BitConverter.ToInt32(bytes,offset+1);usedReferences.Add(token);
                var actual=method.Module.ResolveMethod(token,type.GetGenericArguments(),null);
                Require(actual!=null&&actual.Module==pair.Target.Module&&actual.MetadataToken==pair.Target.MetadataToken&&actual.DeclaringType==pair.Target.DeclaringType,
                    "Proxy forward resolves to a different actual pinned Shim method: "+method.Name);
                graph.Add("forward|"+MethodKey(method,assembly)+"|Target|args="+count+"|"+(pair.Target.IsVirtual?"callvirt":"call")+"|"+MethodKey(pair.Target,assembly));
                guarded.Add(method);guarded.Add(pair.Target);
            }
        }
        else
        {
            Require(!type.IsGenericType&&fields.Length==0&&constructors.Length==0&&declared.Length==1,"Runtime helper contains extra state or methods.");var method=declared[0];
            Require(method.Name=="GetTypeFromHandleUnsafe"&&method.Attributes==(MethodAttributes)22&&method.GetMethodImplementationFlags()==MethodImplAttributes.InternalCall
                &&method.ReturnType==typeof(Type)&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(IntPtr)})&&method.GetMethodBody()==null,"Runtime helper is not the exact pinned internal-call declaration.");
            CheckParameters(method);graph.Add("internalcall|"+MethodKey(method,assembly));guarded.Add(method);
        }
        var attrs=assembly.GetCustomAttributesData();
        if(proxy)
        {
            var attribute=shim.Assembly.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",true)!;
            Require(attrs.Count==1&&attrs[0].Constructor==attribute.GetConstructor(new[]{typeof(string)})&&attrs[0].ConstructorArguments.Count==1
                &&Equals(attrs[0].ConstructorArguments[0].Value,"MonoMod.Utils")&&attrs[0].NamedArguments.Count==0,"Proxy access-check attribute changed.");
            guarded.Add(attrs[0].Constructor);
        }
        else Require(attrs.Count==0,"Runtime helper has unexpected assembly attributes.");
        Require(assembly.ManifestModule.GetMethods(All).Length==0&&assembly.ManifestModule.GetFields(All).Length==0&&assembly.ManifestModule.GetCustomAttributesData().Count==0,"Generated module contains extra code/state/attributes.");
        graph.Add(MetadataGraph(assembly,proxy,usedReferences));
        string canonical=string.Join("\n",graph.OrderBy(x=>x,StringComparer.Ordinal));return new(Hash(canonical),canonical,guarded.Distinct().ToArray());
    }
    private static void CheckManagedBody(MethodBase method)
    {
        Require(method.GetMethodImplementationFlags()==MethodImplAttributes.IL&&method.GetCustomAttributesData().Count==0,"Generated managed implementation attributes changed.");
        var body=method.GetMethodBody();Require(body!=null&&!body.InitLocals&&body.MaxStackSize==8&&body.LocalVariables.Count==0&&body.ExceptionHandlingClauses.Count==0,"Generated managed body metadata changed.");CheckParameters(method);
    }
    private static void CheckParameters(MethodBase method)
    {
        Require(method.GetParameters().All(p=>p.Attributes==ParameterAttributes.None&&p.Name==null&&p.MetadataToken==0x08000000&&p.RawDefaultValue==null&&p.GetCustomAttributesData().Count==0
            &&p.GetRequiredCustomModifiers().Length==0&&p.GetOptionalCustomModifiers().Length==0),"Generated parameter metadata changed.");
        if(method is MethodInfo info)Require(info.ReturnParameter.Attributes==ParameterAttributes.None&&info.ReturnParameter.GetCustomAttributesData().Count==0
            &&info.ReturnParameter.GetRequiredCustomModifiers().Length==0&&info.ReturnParameter.GetOptionalCustomModifiers().Length==0,"Generated return metadata changed.");
    }
    private static void CheckTargetField(MethodBase method,int token,FieldInfo expected,HashSet<int> references)
    {references.Add(token);var actual=method.Module.ResolveField(token,method.DeclaringType!.GetGenericArguments(),null);Require(actual!=null&&actual.Module==expected.Module&&actual.MetadataToken==expected.MetadataToken&&actual.FieldType.IsGenericParameter&&actual.FieldType.GenericParameterPosition==0,"Proxy field operand resolves to another field.");}

    private static unsafe string MetadataGraph(Assembly assembly,bool proxy,HashSet<int> usedReferences)
    {
        Require(assembly.TryGetRawMetadata(out byte* blob,out int length),"Generated loaded metadata is unavailable.");var reader=new MetadataReader(blob,length);
        var expected=new Dictionary<TableIndex,int>{{TableIndex.Module,1},{TableIndex.TypeRef,proxy?14:2},{TableIndex.TypeDef,2},{TableIndex.MethodDef,proxy?43:1},{TableIndex.Assembly,1},{TableIndex.AssemblyRef,proxy?3:2}};
        if(proxy){expected.Add(TableIndex.Field,1);expected.Add(TableIndex.MemberRef,44);expected.Add(TableIndex.CustomAttribute,1);expected.Add(TableIndex.TypeSpec,1);expected.Add(TableIndex.GenericParam,1);expected.Add(TableIndex.GenericParamConstraint,1);}
        foreach(var table in Enum.GetValues<TableIndex>())Require(reader.GetTableRowCount(table)==expected.GetValueOrDefault(table),"Generated metadata table contains changed/extra rows: "+table);
        var module=reader.GetModuleDefinition();Require(module.Generation==0&&module.GenerationId.IsNil&&module.BaseGenerationId.IsNil,"Generated edit-and-continue metadata is unsupported.");
        var definition=reader.GetAssemblyDefinition();var rows=new List<string>{"metadata-version="+reader.MetadataVersion,"module="+reader.GetString(module.Name),
            "assembly="+reader.GetString(definition.Name)+"|"+definition.Version+"|"+(int)definition.Flags+"|"+(int)definition.HashAlgorithm+"|"+reader.GetString(definition.Culture)+"|"+Convert.ToHexString(reader.GetBlobBytes(definition.PublicKey))};
        var signatures=new SignatureNames(assembly);
        foreach(var h in reader.AssemblyReferences)
        {
            var a=reader.GetAssemblyReference(h);string referenceName=reader.GetString(a.Name);var bytes=reader.GetBlobBytes(a.HashValue);string normalizedHash="";
            if(proxy&&referenceName is "MonoMod.Utils" or "System.Private.CoreLib")
            {
                var prefix=Encoding.UTF8.GetBytes("MonoModRefl\0");
                Require(bytes.Length==prefix.Length+4&&bytes.AsSpan(0,prefix.Length).SequenceEqual(prefix)
                    &&BitConverter.ToInt32(bytes,prefix.Length)==Loaded(referenceName).GetHashCode(),"MonoMod runtime assembly reference hash does not bind the actual pinned assembly object.");
                normalizedHash="actual-pinned-assembly="+referenceName+"|sha256="+Pins[referenceName];
            }
            else Require(bytes.Length==0,"Unexpected generated assembly-reference hash data.");
            rows.Add("assembly-ref|"+referenceName+"|"+a.Version+"|"+(int)a.Flags+"|"+reader.GetString(a.Culture)+"|"+Convert.ToHexString(reader.GetBlobBytes(a.PublicKeyOrToken))+"|"+normalizedHash);
        }
        foreach(var h in reader.TypeReferences)rows.Add("type-ref|"+TypeKey(assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(h)),assembly));
        foreach(var h in reader.MemberReferences)
        {
            var member=reader.GetMemberReference(h);int token=MetadataTokens.GetToken(h);
            string signature=member.GetKind()==MemberReferenceKind.Field?member.DecodeFieldSignature(signatures,null):SignatureKey(member.DecodeMethodSignature(signatures,null));
            rows.Add("member-ref|"+reader.GetString(member.Name)+"|"+signature+"|"+HandleType(reader,member.Parent,signatures));
        }
        foreach(var h in reader.CustomAttributes){var a=reader.GetCustomAttribute(h);usedReferences.Add(MetadataTokens.GetToken(a.Constructor));rows.Add("attribute|"+a.Parent.Kind+"|"+MethodKey((MethodBase)assembly.ManifestModule.ResolveMember(MetadataTokens.GetToken(a.Constructor))!,assembly)+"|"+Convert.ToHexString(reader.GetBlobBytes(a.Value)));}
        Require(reader.MemberReferences.Select(h=>MetadataTokens.GetToken(h)).ToHashSet().SetEquals(usedReferences),"Generated graph contains unused or hidden member references.");
        for(int i=1;i<=reader.GetTableRowCount(TableIndex.TypeSpec);i++)rows.Add("type-spec|"+reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(i)).DecodeSignature(signatures,null));
        foreach(var h in reader.TypeDefinitions)
        {
            var t=reader.GetTypeDefinition(h);rows.Add("type-def|"+reader.GetString(t.Namespace)+"."+reader.GetString(t.Name)+"|"+(int)t.Attributes+"|"+HandleType(reader,t.BaseType,signatures));
            if(reader.GetString(t.Name)=="<Module>")Require(t.GetMethods().Count==0&&t.GetFields().Count==0,"Generated module initializer/state exists.");
            foreach(var g in t.GetGenericParameters()){var p=reader.GetGenericParameter(g);rows.Add("generic|"+reader.GetString(p.Name)+"|"+p.Index+"|"+(int)p.Attributes+"|"+string.Join(",",p.GetConstraints().Select(c=>HandleType(reader,reader.GetGenericParameterConstraint(c).Type,signatures))));}
        }
        foreach(var h in reader.MethodDefinitions){var m=reader.GetMethodDefinition(h);rows.Add("method-def|"+reader.GetString(m.Name)+"|"+(int)m.Attributes+"|"+(int)m.ImplAttributes+"|"+SignatureKey(m.DecodeSignature(signatures,null)));}
        foreach(var h in reader.FieldDefinitions){var f=reader.GetFieldDefinition(h);rows.Add("field-def|"+reader.GetString(f.Name)+"|"+(int)f.Attributes+"|"+f.DecodeSignature(signatures,null));}
        return string.Join("\n",rows.OrderBy(s=>s,StringComparer.Ordinal));
    }
    private static string HandleType(MetadataReader reader,EntityHandle handle,SignatureNames names)=>handle.IsNil?"none":handle.Kind switch
    {HandleKind.TypeDefinition=>names.GetTypeFromDefinition(reader,(TypeDefinitionHandle)handle,0),HandleKind.TypeReference=>names.GetTypeFromReference(reader,(TypeReferenceHandle)handle,0),HandleKind.TypeSpecification=>names.GetTypeFromSpecification(reader,null,(TypeSpecificationHandle)handle,0),_=>throw new NotSupportedException("Unexpected generated signature parent: "+handle.Kind)};
    private static string SignatureKey(MethodSignature<string> signature)=>signature.Header.RawValue+"|g="+signature.GenericParameterCount+"|required="+signature.RequiredParameterCount+"|"+signature.ReturnType+"("+string.Join(",",signature.ParameterTypes)+")";
    private sealed class SignatureNames(Assembly assembly):ISignatureTypeProvider<string,object?>
    {
        public string GetArrayType(string e,ArrayShape s)=>"array["+e+";"+s.Rank+";"+string.Join(",",s.Sizes)+";"+string.Join(",",s.LowerBounds)+"]";
        public string GetByReferenceType(string e)=>"ref "+e;public string GetPointerType(string e)=>"ptr "+e;public string GetSZArrayType(string e)=>"sz "+e;
        public string GetFunctionPointerType(MethodSignature<string> s)=>"fn "+SignatureKey(s);
        public string GetGenericInstantiation(string g,ImmutableArray<string> a)=>g+"<"+string.Join(",",a)+">";
        public string GetGenericMethodParameter(object? c,int i)=>"!!"+i;public string GetGenericTypeParameter(object? c,int i)=>"!"+i;
        public string GetModifiedType(string m,string u,bool r)=>(r?"modreq ":"modopt ")+m+" "+u;public string GetPinnedType(string e)=>"pinned "+e;
        public string GetPrimitiveType(PrimitiveTypeCode p)=>"primitive:"+p;
        public string GetTypeFromDefinition(MetadataReader r,TypeDefinitionHandle h,byte k)=>"self::"+r.GetString(r.GetTypeDefinition(h).Namespace)+"."+r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r,TypeReferenceHandle h,byte k)=>TypeKey(assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(h)),assembly);
        public string GetTypeFromSpecification(MetadataReader r,object? c,TypeSpecificationHandle h,byte k)=>r.GetTypeSpecification(h).DecodeSignature(this,c);
    }
    private static string TypeKey(Type type,Assembly generated)
    {
        if(type.IsGenericParameter)return(type.DeclaringMethod==null?"!":"!!")+type.GenericParameterPosition;
        if(type.IsArray)return TypeKey(type.GetElementType()!,generated)+"["+new string(',',type.GetArrayRank()-1)+"]";
        if(type.IsByRef)return TypeKey(type.GetElementType()!,generated)+"&";if(type.IsPointer)return TypeKey(type.GetElementType()!,generated)+"*";
        string prefix=ReferenceEquals(type.Assembly,generated)?"self":AssemblySimpleNameCache.Get(type.Assembly)!;
        return prefix+"::"+(type.IsGenericType?type.GetGenericTypeDefinition().FullName+"<"+string.Join(",",type.GetGenericArguments().Select(t=>TypeKey(t,generated)))+">":type.FullName);
    }
    private static string MethodKey(MethodBase method,Assembly generated)=>TypeKey(method.DeclaringType!,generated)+"::"+method.Name+"("+string.Join(",",method.GetParameters().Select(p=>TypeKey(p.ParameterType,generated)))+")"+(method is MethodInfo m?"->"+TypeKey(m.ReturnType,generated):"");
    private static Assembly Loaded(string name)=>AppDomain.CurrentDomain.GetAssemblies().Single(a=>AssemblySimpleNameCache.Get(a)==name);
    private static IEnumerable<Type> VerifierTypes(Type type)
    {yield return type;foreach(var nested in type.GetNestedTypes(All))foreach(var item in VerifierTypes(nested))yield return item;}
    private static (Module,int) Key(MethodBase method)=>(method.Module,method.MetadataToken);
    private static void EnsureUnpatched(MethodBase method)
    {var p=Harmony.GetPatchInfo(method);Require(p==null||!p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers).Concat(p.ILManipulators).Any(),"Protected framework method has a Harmony patch: "+method.DeclaringType?.FullName+"."+method.Name);}
    private static string HashFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
