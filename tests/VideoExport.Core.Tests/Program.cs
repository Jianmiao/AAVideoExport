using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private const int Width = 96;
    private const int Height = 64;
    private const int Fps = 24;
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private static readonly string Root = ResolveArtifactsRoot();
    private static string ffmpeg = "";
    private static string ffprobe = "";
    private static int passed;
    private static int failed;
    private static FfmpegCapabilities? capabilities;

    private static string ResolveTool(string name, string? directory)
    {
        string filename = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        if (!string.IsNullOrWhiteSpace(directory)) return Path.Combine(directory, filename);
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string folder = entry.Trim().Trim('"');
            if (folder.Length == 0) continue;
            string candidate = Path.Combine(folder, filename);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return "";
    }

    private static async Task<int> Main(string[] args)
    {
        // The harness also supplies controlled FFmpeg failures to the probe/preflight tests.
        if (args.FirstOrDefault() == "-hide_banner")
            return RunFfmpegFixture(args);
        if (args.Contains("--canvas-only", StringComparer.Ordinal))
        {
            await TestCanvasLayouts();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--anime-compute-only", StringComparer.Ordinal))
        {
            await TestAnimeComputeContract();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--d3d-policy-only", StringComparer.Ordinal))
        {
            await TestDirect3DPolicy();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--playback-launch-only", StringComparer.Ordinal))
        {
            await TestPlaybackLaunchGate();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--gpu-device-only", StringComparer.Ordinal))
        {
            await TestGpuDeviceBindings();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--panel-layout-only", StringComparer.Ordinal))
        {
            await TestPanelLayouts();
            await TestVideoBitrate();
            await TestExportErrorText();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--ui-flow-only", StringComparer.Ordinal))
        {
            await TestUiFlow();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--ui-refresh-only", StringComparer.Ordinal))
        {
            await TestUiRefreshCadence();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--encoder-path-only", StringComparer.Ordinal))
        {
            await TestEncoderPaths();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--proxy-only", StringComparer.Ordinal))
        {
            await TestProxyLayouts();
            await TestSuperResolution();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        var directory = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? Environment.GetEnvironmentVariable("FFMPEG_DIR");
        ffmpeg = ResolveTool("ffmpeg", directory);
        ffprobe = ResolveTool("ffprobe", directory);
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
        {
            Console.Error.WriteLine("Put ffmpeg and ffprobe on PATH, pass their directory as the first argument, or set FFMPEG_DIR.");
            return 2;
        }
        if (args.Contains("--gpu-device-smoke", StringComparer.Ordinal))
        {
            await TestGpuDeviceRuntime();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        Directory.CreateDirectory(Root);
        Console.WriteLine("Retained test artifacts: " + Root);
        if (args.Contains("--sr720-media-only", StringComparer.Ordinal))
        {
            await Test720pMedia();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--d3d-media", StringComparer.Ordinal))
        {
            await TestDirect3DMedia();
            await TestDirect3DQuality();
            await TestDirect3DFallback();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--proxy-media-only", StringComparer.Ordinal))
        {
            await TestProxyMedia();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--proxy-gpu-media", StringComparer.Ordinal))
        {
            await TestProxyHardwareMedia();
            await TestGpuModelActivation();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--proxy-benchmark", StringComparer.Ordinal))
        {
            await BenchmarkProxy();
            return 0;
        }
        if (args.Contains("--audio-empty-only", StringComparer.Ordinal))
        {
            await Test("zero-length mixer writes preserve PCM samples and duration", TestEmptyAudioFrames);
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--canvas-media-only", StringComparer.Ordinal))
        {
            await TestCanvasMedia();
            Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--benchmark", StringComparer.Ordinal))
        {
            await Test("600-frame 1080p60 sustained QSV benchmark", Benchmark);
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--benchmark-fill", StringComparer.Ordinal))
        {
            await Test("actual 1080p fill capture shape and QSV pipeline", BenchmarkFill);
            return failed == 0 ? 0 : 1;
        }
        foreach (string quality in new[] { "pcm16", "pcm24" })
        foreach (int sampleRate in new[] { 44100, 48000 })
            await Test($"H.264 + {quality} MP4 at {sampleRate} Hz: exact PCM samples and timing", () => TestMp4Pcm(quality, sampleRate));
        await Test("unsupported audio/container fails before capture and cleans temporary files", () => { TestAudioPreflightFailure(); return Task.CompletedTask; });
        if (args.Contains("--pcm-only", StringComparer.Ordinal))
            return failed == 0 ? 0 : 1;
        await TestPanelLayouts();
        await TestVideoBitrate();
        await TestExportErrorText();
        await TestUiFlow();
        await TestUiRefreshCadence();
        await TestPlaybackLaunchGate();
        await Test("reject invalid export settings", () => { ValidateSettings(); return Task.CompletedTask; });
        await TestCanvasLayouts();
        await TestEncoderPaths();
        await Test("hardware availability hides software and failed probes with stable distinct choices", () => { TestHardwareAvailability(); return Task.CompletedTask; });
        await Test("hardware selection prefers the GPU vendor and allows only working hardware", () => { TestHardwareSelection(); return Task.CompletedTask; });
        await Test("hardware selection rejects software, missing hardware and invalid settings", () => { TestHardwareSelectionFailures(); return Task.CompletedTask; });
        await Test("hardware discovery tests advertised encoders and never probes software", TestHardwareProbeFixture);
        await Test("discover working encoders and prefer matching GPU hardware", TestCapabilities);
        await Test("hardware-only discovery exposes only successful local sample encodes", TestHardwareOnlyCapabilities);
        await Test("reject invalid frame/audio buffers", () => { ValidateBuffers(); return Task.CompletedTask; });
        await Test("H.264 + AAC MP4: bottom-up orientation, frame order, cover and fast-start", TestMp4);
        await TestCanvasMedia();
        await Test("qtrle + PCM16 MOV: exact pixels, sample timing and stereo audio", TestMov);
        await Test("zero-length mixer writes preserve PCM samples and duration", TestEmptyAudioFrames);
        await Test("H.264 + PCM24 MKV: 24-bit samples and frame count", TestMkv);
        await Test("audio can be explicitly disabled", TestSilent);
        await Test("48 kHz capture resamples to 44.1 kHz stereo output", TestResample);
        if (capabilities?.Encoders.Any(e => e.Name == "h264_qsv" && e.Available) == true)
            await Test("Intel QSV 1080p60 VBR and CBR synthetic encoding", TestHardware);
        else
            Console.WriteLine("SKIP Intel QSV 1080p60: no working local Intel encoder; excluded from passed count.");
        await Test("explicit cancellation removes unfinished output", () => { TestCancellation(false); return Task.CompletedTask; });
        await Test("cancelled completion token does not finalize output", () => { TestCancellation(true); return Task.CompletedTask; });
        await Test("existing output is preserved", () => { TestNoOverwrite(false); return Task.CompletedTask; });
        await Test("output appearing before completion is preserved", () => { TestNoOverwrite(true); return Task.CompletedTask; });
        await Test("existing PCM MP4 output is preserved", () => { TestNoOverwrite(false, "pcm16"); return Task.CompletedTask; });
        await Test("PCM MP4 output appearing before completion is preserved", () => { TestNoOverwrite(true, "pcm24"); return Task.CompletedTask; });
        Console.WriteLine($"RESULT: {passed} passed; {failed} failed. Artifacts retained at {Root}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task Test(string name, Func<Task> test)
    {
        try
        {
            await test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine("FAIL " + name + Environment.NewLine + exception);
        }
    }

    private static ExportOptions Options(string name) => new()
    {
        Title = name,
        OutputDirectory = Path.Combine(Root, name),
        Width = Width,
        Height = Height,
        Fps = Fps,
        Container = "mp4",
        Codec = "h264",
        Encoder = "libx264",
        BitrateKbps = 2000,
        AudioQuality = "aac192",
        AudioSampleRate = SampleRate,
        WriteCover = true
    };

    private static void ValidateSettings()
    {
        Equal(4000, new ExportOptions().BitrateKbps, "default recommended bitrate");
        Equal(1, new ExportOptions().Supersampling, "rendering is fixed at native output size");
        var valid = Options("settings");
        valid.Validate();
        (valid with { AudioQuality = "pcm16" }).Validate();
        (valid with { AudioQuality = "pcm24" }).Validate();
        var invalid = new (string Name, ExportOptions Options)[]
        {
            ("empty title", valid with { Title = "" }),
            ("path traversal title", valid with { Title = "../escape" }),
            ("empty output directory", valid with { OutputDirectory = "" }),
            ("newline in output directory", valid with { OutputDirectory = valid.OutputDirectory + "\native" }),
            ("tab in output directory", valid with { OutputDirectory = valid.OutputDirectory + "\tools" }),
            ("carriage return in output directory", valid with { OutputDirectory = valid.OutputDirectory + "\render" }),
            ("wildcard in output directory", valid with { OutputDirectory = valid.OutputDirectory + "*" }),
            ("zero width", valid with { Width = 0 }),
            ("odd width", valid with { Width = Width - 1 }),
            ("zero height", valid with { Height = 0 }),
            ("odd height", valid with { Height = Height - 1 }),
            ("unsupported fps", valid with { Fps = 29 }),
            ("unsupported container", valid with { Container = "avi" }),
            ("unsupported codec", valid with { Codec = "unknown" }),
            ("negative bitrate", valid with { BitrateKbps = -1 }),
            ("unsupported rate control", valid with { RateControl = "unknown" }),
            ("unsupported audio quality", valid with { AudioQuality = "unknown" }),
            ("unsupported sample rate", valid with { AudioSampleRate = 22050 }),
            ("zero rendering scale", valid with { Supersampling = 0 }),
            ("legacy 2x supersampling", valid with { Supersampling = 2 }),
            ("unsupported supersampling", valid with { Supersampling = 3 }),
            ("qtrle in MP4", valid with { Codec = "qtrle", Encoder = "qtrle" })
        };
        foreach (var test in invalid)
            ThrowsCode("invalid_settings", test.Options.Validate, test.Name);
    }

    private static void ValidateBuffers()
    {
        using var session = NewSession(Options("invalid-buffers"));
        var frame = Frame(0, true);
        ThrowsCode("invalid_frame", () => session.WriteFrame(frame, frame.Length - 1), "short frame");
        ThrowsCode("invalid_frame", () => session.WriteFrame(frame, frame.Length + 1), "buffer overrun");
        ThrowsCode("invalid_audio", () => session.WriteAudio(new float[3], 3), "incomplete stereo sample");
        ThrowsCode("invalid_audio", () => session.WriteAudio(new float[4], 6), "audio buffer overrun");
        session.Cancel();
    }

    private static async Task TestCapabilities()
    {
        capabilities = await FfmpegCapabilities.ProbeAsync(ffmpeg, "Intel Arc");
        foreach (var encoder in capabilities.Encoders)
            Console.WriteLine($"ENCODER {encoder.Name}: {(encoder.Available ? "available" : "unavailable")}");
        Check(capabilities.Encoders.Any(encoder => encoder.Name == "libx264" && encoder.Available && !encoder.Hardware), "libx264 must pass its real encode probe");
        Check(capabilities.Encoders.Any(encoder => encoder.Name == "qtrle" && encoder.Available), "qtrle must pass its real encode probe");
        var expected = capabilities.Encoders.Where(e => e.Available && e.Codec == "h264")
            .OrderBy(e => e.Name.EndsWith("_qsv", StringComparison.Ordinal) ? 0 : e.Hardware ? 1 : 2).First().Name;
        Equal(expected, capabilities.PickEncoder(Options("capabilities") with { Encoder = "auto" }), "hardware preference and CPU fallback");
        Equal("qtrle", capabilities.PickEncoder(Options("capabilities-mov") with { Container = "mov", Codec = "qtrle", Encoder = "qtrle" }), "explicit working encoder");
    }

    private static async Task TestMp4()
    {
        var result = Encode(Options("h264-aac"), true);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "h264");
        CheckAudio(probe, "aac", SampleRate);
        var video = Stream(probe, "video");
        Equal("bt709", Property(video, "color_space"), "Rec.709 matrix");
        Equal("bt709", Property(video, "color_transfer"), "Rec.709 transfer");
        Equal("bt709", Property(video, "color_primaries"), "Rec.709 primaries");
        var decoded = await DecodeVideo(result.OutputPath);
        CheckFrames(decoded, false);
        var audio = await DecodeAudio(result.OutputPath);
        Check(audio.Length >= SampleRate * Channels && audio.Length <= (SampleRate + 2048) * Channels, "AAC decoded duration must remain near one second");
        CheckTone(audio, 0, 440, 660);
        CheckTone(audio, 1, 660, 440);
        CheckFastStart(result.OutputPath);
        await CheckCover(result.CoverPath);
    }

    private static async Task TestMov()
    {
        var options = Options("qtrle-pcm16") with { Container = "mov", Codec = "qtrle", Encoder = "qtrle", AudioQuality = "pcm16" };
        var result = Encode(options, false);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "qtrle");
        CheckAudio(probe, "pcm_s16le", SampleRate);
        CheckFrames(await DecodeVideo(result.OutputPath), true);
        CheckPcm(await DecodeAudio(result.OutputPath), 1.01 / 32768.0);
        CheckFastStart(result.OutputPath);
        await CheckCover(result.CoverPath);
    }

    private static async Task TestMp4Pcm(string quality, int sampleRate)
    {
        int bits = quality == "pcm16" ? 16 : 24;
        var options = Options($"h264-{quality}-{sampleRate}-mp4") with
        {
            AudioQuality = quality, AudioSampleRate = sampleRate, WriteCover = false
        };
        var result = Encode(options, true, sampleRate);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "h264");
        CheckAudio(probe, bits == 16 ? "pcm_s16le" : "pcm_s24le", sampleRate);
        var audio = Stream(probe, "audio");
        Equal("ipcm", Property(audio, "codec_tag_string"), "MP4 PCM sample entry");
        Equal(bits.ToString(CultureInfo.InvariantCulture), Property(audio, "bits_per_sample"), "PCM bit depth");
        Equal("0.000000", Property(audio, "start_time"), "PCM must start at the first video frame");
        Equal("1.000000", Property(audio, "duration"), "PCM must end with the final video frame");
        CheckFrames(await DecodeVideo(result.OutputPath), false);
        CheckPcm(await DecodeAudio(result.OutputPath), bits == 16 ? 1.01 / 32768.0 : 1.01 / 8388608.0, sampleRate);
        CheckFastStart(result.OutputPath);
        Check(!Directory.EnumerateDirectories(options.OutputDirectory, ".aa-video-export-*").Any(), "successful PCM MP4 must remove its temporary files");
    }

    private static void TestAudioPreflightFailure()
    {
        var options = Options("unsupported-pcm-mp4") with { AudioQuality = "pcm16" };
        Directory.CreateDirectory(options.OutputDirectory);
        string preserved = Path.Combine(options.OutputDirectory, "unrelated.txt");
        File.WriteAllText(preserved, "preserve this unrelated file");
        string fixture = Path.Combine(AppContext.BaseDirectory, "VideoExport.Core.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        try
        {
            using var session = new ExportSession(options, fixture, ffprobe, options.Encoder);
            throw new InvalidOperationException("The unsupported muxer must fail before any capture is accepted.");
        }
        catch (ExportException ex)
        {
            Equal("encoder_failed", ex.Code, "unsupported audio/container error code");
            Check(ex.Message.Contains("Audio/container preflight (pcm16 in MP4)", StringComparison.Ordinal), "error must identify the chosen audio/container preflight");
        }
        Equal("preserve this unrelated file", File.ReadAllText(preserved), "failed preflight must preserve unrelated files");
        Equal(1, Directory.EnumerateFileSystemEntries(options.OutputDirectory).Count(), "failed preflight must leave no output, cover, or session temporary files");
    }

    private static async Task TestMkv()
    {
        var options = Options("h264-pcm24") with { Container = "mkv", AudioQuality = "pcm24", WriteCover = false };
        var result = Encode(options, true);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "h264");
        CheckAudio(probe, "pcm_s24le", SampleRate);
        Equal("24", Property(Stream(probe, "audio"), "bits_per_raw_sample"), "PCM bit depth");
        CheckPcm(await DecodeAudio(result.OutputPath), 1.01 / 8388608.0);
        Check(result.CoverPath is null || !File.Exists(result.CoverPath), "cover disabled");
    }

    private static async Task TestSilent()
    {
        var result = Encode(Options("silent") with { AudioQuality = "none", WriteCover = false }, true);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "h264");
        Check(!probe.RootElement.GetProperty("streams").EnumerateArray().Any(stream => Property(stream, "codec_type") == "audio"), "audio-disabled output must not contain an audio stream");
    }

    private static async Task TestResample()
    {
        var options = Options("resample-44100") with { Container = "mov", Codec = "qtrle", Encoder = "qtrle", AudioQuality = "pcm16", AudioSampleRate = 44100, WriteCover = false };
        var result = Encode(options, false);
        using var probe = await Probe(result.OutputPath);
        CheckVideo(probe, "qtrle");
        CheckAudio(probe, "pcm_s16le", 44100);
        var audio = await DecodeAudio(result.OutputPath);
        Equal(44100 * Channels, audio.Length, "resampled PCM sample count");
        CheckTone(audio, 0, 440, 660, 44100);
        CheckTone(audio, 1, 660, 440, 44100);
    }

    private static async Task TestHardware()
    {
        if (capabilities?.Encoders.Any(e => e.Name == "h264_qsv" && e.Available) != true)
        {
            Console.WriteLine("SKIP QSV benchmark: Intel hardware encoder is not available on this machine.");
            return;
        }
        foreach (string rateControl in new[] { "vbr", "cbr" })
        {
            var options = Options("qsv-1080p60-" + rateControl) with { Width = 1920, Height = 1080, Fps = 60,
                Encoder = "h264_qsv", RateControl = rateControl, BitrateKbps = 12000, AudioQuality = "none", WriteCover = false };
            var frame = new byte[options.Width * options.Height * 4];
            for (int y = 0; y < options.Height; y++)
            for (int x = 0; x < options.Width; x++)
            {
                int offset = (y * options.Width + x) * 4;
                frame[offset] = (byte)(x % 256); frame[offset + 1] = (byte)(y % 256);
                frame[offset + 2] = (byte)((x + y) % 256); frame[offset + 3] = 255;
            }
            var timer = Stopwatch.StartNew();
            using var session = NewSession(options);
            for (int i = 0; i < 120; i++) { frame[0] = (byte)i; session.WriteFrame(frame, frame.Length); }
            var result = session.Complete();
            timer.Stop();
            Equal(120L, result.Frames, "QSV captured frames");
            Equal(2d, result.DurationSeconds, "QSV duration");
            using var probe = await Probe(result.OutputPath);
            var video = Stream(probe, "video");
            Equal("120", Property(video, "nb_read_frames"), "QSV decoded frame count");
            Equal("1920", Property(video, "width"), "QSV width");
            Equal("1080", Property(video, "height"), "QSV height");
            Equal("60/1", Property(video, "avg_frame_rate"), "QSV exact fps");
            Console.WriteLine($"BENCHMARK h264_qsv/{rateControl} 1920x1080@60: {120 / timer.Elapsed.TotalSeconds:F1} fps ({timer.Elapsed.TotalSeconds:F3}s including preflight, encode, mux, verification; synthetic repeated gradient, no AA render)");
        }
    }

    private static ExportSession NewSession(ExportOptions options, bool flipVertical = true, int captureSampleRate = SampleRate) =>
        new(options, ffmpeg, ffprobe, options.Encoder, captureSampleRate, Channels, flipVertical);

    private static ExportResult Encode(ExportOptions options, bool bottomUp, int captureSampleRate = SampleRate)
    {
        var timer = Stopwatch.StartNew();
        using var session = NewSession(options, bottomUp, captureSampleRate);
        WriteSequence(session, bottomUp, options.AudioQuality != "none", captureSampleRate);
        Equal((long)Fps, session.FramesWritten, "input frame count");
        Equal(options.AudioQuality == "none" ? 0L : captureSampleRate, session.AudioSampleFrames, "input sample frames");
        var result = session.Complete();
        timer.Stop();
        Equal((long)Fps, result.Frames, "result frames");
        Check(Math.Abs(result.DurationSeconds - 1) < 0.00001, "result duration");
        Check(File.Exists(result.OutputPath), "final output must exist");
        Check(new FileInfo(result.OutputPath).Length == result.FileBytes && result.FileBytes > 0, "reported size matches final file");
        Equal(options.Encoder, result.Encoder, "reported encoder");
        Console.WriteLine($"BENCHMARK {options.Encoder}/{options.AudioQuality}: {Fps / timer.Elapsed.TotalSeconds:F1} fps ({Fps} frames including capture writes, encode and verification; {timer.Elapsed.TotalSeconds:F3}s)");
        return result;
    }

    private static void WriteSequence(ExportSession session, bool bottomUp, bool writeAudio = true, int captureSampleRate = SampleRate)
    {
        for (var index = 0; index < Fps; index++)
        {
            var frame = Frame(index, bottomUp);
            session.WriteFrame(frame, frame.Length);
            if (writeAudio)
            {
                int start = index * captureSampleRate / Fps;
                int end = (index + 1) * captureSampleRate / Fps;
                var audio = Audio(start, end - start, captureSampleRate);
                session.WriteAudio(audio, audio.Length);
            }
        }
    }

    private static void TestCancellation(bool token)
    {
        var options = Options(token ? "cancel-token" : "cancel-explicit");
        using var session = NewSession(options);
        var frame = Frame(0, true);
        session.WriteFrame(frame, frame.Length);
        if (token)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            ThrowsCode("cancelled", () => session.Complete(cancellation.Token), "cancelled token");
        }
        else
        {
            session.Cancel();
            ThrowsCode("cancelled", () => session.Complete(), "cancelled session");
        }
        Check(!File.Exists(session.OutputPath), "cancel must not finalize an output");
        Check(!File.Exists(Path.Combine(options.OutputDirectory, options.Title + ".cover.png")), "cancel must not leave a cover");
        session.Dispose();
        Check(!Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any(), "disposing a cancelled session must remove temporary export files");
    }

    private static void TestNoOverwrite(bool late, string audioQuality = "aac192")
    {
        var options = Options((late ? "late-existing-" : "existing-") + audioQuality) with { AudioQuality = audioQuality };
        Directory.CreateDirectory(options.OutputDirectory);
        var path = Path.Combine(options.OutputDirectory, options.Title + ".mp4");
        var sentinel = Encoding.UTF8.GetBytes("existing user output must be preserved exactly");
        if (late)
        {
            using var session = NewSession(options);
            WriteSequence(session, true);
            File.WriteAllBytes(path, sentinel);
            ThrowsCode("output_exists", () => session.Complete(), "late output collision");
        }
        else
        {
            File.WriteAllBytes(path, sentinel);
            ThrowsCode("output_exists", () =>
            {
                using var session = NewSession(options);
                WriteSequence(session, true);
                session.Complete();
            }, "existing output collision");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(sentinel), "existing file contents changed");
    }

    private static byte[] Frame(int index, bool bottomUp)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var gray = (byte)(32 + index * 8);
            byte r = gray, g = gray, b = gray;
            if (x < 16 && y < 16) { r = 220; g = 24; b = 24; }
            if (x >= Width - 16 && y < 16) { r = 24; g = 220; b = 24; }
            if (x < 16 && y >= Height - 16) { r = 24; g = 24; b = 220; }
            if (x >= Width - 16 && y >= Height - 16) { r = 220; g = 220; b = 24; }
            var offset = ((bottomUp ? Height - y - 1 : y) * Width + x) * 4;
            pixels[offset] = r;
            pixels[offset + 1] = g;
            pixels[offset + 2] = b;
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static float[] Audio(int offset, int frames, int sampleRate = SampleRate)
    {
        var samples = new float[frames * Channels];
        for (var index = 0; index < frames; index++)
        {
            samples[index * 2] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (offset + index) / sampleRate));
            samples[index * 2 + 1] = (float)(0.375 * Math.Sin(2 * Math.PI * 660 * (offset + index) / sampleRate));
        }
        return samples;
    }

    private static void CheckFrames(byte[] actual, bool lossless)
    {
        var bytesPerFrame = Width * Height * 4;
        Equal(bytesPerFrame * Fps, actual.Length, "decoded byte/frame count");
        for (var index = 0; index < Fps; index++)
        {
            var expected = Frame(index, false);
            if (lossless)
            {
                for (var offset = 0; offset < bytesPerFrame; offset++)
                    if (expected[offset] != actual[index * bytesPerFrame + offset])
                        throw new InvalidOperationException($"lossless pixel mismatch: frame {index}, byte {offset}, expected {expected[offset]}, actual {actual[index * bytesPerFrame + offset]}");
            }
            else
            {
                foreach (var point in new[] { (8, 8), (Width - 8, 8), (8, Height - 8), (Width - 8, Height - 8), (Width / 2, Height / 2) })
                {
                    var offset = (point.Item2 * Width + point.Item1) * 4;
                    var tolerance = point.Item1 == Width / 2 ? 3 : 14;
                    for (var channel = 0; channel < 3; channel++)
                        Check(Math.Abs(expected[offset + channel] - actual[index * bytesPerFrame + offset + channel]) <= tolerance, $"frame {index}, pixel ({point.Item1},{point.Item2}), channel {channel}: orientation/order/color mismatch");
                }
            }
        }
    }

    private static void CheckPcm(float[] actual, double tolerance, int sampleRate = SampleRate)
    {
        var expected = Audio(0, sampleRate, sampleRate);
        Equal(expected.Length, actual.Length, "exact PCM sample count");
        var maxError = 0.0;
        for (var index = 0; index < expected.Length; index++)
            maxError = Math.Max(maxError, Math.Abs(expected[index] - actual[index]));
        Check(maxError <= tolerance, $"PCM samples differ: maximum error {maxError:R}, allowed {tolerance:R}");
    }

    private static void CheckTone(float[] audio, int channel, double frequency, double otherFrequency, int sampleRate = SampleRate)
    {
        double energy = 0, wantedSin = 0, wantedCos = 0, otherSin = 0, otherCos = 0;
        for (var frame = 0; frame < sampleRate; frame++)
        {
            var sample = audio[frame * Channels + channel];
            energy += sample * sample;
            var position = 2 * Math.PI * frame / sampleRate;
            wantedSin += sample * Math.Sin(position * frequency);
            wantedCos += sample * Math.Cos(position * frequency);
            otherSin += sample * Math.Sin(position * otherFrequency);
            otherCos += sample * Math.Cos(position * otherFrequency);
        }
        var rms = Math.Sqrt(energy / sampleRate);
        var expectedRms = (channel == 0 ? 0.25 : 0.375) / Math.Sqrt(2);
        Check(Math.Abs(rms - expectedRms) < 0.02, "AAC channel level");
        Check(wantedSin * wantedSin + wantedCos * wantedCos > 25 * (otherSin * otherSin + otherCos * otherCos), "AAC stereo channel content");
    }

    private static async Task CheckCover(string? path)
    {
        Check(path is not null && File.Exists(path), "cover file exists");
        var bytes = await RunBinary(ffmpeg, "-v", "error", "-i", path!, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
        Equal(Width * Height * 4, bytes.Length, "cover dimensions");
        var expected = Frame(0, false);
        foreach (var point in new[] { (8, 8), (Width - 8, 8), (8, Height - 8), (Width - 8, Height - 8) })
        {
            var offset = (point.Item2 * Width + point.Item1) * 4;
            for (var channel = 0; channel < 3; channel++)
                Check(Math.Abs(bytes[offset + channel] - expected[offset + channel]) <= 14, "cover orientation/color");
        }
    }

    private static void CheckFastStart(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input);
        long moov = -1, mdat = -1;
        while (input.Position + 8 <= input.Length)
        {
            var position = input.Position;
            var size = ReadBigEndian(reader, 4);
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (size == 1) size = ReadBigEndian(reader, 8);
            if (size == 0) size = input.Length - position;
            Check(size >= 8 && position + size <= input.Length, "valid MP4/MOV box size");
            if (type == "moov") moov = position;
            if (type == "mdat" && mdat < 0) mdat = position;
            input.Position = position + size;
        }
        Check(moov >= 0 && mdat >= 0 && moov < mdat, "fast-start must put moov before mdat");
    }

    private static long ReadBigEndian(BinaryReader reader, int bytes)
    {
        long value = 0;
        for (var index = 0; index < bytes; index++) value = (value << 8) | reader.ReadByte();
        return value;
    }

    private static async Task<JsonDocument> Probe(string path) => JsonDocument.Parse(await RunBinary(ffprobe, "-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", path));

    private static JsonElement Stream(JsonDocument document, string type) => document.RootElement.GetProperty("streams").EnumerateArray().Single(stream => Property(stream, "codec_type") == type);

    private static string Property(JsonElement element, string name) => element.TryGetProperty(name, out var property) ? property.ToString() : "";

    private static void CheckVideo(JsonDocument document, string codec)
    {
        var stream = Stream(document, "video");
        Equal(codec, Property(stream, "codec_name"), "video codec");
        Equal(Width.ToString(CultureInfo.InvariantCulture), Property(stream, "width"), "video width");
        Equal(Height.ToString(CultureInfo.InvariantCulture), Property(stream, "height"), "video height");
        Equal(Fps.ToString(CultureInfo.InvariantCulture), Property(stream, "nb_read_frames"), "ffprobe decoded frame count");
        Equal(Fps + "/1", Property(stream, "r_frame_rate"), "video frame rate");
        var averageRate = Property(stream, "avg_frame_rate").Split('/');
        Check(averageRate.Length == 2, "average frame rate is a rational number");
        var average = double.Parse(averageRate[0], CultureInfo.InvariantCulture) / double.Parse(averageRate[1], CultureInfo.InvariantCulture);
        Check(Math.Abs(average - Fps) < 0.001, "average frame rate must match the requested rate");
        var duration = double.Parse(Property(document.RootElement.GetProperty("format"), "duration"), CultureInfo.InvariantCulture);
        Check(Math.Abs(duration - 1) <= 1.0 / Fps, "container duration");
    }

    private static void CheckAudio(JsonDocument document, string codec, int sampleRate)
    {
        var stream = Stream(document, "audio");
        Equal(codec, Property(stream, "codec_name"), "audio codec");
        Equal(sampleRate.ToString(CultureInfo.InvariantCulture), Property(stream, "sample_rate"), "audio sample rate");
        Equal("2", Property(stream, "channels"), "stereo channels");
    }

    private static Task<byte[]> DecodeVideo(string path) => RunBinary(ffmpeg, "-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");

    private static async Task<float[]> DecodeAudio(string path)
    {
        var bytes = await RunBinary(ffmpeg, "-v", "error", "-i", path, "-map", "0:a:0", "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1");
        Check(bytes.Length % sizeof(float) == 0, "decoded float sample alignment");
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static async Task<byte[]> RunBinary(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + executable);
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Media inspection exceeded 60 seconds.");
        }
        await copy;
        var stderr = await errors;
        Check(process.ExitCode == 0, Path.GetFileName(executable) + " failed: " + stderr);
        return output.ToArray();
    }

    private static void ThrowsCode(string code, Action action, string context)
    {
        try { action(); }
        catch (ExportException exception)
        {
            Equal(code, exception.Code, context + " error code");
            return;
        }
        throw new InvalidOperationException(context + " did not throw ExportException(" + code + ").");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}; actual {actual}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
