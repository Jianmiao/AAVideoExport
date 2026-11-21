using AAVideoExport.Core;
using UnityEngine;

namespace AAVideoExport.Plugin;

/// <summary>
/// Unity-facing sink for already validated sample-addressed audio segments.
/// It deliberately does not infer timing from wall time or frame callbacks;
/// adapters must provide a StrictAudioSegmentScheduler result first.
/// </summary>
public static class StrictAudioDspDispatcher
{
    public static void PrepareReuse(AudioSource source, AudioClip clip)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (clip == null) throw new ArgumentNullException(nameof(clip));
        source.Stop();
        source.clip = clip;
        source.timeSamples = 0;
        source.volume = 1f;
        source.pitch = 1f;
    }

    public static void Schedule(AudioSource source, StrictScheduledAudioSegment scheduled)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (!double.IsFinite(scheduled.DspStartSeconds) || !double.IsFinite(scheduled.DspEndSeconds)
            || scheduled.DspEndSeconds <= scheduled.DspStartSeconds)
            throw new ExportException("audio_clock_precision", "DSP segment timestamps are invalid.");
        if (scheduled.Segment.ClipOffsetSamples > int.MaxValue)
            throw new ExportException("audio_segment_invalid", "Audio clip offset exceeds Unity's timeSamples range.");
        if (!float.IsFinite(scheduled.Segment.Volume) || scheduled.Segment.Volume < 0
            || !float.IsFinite(scheduled.Segment.Pitch) || scheduled.Segment.Pitch <= 0)
            throw new ExportException("audio_command_parameter", "Audio volume and pitch must be finite and within strict ranges.");
        source.volume = scheduled.Segment.Volume;
        source.pitch = scheduled.Segment.Pitch;
        if (scheduled.Segment.ClipOffsetSamples != 0)
            source.timeSamples = checked((int)scheduled.Segment.ClipOffsetSamples);
        source.PlayScheduled(scheduled.DspStartSeconds);
        source.SetScheduledEndTime(scheduled.DspEndSeconds);
    }

    public static void ScheduleResume(AudioSource source, StrictScheduledAudioSegment scheduled) => Schedule(source, scheduled);
}
