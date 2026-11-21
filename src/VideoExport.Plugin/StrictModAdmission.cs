using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

/// <summary>
/// Admission boundary for strict exports. The exporter can operate without
/// optional Mods, but it must not claim a strict timeline when a loaded Mod's
/// clock adapter is only partial or unsupported.
/// </summary>
internal static class StrictModAdmission
{
    internal static void EnsureCompatible(
        bool isLoaded,
        bool fullClockCoverage,
        string status,
        string failureReason)
    {
        if (!isLoaded) return;

        // Status is the human-readable compatibility contract and remains the
        // authoritative fail-closed signal. Do not let an inconsistent stale
        // FullClockCoverage flag override an explicit partial/unsupported
        // result; only a loaded build reporting full coverage may proceed.
        if (fullClockCoverage && string.Equals(status?.Trim(), "full", StringComparison.OrdinalIgnoreCase)) return;

        string state = string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
        string reason = string.IsNullOrWhiteSpace(failureReason) ? "没有可用的完整时钟适配。" : failureReason.Trim();
        throw new ExportException(
            "strict_mod_clock_unsupported",
            $"严格逐帧导出无法保证已加载的“更多的画面效果”计时逐帧对齐（状态：{state}）。{reason}");
    }
}
