using AAVideoExport.Core;

if (ExportBuildProfile.DevelopmentAuditEnabled) throw new Exception("This suite requires the production build.");
if (args.Length != 1) throw new ArgumentException("Pass a directory containing ffmpeg.exe and ffprobe.exe.");
string root=Path.Combine(Path.GetTempPath(),"aave-production-execution-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
int passed=0,failed=0;
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message);}}
void Check(bool value,string message){if(!value)throw new Exception(message);}
void Reject(Action action){try{action();}catch(ExportException){return;}throw new Exception("Expected ordinary execution failure.");}
try
{
    Test("execution plan contains real counts without a fabricated audit identity",()=>
    {
        var plan=StrictFramePlan.CreateExecution(2,30,48000,2);
        Check(!plan.HasSourceIdentity&&plan.SourceIdentity==""&&plan.AudioSampleFrameCount==3200,"Execution plan acquired a fake proof.");
        plan.ValidateCaptureIdentity("",30,48000,2);Reject(()=>plan.ValidateVideoComplete(1));Reject(()=>plan.ValidateAudioComplete(3199));
    });
    Test("prepared PCM remains exact while production omits its audit digest",()=>
    {
        using var pcm=new PreparedAudioTrack(Path.Combine(root,"prepared.pcm"),30,48000,2);
        float[] samples=Enumerable.Range(0,3200).Select(i=>(i%100)*0.001f).ToArray();pcm.Append(samples,samples.Length);pcm.Seal(1);
        Check(pcm.SamplesSha256=="","Production generated a PCM audit digest.");
        var frame=pcm.ReadFrame(0);Check(frame.Samples.SequenceEqual(samples),"Execution PCM changed.");pcm.CompleteReplay();
    });
    Test("execution schedule compresses unchanged positions and consumes the prepared frame order",()=>
    {
        var schedule=new PreparedFrameSchedule();
        for(int frame=0;frame<100;frame++)schedule.Record(frame,frame<50?0:1,frame>=70,1);
        schedule.Seal(100);Check(schedule.SegmentCount==3,"Unchanged native positions were not run-length compressed.");
        for(int frame=0;frame<100;frame++)schedule.Consume(frame,frame<50?0:1,frame>=70,1);
        schedule.Complete(100);
    });
    Test("execution schedule rejects a different branch at the same row and frame",()=>
    {
        var schedule=new PreparedFrameSchedule();schedule.Record(0,2,true,1);schedule.Seal(1);
        Reject(()=>schedule.Consume(0,2,true,2));
    });
    Test("execution schedule rejects missing selection state even when row and counts match",()=>
    {
        var schedule=new PreparedFrameSchedule();schedule.Record(0,2,true,1);schedule.Seal(1);
        Reject(()=>schedule.Consume(0,2,false,-1));
    });
    Test("execution schedule rejects skipped frames and incomplete playback",()=>
    {
        var schedule=new PreparedFrameSchedule();Reject(()=>schedule.Record(1,0,false,-1));
        schedule.Record(0,0,false,-1);schedule.Record(1,0,false,-1);schedule.Seal(2);
        Reject(()=>schedule.Consume(1,0,false,-1));schedule.Consume(0,0,false,-1);Reject(()=>schedule.Complete(2));
    });
    Test("production movie completion ignores audit callbacks and publishes no recipe receipt",()=>
    {
        var options=new ExportOptions{Title="production",OutputDirectory=root,Width=64,Height=64,Fps=30,Container="mov",Codec="h264",EncodingMode="software",Encoder="libx264",AudioQuality="pcm16",AudioSampleRate=48000,StrictSynchronization=true,WriteCover=false};
        using var session=new ExportSession(options,Path.Combine(args[0],"ffmpeg.exe"),Path.Combine(args[0],"ffprobe.exe"),"libx264");
        session.BindStrictTimeline(StrictFramePlan.CreateExecution(2,30,48000,2));
        session.BeforeOutputCommit=()=>throw new Exception("Production entered output audit callback.");
        session.AcquireOutputCommitScope=()=>throw new Exception("Production entered audit publication scope.");
        session.RuntimeFactoryRecipe=new RuntimeFactoryRecipeReceipt(new string('a',64),new string('b',64));
        for(int i=0;i<2;i++){session.WriteFrame(new byte[64*64*4],64*64*4);session.WriteAudioFrame(i,new float[3200],3200);}
        var result=session.Complete();Check(File.Exists(result.OutputPath)&&result.Frames==2,"Production output missing.");
        Check(result.RuntimeRecipeReceiptPath==null&&!File.Exists(result.OutputPath+".runtime-recipes.json"),"Production published an audit sidecar.");
    });
}
finally{Directory.Delete(root,true);}
Console.WriteLine($"RESULT {passed} passed; {failed} failed; production execution and real FFmpeg output");return failed==0?0:1;
