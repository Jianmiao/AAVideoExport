namespace AAVideoExport.Core;

public enum StrictAudioOperation
{
    Play,
    OneShot,
    Stop,
    Pause,
    UnPause,
    Seek,
    ScheduledEnd,
    SetVolume,
    SetPitch
}

public sealed record StrictAudioCommand
{
    public StrictAudioCommand(long sequence, long sample, string targetId, StrictAudioOperation operation,
        string clipId, long clipLengthSamples = 0, long clipOffsetSamples = 0, float parameter = 0)
    {
        if (sequence < 0 || sample < 0 || clipLengthSamples < 0 || clipOffsetSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(clipId))
            throw new ArgumentException("Audio command target and clip are required.");
        if (operation == StrictAudioOperation.OneShot && clipLengthSamples <= 0)
            throw new ArgumentException("OneShot commands require a positive clip length.");
        if (!float.IsFinite(parameter)) throw new ArgumentOutOfRangeException(nameof(parameter));
        Sequence = sequence; Sample = sample; TargetId = targetId; Operation = operation; ClipId = clipId;
        ClipLengthSamples = clipLengthSamples; ClipOffsetSamples = clipOffsetSamples;
        Parameter = parameter;
    }

    public long Sequence { get; }
    public long Sample { get; }
    public string TargetId { get; }
    public StrictAudioOperation Operation { get; }
    public string ClipId { get; }
    public long ClipLengthSamples { get; }
    public long ClipOffsetSamples { get; }
    public float Parameter { get; }
}

public sealed record StrictAudioSegment(
    string TargetId,
    string ClipId,
    long StartSample,
    long EndSample,
    long ClipOffsetSamples,
    bool OneShot,
    float Volume = 1f,
    float Pitch = 1f);

/// <summary>
/// Projects source commands into non-overlapping, sample-addressed segments.
/// It models command semantics without touching Unity; adapters can schedule
/// the resulting segments through the native DSP clock later.
/// </summary>
public sealed class StrictAudioCommandModel
{
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);
    private readonly List<StrictAudioSegment> segments = new();
    private long nextSequence;
    private long lastSample;

    public IReadOnlyList<StrictAudioSegment> Segments => segments;

    public void Apply(StrictAudioCommand command)
    {
        if (command.Sequence != nextSequence)
            throw new ExportException("audio_command_order", $"Audio command sequence expected {nextSequence}, received {command.Sequence}.");
        if (command.Sample < lastSample)
            throw new ExportException("audio_command_order", $"Audio command sample moved backwards from {lastSample} to {command.Sample}.");
        sources.TryGetValue(command.TargetId, out var state);
        switch (command.Operation)
        {
            case StrictAudioOperation.Play:
                Close(state, command.Sample);
                sources[command.TargetId] = new SourceState(command.TargetId, command.ClipId, command.ClipOffsetSamples, command.Sample, Paused: false);
                break;
            case StrictAudioOperation.OneShot:
                segments.Add(new StrictAudioSegment(command.TargetId, command.ClipId, command.Sample,
                    checked(command.Sample + command.ClipLengthSamples), command.ClipOffsetSamples, true));
                break;
            case StrictAudioOperation.Stop:
            case StrictAudioOperation.ScheduledEnd:
                RequirePlaying(state, command);
                Close(state, command.Sample);
                sources.Remove(command.TargetId);
                break;
            case StrictAudioOperation.Pause:
                RequirePlaying(state, command);
                Close(state, command.Sample);
                sources[command.TargetId] = state! with { StartSample = command.Sample, Paused = true };
                break;
            case StrictAudioOperation.UnPause:
                if (state is null || !state.Paused)
                    throw new ExportException("audio_command_state", $"UnPause requires a paused source: {command.TargetId}.");
                sources[command.TargetId] = state with { StartSample = command.Sample, Paused = false };
                break;
            case StrictAudioOperation.Seek:
                RequirePlaying(state, command);
                Close(state, command.Sample);
                sources[command.TargetId] = state! with { ClipOffsetSamples = command.ClipOffsetSamples, StartSample = command.Sample };
                break;
            case StrictAudioOperation.SetVolume:
                RequirePlaying(state, command);
                if (command.Parameter < 0) throw new ExportException("audio_command_parameter", "Audio volume cannot be negative.");
                Close(state, command.Sample);
                sources[command.TargetId] = state! with { StartSample = command.Sample, Volume = command.Parameter };
                break;
            case StrictAudioOperation.SetPitch:
                RequirePlaying(state, command);
                if (command.Parameter <= 0) throw new ExportException("audio_command_parameter", "Audio pitch must be positive.");
                Close(state, command.Sample);
                sources[command.TargetId] = state! with { StartSample = command.Sample, Pitch = command.Parameter };
                break;
            default:
                throw new ExportException("audio_command_unknown", "Unknown strict audio operation.");
        }
        nextSequence++;
        lastSample = command.Sample;
    }

    public void Complete(long endSample)
    {
        if (endSample < lastSample) throw new ExportException("audio_command_order", "Audio completion moved backwards.");
        foreach (var state in sources.Values)
        {
            if (!state.Paused) Close(state, endSample);
        }
        sources.Clear();
        lastSample = endSample;
    }

    private void Close(SourceState? state, long endSample)
    {
        if (state is null || state.Paused) return;
        if (endSample < state.StartSample) throw new ExportException("audio_command_order", "Audio segment ended before it started.");
        if (endSample == state.StartSample) return;
        segments.Add(new StrictAudioSegment(state.TargetId, state.ClipId, state.StartSample, endSample,
            state.ClipOffsetSamples, false, state.Volume, state.Pitch));
    }

    private static void RequirePlaying(SourceState? state, StrictAudioCommand command)
    {
        if (state is null || state.Paused)
            throw new ExportException("audio_command_state", $"{command.Operation} requires a playing source: {command.TargetId}.");
    }

    private sealed record SourceState(string TargetId, string ClipId, long ClipOffsetSamples, long StartSample, bool Paused,
        float Volume = 1f, float Pitch = 1f);
}
