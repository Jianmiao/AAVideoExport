namespace AAVideoExport.NativePath.Tests;

internal sealed record NativeVoicePosition(long Frame, long SampleFrame, int Row);
internal sealed record NativeVoiceObservedAsset(string Identifier, string FileSha256, string DecodedPcmSha256, long SampleFrames, int Channels, int SampleRate);
internal sealed record NativeVoiceObservedEvent(int Index, int RequestOrdinal, string Operation, string VoiceIdentifier,
    NativeVoicePosition Position, double PostDelaySeconds, float? Volume, float? Pitch);
internal sealed record NativeVoiceObservationSnapshot(string Schema, int Fps, int SampleRate,
    NativeVoiceObservedAsset[] Assets, NativeVoiceObservedEvent[] Events, NativeVoicePosition End);

// This collector belongs solely to the native acceptance plugin. It receives
// original runtime transitions; it cannot authorize production publication.
internal sealed class NativeVoiceObservation
{
    private readonly int fps, sampleRate;
    private readonly NativeVoiceObservedAsset[] assets;
    private readonly List<NativeVoiceObservedEvent> events = new();
    private int ordinal = -1, state;
    private string identifier = "";
    private double delay;
    private NativeVoicePosition? last;
    private bool completed;
    internal bool HasOpenRequest => state is 1 or 2 or 3;
    internal bool IsEnded => state == 3;
    internal long NativeSequence { get; private set; } = long.MinValue;
    public NativeVoiceObservation(int fps, int sampleRate, NativeVoiceObservedAsset[] assets)
    {
        if (fps <= 0 || sampleRate <= 0 || assets.Length == 0 || assets.Select(a => a.Identifier).Distinct(StringComparer.Ordinal).Count() != assets.Length)
            throw new InvalidDataException("External voice observation inputs are incomplete.");
        this.fps = fps; this.sampleRate = sampleRate; this.assets = assets.OrderBy(a => a.Identifier, StringComparer.Ordinal).ToArray();
    }
    internal bool Request(long sequence, string voiceIdentifier, NativeVoicePosition position, double postDelay)
    {
        Require(!completed, "Voice observation already completed.");
        if (sequence == NativeSequence) { Require(identifier == voiceIdentifier, "A native request sequence changed its identifier."); return false; }
        Require(!HasOpenRequest && assets.Any(a => a.Identifier == voiceIdentifier) && double.IsFinite(postDelay) && postDelay >= 0,
            "Unknown asset, overlapping request or invalid delay in original native voice execution.");
        NativeSequence = sequence; identifier = voiceIdentifier; delay = postDelay; ordinal++; state = 1;
        Add("Request", position, null, null); return true;
    }
    internal void Start(long sequence, NativeVoicePosition position, float volume, float pitch)
    {
        Require(sequence == NativeSequence && state == 1 && float.IsFinite(volume) && float.IsFinite(pitch), "Native voice start is duplicated or belongs to another request.");
        state = 2; Add("NativeStart", position, volume, pitch);
    }
    internal void End(long sequence, NativeVoicePosition position)
    { Require(sequence == NativeSequence && state == 2, "Native voice end lacks its original start."); state = 3; Add("NativeEnd", position, null, null); }
    internal void DelayComplete(long sequence, NativeVoicePosition position)
    { Require(sequence == NativeSequence && state == 3, "Native voice delay completed without a captured start/end pair."); state = 4; Add("DelayComplete", position, null, null); }
    internal NativeVoiceObservationSnapshot Complete(NativeVoicePosition position)
    {
        Require(!completed && !HasOpenRequest && ordinal >= 0, "Native voice observation is empty or contains an unfinished lifecycle.");
        ValidatePosition(position); completed = true;
        return new("aave.native-test-voice.v1", fps, sampleRate, assets, events.ToArray(), position);
    }
    private void Add(string operation, NativeVoicePosition position, float? volume, float? pitch)
    {
        ValidatePosition(position); last = position;
        events.Add(new(events.Count, ordinal, operation, identifier, position, delay, volume, pitch));
    }
    private void ValidatePosition(NativeVoicePosition position) => Require(position.Frame >= 0 && position.SampleFrame >= 0 && position.Row >= 0
        && (last == null || position.Frame >= last.Frame && position.SampleFrame >= last.SampleFrame), "Native voice clock moved backwards or is invalid.");
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
