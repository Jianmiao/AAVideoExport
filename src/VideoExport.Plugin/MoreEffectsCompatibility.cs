using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using AAVideoExport.Core;

namespace AAVideoExport.Plugin;

// Optional compatibility. No Rukari assembly is referenced by the exporter.
// Bind only exact, inspected builds; unknown versions keep their own code.
internal static class MoreEffectsCompatibility
{
    private static readonly (Version Version, Guid Module, bool ActionTimeline)[] SupportedBuilds =
    {
        // 1.4.1 was the first validated build. Keep it for existing installs.
        (new Version(1, 4, 1, 0), new Guid("c7e07c8f-69a8-4ed7-8c66-c6052711cf27"), false),
        // The active AA 1.0.1 profile currently ships 1.6.7. Its Begin/Tick
        // Stopwatch call shape and ActivePreset.StartedAt field are unchanged.
        // It also contains the separately validated PlayerActionTimelineRuntime
        // playback gate. Its assembly identity is pinned separately.
        (new Version(1, 6, 7, 0), new Guid("7faa0394-68bb-4307-9736-80ceb3c87b57"), true)
    };
    private static readonly MethodInfo WallTimestamp = typeof(Stopwatch).GetMethod(nameof(Stopwatch.GetTimestamp))!;
    private static readonly MethodInfo AdaptedTimestamp = typeof(MoreEffectsCompatibility).GetMethod(nameof(ReadTimestamp), BindingFlags.Static | BindingFlags.Public)!;
    private static readonly List<WeakReference<object>> Runtimes = new();
    private static AnimationTimestampClock _clock = new();
    private static Harmony? _harmony;
    private static Action<string>? _log;
    private static FieldInfo? _activeField, _startedField;
    private static FieldInfo? _actionGateField, _actionDueAtField, _actionDispatchByField;
    private static FieldInfo? _queueField, _queueLockField, _queuePendingField, _queueOriginsField, _queueDueAtField;
    private static PropertyInfo? _actionStatusProperty;
    private static bool _characterInstalled, _actionInstalled;
    private static volatile bool _pending;
    private static long _captureReads;
    private static string _status = "stopped";
    private static string _failureReason = "";

    internal static bool IsReady => _characterInstalled || _actionInstalled;
    // Stable runtime contract for strict admission: an absent optional Mod is
    // compatible, while a loaded Mod must report full clock coverage.
    internal static bool IsLoaded => FindLoadedAssembly() != null;
    internal static bool CharacterClockInstalled => _characterInstalled;
    internal static bool ActionTimelineClockInstalled => _actionInstalled;
    internal static bool FullClockCoverage => _status == "full";
    internal static string Status => _status;
    internal static string FailureReason => _failureReason;

    internal static void Initialize(Harmony harmony, Action<string> log)
    {
        _harmony = harmony;
        _log = log;
        _pending = true;
        _status = "waiting";
        _failureReason = "";
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
        if (IsReady || !_pending || _harmony == null) return;
        _pending = false;
        var assembly = FindLoadedAssembly();
        if (assembly == null) return;
        var version = assembly.GetName().Version ?? new Version(0, 0);
        try
        {
            var build = (ExportBuildProfile.DevelopmentAuditEnabled
                ? FindSupportedBuild(version, assembly.ManifestModule.ModuleVersionId)
                : SupportedBuilds.Where(value => value.Version == version).Select(value => ((Version Version, Guid Module, bool ActionTimeline)?)value).FirstOrDefault())
                ?? throw new NotSupportedException("版本或构建不在已验证范围内");
            var failures = new List<string>();
            try
            {
                var type = assembly.GetType("AzureArchive.VideoTools.Runtime.CharacterPresetRuntime", true)!;
                var begin = type.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(string), typeof(long) }, null)!;
                var tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(int), typeof(bool) }, null)!;
                BindAndPatch(_harmony, type, begin, tick);
                _log?.Invoke(AdaptationEnabledMessage(version));
            }
            catch (Exception error)
            {
                failures.Add("CharacterPresetRuntime: " + error.Message);
            }
            if (build.ActionTimeline)
            {
                try
                {
                    var type = assembly.GetType("AzureArchive.VideoTools.Runtime.PlayerActionTimelineRuntime", true)!;
                    var now = type.GetMethod("Now", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null)!;
                    var queueOwner = assembly.GetType("AzureArchive.VideoTools.Runtime.PlayerCommandObservationRuntime", true)!;
                    BindActionTimelineAndPatch(_harmony, type, now, queueOwner);
                    _log?.Invoke(ActionTimelineEnabledMessage(version));
                }
                catch (Exception error)
                {
                    failures.Add("PlayerActionTimelineRuntime: " + error.Message);
                }
            }
            if (!IsReady)
                throw new NotSupportedException(string.Join("; ", failures));
            _failureReason = string.Join("; ", failures);
            _status = failures.Count == 0 ? (ActionTimelineClockInstalled ? "full" : "character-only") : "partial";
            if (failures.Count != 0)
                _log?.Invoke("更多的画面效果部分计时适配未启用：" + _failureReason + "。普通播放可继续；严格导出将停止，以免未适配动作失去同步。");
        }
        catch (Exception error)
        {
            _status = "unsupported";
            _failureReason = error.Message;
            _log?.Invoke("更多的画面效果计时适配未启用：" + error.Message + "。普通播放可继续；严格导出将停止，此 Mod 的动作完整性尚未保证。");
        }
    }

    private static (Version Version, Guid Module, bool ActionTimeline)? FindSupportedBuild(Version version, Guid moduleVersionId)
    {
        foreach (var build in SupportedBuilds)
            if (build.Version == version && build.Module == moduleVersionId) return build;
        return null;
    }

    internal static bool IsSupportedBuild(Version version, Guid moduleVersionId) =>
        FindSupportedBuild(version, moduleVersionId).HasValue;

    private static Assembly? FindLoadedAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rukari.MoreEffects");

    internal static void EnsureStrictExportAllowed()
    {
        EnsureOwnEffectsAllowed();
        CharacterVoiceCompatibility.EnsureStrictExportAllowed();
    }

    internal static void EnsureOwnEffectsAllowed() => StrictModAdmission.EnsureCompatible(IsLoaded, FullClockCoverage, Status, FailureReason);

    internal static string AdaptationEnabledMessage(Version? version) =>
        $"更多的画面效果 {version?.ToString() ?? "未知版本"}：CharacterPresetRuntime 已接入导出演出时钟（摇晃、转身、头槌、弹性拉伸）。";

    internal static string ActionTimelineEnabledMessage(Version? version) =>
        $"更多的画面效果 {version?.ToString() ?? "未知版本"}：PlayerActionTimelineRuntime 已接入导出演出时钟（播放动作门控）。";

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
            if (ExportBuildProfile.DevelopmentAuditEnabled && Harmony.GetPatchInfo(method)?.Transpilers.Any() == true)
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
            _characterInstalled = true;
        }
        catch
        {
            harmony.Unpatch(begin, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(tick, HarmonyPatchType.All, harmony.Id);
            throw;
        }
    }

    // Narrow adapter for the exact 1.6.7 PlayerActionTimelineRuntime.Now
    // method. The target returns seconds, unlike CharacterPresetRuntime's
    // Stopwatch ticks, so the prefix converts the shared synthetic ticks back
    // to seconds. It never patches Stopwatch globally.
    internal static void BindActionTimelineAndPatch(Harmony harmony, Type runtimeType, MethodInfo now, Type queueOwnerType)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(runtimeType);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(queueOwnerType);
        if (now.DeclaringType != runtimeType || !now.IsStatic || now.ReturnType != typeof(double) || now.GetParameters().Length != 0)
            throw new NotSupportedException("动作时间方法签名已改变");
        var gateField = runtimeType.GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(runtimeType.FullName, "Gate");
        if (!gateField.IsStatic)
            throw new NotSupportedException("动作门控字段不是静态字段");
        var gateType = gateField.FieldType;
        var dueAtField = gateType.GetField("<DueAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(gateType.FullName, "<DueAt>k__BackingField");
        var dispatchByField = gateType.GetField("_dispatchBy", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(gateType.FullName, "_dispatchBy");
        var statusProperty = gateType.GetProperty("Status", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(gateType.FullName, "Status");
        if (dueAtField.FieldType != typeof(double) || dispatchByField.FieldType != typeof(double)
            || !statusProperty.PropertyType.IsEnum || statusProperty.GetMethod == null)
            throw new NotSupportedException("动作门控时间或状态结构已改变");
        var statusNames = Enum.GetNames(statusProperty.PropertyType);
        foreach (var required in new[] { "Idle", "AwaitingDispatch", "Running", "Completed", "DispatchTimeout", "CompletionTimeout", "Cancelled" })
            if (!statusNames.Contains(required, StringComparer.Ordinal))
                throw new NotSupportedException("动作门控状态结构已改变");
        // Now also timestamps deferred slot actions. Bind their restoration
        // state before installing the prefix, not only the completion gate.
        var queueField = queueOwnerType.GetField("SegmentQueue", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(queueOwnerType.FullName, "SegmentQueue");
        var queueType = queueField.FieldType;
        var queueLock = queueType.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(queueType.FullName, "_gate");
        var pending = queueType.GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(queueType.FullName, "_pending");
        var origins = queueType.GetField("_origins", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(queueType.FullName, "_origins");
        if (!queueField.IsInitOnly || queueLock.FieldType != typeof(object) || !queueLock.IsInitOnly
            || !pending.IsInitOnly || !pending.FieldType.IsGenericType
            || pending.FieldType.GetGenericTypeDefinition() != typeof(List<>)
            || origins.FieldType != typeof(Dictionary<long, double>) || !origins.IsInitOnly)
            throw new NotSupportedException("动作队列结构已改变");
        var segmentType = pending.FieldType.GenericTypeArguments[0];
        var queueDueAt = segmentType.GetField("<DueAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(segmentType.FullName, "<DueAt>k__BackingField");
        if (!segmentType.IsClass || queueDueAt.FieldType != typeof(double) || queueDueAt.IsStatic)
            throw new NotSupportedException("动作队列时间结构已改变");
        if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            ValidateActionNow(now);
            var patches = Harmony.GetPatchInfo(now);
            if (patches != null && (patches.Prefixes.Any() || patches.Postfixes.Any() || patches.Transpilers.Any() || patches.Finalizers.Any()))
                throw new NotSupportedException("动作时间方法已有其他代码替换补丁");
        }
        try
        {
            harmony.Patch(now, prefix: new HarmonyMethod(typeof(MoreEffectsCompatibility), nameof(ActionNowPrefix)));
            _actionGateField = gateField;
            _actionDueAtField = dueAtField;
            _actionDispatchByField = dispatchByField;
            _actionStatusProperty = statusProperty;
            _queueField = queueField;
            _queueLockField = queueLock;
            _queuePendingField = pending;
            _queueOriginsField = origins;
            _queueDueAtField = queueDueAt;
            _actionInstalled = true;
        }
        catch
        {
            harmony.Unpatch(now, HarmonyPatchType.All, harmony.Id);
            throw;
        }
    }

    private static void ValidateActionNow(MethodInfo now)
    {
        var instructions = PatchProcessor.GetOriginalInstructions(now, (ILGenerator?)null)
            .Where(i => i.opcode != OpCodes.Nop).ToList();
        var frequency = typeof(Stopwatch).GetField(nameof(Stopwatch.Frequency), BindingFlags.Static | BindingFlags.Public)!;
        if (instructions.Count != 6 || instructions.Any(i => i.blocks.Count != 0)
            || instructions[0].opcode != OpCodes.Call || !Equals(instructions[0].operand, WallTimestamp)
            || instructions[1].opcode != OpCodes.Conv_R8
            || instructions[2].opcode != OpCodes.Ldsfld || !Equals(instructions[2].operand, frequency)
            || instructions[3].opcode != OpCodes.Conv_R8
            || instructions[4].opcode != OpCodes.Div || instructions[5].opcode != OpCodes.Ret)
            throw new NotSupportedException("动作时间调用结构已改变");
    }

    internal static bool ActionNowPrefix(ref double __result)
    {
        // Keep the synthetic offset visible if end-of-export rebasing failed;
        // the caller must not observe wall time against still-synthetic gate
        // deadlines. A successful rebase clears the offset before normal play.
        if (!_clock.Active && _clock.Offset == 0) return true;
        _captureReads++;
        __result = _clock.Read() / (double)Stopwatch.Frequency;
        return false;
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
        if (!IsReady) return;
        if (_status == "restoration-failed")
            throw new InvalidOperationException("动作时钟恢复曾失败，请重新启动 AA 后再导出。");
        _captureReads = 0;
        _clock.Begin(videoTime);
        _log?.Invoke("更多的画面效果：动作计时已切换到视频帧时钟。");
    }

    internal static void EndExport()
    {
        if (!IsReady) return;
        bool wasActive = _clock.Active;
        try
        {
            _clock.End();
            RebaseActiveState();
        }
        catch (Exception error)
        {
            _status = "restoration-failed";
            _failureReason = error.Message;
            throw;
        }
        if (wasActive) _log?.Invoke($"更多的画面效果：已恢复普通播放计时；本次动作时钟读取 {_captureReads} 次。");
    }

    private static void RebaseActiveState()
    {
        long offset = _clock.Offset;
        if (offset == 0) return;
        object? queue = null;
        object? queueLock = null;
        if (_actionInstalled)
        {
            queue = _queueField!.GetValue(null)
                ?? throw new InvalidOperationException("动作队列无法读取，保留连续计时以避免取消后跳动。");
            queueLock = _queueLockField!.GetValue(queue)
                ?? throw new InvalidOperationException("动作队列锁无法读取，保留连续计时以避免取消后跳动。");
            Monitor.Enter(queueLock);
        }
        try { RebaseActiveStateLocked(offset, queue); }
        finally { if (queueLock != null) Monitor.Exit(queueLock); }
    }

    private sealed record RebaseChange(Action Apply, Action Restore);

    private static void AddFieldChange(List<RebaseChange> changes, FieldInfo field, object target, object before, object after) =>
        changes.Add(new RebaseChange(() => field.SetValue(target, after), () => field.SetValue(target, before)));

    private static double RebasedSeconds(double before, double offset)
    {
        double after = before - offset;
        if (!double.IsFinite(before) || !double.IsFinite(after))
            throw new InvalidOperationException("动作时间无效，保留连续计时以避免取消后跳动。");
        return after;
    }

    private static void RebaseActiveStateLocked(long offset, object? queue)
    {
        var changes = new List<RebaseChange>();
        double secondsOffset = offset / (double)Stopwatch.Frequency;
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
                AddFieldChange(changes, _startedField, preset, before, checked(before - offset));
            }
        }
        if (_actionInstalled)
        {
            var gate = _actionGateField!.GetValue(null)
                ?? throw new InvalidOperationException("动作门控实例无法读取，保留连续计时以避免取消后跳动。");
            var status = _actionStatusProperty!.GetValue(gate);
            string statusName = status == null ? "" : Enum.GetName(_actionStatusProperty.PropertyType, status) ?? "";
            if (statusName is "AwaitingDispatch" or "Running")
            {
                double beforeDue = (double)_actionDueAtField!.GetValue(gate)!;
                double beforeDispatch = (double)_actionDispatchByField!.GetValue(gate)!;
                AddFieldChange(changes, _actionDueAtField, gate, beforeDue, RebasedSeconds(beforeDue, secondsOffset));
                AddFieldChange(changes, _actionDispatchByField, gate, beforeDispatch, RebasedSeconds(beforeDispatch, secondsOffset));
            }
            if (_queuePendingField!.GetValue(queue) is not IList pending
                || _queueOriginsField!.GetValue(queue) is not Dictionary<long, double> origins)
                throw new InvalidOperationException("动作队列状态无法读取，保留连续计时以避免取消后跳动。");
            foreach (object? segment in pending)
            {
                if (segment == null) throw new InvalidOperationException("动作队列包含无效项目。");
                double before = (double)_queueDueAtField!.GetValue(segment)!;
                AddFieldChange(changes, _queueDueAtField, segment, before, RebasedSeconds(before, secondsOffset));
            }
            foreach (var origin in origins)
            {
                double after = RebasedSeconds(origin.Value, secondsOffset);
                changes.Add(new RebaseChange(() => origins[origin.Key] = after, () => origins[origin.Key] = origin.Value));
            }
        }
        int changed = 0;
        try
        {
            foreach (var change in changes)
            {
                // Include the attempted write: reflection or a future setter
                // can throw after mutating its target.
                changed++;
                change.Apply();
            }
            _clock.ClearRebasedOffset();
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            for (int i = changed - 1; i >= 0; i--)
            {
                try { changes[i].Restore(); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            throw new AggregateException("动作时钟恢复失败，保留连续计时且停止严格导出。", errors);
        }
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
        _characterInstalled = false;
        _actionInstalled = false;
        _actionGateField = _actionDueAtField = _actionDispatchByField = null;
        _queueField = _queueLockField = _queuePendingField = _queueOriginsField = _queueDueAtField = null;
        _actionStatusProperty = null;
        _status = "stopped";
        _harmony = null;
        _pending = false;
        return true;
    }
}
