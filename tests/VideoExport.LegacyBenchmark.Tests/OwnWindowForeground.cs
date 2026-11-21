using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AAVideoExport.LegacyBenchmark.Tests;

// Native benchmark helper only. One request targets this exact process's AA
// window; no keyboard input, thread attachment, foreign-window activation or
// repeated attempt is permitted. Unity's subsequent focus state is authoritative.
internal sealed class OwnWindowForeground
{
 private bool attempted;
 internal static bool IsOwnForeground()=>ReadForeground().IsOwn;
 internal static ForegroundWindowState ReadForeground()
 {
  if(!OperatingSystem.IsWindows())return new(0,0,false);
  IntPtr foreground=GetForegroundWindow();
  if(foreground==IntPtr.Zero)return new(0,0,false);
  GetWindowThreadProcessId(foreground,out uint pid);
  return new(foreground.ToInt64(),pid,pid==checked((uint)Environment.ProcessId));
 }
 internal static object DescribeForeground(ForegroundWindowState state)
 {
  // Only callers recording a bounded transition/failure use this metadata
  // read. Never inspect titles, command lines, modules or foreign thread input.
  string processName="",className="",processLookupError="";
  if(state.Pid!=0)
   try{using var process=Process.GetProcessById(checked((int)state.Pid));processName=process.ProcessName;}
   catch(Exception error){processLookupError=error.GetType().Name;}
  if(state.Hwnd!=0)className=ClassName(new IntPtr(state.Hwnd));
  return new{state.Hwnd,state.Pid,ProcessName=processName,ClassName=className,state.IsOwn,ProcessLookupError=processLookupError};
 }
 internal object ActivateOnce()
 {
  if(attempted)throw new InvalidOperationException("Own-window foreground activation was already attempted.");
  attempted=true;
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("This native benchmark requires the Windows AA host.");
  using var process=Process.GetCurrentProcess();process.Refresh();
  uint pid=checked((uint)process.Id);
  IntPtr window=process.MainWindowHandle;
  if(window==IntPtr.Zero)
  {
   var candidates=new List<IntPtr>();
   EnumWindows((handle,_)=>{
    GetWindowThreadProcessId(handle,out uint owner);
    if(owner==pid&&GetWindow(handle,4)==IntPtr.Zero)candidates.Add(handle);
    return true;
   },IntPtr.Zero);
   window=candidates.OrderByDescending(handle=>ClassName(handle)=="UnityWndClass").ThenByDescending(IsWindowVisible).FirstOrDefault();
  }
  RequireOwn(window,pid);
  bool visibleBefore=IsWindowVisible(window),minimizedBefore=IsIconic(window);
  bool showReturned=ShowWindow(window,9); // SW_RESTORE: only this process's window.
  RequireOwn(window,pid);
  bool activationReturned=SetForegroundWindow(window);
  int activationError=activationReturned?0:Marshal.GetLastWin32Error();
  IntPtr foreground=GetForegroundWindow();GetWindowThreadProcessId(foreground,out uint foregroundPid);
  return new{Attempt=1,ProcessId=process.Id,Window=window.ToInt64(),Class=ClassName(window),
   VisibleBefore=visibleBefore,MinimizedBefore=minimizedBefore,ShowWindowReturned=showReturned,
   SetForegroundWindowReturned=activationReturned,Win32Error=activationError,ForegroundAfterIsOwnProcess=foregroundPid==pid};
 }
 private static void RequireOwn(IntPtr window,uint pid)
 {
  if(window==IntPtr.Zero||!IsWindow(window))throw new InvalidOperationException("The current AA process has no live native main window.");
  GetWindowThreadProcessId(window,out uint owner);
  if(owner!=pid)throw new InvalidOperationException("Refusing to activate a window outside the current AA process.");
 }
 private static string ClassName(IntPtr window){var text=new StringBuilder(256);GetClassName(window,text,text.Capacity);return text.ToString();}
 private delegate bool EnumWindowCallback(IntPtr window,IntPtr data);
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback,IntPtr data);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
 [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int maximum);
}
internal readonly record struct ForegroundWindowState(long Hwnd,uint Pid,bool IsOwn);
