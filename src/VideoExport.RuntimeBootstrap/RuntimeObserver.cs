using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace AAVideoExport.RuntimeBootstrap;

internal sealed record ObserverLimits(int Records = 4096, int Delegates = 8192, int Markers = 256,
    int StackFrames = 32, int TextCharacters = 512, int StartAssemblies = 512);

internal interface IRuntimeHooks
{
    void Patch(MethodBase target, MethodInfo callback, bool prefix);
    void UnpatchAll();
    int OwnedPatchCount { get; }
}

internal sealed class HarmonyRuntimeHooks : IRuntimeHooks
{
    private readonly Harmony harmony = new("com.aavideoexport.runtime-attribution.v1");
    public void Patch(MethodBase target, MethodInfo callback, bool prefix)
    {
        var patch = new HarmonyMethod(callback);
        if (prefix) harmony.Patch(target, prefix: patch);
        else harmony.Patch(target, postfix: patch);
    }
    public void UnpatchAll() => harmony.UnpatchSelf();
    public int OwnedPatchCount => harmony.GetPatchedMethods().Count();
}

internal sealed class RuntimeObserver : IDisposable
{
    private static readonly object LifecycleGate = new();
    private static RuntimeObserver? active;
    [ThreadStatic] private static bool insideCallback;
    private readonly object sync = new();
    private readonly ObserverLimits limits;
    private readonly IRuntimeHooks hooks;
    private readonly ConditionalWeakTable<DynamicMethod, AttributionRecord> methods = new();
    private readonly ConditionalWeakTable<Delegate, AttributionRecord> delegates = new();
    private readonly List<AttributionRecord> records = new();
    private readonly List<MarkerSnapshot> markers = new();
    private readonly List<TargetSnapshot> targets = new();
    private readonly RuntimeSnapshot runtime;
    private string[] startAssemblies = Array.Empty<string>();
    private string coverageStartedUtc = "";
    private string installationError = "";
    private bool installed;
    private bool attempted;
    private bool disposed;
    private int delegateAdmissions;
    private long nextId;
    private long droppedRecordEvents, droppedDelegateAssociations, droppedMarkers, truncatedStacks, truncatedText;
    private long reentrantCallbacksSkipped, callbackFailures, installFailures, rollbackFailures;
    private long droppedStartAssemblies;

    internal RuntimeObserver(ObserverLimits? limits = null, IRuntimeHooks? hooks = null)
    {
        this.limits = limits ?? new ObserverLimits();
        if (this.limits.Records < 1 || this.limits.Delegates < 1 || this.limits.Markers < 1 ||
            this.limits.StackFrames < 1 || this.limits.TextCharacters < 16 || this.limits.StartAssemblies < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.hooks = hooks ?? new HarmonyRuntimeHooks();
        runtime = RuntimeSnapshot.Current();
    }

    internal bool IsInstalled => Volatile.Read(ref installed);
    internal string CoverageStartedUtc => Volatile.Read(ref coverageStartedUtc);

    internal void Install()
    {
        StartupTrace.Write("v1.Install.enter");
        lock (LifecycleGate)
        {
            if (attempted || disposed) return;
            attempted = true;
            if (active is not null)
            {
                Interlocked.Increment(ref installFailures);
                installationError = "Another runtime observer is already active.";
                return;
            }
            Volatile.Write(ref active, this);
            try
            {
                var constructorCallback = Callback(nameof(ConstructorPostfix));
                var delegateCallback = Callback(nameof(CreateDelegatePostfix));
                var invokeCallback = Callback(nameof(InvokePrefix));
                foreach (var constructor in typeof(DynamicMethod).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    Patch(constructor, constructorCallback, "constructor", false);
                foreach (var method in typeof(DynamicMethod).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == nameof(DynamicMethod.CreateDelegate) && !m.IsGenericMethod))
                    Patch(method, delegateCallback, "createDelegate", false);
                foreach (var method in typeof(DynamicMethod).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == nameof(DynamicMethod.Invoke)))
                    Patch(method, invokeCallback, "invokeAttempt", true);
                if (targets.Count(t => t.Kind == "constructor") != 8 ||
                    targets.Count(t => t.Kind == "createDelegate") != 2 || targets.Count(t => t.Kind == "invokeAttempt") != 1)
                    throw new NotSupportedException("Unexpected DynamicMethod surface; complete requested hook installation was not established.");

                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                Interlocked.Exchange(ref droppedStartAssemblies, Math.Max(0, loaded.Length - limits.StartAssemblies));
                var assemblies = loaded.Select(a => Clip(a.FullName ?? "UNKNOWN"))
                    .OrderBy(n => n, StringComparer.Ordinal).Take(limits.StartAssemblies).ToArray();
                var started = DateTimeOffset.UtcNow.ToString("O");
                lock (sync)
                {
                    startAssemblies = assemblies;
                    coverageStartedUtc = started;
                    Volatile.Write(ref installed, true);
                }
                StartupTrace.Write("v1.Install.complete");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("v1.Install.error", ex.GetType().FullName ?? "Exception");
                Volatile.Write(ref installed, false);
                Interlocked.Increment(ref installFailures);
                var error = Clip(ex.GetType().FullName + ": " + ex.Message);
                lock (sync) installationError = error;
                RollBack();
                Volatile.Write(ref active, null);
            }
        }
    }

    private void Patch(MethodBase target, MethodInfo callback, string kind, bool prefix)
    {
        var signature = Clip(target.ToString() ?? "UNKNOWN");
        try
        {
            StartupTrace.WriteTarget("v1.Harmony.Patch.before", target);
            hooks.Patch(target, callback, prefix);
            StartupTrace.WriteTarget("v1.Harmony.Patch.after", target);
            lock (sync) targets.Add(new(kind, signature, true, ""));
        }
        catch (Exception ex)
        {
            StartupTrace.WriteTarget("v1.Harmony.Patch.error", target);
            var error = Clip(ex.GetType().FullName + ": " + ex.Message);
            lock (sync) targets.Add(new(kind, signature, false, error));
            throw;
        }
    }

    private static MethodInfo Callback(string name) => typeof(RuntimeObserver).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConstructorPostfix(DynamicMethod __instance) => Capture(__instance, EventKind.Construction, null);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateDelegatePostfix(DynamicMethod __instance, Delegate __result) => Capture(__instance, EventKind.CreateDelegate, __result);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokePrefix(DynamicMethod __instance) => Capture(__instance, EventKind.InvokeAttempt, null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Capture(DynamicMethod method, EventKind kind, Delegate? result)
    {
        var observer = Volatile.Read(ref active);
        if (observer is null || !observer.IsInstalled) return;
        if (insideCallback) { Interlocked.Increment(ref observer.reentrantCallbacksSkipped); return; }
        insideCallback = true;
        try
        {
            // Do not hold sync while walking stacks, reading reflection metadata, or calling any external code.
            var evidence = observer.ReadEvidence();
            var name = observer.Clip(method.Name);
            var holder = observer.Clip(method.Module.Assembly.FullName ?? "UNKNOWN");
            var delegateMethodType = result is null ? "" : observer.Clip(result.Method.GetType().FullName ?? "UNKNOWN");
            var delegateMethodName = result is null ? "" : observer.Clip(result.Method.Name);
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            lock (observer.sync)
            {
                if (!observer.installed) return;
                if (!observer.methods.TryGetValue(method, out var record))
                {
                    if (observer.records.Count >= observer.limits.Records)
                    {
                        Interlocked.Increment(ref observer.droppedRecordEvents);
                        if (result is not null) Interlocked.Increment(ref observer.droppedDelegateAssociations);
                        return;
                    }
                    record = new AttributionRecord(++observer.nextId, name, holder, timestamp);
                    observer.methods.Add(method, record);
                    observer.records.Add(record);
                }
                if (kind == EventKind.Construction)
                {
                    record.ConstructorEvents++;
                    if (record.ConstructorEvents == 1)
                    {
                        record.CreatorAssembly = evidence.CallerAssembly;
                        record.CreatorMethod = evidence.CallerMethod;
                        record.CreationStack = evidence.Frames;
                    }
                }
                else if (kind == EventKind.CreateDelegate)
                {
                    record.CreateDelegateEvents++;
                    record.MaterializationStack = evidence.Frames;
                    record.DelegateMethodType = delegateMethodType;
                    record.DelegateMethodName = delegateMethodName;
                    if (result is not null && !observer.delegates.TryGetValue(result, out _))
                    {
                        if (observer.delegateAdmissions >= observer.limits.Delegates)
                            Interlocked.Increment(ref observer.droppedDelegateAssociations);
                        else
                        {
                            observer.delegates.Add(result, record);
                            observer.delegateAdmissions++;
                        }
                    }
                }
                else
                {
                    record.InvokeEvents++;
                    record.InvocationStack = evidence.Frames;
                }
            }
        }
        catch { Interlocked.Increment(ref observer.callbackFailures); }
        finally { insideCallback = false; }
    }

    private StackEvidence ReadEvidence()
    {
        var trace = new StackTrace(false);
        if (trace.FrameCount > limits.StackFrames) Interlocked.Increment(ref truncatedStacks);
        var frames = new List<string>(Math.Min(trace.FrameCount, limits.StackFrames));
        string callerAssembly = "UNKNOWN", callerMethod = "UNKNOWN";
        for (var i = 0; i < Math.Min(trace.FrameCount, limits.StackFrames); i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            if (method is null) continue;
            var assembly = method.Module.Assembly;
            var assemblyName = assembly.GetName().Name ?? "UNKNOWN";
            var methodName = (method.DeclaringType?.FullName ?? "<dynamic>") + "." + method.Name;
            frames.Add(Clip(assemblyName + "!" + methodName));
            if (callerAssembly == "UNKNOWN" && IsCallerCandidate(assembly, assemblyName))
            {
                callerAssembly = Clip(assemblyName);
                callerMethod = Clip(methodName);
            }
        }
        return new StackEvidence(callerAssembly, callerMethod, frames.ToArray());
    }

    private static bool IsCallerCandidate(Assembly assembly, string name) =>
        !assembly.IsDynamic && assembly != typeof(RuntimeObserver).Assembly && assembly != typeof(object).Assembly &&
        !name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("MonoMod.", StringComparison.Ordinal) &&
        name != "0Harmony";

    private string Clip(string value)
    {
        if (value.Length <= limits.TextCharacters) return value;
        Interlocked.Increment(ref truncatedText);
        return value[..limits.TextCharacters];
    }

    internal AttributionSnapshot? FindDelegate(Delegate value)
    {
        lock (sync) return delegates.TryGetValue(value, out var record) ? record.Snapshot() : null;
    }

    internal ObserverSnapshot Snapshot()
    {
        lock (sync)
            return new ObserverSnapshot("aave.runtime-attribution.v1", installed, coverageStartedUtc, runtime,
                false, false, false, "STACK_OBSERVATION_ONLY", Array.AsReadOnly((string[])startAssemblies.Clone()),
                Array.AsReadOnly(targets.ToArray()), Array.AsReadOnly(records.Select(r => r.Snapshot()).ToArray()),
                Array.AsReadOnly(markers.ToArray()), new LossSnapshot(
                    Interlocked.Read(ref droppedRecordEvents), Interlocked.Read(ref droppedDelegateAssociations),
                    Interlocked.Read(ref droppedMarkers), Interlocked.Read(ref reentrantCallbacksSkipped),
                    Interlocked.Read(ref callbackFailures), Interlocked.Read(ref installFailures),
                    Interlocked.Read(ref rollbackFailures), Interlocked.Read(ref truncatedStacks), Interlocked.Read(ref truncatedText),
                    Interlocked.Read(ref droppedStartAssemblies)),
                limits, installationError, "Invoke events count attempts, including calls that throw; delegate execution is not intercepted.");
    }

    internal void Mark(string name)
    {
        if (!IsInstalled) return;
        try
        {
            var label = Clip(name ?? "");
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            lock (sync)
            {
                if (!installed) return;
                if (markers.Count >= limits.Markers) { Interlocked.Increment(ref droppedMarkers); return; }
                markers.Add(new MarkerSnapshot(label, timestamp));
            }
        }
        catch { Interlocked.Increment(ref callbackFailures); }
    }

    private void RollBack()
    {
        try
        {
            hooks.UnpatchAll();
            if (hooks.OwnedPatchCount != 0) throw new InvalidOperationException("Owned hooks remain after rollback.");
            lock (sync)
                for (var i = 0; i < targets.Count; i++) targets[i] = targets[i] with { Installed = false };
        }
        catch { Interlocked.Increment(ref rollbackFailures); }
    }

    public void Dispose()
    {
        lock (LifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            Volatile.Write(ref installed, false);
            if (ReferenceEquals(Volatile.Read(ref active), this))
            {
                RollBack();
                Volatile.Write(ref active, null);
            }
        }
    }

    private enum EventKind { Construction, CreateDelegate, InvokeAttempt }
    private sealed record StackEvidence(string CallerAssembly, string CallerMethod, string[] Frames);
}

internal sealed class AttributionRecord
{
    internal readonly long Id;
    internal readonly string Name, Holder, FirstObservedUtc;
    internal string CreatorAssembly = "UNKNOWN", CreatorMethod = "UNKNOWN";
    internal long ConstructorEvents, CreateDelegateEvents, InvokeEvents;
    internal string[] CreationStack = Array.Empty<string>(), MaterializationStack = Array.Empty<string>(), InvocationStack = Array.Empty<string>();
    internal string DelegateMethodType = "", DelegateMethodName = "";
    internal AttributionRecord(long id, string name, string holder, string firstObservedUtc) =>
        (Id, Name, Holder, FirstObservedUtc) = (id, name, holder, firstObservedUtc);
    internal AttributionSnapshot Snapshot() => new(Id, Name, Holder, FirstObservedUtc, CreatorAssembly, CreatorMethod,
        ConstructorEvents, CreateDelegateEvents, InvokeEvents, Array.AsReadOnly((string[])CreationStack.Clone()),
        Array.AsReadOnly((string[])MaterializationStack.Clone()), Array.AsReadOnly((string[])InvocationStack.Clone()),
        DelegateMethodType, DelegateMethodName);
}

internal sealed record AttributionSnapshot(long Id, string Name, string Holder, string FirstObservedUtc,
    string CreatorAssembly, string CreatorMethod, long ConstructorEvents, long CreateDelegateEvents, long InvokeEvents,
    IReadOnlyList<string> CreationStack, IReadOnlyList<string> MaterializationStack, IReadOnlyList<string> InvocationStack,
    string DelegateMethodType, string DelegateMethodName);
internal sealed record MarkerSnapshot(string Name, string TimestampUtc);
internal sealed record TargetSnapshot(string Kind, string Signature, bool Installed, string Error);
internal sealed record LossSnapshot(long DroppedRecordEvents, long DroppedDelegateAssociations, long DroppedMarkers,
    long ReentrantCallbacksSkipped, long CallbackFailures, long InstallFailures, long RollbackFailures, long TruncatedStacks,
    long TruncatedText, long DroppedStartAssemblies);
internal sealed record RuntimeSnapshot(string Framework, string Version, string Architecture, string Harmony)
{
    internal static RuntimeSnapshot Current() => new(RuntimeInformation.FrameworkDescription, Environment.Version.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(), typeof(Harmony).Assembly.FullName ?? "UNKNOWN");
}
internal sealed record ObserverSnapshot(string Schema, bool Installed, string CoverageStartedUtc, RuntimeSnapshot Runtime,
    bool PreexistingCoverage, bool AllCodeCoverage, bool PermitsStrictExport, string EvidenceKind,
    IReadOnlyList<string> StartAssemblies, IReadOnlyList<TargetSnapshot> Targets, IReadOnlyList<AttributionSnapshot> Records,
    IReadOnlyList<MarkerSnapshot> Markers, LossSnapshot LossCounters, ObserverLimits Limits, string InstallationError, string InvocationSemantics)
{
    internal static ObserverSnapshot Disabled() => new("aave.runtime-attribution.v1", false, "", RuntimeSnapshot.Current(), false,
        false, false, "STACK_OBSERVATION_ONLY", Array.Empty<string>(), Array.Empty<TargetSnapshot>(),
        Array.Empty<AttributionSnapshot>(), Array.Empty<MarkerSnapshot>(), new(0,0,0,0,0,0,0,0,0,0), new(), "", "No observation installed.");
}
