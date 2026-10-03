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
    private SessionState state;
    private bool disposed;
    private bool movedCover;
    private bool committed;
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
    public string OutputPath { get; }
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

    public void WriteAudio(float[] samples, int length)
    {
        RequireWriting();
        if (options.AudioQuality == "none") throw new ExportException("invalid_state", "Audio capture is disabled for this export.");
        if (samples is null || length < 0 || length > samples.Length || length % captureChannels != 0)
            throw new ExportException("invalid_audio", "Audio must contain complete interleaved capture-channel sample frames.");
        for (int i = 0; i < length; i++)
            if (!float.IsFinite(samples[i])) throw new ExportException("invalid_audio", "Audio samples must be finite numbers.");
        try
        {
            audioStream!.Write(MemoryMarshal.AsBytes(samples.AsSpan(0, length)));
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
        try
        {
            ThrowIfCancelled();
            if (FramesAccepted == 0) throw new ExportException("invalid_state", "An export needs at least one captured frame.");
            double duration = (double)FramesAccepted / options.Fps;
            if (options.AudioQuality != "none" && (AudioSampleFrames == 0 || Math.Abs((double)AudioSampleFrames / captureSampleRate - duration) > 1d / options.Fps + 2d / captureSampleRate))
                throw new ExportException("audio_timeline_mismatch", "Captured audio duration differs from video by more than one frame.");
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
                args.AddRange(new[] { "-map", "1:a:0", "-af", "apad,atrim=duration=" + EncoderCatalog.Number(duration) });
                args.AddRange(EncoderArguments.Audio(options));
            }
            else args.Add("-an");
            // Matroska stores millisecond timestamps. Quantize them back onto the capture frame grid
            // when copying into a QuickTime track, preserving B-frame PTS/DTS without re-encoding.
            if (options.Container is "mp4" or "mov") args.AddRange(new[] { "-r", options.Fps.ToString(), "-video_track_timescale", options.Fps.ToString() });
            if (options.Codec == "hevc" && options.Container is "mp4" or "mov") args.AddRange(new[] { "-tag:v", "hvc1" });
            if (options.FastStart && options.Container is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(stagedOutput);
            FfmpegProcess.RunAsync(ffmpegPath, args, timeout, cancellation.Token).GetAwaiter().GetResult().EnsureSuccess("Audio/video mux");
            MediaVerifier.VerifyAsync(ffprobePath, stagedOutput, options, FramesWritten, timeout, cancellation.Token).GetAwaiter().GetResult();
            string? stagedCover = null;
            if (coverPath is not null)
            {
                stagedCover = Path.Combine(temporaryDirectory, "cover.png");
                FfmpegProcess.RunAsync(ffmpegPath, new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", stagedOutput,
                    "-map", "0:v:0", "-frames:v", "1", "-update", "1", stagedCover }, timeout, cancellation.Token).GetAwaiter().GetResult().EnsureSuccess("Cover extraction");
                if (new FileInfo(stagedCover).Length == 0) throw new ExportException("verification_failed", "The exported cover is empty.");
            }
            long fileBytes = new FileInfo(stagedOutput).Length;
            lock (gate)
            {
                ThrowIfCancelled();
                EnsureDestinationsFree();
                if (stagedCover is not null) { File.Move(stagedCover, coverPath!, overwrite: false); movedCover = true; }
                File.Move(stagedOutput, OutputPath, overwrite: false);
                committed = true;
                state = SessionState.Complete;
            }
            return new ExportResult(OutputPath, FramesWritten, duration, encoder, fileBytes, coverPath);
        }
        catch (Exception ex)
        {
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
            DisposeResources();
            Cleanup();
            if (finalizationTimer is not null) FinalizationDuration = finalizationTimer.Elapsed;
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

    private void Cleanup()
    {
        if (movedCover && !committed && coverPath is not null)
        {
            try { File.Delete(coverPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (temporaryDirectory.Length == 0) return;
        // Only this randomly named child directory is ever removed; never the user's output directory.
        string parent = Path.GetFullPath(options.OutputDirectory);
        if (!string.Equals(Path.GetDirectoryName(temporaryDirectory), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(temporaryDirectory) && (File.GetAttributes(temporaryDirectory) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; }
        Cancel();
        DisposeResources();
        Cleanup();
        lock (gate) { disposed = true; }
        cancellation.Dispose();
    }

    private enum SessionState { Writing, Completing, Complete, Cancelled, Failed }
}
