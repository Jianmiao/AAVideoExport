using System.Security.Cryptography;
using AAVideoExport.Core;

var passed = 0;
var failed = 0;
Run("publishes and reads a complete immutable cache", TestPublishRead);
Run("identity mismatch is a cache miss", TestIdentityMismatch);
Run("corrupt cache is rejected without deleting evidence", TestCorruptCache);
Run("existing cache winner is never overwritten", TestNoOverwrite);
Run("cache-owned PCM and transcript readers do not delete artifacts", TestOpenSealed);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void TestPublishRead()
{
    using var root = TempDirectory();
    string identity = Hash("identity"), pcm = Path.Combine(root.Path, "source.pcm"), trace = Path.Combine(root.Path, "source.trace");
    File.WriteAllBytes(pcm, new byte[] { 1, 2, 3, 4 }); File.WriteAllBytes(trace, new byte[] { 5, 6, 7 });
    var receipt = Receipt(identity, pcm, trace, 12);
    var cache = new StrictPreflightCache(root.Path, identity);
    cache.Publish(receipt, pcm, trace);
    Check(cache.TryRead(identity, out var loaded, out var files), "complete cache was not readable");
    Equal(receipt, loaded);
    Check(File.ReadAllBytes(files.PcmPath).SequenceEqual(File.ReadAllBytes(pcm)), "cached PCM changed");
    Check(File.ReadAllBytes(files.TranscriptPath).SequenceEqual(File.ReadAllBytes(trace)), "cached transcript changed");
}

void TestIdentityMismatch()
{
    using var root = TempDirectory();
    string identity = Hash("identity"), pcm = Path.Combine(root.Path, "source.pcm"), trace = Path.Combine(root.Path, "source.trace");
    File.WriteAllBytes(pcm, new byte[] { 1 }); File.WriteAllBytes(trace, new byte[] { 2 });
    var cache = new StrictPreflightCache(root.Path, identity);
    cache.Publish(Receipt(identity, pcm, trace, 1), pcm, trace);
    Check(!cache.TryRead(Hash("different"), out _, out _), "different identity reused a cache");
}

void TestCorruptCache()
{
    using var root = TempDirectory();
    string identity = Hash("identity"), pcm = Path.Combine(root.Path, "source.pcm"), trace = Path.Combine(root.Path, "source.trace");
    File.WriteAllBytes(pcm, new byte[] { 1, 2 }); File.WriteAllBytes(trace, new byte[] { 3, 4 });
    var cache = new StrictPreflightCache(root.Path, identity);
    cache.Publish(Receipt(identity, pcm, trace, 1), pcm, trace);
    File.WriteAllBytes(cache.PublishedFiles.PcmPath, new byte[] { 9 });
    Check(!cache.TryRead(identity, out _, out _), "corrupt PCM was accepted");
    Check(File.Exists(cache.PublishedFiles.ManifestPath), "corrupt cache evidence was deleted");
}

void TestNoOverwrite()
{
    using var root = TempDirectory();
    string identity = Hash("identity"), pcm1 = Path.Combine(root.Path, "one.pcm"), trace1 = Path.Combine(root.Path, "one.trace"), pcm2 = Path.Combine(root.Path, "two.pcm"), trace2 = Path.Combine(root.Path, "two.trace");
    File.WriteAllBytes(pcm1, new byte[] { 1 }); File.WriteAllBytes(trace1, new byte[] { 2 });
    File.WriteAllBytes(pcm2, new byte[] { 9 }); File.WriteAllBytes(trace2, new byte[] { 8 });
    var cache = new StrictPreflightCache(root.Path, identity);
    cache.Publish(Receipt(identity, pcm1, trace1, 1), pcm1, trace1);
    Throws("preflight_cache_mismatch", () => cache.Publish(Receipt(identity, pcm2, trace2, 1), pcm2, trace2));
    Check(cache.TryRead(identity, out var loaded, out var files), "winner cache disappeared");
    Equal(HashFile(pcm1), loaded.PcmSha256); Equal(1, File.ReadAllBytes(files.PcmPath)[0]);
}

void TestOpenSealed()
{
    using var root = TempDirectory();
    string pcm = Path.Combine(root.Path, "cached.pcm"), trace = Path.Combine(root.Path, "cached.trace");
    var floats = new float[1600 * 2];
    using (var output = File.Create(pcm)) output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats.AsSpan()));
    string traceHash, pcmHash = HashFile(pcm);
    var record = new byte[20];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4, 4), 0);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8, 4), -1);
    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(12, 8), 1600);
    File.WriteAllBytes(trace, record);
    traceHash = Convert.ToHexString(SHA256.HashData(record.AsSpan(0, 12))).ToLowerInvariant();
    File.Copy(trace, trace + ".copy");
    using (var prepared = PreparedAudioTrack.OpenSealed(pcm, 30, 48000, 2, 1, HashFile(pcm)))
    {
        var frame = prepared.ReadFrame(0);
        Equal(floats.Length, frame.Samples.Length);
        prepared.CompleteReplay();
    }
    using (var replay = StrictReplayLedger.OpenSealed(trace + ".copy", 2, 1, 1600, traceHash, pcmHash))
    {
        replay.ValidateState(1, false, -1);
        replay.AppendReplayPcm(floats, 0, floats.Length);
        replay.CompleteReplay();
    }
    Check(File.Exists(pcm) && File.Exists(trace + ".copy"), "cache-owned artifacts were deleted by readers");
}

static StrictPreflightReceipt Receipt(string identity, string pcm, string trace, long frames) => StrictPreflightReceipt.Create(
    identity, Hash("story"), Hash("host"), Hash("mods"), Hash("settings"), frames, 30, 48000, 2,
    Hash("frame-trace"), HashFile(pcm), HashFile(trace), frames * 1600);

void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string HashFile(string path) { using var stream = File.OpenRead(path); using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant(); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Throws(string code, Action action) { try { action(); } catch (ExportException error) { Equal(code, error.Code); return; } throw new Exception("Expected " + code + "."); }
static TempDirectoryHandle TempDirectory() { string path = Path.Combine(Path.GetTempPath(), "aave-preflight-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new TempDirectoryHandle(path); }
sealed class TempDirectoryHandle : IDisposable { public TempDirectoryHandle(string path) => Path = path; public string Path { get; } public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
