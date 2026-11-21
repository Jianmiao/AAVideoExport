using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace AAVideoExport.Plugin;

internal sealed partial class FrameworkGeneratedAssemblyScope
{
    // This grammar certifies declaration glue, never a delegate's invocation
    // target or a native trampoline body. Cache objects, not names, supply identity.
    private const string AdditionalGrammar="actual-framework-caches/runtime-delegate-declarations+sizeof-method-v1";
    private static readonly List<MethodBase> GeneratedProducers=new();
    [ThreadStatic] private static int generatedProducerDepth;
    private void RefreshAdditional(bool fullInspection=false)
    {
        var methods=additional.Refresh(fullInspection);
        foreach(var method in methods)
        {
            Require(!MutationHistory.Contains(Key(method)),"Additional framework declaration has protected mutation history.");
            EnsureUnpatched(method);guarded.Add(method);KnownMethods.Add(Key(method));
        }
        foreach(var assembly in additional.Assemblies)
        {
            Require(KnownGeneratedAssemblies.Count<4096||KnownGeneratedAssemblies.Contains(assembly),"Framework generated assembly history capacity overflow.");
            KnownGeneratedAssemblies.Add(assembly);
        }
    }
    private static void InstallGeneratedProducerMonitors(Harmony harmony)
    {
        var factory=typeof(Harmony).Assembly.GetType("HarmonyLib.DelegateTypeFactory",true)!;
        var support=Loaded("Il2CppInterop.Runtime").GetType("Il2CppInterop.Runtime.DelegateSupport",true)!;
        var extension=Loaded("MonoMod.Utils").GetType("MonoMod.Utils.Extensions",true)!;
        GeneratedProducers.AddRange(factory.GetMethods(All).Where(m=>m.Name=="CreateDelegateType"&&m.GetParameters().Last().ParameterType==typeof(CallingConvention?)));
        GeneratedProducers.AddRange(support.GetMethods(All).Where(m=>m.Name is "GetOrCreateDelegateType" or "CreateDelegateType"));
        GeneratedProducers.Add(extension.GetMethod("GetManagedSize",All)!);
        Require(GeneratedProducers.Count==5,"Inspected framework producer surface changed.");
        var flags=BindingFlags.Static|BindingFlags.NonPublic;
        foreach(var method in GeneratedProducers)
        {
            EnsureUnpatched(method);
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(FrameworkGeneratedAssemblyScope).GetMethod(nameof(BeforeGeneratedProducer),flags)),
                finalizer:new HarmonyMethod(typeof(FrameworkGeneratedAssemblyScope).GetMethod(nameof(AfterGeneratedProducer),flags)));
        }
    }
    private static void ValidateGeneratedProducerMonitors()
    {
        Require(GeneratedProducers.Count==5,"Framework producer observation is incomplete.");
        foreach(var method in GeneratedProducers)
        {
            Require(!MutationHistory.Contains(Key(method)),"Framework producer mutation history cannot be reset.");
            var info=Harmony.GetPatchInfo(method)??throw new InvalidOperationException("Framework producer monitor disappeared.");
            bool Exact(IEnumerable<Patch> patches,string name)=>patches.Count()==1&&patches.Single().owner==Owner
                &&patches.Single().PatchMethod==typeof(FrameworkGeneratedAssemblyScope).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
            Require(Exact(info.Prefixes,nameof(BeforeGeneratedProducer))&&Exact(info.Finalizers,nameof(AfterGeneratedProducer))
                &&!info.Postfixes.Any()&&!info.Transpilers.Any()&&!info.ILManipulators.Any(),"Framework producer monitor owner/category changed.");
        }
    }
    private static void BeforeGeneratedProducer(out GeneratedProducerLease? __state)
    {
        __state=null;Monitor.Enter(HarmonyGate);
        try
        {
            lock(Gate)foreach(var weak in Scopes)
                if(weak.TryGetTarget(out var scope)&&!scope.disposed&&!scope.consumed&&scope.commitThread==Environment.CurrentManagedThreadId)
                {scope.Latch("Publication worker attempted new framework generation.");throw new InvalidOperationException(scope.failure);}
            generatedProducerDepth++;__state=new GeneratedProducerLease();
        }
        catch{Monitor.Exit(HarmonyGate);throw;}
    }
    private static Exception? AfterGeneratedProducer(Exception? __exception,GeneratedProducerLease? __state)
    {__state?.Dispose();return __exception;}
    private sealed class GeneratedProducerLease:IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try
            {
                if(--generatedProducerDepth==0)lock(Gate)foreach(var weak in Scopes)
                    if(weak.TryGetTarget(out var scope)&&!scope.disposed&&!scope.consumed)
                        try{scope.RefreshAdditional();}catch(Exception error){scope.Latch(error.Message);}
            }
            finally{Monitor.Exit(HarmonyGate);}
        }
    }
    private sealed class AdditionalProof
    {
        private readonly Dictionary<Type,string> declarations=new();
        private readonly Dictionary<Type,HarmonyMetadataReceipt> harmonyMetadata=new();
        private int harmonyMetadataBytes;
        private readonly Dictionary<Type,(MethodInfo Method,object Entry)> harmonyBindings=new();
        private readonly Dictionary<object,Type> trampolineBindings=new(ReferenceEqualityComparer.Instance);
        private object? harmonyCache,trampolineCache,trampolineAssembly,trampolineModule;
        private MethodInfo? sizeHelper;
        internal readonly HashSet<Assembly> Assemblies=new();
        internal MethodBase[] Refresh(bool fullInspection=false)
        {
            var methods=new List<MethodBase>();var seen=new HashSet<Type>();
            var factory=typeof(Harmony).Assembly.GetType("HarmonyLib.DelegateTypeFactory",true)!;
            var cache=(IDictionary)factory.GetField("TypeCache",All)!.GetValue(null)!;
            Require(harmonyCache==null||ReferenceEquals(harmonyCache,cache),"Actual Harmony delegate cache was replaced.");harmonyCache=cache;
            Require(cache.Count<=4096,"Harmony delegate cache exceeds bounded proof capacity.");
            foreach(var key in cache.Keys.Cast<object>().ToArray())
            {
                Require(key is MethodInfo,"Harmony delegate cache contains an unsupported key.");var source=(MethodInfo)key;
                var entries=(IList)cache[key]!;Require(entries.Count<=16,"Harmony delegate convention list exceeds capacity.");
                foreach(var entry in entries)
                {
                    var type=(Type)entry!.GetType().GetField("delegateType",All)!.GetValue(entry)!;
                    object? convention=entry.GetType().GetField("callingConvention",All)!.GetValue(entry);
                    Require(!harmonyBindings.TryGetValue(type,out var old)||ReferenceEquals(old.Method,source)&&ReferenceEquals(old.Entry,entry),"Harmony delegate cache binding changed.");
                    harmonyBindings[type]=(source,entry);
                    var invoke=type.GetMethod("Invoke",All)!;
                    Require(invoke.ReturnType==source.ReturnType&&invoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(source.GetParameters().Select(p=>p.ParameterType)),"Harmony cached delegate signature differs from its actual source method.");
                    Inspect(type,false,convention==null?null:(CallingConvention)convention,seen,methods,fullInspection);
                }
            }
            Require(harmonyBindings.Keys.All(seen.Contains),"A retained Harmony cached delegate was removed.");
            var support=Loaded("Il2CppInterop.Runtime").GetType("Il2CppInterop.Runtime.DelegateSupport",true)!;
            // Do not manufacture this optional native cache merely to admit a
            // name. Only inspect it if its generated assembly already exists.
            var before=AppDomain.CurrentDomain.GetAssemblies();
            if(trampolineAssembly!=null||before.Any(a=>AssemblySimpleNameCache.Get(a)=="Il2CppTrampolineDelegates"))
            {
                var assembly=support.GetField("AssemblyBuilder",All)!.GetValue(null)!;
                var module=support.GetField("ModuleBuilder",All)!.GetValue(null)!;
                var map=support.GetField("ourDelegateTypes",All)!.GetValue(null)!;
                Require(trampolineAssembly==null||ReferenceEquals(trampolineAssembly,assembly)&&ReferenceEquals(trampolineModule,module)&&ReferenceEquals(trampolineCache,map),"Actual Il2Cpp delegate builder/cache identity changed.");
                var builder=(AssemblyBuilder)assembly;var moduleBuilder=(ModuleBuilder)module;
                Require(before.Any(a=>a.ManifestModule==builder.ManifestModule)&&moduleBuilder.Assembly==builder,"Il2Cpp delegate builder was not an existing actual assembly/module.");
                trampolineAssembly=assembly;trampolineModule=module;trampolineCache=map;
                var pairs=((IEnumerable)map).Cast<object>().ToArray();Require(pairs.Length<=4096,"Il2Cpp delegate cache exceeds bounded proof capacity.");
                var current=new HashSet<Type>();
                foreach(var pair in pairs)
                {
                    var key=pair.GetType().GetProperty("Key")!.GetValue(pair)!;var type=(Type)pair.GetType().GetProperty("Value")!.GetValue(pair)!;
                    Require(!trampolineBindings.TryGetValue(key,out var old)||ReferenceEquals(old,type),"Il2Cpp cached delegate reference changed.");trampolineBindings[key]=type;
                    Require(type.Module==builder.ManifestModule&&type.Module==moduleBuilder,"Il2Cpp cached delegate belongs to another generated module.");
                    current.Add(type);Inspect(type,true,CallingConvention.Cdecl,seen,methods);
                }
                Require(trampolineBindings.All(p=>pairs.Any(x=>ReferenceEquals(x.GetType().GetProperty("Key")!.GetValue(x),p.Key))),"A retained Il2Cpp signature cache entry was removed.");
                Require(builder.GetTypes().ToHashSet().SetEquals(current),"Actual Il2Cpp generated assembly contains an uncached type.");
                InspectModule(builder);Assemblies.Add(builder);
            }
            var extensions=Loaded("MonoMod.Utils").GetType("MonoMod.Utils.Extensions",true)!;
            var helper=extensions.GetField("_GetManagedSizeHelper",All)!.GetValue(null) as MethodInfo;
            Require(sizeHelper==null||ReferenceEquals(sizeHelper,helper),"Actual managed-size helper cache identity changed.");
            if(helper!=null)
            {
                sizeHelper=helper;var type=helper.DeclaringType!;seen.Add(type);
                string graph=InspectSizeHelper(helper);Remember(type,graph);methods.Add(helper);Assemblies.Add(type.Assembly);
            }
            Require(declarations.Keys.All(seen.Contains),"A retained framework declaration disappeared from its producer cache.");
            return methods.Distinct().ToArray();
        }
        private void Inspect(Type type,bool trampoline,CallingConvention? convention,HashSet<Type> seen,List<MethodBase> methods,bool fullInspection=false)
        {
            Require(seen.Add(type),"Duplicate actual framework delegate cache identity.");
            string graph;
            if(!trampoline&&harmonyMetadata.TryGetValue(type,out var receipt))
            {
                bool current=receipt.ValidateCurrent(type,convention);
                if(fullInspection||!current)
                {
                    graph=InspectDelegate(type,false,convention,methods);
                    Require(graph==receipt.Graph,"Retained Harmony declaration semantic proof changed.");
                }
                else{graph=receipt.Graph;receipt.AppendMethods(methods);}
            }
            else if(!trampoline)
            {
                // Snapshot before the complete original semantic proof, then
                // compare fresh bytes afterward. Never bless a later snapshot.
                var before=HarmonyMetadataReceipt.CaptureBefore(type,harmonyMetadataBytes);
                int first=methods.Count;graph=InspectDelegate(type,false,convention,methods);
                if(before!=null)
                {
                    var created=new HarmonyMetadataReceipt(type,convention,before,graph,methods.Skip(first).ToArray());
                    if(created.ValidateCurrent(type,convention))
                    {harmonyMetadata.Add(type,created);harmonyMetadataBytes+=before.Length;}
                }
            }
            else graph=InspectDelegate(type,true,convention,methods);
            Remember(type,graph);Assemblies.Add(type.Assembly);
        }
        private void Remember(Type type,string graph)
        {Require(!declarations.TryGetValue(type,out var old)||old==graph,"Retained framework declaration metadata changed.");declarations[type]=graph;}
    }
    private static string InspectDelegate(Type type,bool trampoline,CallingConvention? convention,List<MethodBase> guarded)
    {
        var assembly=type.Assembly;Require(assembly.EntryPoint==null&&assembly.GetModules().Length==1,"Delegate assembly module/entry point changed.");
        if(!trampoline)Require(!assembly.IsDynamic&&assembly.Location.Length==0&&assembly.GetTypes().SequenceEqual(new[]{type}),"Harmony cached delegate assembly has extra code/types.");
        Require(type.Attributes==(TypeAttributes.Public|TypeAttributes.Sealed)&&type.BaseType==typeof(MulticastDelegate)&&!type.IsGenericType
            &&type.GetNestedTypes(All).Length==0&&type.GetInterfaces().ToHashSet().SetEquals(typeof(MulticastDelegate).GetInterfaces())&&type.TypeInitializer==null
            &&type.GetFields(All).Length==0&&type.GetProperties(All).Length==0&&type.GetEvents(All).Length==0,"Framework delegate has additional executable state or metadata.");
        var constructors=type.GetConstructors(All);var methods=type.GetMethods(All);
        Require(constructors.Length==1&&methods.Length==(trampoline?3:1),"Framework delegate method surface changed.");
        var ctor=constructors[0];Require(ctor.Attributes==(trampoline?(MethodAttributes)6278:(MethodAttributes)4230)
            &&ctor.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(object),typeof(IntPtr)}),"Framework delegate constructor changed.");
        var invoke=methods.Single(m=>m.Name=="Invoke");
        var expected=(MethodAttributes)(trampoline?454:198);
        foreach(var method in methods)Require(method.Attributes==expected,"Framework delegate invocation attributes changed.");
        if(trampoline)
        {
            var begin=methods.Single(m=>m.Name=="BeginInvoke");var end=methods.Single(m=>m.Name=="EndInvoke");
            Require(begin.ReturnType==typeof(IAsyncResult)&&begin.GetParameters().Select(p=>p.ParameterType).SequenceEqual(invoke.GetParameters().Select(p=>p.ParameterType).Concat(new[]{typeof(AsyncCallback),typeof(object)}))
                &&end.ReturnType==invoke.ReturnType&&end.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(IAsyncResult)}),"Il2Cpp delegate asynchronous signature changed.");
        }
        foreach(var method in methods.Cast<MethodBase>().Append(ctor))
        {
            Require(method.GetMethodImplementationFlags()==MethodImplAttributes.Runtime&&method.GetMethodBody()==null&&!method.IsGenericMethod
                &&method.GetCustomAttributesData().Count==0,"Framework delegate contains an executable managed/native body or attributes.");
            CheckParameters(method);guarded.Add(method);
        }
        var attrs=type.GetCustomAttributesData();
        Require(attrs.Count==(convention.HasValue?1:0),"Framework delegate calling convention attributes changed.");
        if(convention.HasValue)Require(attrs[0].Constructor==typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[]{typeof(CallingConvention)})
            &&attrs[0].ConstructorArguments.Count==1&&(int)attrs[0].ConstructorArguments[0].Value! == (int)convention.Value&&attrs[0].NamedArguments.Count==0,"Framework delegate calling convention differs from its actual cache.");
        InspectModule(assembly);
        return "runtime-delegate|"+trampoline+"|"+convention+"|"+TypeKey(invoke.ReturnType,assembly)+"("+string.Join(",",invoke.GetParameters().Select(p=>TypeKey(p.ParameterType,assembly)))+")";
    }
    private static string InspectSizeHelper(MethodInfo method)
    {
        var type=method.DeclaringType!;var assembly=type.Assembly;
        Require(!assembly.IsDynamic&&assembly.Location.Length==0&&assembly.GetTypes().SequenceEqual(new[]{type})&&type.Attributes==(TypeAttributes.Public|TypeAttributes.Abstract|TypeAttributes.Sealed)
            &&type.BaseType==typeof(object)&&!type.IsGenericType&&type.TypeInitializer==null&&type.GetNestedTypes(All).Length==0&&type.GetFields(All).Length==0
            &&type.GetConstructors(All).Length==0&&type.GetProperties(All).Length==0&&type.GetEvents(All).Length==0&&type.GetInterfaces().Length==0&&type.GetCustomAttributesData().Count==0
            &&type.GetMethods(All).SequenceEqual(new[]{method}),"Cached managed-size helper has extra executable state or metadata.");
        Require(method.Attributes==(MethodAttributes)150&&method.ReturnType==typeof(int)&&method.GetParameters().Length==0&&method.IsGenericMethodDefinition
            &&method.GetGenericArguments().Length==1,"Cached managed-size helper signature changed.");
        var generic=method.GetGenericArguments()[0];Require(generic.GenericParameterAttributes==GenericParameterAttributes.None&&generic.GetGenericParameterConstraints().Length==0&&generic.GetCustomAttributesData().Count==0,"Managed-size generic constraints changed.");
        CheckManagedBody(method);var il=method.GetMethodBody()!.GetILAsByteArray()!;
        Require(il.Length==7&&il[0]==0xfe&&il[1]==0x1c&&il[6]==0x2a&&method.Module.ResolveType(BitConverter.ToInt32(il,2),null,new[]{generic})==generic,"Cached managed-size helper is not exactly sizeof<T>; ret.");
        InspectModule(assembly);return "sizeof<!!0>;ret";
    }
    private static void InspectModule(Assembly assembly)
    {
        Require(assembly.GetCustomAttributesData().Count==0,"Additional framework assembly has attributes.");
        // The pinned .NET 6 Run-only AssemblyBuilder does not expose resource
        // enumeration or resource emission. Physical Cecil images do expose it.
        if(!assembly.IsDynamic)Require(assembly.GetManifestResourceNames().Length==0,"Additional framework assembly has resources.");
        var module=assembly.ManifestModule;
        Require(module.GetMethods(All).Length==0&&module.GetFields(All).Length==0&&module.GetCustomAttributesData().Count==0,"Additional generated module contains global executable code or state.");
    }
}
