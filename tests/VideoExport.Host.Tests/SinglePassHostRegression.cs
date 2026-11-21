using AAVideoExport.Core;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;

internal static class SinglePassHostRegression
{
    internal static int Run()
    {
        int passed=0, failed=0;
        void Test(string name, Action action)
        {
            MoreEffectsCompatibility.ResetAdmission(); StrictCapabilityInventory.Reset(); StaticInertModEvidence.Reset(); AASkipIntroExportBoundary.Reset();
            CharacterVoiceDelayClock.Reset(); CharacterVoiceSessionAdapter.Reset(); RetainedMenuPlaybackBoundary.Reset(); RuntimeAttributionClient.Reset();
            FrameworkGeneratedAssemblyScope.Reset(); MultilingualCaptureAdapter.Reset(); RuntimeModBoundaries.EndExport();
            NativeBgmSessionAdapter.Reset();
            ProductionAuditTrap.Calls.Clear(); ProductionAuditTrap.Enabled=true;
            try { action(); Check(ProductionAuditTrap.Calls.Count==0,"Production reached an audit boundary."); passed++; Console.WriteLine("PASS "+name); }
            catch(Exception error) { failed++; Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message); }
            finally { ProductionAuditTrap.Enabled=false; }
        }

        ProductionHostRegression.CommonCases(Test);

        Test("single pass submits video during its only authored playback",()=>
        {
            using var f=new StrictHostRegression.Fixture(); f.Prime(); f.Frame(1600);
            Check(f.Session.FramesWritten==1,"First authored frame was treated as an audio-only preparation.");
            NoReplayState(f); Finish(f);
            Check(f.Session.Completed && f.Session.FramesWritten==1 && f.Session.AudioSampleFrames==1600,"Single authored pass did not finalize exact audio/video.");
            Check(f.Player.AdvanceCalls==1,"Completion advanced or reloaded the story again.");
            Check(!RenderControlV1.IsRenderingOwned,"Completion retained render ownership.");
        });
        Test("single pass preserves real mixer samples across callback boundaries and trims only tail",()=>
        {
            using var f=new StrictHostRegression.Fixture(); f.Prime();
            f.FrameSamples(Enumerable.Repeat(0.125f,3000).ToArray());
            f.FrameSamples(Enumerable.Repeat(-0.25f,3500).ToArray()); Finish(f);
            Check(f.Session.Completed && f.Session.FramesWritten==2 && f.Session.AudioSampleFrames==3200,"Actual PCM was not sealed to the two-frame timeline.");
            Check(f.Session.WrittenAudio.Count==6400 && f.Session.WrittenAudio.Take(3000).All(x=>x==0.125f)
                && f.Session.WrittenAudio.Skip(3000).All(x=>x==-0.25f),"PCM was padded, dropped, reordered or replaced by a prepared replay.");
            Check(!Directory.EnumerateFiles(f.Output,"*.pcm").Any(),"Completed single pass retained raw PCM spool.");
        });
        Test("single pass drains real audio after a zero-length content callback without extra video",()=>
        {
            using var f=new StrictHostRegression.Fixture(); f.Prime(); f.Frame(0);
            NativeCaptureScope.AudioBlocks.Enqueue(Enumerable.Repeat(0.375f,3200).ToArray()); Finish(f);
            Check(f.Session.Completed && f.Session.FramesWritten==1 && f.Session.WrittenAudio.Count==3200
                && f.Session.WrittenAudio.All(x=>x==0.375f),"Native drain invented silence, advanced video or lost its actual samples.");
        });
        Test("audio tail updates continue consuming expiring native video readbacks",()=>
        {
            using var f=new StrictHostRegression.Fixture(); f.Prime(); f.Frame(0);
            var frames=f.Get<FramePipeline>("_frames");int before=frames.DrainCalls;
            NativeCaptureScope.AudioSampleCounts.Enqueue(0);f.Host.BeforePlayerEnd(f.Player);f.Host.Update();
            Check(f.Get<bool>("_strictDraining"),"Fixture did not reach a pending native audio tail.");
            Check(frames.DrainCalls>before,"Audio drain skipped the current Unity frame's pending video readbacks.");
            f.Call("Cancel");f.Cleanup();
        });
        Test("completion keeps the shared clock held through native restoration",()=>
        {
            using var f=new StrictHostRegression.Fixture();f.Prime();f.Frame(1600);Finish(f);
            Check(NativeExportClock.AdvancingAtRestore==false,"Completion allowed the captured count to publish an unrendered tick during restoration.");
        });
        Test("single pass cancellation restores ownership during priming capture and drain",()=>
        {
            foreach(int stage in new[]{0,1,2})
            {
                using var f=new StrictHostRegression.Fixture();
                if(stage>0){f.Prime();f.Frame(stage==2?0:1600);}
                if(stage==2){NativeCaptureScope.AudioSampleCounts.Enqueue(0);f.Host.BeforePlayerEnd(f.Player);f.Host.Update();}
                f.Call("Cancel"); f.Cleanup();
                Check(!f.Host.Capturing && f.Session.Cancelled && !f.Session.Completed && !RenderControlV1.IsRenderingOwned,"Cancellation retained a capture session at stage "+stage);
                Check(NativeExportClock.AdvancingAtRestore==false,"Cancellation released the shared-clock hold before restoration at stage "+stage);
                Check(!Directory.EnumerateFiles(f.Output,"*.pcm").Any(),"Cancellation leaked raw PCM at stage "+stage);
                NoReplayState(f);
            }
        });
        Test("single pass arms and completes the actual voice controller once",()=>
        {
            CharacterVoiceSessionAdapter.Loaded=true;
            using var f=new StrictHostRegression.Fixture();f.Prime();f.Frame(1600);Finish(f);
            Check(f.Session.Completed && CharacterVoiceSessionAdapter.Arms==1 && CharacterVoiceSessionAdapter.Completions==1,"Voice lifecycle was repeated or never completed.");
            Check(CharacterVoiceDelayClock.Begins==1 && CharacterVoiceDelayClock.Ends==1,"Voice clock was reacquired for another story pass.");
        });
        Test("BGM resources finish before frame zero even after the native frozen table is released",()=>
        {
            NativeBgmSessionAdapter.Ready=false;
            using var f=new StrictHostRegression.Fixture();
            Check(!f.Host.Capturing && f.Player.AdvanceCalls==0,"Authored frame zero started before native BGM was loaded.");
            Check(NativeBgmSessionAdapter.Preparations==1,"Native BGM preparation did not retain the selected frozen story.");
            f.Set("_frozenSave",null);NativeBgmSessionAdapter.Ready=true;f.Host.Update();f.Prime();
            Check(NativeBgmSessionAdapter.Active && NativeBgmSessionAdapter.Begins==1,"First authored request was released without the ready BGM session.");
            f.Frame(1600);Finish(f);
            Check(!NativeBgmSessionAdapter.Active && NativeBgmSessionAdapter.Ends==1,"BGM resource interception survived native restoration.");
        });
        Test("cancelling BGM preload releases its session without entering offline capture",()=>
        {
            NativeBgmSessionAdapter.Ready=false;
            using var f=new StrictHostRegression.Fixture();f.Call("Cancel");f.Cleanup();
            Check(f.Player.AdvanceCalls==0 && !f.Host.Capturing && NativeBgmSessionAdapter.Begins==0,"Cancellation advanced BGM or story capture.");
            Check(NativeBgmSessionAdapter.Disposals==1,"Cancelled BGM preload retained callbacks/resources.");
        });
        Test("cancelled custom BGM callbacks settle before another story can begin",()=>
        {
            NativeBgmSessionAdapter.Ready=false;NativeBgmSessionAdapter.PendingCleanup=true;
            using var f=new StrictHostRegression.Fixture();f.Call("Cancel");f.Cleanup();
            Check(RenderControlV1.IsRenderingOwned,"Native BGM cache publication was still pending when render ownership was released.");
            var panel=f.Get<NativeExportPanel>("_panel");
            Check(panel.LastBusy && panel.ExportStage.Contains("背景音乐",StringComparison.Ordinal)
                && panel.ExportStage.Contains("清理",StringComparison.Ordinal),
                "Pending native BGM cleanup did not remain visible in the busy export panel: "+panel.ExportStage);
            f.Call("Prepare",f.Get<NativeExportPanel>("_panel").Options);
            Check(f.Get<Task<ExportSession>?>("_prepare")==null,"A new story was accepted while old native cache callbacks could still publish.");
            NativeBgmSessionAdapter.PendingCleanup=false;f.Host.Update();
            Check(!RenderControlV1.IsRenderingOwned,"Settled native BGM callbacks did not release rendering ownership.");
        });
        Test("single pass waits for actual source readiness before authored capture",()=>
        {
            RuntimeModBoundaries.SourcePreparationRequired=true;int attempts=0;
            RuntimeModBoundaries.BeforeSourcePreparation=()=>{if(++attempts==1)throw new ExportException("strict_effects_source_pending","Pending original effects index.");};
            try
            {
                using var f=new StrictHostRegression.Fixture(begin:false);f.StartStrict();
                Check(!f.Host.Capturing && f.Player.AdvanceCalls==0,"Story advanced before effects readiness.");
                f.Host.Update();f.Prime();f.Frame(1600);Finish(f);
                Check(f.Session.Completed && f.Player.AdvanceCalls==1,"Ready effects did not permit exactly one authored pass.");
            }
            finally { RuntimeModBoundaries.BeforeSourcePreparation=null;RuntimeModBoundaries.SourcePreparationRequired=false; }
        });
        Test("uncertain native BGM release stays owned and requests restart without repeating release",()=>
        {
            // A native release may have completed before throwing. This fault
            // needs restart; restore test-process statics only after assertions.
            var state=typeof(RenderControlV1).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Where(field=>!field.IsLiteral && !field.IsInitOnly)
                .Select(field=>(Field:field,Value:field.GetValue(null))).ToArray();
            try
            {
                NativeBgmSessionAdapter.Ready=false;NativeBgmSessionAdapter.DisposeFailures=1;
                using var f=new StrictHostRegression.Fixture();
                var scope=f.Get<NativeBgmSessionAdapter>("_bgmSession");
                f.Call("Cancel");f.Cleanup();
                var panel=f.Get<NativeExportPanel>("_panel");
                Check(ReferenceEquals(scope,f.Get<NativeBgmSessionAdapter?>("_bgmCleanup")) && scope.CleanupPending && scope.CleanupFailure!=null,
                    "Uncertain BGM resource release lost its retained cleanup fault.");
                Check(RenderControlV1.IsRenderingOwned && panel.LastBusy,
                    "Uncertain BGM resource release became an idle or unowned export session.");
                Check(panel.StatusIsError && panel.LastStatus.Contains("重启",StringComparison.Ordinal),
                    "Uncertain BGM resource release did not display an explicit restart-needed error: "+panel.LastStatus);
                f.Call("Prepare",panel.Options);
                Check(f.Get<Task<ExportSession>?>("_prepare")==null,"A new story started after an uncertain native BGM release.");
                f.Host.Update();f.Host.Update();
                Check(NativeBgmSessionAdapter.DisposeAttempts==1 && NativeBgmSessionAdapter.Disposals==0,
                    "A later native update repeated an asset release whose outcome was uncertain.");
                Check(ReferenceEquals(scope,f.Get<NativeBgmSessionAdapter?>("_bgmCleanup")) && RenderControlV1.IsRenderingOwned && panel.LastBusy,
                    "A later update silently cleared the uncertain native BGM release fault.");
            }
            finally { foreach(var item in state)item.Field.SetValue(null,item.Value); }
        });
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; single authored pass with throwing audit boundaries");
        return failed==0?0:1;
    }
    private static void Finish(StrictHostRegression.Fixture f)
    {
        f.Host.BeforePlayerEnd(f.Player);f.Host.Update();
        for(int i=0;i<4 && f.Get<bool>("_strictDraining");i++) f.Host.Update();
        Check(!f.Get<bool>("_strictReplayPending"),"Completion requested a second story playback.");
        var final=f.Get<Task<ExportResult>?>("_finalize");
        Check(final!=null,"Native single pass did not enter finalization: "+string.Join(" | ",UnityEngine.Debug.Messages.TakeLast(5)));
        final!.GetAwaiter().GetResult();f.Host.Update();NoReplayState(f);
    }
    private static void NoReplayState(StrictHostRegression.Fixture f)
    {
        ProductionHostRegression.NoAudits(f);
        foreach(string field in new[]{"_preparedAudio","_preparedFrameSchedule","_strictLedger","_strictSandboxTrace","_repeatBaseline","_strictPreflightReceipt","_frameworkScope","_recipeScope"})
            Check(f.Get<object?>(field)==null,"Single-pass production created "+field);
        Check(!f.Get<bool>("_strictReplayPending"),"Single-pass production queued replay.");
        Check(!Directory.Exists(Plugin.TimelineReceiptDirectory),"Single-pass production wrote audit files.");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
