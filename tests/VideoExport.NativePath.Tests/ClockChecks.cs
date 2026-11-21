using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using AAVideoExport.Plugin;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AAVideoExport.NativePath.Tests;

public sealed class ClockChecks : MonoBehaviour
{
    private static readonly Type Clock = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeExportClock", true)!;
    private const BindingFlags Members = BindingFlags.Static | BindingFlags.NonPublic;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private IDisposable? _ownership;
    private WaitForSecondsRealtime? _wait;
    private float _virtual, _rawBefore;
    private double _started;
    private int _stage, _passed;

    public ClockChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (_stage == 99 || _wall.Elapsed.TotalSeconds < 12) return;
        try
        {
            if (_stage == 0)
            {
                Check(RenderControlV1.IsReady, "provider readiness");
                _ownership = (IDisposable)typeof(RenderControlV1).GetMethod("Acquire", Members)!.Invoke(null, null)!;
                _rawBefore = Time.realtimeSinceStartup;
                _started = _wall.Elapsed.TotalSeconds;
                _virtual = 30;
                Clock.GetMethod("Begin", Members)!.Invoke(null, new object[] { _virtual, 1f / 30, (Func<float>)(() => _virtual) });
                _wait = new WaitForSecondsRealtime(2);
                Check(_wait.keepWaiting, "native wait begins on video time");
                _virtual = 31;
                _stage = 1;
                return;
            }
            if (_stage == 1)
            {
                if (_wall.Elapsed.TotalSeconds - _started < .2) return;
                Check(Math.Abs(Time.realtimeSinceStartup - _rawBefore - (_wall.Elapsed.TotalSeconds - _started)) < .1,
                    "global realtime continues on wall time");
                Check(Math.Abs(Time.unscaledTime - 31) < .001f, "native unscaled time reads frame clock");
                Check(_wait!.keepWaiting, "native wait retains one video second");
                _virtual = 32;
                _stage = 2;
                return;
            }
            Check(!_wait!.keepWaiting, "native wait completes on video boundary");
            Clock.GetMethod("Restore", Members)!.Invoke(null, null);
            Check(!(bool)Clock.GetProperty("IsActive", Members)!.GetValue(null)!, "clock restores inactive state");
            _ownership!.Dispose();
            _ownership = null;
            Check(!RenderControlV1.IsRenderingOwned && RenderControlV1.IsReady, "render ownership restored after clock");
            _stage = 99;
            Plugin.Report($"PATH-CHECK RESULT {_passed} passed; 0 failed");
        }
        catch (Exception error)
        {
            _stage = 99;
            try { Clock.GetMethod("Restore", Members)!.Invoke(null, null); }
            catch (Exception restore) { Plugin.Report("PATH-CHECK clock cleanup: " + restore); }
            try { _ownership?.Dispose(); }
            catch (Exception restore) { Plugin.Report("PATH-CHECK ownership cleanup: " + restore); }
            Plugin.Report("PATH-CHECK FAIL native-clock: " + error);
            Plugin.Report($"PATH-CHECK RESULT {_passed} passed; 1 failed");
        }
    }

    [HideFromIl2Cpp]
    private void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
        _passed++;
        Plugin.Report("PATH-CHECK PASS " + name);
    }
}
