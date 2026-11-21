using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AAVideoExport.Core;

var passed = 0;
var failed = 0;
Run("delayed native PCM preserves lead/content/tail and exact content", TestDelayedNativePass);
Run("raw gaps, overlaps and invalid values leave receipt unchanged", TestAtomicValidation);
Run("cancellation disposes the private spool", TestCancellationCleanup);
Run("state and delayed audio remain strict when PCM is wrong or missing", TestDelayedLedger);
Run("native DSP observations validate every raw block including empty blocks", TestNativeClock);
Run("invalid native DSP observations never advance the raw receipt", TestNativeClockInvalid);
Run("native DSP precision cannot collapse distinct sample positions", TestNativeClockPrecision);
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void TestDelayedNativePass()
{
    const int channels = 2;
    var raw = new float[10 * channels];
    // Native capture includes three lead frames, five content frames and two tail frames.
    raw[3 * channels] = .25f;
    raw[(3 + 4) * channels + 1] = -.75f;
    string path = Temp("raw-pass");
    string nativeCapturePath = Temp("native-capture");
    File.WriteAllBytes(nativeCapturePath, MemoryMarshal.AsBytes(raw.AsSpan()).ToArray());
    RawAudioPassReceipt receipt;
    var content = new List<float>();
    try
    {
        using var nativeCapture = File.OpenRead(nativeCapturePath);
        using var pass = new RawAudioPass(path, 10, channels, 42.5, 3);
        var first = new byte[8 * sizeof(float)];
        ReadFull(nativeCapture, first);
        pass.Append(0, MemoryMarshal.Cast<byte, float>(first).ToArray(), 8);
        var second = new byte[raw.Length * sizeof(float) - first.Length];
        ReadFull(nativeCapture, second);
        pass.Append(4, MemoryMarshal.Cast<byte, float>(second).ToArray(), second.Length / sizeof(float));
        Check(pass.CanSeal(5, 10), "raw pass can seal after native tail drain");
        receipt = pass.Seal(5, 10);
        Equal(10L, receipt.RawSampleFrames);
        Equal(3L, receipt.LeadSampleFrames);
        Equal(5L, receipt.ContentSampleFrames);
        Equal(2L, receipt.TailSampleFrames);
        pass.CopyContentTo(slice => content.AddRange(slice.ToArray()));
        pass.CopyContentTo(slice => { }); // replay is repeatable for validation/export.
        var expected = raw.AsSpan(3 * channels, 5 * channels).ToArray();
        Check(content.SequenceEqual(expected), "content samples are exact and unmodified");
        Equal(Hash(expected), Hash(content.ToArray()));
        Check(content.Any(value => value != 0), "content keeps native pulses");
        Check(Math.Abs(receipt.RawDspEpochSeconds - 42.5) < double.Epsilon, "DSP epoch is retained");
    }
    finally { File.Delete(nativeCapturePath); }
    Check(!File.Exists(path), "private spool is removed on dispose");
}

void TestAtomicValidation()
{
    string path = Temp("raw-atomic");
    using var pass = new RawAudioPass(path, 10, 2, 1, 0);
    var block = new[] { .1f, .2f, .3f, .4f };
    pass.Append(0, block, block.Length);
    Equal(2L, pass.RawSampleFrames);
    Equal(1L, pass.AppendedBlocks);
    Throws("audio_raw_gap", () => pass.Append(3, block, block.Length));
    Equal(2L, pass.RawSampleFrames);
    Equal(1L, pass.AppendedBlocks);
    Throws("audio_raw_overlap", () => pass.Append(1, block, block.Length));
    Equal(2L, pass.RawSampleFrames);
    Throws("invalid_audio", () => pass.Append(2, new[] { 1f, float.NaN }, 2));
    Equal(2L, pass.RawSampleFrames);
    Equal(1L, pass.AppendedBlocks);
}

void TestCancellationCleanup()
{
    string path = Temp("raw-cancel");
    using (var pass = new RawAudioPass(path, 10, 2, 1, 0)) pass.Append(0, new[] { 1f, 0f }, 2);
    Check(!File.Exists(path), "cancelled pass removes its own spool");
}

void TestDelayedLedger()
{
    string path = Temp("ledger-delay");
    using (var ledger = new StrictReplayLedger(path, 2))
    {
        ledger.PrepareState(7, false, -1);
        ledger.AppendPreparationPcm(new[] { .25f, .5f }, 0, 2);
        ledger.PrepareState(8, true, 1);
        ledger.AppendPreparationPcm(new[] { .75f, 1f }, 0, 2);
        ledger.BeginReplay();
        ledger.ValidateState(7, false, -1);
        ledger.AppendReplayPcm(new[] { .25f, .5f }, 0, 2);
        ledger.ValidateState(8, true, 1);
        ledger.AppendReplayPcm(new[] { .75f, 1.001f }, 0, 2);
        Throws("audio_replay_mismatch", ledger.CompleteReplay);
    }
    string missingPath = Temp("ledger-missing");
    using (var ledger = new StrictReplayLedger(missingPath, 2))
    {
        ledger.PrepareState(1, false, -1);
        ledger.AppendPreparationPcm(new[] { 1f, 0f }, 0, 2);
        ledger.BeginReplay();
        ledger.ValidateState(1, false, -1);
        Throws("audio_replay_mismatch", ledger.CompleteReplay);
    }
}

void TestNativeClock()
{
    const double epoch = 13.269333333333334;
    const int rate = 48000;
    var clock = new NativeAudioSampleClock(epoch, rate);
    Equal(0L, clock.ObserveBlock(epoch, epoch, 0));
    Equal(0L, clock.ObserveBlock(epoch, epoch + 1024d / rate, 1024));
    Equal(1024L, clock.ObserveBlock(epoch + 1024d / rate, epoch + 3072d / rate, 2048));
    Equal(3072L, clock.ObserveBlock(epoch + 3072d / rate, epoch + 3072d / rate, 0));
    Equal(3072L, clock.NextRawSample);
}

void TestNativeClockInvalid()
{
    var clock = new NativeAudioSampleClock(10, 4);
    Equal(0L, clock.ObserveBlock(10, 10.5, 2));
    Throws("audio_raw_gap", () => clock.ObserveBlock(10.75, 11, 1));
    Throws("audio_raw_overlap", () => clock.ObserveBlock(10.25, 10.5, 1));
    Throws("audio_raw_interval", () => clock.ObserveBlock(10.5, 11, 1));
    Throws("audio_raw_interval", () => clock.ObserveBlock(10.5, 10.75, 0));
    Throws("invalid_audio_clock", () => clock.ObserveBlock(double.NaN, 11, 2));
    Throws("invalid_audio_clock", () => clock.ObserveBlock(10.5, double.PositiveInfinity, 2));
    Throws("invalid_audio_clock", () => clock.ObserveBlock(10.5, 10.25, 1));
    Throws("invalid_audio_clock", () => clock.ObserveBlock(9.75, 10, 1));
    Throws("invalid_audio_clock", () => clock.ObserveBlock(10.5, 10.75, -1));
    Throws("audio_clock_precision", () => clock.ObserveBlock(10.5625, 10.8125, 1));
    Throws("audio_clock_precision", () => clock.ObserveBlock(10.5, 10.8125, 1));
    Equal(2L, clock.NextRawSample);
    Equal(2L, clock.ObserveBlock(10.5, 10.75, 1));
    Equal(3L, clock.NextRawSample);
}

void TestNativeClockPrecision()
{
    Throws("audio_clock_precision", () => { _ = new NativeAudioSampleClock(1e15, 48000); });
    var clock = new NativeAudioSampleClock(0, 48000);
    Throws("audio_clock_precision", () => clock.ObserveBlock(1e15, 1e15, 0));
    Throws("audio_clock_precision", () => clock.ObserveBlock(0, double.MaxValue, 1));
    Equal(0L, clock.NextRawSample);
}

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}

static string Temp(string name) => Path.Combine(Path.GetTempPath(), "aave-" + name + "-" + Guid.NewGuid().ToString("N"));
static void ReadFull(Stream stream, byte[] bytes)
{
    var offset = 0;
    while (offset < bytes.Length)
    {
        var read = stream.Read(bytes, offset, bytes.Length - offset);
        if (read == 0) throw new EndOfStreamException();
        offset += read;
    }
}
static string Hash(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan()))).ToLowerInvariant();
static void Throws(string code, Action action)
{
    try { action(); }
    catch (ExportException error) { Equal(code, error.Code); return; }
    throw new Exception("Expected " + code + ".");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
