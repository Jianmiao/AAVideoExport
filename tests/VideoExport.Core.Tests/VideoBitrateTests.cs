using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestVideoBitrate()
    {
        await Test("Mbps input preserves existing Kbps encoding values and round-trips", () =>
        {
            foreach (int rate in new[] { 100, 101, 4000, 6000, 6500, 8123, 19000, 50000, 500000 })
            {
                if (!VideoBitrate.TryParseMbps(VideoBitrate.FormatMbps(rate), out int parsed) || parsed != rate)
                    throw new Exception($"Mbps round-trip changed {rate} Kbps to {parsed}.");
            }
            if (VideoBitrate.FormatMbps(VideoBitrate.RecommendedKbps) != "4.0"
                || new ExportOptions().BitrateKbps != 4000) throw new Exception("Default unit conversion failed.");
            foreach (string encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                var arguments = EncoderArguments.Video(new ExportOptions(), encoder, false).ToList();
                if (arguments[arguments.IndexOf("-b:v") + 1] != "4000k")
                    throw new Exception($"Default Mbps conversion changed the bitrate sent to {encoder}.");
            }
            return Task.CompletedTask;
        });
        await Test("Mbps input rejects empty, ambiguous, non-finite and out-of-range values", () =>
        {
            foreach (string value in new[] { "", ".", "NaN", "Infinity", "6,5", "6e3", "-6", "0", "0.099", "500.001", "6.0001", "6000" })
                if (VideoBitrate.TryParseMbps(value, out _)) throw new Exception($"Accepted invalid Mbps: {value}");
            return Task.CompletedTask;
        });
        await Test("bitrate slider covers all 0.1 Mbps steps without clamping manual encoding values", () =>
        {
            for (int rate = 100; rate <= 50000; rate += 100)
                if (VideoBitrate.FromSlider(VideoBitrate.SliderPosition(rate)) != rate)
                    throw new Exception($"Slider round-trip changed {rate} Kbps.");
            if (VideoBitrate.SliderPosition(500000) != 1
                || !VideoBitrate.TryParseMbps("500", out int manual) || manual != 500000)
                throw new Exception("High manual bitrate was truncated to the slider maximum.");
            return Task.CompletedTask;
        });
    }
}
