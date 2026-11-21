using AAVideoExport.Core;
using Window = AAVideoExport.Core.TimestampedAudioWindow;

namespace AAVideoExport.TimestampedAudioWindow.Tests;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static int Main(string[] args)
    {
        Test("fragmented raw blocks emit only the bounded content window", FragmentedWindow);
        Test("raw gaps and overlaps fail atomically", RejectsNonContiguousBlocks);
        Test("missing samples, invalid channels, and missing bounds fail on seal", RejectsIncompleteWindow);
        Test("frame scheduler uses rational boundaries and rejects backwards frames", Scheduler);
        Test("overflow and non-finite rejected appends leave all receipts unchanged", AtomicValidation);
        Test("explicit non-frame-aligned interval is preserved exactly", ExplicitInterval);
        Test("DSP precision failures reject schedules without moving the event cursor", DspPrecision);
        if (args.Length != 0)
            Test("native scheduled PCM retains lead, content, and trailing receipts", () => NativeEvidence(args[0]));
        Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void FragmentedWindow()
    {
        var window = new Window(rawOriginSample: 0, leadSampleFrames: 4, contentSampleFrames: 6, channels: 2);
        var source = Enumerable.Range(0, 28).Select(i => i / 10f).ToArray();
        var receipts = new List<TimestampedAudioAppendReceipt>();
        receipts.Add(window.Append(source, 0, 3 * 2, 0));
        receipts.Add(window.Append(source, 6, 4 * 2, 3));
        receipts.Add(window.Append(source, 14, 5 * 2, 7));
        receipts.Add(window.Append(source, 24, 2 * 2, 12));
        window.Complete();

        Equal(4L, window.RawLeadSampleFramesAcknowledged, "lead receipt");
        Equal(6L, window.ContentSampleFrames, "target content count");
        Equal(4L, window.RawTrailingSampleFrames, "raw trailing receipt");
        Equal(14L, window.RawSamplesAcknowledged, "raw receipt includes all blocks");
        Equal(6L, window.ContentSampleFramesEmitted, "content receipt");
        Check(window.IsComplete, "window is complete after contiguous tail");

        var slices = receipts.SelectMany(r => r.ContentSlices).ToArray();
        Equal(2, slices.Length, "content crossing blocks is emitted as borrowed slices");
        Equal(4L, slices[0].ContentStartSample, "first content slice start");
        Equal(3L, slices[0].SampleFrames, "first content slice count");
        Equal(7L, slices[1].ContentStartSample, "second content slice start");
        Equal(3L, slices[1].SampleFrames, "second content slice count");
        var values = slices.SelectMany(s => s.Samples.ToArray()).ToArray();
        var expected = source.Skip(4 * 2).Take(6 * 2).ToArray();
        Check(values.SequenceEqual(expected), "content samples preserve source order and values");
        Check(ReferenceEquals(source, slices[0].Samples.Array), "slice borrows the caller buffer synchronously");
    }

    private static void RejectsNonContiguousBlocks()
    {
        var gap = new Window(0, 2, 4, 2);
        gap.Append(new float[4], 0, 4, 0);
        ThrowsCode("audio_raw_gap", () => gap.Append(new float[4], 0, 4, 3), "raw gap");
        Equal(2L, gap.RawSamplesAcknowledged, "gap leaves the prior receipt unchanged");
        gap.Append(new float[8], 0, 8, 2);
        ThrowsCode("audio_raw_overlap", () => gap.Append(new float[2], 0, 2, 5), "raw overlap after content");
        gap.Append(new float[2], 0, 2, 6);
        gap.Complete();

        var duplicate = new Window(10, 0, 2, 2);
        duplicate.Append(new float[4], 0, 4, 10);
        ThrowsCode("audio_raw_overlap", () => duplicate.Append(new float[4], 0, 4, 10), "exact duplicate interval");
    }

    private static void RejectsIncompleteWindow()
    {
        ThrowsArgument(() => new Window(-1, 0, 1, 2), "negative origin");
        ThrowsArgument(() => new Window(0, -1, 1, 2), "negative lead");
        ThrowsArgument(() => new Window(0, 0, -1, 2), "negative target");
        ThrowsArgument(() => new Window(0, 0, 1, 3), "unsupported channels");

        var incomplete = new Window(0, 2, 4, 2);
        incomplete.Append(new float[10], 0, 10, 0);
        ThrowsCode("audio_timeline_incomplete", incomplete.Complete, "one missing content sample frame");
        Equal(5L, incomplete.RawSamplesAcknowledged, "incomplete receipt remains inspectable");

        var noBounds = new Window(0, 0, 0, 2);
        ThrowsCode("audio_raw_bounds_missing", noBounds.Complete, "no acknowledged raw bounds");

        var invalid = new Window(0, 0, 1, 2);
        ThrowsCode("invalid_audio", () => invalid.Append(new[] { 0f, float.NaN }, 0, 2, 0), "non-finite block");
        ThrowsCode("invalid_audio", () => invalid.Append(new float[3], 0, 3, 0), "incomplete interleaved channels");
        Equal(0L, invalid.RawSamplesAcknowledged, "invalid blocks do not advance raw state");
    }

    private static void Scheduler()
    {
        var scheduler = new AudioFrameScheduler(rawOriginSample: 100, leadSampleFrames: 4096, fps: 24, sampleRate: 44100, dspEpochSeconds: 10.0);
        var first = scheduler.Schedule(0);
        Equal(4196L, first.RawSampleStart, "frame zero includes raw origin and lead");
        Equal(1837L, first.SampleFrames, "rational first frame budget");
        Nearly(10.0 + 4096d / 44100d, first.DspStartSeconds, "DSP epoch mapping");
        var second = scheduler.Schedule(1);
        Equal(6033L, second.RawSampleStart, "frame one boundary");
        Equal(1838L, second.SampleFrames, "rational second frame budget");
        Equal(second, scheduler.Schedule(1), "simultaneous events reuse their frame time");
        Equal(9708L, scheduler.Schedule(3).RawSampleStart, "events may skip frames with no audio command");
        ThrowsCode("audio_frame_order_mismatch", () => scheduler.Schedule(2), "backwards event frame");
        ThrowsArgument(() => new AudioFrameScheduler(0, 0, 30, 48000, double.NaN), "non-finite DSP epoch");
        ThrowsArgument(() => new AudioFrameScheduler(0, 0, 30, 48000, -1), "negative DSP epoch");

        var overflow = new AudioFrameScheduler(long.MaxValue, 1, 30, 48000, 1);
        ThrowsCode("audio_clock_overflow", () => overflow.Schedule(0), "raw sample overflow");
        Equal(-1L, overflow.LastFrame, "overflow did not consume a schedule");
        ThrowsCode("audio_clock_overflow", () => scheduler.Schedule(long.MaxValue), "frame multiplication overflow");
        Equal(3L, scheduler.LastFrame, "failed far-future event leaves last frame unchanged");
    }

    private static void AtomicValidation()
    {
        Throws<OverflowException>(() => new Window(long.MaxValue, 1, 0, 2), "origin plus lead overflow");
        Throws<OverflowException>(() => new Window(0, long.MaxValue, 1, 2), "end boundary overflow");
        var distant = new Window(0, long.MaxValue - 1, 1, 2);
        distant.Append(new float[2], 0, 2, 0);
        Equal(1L, distant.RawSamplesAcknowledged, "distant content does not overflow empty intersection arithmetic");

        var window = new Window(0, 1, 2, 2);
        window.Append(new float[2], 0, 2, 0);
        ThrowsCode("invalid_audio", () => window.Append(new[] { 1f, 2f, 3f, float.PositiveInfinity }, 0, 4, 1), "invalid end of input");
        Equal(1L, window.NextRawSample, "invalid value leaves raw cursor");
        Equal(0L, window.ContentSampleFramesEmitted, "invalid value emits no partial content");
        window.Append(new float[4], 0, 4, 1);
        ThrowsCode("audio_raw_gap", () => window.Append(new float[2], 0, 2, 4), "tail gap");
        ThrowsCode("audio_raw_overlap", () => window.Append(new float[2], 0, 2, 2), "tail overlap");
        window.Append(new float[2], 0, 2, 3);
        window.Complete();
        ThrowsCode("invalid_state", () => window.Append(new float[2], 0, 2, 4), "append after completed receipt");

        var rawOverflow = new Window(long.MaxValue - 1, 0, 0, 2);
        ThrowsCode("audio_raw_overflow", () => rawOverflow.Append(new float[4], 0, 4, long.MaxValue - 1), "append raw end overflow");
        Check(!rawOverflow.HasAcknowledgedRawBounds, "overflow does not acknowledge raw bounds");
    }

    private static void ExplicitInterval()
    {
        long count = ExportTimeline.AudioSampleFramesForVideoFrames(7, 24, 44100);
        Equal(12862L, count, "fractional video endpoint rounds down once");
        var window = new Window(93, 4096, count, 2);
        var raw = Enumerable.Range(0, checked((int)((4096 + count + 13) * 2))).Select(i => (float)i).ToArray();
        var receipt = window.Append(raw, 0, raw.Length, 93);
        window.Complete();
        Equal(count * 2, receipt.ContentSlice!.Samples.Count, "unaligned end emits exact explicit count");
        Equal(13L, receipt.RawTrailingSampleFrames, "overshoot is an explicit raw receipt");
        Equal(raw[(4096 + count - 1) * 2 + 1], receipt.ContentSlice.Samples.Last(), "last real channel sample is preserved");
    }

    private static void DspPrecision()
    {
        var hugeEpoch = new AudioFrameScheduler(0, 4096, 30, 48000, 1e15);
        ThrowsCode("audio_clock_precision", () => hugeEpoch.Schedule(0), "finite epoch cannot resolve sample intervals");
        Equal(-1L, hugeEpoch.LastFrame, "DSP precision failure leaves schedule state unchanged");
        var hugeLead = new AudioFrameScheduler(0, 9007199254740992L, 30, 48000, 0);
        ThrowsCode("audio_clock_precision", () => hugeLead.Schedule(0), "sample index exceeds exact double integer range");
        Equal(-1L, hugeLead.LastFrame, "integer precision failure leaves schedule state unchanged");
        var normal = new AudioFrameScheduler(0, 4096, 30, 48000, 123.456);
        var first = normal.Schedule(0);
        Equal(first, normal.Schedule(0), "multiple simultaneous events have an identical DSP target");
        Check(normal.Schedule(1).DspStartSeconds > first.DspStartSeconds, "a later sample target advances DSP time");
    }

    private static void NativeEvidence(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Check(bytes.Length % (sizeof(float) * 2) == 0, "native evidence interleaving");
        var raw = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
        long frames = raw.Length / 2;
        Equal(23552L, frames, "native raw frame count");
        var window = new Window(0, 4096, 19200, 2);
        var output = new List<TimestampedAudioContentSlice>();
        foreach (int block in new[] { 4097, 1600, 8192, 4096, 5567 })
        {
            int offset = checked((int)(window.RawSamplesAcknowledged * 2));
            var receipt = window.Append(raw, offset, checked(block * 2), window.RawSamplesAcknowledged);
            output.AddRange(receipt.ContentSlices);
        }
        window.Complete();
        Equal(256L, window.RawTrailingSampleFrames, "native trailing receipt");
        Equal(4096L, window.RawLeadSampleFramesAcknowledged, "native lead receipt");
        Equal(23552L, window.RawSamplesAcknowledged, "native entire raw capture acknowledged");
        var content = output.SelectMany(slice => slice.Samples.ToArray()).ToArray();
        Check(content.SequenceEqual(raw.Skip(4096 * 2).Take(19200 * 2)), "native content interval exact samples");
        int[] markers = { 0, 1599, 1600, 3199, 3200, 4799 };
        float[] amplitudes = { .25f, -.35f, .45f, -.55f, .65f, -.75f };
        for (int channel = 0; channel < 2; channel++)
        {
            int channelDelay = channel * 1600;
            var nonzero = Enumerable.Range(0, 19200).Where(i => Math.Abs(content[i * 2 + channel]) > .00001f).ToArray();
            Check(nonzero.SequenceEqual(markers.Select(i => i + channelDelay)), "all six native markers preserve authored positions for channel " + channel);
            for (int marker = 0; marker < markers.Length; marker++)
                Equal(amplitudes[marker], content[(markers[marker] + channelDelay) * 2 + channel], "native marker amplitude");
        }
        var unchangedBytes = new byte[bytes.Length];
        Buffer.BlockCopy(raw, 0, unchangedBytes, 0, unchangedBytes.Length);
        Check(bytes.SequenceEqual(unchangedBytes), "raw evidence bits remain unchanged");
    }

    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    private static void Throws<T>(Action action, string context) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(context + " did not throw " + typeof(T).Name);
    }

    private static void ThrowsCode(string code, Action action, string context)
    {
        try { action(); }
        catch (ExportException error) { Equal(code, error.Code, context + " code"); return; }
        throw new InvalidOperationException(context + " did not throw " + code);
    }

    private static void ThrowsArgument(Action action, string context)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException(context + " did not throw ArgumentOutOfRangeException");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{context}: expected {expected}; actual {actual}");
    }

    private static void Nearly(double expected, double actual, string context)
    {
        if (Math.Abs(expected - actual) > 1e-12) throw new InvalidOperationException($"{context}: expected {expected:R}; actual {actual:R}");
    }

    private static void Check(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException(context);
    }
}
