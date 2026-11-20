using System.Collections;
using System.Collections.Concurrent;

using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using ManagedOriginalCallWitness;
namespace Aave.WaitOwnership.CallerSource;
internal sealed class ChainGeneration
{
    private static long next;
    private static readonly ConcurrentDictionary<long,ChainGeneration> All=new();
    private static readonly ChainGeneration Unknown=new();
    internal readonly long Token;
    internal readonly int PlanId;
    internal readonly CallerSourceKind Kind;
    internal readonly ManagedChainRegistration? Registration;
    internal DynamicBodyExpectation? Expected;
    internal MethodInfo? ActualMethod;internal Delegate? Callable;
    internal string Reason="Unqualified",CodeHash="",Provenance="";
    internal string? PreparationFailure;
    internal string PreparationStatus="Not prepared",PublicationStatus="Not attempted",InvalidationReason="";
    internal bool NormalProvider;
    private int active;
    internal bool IsActive=>Volatile.Read(ref active)!=0;
    private ChainGeneration(){}
    internal ChainGeneration(ManagedChainRegistration registration)
    {Registration=registration;PlanId=registration.PlanId;Kind=registration.Kind;Token=Interlocked.Increment(ref next);All[Token]=this;}
    internal static ChainGeneration Lookup(long token)=>All.TryGetValue(token,out var value)?value:Unknown;
    internal void Revoke(){Volatile.Write(ref active,0);if(Token!=0)All.TryRemove(Token,out _);}
    internal void Activate(){All[Token]=this;Volatile.Write(ref active,1);}
}
public sealed class ManagedChainRegistration : IDisposable
{
    private static readonly ConcurrentDictionary<MethodBase,ManagedChainRegistration> Registrations=new();
    private static readonly ConcurrentDictionary<int,MethodInfo> PlanTargets=new();
    private static readonly object HooksGate=new();private static bool hooks;
    [ThreadStatic] private static ManagedChainRegistration? publishing;
    private readonly object gate=new();
    private readonly bool ordinary;
    private bool disposed;
    private bool attemptCancelled;
    private long epoch;
    private ChainGeneration? latest;
    private Harmony? installedBy;
    internal readonly SourceBodyTemplate Template;
    public MethodInfo Original{get;}
    public int PlanId{get;}
    public CallerSourceKind Kind{get;}
    public bool Qualified {get {lock(gate)return latest?.IsActive==true;}}
    public bool IsDisposed {get {lock(gate)return disposed;}}
    public bool NormalProviderPublished {get {lock(gate)return !disposed&&latest?.IsActive==true&&latest.NormalProvider;}}
    public string Reason {get {lock(gate)return latest?.PreparationFailure??latest?.Reason??"No generated candidate";}}
    public string PreparationFailure {get {lock(gate)return latest?.PreparationFailure??"";}}
    public string PreparationStatus {get {lock(gate)return latest?.PreparationStatus??"No generated candidate";}}
    public string PublicationStatus {get {lock(gate)return latest?.PublicationStatus??"Not attempted";}}
    public string InvalidationReason {get {lock(gate)return latest?.InvalidationReason??"";}}
    public long Generation {get {lock(gate)return latest?.Token??0;}}
    internal ChainGeneration? ActiveGeneration{get{lock(gate)return latest?.IsActive==true?latest:null;}}
    public string GeneratedCodeHash {get {lock(gate)return latest?.CodeHash??"";}}
    private ManagedChainRegistration(MethodInfo original,Mono.Cecil.MethodDefinition source,int id,CallerSourceKind kind,bool control)
    {
        if(kind==CallerSourceKind.Observation)
        {
            if(original.ContainsGenericParameters||original.DeclaringType?.IsGenericType==true||original.DeclaringType?.IsValueType==true||original.GetParameters().Length>6||
                original.IsVirtual&&!original.IsFinal&&original.DeclaringType?.IsSealed!=true||!FactShape.Supported(original.ReturnType,true)||
                original.GetParameters().Any(p=>!FactShape.Supported(p.ParameterType,false)))throw new NotSupportedException("Unsupported generic original observation shape");
        }
        else if(!original.IsStatic||original.ReturnType!=typeof(void)||original.ContainsGenericParameters)
            throw new NotSupportedException("Only static non-generic void source targets");
        var args=original.GetParameters();
        bool zeroCaller=kind==CallerSourceKind.Replacement&&args.Length==0;
        if(kind!=CallerSourceKind.Observation&&!zeroCaller&&(args.Length!=3||args[0].ParameterType.IsValueType||args[1].ParameterType.IsValueType||args.Any(p=>p.ParameterType.IsByRef||p.ParameterType.IsPointer)||
           (kind==CallerSourceKind.Release?args[2].ParameterType!=typeof(string):args[2].ParameterType!=typeof(string)&&args[2].ParameterType!=typeof(float))))throw new NotSupportedException("Unsupported source operand shape");
        if(control)PinnedEmitterFlags.RequirePreparedSource(source);
        Original=original;PlanId=id;Kind=kind;ordinary=control;Template=SourceBodyTemplate.Capture(source,id,kind);
        if(!PlanTargets.TryAdd(id,original))throw new InvalidOperationException("Source plan id already binds another live registration");
        if(!Registrations.TryAdd(original,this)){PlanTargets.TryRemove(id,out _);throw new InvalidOperationException("Exact original already registered");}
    }
    public static ManagedChainRegistration FromCurrentOriginal(MethodInfo original,int sourcePlanId,CallerSourceKind kind)
    {
        RequirePinned();PinnedEmitterFlags.RequireOriginalSource(original);using var definition=new DynamicMethodDefinition(original);
        return new(original,definition.Definition,sourcePlanId,kind,false);
    }
    // Ordinary test harness compiles this source into its own assembly. This
    // prepared-body path is internal and cannot be called by a deployed owner.
    internal static ManagedChainRegistration ForOrdinaryControl(MethodInfo original,Mono.Cecil.MethodDefinition source,int planId,CallerSourceKind kind)
        =>new(original,source,planId,kind,true);
    internal void SetOrdinaryForwarder(MethodBase fixtureForwarder,MethodBase actualGeneratedOriginal)
    {if(!ordinary)throw new InvalidOperationException("Only explicit no-detour fixture routing");Template.ControlForwarder(fixtureForwarder,actualGeneratedOriginal);}
    internal static bool TryGet(MethodBase method,out ManagedChainRegistration registration)=>Registrations.TryGetValue(method,out registration!);
    // Read-only qualification query for cold-declared runtime clock plans: does
    // this exact original currently hold a live, qualified, normally published
    // registration of the given plan? No state is created or changed.
    public static bool IsPublished(MethodBase method,int sourcePlanId)
    {
        if(method is null||sourcePlanId<=0)return false;
        if(!Registrations.TryGetValue(method,out var registration)||registration is null)return false;
        return registration.PlanId==sourcePlanId&&registration.Qualified&&registration.NormalProviderPublished;
    }
    internal ChainGeneration BeginBuild()
    {
        lock(gate)
        {
            latest?.Revoke();epoch++;
            var value=new ChainGeneration(this);latest=value;
            if(disposed)value.Reason="Disposed registration";
            return value;
        }
    }
    public void Revoke(string reason)
    {lock(gate){latest?.Revoke();if(latest!=null){latest.InvalidationReason=reason;latest.Reason=reason;}epoch++;}}
    private void ExternalChange()
    {lock(gate){latest?.Revoke();epoch++;if(!ReferenceEquals(publishing,this))attemptCancelled=true;if(latest!=null){latest.InvalidationReason="Normal detour/ILHook change observed; no successful new publication yet";latest.Reason=latest.InvalidationReason;}}}
    private static void Change(MethodBase original)
    {if(TryGet(original,out var r))r.ExternalChange();}
    private static bool Detouring(Detour d,MethodBase original,MethodBase target){Change(original);return true;}
    private static bool Undoing(Detour d){Change(d.Method);return true;}
    private static bool ILDetouring(ILHook h,MethodBase original,ILContext.Manipulator m){Change(original);return true;}
    private static bool ILUndoing(ILHook h){Change(h.Method);return true;}
    private static void Resolve(object? sender,PatchManager.PatcherResolverEventArgs args)
    {
        if(TryGet(args.Original,out var r)&&!r.ordinary&&args.MethodPatcher?.GetType()==typeof(ManagedMethodPatcher))
            args.MethodPatcher=new PublishingPatcher(args.Original,r);
    }
    private static void EnsureHooks()
    {
        lock(HooksGate)
        {
            if(hooks)return;
            PatchManager.ResolvePatcher+=Resolve;Detour.OnDetour+=Detouring;Detour.OnUndo+=Undoing;ILHook.OnDetour+=ILDetouring;ILHook.OnUndo+=ILUndoing;hooks=true;
        }
    }
    // This is an explicit Root host entry point. Ordinary controls NEVER call
    // it. Existing installer/backend remains stock ManagedMethodPatcher.
    public void InstallNormal(Harmony harmony)
    {
        if(ordinary||disposed)throw new InvalidOperationException("Not a live normal-provider registration");
        RequirePinned();EnsureHooks();
        if(Original.GetMethodPatcher() is not PublishingPatcher p||!ReferenceEquals(p.Registration,this))
            throw new InvalidOperationException("Original already resolved through a different provider; untouched");
        installedBy=harmony;
        harmony.Patch(Original,ilmanipulator:new HarmonyMethod(typeof(WholeChainManipulator).GetMethod(nameof(WholeChainManipulator.Apply))!));
    }
    private sealed class PublishingPatcher : ManagedMethodPatcher
    {
        internal readonly ManagedChainRegistration Registration;
        internal PublishingPatcher(MethodBase method,ManagedChainRegistration registration):base(method){Registration=registration;}
        public override MethodBase DetourTo(MethodBase replacement)
        {
            var prior=publishing;publishing=Registration;
            lock(Registration.gate){Registration.latest?.Revoke();Registration.attemptCancelled=false;}
            try
            {
                MethodBase target=base.DetourTo(replacement);
                Registration.PublishActual(target);
                return target;
            }
            catch{Registration.Revoke("Normal provider failed; old qualification revoked");throw;}
            finally{publishing=prior;}
        }
    }
    private void PublishActual(MethodBase target)
    {
        try
        {
            if(target is not MethodInfo method)throw new NotSupportedException("Normal target is not MethodInfo");
            RequireExclusiveCurrentDetour(target);
            Publish(method,null,true);
        }
        catch(Exception e){var reason="Exact normal publication refused: "+e.GetType().Name+": "+e.Message;lock(gate){if(latest!=null)latest.PublicationStatus=reason;}Revoke(reason);}
    }
    internal void PublishOrdinary(MethodInfo method,Delegate callable)
    {
        if(!ordinary)throw new InvalidOperationException("Control publication cannot authorize actual provider");
        Publish(method,callable,false);
    }
    private void Publish(MethodInfo method,Delegate? callable,bool normal)
    {
        ChainGeneration? candidate;long version;
        lock(gate){candidate=latest;version=epoch;if(disposed||candidate==null||normal&&attemptCancelled){if(candidate!=null)candidate.PublicationStatus="Refused: disposed or invalidated publication attempt";return;}}
        if(candidate.Expected==null){candidate.PublicationStatus="Refused: preparation produced no exact expectation";return;}
        try
        {
            DynamicBodyReadback readback;
            if(normal)
            {
                if(callable!=null)throw new InvalidDataException("Actual provider readback cannot substitute a callable.");
                readback=DynamicMethodBodyReader.ReadPublished(method,candidate.Token);
                readback.RequirePublishedExact(method,candidate.Expected);
            }
            else
            {
                if(callable==null)throw new InvalidDataException("Ordinary fixture lost its existing normal callable.");
                readback=DynamicMethodBodyReader.Read(method,callable,candidate.Token);
                readback.RequireExact(method,callable,candidate.Expected);
            }
            lock(gate)
            {
                if(disposed||!ReferenceEquals(latest,candidate)||epoch!=version||normal&&attemptCancelled){candidate.PublicationStatus="Refused: generation changed during actual readback";return;}
                candidate.ActualMethod=method;candidate.Callable=callable;candidate.CodeHash=readback.CodeSha256;candidate.Provenance=readback.Provenance;
                candidate.NormalProvider=normal;candidate.Reason=normal?"Exact after-Apply normal target qualified":"Exact ordinary generated full chain qualified; no detour installed";candidate.PublicationStatus=candidate.Reason;candidate.Activate();
            }
        }
        catch(Exception e){candidate.Revoke();candidate.Reason="Final actual body not qualified: "+e.GetType().Name+": "+e.Message;candidate.PublicationStatus=candidate.Reason;}
    }
    private static void RequireExclusiveCurrentDetour(MethodBase target)
    {
        var f=typeof(Detour).GetField("_DetourMap",BindingFlags.Static|BindingFlags.NonPublic)??throw new MissingFieldException("Pinned Detour map");
        var map=f.GetValue(null) as IDictionary??throw new InvalidDataException("Pinned Detour map shape differs");
        // Called only at publication, never on frame/invocation paths. Require
        // exactly one applied stock ILHook detour; unknown pre-existing detour
        // wrappers can execute before our complete Harmony chain, so refuse.
        var matches=new List<Detour>();
        foreach(DictionaryEntry e in map)
            if(e.Value is IEnumerable values)
                foreach(var v in values)if(v is Detour d&&d.IsApplied&&ReferenceEquals(d.Target,target))matches.Add(d);
        if(matches.Count!=1)throw new InvalidDataException("Exact applied target not unique");
        var source=matches[0].Method;
        var list=map[source] as IEnumerable??throw new InvalidDataException("Missing current normal chain");
        var active=list.Cast<object>().OfType<Detour>().Where(d=>d.IsApplied).ToArray();
        if(active.Length!=1||!ReferenceEquals(active[0],matches[0]))throw new InvalidDataException("Foreign detour before whole Harmony chain cannot be qualified");
    }
    private static void RequirePinned()
    {
        void Pin(Type type,string hash){if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location)))!=hash)throw new NotSupportedException("Unverified "+type.Assembly.GetName().Name);}
        Pin(typeof(ManagedMethodPatcher),"6C898933B52149E8BCF6722305C7E4D94ADD47C9E53B364096F91721624C0877");
        Pin(typeof(Detour),"1B3D55F53CB9F9F2550DAED4DFD300B247D71698A0ACE82BC7B9FF38D563594F");
    }
    public void Dispose()
    {
        lock(gate){if(disposed)return;disposed=true;latest?.Revoke();if(latest!=null)latest.InvalidationReason="Registration disposed";epoch++;}
        Registrations.TryRemove(Original,out _);
        PlanTargets.TryRemove(PlanId,out _);
        if(installedBy!=null)installedBy.Unpatch(Original,typeof(WholeChainManipulator).GetMethod(nameof(WholeChainManipulator.Apply))!);
    }
}





