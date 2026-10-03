using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestExportErrorText()
    {
        await Test("output collision gives actionable Chinese guidance while retaining the original diagnostic", () =>
        {
            var error = new ExportException("output_exists", "The video or cover filename already exists. Choose a different title.");
            string text = ExportErrorText.Describe(error);
            if (!text.Contains("同名的视频或封面") || !text.Contains("修改文件名称") || !text.Contains("不会被覆盖")
                || text.Contains("already exists") || text.Contains("output_exists"))
                throw new Exception("Output collision did not provide Chinese recovery guidance.");
            Equal("output_exists", error.Code, "machine-readable code is retained");
            Check(error.Message.Contains("already exists"), "original diagnostic remains available for logs");
            return Task.CompletedTask;
        });
        await Test("common export errors and invalid bitrate use Chinese messages without raw backend diagnostics", () =>
        {
            foreach (string code in new[] { "disk_full", "io_error", "cancelled", "hardware_encoder_required", "hardware_encoder_unavailable",
                "encoder_unavailable", "encoder_failed", "encode_failed", "process_start_failed", "process_timeout", "upscaler_unavailable",
                "invalid_frame", "invalid_audio", "audio_timeline_mismatch", "verification_failed", "invalid_state", "unknown" })
            {
                string text = ExportErrorText.Describe(new ExportException(code, "RAW_BACKEND_DETAIL"));
                if (text.Contains("RAW_BACKEND_DETAIL") || !text.Any(c => c is >= '\u4e00' and <= '\u9fff'))
                    throw new Exception($"Backend text leaked for {code}.");
            }
            Check(ExportErrorText.Describe(new ExportException("invalid_settings", "Bitrate must be 100–500000 kbps.")).Contains("0.1–500 Mbps"),
                "validation uses the displayed video bitrate unit");
            return Task.CompletedTask;
        });
        await Test("asynchronous, native and permission errors retain useful Chinese recovery instructions", () =>
        {
            var error = new ExportException("story_not_selected", "请先在鉴赏列表选择要导出的剧情。");
            Equal(error.Message, ExportErrorText.Describe(new AggregateException(error)), "native Chinese error preserved across async boundary");
            var permission = new ExportException("io_error", "Access denied", new UnauthorizedAccessException());
            Check(ExportErrorText.Describe(permission).Contains("写入权限"), "permission guidance");
            Check(ExportErrorText.Describe(new OperationCanceledException()).Contains("已取消"), "cancellation guidance");
            return Task.CompletedTask;
        });
    }
}
