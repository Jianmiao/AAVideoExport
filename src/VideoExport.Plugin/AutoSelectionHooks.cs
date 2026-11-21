using System.Reflection;
using AAVideoExport.Core;
using HarmonyLib;

namespace AAVideoExport.Plugin;

internal static class AutoSelectionHooks
{
    private sealed record Binding(MethodInfo MoveNext, Dictionary<string, PropertyInfo> Properties)
    {
        internal object? Get(object instance, string name) => Properties[name].GetValue(instance);
    }

    private static Func<NativeAutoSelection?>? _current;
    private static Action<Exception>? _failed;
    private static Binding? _countdown, _press;

    internal static void Initialize(Harmony harmony, Func<NativeAutoSelection?> current, Action<Exception> failed, Action<string> log)
    {
        _current = current;
        _failed = failed;
        _countdown = Install(harmony, typeof(SelectionManager), "_CoAutoSelect_d__",
            new[] { "__4__this", "index", "_elem_5__2", "_elapsed_5__3" }, nameof(CountdownFinished), false, log);
        _press = Install(harmony, typeof(UI.MXButton), "_CoSimulatePress_d__",
            new[] { "__4__this", "__1__state" }, nameof(SubmitAutoPress), true, log);
        harmony.Patch(AccessTools.Method(typeof(SelectionManager), nameof(SelectionManager.CreateSelection)),
            prefix: new HarmonyMethod(typeof(AutoSelectionHooks), nameof(NewSelection)));
        log(_countdown != null && _press != null ? "AUTO 选项导出适配已启用：保留原生倒计时，直接提交标记的分支。"
            : "AUTO 选项接口不兼容：遇到选项时将停止导出并提示，避免持续录制停留画面。");
    }

    private static Binding? Install(Harmony harmony, Type owner, string prefix, string[] names, string hook, bool isPrefix, Action<string> log)
    {
        var type = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault(t => t.Name.StartsWith(prefix, StringComparison.Ordinal));
        var method = type == null ? null : AccessTools.Method(type, "MoveNext");
        var properties = names.ToDictionary(name => name, name => type?.GetProperty(name));
        if (method == null || method.ReturnType != typeof(bool) || properties.Values.Any(p => p == null || !p.CanRead)
            || isPrefix && !properties["__1__state"]!.CanWrite)
        {
            log("AUTO selection hook unavailable: " + owner.Name + "." + prefix);
            return null;
        }
        try
        {
            var patch = new HarmonyMethod(typeof(AutoSelectionHooks), hook);
            harmony.Patch(method, prefix: isPrefix ? patch : null, postfix: isPrefix ? null : patch);
            return new Binding(method, properties.ToDictionary(p => p.Key, p => p.Value!));
        }
        catch (Exception error)
        {
            log("AUTO selection hook unavailable: " + owner.Name + "." + prefix + " (" + error.GetType().Name + ").");
            return null;
        }
    }

    internal static void EnsureSupported()
    {
        if (_countdown == null || _press == null)
            throw new ExportException("selection_api_unsupported", "当前 AA 的 AUTO 选项接口尚未适配，导出已停止。请检查 AA 与内录 Mod 的版本。");
    }

    internal static void Shutdown()
    {
        _current = null;
        _failed = null;
        _countdown = _press = null;
    }

    private static void NewSelection(SelectionManager __instance) => _current?.Invoke()?.Reset(__instance);

    private static void CountdownFinished(object __instance, bool __result)
    {
        var session = _current?.Invoke();
        if (__result || session == null || _countdown == null) return;
        try
        {
            if (_countdown.Get(__instance, "__4__this") is not SelectionManager manager
                || _countdown.Get(__instance, "index") is not int index
                || _countdown.Get(__instance, "_elapsed_5__3") is not float elapsed
                || !float.IsFinite(elapsed) || !float.IsFinite(manager.autoSelectDelaySeconds)
                || elapsed < manager.autoSelectDelaySeconds)
                return;
            var choice = session.FindChoice(manager, index);
            if (choice != null && _countdown.Get(__instance, "_elem_5__2") is SelectionElement original && choice == original)
                session.Submit(choice, "countdown-complete");
        }
        catch (Exception error) { _failed?.Invoke(new ExportException("selection_submit_failed", "AUTO 选项提交失败，导出已停止。请重新加载剧情后重试。", error)); }
    }

    private static bool SubmitAutoPress(object __instance, ref bool __result)
    {
        var session = _current?.Invoke();
        if (session == null || _press == null) return true;
        try
        {
            if (_press.Get(__instance, "__1__state") is not int state || state != 0
                || _press.Get(__instance, "__4__this") is not UI.MXButton button)
                return true;
            var choice = session.FindButton(button);
            if (choice == null) return true;
            _press.Properties["__1__state"].SetValue(__instance, -1);
            __result = false;
            session.Submit(choice, "auto-button");
            return false;
        }
        catch (Exception error)
        {
            __result = false;
            _failed?.Invoke(new ExportException("selection_submit_failed", "AUTO 选项提交失败，导出已停止。请重新加载剧情后重试。", error));
            return false;
        }
    }
}
