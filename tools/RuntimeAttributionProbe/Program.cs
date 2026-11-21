using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RuntimeAttributionProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--inspect-host")
        {
            Console.WriteLine(JsonSerializer.Serialize(HostSeamInspector.Inspect(args.Length > 1
                ? args[1] : @"F:\AzureArchive_101"), Json));
            return 0;
        }

        var checks = new List<object>();
        var failed = 0;
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            if (!ok) failed++;
        }

        var assemblyA = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "AAVE.Probe.ProducerA.dll"));
        var assemblyB = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "AAVE.Probe.ProducerB.dll"));
        var factoryA = assemblyA.GetType("FixtureProducer.Factory")!.GetMethod("Build")!;
        var factoryB = assemblyB.GetType("FixtureProducer.Factory")!.GetMethod("Build")!;
        (DynamicMethod method, Func<int, int> call) Build(MethodInfo factory, params object[] arguments)
        {
            var result = (object[])factory.Invoke(null, arguments)!;
            return ((DynamicMethod)result[0], (Func<int, int>)result[1]);
        }

        // Warm the anonymous holder and fixture paths before instrumentation.
        var baselineA = Build(factoryA, 3);
        var baselineB = Build(factoryB, 9);
        var overloadFactory = assemblyA.GetType("FixtureProducer.Factory")!.GetMethod("BuildWithConstructor")!;
        var baselineOverloads = Enumerable.Range(0, 8).Select(v => Build(overloadFactory, v, 30 + v)).ToArray();
        var boundFactory = assemblyB.GetType("FixtureProducer.Factory")!.GetMethod("BuildBound")!;
        var baselineBound = Build(boundFactory, 23);
        using var observer = new RuntimeObserver();
        Exception? instrumentationFailure = null;
        try { if (!args.Contains("--without-hooks")) observer.Install(); }
        catch (Exception ex) { instrumentationFailure = ex; }

        var actualA = Build(factoryA, 3);
        var actualB = Build(factoryB, 9);
        var recordA = observer.Find(actualA.method);
        var recordB = observer.Find(actualB.method);
        Check("constructor_attribution_A", recordA?.ProducerAssembly == assemblyA.GetName().Name, recordA);
        Check("constructor_attribution_B", recordB?.ProducerAssembly == assemblyB.GetName().Name, recordB);
        Check("producer_method_captured", recordA?.ProducerMethod.Contains("FixtureProducer.Factory.Build") == true &&
            recordB?.ProducerMethod.Contains("FixtureProducer.Factory.Build") == true);
        Check("same_holder_distinct_identity", ReferenceEquals(actualA.method.Module.Assembly, actualB.method.Module.Assembly) &&
            actualA.method.Name == actualB.method.Name && recordA is not null && recordB is not null && recordA.Id != recordB.Id,
            new { holder = actualA.method.Module.Assembly.FullName, idA = recordA?.Id, idB = recordB?.Id });
        Check("materialization_captured", recordA?.MaterializationEvents == 1 && recordB?.MaterializationEvents == 1);
        Check("delegate_output_unchanged", Enumerable.Range(-100, 201).All(i => actualA.call(i) == baselineA.call(i) &&
            actualB.call(i) == baselineB.call(i)));
        Check("preexisting_methods_not_falsely_claimed", observer.Find(baselineA.method) is null && observer.Find(baselineB.method) is null);

        var loadedDuringLate = new List<string>();
        AssemblyLoadEventHandler handler = (_, e) => loadedDuringLate.Add(e.LoadedAssembly.FullName ?? "?");
        AppDomain.CurrentDomain.AssemblyLoad += handler;
        var late = Build(factoryA, 17);
        AppDomain.CurrentDomain.AssemblyLoad -= handler;
        var lateRecord = observer.Find(late.method);
        Check("late_creation_without_assemblyload", loadedDuringLate.Count == 0 && lateRecord is not null &&
            lateRecord.MaterializationEvents == 1, new { loadedDuringLate, attribution = lateRecord });
        Check("late_delegate_output", Enumerable.Range(-100, 201).All(i => late.call(i) == i + 17));

        var overloadResults = Enumerable.Range(0, 8).Select(v =>
        {
            var created = Build(overloadFactory, v, 30 + v);
            var record = observer.Find(created.method);
            return new { variant = v, record, outputEqual = Enumerable.Range(-100, 201)
                .All(i => baselineOverloads[v].call(i) == created.call(i)) };
        }).ToArray();
        Check("all_public_constructor_overloads_observed", overloadResults.All(r =>
            r.record?.ConstructorEvents >= 1 && r.record.ProducerAssembly == assemblyA.GetName().Name), overloadResults);
        Check("all_constructor_overload_outputs_unchanged", overloadResults.All(r => r.outputEqual));
        var bound = Build(boundFactory, 23);
        var boundRecord = observer.Find(bound.method);
        Check("bound_delegate_overload_observed", boundRecord?.MaterializationEvents == 1, boundRecord);
        Check("bound_delegate_output_unchanged", Enumerable.Range(-100, 201).All(i => bound.call(i) == baselineBound.call(i)));
        Check("returned_delegates_correlate_to_method_identity", recordA is not null && recordB is not null && boundRecord is not null &&
            observer.FindDelegate(actualA.call)?.Id == recordA.Id && observer.FindDelegate(actualB.call)?.Id == recordB.Id &&
            observer.FindDelegate(bound.call)?.Id == boundRecord.Id);

        // Seeing a late CreateDelegate must never invent who originally constructed the method.
        baselineA.method.CreateDelegate(typeof(Func<int, int>));
        var preexistingRecord = observer.Find(baselineA.method);
        Check("preexisting_materialization_retains_unknown_creator", preexistingRecord is not null &&
            preexistingRecord.ConstructorEvents == 0 && preexistingRecord.MaterializationEvents == 1 &&
            preexistingRecord.ProducerAssembly == "UNKNOWN", preexistingRecord);

        var directFactory = assemblyA.GetType("FixtureProducer.Factory")!.GetMethod("BuildWithoutDelegate")!;
        var direct = (DynamicMethod)directFactory.Invoke(null, new object[] { 40 })!;
        var directResult = (int)direct.Invoke(null, new object[] { 2 })!;
        var directRecord = observer.Find(direct);
        Check("direct_invoke_output", directResult == 42);
        Check("direct_invoke_materialization_is_explicit_gap", directRecord?.ConstructorEvents >= 1 &&
            directRecord.MaterializationEvents == 0, directRecord);
        Check("no_callback_errors", observer.CallbackFailures == 0);
        observer.Dispose();
        var afterRemoval = Build(factoryA, 31);
        Check("patch_removal_verified", observer.OwnedPatchesRemaining == 0 &&
            observer.Find(afterRemoval.method) is null && afterRemoval.call(11) == 42);

        var report = new
        {
            schema = "aave.runtime-attribution.feasibility.v1",
            timestampUtc = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            runtimeVersion = Environment.Version.ToString(),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            harmony = typeof(HarmonyLib.Harmony).Assembly.FullName,
            hooksRequested = !args.Contains("--without-hooks"),
            instrumentationFailure = instrumentationFailure?.ToString(),
            observer.PatchAttempts,
            observer.ReentrantCallbacksSkipped,
            observer.CallbackFailures,
            delegateComparisons = 2412,
            checks,
            failed,
            verdict = failed == 0 ? "PASS_BOUNDED_FIXTURES_ONLY" : "FAIL_ATTRIBUTION_PROBE"
        };
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return failed == 0 ? 0 : 1;
    }
}
