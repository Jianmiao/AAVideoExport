using System.Text.Json;

namespace AAVideoExport.Plugin;

internal enum RuntimePlaneState
{
    Idle,
    Probed,
    Active,
    Restored,
    Failed
}

internal sealed class RuntimePlaneStatus
{
    internal string Name { get; }
    internal RuntimePlaneState State { get; private set; } = RuntimePlaneState.Idle;
    internal string Detail { get; private set; } = "";
    internal RuntimePlaneStatus(string name) => Name = name;
    internal void Set(RuntimePlaneState state, string detail = "")
    {
        State = state;
        Detail = detail;
    }
}

// Host for the four runtime-virtualization planes. Stage 1 keeps behavior
// unchanged: it records probe and lifecycle events so later stages can replace
// the compatibility stubs without losing forensic visibility.
internal static class RuntimeVirtualizationHost
{
    private static readonly object Gate = new();
    private static readonly RuntimePlaneStatus Clock = new("clock");
    private static readonly RuntimePlaneStatus Audio = new("audio");
    private static readonly RuntimePlaneStatus Render = new("render");
    private static readonly RuntimePlaneStatus Scene = new("scene");
    private static RuntimeTrace? _trace;
    private static Action<string>? _log;
    private static bool _active;
    private static string _buildIdentity = "";
    private static int _fps;
    private static bool _singlePass;

    internal static bool Active => _active;
    internal static IReadOnlyList<RuntimePlaneStatus> Planes => new[] { Clock, Audio, Render, Scene };

    internal static void Initialize(Action<string> log)
    {
        _log = log;
        _trace = RuntimeTrace.Create();
    }

    internal static void Probe(string name, RuntimePlaneState state, string detail)
    {
        lock (Gate)
        {
            Status(name).Set(state, detail);
            _trace?.Write("probe", new Dictionary<string, object?>
            {
                ["plane"] = name,
                ["state"] = state.ToString(),
                ["detail"] = detail
            });
        }
    }

    internal static void BeginExport(string buildIdentity, int fps, bool singlePass, string outputPath,
        int cameraCount, int sampleRate, int channels)
    {
        lock (Gate)
        {
            _active = true;
            _buildIdentity = buildIdentity;
            _fps = fps;
            _singlePass = singlePass;
            Clock.Set(RuntimePlaneState.Active, "managed clock bridge armed");
            Audio.Set(RuntimePlaneState.Active, "native mixer capture armed");
            Render.Set(RuntimePlaneState.Active, "camera capture armed");
            Scene.Set(RuntimePlaneState.Active, "scene scope armed");
            _trace?.Write("begin", new Dictionary<string, object?>
            {
                ["build"] = buildIdentity,
                ["fps"] = fps,
                ["single_pass"] = singlePass,
                ["output"] = outputPath,
                ["cameras"] = cameraCount,
                ["sample_rate"] = sampleRate,
                ["channels"] = channels,
                ["clock_patched_methods"] = GenericStopwatchBridge.PatchedMethods.Count,
                ["clock_capture_reads"] = GenericStopwatchBridge.CaptureReadCount,
                ["clock_status"] = GenericStopwatchBridge.Status
            });
        }
        _log?.Invoke("runtime virtualization begin: build=" + buildIdentity + "; fps=" + fps + "; single-pass=" + singlePass);
    }

    internal static void Trace(string name, IReadOnlyDictionary<string, object?> values)
    {
        lock (Gate) _trace?.Write(name, values);
    }

    internal static void Fail(string plane, string detail)
    {
        lock (Gate)
        {
            Status(plane).Set(RuntimePlaneState.Failed, detail);
            _trace?.Write("plane_failed", new Dictionary<string, object?>
            {
                ["plane"] = plane,
                ["detail"] = detail
            });
        }
    }

    internal static void EndExport(long frames, long audioSamples, bool cancelled)
    {
        lock (Gate)
        {
            if (!_active) return;
            _active = false;
            Clock.Set(RuntimePlaneState.Restored, "managed clock bridge disarmed");
            Audio.Set(RuntimePlaneState.Restored, "native mixer capture stopped");
            Render.Set(RuntimePlaneState.Restored, "camera capture stopped");
            Scene.Set(RuntimePlaneState.Restored, "scene scope restored");
            _trace?.Write("end", new Dictionary<string, object?>
            {
                ["build"] = _buildIdentity,
                ["fps"] = _fps,
                ["single_pass"] = _singlePass,
                ["frames"] = frames,
                ["audio_samples"] = audioSamples,
                ["cancelled"] = cancelled,
                ["clock_capture_reads"] = GenericStopwatchBridge.CaptureReadCount,
                ["clock_status"] = GenericStopwatchBridge.Status
            });
            _trace?.Flush();
        }
        _log?.Invoke("runtime virtualization end: frames=" + frames + "; audio-samples=" + audioSamples + "; cancelled=" + cancelled);
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            _active = false;
            _trace?.Dispose();
            _trace = null;
        }
    }

    private static RuntimePlaneStatus Status(string name) => name switch
    {
        "clock" => Clock,
        "audio" => Audio,
        "render" => Render,
        "scene" => Scene,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown runtime plane.")
    };
}

internal sealed class RuntimeTrace : IDisposable
{
    private readonly StreamWriter? _writer;

    private RuntimeTrace(StreamWriter? writer) => _writer = writer;

    internal static RuntimeTrace Create()
    {
        string? path = Environment.GetEnvironmentVariable("AAVE_TRACE");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Plugin.TimelineReceiptDirectory, "runtime-trace.jsonl");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new RuntimeTrace(new StreamWriter(path, append: false, encoding: new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (Exception error)
        {
            Plugin.LogPerformance("runtime trace unavailable: " + error.Message);
            return new RuntimeTrace(null);
        }
    }

    internal void Write(string name, IReadOnlyDictionary<string, object?> values)
    {
        if (_writer == null) return;
        var payload = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["name"] = name
        };
        foreach (var pair in values) payload[pair.Key] = pair.Value;
        _writer.WriteLine(JsonSerializer.Serialize(payload));
    }

    internal void Flush() => _writer?.Flush();

    public void Dispose()
    {
        try { _writer?.Dispose(); }
        catch (Exception error) { Plugin.LogPerformance("runtime trace close failed: " + error.Message); }
    }
}
