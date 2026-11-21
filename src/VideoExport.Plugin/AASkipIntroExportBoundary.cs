using System.Reflection;
using System.Security.Cryptography;
using AAVideoExport.Core;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.Plugin;

// Native Awake and the original skip postfix remain in control. Production
// keeps only startup resource readiness/lifetime; DevelopmentAudit adds the
// retained provenance monitor. Neither path disables the Mod or a Unity object.
internal static class AASkipIntroExportBoundary
{
    private const string AssemblyName = "AASkipIntro";
    private const string ModOwner = "halocue.aa.skipintro";
    private const string SupportedHash = "A78A9BB90FDD61C4A7D32B03AE35B08A8633BDF68B4CFBFEEE5360EEAE770DC6";
    private static readonly Version SupportedVersion = new(0, 1, 0, 0);
    private static readonly Guid SupportedModule = new("9a4693f8-010c-4c58-b66d-fad395393afb");
    private static readonly object Gate = new();
    private static readonly MethodInfo Monitor = typeof(AASkipIntroExportBoundary)
        .GetMethod(nameof(OnCreatorSplashAwake), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Harmony? _harmony;
    private static MethodInfo? _awake;
    private static int _mainThread;
    private static Lease? _active;
    private static bool _acquisitionObserved;
    private static bool _requiresLease;
    private static Exception? _fault;

    internal static bool IsActive { get { lock (Gate) return _active != null; } }
    internal static string BuildFingerprint { get { RequireAudit(); lock (Gate) return _active?.Fingerprint ?? ""; } }

    // Admission consumes the same retained, live-validated controller used by
    // both capture passes and publication. A historical idle probe or observed
    // absence can never stand in for a currently acquired exact-build lease.
    internal static string CaptureActiveProof()
    {
        RequireAudit();
        RequireMainThread();
        ValidateActive();
        lock (Gate)
        {
            ThrowLatchedFault();
            if (!_acquisitionObserved || !_requiresLease || _active == null)
                throw new InvalidOperationException("Startup admission requires an active retained controller.");
            return _active.Fingerprint;
        }
    }

    internal static void Initialize(Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        // This ID belongs solely to this retained controller. Owner-scoped
        // cleanup must not unpatch other AAVE hooks or another owner that
        // happens to register the same managed monitor method.
        var monitorHarmony = new Harmony(harmony.Id + ".aaskipintro-boundary");
        lock (Gate)
        {
            if (_harmony != null) throw new InvalidOperationException("Startup boundary is already initialized.");
            _mainThread = Environment.CurrentManagedThreadId;
            _harmony = monitorHarmony;
            _acquisitionObserved = false;
            _requiresLease = false;
            _fault = null;
        }
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        bool patchAttempted = false;
        try
        {
            var methods = typeof(CreatorSplash).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.Name == "Awake").ToArray();
            if (methods.Length != 1 || methods[0].IsStatic || methods[0].ReturnType != typeof(void) ||
                methods[0].ContainsGenericParameters || methods[0].GetParameters().Length != 0)
                throw new NotSupportedException("CreatorSplash.Awake signature is not the inspected native boundary.");
            _awake = methods[0];
            if (Harmony.GetPatchInfo(_awake)?.Prefixes.Any(p => p.PatchMethod == Monitor || p.owner == monitorHarmony.Id) == true)
                throw new NotSupportedException("A startup boundary monitor is already installed.");
            patchAttempted = true;
            monitorHarmony.Patch(_awake, prefix: new HarmonyMethod(Monitor) { priority = Priority.First });
            EnsureMonitor();
        }
        catch (Exception failure)
        {
            try
            {
                if (patchAttempted && _awake != null) monitorHarmony.Unpatch(_awake, HarmonyPatchType.Prefix, monitorHarmony.Id);
                ClearInitialization();
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Startup monitor installation and rollback failed.", failure, rollback);
            }
            throw;
        }
    }

    internal static IDisposable? TryAcquire()
    {
        RequireMainThread();
        BeginAdmission();
        try
        {
            var assemblies = LoadedAssemblies();
            if (assemblies.Length == 0)
            {
                lock (Gate) { _acquisitionObserved = true; _requiresLease = false; }
                return null;
            }
            if (assemblies.Length != 1) throw new NotSupportedException("Multiple AASkipIntro assemblies are loaded.");
            if (!ExportBuildProfile.DevelopmentAuditEnabled) return AcquireExecutionBoundary();
            var assembly = assemblies[0];
            ValidateIdentity(assembly);
            var postfix = ResolvePostfix(assembly.GetType("AASkipIntro.CreatorSplashPatch", throwOnError: true)!);
            string fingerprint = AssemblyName + "|" + SupportedVersion + "|" + SupportedModule + "|" + SupportedHash;
            return AcquireValidatedBoundary(fingerprint, () => ValidateBoundAssembly(assembly, postfix));
        }
        catch (Exception error) { Latch(error); throw; }
    }

    // Shared retained-lifetime seam. The production caller above supplies the
    // exact-build validator; managed Harmony fixtures exercise this same lease
    // engine without substituting an unknown assembly in production admission.
    internal static IDisposable AcquireValidatedBoundary(string fingerprint, Action validateBuildAndPatches)
    {
        RequireAudit();
        RequireMainThread();
        ArgumentNullException.ThrowIfNull(validateBuildAndPatches);
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Startup build fingerprint is empty.", nameof(fingerprint));
        BeginAdmission();
        try
        {
            validateBuildAndPatches();
            EnsureNoActiveSplash();
            var lease = new Lease(fingerprint, validateBuildAndPatches);
            lock (Gate)
            {
                _active = lease;
                _acquisitionObserved = true;
                _requiresLease = true;
            }
            return lease;
        }
        catch (Exception error) { Latch(error); throw; }
    }

    internal static IDisposable AcquireExecutionBoundary()
    {
        RequireMainThread();
        if (ExportBuildProfile.DevelopmentAuditEnabled) throw new InvalidOperationException("DevelopmentAudit requires its explicit startup proof path.");
        BeginAdmission();
        try
        {
            EnsureNoActiveSplash();
            var lease = new Lease("", null);
            lock (Gate) { _active = lease; _acquisitionObserved = true; _requiresLease = true; }
            return lease;
        }
        catch (Exception error) { Latch(error); throw; }
    }
    private static void RequireAudit()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) throw new InvalidOperationException("Startup provenance proofs are available only in DevelopmentAudit.");
    }
    internal static void ValidateActive()
    {
        RequireMainThread();
        lock (Gate)
        {
            var lease = _active;
            bool observedAbsence = _acquisitionObserved && !_requiresLease;
            if (lease == null && !observedAbsence) return;
            ThrowLatchedFault();
            // A publication worker must not consume the previous proof while
            // these main-thread live checks are still in progress.
            try
            {
                if (lease != null)
                {
                    if (ExportBuildProfile.DevelopmentAuditEnabled) lease.ValidateBuildAndPatches!();
                    EnsureNoActiveSplash();
                }
                else if (ExportBuildProfile.DevelopmentAuditEnabled && LoadedAssemblies().Length != 0)
                    throw new NotSupportedException("AASkipIntro loaded after the export acquired an absent-Mod boundary.");
            }
            catch (Exception error) { _fault ??= error; throw; }
        }
    }

    // ExportSession's final-output worker can only consume retained state.
    // All Unity, file identity, reflection, and patch inspection happens above
    // on the host thread; a recorded fault cannot be healed before commit.
    internal static void ValidateForCommit()
    {
        RequireAudit();
        lock (Gate)
        {
            if (_harmony == null || !_acquisitionObserved)
                throw new InvalidOperationException("Startup boundary has no completed export acquisition.");
            ThrowLatchedFault();
            if (_requiresLease && _active == null)
                throw new InvalidOperationException("Startup boundary lease was released before output commit.");
        }
    }

    // Publication is synchronous: the acquiring worker must dispose this
    // scope on the same thread after its final validation and file moves.
    // The monitor can then admit a waiting Awake without suppressing it.
    internal static IDisposable AcquireCommitScope()
    {
        RequireAudit();
        System.Threading.Monitor.Enter(Gate);
        try
        {
            ValidateForCommit();
            return new CommitScope(Environment.CurrentManagedThreadId);
        }
        catch
        {
            System.Threading.Monitor.Exit(Gate);
            throw;
        }
    }

    // A null TryAcquire result still owns an observed absence for this export.
    // The host ends that scope explicitly so a late worker cannot reuse it.
    internal static void ReleaseAbsentSnapshot()
    {
        RequireMainThread();
        lock (Gate)
        {
            if (_active != null || (_acquisitionObserved && _requiresLease))
                throw new InvalidOperationException("Absent startup snapshot release cannot release a present-Mod boundary.");
            _acquisitionObserved = false;
        }
    }

    internal static bool IsSupportedBuild(Version version, Guid module, string hash) =>
        version == SupportedVersion && module == SupportedModule && string.Equals(hash, SupportedHash, StringComparison.OrdinalIgnoreCase);

    private static Assembly[] LoadedAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static void ValidateIdentity(Assembly assembly)
    {
        RequireMainThread();
        if (!string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal) ||
            assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            throw new NotSupportedException("AASkipIntro assembly identity or location is unavailable.");
        using var stream = File.OpenRead(assembly.Location);
        using var sha256 = SHA256.Create();
        string hash = Convert.ToHexString(sha256.ComputeHash(stream));
        if (!IsSupportedBuild(assembly.GetName().Version ?? new Version(0, 0), assembly.ManifestModule.ModuleVersionId, hash))
            throw new NotSupportedException("AASkipIntro version, MVID, or file hash is not the inspected 0.1.0 build.");
    }

    internal static MethodInfo ResolvePostfix(Type patchType)
    {
        RequireMainThread();
        if (patchType.FullName != "AASkipIntro.CreatorSplashPatch" || !patchType.IsAbstract || !patchType.IsSealed || patchType.ContainsGenericParameters)
            throw new NotSupportedException("AASkipIntro patch type changed.");
        var methods = patchType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.Name == "Postfix").ToArray();
        if (methods.Length != 1) throw new NotSupportedException("AASkipIntro postfix is missing or ambiguous.");
        var method = methods[0];
        var parameters = method.GetParameters();
        if (!method.IsStatic || !method.IsAssembly || method.ReturnType != typeof(void) || method.ContainsGenericParameters ||
            parameters.Length != 1 || parameters[0].ParameterType != typeof(CreatorSplash) || parameters[0].Name != "__instance")
            throw new NotSupportedException("AASkipIntro postfix signature changed.");
        return method;
    }

    private static void ValidateBoundAssembly(Assembly assembly, MethodInfo postfix)
    {
        RequireMainThread();
        var assemblies = LoadedAssemblies();
        if (assemblies.Length != 1 || assemblies[0] != assembly)
            throw new NotSupportedException("The retained AASkipIntro assembly binding changed.");
        ValidateIdentity(assembly);
        if (ResolvePostfix(assembly.GetType("AASkipIntro.CreatorSplashPatch", throwOnError: true)!) != postfix)
            throw new NotSupportedException("The retained AASkipIntro postfix binding changed.");
        var plugin = assembly.GetType("AASkipIntro.Plugin", throwOnError: true)!;
        var id = plugin.GetField("Id", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (!plugin.IsSealed || id == null || !id.IsLiteral || id.FieldType != typeof(string) ||
            !string.Equals(id.GetRawConstantValue() as string, ModOwner, StringComparison.Ordinal))
            throw new NotSupportedException("AASkipIntro plugin ownership contract changed.");
        ValidatePatchState(postfix);
    }

    internal static void ValidatePatchState(MethodInfo postfix)
    {
        RequireAudit();
        RequireMainThread();
        EnsureMonitor();
        var info = Harmony.GetPatchInfo(_awake!)!;
        if (info.Prefixes.Count != 1 || info.Postfixes.Count != 1 || info.Transpilers.Count != 0 || info.Finalizers.Count != 0 || info.ILManipulators.Count != 0 ||
            info.Postfixes[0].owner != ModOwner || info.Postfixes[0].PatchMethod != postfix)
            throw new NotSupportedException("CreatorSplash.Awake does not have the inspected AASkipIntro patch ownership and shape.");
        var patchedPostfix = Harmony.GetPatchInfo(postfix);
        if (patchedPostfix != null && (patchedPostfix.Prefixes.Count != 0 || patchedPostfix.Postfixes.Count != 0 ||
            patchedPostfix.Transpilers.Count != 0 || patchedPostfix.Finalizers.Count != 0 || patchedPostfix.ILManipulators.Count != 0))
            throw new NotSupportedException("The inspected AASkipIntro postfix is itself patched.");
    }

    private static void EnsureMonitor()
    {
        var info = _awake == null ? null : Harmony.GetPatchInfo(_awake);
        if (info == null || info.Prefixes.Count(p => p.PatchMethod == Monitor && p.owner == _harmony!.Id) != 1 ||
            info.Prefixes.Any(p => p.PatchMethod == Monitor && p.owner != _harmony!.Id))
            throw new NotSupportedException("The retained AAVE startup monitor is missing or has a different owner.");
    }

    private static void EnsureNoActiveSplash()
    {
        foreach (var splash in Resources.FindObjectsOfTypeAll<CreatorSplash>())
            if (splash != null && splash.gameObject != null && splash.gameObject.activeInHierarchy)
                throw new InvalidOperationException("An active typed CreatorSplash exists during the retained export boundary.");
    }

    // A void Harmony prefix never skips native Awake or AASkipIntro's postfix.
    // Do not throw here: even an invalid export must preserve native startup.
    private static void OnCreatorSplashAwake()
    {
        lock (Gate)
            if (_active != null)
                _fault ??= new InvalidOperationException("CreatorSplash.Awake was invoked during the retained export boundary.");
    }

    private static void BeginAdmission()
    {
        lock (Gate)
        {
            if (_active != null) throw new InvalidOperationException("A startup boundary lease is already active.");
            _acquisitionObserved = false;
            _requiresLease = true;
            _fault = null;
        }
    }

    private static void Latch(Exception error) { lock (Gate) _fault ??= error; }
    private static void ThrowLatchedFault()
    {
        if (_fault != null) throw new InvalidOperationException("The retained startup boundary was invalidated: " + _fault.Message, _fault);
    }

    private static void RequireMainThread()
    {
        if (_harmony == null || _mainThread == 0 || Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("Startup boundary lifecycle requires the initialized Unity main thread.");
    }

    internal static void Shutdown()
    {
        if (_harmony == null) return;
        RequireMainThread();
        var errors = new List<Exception>();
        try { _active?.Dispose(); } catch (Exception error) { errors.Add(error); }
        try
        {
            if (_awake != null)
            {
                _harmony.Unpatch(_awake, HarmonyPatchType.Prefix, _harmony.Id);
                if (Harmony.GetPatchInfo(_awake)?.Prefixes.Any(p => p.owner == _harmony.Id) == true)
                    throw new InvalidOperationException("The owned startup monitor remained installed during shutdown.");
            }
            ClearInitialization();
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Startup boundary shutdown failed validation or monitor cleanup.", errors);
    }

    private static void ClearInitialization()
    {
        lock (Gate)
        {
            _harmony = null;
            _awake = null;
            _mainThread = 0;
            _acquisitionObserved = false;
            _requiresLease = false;
            _fault = null;
        }
    }

    private sealed class CommitScope : IDisposable
    {
        private readonly int _ownerThread;
        private bool _released;
        internal CommitScope(int ownerThread) { _ownerThread = ownerThread; }

        public void Dispose()
        {
            // Never wait for Gate here: a wrong-thread caller would otherwise
            // block the owning worker instead of reporting misuse promptly.
            if (Environment.CurrentManagedThreadId != _ownerThread)
                throw new InvalidOperationException("Startup commit scope must be released by its acquiring thread.");
            if (_released) return;
            System.Threading.Monitor.Exit(Gate);
            _released = true;
        }
    }

    private sealed class Lease : IDisposable
    {
        internal readonly string Fingerprint;
        internal readonly Action? ValidateBuildAndPatches;
        private bool _released;
        internal Lease(string fingerprint, Action? validateBuildAndPatches)
        { Fingerprint = fingerprint; ValidateBuildAndPatches = validateBuildAndPatches; }

        public void Dispose()
        {
            lock (Gate) if (_released) return;
            RequireMainThread();
            lock (Gate)
                if (!ReferenceEquals(_active, this))
                    throw new InvalidOperationException("Startup boundary lease does not own the current export.");
            try { ValidateActive(); }
            finally
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_active, this)) _active = null;
                    _released = true;
                }
            }
        }
    }
}
