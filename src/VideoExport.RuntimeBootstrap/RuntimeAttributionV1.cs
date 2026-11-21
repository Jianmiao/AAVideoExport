using System.Text.Json;

namespace AAVideoExport.RuntimeBootstrap;

/// <summary>Opt-in diagnostic evidence only. No result from this API certifies strict export safety.</summary>
public static class RuntimeAttributionV1
{
    private static readonly object InitializationGate = new();
    private static RuntimeObserver? observer;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static bool WasRequested { get; private set; }
    public static int ProtocolVersion => 1;
    public static bool IsInstalled => Volatile.Read(ref observer)?.IsInstalled == true;
    public static string CoverageStartedUtc => Volatile.Read(ref observer)?.CoverageStartedUtc ?? "";

    public static string GetSnapshotJson()
    {
        var current = Volatile.Read(ref observer);
        // Snapshot copies are made under the collector lock; JSON work runs after release.
        return JsonSerializer.Serialize(current?.Snapshot() ?? ObserverSnapshot.Disabled(), Json);
    }

    public static string GetAttributionJson(Delegate value)
    {
        var current = Volatile.Read(ref observer);
        var record = value is null ? null : current?.FindDelegate(value);
        return JsonSerializer.Serialize(new DelegateSnapshot("aave.runtime-attribution.delegate.v1",
            record is null ? "UNKNOWN" : "OBSERVED", false, "STACK_OBSERVATION_ONLY",
            current?.CoverageStartedUtc ?? "", record), Json);
    }

    public static void Mark(string name) => Volatile.Read(ref observer)?.Mark(name);

    internal static void InitializeFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION") != "1") return;
        lock (InitializationGate)
        {
            WasRequested = true;
            if (observer is not null) return;
            var candidate = new RuntimeObserver();
            // Warm serializer metadata before capture, including non-null record shapes.
            JsonSerializer.Serialize(candidate.Snapshot(), Json);
            JsonSerializer.Serialize(new DelegateSnapshot("aave.runtime-attribution.delegate.v1", "UNKNOWN",
                false, "STACK_OBSERVATION_ONLY", "", null), Json);
            Volatile.Write(ref observer, candidate);
            candidate.Install();
        }
    }
}

internal sealed record DelegateSnapshot(string Schema, string Status, bool PermitsStrictExport,
    string EvidenceKind, string CoverageStartedUtc, AttributionSnapshot? Record);
