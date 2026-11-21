using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;

internal static class ProductionHostRegression
{
    internal static int Run()
    {
        int passed=0,failed=0;
        void Test(string name,Action action)
        {
            MoreEffectsCompatibility.ResetAdmission();StrictCapabilityInventory.Reset();StaticInertModEvidence.Reset();AASkipIntroExportBoundary.Reset();
            CharacterVoiceDelayClock.Reset();CharacterVoiceSessionAdapter.Reset();RetainedMenuPlaybackBoundary.Reset();RuntimeAttributionClient.Reset();
            FrameworkGeneratedAssemblyScope.Reset();MultilingualCaptureAdapter.Reset();RuntimeModBoundaries.EndExport();
            NativeBgmSessionAdapter.Reset();
            ProductionAuditTrap.Calls.Clear();ProductionAuditTrap.Enabled=true;
            try{action();Check(ProductionAuditTrap.Calls.Count==0,"Audit trap was reached despite caught failure.");passed++;Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message);}
            finally{ProductionAuditTrap.Enabled=false;}
        }
        Test("production prepares and replays the real PCM/count plan without audit objects or receipts",()=>
        {
            using var f=new StrictHostRegression.Fixture();
            NoAudits(f);f.Prime();f.Frame(1600);NoAudits(f);
            f.FinishPreparationAndStartReplay();NoAudits(f);f.Prime();f.Frame(1600);
            f.Host.BeforePlayerEnd(f.Player);f.Host.Update();
            f.Get<Task<ExportResult>?>("_finalize")?.GetAwaiter().GetResult();f.Host.Update();
            Check(f.Session.Completed,"Production movie did not complete: "+string.Join(" | ",UnityEngine.Debug.Messages.TakeLast(5)));
            Check(f.Session.RuntimeFactoryRecipe==null,"Production fabricated a runtime recipe receipt.");
            Check(!Directory.Exists(Plugin.TimelineReceiptDirectory),"Production wrote an audit baseline/preflight directory.");
            Check(FrameworkGeneratedAssemblyScope.Acquisitions==0&&RuntimeAttributionClient.Acquisitions==0&&StaticInertModEvidence.Creations==0,"Production acquired audit scopes.");
        });
        CommonCases(Test);
        Test("production waits for the real effects execution boundary without acquiring its audit proof",()=>
        {
            RuntimeModBoundaries.SourcePreparationRequired=true;int tries=0;
            RuntimeModBoundaries.BeforeSourcePreparation=()=>{if(++tries==1)throw new ExportException("strict_effects_source_pending","Native index is still loading.");};
            try
            {
                using var f=new StrictHostRegression.Fixture(begin:false);f.StartStrict();
                Check(!f.Host.Capturing&&f.Player.AdvanceCalls==0,"Native playback advanced before its effects were ready.");
                f.Host.Update();Check(f.Host.Capturing&&RuntimeModBoundaries.SourceInput!.Identity.StartsWith("execution:",StringComparison.Ordinal),"Production effects readiness did not resume with an execution lease.");
                f.Prime();f.Frame(1600);NoAudits(f);
            }
            finally{RuntimeModBoundaries.BeforeSourcePreparation=null;RuntimeModBoundaries.SourcePreparationRequired=false;RuntimeModBoundaries.SourcePreparations=RuntimeModBoundaries.SourceRevalidations=0;}
        });
        Test("production uses prepared PCM when replay follows the same execution schedule without a PCM audit",()=>
        {
            using var f=new StrictHostRegression.Fixture();f.Prime();
            float[] content=Enumerable.Repeat(0.125f,3200).ToArray();f.FrameSamples(content);f.FinishPreparationAndStartReplay();f.Prime();
            f.FrameSamples(Enumerable.Repeat(0.25f,3200).ToArray());
            f.Host.BeforePlayerEnd(f.Player);f.Host.Update();f.Get<Task<ExportResult>?>("_finalize")?.GetAwaiter().GetResult();f.Host.Update();
            Check(f.Session.Completed&&f.Session.WrittenAudio.SequenceEqual(content),"Execution lost the prepared PCM or performed a replay audit.");NoAudits(f);
        });
        Test("same frame count cannot pair prepared audio with a different native story position",()=>
        {
            using var f=new StrictHostRegression.Fixture();f.PrepareOneFrameAndStartReplay();f.Prime();f.Player.cur=7;f.Frame(1600);
            Check(!f.Session.Completed&&UnityEngine.Debug.Messages.Any(message=>message.Contains("playback_schedule_mismatch")),"Production accepted video from a different story position for the prepared audio.");
        });
        Test("production cancellation restores execution during both preparation and replay without audits",()=>
        {
            foreach(bool replay in new[]{false,true})
            {
                using var f=new StrictHostRegression.Fixture();
                if(replay)f.PrepareOneFrameAndStartReplay();
                f.Prime();f.Frame(1600);f.Call("Cancel");f.Cleanup();
                Check(!f.Host.Capturing&&!f.Session.Completed&&!RenderControlV1.IsRenderingOwned,"Cancelled execution retained capture or rendering ownership.");
                Check(!Directory.Exists(Plugin.TimelineReceiptDirectory),"Cancellation wrote an audit baseline.");NoAudits(f);
                Check(f.Get<PreparedFrameSchedule?>("_preparedFrameSchedule")==null,"Cancellation retained the native execution schedule.");
            }
        });
        Test("production still rejects a replay ending outside its prepared frame count",()=>
        {
            using var f=new StrictHostRegression.Fixture();f.Prime();f.Frame(1600);f.Frame(1600);f.FinishPreparationAndStartReplay();f.Prime();f.Frame(1600);
            f.Host.BeforePlayerEnd(f.Player);f.Host.Update();
            Check(!f.Session.Completed&&UnityEngine.Debug.Messages.Any(message=>message.Contains("video_timeline_mismatch")),"Wrong output frame count was accepted: "+string.Join(" | ",UnityEngine.Debug.Messages.TakeLast(4)));
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; production execution with throwing audit boundaries");return failed==0?0:1;
    }
    internal static void CommonCases(Action<string,Action> test)
    {
        test("normal production Prepare does not classify unknown loaded modules or acquire provenance scopes",()=>
        {
            using var f=new StrictHostRegression.Fixture(begin:false);f.Session.Dispose();f.Set("_session",null);
            StrictCapabilityInventory.Snapshot=StrictCapabilitySnapshot.Create(new[]{new StrictCapability("Unknown.Mod",new string('a',64),StrictCapabilityLevel.Opaque,Array.Empty<string>())});
            f.Call("Prepare",f.Get<NativeExportPanel>("_panel").Options);
            var work=f.Get<Task<ExportSession>?>("_prepare");
            Check(work!=null,"Production Prepare did not reach encoder preparation: "+string.Join(" | ",UnityEngine.Debug.Messages.TakeLast(5)));
            work!.GetAwaiter().GetResult();NoAudits(f);f.Call("Cancel");f.Cleanup();
        });
        test("production retains the established deterministic scenario-content RNG seed without an audit identity",()=>
        {
            using var f=new StrictHostRegression.Fixture(begin:false);
            f.Call("PrepareStrictIdentity",f.Story);Check(f.Get<int>("_strictSeed")==1046337979,"Original seed for known scenario bytes changed.");
            f.Call("PrepareStrictIdentity",f.Story);Check(f.Get<int>("_strictSeed")==1046337979,"Same scenario bytes produced a different execution seed.");
            File.WriteAllText(f.Story,"changed frozen story");f.Call("PrepareStrictIdentity",f.Story);
            Check(f.Get<int>("_strictSeed")==684193391,"Changed scenario content did not select the established new seed.");
            Check(f.Get<string>("_strictStoryHash")==""&&f.Get<string>("_strictModFingerprint")=="","Execution seed created an audit fingerprint.");NoAudits(f);
        });
        test("production applies its RNG seed at authored frame zero and restores native RNG after cancellation",()=>
        {
            var previous=UnityEngine.Random.state;
            try
            {
                using var f=new StrictHostRegression.Fixture(begin:false);
                UnityEngine.Random.state=new UnityEngine.Random.State { Seed=31337 };
                f.StartStrict();
                Check(UnityEngine.Random.state.Seed==31337,"Mixer priming changed native RNG before authored playback.");
                f.Prime();
                Check(UnityEngine.Random.state.Seed==1046337979,"Authored frame zero did not apply the scenario-content RNG seed.");
                f.Frame(1600);f.Call("Cancel");f.Cleanup();
                Check(UnityEngine.Random.state.Seed==31337,"Cancellation did not restore the RNG state preceding authored playback.");
                NoAudits(f);
            }
            finally { UnityEngine.Random.state=previous; }
        });
    }
    internal static void NoAudits(StrictHostRegression.Fixture f)
    {
        foreach(string field in new[]{"_staticInertScope","_frameworkScope","_recipeScope","_strictCapabilitySnapshot","_strictLedger","_strictSandboxTrace","_repeatBaseline","_strictPreflightReceipt"})
            Check(f.Get<object?>(field)==null,"Production created "+field);
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
