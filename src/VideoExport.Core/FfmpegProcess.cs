using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace AAVideoExport.Core;

internal sealed class FfmpegProcess : IDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly Task<string> stdout;
    internal Stream Input => process.StandardInput.BaseStream;
    internal bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

    internal FfmpegProcess(string executable, IEnumerable<string> arguments, bool input = false, string? workingDirectory = null,
        string? gpuEncoder = null, Action<string>? stdoutLineSink = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = input
        };
        if (workingDirectory != null) start.WorkingDirectory = workingDirectory;
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        VulkanDevicePolicy.ConfigureEnvironment(start, gpuEncoder);
        process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ExportException("process_start_failed", "Could not start " + Path.GetFileName(executable) + ".");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { process.Dispose(); throw new ExportException("process_start_failed", "Could not start " + Path.GetFileName(executable) + ": " + ex.Message, ex); }
        stderr = ReadBoundedAsync(process.StandardError, 32768);
        stdout = stdoutLineSink is null
            ? ReadBoundedAsync(process.StandardOutput, 262144)
            : ReadLinesAsync(process.StandardOutput, stdoutLineSink);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                result.Append(buffer, 0, count);
                if (result.Length > limit) result.Remove(0, result.Length - limit);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        return result.ToString();
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> lineSink)
    {
        Exception? sinkFailure = null;
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // Always drain stdout after a consumer failure. Otherwise the
            // child can block on a full pipe and hide the validation error
            // behind the process timeout.
            if (sinkFailure is not null) continue;
            try { lineSink(line); }
            catch (Exception ex) { sinkFailure = ex; }
        }
        if (sinkFailure is not null) ExceptionDispatchInfo.Capture(sinkFailure).Throw();
        return string.Empty;
    }

    internal async Task<ProcessResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var registration = deadline.Token.Register(Kill);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // Process exit closes redirected handles; still bound drainage in case an inherited handle survived.
            string[] captured = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, captured[0], captured[1]);
        }
        catch (OperationCanceledException ex)
        {
            Kill();
            throw new ExportException(cancellationToken.IsCancellationRequested ? "cancelled" : "process_timeout",
                cancellationToken.IsCancellationRequested ? "Export cancelled." : "The media process exceeded its time limit.", ex);
        }
        catch (TimeoutException ex) { Kill(); throw new ExportException("process_timeout", "Timed out while reading media process output.", ex); }
    }

    internal void CloseInput()
    {
        try { if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    internal void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        CloseInput();
    }

    public void Dispose()
    {
        Kill();
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
        string? workingDirectory = null, string? gpuEncoder = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new FfmpegProcess(executable, arguments, workingDirectory: workingDirectory, gpuEncoder: gpuEncoder);
        return await process.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a media tool while consuming stdout one line at a time.  This is
    /// intentionally separate from <see cref="RunAsync"/>: bounded text is
    /// useful for diagnostics, but it cannot represent an arbitrarily long
    /// ffprobe packet/frame listing without silently dropping its beginning.
    /// </summary>
    internal static async Task<ProcessResult> RunLinesAsync(string executable, IEnumerable<string> arguments,
        Action<string> lineSink, TimeSpan timeout, CancellationToken cancellationToken,
        string? workingDirectory = null, string? gpuEncoder = null)
    {
        ArgumentNullException.ThrowIfNull(lineSink);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new FfmpegProcess(executable, arguments, workingDirectory: workingDirectory,
            gpuEncoder: gpuEncoder, stdoutLineSink: lineSink);
        return await process.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    internal void EnsureSuccess(string stage)
    {
        if (ExitCode == 0) return;
        string code = Error.Contains("No space left", StringComparison.OrdinalIgnoreCase) || Error.Contains("disk full", StringComparison.OrdinalIgnoreCase)
            ? "disk_full" : "encoder_failed";
        throw new ExportException(code, stage + " failed (exit " + ExitCode + "). " + Error);
    }
}
