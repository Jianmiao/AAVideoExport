using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Integration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AAVideoExport.NativePath.Tests;

// Temporary probe. A generated interop wrapper pass alone is not native caller evidence.
public sealed class AudioHookChecks : MonoBehaviour
{
    private const string Owner = "halocue.aa.videoexport.nativeaudiohooks";
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Stopwatch _playbackWait = new();
    private Harmony? _harmony;
    private AudioManager? _manager;
    private AudioSource? _oldVoice, _probeSource;
    private AudioClip? _probeClip;
    private GameObject? _fixture;
    private IntPtr _managerGetterInfo;
    private bool _voiceReplaced, _finished;
    private int _stage, _waitUpdates, _passed, _failed;

    private static IntPtr ActiveSourcePointer, ActiveManagerPointer;
    private static bool OverrideSourcePlaying, SyntheticSourcePlaying, SyntheticManagerPlaying;
    private static int PlayCalls, PlayOneShotCalls, StopCalls, PauseCalls, UnPauseCalls;
    private static int IsPlayingCalls, TimeSamplesGetCalls, TimeSamplesSetCalls, ManagerGetterCalls;

    public AudioHookChecks(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        if (_finished) return;
        try
        {
            if (_stage == 0)
            {
                // The full partner profile can spend over a minute installing native patches.
                if (_wall.Elapsed.TotalSeconds > 120)
                    throw new TimeoutException("Native audio hook probe timed out waiting for CatalogScene.");
                if (!RenderControlV1.IsReady || SceneManager.GetActiveScene().name != "CatalogScene") return;
                PrepareBoundary();
                BeginPlaybackWait();
                _stage = 1;
                return;
            }
            if (_stage == 1)
            {
                if (!CheckPlaybackBaseline("unpatched")) return;
                Pass("audio-hooks-unpatched-playback-baseline");
                InstallSourceHooks();
                CheckCase("audio-source-operation-hooks", CheckAudioSourceOperations);
                BeginPlaybackWait();
                _stage = 2;
                return;
            }
            if (!CheckPlaybackBaseline("source-hooks-pass-through")) return;
            CheckCase("audio-manager-native-source-boundary", CheckAudioManagerSourceBoundary);
            CheckCase("audio-manager-native-getter-entry", CheckAudioManagerGetterEntry);
            Finish();
        }
        catch (Exception error)
        {
            Fail("audio-hooks-setup-or-baseline", error);
            Finish();
        }
    }

    [HideFromIl2Cpp]
    private void PrepareBoundary()
    {
        if (RenderControlV1.IsRenderingOwned)
            throw new InvalidOperationException("Cannot run the native audio probe during an export.");
        var managers = Resources.FindObjectsOfTypeAll<AudioManager>()
            .Where(manager => manager != null && manager.gameObject.scene.IsValid()).ToArray();
        if (managers.Length != 1)
            throw new InvalidOperationException($"Expected one existing AudioManager, observed {managers.Length}; native boundary is unproven.");
        _manager = managers[0];
        _oldVoice = _manager.voiceSource;
        if (_oldVoice != null && _oldVoice.isPlaying)
            throw new InvalidOperationException("Existing voiceSource is playing; native boundary cannot be tested without interrupting it.");
        var getter = typeof(AudioManager).GetProperty("isVoicePlaying", Any)!.GetGetMethod(true)!;
        var nativeInfoField = typeof(AudioManager).GetField("NativeMethodInfoPtr_get_isVoicePlaying_Public_get_Boolean_0", Any)
            ?? throw new MissingFieldException("Verified AudioManager native getter metadata field is missing.");
        _managerGetterInfo = (IntPtr)nativeInfoField.GetValue(null)!;
        if (_managerGetterInfo == IntPtr.Zero) throw new InvalidOperationException("AudioManager native getter metadata is null.");
        Plugin.Report("PATH-CHECK AUDIO-HOOK signature=" + getter.DeclaringType!.FullName + "." + getter);
        Plugin.Report("PATH-CHECK AUDIO-HOOK native-entry=verified-original-MethodInfo; source-boundary=unpatched-native-AudioManager-body-to-AudioSource");

        _fixture = new GameObject("AAVE temporary native audio hook probe");
        Object.DontDestroyOnLoad(_fixture);
        _probeClip = AudioClip.Create("AAVE temporary native audio hook clip", 4800, 1, 48000, false);
        if (_probeClip == null || !_probeClip.SetData(new Il2CppStructArray<float>(new float[4800]), 0))
            throw new InvalidOperationException("Temporary AudioClip creation or SetData failed.");
        _probeSource = _fixture.AddComponent<AudioSource>();
        _probeSource.playOnAwake = false;
        _probeSource.loop = true;
        _probeSource.clip = _probeClip;
        _probeSource.mute = true;
        _probeSource.ignoreListenerPause = true;
        _probeSource.Stop();
        _manager.voiceSource = _probeSource;
        _voiceReplaced = true;
        if (_manager.voiceSource == null || _manager.voiceSource.Pointer != _probeSource.Pointer)
            throw new InvalidOperationException("AudioManager voiceSource replacement did not round-trip.");
        bool sourceStopped = _probeSource.isPlaying;
        bool managerStopped = _manager.isVoicePlaying;
        bool nativeManagerStopped = InvokeNativeManagerGetter();
        Plugin.Report($"PATH-CHECK AUDIO-HOOK unpatched-stopped source={sourceStopped}; manager={managerStopped}; native_manager={nativeManagerStopped}");
        if (sourceStopped || managerStopped || nativeManagerStopped)
            throw new InvalidOperationException("Stopped temporary source does not yield a false native baseline.");
    }

    [HideFromIl2Cpp]
    private void BeginPlaybackWait()
    {
        OverrideSourcePlaying = false;
        _probeSource!.Play();
        _waitUpdates = 0;
        _playbackWait.Restart();
    }

    [HideFromIl2Cpp]
    private bool CheckPlaybackBaseline(string phase)
    {
        if (++_waitUpdates < 2) return false;
        bool source = _probeSource!.isPlaying;
        bool manager = _manager!.isVoicePlaying;
        bool nativeManager = InvokeNativeManagerGetter();
        bool ready = source && manager && nativeManager;
        bool timedOut = _playbackWait.Elapsed.TotalSeconds > 10;
        if (ready || timedOut)
            Plugin.Report($"PATH-CHECK AUDIO-HOOK baseline phase={phase}; updates={_waitUpdates}; source={source}; manager={manager}; native_manager={nativeManager}; source_prefix_calls={IsPlayingCalls}");
        if (timedOut && !ready)
            throw new InvalidOperationException("Actual temporary playback was not established: " + phase + "; native seam is unproven.");
        return ready;
    }

    [HideFromIl2Cpp]
    private void InstallSourceHooks()
    {
        ActiveSourcePointer = _probeSource!.Pointer;
        ActiveManagerPointer = _manager!.Pointer;
        _harmony = new Harmony(Owner);
        PatchSource(nameof(AudioSource.Play), Type.EmptyTypes, nameof(PlayPrefix));
        PatchSource(nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip) }, nameof(PlayOneShotPrefix));
        PatchSource(nameof(AudioSource.Stop), Type.EmptyTypes, nameof(StopPrefix));
        PatchSource(nameof(AudioSource.Pause), Type.EmptyTypes, nameof(PausePrefix));
        PatchSource(nameof(AudioSource.UnPause), Type.EmptyTypes, nameof(UnPausePrefix));
        Patch(typeof(AudioSource).GetProperty(nameof(AudioSource.isPlaying))!.GetGetMethod(true)!, nameof(IsPlayingPrefix));
        Patch(typeof(AudioSource).GetProperty(nameof(AudioSource.timeSamples))!.GetGetMethod(true)!, nameof(TimeSamplesGetPrefix));
        Patch(typeof(AudioSource).GetProperty(nameof(AudioSource.timeSamples))!.GetSetMethod(true)!, nameof(TimeSamplesSetPrefix));
    }

    [HideFromIl2Cpp]
    private void PatchSource(string name, Type[] arguments, string prefix) =>
        Patch(typeof(AudioSource).GetMethod(name, Any, null, arguments, null)
            ?? throw new MissingMethodException(typeof(AudioSource).FullName, name), prefix);

    [HideFromIl2Cpp]
    private void Patch(MethodInfo method, string prefix)
    {
        Plugin.Report("PATH-CHECK AUDIO-HOOK patch=" + method.DeclaringType!.FullName + "." + method);
        _harmony!.Patch(method, prefix: new HarmonyMethod(typeof(AudioHookChecks), prefix));
    }

    [HideFromIl2Cpp]
    private void CheckAudioSourceOperations()
    {
        PlayCalls = PlayOneShotCalls = StopCalls = PauseCalls = UnPauseCalls = 0;
        IsPlayingCalls = TimeSamplesGetCalls = TimeSamplesSetCalls = 0;
        OverrideSourcePlaying = false;
        _probeSource!.Play();
        _ = _probeSource.isPlaying;
        _probeSource.timeSamples = 0;
        _ = _probeSource.timeSamples;
        _probeSource.Pause();
        _probeSource.UnPause();
        _probeSource.PlayOneShot(_probeClip!);
        _probeSource.Stop();
        string counts = $"play={PlayCalls}; oneshot={PlayOneShotCalls}; stop={StopCalls}; pause={PauseCalls}; unpause={UnPauseCalls}; isplaying={IsPlayingCalls}; time_get={TimeSamplesGetCalls}; time_set={TimeSamplesSetCalls}";
        Plugin.Report("PATH-CHECK AUDIO-HOOK managed-counters " + counts);
        if (PlayCalls < 1 || PlayOneShotCalls < 1 || StopCalls < 1 || PauseCalls < 1 || UnPauseCalls < 1
            || IsPlayingCalls < 1 || TimeSamplesGetCalls < 1 || TimeSamplesSetCalls < 1)
            throw new InvalidOperationException("Managed operation counters are incomplete: " + counts);
    }

    [HideFromIl2Cpp]
    private void CheckAudioManagerSourceBoundary()
    {
        var observations = new List<(bool Expected, bool Source, bool Manager, bool Native, int DirectCalls, int ManagerCalls, int NativeCalls)>();
        OverrideSourcePlaying = true;
        try
        {
            // No AudioManager patch is installed here. Any source-prefix hit
            // from its body is evidence of a native caller entering the detour.
            foreach (bool expected in new[] { false, true, false })
            {
                SyntheticSourcePlaying = expected;
                int before = IsPlayingCalls;
                bool source = _probeSource!.isPlaying;
                int directCalls = IsPlayingCalls - before;
                before = IsPlayingCalls;
                bool manager = _manager!.isVoicePlaying;
                int managerCalls = IsPlayingCalls - before;
                before = IsPlayingCalls;
                bool native = InvokeNativeManagerGetter();
                int nativeCalls = IsPlayingCalls - before;
                observations.Add((expected, source, manager, native, directCalls, managerCalls, nativeCalls));
                Plugin.Report($"PATH-CHECK AUDIO-HOOK source-sentinel expected={expected}; direct={source}; manager={manager}; native_manager={native}; direct_calls={directCalls}; manager_calls={managerCalls}; native_manager_calls={nativeCalls}");
            }
        }
        finally { OverrideSourcePlaying = false; }
        if (observations.Any(row => row.Source != row.Expected || row.DirectCalls < 1))
            throw new InvalidOperationException("Direct AudioSource sentinel transitions were not observed.");
        if (observations.Any(row => row.Manager != row.Expected || row.Native != row.Expected || row.ManagerCalls < 1 || row.NativeCalls < 1))
            throw new InvalidOperationException("Native AudioManager did not consistently enter and honor the AudioSource detour; this seam is unsupported or unproven.");
        Plugin.Report("PATH-CHECK AUDIO-HOOK native-source-detour-supported=true; caller=unpatched-AA-AudioManager; transitions=false,true,false");
    }

    [HideFromIl2Cpp]
    private void CheckAudioManagerGetterEntry()
    {
        OverrideSourcePlaying = false;
        Patch(typeof(AudioManager).GetProperty("isVoicePlaying", Any)!.GetGetMethod(true)!, nameof(AudioManagerPlayingPrefix));
        var observations = new List<(bool Expected, bool Direct, bool Native, int DirectCalls, int NativeCalls)>();
        foreach (bool expected in new[] { false, true, false })
        {
            SyntheticManagerPlaying = expected;
            int before = ManagerGetterCalls;
            bool direct = _manager!.isVoicePlaying;
            int directCalls = ManagerGetterCalls - before;
            before = ManagerGetterCalls;
            bool native = InvokeNativeManagerGetter();
            int nativeCalls = ManagerGetterCalls - before;
            observations.Add((expected, direct, native, directCalls, nativeCalls));
            Plugin.Report($"PATH-CHECK AUDIO-HOOK manager-sentinel expected={expected}; direct={direct}; native_entry={native}; direct_calls={directCalls}; native_calls={nativeCalls}");
        }
        if (observations.Any(row => row.Direct != row.Expected || row.Native != row.Expected || row.DirectCalls < 1 || row.NativeCalls < 1))
            throw new InvalidOperationException("Original native AudioManager metadata entry did not honor getter sentinel transitions; native manager seam is unproven.");
        Plugin.Report("PATH-CHECK AUDIO-HOOK native-manager-entry-supported=true; entry=original-MethodInfo-runtime-invoke; transitions=false,true,false; story-dispatch-not-exercised=true");
    }

    [HideFromIl2Cpp]
    private unsafe bool InvokeNativeManagerGetter()
    {
        // This exact metadata field and runtime_invoke signature were verified
        // against AA 1.0.1 interop IL. Capture it before installing any test hook.
        IntPtr exception = IntPtr.Zero;
        IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(_managerGetterInfo, _manager!.Pointer, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        if (boxed == IntPtr.Zero) throw new InvalidOperationException("Native AudioManager getter returned no boxed boolean.");
        return *(bool*)IL2CPP.il2cpp_object_unbox(boxed);
    }

    [HideFromIl2Cpp]
    private static bool IsTarget(AudioSource source) =>
        ActiveSourcePointer != IntPtr.Zero && source != null && source.Pointer == ActiveSourcePointer;

    [HideFromIl2Cpp]
    private static void PlayPrefix(AudioSource __instance) { if (IsTarget(__instance)) PlayCalls++; }
    [HideFromIl2Cpp]
    private static void PlayOneShotPrefix(AudioSource __instance) { if (IsTarget(__instance)) PlayOneShotCalls++; }
    [HideFromIl2Cpp]
    private static void StopPrefix(AudioSource __instance) { if (IsTarget(__instance)) StopCalls++; }
    [HideFromIl2Cpp]
    private static void PausePrefix(AudioSource __instance) { if (IsTarget(__instance)) PauseCalls++; }
    [HideFromIl2Cpp]
    private static void UnPausePrefix(AudioSource __instance) { if (IsTarget(__instance)) UnPauseCalls++; }
    [HideFromIl2Cpp]
    private static bool IsPlayingPrefix(AudioSource __instance, ref bool __result)
    {
        if (!IsTarget(__instance)) return true;
        IsPlayingCalls++;
        if (!OverrideSourcePlaying) return true;
        __result = SyntheticSourcePlaying;
        return false;
    }
    [HideFromIl2Cpp]
    private static void TimeSamplesGetPrefix(AudioSource __instance) { if (IsTarget(__instance)) TimeSamplesGetCalls++; }
    [HideFromIl2Cpp]
    private static void TimeSamplesSetPrefix(AudioSource __instance) { if (IsTarget(__instance)) TimeSamplesSetCalls++; }
    [HideFromIl2Cpp]
    private static bool AudioManagerPlayingPrefix(AudioManager __instance, ref bool __result)
    {
        if (ActiveManagerPointer == IntPtr.Zero || __instance == null || __instance.Pointer != ActiveManagerPointer
            || __instance.voiceSource == null || __instance.voiceSource.Pointer != ActiveSourcePointer) return true;
        ManagerGetterCalls++;
        __result = SyntheticManagerPlaying;
        return false;
    }

    [HideFromIl2Cpp]
    private void CheckCase(string name, Action check)
    {
        try { check(); Pass(name); }
        catch (Exception error) { Fail(name, error); }
    }

    [HideFromIl2Cpp]
    private void Pass(string name) { _passed++; Plugin.Report("PATH-CHECK PASS " + name); }

    [HideFromIl2Cpp]
    private void Fail(string name, Exception error) { _failed++; Plugin.Report("PATH-CHECK FAIL " + name + ": " + Describe(error)); }

    [HideFromIl2Cpp]
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            OverrideSourcePlaying = false;
            ActiveSourcePointer = ActiveManagerPointer = IntPtr.Zero;
            CheckCase("audio-hooks-unpatch", () =>
            {
                _harmony?.UnpatchSelf();
                if (Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true))
                    throw new InvalidOperationException("Temporary audio hook owner remains installed.");
            });
            if (_voiceReplaced)
                CheckCase("audio-hooks-restore-voice-source", () =>
                {
                    _manager!.voiceSource = _oldVoice;
                    if ((_manager.voiceSource?.Pointer ?? IntPtr.Zero) != (_oldVoice?.Pointer ?? IntPtr.Zero))
                        throw new InvalidOperationException("Original AudioManager voiceSource was not restored.");
                });
            if (_probeSource != null) CheckCase("audio-hooks-stop-fixture", () => _probeSource.Stop());
            if (_fixture != null) CheckCase("audio-hooks-destroy-fixture", () => Object.Destroy(_fixture));
            if (_probeClip != null) CheckCase("audio-hooks-destroy-clip", () => Object.Destroy(_probeClip));
        }
        finally { Plugin.Report($"PATH-CHECK RESULT {_passed} passed; {_failed} failed"); }
    }

    [HideFromIl2Cpp]
    private static string Describe(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null) error = invocation.InnerException;
        return error.GetType().Name + ": " + error.Message.Replace("\r", " ").Replace("\n", " ");
    }
}
