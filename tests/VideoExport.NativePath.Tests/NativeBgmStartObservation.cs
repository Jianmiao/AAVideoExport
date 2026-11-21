using System.Reflection;
using System.Text.Json;
using AAVideoExport.Plugin;
using HarmonyLib;
using UnityEngine;

namespace AAVideoExport.NativePath.Tests;

// Explicit external diagnostic. Original native request, asset callback,
// coroutine, Play and mixer calls run unchanged. No clip loading or scheduling.
internal static class NativeBgmStartObservation
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Harmony? hooks;
    private static Attempt? current;
    [ThreadStatic] private static AudioManager.BGMInfo? callingInfo;
    internal static bool Enabled => Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_BGM_TRACE") == "1";

    internal static void Install()
    {
        if (!Enabled || hooks != null) return;
        hooks = new Harmony("aave.test.original-bgm-start-observation");
        try
        {
            Patch(typeof(AudioManager).GetMethod(nameof(AudioManager.PlayBGM), Members, null, new[] { typeof(long) }, null)!, nameof(BeforeRequest), nameof(AfterRequest));
            Patch(typeof(AudioManager.__c__DisplayClass18_0).GetMethod("_PlayBGM_b__1", Members)!, nameof(BeforeResourceCallback), nameof(AfterResourceCallback));
            var move = typeof(AudioManager._PlayBGMTask_d__23).GetMethod("MoveNext", Members)!;
            hooks.Patch(move, prefix: Method(nameof(BeforeMove)), postfix: Method(nameof(AfterMove)), finalizer: Method(nameof(FinalizeMove)));
            Patch(typeof(AudioSource).GetMethod(nameof(AudioSource.Play), Members, null, Type.EmptyTypes, null)!, nameof(BeforePlay), nameof(AfterPlay));
            var capture = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeCaptureScope", true)!;
            Patch(capture.GetMethod("RenderAudio", Members)!, nameof(BeforeMixer), nameof(AfterMixer));
        }
        catch { hooks.UnpatchSelf(); hooks = null; throw; }
        Plugin.Report("PATH-CHECK BGM-TRACE enabled; original calls unchanged; bounded memory records flushed after cleanup");
    }
    private static HarmonyMethod Method(string name) => new(typeof(NativeBgmStartObservation), name);
    private static void Patch(MethodInfo method, string prefix, string postfix)
    {
        if (method == null) throw new MissingMethodException("Original BGM diagnostic seam is unavailable.");
        hooks!.Patch(method, prefix: Method(prefix), postfix: Method(postfix));
    }
    internal static void BeginAttempt(int attempt, ExportHost host, string evidenceDirectory)
    {
        if (!Enabled) return;
        Install();
        if (current != null) throw new InvalidOperationException("Previous BGM startup observation was not finished.");
        current = new(attempt, host, evidenceDirectory, Environment.CurrentManagedThreadId);
        callingInfo = null;
    }
    internal static void FinishAttempt(string outcome)
    {
        var attempt = current; if (attempt == null) return;
        current = null; callingInfo = null;
        bool complete = attempt.Requests > 0 && attempt.Callbacks > 0 && attempt.InitialMoves > 0 && attempt.Plays > 0 && attempt.MixerAfter > 0;
        string path = Path.Combine(attempt.Directory, $"bgm-start-attempt-{attempt.Index:00}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            Schema = "aave.test.original-bgm-start.v1", Attempt = attempt.Index, Outcome = outcome, Complete = complete,
            attempt.Requests, attempt.Callbacks, attempt.InitialMoves, attempt.Plays, attempt.MixerCalls, attempt.AuthoredMixerCalls, attempt.MixerAfter,
            MainThread = attempt.Thread, Events = attempt.Events, Errors = attempt.Errors,
            Scope = "Original native BGM request/resource callback/iterator/pooled-source Play and before/after first eight mixer calls plus first eight authored mixer calls. All records buffered until real cleanup; no source, clip, scheduling, PCM, settings or clocks are mutated."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Report($"PATH-CHECK BGM-TRACE attempt={attempt.Index}; complete={complete}; requests={attempt.Requests}; callbacks={attempt.Callbacks}; initial_moves={attempt.InitialMoves}; plays={attempt.Plays}; errors={attempt.Errors.Count}; evidence={path}");
        if (outcome == "completed" && (!complete || attempt.Errors.Count != 0))
            throw new InvalidOperationException("Completed original BGM startup diagnostic is missing a seam or contains observation errors; see " + path);
    }
    internal static void Shutdown()
    {
        FinishAttempt("observer-shutdown"); hooks?.UnpatchSelf(); hooks = null;
    }
    private static void BeforeRequest(AudioManager __instance, long __0) => Guard("bgm-request-before", attempt => {
        attempt.Requests++; Record(attempt, "bgm-request-before", new { Id = __0, Manager = __instance.Pointer.ToString() });
    });
    private static void AfterRequest(AudioManager __instance, long __0, bool __runOriginal) => Guard("bgm-request-after", attempt =>
        Record(attempt, "bgm-request-after", new { Id = __0, Manager = __instance.Pointer.ToString(), OriginalRan = __runOriginal }));
    private static void BeforeResourceCallback(AudioManager.__c__DisplayClass18_0 __instance, AudioClip __0) => Guard("bgm-resource-before", attempt => {
        attempt.Callbacks++; Remember(attempt, __instance.info);
        Record(attempt, "bgm-resource-before", new { Id = __instance.id, Manager = __instance.__4__this?.Pointer.ToString(), Info = Info(__instance.info), LoadedClip = Clip(__0) });
    });
    private static void AfterResourceCallback(AudioManager.__c__DisplayClass18_0 __instance, AudioClip __0, bool __runOriginal) => Guard("bgm-resource-after", attempt =>
        Record(attempt, "bgm-resource-after", new { Id = __instance.id, Info = Info(__instance.info), LoadedClip = Clip(__0), OriginalRan = __runOriginal }));
    private static void BeforeMove(AudioManager._PlayBGMTask_d__23 __instance, out MoveScope? __state)
    {
        __state = null; var attempt = current; if (attempt == null) return;
        MoveScope? scope = null;
        Guard("bgm-iterator-before", _ => {
            Remember(attempt, __instance.info);
            int state = __instance.__1__state;
            bool retain = state == 0 || attempt.IteratorSamples++ < 8;
            if (state == 0) attempt.InitialMoves++;
            if (retain) Record(attempt, "bgm-iterator-before", new { State = state, Info = Info(__instance.info), LoadedClip = Clip(__instance.loadedClip) });
            scope = new(callingInfo, __instance.info, retain);
            callingInfo = __instance.info;
        });
        // Test-owned call context identifies Play inside the original native
        // coroutine even when its pooled source is assigned after our prefix.
        __state = scope;
    }
    private static void AfterMove(AudioManager._PlayBGMTask_d__23 __instance, bool __result, bool __runOriginal, MoveScope? __state)
    {
        if (__state?.Retain != true) return;
        Guard("bgm-iterator-after", attempt => Record(attempt, "bgm-iterator-after", new {
            State = __instance.__1__state, Result = __result, OriginalRan = __runOriginal, Info = Info(__instance.info), LoadedClip = Clip(__instance.loadedClip)
        }));
    }
    private static void FinalizeMove(Exception? __exception, MoveScope? __state)
    {
        if (__state != null) callingInfo = __state.Previous;
        if (__exception != null) Guard("bgm-iterator-exception", attempt => attempt.Errors.Add(__exception.GetBaseException().Message));
    }
    private static AudioManager.BGMInfo? MatchingInfo(Attempt attempt, AudioSource source)
    {
        if (callingInfo?.currentSource?.Pointer == source.Pointer) return callingInfo;
        return attempt.Infos.Values.FirstOrDefault(info => info.currentSource?.Pointer == source.Pointer);
    }
    private static void BeforePlay(AudioSource __instance, out bool __state)
    {
        __state = false; var attempt = current; if (attempt == null) return;
        bool matched = false;
        Guard("pooled-bgm-play-before", _ => {
            var info = MatchingInfo(attempt, __instance); if (info == null) return;
            matched = true; attempt.Plays++;
            Record(attempt, "pooled-bgm-play-before", new { InsideOriginalIterator = callingInfo != null, Info = Info(info), Source = Source(__instance) });
        });
        __state = matched;
    }
    private static void AfterPlay(AudioSource __instance, bool __state, bool __runOriginal)
    {
        if (__state) Guard("pooled-bgm-play-after", attempt => Record(attempt, "pooled-bgm-play-after", new { Source = Source(__instance), OriginalRan = __runOriginal }));
    }
    private static void BeforeMixer(object __instance, out MixerScope? __state)
    {
        __state = null; var attempt = current;
        if (attempt == null || !ReferenceEquals(Field(attempt.Host, "_native"), __instance)) return;
        Guard("mixer-before", _ => {
            int call = ++attempt.MixerCalls;
            bool authored = Field(attempt.Host, "_strictPriming") is false && Field(attempt.Host, "_strictAwaitingStart") is false;
            int authoredCall = authored ? ++attempt.AuthoredMixerCalls : 0;
            if (call > 8 && (!authored || authoredCall > 8)) return;
            attempt.PendingMixer = new(call, authoredCall);
            Record(attempt, "mixer-before", new { Call = call, AuthoredCall = authoredCall, Sources = attempt.Infos.Values.Select(Info).ToArray() });
        });
        __state = attempt.PendingMixer; attempt.PendingMixer = null;
    }
    private static void AfterMixer(object __instance, (float[] Samples, int Count) __result, bool __runOriginal, MixerScope? __state)
    {
        if (__state == null) return;
        Guard("mixer-after", attempt => {
            attempt.MixerAfter++;
            Record(attempt, "mixer-after", new { __state.Call, __state.AuthoredCall, FloatCount = __result.Count,
                OriginalRan = __runOriginal, Sources = attempt.Infos.Values.Select(Info).ToArray() });
        });
    }
    private static void Remember(Attempt attempt, AudioManager.BGMInfo? info)
    {
        if (info == null) return;
        if (attempt.Infos.Count >= 16 && !attempt.Infos.ContainsKey(info.Pointer)) throw new InvalidOperationException("BGM diagnostic exceeded 16 original entries.");
        attempt.Infos[info.Pointer] = info;
    }
    private static object? Info(AudioManager.BGMInfo? info) => info == null ? null : new {
        Pointer = info.Pointer.ToString(), info.id, info.name, info.isOverride, info.volume, info.loopInTime, info.loopOutTime,
        info.offset, info.transitionTime, Source = Source(info.currentSource)
    };
    private static object? Clip(AudioClip? clip) => clip == null ? null : new {
        Pointer = clip.Pointer.ToString(), clip.name, LoadState = clip.loadState.ToString(), LoadType = clip.loadType.ToString(),
        clip.loadInBackground, clip.preloadAudioData, clip.frequency, clip.samples, clip.channels, clip.length
    };
    private static object? Source(AudioSource? source) => source == null ? null : new {
        Pointer = source.Pointer.ToString(), source.name, source.timeSamples, source.time, source.volume, source.pitch, source.loop,
        source.isPlaying, source.mute, Clip = Clip(source.clip)
    };
    private static void Record(Attempt attempt, string kind, object detail)
    {
        if (attempt.Events.Count >= 160) throw new InvalidOperationException("BGM diagnostic exceeded its bounded event count.");
        var native = Field(attempt.Host, "_native"); var raw = Field(attempt.Host, "_rawAudio");
        var clock = typeof(ExportHost).Assembly.GetType("AAVideoExport.Plugin.NativeExportClock", true)!;
        attempt.Events.Add(new {
            Index = attempt.Events.Count, Kind = kind, Thread = Environment.CurrentManagedThreadId, UnityFrame = Time.frameCount,
            SharedFrame = Field(clock, "_timelineFrame"), SharedSeconds = Field(clock, "_elapsedSeconds"), SharedNow = Field(clock, "_now"),
            CapturedFrames = Field(attempt.Host, "_captured"), Priming = Field(attempt.Host, "_strictPriming"), AwaitingStart = Field(attempt.Host, "_strictAwaitingStart"),
            NativeRawSampleFrames = Member(native, "AudioRawSampleFrames"), LastRawStartSample = Member(native, "LastAudioRawStartSample"),
            RawSpoolSampleFrames = Member(raw, "RawSampleFrames"), DspTime = AudioSettings.dspTime, Detail = detail
        });
    }
    private static object? Field(object value, string name) => value is Type type ? type.GetField(name, Members)?.GetValue(null) : value.GetType().GetField(name, Members)?.GetValue(value);
    private static object? Member(object? value, string name) => value == null ? null : value.GetType().GetProperty(name, Members)?.GetValue(value) ?? Field(value, name);
    private static void Guard(string phase, Action<Attempt> action)
    {
        var attempt = current; if (attempt == null) return;
        try
        {
            if (Environment.CurrentManagedThreadId != attempt.Thread) throw new InvalidOperationException("Original BGM observation reached a non-main thread; Unity getters were not read.");
            action(attempt);
        }
        catch (Exception error) { if (attempt.Errors.Count < 32) attempt.Errors.Add(phase + ": " + error.GetBaseException().Message); }
    }
    private sealed record MoveScope(AudioManager.BGMInfo? Previous, AudioManager.BGMInfo? Info, bool Retain);
    private sealed record MixerScope(int Call, int AuthoredCall);
    private sealed class Attempt(int index, ExportHost host, string directory, int thread)
    {
        internal readonly int Index = index, Thread = thread;
        internal readonly ExportHost Host = host;
        internal readonly string Directory = directory;
        internal readonly List<object> Events = new();
        internal readonly List<string> Errors = new();
        internal readonly Dictionary<IntPtr, AudioManager.BGMInfo> Infos = new();
        internal int Requests, Callbacks, InitialMoves, Plays, MixerCalls, AuthoredMixerCalls, MixerAfter, IteratorSamples;
        internal MixerScope? PendingMixer;
    }
}
