using System.Runtime.InteropServices;
using System.Diagnostics;

namespace AAVideoExport.Core;

/// <summary>A single export. The caller serializes frame/audio writes and completion. Cancel is safe from another thread.</summary>
public sealed class ExportSession : IDisposable
{
    private readonly ExportOptions options;
    private readonly string ffmpegPath;
    private readonly string ffprobePath;
    private readonly string encoder;
    private readonly int captureSampleRate;
    private readonly int captureChannels;
    private readonly int frameBytes;
    private readonly string temporaryDirectory = "";
    private readonly string videoPath = "";
    private readonly string audioPath = "";
    private readonly string stagedOutput = "";
    private readonly string? coverPath;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private FfmpegProcess? videoProcess;
    private FileStream? audioStream;
    private long framesWritten;
    private long audioSampleFrames;
    private StrictFramePlan? strictPlan;
    private bool liveTimeline;
    private long nextAudioFrame;
    private SessionState state;
    private bool disposed;
    private bool movedCover;
    private bool committed;
    private RuntimeFactoryRecipeSidecar? runtimeSidecar;
    private readonly Stopwatch encodingTimer = new();
    private Direct3DAnimeUpscaler? direct3DUpscaler;
    private QueuedVideoFrameWriter? queuedVideoWriter;
    private string upscaleExecution = "none";
    public string UpscaleAdapter { get; private set; } = "";
    public string UpscaleFallbackReason { get; private set; } = "";

    // Accepted frames follow the capture/story clock. FramesWritten count only
    // complete frames synchronously consumed by the encoder's input stream.
    public long FramesAccepted => queuedVideoWriter?.AcceptedFrames ?? FramesWritten;
    public long FramesWritten => queuedVideoWriter?.WrittenFrames ?? Interlocked.Read(ref framesWritten);
    public int UpscaleReadbackFrames { get; private set; }
    public long AudioSampleFrames => Interlocked.Read(ref audioSampleFrames);
    public long ExpectedAudioSampleFramesForFrame(long videoFrameIndex) =>
        ExportTimeline.AudioSampleFramesForVideoFrame(videoFrameIndex, options.Fps, captureSampleRate);
    public string OutputPath { get; }
    public Action? BeforeOutputCommit { get; set; }
    public RuntimeFactoryRecipeReceipt? RuntimeFactoryRecipe { get; set; }
    /// <summary>
    /// Acquires an optional synchronous validation scope immediately before
    /// final publication, after potentially slow receipt persistence. The
    /// scope is disposed on the same worker after output moves/state commit;
    /// it must not dispatch to Unity or require main-thread work while held.
    /// </summary>
    public Func<IDisposable?>? AcquireOutputCommitScope { get; set; }
    public PreparedAudioTrack CreateAudioPreparationTrack() =>
        new(Path.Combine(temporaryDirectory, "prepared-audio.f32le"), options.Fps, captureSampleRate, captureChannels);

    public void BindStrictTimeline(StrictFramePlan plan)
    {
        RequireWriting();
        if (!options.StrictSynchronization || liveTimeline || strictPlan != null || FramesAccepted != 0 || AudioSampleFrames != 0)
            throw new ExportException("invalid_state", "The strict timeline must be bound once before any output frames.");
        plan.ValidateCaptureIdentity(plan.SourceIdentity, options.Fps, captureSampleRate, captureChannels);
        strictPlan = plan;
    }

    /// <summary>
    /// Starts strict first-pass capture before its final duration is known.
    /// The caller must seal the actual timeline before completing the export.
    /// </summary>
    public void BeginLiveTimeline()
    {
        lock (gate)
        {
            RequireWriting();
            if (!options.StrictSynchronization || liveTimeline || strictPlan != null || FramesAccepted != 0 || AudioSampleFrames != 0)
                throw new ExportException("invalid_state", "A live timeline must begin once before any output frames.");
            liveTimeline = true;
        }
    }

    /// <summary>
    /// Seals the natural playback endpoint. Captured frames may still be in
    /// external readback or the bounded encoder queue; Complete checks their
    /// exact accepted/written totals after all remaining work has arrived.
    /// </summary>
    public void SealLiveTimeline(StrictFramePlan plan)
    {
        lock (gate)
        {
            RequireWriting();
            if (!liveTimeline || strictPlan != null)
                throw new ExportException("invalid_state", "Only an unsealed live timeline can be sealed.");
            ArgumentNullException.ThrowIfNull(plan);
            plan.ValidateCaptureIdentity(plan.SourceIdentity, options.Fps, captureSampleRate, captureChannels);
            if (FramesAccepted > plan.FrameCount) plan.ValidateVideoComplete(FramesAccepted);
            strictPlan = plan;
        }
    }
    public TimeSpan PreflightDuration { get; private set; }
    public TimeSpan EncodingDuration { get; private set; }
    public TimeSpan FinalizationDuration { get; private set; }
    public string UpscaleExecution => upscaleExecution;

    public ExportSession(ExportOptions options, string ffmpegPath, string ffprobePath, string encoder,
        int captureSampleRate = 48000, int captureChannels = 2, bool flipVertical = true)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        if (captureSampleRate is < 8000 or > 192000 || captureChannels is not (1 or 2 or 4 or 5 or 6 or 8))
            throw new ExportException("invalid_settings", "Capture audio must have 1, 2, 4, 5, 6, or 8 channels at 8000–192000 Hz.");
        this.ffmpegPath = ffmpegPath;
        this.ffprobePath = ffprobePath;
        this.encoder = encoder;
        this.captureSampleRate = captureSampleRate;
        this.captureChannels = captureChannels;
        var canvas = options.GetCanvasLayout();
        frameBytes = checked(canvas.CaptureWidth * canvas.CaptureHeight * 4);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        OutputPath = Path.Combine(outputDirectory, options.Title + "." + options.Container);
        coverPath = options.WriteCover ? Path.Combine(outputDirectory, options.Title + ".cover.png") : null;
        EnsureDestinationsFree();
        try
        {
            // This is deliberately before the caller advances the first captured game frame.
            var preflightTimer = Stopwatch.StartNew();
            var encodingOptions = options;
            if (Direct3DUpscalePolicy.IsEligible(options, encoder))
            {
                try
                {
                    direct3DUpscaler = PrepareDirect3DUpscaler(options, encoder, flipVertical, useNv12: true);
                    encodingOptions = Direct3DUpscalePolicy.EncodingOptions(options);
                    try
                    {
                        encodingOptions=FfmpegCapabilities.PrepareEncoderAsync(ffmpegPath,encodingOptions,encoder,
                            CancellationToken.None,nv12Input:direct3DUpscaler.Nv12Output).GetAwaiter().GetResult();
                    }
                    catch(ExportException error) when(direct3DUpscaler.Nv12Output && error.Code is "encoder_failed" or "process_timeout")
                    {
                        // Some encoder/driver combinations cannot ingest NV12.
                        // Retry the same model, adapter and encoding quality with
                        // RGBA before the first captured story frame is advanced.
                        AppendUpscaleFallback("NV12 input: " + error.Message);
                        direct3DUpscaler.Dispose();direct3DUpscaler=null;
                        direct3DUpscaler = PrepareDirect3DUpscaler(options, encoder, flipVertical, useNv12: false);
                        encodingOptions=FfmpegCapabilities.PrepareEncoderAsync(ffmpegPath,encodingOptions,encoder,
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    UpscaleAdapter = direct3DUpscaler.AdapterName;
                    UpscaleReadbackFrames = direct3DUpscaler.Capacity;
                    upscaleExecution = direct3DUpscaler.Nv12Output ? "d3d11-nv12" : "d3d11";
                }
                catch(Exception error) when(error is not OutOfMemoryException)
                {
                    direct3DUpscaler?.Dispose();direct3DUpscaler=null;
                    AppendUpscaleFallback(error.GetType().Name + ": " + error.Message);
                    UpscaleReadbackFrames = 0;
                    encodingOptions = options;
                }
            }
            if(direct3DUpscaler == null)
                encodingOptions = FfmpegCapabilities.PrepareEncoderAsync(ffmpegPath, encodingOptions, encoder, CancellationToken.None).GetAwaiter().GetResult();
            if (direct3DUpscaler == null) options = encodingOptions;
            this.options = options;
            if(direct3DUpscaler == null) upscaleExecution = !UpscaleShaders.UsesGpu(options) ? "none"
                : options.UseComputeUpscale && options.UpscaleAlgorithm is "anime4k-cnn" or "anime4k-rcas" ? "compute8" : "fragment";
            var encodingCanvas = encodingOptions.GetCanvasLayout();
            Directory.CreateDirectory(outputDirectory);
            temporaryDirectory = Path.Combine(outputDirectory, ".aa-video-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            if (UpscaleShaders.UsesGpu(encodingOptions)) UpscaleShaders.Stage(temporaryDirectory, encodingOptions);
            PreflightAudioContainer();
            PreflightDuration = preflightTimer.Elapsed;
            videoPath = Path.Combine(temporaryDirectory, options.Codec == "qtrle" ? "video.mov" : "video.mkv");
            audioPath = Path.Combine(temporaryDirectory, "audio.f32le");
            stagedOutput = Path.Combine(temporaryDirectory, "verified." + options.Container);
            if (options.AudioQuality != "none")
                audioStream = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            bool nv12Input = direct3DUpscaler?.Nv12Output == true;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", nv12Input ? "nv12" : "rgba",
                "-video_size", encodingCanvas.CaptureWidth + "x" + encodingCanvas.CaptureHeight, "-framerate", options.Fps.ToString(), "-i", "pipe:0", "-an" };
            EncoderArguments.AddHardwareDeviceOptions(args, encodingOptions, encoder);
            EncoderArguments.AddFilterThreadOptions(args, encodingOptions);
            args.AddRange(nv12Input ? EncoderArguments.VideoNv12(encodingOptions,encoder) :
                EncoderArguments.Video(encodingOptions, encoder, direct3DUpscaler==null && flipVertical));
            args.AddRange(new[] { "-fps_mode", "passthrough", videoPath });
            videoProcess = new FfmpegProcess(ffmpegPath, args, input: true,
                workingDirectory: UpscaleShaders.UsesGpu(encodingOptions) ? temporaryDirectory : null, gpuEncoder: encoder);
            if (direct3DUpscaler != null)
                queuedVideoWriter = new QueuedVideoFrameWriter(direct3DUpscaler, WriteProcessedFrame);
        }
        catch (Exception ex)
        {
            DisposeResources();
            Cleanup();
            cancellation.Dispose();
            if (ex is IOException or UnauthorizedAccessException) throw ExportException.FromIo(ex, "Preparing export");
            throw;
        }
    }

    private void AppendUpscaleFallback(string reason) => UpscaleFallbackReason =
        UpscaleFallbackReason.Length == 0 ? reason : UpscaleFallbackReason + "; " + reason;

    private Direct3DAnimeUpscaler PrepareDirect3DUpscaler(ExportOptions settings, string selectedEncoder,
        bool flipVertical, bool useNv12)
    {
        Direct3DAnimeUpscaler CreateAndTest(int depth)
        {
            var gpu = new Direct3DAnimeUpscaler(settings, selectedEncoder, flipVertical, useNv12, depth);
            try
            {
                var probe = new byte[frameBytes];
                for (int i = 3; i < probe.Length; i += 4) probe[i] = 255;
                // Exercise every staging slot, including final drain, before
                // starting FFmpeg or advancing any captured story/audio frame.
                for (int i = 0; i < Math.Max(3, depth); i++)
                {
                    gpu.Submit(probe);
                    if (gpu.PendingFrames == gpu.Capacity) gpu.Receive(CancellationToken.None);
                }
                while (gpu.PendingFrames != 0) gpu.Receive(CancellationToken.None);
                return gpu;
            }
            catch { gpu.Dispose(); throw; }
        }
        try { return CreateAndTest(settings.Direct3DReadbackFrames); }
        catch (Exception error) when (settings.Direct3DReadbackFrames > 1 && error is not OutOfMemoryException)
        {
            // No captured frame exists yet, so retrying the same model and GPU
            // with baseline storage cannot lose frames or change the timeline.
            AppendUpscaleFallback("Queued readback -> 1 slot: " + error.GetType().Name + ": " + error.Message);
            return CreateAndTest(1);
        }
    }

    private void WriteProcessedFrame(byte[] processed) => WriteVideoFrame(processed.AsSpan());

    private void WriteVideoFrame(ReadOnlySpan<byte> pixels)
    {
        ValidateVideoFrame(FramesWritten);
        int timedOut = 0;
        using var timeout = new CancellationTokenSource();
        using var registration = timeout.Token.Register(() =>
        {
            Interlocked.Exchange(ref timedOut, 1);
            videoProcess?.Kill();
        });
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            // Synchronous consumption is essential: the GPU readback array is
            // reused for the next frame only after this write has returned.
            videoProcess!.Input.Write(pixels);
            timeout.CancelAfter(Timeout.Infinite);
            if (Volatile.Read(ref timedOut) != 0) throw new TimeoutException();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (Volatile.Read(ref timedOut) != 0)
        { throw new TimeoutException("The encoder did not consume a video frame within 60 seconds.", error); }
    }

    private void PreflightAudioContainer()
    {
        if (options.AudioQuality == "none") return;
        // Actual muxing also checks FFmpeg builds that predate MP4's ipcm support.
        string path = Path.Combine(temporaryDirectory, "audio-preflight." + options.Container);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
            "anullsrc=r=" + options.AudioSampleRate + ":cl=stereo", "-t", "0.1", "-vn" };
        args.AddRange(EncoderArguments.Audio(options));
        args.Add(path);
        try
        {
            FfmpegProcess.RunAsync(ffmpegPath, args, TimeSpan.FromSeconds(10), cancellation.Token).GetAwaiter().GetResult()
                .EnsureSuccess("Audio/container preflight (" + options.AudioQuality + " in " + options.Container.ToUpperInvariant() + ")");
        }
        catch (ExportException ex) when (ex.Code == "encoder_failed")
        {
            throw new ExportException(ex.Code, "This FFmpeg build could not write " + options.AudioQuality + " audio to " + options.Container.ToUpperInvariant() +
                ". Use an FFmpeg build that supports this combination, or choose another audio format or container. " + ex.Message, ex);
        }
        File.Delete(path);
    }

    public void WriteFrame(byte[] rgba, int length)
    {
        RequireWriting();
        if (rgba is null || length != frameBytes || length > rgba.Length)
            throw new ExportException("invalid_frame", "Each frame must match the planned capture dimensions and contain packed RGBA bytes.");
        ValidateVideoFrame(FramesAccepted);
        try
        {
            // Waiting synchronously applies backpressure; Cancel kills FFmpeg to unblock an outstanding pipe write.
            encodingTimer.Start();
            if (queuedVideoWriter != null) queuedVideoWriter.Write(rgba, cancellation.Token);
            else
            {
                videoProcess!.Input.WriteAsync(rgba.AsMemory(0, length), cancellation.Token).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(60), cancellation.Token).GetAwaiter().GetResult();
                Interlocked.Increment(ref framesWritten);
            }
        }
        catch (Exception ex)
        {
            queuedVideoWriter?.Abort();
            bool cancelled = cancellation.IsCancellationRequested;
            videoProcess?.Kill();
            lock (gate) { if (state != SessionState.Cancelled) state = SessionState.Failed; }
            if (cancelled) throw new ExportException("cancelled", "Export cancelled.", ex);
            if (ex is TimeoutException) throw new ExportException("process_timeout", "The encoder did not consume a video frame within 60 seconds.", ex);
            if (ex is not (IOException or ObjectDisposedException)) throw;
            if (videoProcess is not null)
            {
                var result = videoProcess.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
                result.EnsureSuccess("Video encoding");
            }
            throw ExportException.FromIo(ex, "Writing a video frame");
        }
    }

    /// <summary>
    /// Writes a caller-owned native RGBA buffer without a managed staging array.
    /// The caller must keep the buffer alive until this synchronous method returns.
    /// Cancellation kills the child process to unblock the pipe before returning.
    /// </summary>
    public unsafe void WriteFrame(IntPtr rgba, int length)
    {
        RequireWriting();
        if (rgba == IntPtr.Zero || length != frameBytes)
            throw new ExportException("invalid_frame", "Each native frame must contain exactly the planned packed RGBA bytes.");
        ValidateVideoFrame(FramesAccepted);
        try
        {
            encodingTimer.Start();
            if (queuedVideoWriter != null) queuedVideoWriter.Write(rgba, cancellation.Token);
            else
            {
                WriteVideoFrame(new ReadOnlySpan<byte>((void*)rgba, length));
                Interlocked.Increment(ref framesWritten);
            }
        }
        catch (Exception ex)
        {
            queuedVideoWriter?.Abort();
            bool cancelled = cancellation.IsCancellationRequested;
            videoProcess?.Kill();
            lock (gate) { if (state != SessionState.Cancelled) state = SessionState.Failed; }
            if (cancelled) throw new ExportException("cancelled", "Export cancelled.", ex);
            if (ex is TimeoutException)
                throw new ExportException("process_timeout", "The encoder did not consume a video frame within 60 seconds.", ex);
            if (ex is not (IOException or ObjectDisposedException)) throw;
            if (videoProcess is not null)
                videoProcess.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult().EnsureSuccess("Video encoding");
            throw ExportException.FromIo(ex, "Writing a native video frame");
        }
    }

    public void WriteAudio(float[] samples, int length) => WriteAudio(samples, 0, length);

    /// <summary>
    /// Consumes a borrowed interleaved PCM slice synchronously without a
    /// staging array. The caller may reuse the buffer when this method returns.
    /// </summary>
    public void WriteAudio(float[] samples, int offset, int length)
    {
        RequireWriting();
        if (options.AudioQuality == "none") throw new ExportException("invalid_state", "Audio capture is disabled for this export.");
        RequireTimeline();
        ValidateAudio(samples, offset, length);
        WriteAudioSamples(samples, offset, length);
    }

    /// <summary>
    /// Writes the audio block belonging to one video frame and verifies that
    /// Unity's offline renderer returned the exact rational sample budget for
    /// that frame. The legacy WriteAudio method remains available for callers
    /// that provide one already-timed contiguous stream.
    /// </summary>
    public void WriteAudioFrame(long videoFrameIndex, float[] samples, int length)
    {
        RequireWriting();
        if (options.AudioQuality == "none") throw new ExportException("invalid_state", "Audio capture is disabled for this export.");
        RequireTimeline();
        ValidateAudio(samples, 0, length);
        long actual = length / captureChannels;
        long expected = ExpectedAudioSampleFramesForFrame(videoFrameIndex);
        if (options.StrictSynchronization)
        {
            if (strictPlan == null || videoFrameIndex != nextAudioFrame)
                throw new ExportException("audio_frame_order_mismatch", "Strict output audio must follow the sealed video frame order.");
            strictPlan.ValidateNextVideoFrame(videoFrameIndex);
        }
        // Unity can expose one startup mixer callback with no samples before
        // the first real block. It carries no timeline data and is safe to
        // omit; an empty block after audio has started is a real gap and must
        // fail instead of being hidden by end padding.
        if (!options.StrictSynchronization && actual == 0 && AudioSampleFrames == 0)
        {
            WriteAudioSamples(samples, 0, length);
            return;
        }
        if (actual != expected)
            throw new ExportException("audio_frame_timing_mismatch",
                $"Audio sample budget differs on video frame {videoFrameIndex}: expected={expected}, actual={actual}, fps={options.Fps}, capture_rate={captureSampleRate}.");
        WriteAudioSamples(samples, 0, length);
        nextAudioFrame++;
    }

    private void RequireTimeline()
    {
        if (options.StrictSynchronization && strictPlan == null && !liveTimeline)
            throw new ExportException("invalid_state", "Strict capture requires a bound plan or an initialized live timeline.");
    }

    private void ValidateVideoFrame(long frameIndex)
    {
        if (!options.StrictSynchronization) return;
        lock (gate)
        {
            RequireTimeline();
            strictPlan?.ValidateNextVideoFrame(frameIndex);
        }
    }

    private void ValidateAudio(float[] samples, int offset, int length)
    {
        if (samples is null || offset < 0 || length < 0 || offset > samples.Length || length > samples.Length - offset ||
            offset % captureChannels != 0 || length % captureChannels != 0)
            throw new ExportException("invalid_audio", "Audio must contain complete interleaved capture-channel sample frames.");
        for (int i = offset; i < offset + length; i++)
            if (!float.IsFinite(samples[i])) throw new ExportException("invalid_audio", "Audio samples must be finite numbers.");
    }

    private void WriteAudioSamples(float[] samples, int offset, int length)
    {
        try
        {
            audioStream!.Write(MemoryMarshal.AsBytes(samples.AsSpan(offset, length)));
            Interlocked.Add(ref audioSampleFrames, length / captureChannels);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (cancellation.IsCancellationRequested) throw new ExportException("cancelled", "Export cancelled.", ex);
            lock (gate) state = SessionState.Failed;
            videoProcess?.Kill();
            throw ExportException.FromIo(ex, "Writing captured audio");
        }
    }

    public ExportResult Complete(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RequireWriting();
            state = SessionState.Completing;
        }
        using var registration = cancellationToken.Register(Cancel);
        Stopwatch? finalizationTimer = null;
        Exception? completionError = null;
        try
        {
            ThrowIfCancelled();
            if (FramesAccepted == 0) throw new ExportException("invalid_state", "An export needs at least one captured frame.");
            double duration = (double)FramesAccepted / options.Fps;
            if (options.StrictSynchronization)
            {
                if (strictPlan == null) throw new ExportException("invalid_state", "Strict completion requires a sealed timeline.");
                strictPlan.ValidateVideoComplete(FramesAccepted);
                if (options.AudioQuality != "none") strictPlan.ValidateAudioComplete(AudioSampleFrames);
            }
            else if (options.AudioQuality != "none" && (AudioSampleFrames == 0 || Math.Abs((double)AudioSampleFrames / captureSampleRate - duration) > options.AudioTimelineToleranceSeconds + 2d / captureSampleRate))
                throw new ExportException("audio_timeline_mismatch",
                    $"Captured audio is missing or exceeds the output frame-rate tolerance. Video={duration:R}s, audio={(double)AudioSampleFrames / captureSampleRate:R}s, fps={options.Fps}, tolerance_frames={options.AudioTimelineToleranceFrames}, tolerance_seconds={options.AudioTimelineToleranceSeconds:R}.");
            audioStream?.Flush(flushToDisk: true);
            audioStream?.Dispose();
            audioStream = null;
            // Natural completion must submit the remaining model outputs before
            // closing video input. Cancellation never takes this drain path.
            try { queuedVideoWriter?.Drain(cancellation.Token); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // Short clips may write their very first frame during drain.
                // Keep the encoder's real error (including disk-full stderr)
                // rather than replacing it with a generic finalization IO error.
                ThrowIfCancelled();
                videoProcess?.Kill();
                if (videoProcess != null)
                    videoProcess.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult()
                        .EnsureSuccess("Video encoding");
                throw;
            }
            ThrowIfCancelled();
            if (FramesAccepted != FramesWritten)
                throw new ExportException("verification_failed", "Captured and encoded frame counts differ after GPU drain.");
            if (options.StrictSynchronization) strictPlan!.ValidateVideoComplete(FramesWritten);
            videoProcess!.CloseInput();
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(duration * 3 + 60, 120, 7200));
            videoProcess.WaitAsync(timeout, cancellation.Token).GetAwaiter().GetResult().EnsureSuccess("Video encoding");
            encodingTimer.Stop();
            EncodingDuration = encodingTimer.Elapsed;
            finalizationTimer = Stopwatch.StartNew();
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", videoPath };
            if (options.AudioQuality != "none") args.AddRange(new[] { "-f", "f32le", "-ar", captureSampleRate.ToString(), "-ac", captureChannels.ToString(), "-i", audioPath });
            args.AddRange(new[] { "-map", "0:v:0", "-c:v", "copy" });
            if (options.AudioQuality != "none")
            {
                args.AddRange(new[] { "-map", "1:a:0", "-af", options.StrictSynchronization
                    ? "asetpts=PTS-STARTPTS,aresample=" + options.AudioSampleRate + ",atrim=end_sample=" +
                        ExportTimeline.AudioSampleFramesForVideoFrames(FramesWritten, options.Fps, options.AudioSampleRate)
                    : "asetpts=PTS-STARTPTS,apad,atrim=duration=" + EncoderCatalog.Number(duration) });
                args.AddRange(EncoderArguments.Audio(options));
            }
            else args.Add("-an");
            // Matroska stores millisecond timestamps. Quantize them back onto the capture frame grid
            // when copying into a QuickTime track, preserving B-frame PTS/DTS without re-encoding.
            if (options.Container is "mp4" or "mov") args.AddRange(new[] { "-r", options.Fps.ToString(), "-video_track_timescale", options.Fps.ToString() });
            if (options.StrictSynchronization && options.Container is "mp4" or "mov")
            {
                int a = options.AudioSampleRate, b = options.Fps;
                while (b != 0) { int remainder = a % b; a = b; b = remainder; }
                args.AddRange(new[] { "-movie_timescale", checked(options.AudioSampleRate / a * options.Fps).ToString() });
            }
            if (options.Codec == "hevc" && options.Container is "mp4" or "mov") args.AddRange(new[] { "-tag:v", "hvc1" });
            if (options.FastStart && options.Container is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(stagedOutput);
            FfmpegProcess.RunAsync(ffmpegPath, args, timeout, cancellation.Token).GetAwaiter().GetResult().EnsureSuccess("Audio/video mux");
            if (ExportBuildProfile.DevelopmentAuditEnabled)
                MediaVerifier.VerifyAsync(ffprobePath, stagedOutput, options, FramesWritten, timeout, cancellation.Token).GetAwaiter().GetResult();
            else
                MediaVerifier.CheckCompletionAsync(ffprobePath, stagedOutput, options, FramesWritten, timeout, cancellation.Token).GetAwaiter().GetResult();
            string? stagedCover = null;
            if (coverPath is not null)
            {
                stagedCover = Path.Combine(temporaryDirectory, "cover.png");
                FfmpegProcess.RunAsync(ffmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", stagedOutput,
                    "-map", "0:v:0", "-frames:v", "1", "-update", "1", stagedCover }, timeout, cancellation.Token).GetAwaiter().GetResult().EnsureSuccess("Cover extraction");
                if (new FileInfo(stagedCover).Length == 0) throw new ExportException("verification_failed", "The exported cover is empty.");
            }
            long fileBytes = new FileInfo(stagedOutput).Length;
            ThrowIfCancelled();
            // Receipt flush can be slow on external storage; keep it outside
            // the cancellation/state lock. Recheck cancellation before publish.
            if (ExportBuildProfile.DevelopmentAuditEnabled) BeforeOutputCommit?.Invoke();
            if (ExportBuildProfile.DevelopmentAuditEnabled && RuntimeFactoryRecipe is { } recipe)
                runtimeSidecar = recipe.Stage(temporaryDirectory, stagedOutput, OutputPath, FramesWritten,
                    options.Fps, options.Width, options.Height, strictPlan?.SourceIdentity ?? "");
            // Revalidate and serialize boundary admission with publication.
            // A fault recorded while the receipt was flushing must not slip
            // through the earlier check. Do not hold this scope during muxing
            // or receipt IO; only destination moves need the commit barrier.
            using (ExportBuildProfile.DevelopmentAuditEnabled ? AcquireOutputCommitScope?.Invoke() : null)
            {
                lock (gate)
                {
                    ThrowIfCancelled();
                    EnsureDestinationsFree();
                    runtimeSidecar?.Publish();
                    if (stagedCover is not null) { File.Move(stagedCover, coverPath!, overwrite: false); movedCover = true; }
                    File.Move(stagedOutput, OutputPath, overwrite: false);
                    committed = true;
                    state = SessionState.Complete;
                }
            }
            return new ExportResult(OutputPath, FramesWritten, duration, encoder, fileBytes, coverPath)
                { RuntimeRecipeReceiptPath = runtimeSidecar?.Destination };
        }
        catch (Exception ex)
        {
            completionError = ex;
            lock (gate) { if (state != SessionState.Cancelled) state = SessionState.Failed; }
            if (cancellation.IsCancellationRequested || ex is OperationCanceledException)
                throw new ExportException("cancelled", "Export cancelled.", ex);
            if (ex is TimeoutException)
                throw new ExportException("process_timeout", "The encoder did not consume the remaining video frames within 60 seconds.", ex);
            if (ex is IOException or UnauthorizedAccessException) throw ExportException.FromIo(ex, "Finalizing export");
            throw;
        }
        finally
        {
            try { DisposeResources(); }
            finally
            {
                string? residual = Cleanup();
                if (finalizationTimer is not null) FinalizationDuration = finalizationTimer.Elapsed;
                if (residual != null)
                    throw new ExportException("output_cleanup_incomplete", "The movie was not published and an owned runtime receipt could not be rolled back: " + residual, completionError);
            }
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (state == SessionState.Complete || disposed) return;
            state = SessionState.Cancelled;
            cancellation.Cancel();
        }
        videoProcess?.Kill();
    }

    private void RequireWriting()
    {
        lock (gate)
        {
            if (state == SessionState.Cancelled) throw new ExportException("cancelled", "Export cancelled.");
            if (disposed || state != SessionState.Writing) throw new ExportException("invalid_state", "The session is no longer accepting captured frames.");
        }
    }

    private void ThrowIfCancelled()
    {
        if (cancellation.IsCancellationRequested || state == SessionState.Cancelled) throw new ExportException("cancelled", "Export cancelled.");
    }

    private void EnsureDestinationsFree()
    {
        if (File.Exists(OutputPath) || Directory.Exists(OutputPath) || (coverPath is not null && (File.Exists(coverPath) || Directory.Exists(coverPath))))
            throw new ExportException("output_exists", "The video or cover filename already exists. Choose a different title.");
        if (runtimeSidecar != null && (File.Exists(runtimeSidecar.Destination) || Directory.Exists(runtimeSidecar.Destination)))
            throw new ExportException("output_exists", "The runtime receipt filename already exists. Choose a different title; the existing file was preserved.");
    }

    private void DisposeResources()
    {
        queuedVideoWriter?.Abort();
        videoProcess?.Dispose();
        videoProcess = null;
        audioStream?.Dispose();
        audioStream = null;
        direct3DUpscaler?.Dispose();
        direct3DUpscaler = null;
    }

    private string? Cleanup()
    {
        string? residual = null;
        if (!committed && runtimeSidecar != null) runtimeSidecar.TryRollback(out residual);
        if (movedCover && !committed && coverPath is not null)
        {
            try { File.Delete(coverPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (temporaryDirectory.Length == 0) return residual;
        // Only this randomly named child directory is ever removed; never the user's output directory.
        string parent = Path.GetFullPath(options.OutputDirectory);
        if (!string.Equals(Path.GetDirectoryName(temporaryDirectory), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)) return residual;
        try
        {
            if (Directory.Exists(temporaryDirectory) && (File.GetAttributes(temporaryDirectory) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return residual;
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; }
        Cancel();
        DisposeResources();
        string? residual = Cleanup();
        if (residual != null) throw new ExportException("output_cleanup_incomplete", "Owned runtime receipt cleanup remains pending: " + residual);
        lock (gate) { disposed = true; }
        cancellation.Dispose();
    }

    private enum SessionState { Writing, Completing, Complete, Cancelled, Failed }
}
