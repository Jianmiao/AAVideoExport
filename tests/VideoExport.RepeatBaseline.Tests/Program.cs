using AAVideoExport.Core;

const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string trace = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const string pcm = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
const string changed = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
const string events = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
const string voice = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
const string background = "1111111111111111111111111111111111111111111111111111111111111111";
int passed = 0, failed = 0;
Run("legacy v1 receipt cannot silently become a complete event baseline", () => WithDirectory(directory =>
{
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, identity + ".json");
    string original = System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Identity = identity, Frames = 100, FrameTraceSha256 = trace, PcmSha256 = pcm });
    File.WriteAllText(path, original);
    Throws("baseline_invalid", () => new RepeatExportBaseline(directory, identity).Validate(100, trace, pcm, events, voice, background));
    Throws("baseline_invalid", () => new RepeatExportBaseline(directory, identity).Commit(100, trace, pcm, events, voice, background));
    Equal(original, File.ReadAllText(path));
}));
Run("v2 receipt persists explicit event voice and background output fingerprints", () => WithDirectory(directory =>
{
    OutputCall(new RepeatExportBaseline(directory, identity), "Commit", events, voice, background);
    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "*.json").Single()));
    Equal(2, json.RootElement.GetProperty("Version").GetInt32());
    Equal(events, json.RootElement.GetProperty("EventTraceSha256").GetString());
    Equal(voice, json.RootElement.GetProperty("VoiceReceiptSha256").GetString());
    Equal(background, json.RootElement.GetProperty("BackgroundCommandsSha256").GetString());
}));
foreach (string field in new[] { "event", "voice", "background" })
Run("same input rejects changed " + field + " timing with identical frame trace and PCM", () => WithDirectory(directory =>
{
    var store = new RepeatExportBaseline(directory, identity);
    OutputCall(store, "Commit", events, voice, background);
    byte[] original = File.ReadAllBytes(Directory.GetFiles(directory, "*.json").Single());
    Action changedOutput = () => OutputCall(store, "Validate", field == "event" ? changed : events, field == "voice" ? changed : voice, field == "background" ? changed : background);
    Throws("timeline_repeat_mismatch", changedOutput);
    Throws("timeline_repeat_mismatch", () => OutputCall(store, "Commit", field == "event" ? changed : events, field == "voice" ? changed : voice, field == "background" ? changed : background));
    Check(original.SequenceEqual(File.ReadAllBytes(Directory.GetFiles(directory, "*.json").Single())), "output mismatch replaced the successful receipt");
}));
Run("different output events racing for one input key have one immutable winner", () => WithDirectory(directory =>
{
    int wins = 0, rejects = 0;
    Parallel.For(0, 16, index =>
    {
        string eventHash = index.ToString("x64");
        try { OutputCall(new RepeatExportBaseline(directory, identity), "Commit", eventHash, voice, background); Interlocked.Increment(ref wins); }
        catch (ExportException error) when (error.Code == "timeline_repeat_mismatch") { Interlocked.Increment(ref rejects); }
    });
    Equal(1, wins); Equal(15, rejects); Equal(1, Directory.GetFiles(directory).Length);
}));
Run("incomplete v2 output proof fails without overwriting the existing receipt", () => WithDirectory(directory =>
{
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, identity + ".json");
    string original = System.Text.Json.JsonSerializer.Serialize(new { Version = 2, Identity = identity, Frames = 100, FrameTraceSha256 = trace, PcmSha256 = pcm,
        EventTraceSha256 = events, VoiceReceiptSha256 = voice });
    File.WriteAllText(path, original);
    Throws("baseline_invalid", () => OutputCall(new RepeatExportBaseline(directory, identity), "Commit", events, voice, background));
    Equal(original, File.ReadAllText(path));
}));
Run("validation before success does not create storage or baseline", () => WithDirectory(directory =>
{
    new RepeatExportBaseline(directory, identity).Validate(100, trace, pcm, events, voice, background);
    Check(!Directory.Exists(directory), "Validate created a directory before export success");
}));
Run("first successful commit persists across instances", () => WithDirectory(directory =>
{
    var first = new RepeatExportBaseline(directory, identity);
    first.Validate(100, trace, pcm, events, voice, background);
    first.Commit(100, trace, pcm, events, voice, background);
    Check(Directory.Exists(directory), "Commit did not create durable storage");
    Equal(1, Directory.GetFiles(directory).Length);
    new RepeatExportBaseline(directory, identity).Validate(100, trace, pcm, events, voice, background);
}));
Run("same identity with changed frame count is rejected and kept", () => WithDirectory(directory =>
{
    new RepeatExportBaseline(directory, identity).Commit(100, trace, pcm, events, voice, background);
    var bytes = File.ReadAllBytes(Directory.GetFiles(directory).Single());
    var next = new RepeatExportBaseline(directory, identity);
    Throws("timeline_repeat_mismatch", () => next.Validate(101, trace, pcm, events, voice, background));
    Throws("timeline_repeat_mismatch", () => next.Commit(101, trace, pcm, events, voice, background));
    Check(bytes.SequenceEqual(File.ReadAllBytes(Directory.GetFiles(directory).Single())), "mismatching export overwrote baseline");
}));
Run("same identity with changed frame trace is rejected", () => WithDirectory(directory =>
{
    var store = new RepeatExportBaseline(directory, identity);
    store.Commit(100, trace, pcm, events, voice, background);
    Throws("timeline_repeat_mismatch", () => store.Validate(100, changed, pcm, events, voice, background));
}));
Run("same identity with changed PCM is rejected", () => WithDirectory(directory =>
{
    var store = new RepeatExportBaseline(directory, identity);
    store.Commit(100, trace, pcm, events, voice, background);
    Throws("timeline_repeat_mismatch", () => store.Validate(100, trace, changed, events, voice, background));
}));
Run("changed identity receives separate baseline", () => WithDirectory(directory =>
{
    new RepeatExportBaseline(directory, identity).Commit(100, trace, pcm, events, voice, background);
    var next = new RepeatExportBaseline(directory, changed);
    next.Validate(200, pcm, trace, events, voice, background);
    next.Commit(200, pcm, trace, events, voice, background);
    Equal(2, Directory.GetFiles(directory).Length);
}));
Run("matching concurrent commits leave one complete baseline", () => WithDirectory(directory =>
{
    Parallel.For(0, 20, _ => new RepeatExportBaseline(directory, identity).Commit(100, trace, pcm, events, voice, background));
    Equal(1, Directory.GetFiles(directory).Length);
    new RepeatExportBaseline(directory, identity).Validate(100, trace, pcm, events, voice, background);
}));
Run("mismatching concurrent commits choose one winner without overwrite", () => WithDirectory(directory =>
{
    int wins = 0, rejects = 0;
    var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    Parallel.For(0, 20, index =>
    {
        try { new RepeatExportBaseline(directory, identity).Commit(100 + index, trace, pcm, events, voice, background); Interlocked.Increment(ref wins); }
        catch (ExportException error) when (error.Code == "timeline_repeat_mismatch") { Interlocked.Increment(ref rejects); }
        catch (Exception error) { errors.Enqueue(error); }
    });
    Check(errors.IsEmpty, "unexpected race error: " + string.Join("; ", errors.Select(error => error.Message)));
    Equal(1, wins);
    Equal(19, rejects);
    Equal(1, Directory.GetFiles(directory).Length);
}));
Run("corrupt baseline is rejected rather than replaced", () => WithDirectory(directory =>
{
    var store = new RepeatExportBaseline(directory, identity);
    store.Commit(100, trace, pcm, events, voice, background);
    var file = Directory.GetFiles(directory).Single();
    File.WriteAllText(file, "truncated{");
    Throws("baseline_invalid", () => store.Validate(100, trace, pcm, events, voice, background));
    Throws("baseline_invalid", () => store.Commit(100, trace, pcm, events, voice, background));
    Equal("truncated{", File.ReadAllText(file));
    Equal(1, Directory.GetFiles(directory).Length);
}));
Run("hash casing does not create duplicate identities", () => WithDirectory(directory =>
{
    new RepeatExportBaseline(directory, identity.ToUpperInvariant()).Commit(100, trace.ToUpperInvariant(), pcm.ToUpperInvariant(), events, voice, background);
    new RepeatExportBaseline(directory, identity).Validate(100, trace, pcm, events, voice, background);
    new RepeatExportBaseline(directory, identity).Commit(100, trace, pcm, events, voice, background);
    Equal(1, Directory.GetFiles(directory).Length);
}));
Run("invalid identity cannot address files outside receipt storage", () => WithDirectory(directory =>
{
    Argument(() => new RepeatExportBaseline(directory, "..\\outside"));
    Argument(() => new RepeatExportBaseline(directory, ""));
    Argument(() => new RepeatExportBaseline(directory, new string('x', 64)));
    Check(!Directory.Exists(directory), "invalid identity changed storage");
}));
Run("invalid receipt cannot become a baseline", () => WithDirectory(directory =>
{
    var store = new RepeatExportBaseline(directory, identity);
    Argument(() => store.Commit(0, trace, pcm, events, voice, background));
    Argument(() => store.Commit(100, "wrong", pcm, events, voice, background));
    Argument(() => store.Commit(100, trace, "wrong", events, voice, background));
    Argument(() => store.Commit(100, trace, pcm, "wrong", voice, background));
    Argument(() => store.Commit(100, trace, pcm, events, "wrong", background));
    Argument(() => store.Commit(100, trace, pcm, events, voice, "wrong"));
    Check(!Directory.Exists(directory), "invalid receipt changed storage");
}));
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
static void WithDirectory(Action<string> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "aave-baseline-test-" + Guid.NewGuid().ToString("N"));
    try { test(directory); }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
static void Throws(string code, Action test)
{
    try { test(); }
    catch (ExportException error) { Equal(code, error.Code); return; }
    throw new Exception("Expected " + code);
}
static void Argument(Action test)
{
    try { test(); } catch (ArgumentException) { return; }
    throw new Exception("Expected invalid argument rejection");
}
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void OutputCall(RepeatExportBaseline store, string operation, string eventHash, string voiceHash, string backgroundHash)
{
    if (operation == "Validate") store.Validate(100, new string('b', 64), new string('c', 64), eventHash, voiceHash, backgroundHash);
    else if (operation == "Commit") store.Commit(100, new string('b', 64), new string('c', 64), eventHash, voiceHash, backgroundHash);
    else throw new ArgumentOutOfRangeException(nameof(operation));
}
