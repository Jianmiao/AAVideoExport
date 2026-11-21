using System.Globalization;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.NativePath.Tests;

// Test-only configuration and timing. No native player, output publication or
// clock implementation is replaced. Full-media verification keeps its existing
// fixed profile; explicit alternative options are bounded performance-only.
internal sealed class NativeStoryTestProfile
{
    private readonly ExportOptions? options;
    internal int? PerformanceStopFrame { get; }
    internal bool WholeExportBenchmark { get; }
    private NativeStoryTestProfile(ExportOptions? options, int? stopFrame, bool wholeExportBenchmark = false)
    { this.options = options; PerformanceStopFrame = stopFrame; WholeExportBenchmark = wholeExportBenchmark; }

    internal static NativeStoryTestProfile ParseBenchmark(string? optionsJson, string? stopFrameText, bool wholeExportBenchmark)
    {
        if (!wholeExportBenchmark) return Parse(optionsJson, stopFrameText);
        if (string.IsNullOrWhiteSpace(optionsJson) || !string.IsNullOrEmpty(stopFrameText))
            throw new InvalidOperationException("Whole-export benchmark requires explicit options and cannot have a cancellation window.");
        var selected = JsonSerializer.Deserialize<ExportOptions>(optionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Whole-export benchmark options are empty.");
        if (!selected.StrictSynchronization) throw new InvalidOperationException("Native benchmark requires strict synchronization.");
        selected.Validate();
        return new(selected, null, true);
    }

    internal static NativeStoryTestProfile Parse(string? optionsJson, string? stopFrameText)
    {
        int? stopFrame = null;
        if (!string.IsNullOrEmpty(stopFrameText))
        {
            if (!int.TryParse(stopFrameText, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is <= 60 or > 3600)
                throw new InvalidOperationException("AA_VIDEOEXPORT_TEST_PERF_STOP_FRAME must be an integer from 61 through 3600; the measured window starts at authored frame 60.");
            stopFrame = value;
        }
        ExportOptions? selected = null;
        if (!string.IsNullOrEmpty(optionsJson))
        {
            if (stopFrame == null) throw new InvalidOperationException("Explicit test option overrides require bounded performance-only mode; full-media acceptance retains its original verified profile.");
            selected = JsonSerializer.Deserialize<ExportOptions>(optionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The explicit export options JSON is empty.");
            if (!selected.StrictSynchronization) throw new InvalidOperationException("Native performance comparison requires strict synchronization.");
        }
        return new(selected, stopFrame);
    }

    internal ExportOptions CreateOptions(string outputDirectory, string title)
    {
        var selected = options ?? new ExportOptions
        {
            Width = 640, Height = 360, Fps = 30, Container = "mov", Codec = "h264", EncodingMode = "software", Encoder = "libx264",
            AudioQuality = "pcm16", AudioSampleRate = 48000, StrictSynchronization = true, BitrateKbps = 4000, RateControl = "vbr"
        };
        var result = selected with { Title = title, OutputDirectory = outputDirectory, WriteCover = false };
        result.Validate();
        return result;
    }

    internal string ValidateProbe(ExportOptions requested, string observedMode, IEnumerable<EncoderCapability> candidates, string gpuName)
    {
        if (observedMode != requested.EncodingMode) throw new InvalidOperationException("The completed native encoder probe belongs to a different encoding mode.");
        return EncoderSelectionPolicy.Select(requested, candidates, gpuName);
    }

    internal void ValidateEffectiveOptions(ExportOptions requested, ExportOptions effective, string selectedEncoder, string actualEncoder, string actualOutputPath)
    {
        // The host intentionally fills renderer IDs from the real device. All
        // user-selected image/audio settings and artifact ownership must agree.
        var normalized = effective with { RenderGpuVendorId = requested.RenderGpuVendorId, RenderGpuDeviceId = requested.RenderGpuDeviceId };
        if (JsonSerializer.Serialize(normalized) != JsonSerializer.Serialize(requested) || selectedEncoder != actualEncoder
            || !string.Equals(Path.GetFullPath(actualOutputPath), Path.GetFullPath(Path.Combine(requested.OutputDirectory, requested.Title + "." + requested.Container)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The production host/session differs from the explicit test profile or selected real encoder.");
    }

    internal StoryPerformanceWindow? CreatePerformanceWindow() => PerformanceStopFrame is { } stop ? new StoryPerformanceWindow(stop) : null;
}

internal sealed class StoryPerformanceWindow
{
    private readonly int stopFrame;
    private long previousFrame = -1;
    private double previousSeconds = -1;
    private double? started;
    private long startFrame;
    private StoryPerformanceMeasurement? measurement;
    internal bool HasStarted => started != null;
    internal StoryPerformanceWindow(int stopFrame) { this.stopFrame = stopFrame; }
    internal bool Observe(long frame, double elapsedSeconds)
    {
        if (measurement != null) return true;
        if (frame < previousFrame || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds < previousSeconds)
            throw new InvalidOperationException("Performance observation crossed a backwards frame or monotonic clock boundary.");
        previousFrame = frame; previousSeconds = elapsedSeconds;
        if (frame < 60) return false;
        if (started == null)
        {
            if (frame >= stopFrame) throw new InvalidOperationException("Native observation missed the entire requested performance interval.");
            started = elapsedSeconds; startFrame = frame;
        }
        if (frame < stopFrame) return false;
        double seconds = elapsedSeconds - started.Value;
        if (seconds <= 0) throw new InvalidOperationException("The bounded native performance interval is empty.");
        measurement = new(60, stopFrame, startFrame, frame, frame - startFrame, started.Value, elapsedSeconds, seconds, (frame - startFrame) / seconds);
        return true;
    }
    internal StoryPerformanceMeasurement Snapshot() => measurement ?? throw new InvalidOperationException("The native performance interval has not completed.");
}

// Observation policy only: never toggles the loaded production execution mode.
internal sealed class StoryPerformanceMode
{
    private readonly bool singlePass;
    public StoryPerformanceMode(bool singlePass) { this.singlePass = singlePass; }
    internal string Phase => singlePass ? "single-pass-render-encode" : "first-audio-preparation";
    internal void ValidateObservedPass(bool sawAudio, bool sawVideo, bool replayPending, bool completedMovie, int passBegins)
    {
        if (replayPending || completedMovie || passBegins > 1 || (singlePass ? sawAudio : sawVideo))
            throw new InvalidOperationException("Bounded performance observation crossed its original " + Phase + " pass or completed a movie.");
    }
    internal void ValidateStoppedWindow(long captured, long accepted, long encoded)
    {
        if (captured <= 60 || accepted < 0 || encoded < 0 || encoded > accepted || accepted > captured
            || (singlePass ? accepted == 0 || encoded == 0 : accepted != 0 || encoded != 0))
            throw new InvalidOperationException("Bounded " + Phase + " counters do not prove the selected original work.");
    }
}

internal sealed record StoryPerformanceMeasurement(long RequestedStartFrame, long RequestedStopFrame, long StartFrame, long StopFrame, long MeasuredFrames,
    double StartElapsedSeconds, double StopElapsedSeconds, double ElapsedSeconds, double FramesPerSecond);
