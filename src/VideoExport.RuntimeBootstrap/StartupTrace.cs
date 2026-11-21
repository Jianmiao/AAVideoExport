using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace AAVideoExport.RuntimeBootstrap;

// Explicit diagnostic transport only. Keep this independent of BepInEx logging,
// JSON serialization and Unity so an early native crash leaves a durable boundary.
internal static class StartupTrace
{
    private static readonly object Gate = new();
    private static long sequence;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Write(string stage, string detail = "")
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_STARTUP_TRACE_FILE");
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return;
            lock (Gate)
            {
                var line = "seq=" + (++sequence).ToString(CultureInfo.InvariantCulture)
                    + "|utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    + "|pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                    + "|tid=" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)
                    + "|stage=" + Clean(stage) + "|detail=" + Clean(detail) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch { /* Optional diagnostics must never become a startup dependency. */ }
    }

    internal static void WriteTarget(string stage, MethodBase target)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_STARTUP_TRACE_FILE");
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return;
            Write(stage, (target.DeclaringType?.FullName ?? "<no-type>") + "::" + target.Name
                + " token=0x" + target.MetadataToken.ToString("X8", CultureInfo.InvariantCulture)
                + " signature=" + target);
        }
        catch { /* Metadata formatting is diagnostic only. */ }
    }

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
}
