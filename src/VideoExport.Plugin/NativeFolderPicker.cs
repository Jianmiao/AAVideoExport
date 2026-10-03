using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AAVideoExport.Plugin;

/// <summary>A folder-only Windows dialog; results are consumed on the Unity thread.</summary>
internal sealed class NativeFolderPicker
{
    private readonly object _gate = new();
    private bool _busy;
    private bool _hasResult;
    private string? _path;
    private string? _error;

    public bool Busy { get { lock (_gate) return _busy; } }

    public void Open(string initialDirectory)
    {
        lock (_gate)
        {
            if (_busy) return;
            _busy = true;
            _hasResult = false;
        }

        if (!OperatingSystem.IsWindows())
        {
            Complete(null, "当前系统无法打开目录选择器，请直接填写保存位置。");
            return;
        }

        try
        {
            var thread = new Thread(() =>
            {
                if (OperatingSystem.IsWindows()) OpenOnStaThread(initialDirectory);
                else Complete(null, "当前系统无法打开目录选择器，请直接填写保存位置。");
            })
            {
                IsBackground = true,
                Name = "AA export folder picker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex)
        {
            Plugin.LogPerformance("AA Video Export: folder picker start failed\n" + ex);
            Complete(null, "无法打开目录选择器，请直接填写保存位置。");
        }
    }

    public bool TryTakeResult(out string? path, out string? error)
    {
        lock (_gate)
        {
            path = _path;
            error = _error;
            if (!_hasResult) return false;
            _hasResult = false;
            _path = null;
            _error = null;
            return true;
        }
    }

    private void Complete(string? path, string? error)
    {
        lock (_gate)
        {
            _path = path;
            _error = error;
            _hasResult = true;
            _busy = false;
        }
    }

    [SupportedOSPlatform("windows")]
    private void OpenOnStaThread(string initialDirectory)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? initial = null;
        IShellItem? selected = null;
        var initialized = false;
        string? path = null;
        string? error = null;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0x2));
            initialized = true;
            dialog = (IFileOpenDialog)new FileOpenDialog();
            // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST | NOCHANGEDIR | DONTADDTORECENT.
            Marshal.ThrowExceptionForHR(dialog.SetOptions(0x20 | 0x40 | 0x800 | 0x8 | 0x02000000));
            Marshal.ThrowExceptionForHR(dialog.SetTitle("选择视频保存位置"));
            Marshal.ThrowExceptionForHR(dialog.SetOkButtonLabel("选择此文件夹"));
            if (Directory.Exists(initialDirectory))
            {
                var iid = typeof(IShellItem).GUID;
                // NGUI displays portable slashes, but Windows Shell parsing
                // requires the native spelling to select the initial folder.
                if (SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory), IntPtr.Zero, ref iid, out initial) >= 0 && initial != null)
                    Marshal.ThrowExceptionForHR(dialog.SetFolder(initial));
            }

            var result = dialog.Show(IntPtr.Zero);
            if (result != unchecked((int)0x800704C7)) // User cancelled.
            {
                Marshal.ThrowExceptionForHR(result);
                Marshal.ThrowExceptionForHR(dialog.GetResult(out selected));
                Marshal.ThrowExceptionForHR(selected.GetDisplayName(0x80058000, out var nativePath));
                try { path = Marshal.PtrToStringUni(nativePath); }
                finally { Marshal.FreeCoTaskMem(nativePath); }
            }
        }
        catch (Exception ex)
        {
            Plugin.LogPerformance("AA Video Export: folder selection failed\n" + ex);
            error = "目录选择失败，请直接填写保存位置。";
        }
        finally
        {
            ReleaseComObject(selected);
            if (!ReferenceEquals(initial, selected)) ReleaseComObject(initial);
            ReleaseComObject(dialog);
            if (initialized) CoUninitialize();
            Complete(path, error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseComObject(object? value)
    {
        if (value == null) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // The complete inherited vtable must be declared in native order.
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        [PreserveSig] int SetFileTypes(uint count, IntPtr filters);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IShellItem item);
        [PreserveSig] int SetFolder(IShellItem item);
        [PreserveSig] int GetFolder(out IShellItem item);
        [PreserveSig] int GetCurrentSelection(out IShellItem item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out IntPtr name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IShellItem item);
        [PreserveSig] int AddPlace(IShellItem item, uint placement);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr filter);
        [PreserveSig] int GetResults(out IntPtr items);
        [PreserveSig] int GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr value);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint format, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
}
