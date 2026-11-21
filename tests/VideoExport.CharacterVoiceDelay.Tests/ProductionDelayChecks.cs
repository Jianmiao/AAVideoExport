using System.Diagnostics;
using System.Reflection;
using AAVideoExport.Plugin;
using HarmonyLib;
using Rukari.CharacterVoice.Core;

internal static class ProductionDelayChecks
{
    internal static int Run()
    {
        var api = new AdapterApi(); var traps = new Harmony("aave.production.voice-audit-traps");
        try
        {
            foreach (string name in new[] { "ValidateAssembly", "EnsureNoConflicts" })
                traps.Patch(typeof(CharacterVoiceDelayClock).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!, prefix: new HarmonyMethod(typeof(ProductionDelayChecks).GetMethod(nameof(Audit))!));
            FakeRuntime.Reset(); api.Initialize(new Harmony("aave.production.voice-clock"), _ => { }, error => throw error); api.Bind(typeof(FakeRuntime));
            Check(api.Ready, "Functional delay adapter did not install.");
            var gate = FakeRuntime.Value; long frame = 0; api.BeginFrames(() => frame, 60);
            long sequence = gate.Begin("aavt/voice/production", 100, 10, .1);
            Check(gate.MarkStarting(sequence, 100, 10) && gate.ObservePlayback(sequence, true, 101, 300), "Original native voice gate did not start.");
            frame = 1; gate.ObservePlayback(sequence, false, 102, 300);
            frame = 6; Check(!gate.Expire(99999) && gate.Active, "Authored frame delay ended too soon.");
            frame = 7; Check(!gate.Expire(99999) && gate.Phase == VoicePlaybackPhase.Completed, "Original gate did not finish at its exact due frame.");
            api.ValidateBinding(); api.End();
            sequence = gate.Begin("aavt/voice/wall", 200, 10, .1); gate.MarkStarting(sequence, 200, 10); gate.ObservePlayback(sequence, true, 201, 300); gate.ObservePlayback(sequence, false, 202, 300);
            Check(!gate.Expire(202.05) && gate.Active, "Normal wall delay changed after release.");
            gate.Expire(202.1); Check(gate.Phase == VoicePlaybackPhase.Completed, "Normal wall delay did not restore.");
            Check(api.Shutdown(), "Clock cancellation/restoration cleanup failed.");
            Console.WriteLine("PASS production native gate start/frame-delay/end/wall-restore with assembly and patch audit traps");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL production delay: " + error.GetBaseException().Message); return 1; }
        finally { api.Shutdown(); traps.UnpatchSelf(); }
    }
    public static void Audit() => throw new InvalidOperationException("VOICE AUDIT ENTRY REACHED IN PRODUCTION");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
