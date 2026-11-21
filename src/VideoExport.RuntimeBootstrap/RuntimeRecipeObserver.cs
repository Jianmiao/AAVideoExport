using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace AAVideoExport.RuntimeBootstrap;

internal sealed record RecipeInstruction(string Opcode, string OperandKind, string OperandIdentity);
internal sealed record RecipeView(long Id,string Name,string Status,string Reason,string ReturnType,IReadOnlyList<string> Parameters,
    IReadOnlyList<RecipeInstruction> Instructions,string RawIlSha256,InteropRecipeProof? Proof,bool ValidatedBeforeMaterialization=false,string EvidenceKind=InteropRecipePolicy.EvidenceKind);
internal sealed record RecipeScopeView(string Schema,string Token,bool Valid,string Reason,string Policy,string PolicySha256,string RecipesSha256,bool KnownHostBaseline,bool Sealed,
    bool PreexistingCoverage,bool AllCodeCoverage,bool PermitsStrictExport,IReadOnlyList<RecipeView> Recipes,string EvidenceKind=InteropRecipePolicy.EvidenceKind);

internal sealed class RuntimeRecipeObserver : IDisposable
{
    private const string Owner="com.aavideoexport.runtime-recipes.v2";
    private static RuntimeRecipeObserver? active;
    private static readonly object InstallationGate=new();
    // Exact Harmony 2.10.2 Patch/Unpatch take this mutex. Always acquire it
    // before our gate, including callbacks invoked while Harmony emits code.
    private static readonly object HarmonyGate=ResolveHarmonyGate();
    // Protect the fixed emitter surface against foreign patches, but never
    // detour CoreCLR's ILGenerator/DynamicILGenerator implementation itself.
    private static readonly HashSet<MethodBase> ProtectedEmissionMethods=ResolveProtectedEmissionMethods();
    private static readonly Dictionary<MethodBase,(MethodInfo? Prefix,MethodInfo? Postfix,MethodInfo? Finalizer)> PermanentHooks=new();
    private static string permanentInstallationFailure="";
    [ThreadStatic] private static bool inCallback;
    private readonly object gate=new();
    private readonly InteropRecipePolicy policy;
    private readonly Harmony harmony=new(Owner);
    private readonly ConditionalWeakTable<DynamicMethod,Record> methods=new();
    private readonly ConditionalWeakTable<Delegate,Record> delegates=new();
    private readonly List<Record> records=new();
    private readonly Dictionary<string,ScopeState> scopes=new(StringComparer.Ordinal);
    private readonly HashSet<string> mutatedMembers=new(StringComparer.Ordinal);
    private readonly Dictionary<MethodBase,(MethodInfo? Prefix,MethodInfo? Postfix,MethodInfo? Finalizer)> hooks=new();
    private readonly int maximumRecords,maximumInstructions;
    private string failure="";
    private bool installed,disposed;
    private long nextId,generation;private int delegateCount;
    private int committingThread;
    private string committingToken="";
    internal string PolicyId=>policy.PolicyId;
    internal string PolicySha256 {get;}
    internal bool IsInstalled=>installed&&!disposed;
    internal string Failure=>failure;
    internal RuntimeRecipeObserver(InteropRecipePolicy policy,int maximumRecords=2048,int maximumInstructions=16)
    { InteropRecipePolicy.Require(maximumRecords>0&&maximumRecords<=8192&&maximumInstructions>0&&maximumInstructions<=64,"Invalid bounded recipe limits.");this.policy=policy;this.maximumRecords=maximumRecords;this.maximumInstructions=maximumInstructions;
        PolicySha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policy.PolicySha256+"|final-il-pre-materialization-v2|"+maximumRecords+"|"+maximumInstructions))); }

    internal void Install()
    {
        StartupTrace.Write("v2.Install.enter");
        lock(HarmonyGate)lock(InstallationGate)lock(gate)
        {
            try
            {
                InteropRecipePolicy.Require(active==null,"Another recipe observer is active.");
                InteropRecipePolicy.Require(permanentInstallationFailure.Length==0,"Earlier process-lifetime hook installation failed: "+permanentInstallationFailure);
                active=this;
                if(PermanentHooks.Count!=0)
                {
                    foreach(var entry in PermanentHooks)hooks.Add(entry.Key,entry.Value);
                    installed=true;ValidateHooks();StartupTrace.Write("v2.Install.complete","reused");return;
                }
                foreach(var c in typeof(DynamicMethod).GetConstructors())Patch(c,Callback(nameof(BeforeConstruction)),Callback(nameof(Constructed)),Callback(nameof(AfterConstruction)));
                foreach(var m in typeof(DynamicMethod).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                {
                    if(m.Name==nameof(DynamicMethod.CreateDelegate)&&!m.IsGenericMethod)Patch(m,Callback(nameof(BeforeMaterialization)),Callback(nameof(DelegateReturned)),Callback(nameof(AfterMaterialization)));
                    else if(m.Name==nameof(DynamicMethod.Invoke))Patch(m,Callback(nameof(BeforeInvoke)),null,Callback(nameof(AfterInvoke)));
                }
                foreach(var m in typeof(PatchProcessor).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name is "Patch" or "Unpatch"))
                    Patch(m,Callback(nameof(BeforePatchMutation)),null,null);
                if(policy.KnownHostBaseline)
                    InteropRecipePolicy.Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>IsAnonymous(a.FullName??"")),"Anonymous holder existed before complete v2 observation.");
                installed=true;ValidateHooks();
                StartupTrace.Write("v2.Install.complete","new");
            }
            catch(Exception error)
            {
                StartupTrace.Write("v2.Install.error",error.GetType().FullName??"Exception");
                failure=error.GetType().Name+": "+error.Message;installed=false;
                // Partial boundary hooks stay inert after any installation
                // failure. No retained receipt may be issued from them.
                permanentInstallationFailure=failure;
                if(ReferenceEquals(active,this))active=null;
            }
        }
    }
    private static bool IsMutation(string name)=>name.StartsWith("Emit",StringComparison.Ordinal)||name.StartsWith("Begin",StringComparison.Ordinal)
        ||name.StartsWith("End",StringComparison.Ordinal)||name is "DeclareLocal" or "MarkLabel" or "DefineLabel" or "UsingNamespace" or "ThrowException";
    private static HashSet<MethodBase> ResolveProtectedEmissionMethods()
    {
        var dynamicGenerator=typeof(DynamicMethod).Assembly.GetType("System.Reflection.Emit.DynamicILGenerator",true)!;
        var mutators=new[]{typeof(ILGenerator),dynamicGenerator}.SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            .Where(m=>m.GetMethodBody()!=null&&IsMutation(m.Name)).ToArray();
        InteropRecipePolicy.Require(mutators.Length==52,"The inspected .NET6 emission surface changed: "+mutators.Length);
        return new(mutators.Cast<MethodBase>().Concat(typeof(DynamicMethod).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)
            .Where(m=>m.Name is nameof(DynamicMethod.GetILGenerator) or nameof(DynamicMethod.GetDynamicILInfo))));
    }
    private static bool IsAnonymous(string name)=>name.StartsWith("Anonymously Hosted DynamicMethods Assembly,",StringComparison.Ordinal);
    private static MethodInfo Callback(string name)=>typeof(RuntimeRecipeObserver).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!;
    private static object ResolveHarmonyGate()
    {
        InteropRecipePolicy.Require(InteropRecipePolicy.HashFile(typeof(Harmony).Assembly.Location)=="6C898933B52149E8BCF6722305C7E4D94ADD47C9E53B364096F91721624C0877","Uninspected Harmony commit mutex layout.");
        return typeof(PatchProcessor).GetField("locker",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new NotSupportedException("Inspected Harmony patch mutex is unavailable.");
    }
    private void Patch(MethodBase method,MethodInfo? prefix,MethodInfo? postfix,MethodInfo? finalizer)
    {
        StartupTrace.WriteTarget("v2.Harmony.Patch.before",method);
        var existing=Harmony.GetPatchInfo(method);
        if(existing!=null)foreach(var patch in existing.Prefixes.Concat(existing.Postfixes).Concat(existing.Transpilers).Concat(existing.Finalizers).Concat(existing.ILManipulators))
            InteropRecipePolicy.Require(IsV1(patch,method),"Preexisting foreign patch on emitter: "+method);
        harmony.Patch(method,prefix:prefix==null?null:new HarmonyMethod(prefix),postfix:postfix==null?null:new HarmonyMethod(postfix),finalizer:finalizer==null?null:new HarmonyMethod(finalizer));
        StartupTrace.WriteTarget("v2.Harmony.Patch.after",method);
        hooks.Add(method,(prefix,postfix,finalizer));
        PermanentHooks.Add(method,(prefix,postfix,finalizer));
    }
    private static bool IsV1(Patch p,MethodBase target)=>p.owner=="com.aavideoexport.runtime-attribution.v1"&&p.PatchMethod.DeclaringType==typeof(RuntimeObserver)
        &&target.DeclaringType==typeof(DynamicMethod)&&(target.IsConstructor&&p.PatchMethod.Name=="ConstructorPostfix"
        ||target.Name=="CreateDelegate"&&p.PatchMethod.Name=="CreateDelegatePostfix"||target.Name=="Invoke"&&p.PatchMethod.Name=="InvokePrefix");
    private void ValidateHooks()
    {
        foreach(var method in ProtectedEmissionMethods)InteropRecipePolicy.EnsureUnpatched(method);
        foreach(var entry in hooks)
        {
            var p=Harmony.GetPatchInfo(entry.Key)??throw new InvalidOperationException("Recipe hook disappeared.");
            CheckKind(p.Prefixes,entry.Value.Prefix,entry.Key);CheckKind(p.Postfixes,entry.Value.Postfix,entry.Key);CheckKind(p.Finalizers,entry.Value.Finalizer,entry.Key);
            InteropRecipePolicy.Require(!p.Transpilers.Any()&&!p.ILManipulators.Any(),"Foreign emitter rewrite detected.");
        }
        void CheckKind(IEnumerable<Patch> patches,MethodInfo? expected,MethodBase target)
        {
            var own=patches.Where(p=>p.owner==Owner).ToArray();
            InteropRecipePolicy.Require(own.Length==(expected==null?0:1)&&own.All(p=>p.PatchMethod==expected)&&patches.All(p=>p.owner==Owner||IsV1(p,target)),"Emitter hook set/owner changed.");
            if(expected!=null)InteropRecipePolicy.EnsureUnpatched(expected);
        }
    }
    private static void Observe(Action<RuntimeRecipeObserver> action)
    {
        var observer=active;if(observer==null||!observer.IsInstalled)return;
        if(inCallback){lock(HarmonyGate)lock(observer.gate)observer.Fail("Reentrant recipe callback lost observation.");return;}
        inCallback=true;
        try{lock(HarmonyGate)lock(observer.gate)action(observer);}
        catch(Exception error){lock(HarmonyGate)lock(observer.gate)observer.Fail("Recipe callback failed: "+error.GetType().Name+": "+error.Message);}
        finally{inCallback=false;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Constructed(DynamicMethod __instance)=>Observe(o=>o.OnConstructed(__instance));
    private static void BeforeConstruction(MethodBase __originalMethod,out OperationLease? __state)
    {
        var p=__originalMethod.GetParameters();
        bool anonymous=p.Count(x=>x.ParameterType==typeof(Type))==1&&!p.Any(x=>x.ParameterType==typeof(Module));
        __state=anonymous?BeginOperation("anonymous construction"):null;
    }
    private static Exception? AfterConstruction(Exception? __exception,OperationLease? __state)
    {
        if(__state!=null){try{if(__exception!=null)__state.Owner.Fail("Anonymous construction threw before complete observation.");}finally{__state.Dispose();}}
        return __exception;
    }
    private void OnConstructed(DynamicMethod method)
    {
        if(!IsAnonymous(method.Module.Assembly.FullName??""))return;
        if(methods.TryGetValue(method,out _))return;
        if(records.Count>=maximumRecords){Fail("Recipe record overflow.");return;}
        var record=new Record(++nextId,method,this);records.Add(record);methods.Add(method,record);generation++;
        foreach(var scope in scopes.Values.Where(s=>s.Sealed))if(scope.Error.Length==0)scope.Error="Anonymous generation began after scope sealing.";
        MethodBase? factory=null;
        foreach(var frame in new StackTrace(false).GetFrames()??Array.Empty<StackFrame>())
        {
            var m=frame.GetMethod();if(m!=null&&oToken(m,policy.Factory)){factory=m;break;}
        }
        if(factory==null||!policy.IsFactory(factory)){record.Error="Anonymous creation is not the inspected factory.";return;}
        record.FactoryObserved=true;
        static bool oToken(MethodBase a,MethodBase b){try{return a.Module==b.Module&&a.MetadataToken==b.MetadataToken;}catch{return false;}}
    }
    private static OperationLease? BeginOperation(string operation)
    {
        var observer=active;if(observer==null||!observer.IsInstalled)return null;
        System.Threading.Monitor.Enter(HarmonyGate);
        System.Threading.Monitor.Enter(observer.gate);
        try
        {
            if(!ReferenceEquals(active,observer)||!observer.IsInstalled)
            {System.Threading.Monitor.Exit(observer.gate);System.Threading.Monitor.Exit(HarmonyGate);return null;}
            if(observer.committingThread==Environment.CurrentManagedThreadId)
            {
                observer.Fail("Submission worker attempted "+operation+" inside its sealed publication guard.");
                throw new InvalidOperationException(observer.failure);
            }
            return new OperationLease(observer,Environment.CurrentManagedThreadId);
        }
        catch{System.Threading.Monitor.Exit(observer.gate);System.Threading.Monitor.Exit(HarmonyGate);throw;}
    }
    private sealed class OperationLease : IDisposable
    {
        internal RuntimeRecipeObserver Owner {get;}
        private readonly int thread;private bool disposed;
        internal OperationLease(RuntimeRecipeObserver owner,int thread)=>(Owner,this.thread)=(owner,thread);
        public void Dispose()
        {
            if(disposed)return;
            if(thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException("Synchronous emission lease changed threads.");
            disposed=true;System.Threading.Monitor.Exit(Owner.gate);System.Threading.Monitor.Exit(HarmonyGate);
        }
    }
    private static void BeforeInvoke(DynamicMethod __instance,out OperationLease? __state)
    {
        __state=IsAnonymous(__instance.Module.Assembly.FullName??"")?BeginOperation("DynamicMethod.Invoke"):null;
        if(__state==null)return;
        Observe(o=>
        {
            if(!o.methods.TryGetValue(__instance,out var record)){o.Fail("Public Invoke lacks an observed factory recipe.");return;}
            try{o.ValidateRecord(record);}catch(Exception error){record.Error="Invoke before complete recipe qualification: "+error.Message;}
        });
    }
    private static Exception? AfterInvoke(Exception? __exception,OperationLease? __state)
    {
        if(__state!=null)try{if(__exception!=null)__state.Owner.Fail("Observed DynamicMethod.Invoke failed: "+__exception.GetType().Name);}
        finally{__state.Dispose();}
        return __exception;
    }
    private static string MemberKey(MethodBase method)=>method.Module.ModuleVersionId.ToString()+":"+method.MetadataToken.ToString("X8");
    private static void BeforePatchMutation(PatchProcessor __instance)
    {
        var observer=active;if(observer==null||!observer.IsInstalled)return;
        var target=typeof(PatchProcessor).GetField("original",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(__instance) as MethodBase;
        if(target==null)return;
        // This prefix runs before Patch/Unpatch enters the very same Harmony
        // mutex. No external controller callback is made under either lock.
        lock(HarmonyGate)lock(observer.gate)
        {
            bool infrastructure=observer.hooks.ContainsKey(target)||ProtectedEmissionMethods.Contains(target)||target.Module.Assembly==typeof(RuntimeRecipeObserver).Assembly||observer.policy.IsFactoryInfrastructure(target);
            bool constructor=observer.policy.IsPotentialConstructor(target);
            if(!infrastructure&&!constructor)return;
            string key=MemberKey(target);observer.mutatedMembers.Add(key);
            foreach(var record in observer.records.Where(r=>r.Guarded.Any(m=>MemberKey(m)==key)))record.Error="A protected constructor/initializer patch mutation was attempted.";
            if(infrastructure)observer.Fail("Protected recipe hook/factory patch mutation was attempted.");
            if(observer.committingThread==Environment.CurrentManagedThreadId)
            {
                observer.Fail("Submission worker attempted a protected Harmony mutation inside publication.");
                throw new InvalidOperationException(observer.failure);
            }
        }
    }

    private static void DelegateReturned(DynamicMethod __instance,Delegate __result)=>Observe(o=>o.OnDelegate(__instance,__result));
    private static void BeforeMaterialization(DynamicMethod __instance,object[] __args,out OperationLease? __state)
    {
        __state=IsAnonymous(__instance.Module.Assembly.FullName??"")?BeginOperation("delegate materialization"):null;
        if(__state==null)return;
        Observe(o=>
        {
            if(!o.methods.TryGetValue(__instance,out var record)){o.Fail("Materialization lacks actual constructor observation.");return;}
            try
            {
                InteropRecipePolicy.Require(!record.Sealed,"Repeated materialization is outside the one cached factory recipe.");
                if(record.Prepared){InteropRecipePolicy.Require(record.RequestedDelegateType==(Type)__args[0]&&(__args.Length<2||__args[1]==null),"Nested materialization request changed after validation.");record.FinalIl!.ValidateBeforeMaterialization();return;}
                InteropRecipePolicy.Require(record.Error.Length==0&&record.FactoryObserved,"Missing pinned factory constructor provenance: "+record.Error);
                InteropRecipePolicy.Require(record.Thread==Environment.CurrentManagedThreadId,"Factory materialization changed its observed producer thread.");
                record.FinalIl=FinalIlSnapshot.CaptureBeforeMaterialization(__instance,o.maximumInstructions);
                record.Constructor=record.FinalIl.Constructor;record.RawIlSha=record.FinalIl.RawIlSha256;
                record.Instructions.AddRange(new[]{new RecipeInstruction("ldarg.0","none",""),new RecipeInstruction("newobj","ConstructorInfo",record.Constructor.ToString()??""),new RecipeInstruction("ret","none","")});
                o.ValidateHooks();
                record.RequestedDelegateType=(Type)__args[0];
                record.Proof=o.policy.ProveBeforeMaterialization(record.Constructor!,__instance.ReturnType,record.RequestedDelegateType,__args.Length>1?__args[1]:null,
                    out var redirectField,out var redirect,out var cache,out var guarded);
                InteropRecipePolicy.Require(!guarded.Any(m=>o.mutatedMembers.Contains(MemberKey(m))),"Constructor/factory mutation history predates this recipe; absence of a current patch does not heal it.");
                record.RedirectField=redirectField;record.Redirect=redirect;record.CachedInitializer=cache;record.Guarded=guarded;record.Prepared=true;
            }
            catch(Exception error){record.Error=error.GetType().Name+": "+error.Message;if(error.Message.Contains("capacity overflow",StringComparison.Ordinal))o.Fail(error.Message);}
        });
    }
    private static Exception? AfterMaterialization(Exception? __exception,DynamicMethod __instance,OperationLease? __state)
    {
        if(__state!=null)try{if(__exception!=null)Observe(o=>{if(o.methods.TryGetValue(__instance,out var record))record.Error="Original CreateDelegate failed: "+__exception.GetType().Name;else o.Fail("Unobserved CreateDelegate failed.");});}
        finally{__state.Dispose();}
        return __exception;
    }
    private void OnDelegate(DynamicMethod method,Delegate result)
    {
        if(!methods.TryGetValue(method,out var record))return;
        if(delegates.TryGetValue(result,out var old)){if(!ReferenceEquals(old,record))record.Error="Delegate identity maps to another method.";return;}
        if(delegateCount>=maximumRecords*2){Fail("Delegate association overflow.");return;}
        delegates.Add(result,record);delegateCount++;
        if(record.Sealed){record.Error="Repeated materialization is outside the one cached factory recipe.";return;}
        record.Sealed=true;record.Delegate=new(result);
        try
        {
            InteropRecipePolicy.Require(record.Error.Length==0&&record.Prepared&&record.Proof!=null,"Recipe was not qualified before CreateDelegate: "+record.Error);
            InteropRecipePolicy.Require(result.GetType()==record.RequestedDelegateType&&result.Target==null,"Actual returned delegate differs from the checked request.");
            record.FinalIl!.ConfirmReturnedDelegate(method,result);
            InteropRecipePolicy.Require(ReferenceEquals(record.RedirectField!.GetValue(null),record.Redirect),"Redirect changed during materialization.");
            record.ResolverConfirmed=true;
        }
        catch(Exception error){record.Error=error.GetType().Name+": "+error.Message;}
    }
    private void Fail(string message){if(failure.Length==0)failure=message;InvalidateScopes(message);}
    private void InvalidateScopes(string message){foreach(var scope in scopes.Values)if(scope.Error.Length==0)scope.Error=message;}
    private void ValidateGlobal(bool images)
    {
        InteropRecipePolicy.Require(IsInstalled&&failure.Length==0,"Recipe observer is unavailable/invalid: "+failure);
        try{ValidateHooks();policy.Validate(images);}catch(Exception error){Fail(error.Message);throw;}
    }
    private void ValidateRecord(Record record)
    {
        InteropRecipePolicy.Require(record.Error.Length==0&&record.Proof!=null&&record.Sealed&&record.ResolverConfirmed,"Recipe has no qualified actual materialized body: "+record.Error);
        try
        {
            record.FinalIl!.ValidateCurrent();
            foreach(var method in record.Guarded)InteropRecipePolicy.EnsureUnpatched(method);
            InteropRecipePolicy.Require(!record.Guarded.Any(m=>mutatedMembers.Contains(MemberKey(m))),"Protected member mutation history invalidated the recipe.");
            InteropRecipePolicy.Require(ReferenceEquals(record.RedirectField!.GetValue(null),record.Redirect),"CreatedTypeRedirect changed after recipe sealing.");
            InteropRecipePolicy.Require(record.Delegate!=null&&record.Delegate.TryGetTarget(out var d)&&ReferenceEquals(record.CachedInitializer!.GetValue(null),d),"Factory cached delegate is missing/replaced.");
        }
        catch(Exception error){record.Error=error.Message;throw;}
    }
    internal RecipeView Find(Delegate value)
    {
        lock(HarmonyGate)lock(gate)
        {
            if(!delegates.TryGetValue(value,out var record))return new(0,"","UNKNOWN","No observed final IL recipe for this actual delegate.","",Array.Empty<string>(),Array.Empty<RecipeInstruction>(),"",null);
            string error="";try{ValidateGlobal(false);ValidateRecord(record);}catch(Exception e){error=e.Message;}
            return View(record,error);
        }
    }
    internal RecipeScopeView AcquireScope()
    {
        lock(HarmonyGate)lock(gate)
        {
            if(scopes.Count>=32){Fail("Retained recipe scope overflow.");return Result("",false,failure,Array.Empty<RecipeView>());}
            string token=Guid.NewGuid().ToString("N");var scope=new ScopeState(generation);scopes.Add(token,scope);
            return ValidateScopeLocked(token,scope,true);
        }
    }
    internal RecipeScopeView ValidateScope(string token,bool images,bool requireSealed=false)
    {
        lock(HarmonyGate)lock(gate)return scopes.TryGetValue(token,out var scope)?ValidateScopeLocked(token,scope,images,requireSealed):Result(token,false,"Unknown or released recipe scope.",Array.Empty<RecipeView>());
    }
    internal RecipeScopeView ValidateScopeStatus(string token)
    {
        // Frame callers need the scope envelope, not copies of every immutable
        // diagnostic recipe. Run the same live checks before omitting the views.
        lock(HarmonyGate)lock(gate)return scopes.TryGetValue(token,out var scope)
            ?ValidateScopeLocked(token,scope,false,includeRecipeViews:false)
            :Result(token,false,"Unknown or released recipe scope.",Array.Empty<RecipeView>());
    }
    internal RecipeScopeView SealScope(string token)
    {
        lock(HarmonyGate)lock(gate)
        {
            if(!scopes.TryGetValue(token,out var scope))return Result(token,false,"Unknown or released recipe scope.",Array.Empty<RecipeView>());
            var result=ValidateScopeLocked(token,scope,false,false,true);
            if(result.Valid){scope.Sealed=true;scope.Generation=generation;scope.RecipesSha256=RecipesHash();return result with{Sealed=true,RecipesSha256=scope.RecipesSha256};}
            return result;
        }
    }
    private RecipeScopeView ValidateScopeLocked(string token,ScopeState scope,bool images,bool requireSealed=false,bool requireComplete=false,bool includeRecipeViews=true)
    {
        try
        {
            InteropRecipePolicy.Require(scope.Error.Length==0,scope.Error);ValidateGlobal(images);
            if(requireSealed)InteropRecipePolicy.Require(scope.Sealed,"Recipe scope was not sealed before commit validation.");
            if(scope.Sealed)InteropRecipePolicy.Require(scope.Generation==generation,"Anonymous generation set changed after scope sealing.");
            InteropRecipePolicy.Require(records.Count>0,"No current observed interop recipes exist.");
            foreach(var record in records)
            {
                // A known factory may be between synchronous emissions on
                // another producer thread. It cannot return a delegate until
                // pre-materialization proof succeeds. Collecting validation
                // may observe that pending metadata; sealing/commit may not.
                if(!scope.Sealed&&!requireSealed&&!requireComplete&&record.FactoryObserved&&record.Error.Length==0)
                {
                    if(!record.Sealed)continue;
                    if(record.CachedInitializer?.GetValue(null)==null)continue;
                }
                ValidateRecord(record);
            }
            if(!scope.Sealed)scope.Generation=generation;
        }
        catch(Exception error){if(scope.Error.Length==0)scope.Error=error.Message;}
        return Result(token,scope.Error.Length==0,scope.Error,includeRecipeViews?records.Select(r=>View(r,r.Error)).ToArray():Array.Empty<RecipeView>(),scope.Sealed,scope.RecipesSha256);
    }
    internal void ReleaseScope(string token){lock(HarmonyGate)lock(gate)scopes.Remove(token);}
    internal bool ValidateHolder(string token,Assembly candidate)
    {
        lock(HarmonyGate)lock(gate)
        {
            if(!scopes.TryGetValue(token,out var scope)||!ValidateScopeLocked(token,scope,false).Valid)return false;
            return records.Any(r=>r.Sealed&&r.Prepared&&r.Error.Length==0&&r.Proof!=null&&ReferenceEquals(r.Holder,candidate));
        }
    }
    internal IDisposable EnterCommitScope(string token,CancellationToken cancellationToken=default)
    {
        bool harmonyHeld=false,recipeHeld=false;
        try
        {
            EnterCancellable(HarmonyGate,cancellationToken);harmonyHeld=true;
            EnterCancellable(gate,cancellationToken);recipeHeld=true;
            cancellationToken.ThrowIfCancellationRequested();
            InteropRecipePolicy.Require(committingThread==0,"A recipe publication guard is already active.");
            InteropRecipePolicy.Require(scopes.TryGetValue(token,out var scope),"Unknown or released recipe scope.");
            var result=ValidateScopeLocked(token,scope!,true,true,true);
            InteropRecipePolicy.Require(result.Valid,result.Reason);
            committingThread=Environment.CurrentManagedThreadId;committingToken=token;
            return new PublicationLease(this,token,committingThread);
        }
        catch{if(recipeHeld)System.Threading.Monitor.Exit(gate);if(harmonyHeld)System.Threading.Monitor.Exit(HarmonyGate);throw;}
    }
    private static void EnterCancellable(object sync,CancellationToken token)
    {token.ThrowIfCancellationRequested();while(!System.Threading.Monitor.TryEnter(sync,25))token.ThrowIfCancellationRequested();}
    private sealed class PublicationLease : IDisposable
    {
        private RuntimeRecipeObserver? owner;private readonly string token;private readonly int thread;
        internal PublicationLease(RuntimeRecipeObserver owner,string token,int thread)=>(this.owner,this.token,this.thread)=(owner,token,thread);
        public void Dispose()
        {
            if(owner==null)return;
            if(Environment.CurrentManagedThreadId!=thread)throw new InvalidOperationException("Publication guard must be disposed by its synchronous acquiring worker.");
            var current=owner;owner=null;
            // Consume while both locks remain held. New normal work after
            // publication cannot retroactively mutate this completed token.
            current.scopes.Remove(token);current.committingThread=0;current.committingToken="";
            System.Threading.Monitor.Exit(current.gate);System.Threading.Monitor.Exit(HarmonyGate);
        }
    }
    internal RecipeScopeView Snapshot()
    {
        lock(HarmonyGate)lock(gate)return Result("",false,failure.Length==0?"Snapshot is evidence only; acquire a retained scope.":failure,records.Select(r=>View(r,r.Error)).ToArray());
    }
    private string RecipesHash()
    {
        // Stable semantic identities only: no process ordinal, scope token,
        // anonymous-module MVID, native pointer or dynamic token table index.
        var hashes=records.Select(r=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "final-il-wrapper-v2|"+string.Join(",",r.Instructions.Select(i=>i.Opcode))+"|"+System.Text.Json.JsonSerializer.Serialize(r.Proof)))))
            .OrderBy(h=>h,StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("sealed-final-il-recipes-v2\n"+string.Join("\n",hashes))));
    }
    private RecipeScopeView Result(string token,bool valid,string reason,IReadOnlyList<RecipeView> views,bool sealedScope=false,string recipesSha256="")=>new("aave.runtime-recipes.v2",token,valid,reason,policy.PolicyId,PolicySha256,recipesSha256,policy.KnownHostBaseline,sealedScope,false,false,false,views);
    private static RecipeView View(Record record,string reason)=>new(record.Id,record.Name,reason.Length==0&&record.Proof!=null&&record.Sealed&&record.ResolverConfirmed?"QUALIFIED":reason.Length==0?"PENDING":"UNQUALIFIED",reason,
        record.ReturnType,Array.AsReadOnly((string[])record.Parameters.Clone()),record.Instructions.ToArray(),record.RawIlSha,record.Proof,reason.Length==0&&record.Error.Length==0&&record.Prepared&&record.Sealed&&record.ResolverConfirmed);
    public void Dispose()
    {
        lock(HarmonyGate)lock(gate)
        {
            if(disposed)return;
            installed=false;disposed=true;foreach(var scope in scopes.Values)scope.Error="Observer disposed.";
            // Deactivate receipts, not the process-lifetime narrow boundary
            // hooks. There are no ILGenerator/DynamicILGenerator detours.
            if(ReferenceEquals(active,this))active=null;
        }
    }
    private sealed class ScopeState {internal long Generation;internal bool Sealed;internal string Error="",RecipesSha256="";internal ScopeState(long generation)=>Generation=generation;}
    private sealed class Record
    {
        internal readonly long Id;internal readonly string Name,ReturnType;internal readonly string[] Parameters;internal readonly int Thread;internal readonly Assembly Holder;
        internal readonly List<RecipeInstruction> Instructions=new();
        private string error="";private readonly RuntimeRecipeObserver observer;
        internal string Error {get=>error;set{if(value.Length==0)return;if(error.Length==0)error=value;else if(!error.Contains(value,StringComparison.Ordinal)&&error.Length+value.Length<8192)error+="; "+value;observer.InvalidateScopes(value);}}
        internal string RawIlSha="";internal bool FactoryObserved,Sealed,Prepared,ResolverConfirmed;internal Type? RequestedDelegateType;
        internal FinalIlSnapshot? FinalIl;internal WeakReference<Delegate>? Delegate;internal ConstructorInfo? Constructor;
        internal FieldInfo? RedirectField,CachedInitializer;internal Type? Redirect;internal MethodBase[] Guarded=Array.Empty<MethodBase>();internal InteropRecipeProof? Proof;
        internal Record(long id,DynamicMethod method,RuntimeRecipeObserver observer){this.observer=observer;Id=id;Name=method.Name;Holder=method.Module.Assembly;ReturnType=method.ReturnType.AssemblyQualifiedName??"";Parameters=method.GetParameters().Select(p=>p.ParameterType.AssemblyQualifiedName??"").ToArray();InteropRecipePolicy.Require(Name.Length<=4096&&ReturnType.Length<=4096&&Parameters.Length<=16&&Parameters.All(p=>p.Length<=4096),"Recipe identity overflow; no truncated proof is accepted.");Thread=Environment.CurrentManagedThreadId;}
    }
}
