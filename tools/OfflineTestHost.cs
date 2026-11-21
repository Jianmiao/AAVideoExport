// .NET Framework bridge for offline builds without an SDK-generated apphost.
// Test fixtures spawn this executable; it forwards literal arguments and bytes
// to the .NET 6 assertion harness. It is never included in the mod package.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class OfflineTestHost
{
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static int Main(string[] args)
    {
        string assembly = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoExport.Core.Tests.dll");
        var start = new ProcessStartInfo("dotnet", string.Join(" ", new[] { assembly }.Concat(args).Select(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var child = Process.Start(start))
        {
            Task.Run(async () =>
            {
                try { await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream); }
                catch (IOException) { }
                finally { child.StandardInput.Close(); }
            });
            var output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            var error = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            child.WaitForExit();
            Task.WaitAll(output, error);
            return child.ExitCode;
        }
    }
}
