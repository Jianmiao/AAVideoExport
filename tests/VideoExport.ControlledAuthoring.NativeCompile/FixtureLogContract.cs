namespace AAVideoExport.NativePath.Tests;

// Compile-only log contract. This library has no entry point and is never
// staged. The real NativePath plugin supplies Plugin.Report inside AA.
internal static class Plugin
{
    internal static string DiagnosticsRoot => throw new NotSupportedException("Compile-only contract.");
    internal static void Report(string message) => throw new NotSupportedException("Compile-only contract; run the actual native acceptance plugin.");
}
