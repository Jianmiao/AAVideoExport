using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AAVideoExport.NativePath.Tests;

internal sealed record ControlledStoryPassTrace(int Attempt, bool Preparation, int Fps, long CapturedFrames,
    JsonElement Events, JsonElement VoiceReceipt, JsonElement BackgroundCommands)
{
    public JsonElement? VoiceObservation { get; init; }
    public JsonElement? PcmObservation { get; init; }
}

internal static class ControlledStoryTrajectory
{
    // This is a test-only content/event comparator. It neither injects product
    // audit records nor requires a production publication baseline. Event/frame/sample/command order is never sorted.
    // Only JSON object property order and the explicitly named process-local
    // metadata below are normalized. Unknown fields/kinds fail closed.
    internal static JsonElement Validate(IReadOnlyList<ControlledStoryPassTrace> passes, int expectedAttempts)
    {
        if (expectedAttempts <= 0 || passes.Count != checked(expectedAttempts * 2)
            || passes.Any(pass => pass.Attempt <= 0) || passes.Select(pass => pass.Attempt).Distinct().Count() != expectedAttempts)
            throw new InvalidDataException("Completed attempt/pass count differs from requested trajectory acceptance.");
        foreach (var attempt in passes.GroupBy(pass => pass.Attempt))
        {
            if (attempt.Count() != 2 || attempt.Count(pass => pass.Preparation) != 1)
                throw new InvalidDataException("Each completed attempt needs exactly one preparation and one replay trajectory.");
        }
        var normalized = passes.Select(Normalize).ToArray();
        foreach (var attempt in normalized.GroupBy(pass => pass.Input.Attempt))
            Compare(attempt.Single(pass => pass.Input.Preparation), attempt.Single(pass => !pass.Input.Preparation));
        var baseline = normalized[0];
        foreach (var candidate in normalized.Skip(1)) Compare(baseline, candidate);
        return JsonSerializer.SerializeToElement(new {
            Version = passes.Any(pass => pass.VoiceObservation != null) ? 2 : 1, CompletedAttempts = expectedAttempts, BaselineSha256 = baseline.Sha256,
            Passes = normalized.Select(pass => new { pass.Input.Attempt, pass.Input.Preparation, pass.Sha256, Trajectory = pass.Payload }).ToArray(),
            Normalization = "Object property order only; pass-begin Attempt/Preparation and original-layer-rendered layer/image addresses are excluded. All observed frame/sample positions, ordered events, directives, visibility, voice evidence/assets and background commands remain exact. Production PCM compares every float in the fixed authored window; native mixer tail counts remain diagnostic only. No wall-time fields enter content comparison."
        });
    }

    internal static JsonElement ValidateSinglePass(IReadOnlyList<ControlledStoryPassTrace> passes, int expectedAttempts)
    {
        if (expectedAttempts <= 0 || passes.Count != expectedAttempts || passes.Any(pass => pass.Attempt <= 0 || pass.Preparation)
            || passes.Select(pass => pass.Attempt).Distinct().Count() != expectedAttempts)
            throw new InvalidDataException("Single-pass acceptance needs exactly one original non-preparation pass per completed attempt.");
        var normalized = passes.Select(Normalize).ToArray();
        var baseline = normalized[0];
        foreach (var candidate in normalized.Skip(1)) Compare(baseline, candidate);
        return JsonSerializer.SerializeToElement(new {
            Version = 3, Mode = "single-live-pass", CompletedAttempts = expectedAttempts, BaselineSha256 = baseline.Sha256,
            Passes = normalized.Select(pass => new { pass.Input.Attempt, pass.Input.Preparation, pass.Sha256, Trajectory = pass.Payload }).ToArray(),
            Normalization = "One original playback per movie. Only object property order and documented process-local metadata are normalized; original voice lifecycle, frame/sample positions, all authored PCM, effects and background commands remain exact across repeated movies."
        });
    }

    private static NormalizedPass Normalize(ControlledStoryPassTrace pass)
    {
        if (pass.Fps <= 0 || pass.CapturedFrames <= 0) throw new InvalidDataException("Trajectory needs positive fps and captured frame count.");
        RequireArray(pass.Events, "authored events");
        if (pass.Events.GetArrayLength() == 0) throw new InvalidDataException("An empty event trajectory cannot prove repeated behavior.");
        var events = new List<JsonElement>();
        foreach (var entry in pass.Events.EnumerateArray())
        {
            RequireFields(entry, "event", "kind", "frame", "detail");
            string kind = entry.GetProperty("kind").GetString() ?? throw new InvalidDataException("Event kind is missing.");
            if (!entry.GetProperty("frame").TryGetInt64(out long frame) || frame < 0)
                throw new InvalidDataException("Observed event frame must be a nonnegative integer.");
            var detail = entry.GetProperty("detail");
            string[] fields = kind switch {
                "pass-begin" => new[] { "Attempt", "Preparation", "Fps" },
                "native-row" => new[] { "row", "isBackgroundLatest" },
                "original-title-ready" => new[] { "isBackgroundLatest" },
                "original-title-complete" => new[] { "row", "hasCompleted" },
                "original-layer-rendered" => new[] { "isBackgroundLatest", "visible", "layer", "image", "texture", "material" },
                "original-preset-success" => new[] { "kind", "directive", "row" },
                "voice-receipt-sealed" => new[] { "trace" },
                "voice-observation-complete" => new[] { "requests" },
                "host-production-pass-finished" => new[] { "CapturedFrames", "hasVoiceObservation" },
                "host-pass-finished" => new[] { "CapturedFrames", "hasVoiceReceipt" },
                _ => throw new InvalidDataException("Unknown event kind needs an explicit trajectory schema: " + kind)
            };
            RequireFields(detail, kind + " detail", fields);
            if (kind == "pass-begin" && (detail.GetProperty("Attempt").GetInt32() != pass.Attempt
                || detail.GetProperty("Preparation").GetBoolean() != pass.Preparation || detail.GetProperty("Fps").GetInt32() != pass.Fps))
                throw new InvalidDataException("Pass-begin metadata differs from its observed pass.");
            // These are the only dropped fields. Native pointers identify a
            // live object, not its output; semantic layer ordinals remain in
            // every background command. Attempt and pass type label the
            // comparison and cannot themselves be repeated-output content.
            string[] excluded = kind == "pass-begin" ? new[] { "Attempt", "Preparation" }
                : kind == "original-layer-rendered" ? new[] { "layer", "image" } : Array.Empty<string>();
            var normalizedDetail = detail.EnumerateObject().Where(property => !excluded.Contains(property.Name, StringComparer.Ordinal))
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            events.Add(Canonical(JsonSerializer.SerializeToElement(new { kind, frame, detail = normalizedDetail })));
        }
        if (pass.VoiceObservation is { } observation)
        {
            if (pass.VoiceReceipt.ValueKind != JsonValueKind.Undefined)
                throw new InvalidDataException("External production voice observation cannot substitute for or coexist with a product audit receipt.");
            ValidateVoiceObservation(observation);
            if (pass.PcmObservation == null) throw new InvalidDataException("Production trajectory lacks independently observed native PCM.");
        }
        else ValidateVoice(pass.VoiceReceipt);
        RequireArray(pass.BackgroundCommands, "background commands");
        foreach (var command in pass.BackgroundCommands.EnumerateArray())
            RequireFields(command, "background command", "Sequence", "Frame", "Operation", "Layer", "Resource", "SourceSha256", "SourceFrame");
        var normalizedEvents = Canonical(JsonSerializer.SerializeToElement(events));
        var voice = Canonical(pass.VoiceObservation ?? pass.VoiceReceipt); var background = Canonical(pass.BackgroundCommands);
        var pcm = pass.PcmObservation is { } observedPcm ? NormalizePcm(observedPcm, pass) : default(JsonElement?);
        var payload = pass.VoiceObservation == null ? Canonical(JsonSerializer.SerializeToElement(new {
            Version = 1, pass.Fps, pass.CapturedFrames, Events = normalizedEvents, VoiceReceipt = voice, BackgroundCommands = background
        })) : Canonical(JsonSerializer.SerializeToElement(new {
            Version = 2, pass.Fps, pass.CapturedFrames, Events = normalizedEvents, VoiceObservation = voice, NativePcm = pcm, BackgroundCommands = background
        }));
        return new NormalizedPass(pass, normalizedEvents, voice, background, payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))));
    }

    private static void ValidateVoice(JsonElement receipt)
    {
        RequireFields(receipt, "voice receipt", "Scope", "StorySha256", "Fps", "SampleRate", "SettingsIdentity", "ManifestSha256",
            "Assets", "Events", "End", "TraceSha256", "NoOwnedVoiceExpected");
        RequireArray(receipt.GetProperty("Assets"), "voice assets");
        foreach (var asset in receipt.GetProperty("Assets").EnumerateArray())
            RequireFields(asset, "voice asset", "Identifier", "FileSha256", "DecodedPcmSha256", "SampleFrames", "Channels", "SampleRate", "AssetId");
        RequireArray(receipt.GetProperty("Events"), "voice events");
        foreach (var entry in receipt.GetProperty("Events").EnumerateArray())
        {
            RequireFields(entry, "voice event", "Index", "RequestOrdinal", "SourceId", "VoiceIdentifier", "AssetId", "Operation", "Position", "PostDelaySeconds", "Volume", "Pitch");
            RequireFields(entry.GetProperty("Position"), "voice event position", "Frame", "SampleFrame", "Row");
        }
        RequireFields(receipt.GetProperty("End"), "voice end position", "Frame", "SampleFrame", "Row");
    }

    private static void Compare(NormalizedPass expected, NormalizedPass actual)
    {
        string context = $"attempt {expected.Input.Attempt}/{(expected.Input.Preparation ? "preparation" : "replay")} vs attempt {actual.Input.Attempt}/{(actual.Input.Preparation ? "preparation" : "replay")}";
        if (expected.Input.Fps != actual.Input.Fps || expected.Input.CapturedFrames != actual.Input.CapturedFrames)
            throw new InvalidDataException("Repeated trajectory frame layout differs: " + context);
        RequireEqual(expected.Events, actual.Events, "authored events", context);
        RequireEqual(expected.Voice, actual.Voice, expected.Input.VoiceObservation == null ? "voice receipt" : "external voice observation", context);
        RequireEqual(expected.Background, actual.Background, "background commands", context);
        if (expected.Input.PcmObservation is { } expectedPcm && actual.Input.PcmObservation is { } actualPcm)
            RequireEqual(NormalizePcm(expectedPcm, expected.Input), NormalizePcm(actualPcm, actual.Input), "native authored PCM", context);
        else if (expected.Input.PcmObservation != null || actual.Input.PcmObservation != null)
            throw new InvalidDataException("Native PCM observation is missing from one compared pass.");
    }

    private static void ValidateVoiceObservation(JsonElement observation)
    {
        RequireFields(observation, "external voice observation", "Schema", "Fps", "SampleRate", "Assets", "Events", "End");
        if (observation.GetProperty("Schema").GetString() != "aave.native-test-voice.v1") throw new InvalidDataException("External voice observation schema is unknown.");
        RequireArray(observation.GetProperty("Assets"), "external voice assets");
        foreach (var asset in observation.GetProperty("Assets").EnumerateArray())
            RequireFields(asset, "external voice asset", "Identifier", "FileSha256", "DecodedPcmSha256", "SampleFrames", "Channels", "SampleRate");
        RequireArray(observation.GetProperty("Events"), "external native voice lifecycle");
        var events = observation.GetProperty("Events").EnumerateArray().ToArray();
        if (events.Length == 0) throw new InvalidDataException("External native voice lifecycle is empty.");
        for (int i = 0; i < events.Length; i++)
        {
            var entry = events[i];
            RequireFields(entry, "external native voice lifecycle", "Index", "RequestOrdinal", "Operation", "VoiceIdentifier", "Position", "PostDelaySeconds", "Volume", "Pitch");
            if (entry.GetProperty("Index").GetInt32() != i) throw new InvalidDataException("External native voice lifecycle event order is not contiguous.");
            RequireFields(entry.GetProperty("Position"), "external voice position", "Frame", "SampleFrame", "Row");
        }
        var requests = events.GroupBy(e => e.GetProperty("RequestOrdinal").GetInt32()).ToArray();
        if (!requests.Select(group => group.Key).SequenceEqual(Enumerable.Range(0, requests.Length))
            || requests.Any(group => !group.Select(e => e.GetProperty("Operation").GetString()).SequenceEqual(new[] { "Request", "NativeStart", "NativeEnd", "DelayComplete" })))
            throw new InvalidDataException("External native voice lifecycle lacks exactly one ordered request/start/end/delay sequence.");
        RequireFields(observation.GetProperty("End"), "external voice end position", "Frame", "SampleFrame", "Row");
    }
    private static JsonElement NormalizePcm(JsonElement pcm, ControlledStoryPassTrace pass)
    {
        RequireFields(pcm, "native PCM observation", "Schema", "Frames", "Fps", "SampleRate", "Channels", "SampleFrames", "ContentSha256", "RawSampleFrames", "TailSampleFrames");
        long frames = pcm.GetProperty("Frames").GetInt64(), samples = pcm.GetProperty("SampleFrames").GetInt64();
        int fps = pcm.GetProperty("Fps").GetInt32(), rate = pcm.GetProperty("SampleRate").GetInt32(), channels = pcm.GetProperty("Channels").GetInt32();
        long raw = pcm.GetProperty("RawSampleFrames").GetInt64(), tail = pcm.GetProperty("TailSampleFrames").GetInt64();
        string hash = pcm.GetProperty("ContentSha256").GetString() ?? "";
        if (pcm.GetProperty("Schema").GetString() != "aave.native-test-pcm.v1" || frames != pass.CapturedFrames || fps != pass.Fps
            || rate <= 0 || channels <= 0 || checked(samples * fps) != checked(frames * rate) || raw < samples || tail != raw - samples
            || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Native PCM observation has an invalid authored sample window.");
        // Native mixer tail beyond the exact movie window is diagnostic data;
        // every authored float sample in that fixed window remains compared.
        return Canonical(JsonSerializer.SerializeToElement(new { Frames = frames, Fps = fps, SampleRate = rate, Channels = channels, SampleFrames = samples, ContentSha256 = hash }));
    }

    private static void RequireEqual(JsonElement expected, JsonElement actual, string category, string context)
    {
        if (expected.GetRawText() == actual.GetRawText()) return;
        throw new InvalidDataException($"Repeated {category} differ ({context}); first difference: {Difference(expected, actual, "$")}.");
    }
    private static string Difference(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) return path + " type";
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength()) return path + ".length";
            for (int index = 0; index < expected.GetArrayLength(); index++)
                if (expected[index].GetRawText() != actual[index].GetRawText()) return Difference(expected[index], actual[index], path + "[" + index + "]");
        }
        else if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
                if (!actual.TryGetProperty(property.Name, out var value)) return path + "." + property.Name + " missing";
                else if (property.Value.GetRawText() != value.GetRawText()) return Difference(property.Value, value, path + "." + property.Name);
        }
        return path + ": expected=" + expected.GetRawText() + ", actual=" + actual.GetRawText();
    }
    private static void RequireFields(JsonElement value, string context, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(context + " schema requires an object.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Except(expected, StringComparer.Ordinal).Any())
            throw new InvalidDataException(context + " schema changed; unknown, missing, or duplicate fields require explicit review.");
    }
    private static void RequireArray(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException(context + " schema requires an array.");
    }
    private static JsonElement Canonical(JsonElement value)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteCanonical(writer, value);
        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    private sealed record NormalizedPass(ControlledStoryPassTrace Input, JsonElement Events, JsonElement Voice,
        JsonElement Background, JsonElement Payload, string Sha256);
}
