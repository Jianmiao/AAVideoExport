using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

const string owner = "halocue.aa.skipintro";
const string fixtureFingerprint = "verified-fixture-build";
var harmony = new Harmony("aave.startup.boundary.tests");
var mod = new Harmony(owner);
MethodInfo awake = typeof(CreatorSplash).GetMethod(nameof(CreatorSplash.Awake))!;
MethodInfo postfix = typeof(AASkipIntro.CreatorSplashPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!;
if (!AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled) return ProductionStartupChecks.Run(harmony, mod, awake, postfix);
if (args.Length == 2 && args[0] == "--inspect-installed")
{
    using var binary = File.OpenRead(args[1]);
    using var pe = new PEReader(binary);
    var metadata = pe.GetMetadataReader();
    var version = metadata.GetAssemblyDefinition().Version;
    var module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])));
    Check((bool)Api.Call("IsSupportedBuild", version, module, hash)!);
    Console.WriteLine($"PASS inspected installed build: version={version}; mvid={module}; sha256={hash}; path={args[1]}");
    return 0;
}
var tests = new (string Name, Action Run)[]
{
    ("admission proof requires the currently retained startup controller", () =>
    {
        Start();
        Throws<InvalidOperationException>(() => Api.Call("CaptureActiveProof"));
        var lease = Acquire();
        Equal(fixtureFingerprint, Api.Call("CaptureActiveProof"));
        lease.Dispose();
        Throws<InvalidOperationException>(() => Api.Call("CaptureActiveProof"));
    }),
    ("an absent startup snapshot cannot authorize a loaded startup module", () =>
    {
        Api.Call("Initialize", harmony); Equal(null, Api.Call("TryAcquire"));
        Throws<InvalidOperationException>(() => Api.Call("CaptureActiveProof"));
    }),
    ("admission proof revalidates live startup state before returning", () =>
    {
        Start(); var lease = Acquire();
        Resources.Objects.Add(new CreatorSplash());
        Throws<InvalidOperationException>(() => Api.Call("CaptureActiveProof"));
        try { lease.Dispose(); } catch (InvalidOperationException) { }
    }),
    ("absent optional Mod yields no lease and admits the worker commit", () =>
    {
        Api.Call("Initialize", harmony);
        Equal(null, Api.Call("TryAcquire"));
        Check(!Api.Get<bool>("IsActive"));
        Equal("", Api.Get<string>("BuildFingerprint"));
        Api.Call("ValidateActive");
        Worker(() => Api.Call("ValidateForCommit"));
    }),
    ("releasing an absent snapshot rejects late worker commit without live Unity reads", () =>
    {
        Api.Call("Initialize", harmony);
        Equal(null, Api.Call("TryAcquire"));
        int reads = Resources.ReadCount;
        Worker(() => Api.Call("ValidateForCommit"));
        Equal(reads, Resources.ReadCount);
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ReleaseAbsentSnapshot")));
        Worker(() => Api.Call("ValidateForCommit"));
        Api.Call("ReleaseAbsentSnapshot");
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
        Equal(reads, Resources.ReadCount);
        Api.Call("ReleaseAbsentSnapshot");
        Equal(null, Api.Call("TryAcquire"));
        Worker(() => Api.Call("ValidateForCommit"));
    }),
    ("exact version MVID and file hash are jointly required", () =>
    {
        var version = new Version(0, 1, 0, 0);
        var module = new Guid("9a4693f8-010c-4c58-b66d-fad395393afb");
        const string hash = "A78A9BB90FDD61C4A7D32B03AE35B08A8633BDF68B4CFBFEEE5360EEAE770DC6";
        Check((bool)Api.Call("IsSupportedBuild", version, module, hash)!);
        Check(!(bool)Api.Call("IsSupportedBuild", new Version(0, 1, 1, 0), module, hash)!);
        Check(!(bool)Api.Call("IsSupportedBuild", version, Guid.NewGuid(), hash)!);
        Check(!(bool)Api.Call("IsSupportedBuild", version, module, new string('0', 64))!);
    }),
    ("failed initialization preserves a pre-existing foreign monitor registration", () =>
    {
        var foreign = new Harmony("aave.startup.foreign.monitor");
        try
        {
            foreign.Patch(awake, prefix: new HarmonyMethod(Api.Method("OnCreatorSplashAwake")));
            Throws<NotSupportedException>(() => Api.Call("Initialize", harmony));
            Check(Harmony.GetPatchInfo(awake)!.Prefixes.Any(p => p.owner == foreign.Id));
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("only the exact static typed postfix signature is accepted", () =>
    {
        Api.Call("Initialize", harmony);
        Equal(postfix, Api.Call("ResolvePostfix", typeof(AASkipIntro.CreatorSplashPatch)));
        Throws<NotSupportedException>(() => Api.Call("ResolvePostfix", SignatureFixture(typeof(bool), typeof(CreatorSplash), true, "__instance")));
        Throws<NotSupportedException>(() => Api.Call("ResolvePostfix", SignatureFixture(typeof(void), typeof(object), true, "__instance")));
        Throws<NotSupportedException>(() => Api.Call("ResolvePostfix", SignatureFixture(typeof(void), typeof(CreatorSplash), false, "__instance")));
        Throws<NotSupportedException>(() => Api.Call("ResolvePostfix", SignatureFixture(typeof(void), typeof(CreatorSplash), true, "presenter")));
    }),
    ("missing or wrong patch ownership rejects before acquiring", () =>
    {
        Api.Call("Initialize", harmony);
        Throws<NotSupportedException>(() => Acquire());
        var impostor = new Harmony("unrelated.owner");
        try
        {
            impostor.Patch(awake, postfix: new HarmonyMethod(postfix));
            Throws<NotSupportedException>(() => Acquire());
            Check(Harmony.GetPatchInfo(awake)!.Postfixes.Single().owner == impostor.Id);
        }
        finally { impostor.UnpatchSelf(); }
        Check(!Api.Get<bool>("IsActive"));
    }),
    ("typed active splash blocks acquisition while inactive and unrelated presenters do not", () =>
    {
        Start();
        var splash = new CreatorSplash();
        Resources.Objects.Add(splash);
        Throws<InvalidOperationException>(() => Acquire());
        Check(splash.gameObject.activeSelf && !splash.NativeAwakeRan);
        splash.gameObject.SetActive(false);
        Resources.Objects.Add(new UnrelatedPresenter());
        using var lease = Acquire();
        Api.Call("ValidateActive");
    }),
    ("retained lease survives object-list replacement across scene roundtrip", () =>
    {
        Start();
        int validations = 0;
        using var lease = (IDisposable)Api.Call("AcquireValidatedBoundary", fixtureFingerprint,
            (Action)(() => { validations++; Api.Call("ValidatePatchState", postfix); }))!;
        Check(Api.Get<bool>("IsActive"));
        Equal(fixtureFingerprint, Api.Get<string>("BuildFingerprint"));
        for (int scene = 0; scene < 3; scene++)
        {
            Resources.Objects.Clear();
            Resources.Objects.Add(new UnrelatedPresenter());
            Api.Call("ValidateActive");
        }
        int reads = Resources.ReadCount;
        int checks = validations;
        Worker(() => Api.Call("ValidateForCommit"));
        Equal(reads, Resources.ReadCount);
        Equal(checks, validations);
        Check(Api.Get<bool>("IsActive"));
    }),
    ("Awake invalidates retained lease but still runs native initialization and the real Mod effect", () =>
    {
        Start();
        var lease = Acquire();
        var splash = new CreatorSplash();
        Resources.Objects.Add(splash);
        splash.Awake();
        Check(splash.NativeAwakeRan && splash.InputRestored && splash.IsComplete);
        Check(!splash.gameObject.activeSelf);
        Throws<InvalidOperationException>(() => Api.Call("ValidateActive"));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
        Throws<InvalidOperationException>(() => lease.Dispose());
        Check(!Api.Get<bool>("IsActive"));
        Equal("", Api.Get<string>("BuildFingerprint"));
        lease.Dispose();
        Check(Harmony.GetPatchInfo(awake)!.Postfixes.Single().PatchMethod == postfix);
    }),
    ("mid-lease patch removal latches failure through repair and release", () =>
    {
        Start();
        var lease = Acquire();
        mod.Unpatch(awake, postfix);
        Throws<NotSupportedException>(() => Api.Call("ValidateActive"));
        mod.Patch(awake, postfix: new HarmonyMethod(postfix));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
        Throws<InvalidOperationException>(() => lease.Dispose());
        Check(!Api.Get<bool>("IsActive"));
    }),
    ("late active typed splash is detected even without an Awake notification", () =>
    {
        Start();
        var lease = Acquire();
        Resources.Objects.Add(new CreatorSplash());
        Throws<InvalidOperationException>(() => Api.Call("ValidateActive"));
        Throws<InvalidOperationException>(() => lease.Dispose());
        Check(!Api.Get<bool>("IsActive"));
    }),
    ("foreign target patches fail closed without removing unrelated behavior", () =>
    {
        Start();
        var foreign = new Harmony("aave.startup.foreign");
        try
        {
            foreign.Patch(awake, prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Count))));
            Throws<NotSupportedException>(() => Acquire());
            var splash = new CreatorSplash();
            splash.Awake();
            Check(Hooks.Calls == 1 && splash.NativeAwakeRan && !splash.gameObject.activeSelf);
            Check(Harmony.GetPatchInfo(awake)!.Prefixes.Any(p => p.owner == foreign.Id));
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("patching the inspected Mod postfix itself cannot bypass validation", () =>
    {
        Start();
        var foreign = new Harmony("aave.startup.postfix.foreign");
        try
        {
            foreign.Patch(postfix, prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Count))));
            Throws<NotSupportedException>(() => Acquire());
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("foreign Harmony IL manipulators cannot bypass target or postfix ownership checks", () =>
    {
        Start();
        var foreign = new Harmony("aave.startup.il.foreign");
        var manipulate = new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Manipulate)));
        try
        {
            foreign.Patch(awake, prefix: null, postfix: null, transpiler: null, finalizer: null, ilmanipulator: manipulate);
            Throws<NotSupportedException>(() => Acquire());
            foreign.UnpatchSelf();
            foreign.Patch(postfix, prefix: null, postfix: null, transpiler: null, finalizer: null, ilmanipulator: manipulate);
            Throws<NotSupportedException>(() => Acquire());
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("duplicate acquire and stale lease cannot release another export", () =>
    {
        Start();
        var first = Acquire();
        Throws<InvalidOperationException>(() => Acquire());
        first.Dispose();
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
        using var second = Acquire();
        first.Dispose();
        Check(Api.Get<bool>("IsActive"));
        Api.Call("ValidateActive");
    }),
    ("absent snapshot release cannot terminate a present-Mod lease", () =>
    {
        Api.Method("ReleaseAbsentSnapshot");
        Start();
        using var lease = Acquire();
        Throws<InvalidOperationException>(() => Api.Call("ReleaseAbsentSnapshot"));
        Check(Api.Get<bool>("IsActive"));
        Worker(() => Api.Call("ValidateForCommit"));
    }),
    ("main-thread lifecycle guard preserves the active lease on wrong-thread calls", () =>
    {
        Start();
        using var lease = Acquire();
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("TryAcquire")));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateActive")));
        Throws<InvalidOperationException>(() => Worker(() => lease.Dispose()));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("Shutdown")));
        Check(Api.Get<bool>("IsActive"));
        Api.Call("ValidateActive");
    }),
    ("shutdown removes only its monitor and preserves native Mod and unrelated behavior", () =>
    {
        Start();
        using var lease = Acquire();
        Api.Call("Shutdown");
        var info = Harmony.GetPatchInfo(awake)!;
        Check(info.Prefixes.All(p => p.owner != harmony.Id));
        Check(info.Postfixes.Single().PatchMethod == postfix && info.Postfixes.Single().owner == owner);
        var splash = new CreatorSplash();
        splash.Awake();
        Check(splash.NativeAwakeRan && splash.InputRestored && !splash.gameObject.activeSelf);
        var other = new UnrelatedPresenter();
        other.Awake();
        Check(other.NativeAwakeRan && other.gameObject.activeSelf);
        Check(Harmony.GetPatchInfo(typeof(UnrelatedPresenter).GetMethod(nameof(UnrelatedPresenter.Awake))) == null);
    }),
    ("shutdown preserves foreign registrations of the same monitor and other shared-owner patches", () =>
    {
        Start();
        var foreign = new Harmony("aave.startup.foreign.monitor");
        try
        {
            foreign.Patch(awake, prefix: new HarmonyMethod(Api.Method("OnCreatorSplashAwake")));
            harmony.Patch(awake, prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Count))));
            Api.Call("Shutdown");
            var prefixes = Harmony.GetPatchInfo(awake)!.Prefixes;
            Check(prefixes.Any(p => p.owner == foreign.Id));
            Check(prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod.Name == nameof(Hooks.Count)));
            Check(Harmony.GetPatchInfo(awake)!.Postfixes.Single().PatchMethod == postfix);
        }
        finally { foreign.UnpatchSelf(); }
    }),
    ("invalidated commit scope acquisition fails and releases its mutex", () => CommitScopeChecks.InvalidatedAcquisition(Start, Acquire)),
    ("worker publication scope protects absent proof until same-thread release", () => CommitScopeChecks.AbsentPublication(harmony)),
    ("main-thread live validation blocks worker publication without worker Unity access", () => CommitScopeChecks.ValidationExcludesPublication(Start, postfix)),
    ("wrong-thread publication token disposal cannot unlock or consume the token", () => CommitScopeChecks.WrongThreadDisposal(harmony)),
    ("publication release admits pending Awake and preserves native and Mod effects", () => CommitScopeChecks.PendingAwake(Start, Acquire)),
    ("a loaded same-name unknown assembly never falls through the absent-Mod path", () =>
    {
        Api.Call("Initialize", harmony);
        Equal(null, Api.Call("TryAcquire"));
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AASkipIntro") { Version = new Version(0, 1, 0, 0) }, AssemblyBuilderAccess.Run);
        Throws<NotSupportedException>(() => Api.Call("ValidateActive"));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
        Throws<NotSupportedException>(() => Api.Call("TryAcquire"));
        Check(!Api.Get<bool>("IsActive"));
        Throws<InvalidOperationException>(() => Worker(() => Api.Call("ValidateForCommit")));
    }),
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
    finally
    {
        try { Api.Call("Shutdown"); } catch (Exception error) { Console.WriteLine("CLEANUP " + error.Message); }
        harmony.UnpatchSelf(); mod.UnpatchSelf(); Resources.Objects.Clear(); Hooks.Calls = 0;
    }
}
Console.WriteLine($"RESULT {tests.Length - failures} passed; {failures} failed");
return failures == 0 ? 0 : 1;

void Start() { Api.Call("Initialize", harmony); mod.Patch(awake, postfix: new HarmonyMethod(postfix)); }
IDisposable Acquire() => (IDisposable)Api.Call("AcquireValidatedBoundary", fixtureFingerprint,
    (Action)(() => Api.Call("ValidatePatchState", postfix)))!;
static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
static void Equal(object? expected, object? actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected ?? "<null>"}, got {actual ?? "<null>"}."); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name + ".");
}
static void Worker(Action action)
{
    Exception? fault = null;
    var thread = new Thread(() => { try { action(); } catch (Exception error) { fault = error; } });
    thread.Start(); thread.Join();
    if (fault != null) ExceptionDispatchInfo.Capture(fault).Throw();
}
static Type SignatureFixture(Type returns, Type parameter, bool isStatic, string parameterName)
{
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MalformedStartupFixture" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
    var builder = assembly.DefineDynamicModule("fixture").DefineType("AASkipIntro.CreatorSplashPatch", TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
    var method = builder.DefineMethod("Postfix", MethodAttributes.Assembly | (isStatic ? MethodAttributes.Static : 0), returns, new[] { parameter });
    method.DefineParameter(1, ParameterAttributes.None, parameterName);
    var il = method.GetILGenerator();
    if (returns == typeof(bool)) il.Emit(OpCodes.Ldc_I4_0);
    il.Emit(OpCodes.Ret);
    return builder.CreateType()!;
}

internal static class Api
{
    private static Type Controller => typeof(Api).Assembly.GetType("AAVideoExport.Plugin.AASkipIntroExportBoundary")
        ?? throw new InvalidOperationException("Retained startup controller is not implemented.");
    internal static object? Call(string name, params object?[] args)
    {
        var method = Method(name);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    internal static MethodInfo Method(string name) => Controller.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Retained startup operation is missing: " + name);
    internal static T Get<T>(string name) => (T)Controller.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
    internal static T Field<T>(string name) => (T)Controller.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}

public static class Hooks
{
    public static int Calls;
    public static void Count() { Calls++; }
    public static void Manipulate(MonoMod.Cil.ILContext context) { }
}
