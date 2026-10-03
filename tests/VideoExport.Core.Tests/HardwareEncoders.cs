using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private const string ProbeFixtureDirectoryVariable = "AA_VIDEOEXPORT_TEST_PROBE_DIRECTORY";

    private static EncoderCapability Candidate(string name, string codec = "h264", bool hardware = true, bool available = true) =>
        new(name, codec, hardware, available, available ? "Passed sample encode." : "Advertised, but sample encode failed.");

    private static void TestHardwareAvailability()
    {
        Equal(0, HardwareEncoderPolicy.GetAvailable(Array.Empty<EncoderCapability>()).Count, "empty discovery has no hardware choices");
        Equal(0, HardwareEncoderPolicy.GetAvailable(new[] { Candidate("libx264", hardware: false) }).Count, "software alone has no hardware choices");
        var candidates = new[]
        {
            Candidate("h264_qsv"), Candidate("libx264", hardware: false), Candidate("h264_nvenc", available: false),
            Candidate("hevc_qsv", "hevc"), Candidate("h264_qsv"), Candidate("av1_qsv", "av1"), Candidate("qtrle", "qtrle", hardware: false)
        };
        Equal("av1_qsv,h264_qsv,hevc_qsv", string.Join(',', HardwareEncoderPolicy.GetAvailable(candidates).Select(candidate => candidate.Name)), "sorted distinct working hardware");
        Equal("h264_qsv", string.Join(',', HardwareEncoderPolicy.GetAvailable(candidates, "h264").Select(candidate => candidate.Name)), "codec filter hides failed advertised encoders");
        Equal(0, HardwareEncoderPolicy.GetAvailable(candidates, "qtrle").Count, "software-only codec has no hardware choices");
        Equal(0, HardwareEncoderPolicy.GetAvailable(candidates, "unknown").Count, "unavailable codec has no hardware choices");
    }

    private static void TestHardwareSelection()
    {
        var options = Options("hardware-policy") with { Encoder = "auto" };
        var candidates = new[] { Candidate("libx264", hardware: false), Candidate("h264_amf"), Candidate("h264_nvenc"), Candidate("h264_qsv"), Candidate("av1_qsv", "av1") };
        foreach (var pair in new[] { ("Intel Arc A770", "h264_qsv"), ("intel UHD", "h264_qsv"), ("Arc", "h264_qsv"), ("NVIDIA GeForce", "h264_nvenc"), ("AMD", "h264_amf"), ("Radeon", "h264_amf") })
            Equal(pair.Item2, HardwareEncoderPolicy.Select(options, candidates, pair.Item1), "preferred hardware for " + pair.Item1);
        Equal("h264_amf", HardwareEncoderPolicy.Select(options, candidates, null), "unknown GPU uses deterministic working hardware");
        Equal("h264_amf", HardwareEncoderPolicy.Select(options, candidates.AsEnumerable().Reverse(), "unknown"), "input order cannot change the hardware selection");
        Equal("h264_nvenc", HardwareEncoderPolicy.Select(options with { Encoder = "h264_nvenc" }, candidates, "Intel Arc"), "explicit working hardware overrides vendor preference");
        Equal("av1_qsv", HardwareEncoderPolicy.Select(options with { Codec = "av1" }, candidates, "NVIDIA"), "selected codec limits hardware choices");
        var unavailablePreferred = new[] { Candidate("h264_qsv", available: false), Candidate("h264_nvenc"), Candidate("libx264", hardware: false) };
        Equal("h264_nvenc", HardwareEncoderPolicy.Select(options, unavailablePreferred, "Intel Arc"), "failed preferred hardware falls back to other successful hardware");
    }

    private static void TestHardwareSelectionFailures()
    {
        var options = Options("hardware-failures") with { Encoder = "auto" };
        var softwareOnly = new[] { Candidate("libx264", hardware: false) };
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options, Array.Empty<EncoderCapability>(), "Intel Arc"), "empty hardware result");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options, softwareOnly, "Intel Arc"), "software must not be an automatic fallback");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options, new[] { Candidate("h264_qsv", available: false) }, "Intel Arc"), "advertised hardware that failed its sample");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options with { Encoder = "h264_qsv" }, new[] { Candidate("h264_nvenc") }, "Intel Arc"), "explicit missing hardware must not silently switch");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options with { Encoder = "h264_qsv" }, new[] { Candidate("h264_qsv", available: false) }, "Intel Arc"), "explicit failed hardware");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options with { Codec = "hevc" }, new[] { Candidate("h264_qsv") }, "Intel Arc"), "no hardware for the selected codec");
        ThrowsCode("hardware_encoder_unavailable", () => HardwareEncoderPolicy.Select(options with { Codec = "qtrle", Container = "mov" }, new[] { Candidate("qtrle", "qtrle", hardware: false) }, "Intel Arc"), "software-only codec cannot fall back to CPU");
        foreach (var pair in new[] { ("libx264", "h264"), ("libx265", "hevc"), ("libsvtav1", "av1"), ("libaom-av1", "av1"), ("qtrle", "qtrle") })
        {
            var selected = options with { Encoder = pair.Item1, Codec = pair.Item2, Container = pair.Item2 == "qtrle" ? "mov" : "mp4" };
            ThrowsCode("hardware_encoder_required", () => HardwareEncoderPolicy.Select(selected, new[] { Candidate(pair.Item1, pair.Item2, hardware: false) }, "Intel Arc"), "explicit software " + pair.Item1);
        }
        ThrowsCode("invalid_settings", () => HardwareEncoderPolicy.Select(options with { Width = 0 }, new[] { Candidate("h264_qsv") }, "Intel Arc"), "settings are validated before hardware selection");
    }

    private static int RunFfmpegFixture(string[] args)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(D3DFixtureVariable)))
            return RunDirect3DFixture(args);
        string? directory = Environment.GetEnvironmentVariable(ProbeFixtureDirectoryVariable);
        if (string.IsNullOrEmpty(directory))
        {
            if (args.Contains("null", StringComparer.Ordinal)) return 0;
            Console.Error.WriteLine("The test FFmpeg fixture does not support PCM in MP4.");
            return 73;
        }
        if (args.Contains("-encoders", StringComparer.Ordinal))
        {
            Console.WriteLine(" V..... h264_qsv successful hardware fixture\n V..... h264_nvenc advertised but failing fixture\n V..... libx264 software fixture");
            return 0;
        }
        int encoderIndex = Array.IndexOf(args, "-c:v");
        string encoder = encoderIndex >= 0 && encoderIndex + 1 < args.Length ? args[encoderIndex + 1] : "missing";
        File.WriteAllText(Path.Combine(directory, encoder + ".json"), JsonSerializer.Serialize(args));
        int framesIndex = Array.IndexOf(args, "-frames:v");
        bool realSample = framesIndex >= 0 && framesIndex + 1 < args.Length && args[framesIndex + 1] == "3"
            && args.Contains("lavfi", StringComparer.Ordinal) && args.Contains("null", StringComparer.Ordinal)
            && args.Contains("color=c=black:s=256x144:r=30", StringComparer.Ordinal);
        if (realSample && (encoder == "h264_qsv" || encoder == "libx264")) return 0;
        Console.Error.WriteLine("The advertised hardware encoder fixture cannot encode a sample.");
        return 74;
    }

    private static async Task TestHardwareProbeFixture()
    {
        string directory = Path.Combine(Root, "hardware-probe-fixture");
        Directory.CreateDirectory(directory);
        string? original = Environment.GetEnvironmentVariable(ProbeFixtureDirectoryVariable);
        Environment.SetEnvironmentVariable(ProbeFixtureDirectoryVariable, directory);
        try
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "VideoExport.Core.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var discovered = await FfmpegCapabilities.ProbeHardwareAsync(fixture, "NVIDIA");
            Check(discovered.Encoders.All(candidate => candidate.Hardware), "hardware-only discovery must exclude software candidates");
            Equal("h264_qsv", string.Join(',', discovered.HardwareEncoders.Select(candidate => candidate.Name)), "only the successful sample may become a selectable encoder");
            var failed = discovered.Encoders.Single(candidate => candidate.Name == "h264_nvenc");
            Check(!failed.Available && failed.Detail.Contains("cannot encode a sample", StringComparison.Ordinal), "advertised encoder must be rejected using its sample failure");
            Equal("h264_qsv", discovered.PickHardwareEncoder(Options("hardware-fixture") with { Encoder = "auto" }), "advertised matching vendor cannot win over a working sample");
            Equal("h264_nvenc.json,h264_qsv.json", string.Join(',', Directory.EnumerateFiles(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)), "only advertised hardware candidates are sample-tested");
        }
        finally { Environment.SetEnvironmentVariable(ProbeFixtureDirectoryVariable, original); }
    }

    private static async Task TestHardwareOnlyCapabilities()
    {
        var discovered = await FfmpegCapabilities.ProbeHardwareAsync(ffmpeg, "Intel Arc");
        Check(discovered.Encoders.All(candidate => candidate.Hardware), "real hardware-only probe must exclude software candidates");
        string expected = string.Join(',', discovered.Encoders.Where(candidate => candidate.Available && candidate.Hardware).Select(candidate => candidate.Name).OrderBy(name => name, StringComparer.Ordinal));
        Equal(expected, string.Join(',', discovered.HardwareEncoders.Select(candidate => candidate.Name)), "real hardware choices must contain exactly the successful probes");
        Console.WriteLine("HARDWARE CHOICES: " + (expected.Length == 0 ? "none" : expected));
        File.WriteAllText(Path.Combine(Root, "hardware-capabilities.json"), JsonSerializer.Serialize(new
        {
            Ffmpeg = ffmpeg,
            Encoders = discovered.Encoders,
            HardwareChoices = discovered.HardwareEncoders.Select(candidate => candidate.Name).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string codec in new[] { "h264", "hevc", "av1" })
        {
            var options = Options("local-hardware-" + codec) with { Codec = codec, Encoder = "auto" };
            var available = discovered.HardwareEncoders.Where(candidate => candidate.Codec == codec).ToArray();
            if (available.Length == 0)
                ThrowsCode("hardware_encoder_unavailable", () => discovered.PickHardwareEncoder(options), "no local hardware for " + codec);
            else
            {
                string selected = discovered.PickHardwareEncoder(options);
                Check(available.Any(candidate => candidate.Name == selected), "local selection must be a successful hardware probe for " + codec);
                foreach (var candidate in available)
                    Equal(candidate.Name, discovered.PickHardwareEncoder(options with { Encoder = candidate.Name }), "explicit successful local hardware");
            }
            foreach (var candidate in discovered.Encoders.Where(candidate => candidate.Codec == codec && !candidate.Available))
                ThrowsCode("hardware_encoder_unavailable", () => discovered.PickHardwareEncoder(options with { Encoder = candidate.Name }), "failed local hardware cannot be selected");
        }
        ThrowsCode("hardware_encoder_required", () => discovered.PickHardwareEncoder(Options("local-software")), "software cannot be selected after a hardware-only probe");
    }
}
