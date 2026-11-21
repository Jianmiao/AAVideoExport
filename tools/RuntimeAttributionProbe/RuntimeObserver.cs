using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace RuntimeAttributionProbe;

internal sealed class RuntimeObserver : IDisposable
{
    private static RuntimeObserver? active;
    [ThreadStatic] private static bool insideCallback;
    private readonly Harmony harmony = new("aave.experimental.runtime-attribution-probe");
    private readonly ConditionalWeakTable<DynamicMethod, Attribution> methods = new();
    private readonly ConditionalWeakTable<Delegate, Attribution> delegates = new();
    private readonly object sync = new();
    private bool enabled;
    private bool disposed;
    private long nextId;
    public readonly List<object> PatchAttempts = new();
    public readonly List<Attribution> Records = new();
    public int ReentrantCallbacksSkipped { get; private set; }
    public int CallbackFailures { get; private set; }
    public int OwnedPatchesRemaining => harmony.GetPatchedMethods().Count();

    public void Install()
    {
        active = this;
        // Install before enabling capture, because Harmony emits DynamicMethods itself.
        foreach (var constructor in typeof(DynamicMethod).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            Patch(constructor, nameof(ConstructorPostfix));
        foreach (var method in typeof(DynamicMethod).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.Name == nameof(DynamicMethod.CreateDelegate) && !m.IsGenericMethod))
            Patch(method, nameof(MaterializationPostfix));
        enabled = true;
    }

    private void Patch(MethodBase target, string callback)
    {
        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(RuntimeObserver), callback));
            PatchAttempts.Add(new { target = target.ToString(), succeeded = true, error = (string?)null });
        }
        catch (Exception ex)
        {
            PatchAttempts.Add(new { target = target.ToString(), succeeded = false, error = ex.ToString() });
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConstructorPostfix(DynamicMethod __instance) => Capture(__instance, true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MaterializationPostfix(DynamicMethod __instance, Delegate __result) => Capture(__instance, false, __result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Capture(DynamicMethod method, bool construction, Delegate? result = null)
    {
        var observer = active;
        if (observer is null || !observer.enabled) return;
        if (insideCallback) { observer.ReentrantCallbacksSkipped++; return; }
        insideCallback = true;
        try
        {
            var stack = new StackTrace(false).GetFrames().Select(frame => frame.GetMethod())
                .Where(m => m is not null).Cast<MethodBase>().ToArray();
            var caller = stack.FirstOrDefault(m => IsProducerCandidate(m.Module.Assembly));
            var frames = stack.Select(m => $"{m.Module.Assembly.GetName().Name}!{m.DeclaringType?.FullName}.{m.Name}").ToArray();
            lock (observer.sync)
            {
                if (!observer.methods.TryGetValue(method, out var record))
                {
                    record = new Attribution
                    {
                        Id = ++observer.nextId,
                        Name = method.Name,
                        Holder = method.Module.Assembly.FullName ?? "?"
                    };
                    observer.methods.Add(method, record);
                    observer.Records.Add(record);
                }
                if (construction)
                {
                    record.ConstructorEvents++;
                    record.ProducerAssembly = caller?.Module.Assembly.GetName().Name ?? "UNKNOWN";
                    record.ProducerMethod = caller is null ? "UNKNOWN" : $"{caller.DeclaringType?.FullName}.{caller.Name}";
                    record.CreationStack = frames;
                }
                else
                {
                    record.MaterializationEvents++;
                    record.MaterializationStack = frames;
                    if (result is not null)
                    {
                        if (!observer.delegates.TryGetValue(result, out _)) observer.delegates.Add(result, record);
                        record.DelegateMethodType = result.Method.GetType().FullName ?? "?";
                        record.DelegateMethodName = result.Method.Name;
                    }
                }
            }
        }
        catch { observer.CallbackFailures++; }
        finally { insideCallback = false; }
    }

    private static bool IsProducerCandidate(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        return !assembly.IsDynamic && assembly != typeof(RuntimeObserver).Assembly &&
            assembly != typeof(object).Assembly && !name.StartsWith("System.", StringComparison.Ordinal) &&
            !name.StartsWith("MonoMod.", StringComparison.Ordinal) && name != "0Harmony";
    }

    public Attribution? Find(DynamicMethod method) => methods.TryGetValue(method, out var value) ? value : null;
    public Attribution? FindDelegate(Delegate value) => delegates.TryGetValue(value, out var record) ? record : null;

    public void Dispose()
    {
        if (disposed) return;
        enabled = false;
        harmony.UnpatchSelf();
        if (ReferenceEquals(active, this)) active = null;
        disposed = true;
    }
}

internal sealed class Attribution
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Holder { get; init; } = "";
    public string ProducerAssembly { get; set; } = "UNKNOWN";
    public string ProducerMethod { get; set; } = "UNKNOWN";
    public int ConstructorEvents { get; set; }
    public int MaterializationEvents { get; set; }
    public string[] CreationStack { get; set; } = Array.Empty<string>();
    public string[] MaterializationStack { get; set; } = Array.Empty<string>();
    public string DelegateMethodType { get; set; } = "";
    public string DelegateMethodName { get; set; } = "";
}
