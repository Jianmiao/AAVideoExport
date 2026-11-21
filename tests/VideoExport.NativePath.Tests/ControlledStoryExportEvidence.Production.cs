using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AAVideoExport.NativePath.Tests;

internal static partial class ControlledStoryExportEvidence
{
    private static bool ProductionObservation;
    private static Type? originalVoiceRuntime;
    private static PropertyInfo? backgroundObserver;
    private static Delegate? previousBackgroundObserver, installedBackgroundObserver;
    private static int observationThread;

    private static void InstallProductionObservers()
    {
        ProductionObservation = true; observationThread = Environment.CurrentManagedThreadId;
        originalVoiceRuntime = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Rukari.CharacterVoice")
            .GetType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", true)!;
        Patch(voiceAdapter!, "BeginPassCore", after: nameof(AfterProductionVoiceBegin));
        Patch(voiceAdapter!, "CompletePass", before: nameof(BeforeProductionVoiceComplete), after: nameof(AfterProductionVoiceComplete));
        Patch(typeof(AAVideoExport.Core.RawAudioPass), "Append", after: nameof(AfterProductionRawAudioAppend));
        Patch(originalVoiceRuntime, "SetVoicePrefix", after: nameof(AfterOriginalVoiceRequest));
        Patch(originalVoiceRuntime, "ObserveOwnedPlayback", before: nameof(BeforeOriginalVoiceObserve), after: nameof(AfterOriginalVoiceObserve));
        Patch(originalVoiceRuntime, "TickOnMainThread", after: nameof(AfterOriginalVoiceTick));
        Patch(originalVoiceRuntime, "ResetOnMainThread", after: nameof(AfterOriginalVoiceReset));
        backgroundObserver = backgroundAdapter!.GetProperty("CommandObserved", Flags) ?? throw new MissingMemberException("The optional background observation sink is unavailable.");
        previousBackgroundObserver = backgroundObserver.GetValue(null) as Delegate;
        var own = typeof(ControlledStoryExportEvidence).GetMethod(nameof(ObserveProductionBackgroundCommand), Flags)!.CreateDelegate(backgroundObserver.PropertyType);
        installedBackgroundObserver = Delegate.Combine(previousBackgroundObserver, own);
        backgroundObserver.SetValue(null, installedBackgroundObserver);
    }
    private static void RestoreProductionObservers()
    {
        if (!ProductionObservation || backgroundObserver == null) return;
        Require(ReferenceEquals(backgroundObserver.GetValue(null), installedBackgroundObserver), "The test-owned background observation registration changed.");
        backgroundObserver.SetValue(null, previousBackgroundObserver);
        backgroundObserver = null; previousBackgroundObserver = installedBackgroundObserver = null;
    }
    private static void ObserveProductionBackgroundCommand(object command) => Guard("external-background-command", () =>
    {
        var pass = Current(); if (pass == null || pass.Sealed) return;
        pass.ExternalBackgroundCommands.Add(JsonValue(command));
    });
    private static void AfterProductionRawAudioAppend(object __instance, long __0, float[] __1, int __2, bool __runOriginal) => Guard("external-native-pcm", () =>
    {
        var pass = Current();
        if (!__runOriginal || pass == null || pass.Cancelled || !ReferenceEquals(Field(host!, "_rawAudio"), __instance)) return;
        pass.ExternalPcm ??= new NativePcmObservation(pass.Fps, Value<int>(__instance, "SampleRate"), Value<int>(__instance, "Channels"));
        Require(Value<long>(__instance, "LeadSampleFrames") == 0, "Native fixture PCM execution unexpectedly introduced a leading offset.");
        pass.ExternalPcm.Append(__0, __1, __2);
    });
    private static void AfterProductionVoiceBegin(object __instance, int __1, int __2) => Guard("external-voice-begin", () =>
    {
        Require(Environment.CurrentManagedThreadId == observationThread, "Native voice evidence arrived off the observed main thread.");
        var pass = Current(); if (pass == null || !ReferenceEquals(pass.VoiceController, __instance)) return;
        Require(pass.ExternalVoice == null, "Native voice pass began twice.");
        var assets = new List<NativeVoiceObservedAsset>();
        foreach (object entry in (IEnumerable)(Field(__instance, "pins") ?? throw new InvalidOperationException("Native preloaded voice pins are absent.")))
        {
            string identifier = Value<string>(entry, "Key"); object pin = Member(entry, "Value")!;
            Require(Member(pin, "Asset") == null, "Production voice pin contains an audit asset receipt.");
            var clip = (AudioClip)(Member(pin, "Clip") ?? throw new InvalidOperationException("Actual native voice clip is absent."));
            long frames = Convert.ToInt64(Member(pin, "SampleFrames")); int channels = Value<int>(pin, "Channels"), rate = Value<int>(pin, "SampleRate");
            Require(frames > 0 && channels > 0 && checked(frames * channels) <= 20000000, "Native test voice read exceeds its bounded sample count.");
            var native = new Il2CppStructArray<float>(checked(frames * channels));
            Require(clip.GetData(native, 0), "Independent native clip GetData failed.");
            float[] samples = native.ToArray(); byte[] bytes = new byte[checked(samples.Length * sizeof(float))]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            assets.Add(new(identifier, Hash(Value<string>(pin, "Path")), Convert.ToHexString(SHA256.HashData(bytes)), frames, channels, rate));
            pass.ExternalVoiceClips.Add(identifier, clip);
        }
        Require(assets.Count == 1 && assets[0].Identifier == expectedVoiceIdentifier && assets[0].FileSha256.Equals(expectedVoiceSha256, StringComparison.OrdinalIgnoreCase), "Observed native voice asset differs from the authored fixture.");
        pass.ExternalVoice = new NativeVoiceObservation(__1, __2, assets.ToArray());
    });
    private static object OriginalGate() => Member(originalVoiceRuntime!, "Gate") ?? throw new InvalidOperationException("Original native voice gate is absent.");
    private static long VoiceSequence() => Convert.ToInt64(Member(OriginalGate(), "RequestSequence"));
    private static int VoicePhase() => Convert.ToInt32(Member(OriginalGate(), "Phase"));
    private static NativeVoicePosition VoicePosition(Pass pass)
    {
        Require(Environment.CurrentManagedThreadId == observationThread, "Native voice lifecycle observation arrived off the main thread.");
        var callback = Field(pass.VoiceController, "position") as Delegate ?? throw new InvalidOperationException("Original voice execution position is unavailable.");
        return Position(callback.DynamicInvoke() ?? throw new InvalidOperationException("Original voice position returned no value."));
    }
    private static NativeVoicePosition Position(object value) => new(Value<long>(value, "Frame"), Value<long>(value, "SampleFrame"), Value<int>(value, "Row"));
    private static void AfterOriginalVoiceRequest(string __0, bool __runOriginal) => Guard("external-voice-request", () =>
    {
        var pass = Current(); if (pass?.ExternalVoice == null || !__runOriginal || __0?.StartsWith("aavt/voice/", StringComparison.Ordinal) != true) return;
        Require(Environment.CurrentManagedThreadId == observationThread, "Native voice request observation arrived off the main thread.");
        if (VoiceSequence() == pass.ExternalVoice.NativeSequence) { Require(Value<string>(OriginalGate(), "VoiceIdentifier") == __0, "Original native request identity changed."); return; }
        Require(Value<string>(OriginalGate(), "VoiceIdentifier") == __0 && VoicePhase() is 1 or 2, "Original runtime rejected the fixture voice request.");
        pass.ExternalVoice.Request(VoiceSequence(), __0, VoicePosition(pass), Convert.ToDouble(Member(OriginalGate(), "PostPlaybackDelaySeconds")));
    });
    private sealed record VoiceGateObservation(long Sequence, int Phase);
    private static void BeforeOriginalVoiceObserve(out VoiceGateObservation? __state)
    {
        __state = null;
        try { if (Current()?.ExternalVoice != null) __state = new(VoiceSequence(), VoicePhase()); }
        catch (Exception error) { Error("external-voice-observe-prefix", error); }
    }
    private static void AfterOriginalVoiceObserve(bool __0, VoiceGateObservation? __state, bool __runOriginal) => Guard("external-voice-observe", () =>
    {
        var pass = Current(); if (pass?.ExternalVoice == null || __state == null || !__runOriginal) return;
        Require(VoiceSequence() == __state.Sequence, "Native voice request changed within an observation call.");
        int phase = VoicePhase();
        if (__state.Phase == 2 && phase == 3 && __0)
        {
            var source = (AudioSource)(Field(pass.VoiceController, "voice") ?? throw new InvalidOperationException("Actual voice AudioSource is absent."));
            string identifier = Value<string>(OriginalGate(), "VoiceIdentifier");
            Require(pass.ExternalVoiceClips.TryGetValue(identifier, out var clip) && source.clip != null && source.clip.Pointer == clip.Pointer, "Original native voice started a different clip.");
            pass.ExternalVoice.Start(__state.Sequence, VoicePosition(pass), source.volume, source.pitch);
        }
        if (__state.Phase == 3 && phase is 7 or 4 && !__0) pass.ExternalVoice.End(__state.Sequence, VoicePosition(pass));
        ObserveProductionVoiceTerminal(pass);
    });
    private static void AfterOriginalVoiceTick(bool __runOriginal) => Guard("external-voice-tick", () =>
    { var pass = Current(); if (__runOriginal && pass?.ExternalVoice != null) ObserveProductionVoiceTerminal(pass); });
    private static void ObserveProductionVoiceTerminal(Pass pass)
    {
        var observer = pass.ExternalVoice!; if (!observer.HasOpenRequest) return;
        Require(VoiceSequence() == observer.NativeSequence && VoicePhase() is not (5 or 6), "Original voice failed, cancelled or changed while the fixture request was open.");
        if (VoicePhase() == 4) observer.DelayComplete(VoiceSequence(), VoicePosition(pass));
    }
    private static void AfterOriginalVoiceReset(string __0, bool __runOriginal) => Guard("external-voice-reset", () =>
    { var pass = Current(); if (__runOriginal && pass?.ExternalVoice?.HasOpenRequest == true && !pass.Cancelled) throw new InvalidOperationException("Original native voice reset an unfinished fixture request: " + __0); });
    private static void BeforeProductionVoiceComplete(object __instance, object __0, out NativeVoiceObservationSnapshot? __state)
    {
        __state = null;
        try { var pass = Current(); if (pass?.ExternalVoice != null && ReferenceEquals(pass.VoiceController, __instance)) __state = pass.ExternalVoice.Complete(Position(__0)); }
        catch (Exception error) { Error("external-voice-complete-prefix", error); }
    }
    private static void AfterProductionVoiceComplete(object __instance, NativeVoiceObservationSnapshot? __state, bool __runOriginal) => Guard("external-voice-complete", () =>
    {
        var pass = Current(); if (pass == null || !ReferenceEquals(pass.VoiceController, __instance)) return;
        Require(__runOriginal && __state != null && pass.VoiceObservation == null, "Actual production voice pass did not complete once.");
        var requests = __state!.Events.Where(e => e.Operation == "Request").ToArray();
        Require(requests.Length == expectedRequests && requests.Select(e => e.RequestOrdinal).SequenceEqual(Enumerable.Range(0, expectedRequests))
            && requests.Select(e => e.Position.Row).SequenceEqual(expectedVoiceRows) && requests.All(e => e.VoiceIdentifier == expectedVoiceIdentifier), "Observed voice requests differ from authored concrete rows.");
        foreach (var request in requests) Require(__state.Events.Where(e => e.RequestOrdinal == request.RequestOrdinal).Select(e => e.Operation)
            .SequenceEqual(new[] { "Request", "NativeStart", "NativeEnd", "DelayComplete" }), "External voice evidence lacks exactly-once native lifecycle transitions.");
        pass.VoiceRequests = requests.Length; pass.VoiceObservation = JsonValue(__state);
        Event(pass, "voice-observation-complete", new { requests = pass.VoiceRequests });
    });
}
