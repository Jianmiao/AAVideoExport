using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devMode, int flags, uint access, IntPtr sa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, string commandLine, IntPtr procSa, IntPtr threadSa,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: launcher <exe> <cwd> [args...]"); return 2; }
        string desktopName = "AAVE03Test-" + Environment.ProcessId;
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktopName };
        string commandLine = "\"" + args[0] + "\"";
        for (int i = 2; i < args.Length; i++) commandLine += " \"" + args[i].Replace("\"", "\\\"") + "\"";
        if (!CreateProcessW(args[0], commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, args[1], ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        CloseHandle(pi.hThread);
        Console.WriteLine("pid=" + pi.dwProcessId + " desktop=" + desktopName);
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        GetExitCodeProcess(pi.hProcess, out uint code);
        CloseHandle(pi.hProcess);
        CloseHandle(desktop);
        return (int)code;
    }
}
