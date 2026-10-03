using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace AAVideoExport.Plugin;

// Optional compatibility. No Rukari assembly is referenced by the exporter.
// Bind only the exact supplied 1.4.1 build; unknown versions keep their own code.
internal static class MoreEffectsCompatibility
{
    private static readonly Guid SupportedModule = new("c7e07c8f-69a8-4ed7-8c66-c6052711cf27");
    private static readonly MethodInfo WallTimestamp = typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!;
    private static readonly MethodInfo AdaptedTimestamp = typeof(MoreEffectsCompatibility).GetMethod(nameof(ReadTimestamp), BindingFlags.Static | BindingFlags.Public)!;
    private static readonly List<WeakReference<object>> Runtimes = new();
    private static AnimationTimestampClock _clock = new();
    private static Harmony? _harmony;
    private static Action<string>? _log;
    private static FieldInfo? _activeField, _startedField;
    private static bool _installed;
    private static volatile bool _pending;
    private static long _captureReads;

    internal static void Initialize(Harmony harmony, Action<string> log)
    {
        _harmony = harmony;
        _log = log;
        _pending = true;
        AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
        TryInstall();
    }

    private static void AssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        // Assembly resolution may run off-thread. Patch only from the host thread.
        if (args.LoadedAssembly.GetName().Name == "Rukari.MoreEffects") _pending = true;
    }

    internal static void TryInstall()
    {
        if (_installed || !_pending || _harmony == null) return;
        _pending = false;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rukari.MoreEffects");
        if (assembly == null) return;
        try
        {
            if (assembly.GetName().Version != new Version(1, 4, 1, 0) || assembly.ManifestModule.ModuleVersionId != SupportedModule)
                throw new NotSupportedException("版本或构建不在已验证范围内");
            var type = assembly.GetType("AzureArchive.VideoTools.Runtime.CharacterPresetRuntime", true)!;
            var begin = type.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(long) }, null)!;
            var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(bool) }, null)!;
            BindAndPatch(_harmony, type, begin, tick);
            _log?.Invoke("更多的画面效果 1.4.1：已接入导出演出时钟（摇晃、转身、头槌、弹性拉伸）。");
        }
        catch (Exception error)
        {
            _log?.Invoke("更多的画面效果计时适配未启用：" + error.Message + "。导出插件仍可使用，此 Mod 的动作完整性尚未保证。");
        }
    }

    // Also exercised with a real Harmony patch against a managed action fixture.
    internal static void BindAndPatch(Harmony harmony, Type runtimeType, MethodInfo begin, MethodInfo tick)
    {
        ArgumentNullException.ThrowIfNull(begin);
        ArgumentNullException.ThrowIfNull(tick);
        var activeField = runtimeType.GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("CharacterPresetRuntime._active");
        if (!activeField.FieldType.IsGenericType || activeField.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)
            || activeField.FieldType.GenericTypeArguments[0] != typeof(int))
            throw new NotSupportedException("动作集合结构已改变");
        var presetType = activeField.FieldType.GenericTypeArguments[1];
        var startedField = presetType.GetField("<StartedAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (startedField == null || startedField.FieldType != typeof(long) || startedField.IsStatic)
            throw new NotSupportedException("动作起点结构已改变");
        foreach (var method in new[] { begin, tick })
        {
            if (Harmony.GetPatchInfo(method)?.Transpilers.Any() == true)
                throw new NotSupportedException("动作函数已有其他代码替换补丁");
            Rewrite(PatchProcessor.GetOriginalInstructions(method, (ILGenerator?)null)).ToList();
        }
        try
        {
            var rewrite = new HarmonyMethod(typeof(MoreEffectsCompatibility), nameof(Rewrite));
            harmony.Patch(begin, postfix: new HarmonyMethod(typeof(MoreEffectsCompatibility), nameof(TrackRuntime)), transpiler: rewrite);
            harmony.Patch(tick, transpiler: rewrite);
            _activeField = activeField;
            _startedField = startedField;
            _installed = true;
        }
        catch
        {
            harmony.Unpatch(begin, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(tick, HarmonyPatchType.All, harmony.Id);
            throw;
        }
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        var calls = result.Where(i => i.opcode == OpCodes.Call && Equals(i.operand, WallTimestamp)).ToArray();
        if (calls.Length != 1) throw new NotSupportedException("动作计时调用结构已改变");
        calls[0].operand = AdaptedTimestamp;
        return result;
    }

    public static long ReadTimestamp()
    {
        if (_clock.Active) _captureReads++;
        return _clock.Read();
    }

    internal static void TrackRuntime(object __instance)
    {
        Runtimes.RemoveAll(w => !w.TryGetTarget(out _));
        if (!Runtimes.Any(w => w.TryGetTarget(out var item) && ReferenceEquals(item, __instance)))
            Runtimes.Add(new WeakReference<object>(__instance));
    }

    internal static void BeginExport(Func<double> videoTime)
    {
        if (!_installed) return;
        _captureReads = 0;
        _clock.Begin(videoTime);
        _log?.Invoke("更多的画面效果：动作计时已切换到视频帧时钟。");
    }

    internal static void EndExport()
    {
        if (!_installed) return;
        bool wasActive = _clock.Active;
        _clock.End();
        RebaseActivePresets();
        if (wasActive) _log?.Invoke($"更多的画面效果：已恢复普通播放计时；本次动作时钟读取 {_captureReads} 次。");
    }

    private static void RebaseActivePresets()
    {
        long offset = _clock.Offset;
        if (offset == 0) return;
        var changes = new List<(object Preset, long Before, long After)>();
        // Validate every value before modifying any active animation.
        foreach (var weak in Runtimes)
        {
            if (!weak.TryGetTarget(out var runtime)) continue;
            if (_activeField!.GetValue(runtime) is not IDictionary active)
                throw new InvalidOperationException("动作集合无法读取，保留连续计时以避免取消后跳动。");
            foreach (var preset in active.Values)
            {
                if (preset == null) continue;
                long before = (long)_startedField!.GetValue(preset)!;
                changes.Add((preset, before, checked(before - offset)));
            }
        }
        int changed = 0;
        try
        {
            foreach (var change in changes)
            {
                _startedField!.SetValue(change.Preset, change.After);
                changed++;
            }
        }
        catch
        {
            for (int i = changed - 1; i >= 0; i--) _startedField!.SetValue(changes[i].Preset, changes[i].Before);
            throw;
        }
        _clock.ClearRebasedOffset();
    }

    internal static bool Shutdown()
    {
        try { EndExport(); }
        catch (Exception error)
        {
            _log?.Invoke("动作时钟恢复未完成，暂不卸载适配以保持动作连续：" + error.Message);
            return false;
        }
        AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
        Runtimes.Clear();
        _installed = false;
        _harmony = null;
        _pending = false;
        return true;
    }
}
