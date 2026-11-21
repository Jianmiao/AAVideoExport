namespace AAVideoExport.Core;

/// <summary>
/// Reassembles arbitrary offline-mixer audio blocks onto the fixed video clock.
///
/// Unity may return a block that ends in the middle of a video frame, or a block
/// that spans several video frames. This type owns that boundary: callers append
/// interleaved samples as they arrive and consume the exact per-frame chunks it
/// emits. The expected total is fixed at construction, so missing and extra
/// samples are hard errors instead of being hidden by tail padding.
/// </summary>
public sealed class AudioTimelineAccumulator
{
    private readonly long totalVideoFrames;
    private readonly int fps;
    private readonly int sampleRate;
    private readonly int channels;
    private readonly long expectedSampleFrames;
    private readonly List<float> pending = new();
    private int pendingOffset;
    private long nextVideoFrame;
    private long receivedSampleFrames;

    public AudioTimelineAccumulator(long totalVideoFrames, int fps, int sampleRate, int channels)
    {
        if (totalVideoFrames < 0) throw new ArgumentOutOfRangeException(nameof(totalVideoFrames));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is not (1 or 2 or 4 or 5 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(channels));
        this.totalVideoFrames = totalVideoFrames;
        this.fps = fps;
        this.sampleRate = sampleRate;
        this.channels = channels;
        expectedSampleFrames = ExportTimeline.AudioSampleFramesForVideoFrames(totalVideoFrames, fps, sampleRate);
    }

    public long TotalVideoFrames => totalVideoFrames;
    public long ExpectedSampleFrames => expectedSampleFrames;
    public long ReceivedSampleFrames => receivedSampleFrames;
    public long EmittedSampleFrames => ExportTimeline.AudioSampleFramesForVideoFrames(nextVideoFrame, fps, sampleRate);
    public long NextVideoFrame => nextVideoFrame;
    public bool IsComplete => nextVideoFrame == totalVideoFrames && PendingSampleFrames == 0 && receivedSampleFrames == expectedSampleFrames;

    /// <summary>Append one arbitrary interleaved block and return every complete frame now available.</summary>
    public IReadOnlyList<AudioTimelineFrame> Append(float[] samples, int length)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        return Append(samples, 0, length);
    }

    /// <summary>Append a slice of an interleaved block without requiring a copy at the call site.</summary>
    public IReadOnlyList<AudioTimelineFrame> Append(float[] samples, int offset, int length)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (offset < 0 || length < 0 || offset > samples.Length - length || offset % channels != 0 || length % channels != 0)
            throw new ExportException("invalid_audio", "Audio must contain complete interleaved capture-channel sample frames.");
        for (int index = offset; index < offset + length; index++)
            if (!float.IsFinite(samples[index]))
                throw new ExportException("invalid_audio", "Audio samples must be finite numbers.");

        long incomingFrames = length / channels;
        long newTotal = checked(receivedSampleFrames + incomingFrames);
        if (newTotal > expectedSampleFrames)
            throw new ExportException("audio_timeline_extra",
                $"Audio contains {newTotal - expectedSampleFrames} sample frames beyond the fixed export timeline.");

        if (length != 0)
        {
            for (int index = offset; index < offset + length; index++) pending.Add(samples[index]);
            receivedSampleFrames = newTotal;
        }
        return DrainReadyFrames();
    }

    /// <summary>
    /// Seal the timeline. Any missing samples or an incomplete frame is a hard
    /// failure; no implicit silence or trimming is allowed.
    /// </summary>
    public void Complete()
    {
        if (receivedSampleFrames != expectedSampleFrames || nextVideoFrame != totalVideoFrames || PendingSampleFrames != 0)
            throw new ExportException("audio_timeline_incomplete",
                $"Audio timeline is incomplete: received={receivedSampleFrames}, expected={expectedSampleFrames}, next_frame={nextVideoFrame}, total_frames={totalVideoFrames}.");
    }

    private IReadOnlyList<AudioTimelineFrame> DrainReadyFrames()
    {
        List<AudioTimelineFrame>? ready = null;
        while (nextVideoFrame < totalVideoFrames)
        {
            long budget = ExportTimeline.AudioSampleFramesForVideoFrame(nextVideoFrame, fps, sampleRate);
            if (PendingSampleFrames < budget) break;
            int sampleCount = checked((int)(budget * channels));
            var frame = new float[sampleCount];
            if (sampleCount != 0) pending.CopyTo(pendingOffset, frame, 0, sampleCount);
            pendingOffset += sampleCount;
            nextVideoFrame++;
            ready ??= new List<AudioTimelineFrame>();
            ready.Add(new AudioTimelineFrame(nextVideoFrame - 1, frame, budget));
            CompactPending();
        }
        return ready is null ? Array.Empty<AudioTimelineFrame>() : ready;
    }

    private long PendingSampleFrames => (pending.Count - pendingOffset) / channels;

    private void CompactPending()
    {
        if (pendingOffset == 0) return;
        if (pendingOffset == pending.Count)
        {
            pending.Clear();
            pendingOffset = 0;
            return;
        }
        // Avoid O(n²) front removals for long exports. Clear keeps the backing
        // storage for reuse by subsequent blocks.
        if (pendingOffset >= 65536 && pendingOffset >= pending.Count / 2)
        {
            pending.RemoveRange(0, pendingOffset);
            pendingOffset = 0;
        }
    }
}

public readonly record struct AudioTimelineFrame(long VideoFrameIndex, float[] Samples, long SampleFrames);
