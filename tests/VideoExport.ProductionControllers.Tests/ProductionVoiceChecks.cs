using System.Reflection;
using System.Reflection.Emit;
using AAVideoExport.Plugin;
using HarmonyLib;

internal static class ProductionVoiceChecks
{
    internal static void Run()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Rukari.CharacterVoice") { Version = new Version(1, 6, 8, 0) }, AssemblyBuilderAccess.Run);
        var builder = assembly.DefineDynamicModule("fixture").DefineType("Rukari.CharacterVoice.Runtime.VoicePlaybackRuntime", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        builder.DefineField("Gate", typeof(ProductionVoiceGate), FieldAttributes.Public | FieldAttributes.Static);
        foreach (string field in new[] { "_installed", "_pollingSuspended", "_usesNativeStartedCallback", "_ownerPreview" }) builder.DefineField(field, typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
        foreach (string field in new[] { "_mainThreadId", "_ownerPlayerInstanceId" }) builder.DefineField(field, typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        var runtime = builder.CreateType()!; runtime.GetField("Gate")!.SetValue(null, new ProductionVoiceGate());
        runtime.GetField("_installed")!.SetValue(null, true); runtime.GetField("_usesNativeStartedCallback")!.SetValue(null, true);
        runtime.GetField("_mainThreadId")!.SetValue(null, Environment.CurrentManagedThreadId);
        var traps = new Harmony("aave.production.voice-controller-traps");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        foreach (string method in new[] { "HashFile", "InspectClip", "ValidateHook", "AddTarget", "SettingsIdentity", "ValidateManagedCommitProof" })
            traps.Patch(typeof(CharacterVoiceSessionAdapter).GetMethod(method, flags)!, prefix: new HarmonyMethod(typeof(Traps).GetMethod(nameof(Traps.Audit))!));
        // Delay timing itself is tested with the actual installed pure Gate in
        // ProductionDelayChecks. This fixture supplies only its ready binding.
        traps.Patch(typeof(CharacterVoiceDelayClock).GetMethod("ValidateBinding", flags)!, prefix: new HarmonyMethod(typeof(ProductionVoiceChecks).GetMethod(nameof(ClockReady))!));
        string path = Path.GetTempFileName();
        try
        {
            const string id = "aavt/voice/test"; var clip = new UnityEngine.AudioClip();
            ScenarioResourceManager.Instance = new(); ScenarioResourceManager.Instance.Paths[id] = path; ScenarioResourceManager.Instance._loadedCustomVoices[id] = clip;
            AudioManager.Instance = new(); AudioManager.Instance.voiceSource.clip = clip; Test.Instance = new();
            using var session = CharacterVoiceSessionAdapter.Attach(error => throw error, _ => { });
            session.BindStory(@"E:\actual-selected-story.aas", new[] { id }); session.PreparePass();
            if (!session.PumpPreload()) throw new Exception("Ready native clip did not complete preload.");
            session.BeginPass(Test.Instance, 60, 48000, () => new CharacterVoicePosition(0, 0, 0)); session.ValidateFrame();
            var complete = typeof(CharacterVoiceSessionAdapter).GetMethod("CompletePass", flags) ?? throw new Exception("Functional voice completion API is missing.");
            complete.Invoke(session, new object[] { new CharacterVoicePosition(1, 800, 1) }); session.EndPass();
            session.PreparePass(); if (!session.PumpPreload()) throw new Exception("Replay preload did not reuse actual ready resources.");
            session.BeginPass(Test.Instance, 60, 48000, () => new CharacterVoicePosition(0, 0, 0)); session.EndPass();
            if (session.IsArmed) throw new Exception("Cancellation left the functional voice pass armed.");
            if (Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains("halocue.aa.videoexport.character-voice-session") == true))
                throw new Exception("Production installed audit-only native voice event observers.");
        }
        finally { traps.UnpatchSelf(); File.Delete(path); ScenarioResourceManager.Instance = null; AudioManager.Instance = null; Test.Instance = null; }
    }
    public static bool ClockReady() => false;
}
