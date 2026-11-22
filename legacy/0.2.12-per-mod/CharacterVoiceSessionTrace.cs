using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AAVideoExport.Plugin;

// Native addresses and provider sequence numbers are pass-local guards only.
// The receipt certifies known assets and observed events, never a whole Mod set.
internal sealed record CharacterVoiceAsset(string Identifier, string FileSha256, string DecodedPcmSha256, int SampleFrames, int Channels, int SampleRate)
{
    public string AssetId => CharacterVoiceIdentity.Hash(FormattableString.Invariant(
        $"{FileSha256.ToLowerInvariant()}|{DecodedPcmSha256.ToLowerInvariant()}|{SampleFrames}|{Channels}|{SampleRate}"));
}
internal readonly record struct CharacterVoicePosition(long Frame, long SampleFrame, int Row);
internal sealed record CharacterVoiceEvent(int Index, int RequestOrdinal, string SourceId, string VoiceIdentifier, string AssetId,
    string Operation, CharacterVoicePosition Position, double PostDelaySeconds, float? Volume, float? Pitch);

internal sealed class CharacterVoiceReceipt
{
    public string Scope => CharacterVoiceSessionTrace.Scope;
    public string StorySha256 { get; }
    public int Fps { get; }
    public int SampleRate { get; }
    public string SettingsIdentity { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<CharacterVoiceAsset> Assets { get; }
    public IReadOnlyList<CharacterVoiceEvent> Events { get; }
    public CharacterVoicePosition End { get; }
    public string TraceSha256 { get; }
    public bool NoOwnedVoiceExpected { get; }
    internal CharacterVoiceReceipt(string story, int fps, int sampleRate, string settings, string manifest,
        IEnumerable<CharacterVoiceAsset> assets, IEnumerable<CharacterVoiceEvent> events, CharacterVoicePosition end, bool noOwnedVoiceExpected = false)
    {
        StorySha256 = story; Fps = fps; SampleRate = sampleRate; SettingsIdentity = settings; ManifestSha256 = manifest;
        Assets = Array.AsReadOnly(assets.ToArray()); Events = Array.AsReadOnly(events.ToArray()); End = end;
        NoOwnedVoiceExpected = noOwnedVoiceExpected;
        TraceSha256 = CharacterVoiceIdentity.Hash(JsonSerializer.Serialize(new
        { Scope, StorySha256, Fps, SampleRate, SettingsIdentity, ManifestSha256, NoOwnedVoiceExpected, Events, End }));
    }
}
internal sealed class CharacterVoiceTraceException : InvalidOperationException
{
    internal string Code { get; }
    internal CharacterVoiceTraceException(string code, string message) : base(message) => Code = code;
}
internal static class CharacterVoiceIdentity
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static bool IsIdentifier(string? value) => value != null && value.StartsWith("aavt/voice/", StringComparison.Ordinal)
        && !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains(':') && !value.Contains(';')
        && value == value.Trim() && value.Split('/').All(part => part.Length != 0 && part != "." && part != "..");
}
internal sealed class CharacterVoiceSessionTrace
{
    internal const string Scope = "CharacterVoice-1.6.8-3c455a03-837f-40d5-a719-281df650be68-native-observed-events-v1";
    private readonly string _story, _settings, _sourceId, _manifest;
    private readonly int _fps, _rate;
    private readonly Dictionary<string, CharacterVoiceAsset> _assets;
    private readonly List<CharacterVoiceEvent> _events = new();
    private readonly CharacterVoiceReceipt? _replay;
    private readonly bool _noOwnedVoiceExpected;
    private long _sequence = -1;
    private int _ordinal = -1, _phase, _tenths;
    private CharacterVoiceAsset? _asset;
    private CharacterVoicePosition _last, _start, _end;
    private float _volume, _pitch;
    private string? _failure;
    private bool _sealed;
    internal long RuntimeSequence => _sequence;
    internal bool HasOpenRequest => _phase is 1 or 2 or 3;
    internal bool IsPlaying => _phase == 2;
    internal bool IsEnded => _phase == 3;
    internal bool IsCompleted => _phase == 4;
    internal bool IsFaulted => _failure != null;
    internal int EventCount => _events.Count;
    internal IReadOnlyList<CharacterVoiceAsset> Assets { get; }

    internal CharacterVoiceSessionTrace(string storyHash, int fps, int sampleRate, string settingsIdentity,
        IEnumerable<CharacterVoiceAsset> assets, CharacterVoiceReceipt? replay = null, bool noOwnedVoiceExpected = false)
    {
        if (!CharacterVoiceIdentity.IsHash(storyHash) || fps <= 0 || fps > 240 || sampleRate <= 0 || sampleRate > 384000
            || string.IsNullOrWhiteSpace(settingsIdentity))
            throw new CharacterVoiceTraceException("voice_scope_invalid", "A concrete story hash, frame rate, sample rate and playback settings are required.");
        var input = assets?.ToArray() ?? throw new CharacterVoiceTraceException("voice_asset_invalid", "Known voice assets are required.");
        if ((noOwnedVoiceExpected ? input.Length != 0 : input.Length < 1) || input.Length > 128 || input.Any(asset => asset == null || !CharacterVoiceIdentity.IsIdentifier(asset.Identifier)
            || !CharacterVoiceIdentity.IsHash(asset.FileSha256) || !CharacterVoiceIdentity.IsHash(asset.DecodedPcmSha256)
            || asset.SampleFrames <= 0 || asset.Channels is < 1 or > 8 || asset.SampleRate is <= 0 or > 384000)
            || input.Select(asset => asset.Identifier).Distinct(StringComparer.Ordinal).Count() != input.Length)
            throw new CharacterVoiceTraceException("voice_asset_invalid", "Known voices require unique canonical identifiers, content digests and nonempty native clip formats.");
        _story = storyHash.ToLowerInvariant(); _fps = fps; _rate = sampleRate; _settings = settingsIdentity;
        _noOwnedVoiceExpected = noOwnedVoiceExpected;
        _sourceId = "story/" + _story + "/character-voice-channel";
        var normalized = input.Select(asset => asset with { FileSha256 = asset.FileSha256.ToLowerInvariant(), DecodedPcmSha256 = asset.DecodedPcmSha256.ToLowerInvariant() })
            .OrderBy(asset => asset.Identifier, StringComparer.Ordinal).ToArray();
        _assets = normalized.ToDictionary(asset => asset.Identifier, StringComparer.Ordinal);
        Assets = Array.AsReadOnly(normalized);
        _manifest = CharacterVoiceIdentity.Hash(JsonSerializer.Serialize(normalized.Select(asset => new { asset.Identifier, asset.AssetId }).ToArray()));
        _replay = replay;
        if (replay != null && (replay.Scope != Scope || replay.StorySha256 != _story || replay.Fps != fps || replay.SampleRate != sampleRate
            || replay.SettingsIdentity != _settings || replay.ManifestSha256 != _manifest || replay.NoOwnedVoiceExpected != _noOwnedVoiceExpected))
            throw new CharacterVoiceTraceException("voice_replay_scope", "Prepared receipt does not match the story, settings, native clip manifest or clocks.");
    }
    internal void Request(long sequence, string identifier, CharacterVoicePosition at, double delay)
    {
        EnsureOpen(); Guard(!_noOwnedVoiceExpected, "voice_unexpected_request", "A real owned voice request contradicted the frozen no-owned-voice story scope.");
        Guard(!HasOpenRequest, "voice_event_order", "A new voice replaced an unfinished known request.");
        Guard(sequence >= 0 && sequence > _sequence, "voice_request_identity", "Native request identity did not advance.");
        Guard(_assets.TryGetValue(identifier, out var asset), "voice_asset_unknown", "An undeclared voice reached the active story: " + identifier);
        Guard(double.IsFinite(delay) && delay is >= 0 and <= 10, "voice_delay_boundary", "Authored delay is outside the inspected provider contract.");
        int tenths = (int)Math.Round(delay * 10, MidpointRounding.AwayFromZero);
        Guard(delay == tenths / 10d, "voice_delay_boundary", "Authored delay was not the provider's normalized tenth-second value.");
        ValidatePosition(at); int nextOrdinal = checked(_ordinal + 1);
        Append(new CharacterVoiceEvent(_events.Count, nextOrdinal, _sourceId, identifier, asset!.AssetId, "Request", at, delay, null, null));
        _sequence = sequence; _ordinal = nextOrdinal; _asset = asset; _tenths = tenths; _phase = 1;
    }
    internal void NativeStart(long sequence, CharacterVoicePosition at, string assetId, float volume, float pitch)
    {
        EnsureRequest(sequence); Guard(_phase == 1, "voice_event_order", "A native start must occur exactly once after its request.");
        ValidateParameters(assetId, volume, pitch); ValidatePosition(at);
        Append(Event("NativeStart", at, volume, pitch)); _start = at; _volume = volume; _pitch = pitch; _phase = 2;
    }
    internal void NativeEnd(long sequence, CharacterVoicePosition at)
    {
        EnsureRequest(sequence); Guard(_phase == 2, "voice_event_order", "A native end must occur exactly once after an observed start.");
        ValidatePosition(at); Guard(at.SampleFrame > _start.SampleFrame, "voice_event_order", "Playback ended without rendered sample progress.");
        Append(Event("NativeEnd", at)); _end = at; _phase = 3;
    }
    internal void DelayComplete(long sequence, CharacterVoicePosition at)
    {
        EnsureRequest(sequence); Guard(_phase == 3, "voice_event_order", "Delay completion requires one prior actual native end.");
        ValidatePosition(at); long due = checked(_end.Frame + ((long)_tenths * _fps + 9) / 10);
        Guard(at.Frame == due, "voice_delay_boundary", $"Authored delay released at frame {at.Frame}, expected first eligible frame {due}.");
        Append(Event("DelayComplete", at)); _phase = 4;
    }
    internal void ValidatePlayback(long sequence, string assetId, float volume, float pitch)
    {
        EnsureRequest(sequence); Guard(_phase == 2, "voice_event_order", "Playback validation requires an observed playing request.");
        ValidateParameters(assetId, volume, pitch);
        Guard(volume == _volume && pitch == _pitch, "voice_playback_changed", "Native voice gain or pitch changed after its start.");
    }
    internal void Abort(string reason) { if (!_sealed) _failure ??= string.IsNullOrWhiteSpace(reason) ? "Native voice was aborted." : reason; }
    internal CharacterVoiceReceipt Seal(CharacterVoicePosition at)
    {
        EnsureOpen();
        if (_noOwnedVoiceExpected)
            Guard(_events.Count == 0 && _phase == 0, "voice_unexpected_request", "A no-owned-voice receipt contains an unexpected voice request.");
        else
        {
            Guard(_events.Count != 0, "voice_session_empty", "No real known voice request was observed.");
            Guard(_phase == 4, "voice_session_incomplete", "A voice request lacks its native start, end or delay completion.");
        }
        ValidatePosition(at);
        if (_replay != null)
        {
            Guard(_events.Count == _replay.Events.Count, "voice_replay_mismatch", "Replay omitted prepared voice events.");
            Guard(at == _replay.End, "voice_replay_mismatch", "Replay ended at a different frame/sample/story coordinate.");
        }
        var receipt = new CharacterVoiceReceipt(_story, _fps, _rate, _settings, _manifest, Assets, _events, at, _noOwnedVoiceExpected);
        if (_replay != null) Guard(receipt.TraceSha256 == _replay.TraceSha256, "voice_replay_mismatch", "Replay receipt digest differs.");
        _sealed = true; return receipt;
    }
    private CharacterVoiceEvent Event(string operation, CharacterVoicePosition at, float? volume = null, float? pitch = null) =>
        new(_events.Count, _ordinal, _sourceId, _asset!.Identifier, _asset.AssetId, operation, at, _tenths / 10d, volume, pitch);
    private void Append(CharacterVoiceEvent item)
    {
        if (_replay != null) Guard(_events.Count < _replay.Events.Count && item == _replay.Events[_events.Count], "voice_replay_mismatch",
            "Native event differs from preparation at event " + _events.Count + ": " + item.Operation);
        _events.Add(item); _last = item.Position;
    }
    private void ValidatePosition(CharacterVoicePosition at) => Guard(at.Frame >= 0 && at.SampleFrame >= 0 && at.Row >= 0
        && (_events.Count == 0 || at.Frame >= _last.Frame && at.SampleFrame >= _last.SampleFrame),
        "voice_position_invalid", "Event coordinates must be nonnegative and monotonic relative to the authored pass.");
    private void ValidateParameters(string assetId, float volume, float pitch)
    {
        Guard(assetId == _asset!.AssetId, "voice_clip_identity", "The native clip does not match the pinned asset.");
        Guard(float.IsFinite(volume) && volume is >= 0 and <= 1 && float.IsFinite(pitch) && pitch is > 0 and <= 3,
            "voice_playback_changed", "Native gain or pitch is invalid for this scoped receipt.");
    }
    private void EnsureRequest(long sequence)
    { EnsureOpen(); Guard(sequence == _sequence && _asset != null, "voice_request_identity", "An event belongs to a stale or unobserved request."); }
    private void EnsureOpen()
    {
        if (_failure != null) throw new CharacterVoiceTraceException("voice_session_faulted", _failure);
        if (_sealed) throw new CharacterVoiceTraceException("voice_session_sealed", "The receipt is already sealed.");
    }
    private void Guard(bool condition, string code, string message)
    {
        if (condition) return;
        _failure ??= message; throw new CharacterVoiceTraceException(code, message);
    }
}

internal sealed record CharacterVoiceBoundStory(string StoryKey, IReadOnlyList<string> Identifiers)
{
    internal string StorySha256 => AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled ? StoryKey
        : throw new InvalidOperationException("Production execution binding does not contain an audited story digest.");
    internal bool NoOwnedVoiceExpected => Identifiers.Count == 0;
}
internal sealed class CharacterVoiceStoryBinding
{
    private CharacterVoiceBoundStory? story;
    private string? failure;
    internal bool IsBound => story != null && failure == null;
    internal bool IsFaulted => failure != null;
    internal void BindStory(string hash, IEnumerable<string> identifiers)
    {
        RequireHealthy();
        if (story != null) Fail("voice_story_rebound", "The retained backend cannot bind or replace its story twice.");
        if (AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled ? !CharacterVoiceIdentity.IsHash(hash) : string.IsNullOrWhiteSpace(hash))
            Fail("voice_scope_invalid", "A concrete selected story key is required; development auditing also requires its digest.");
        var values = identifiers?.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (values == null || values.Length > 128 || values.Any(value => !CharacterVoiceIdentity.IsIdentifier(value)))
            Fail("voice_asset_invalid", "Story binding requires a frozen list of at most 128 canonical owned identifiers, including an explicit empty list.");
        story = new CharacterVoiceBoundStory(AAVideoExport.Core.ExportBuildProfile.DevelopmentAuditEnabled ? hash.ToLowerInvariant() : hash, Array.AsReadOnly(values!));
    }
    internal CharacterVoiceBoundStory RequireBound(string operation)
    {
        RequireHealthy();
        if (story == null) Fail("voice_story_unbound", operation + " requires an immutable story binding on this retained backend.");
        return story!;
    }
    private void RequireHealthy()
    {
        if (failure != null) throw new CharacterVoiceTraceException("voice_binding_faulted", failure);
    }
    private void Fail(string code, string message)
    {
        failure ??= message;
        throw new CharacterVoiceTraceException(code, message);
    }
}

internal sealed record CharacterVoiceCommitFilePin(string Path, string Sha256);
internal static class CharacterVoiceCommitFiles
{
    internal static IDisposable Acquire(IEnumerable<CharacterVoiceCommitFilePin> pins)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var pin in pins)
            {
                if (!Path.IsPathFullyQualified(pin.Path) || !CharacterVoiceIdentity.IsHash(pin.Sha256))
                    throw new CharacterVoiceTraceException("voice_commit_pin_invalid", "Publication requires concrete absolute voice file pins.");
                var stream = new FileStream(pin.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                using var sha = SHA256.Create();
                string actual = Convert.ToHexString(sha.ComputeHash(stream));
                if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new CharacterVoiceTraceException("voice_commit_file_changed", "A pinned voice resource or provider binary changed before publication.");
            }
            return new FilesLease(streams);
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }
    private sealed class FilesLease : IDisposable
    {
        private List<FileStream>? streams;
        internal FilesLease(List<FileStream> streams) => this.streams = streams;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref streams, null);
            if (owned == null) return;
            var errors = new List<Exception>();
            foreach (var stream in owned)
                try { stream.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Voice publication file handles did not all close cleanly.", errors);
        }
    }
}
internal sealed class CharacterVoiceCommitState : IDisposable
{
    private readonly object sync = new();
    private CharacterVoiceReceipt? preparation, replay;
    private Exception? failure;
    private int pass;
    private bool sealedForCommit, disposed;
    internal bool IsHealthy { get { lock (sync) return failure == null && !disposed; } }
    internal string FailureReason { get { lock (sync) return failure?.Message ?? (disposed ? "The retained voice scope was disposed." : ""); } }

    internal void BeginPass(CharacterVoiceReceipt? expected) => Run(() =>
    {
        Require(pass == 0 && replay == null && !sealedForCommit, "voice_commit_order", "A third or overlapping voice pass cannot replace retained publication proof.");
        if (preparation == null)
        {
            Require(expected == null, "voice_commit_order", "A replay cannot precede preparation on this retained scope."); pass = 1;
        }
        else
        {
            Require(expected != null && Equivalent(preparation, expected), "voice_commit_order", "Replay must use this scope's completed preparation receipt."); pass = 2;
        }
        return true;
    });

    internal void CompletePass(CharacterVoiceReceipt receipt) => Run(() =>
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Require(pass is 1 or 2, "voice_commit_order", "No voice pass is awaiting a successful seal.");
        if (pass == 1) preparation = receipt;
        else
        {
            Require(preparation != null && Equivalent(preparation, receipt), "voice_commit_mismatch", "Replay did not match the retained preparation receipt."); replay = receipt;
        }
        pass = 0; return true;
    });

    internal void SealForCommit() => Run(() =>
    {
        Require(pass == 0 && preparation != null && replay != null && Equivalent(preparation, replay),
            "voice_commit_incomplete", "Publication requires successful preparation and matching replay receipts on this exact retained scope.");
        sealedForCommit = true; return true;
    });

    internal void ValidateForCommit(Action verify) => Run(() => { RequireCommit(); verify(); return true; });

    internal IDisposable AcquireCommitScope(Func<IDisposable> acquire)
    {
        Monitor.Enter(sync);
        try
        {
            RequireHealthy(); RequireCommit();
            var resources = acquire() ?? throw new InvalidOperationException("Publication resource lease was not returned.");
            return new PublicationLease(this, resources);
        }
        catch (Exception error) { failure ??= error; Monitor.Exit(sync); throw; }
    }

    internal T Run<T>(Func<T> operation)
    {
        lock (sync)
        {
            RequireHealthy();
            try { return operation(); }
            catch (Exception error) { failure ??= error; throw; }
        }
    }

    internal void Fail(Exception error, Action? onLatched = null)
    { lock (sync) { failure ??= error; onLatched?.Invoke(); } }

    internal void Cleanup(Action action)
    { lock (sync) { try { action(); } catch (Exception error) { failure ??= error; throw; } } }

    internal void DisposeBackend(Action action)
    { lock (sync) { disposed = true; try { action(); } catch (Exception error) { failure ??= error; throw; } } }

    public void Dispose() => DisposeBackend(() => { });

    private void RequireCommit() => Require(sealedForCommit && pass == 0 && preparation != null && replay != null && Equivalent(preparation, replay),
        "voice_commit_incomplete", "A matching two-pass voice publication receipt has not been sealed.");
    private void RequireHealthy()
    {
        if (disposed) throw new CharacterVoiceTraceException("voice_commit_disposed", "The retained voice scope was disposed.");
        if (failure != null) throw new CharacterVoiceTraceException("voice_commit_faulted", failure.Message);
    }
    private static bool Equivalent(CharacterVoiceReceipt left, CharacterVoiceReceipt right) =>
        left.Scope == right.Scope && left.TraceSha256 == right.TraceSha256 && left.StorySha256 == right.StorySha256
        && left.Fps == right.Fps && left.SampleRate == right.SampleRate && left.SettingsIdentity == right.SettingsIdentity
        && left.ManifestSha256 == right.ManifestSha256 && left.NoOwnedVoiceExpected == right.NoOwnedVoiceExpected
        && left.End == right.End && left.Assets.SequenceEqual(right.Assets) && left.Events.SequenceEqual(right.Events);
    private static void Require(bool condition, string code, string message)
    { if (!condition) throw new CharacterVoiceTraceException(code, message); }

    private sealed class PublicationLease : IDisposable
    {
        private CharacterVoiceCommitState? owner;
        private readonly IDisposable resources;
        private readonly int thread = Environment.CurrentManagedThreadId;
        internal PublicationLease(CharacterVoiceCommitState owner, IDisposable resources) { this.owner = owner; this.resources = resources; }
        public void Dispose()
        {
            var active = owner;
            if (active == null) return;
            if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Publication lease must end on the acquiring worker without an await.");
            owner = null;
            try { resources.Dispose(); }
            catch (Exception error) { active.failure ??= error; throw; }
            finally { Monitor.Exit(active.sync); }
        }
    }
}
