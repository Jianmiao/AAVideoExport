using System.Reflection;
using System.Text.Json;
using AAVideoExport.Core;

const string UserJson = """
{"Width":1920,"Height":1080,"Fps":60,"Container":"mp4","Codec":"h264","EncodingMode":"hardware","Encoder":"h264_qsv","BitrateKbps":4000,"RateControl":"vbr","AudioQuality":"aac320","AudioSampleRate":48000,"StrictSynchronization":true,"SuperResolutionEnabled":false,"CaptureMode":"native","OutputDirectory":"G:/","Title":"user-owned"}
""";
int passed=0,failed=0;
void Check(bool value,string reason){if(!value)throw new Exception(reason);}
Type Profile()=>Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests.NativeStoryTestProfile")??throw new Exception("Missing source-linked native options/performance profile.");
object Parse(string? json=null,string? stop=null)=>Profile().GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{json,stop})!;
object Whole(string? json, string? stop = null) => Profile().GetMethod("ParseBenchmark",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{json,stop,true})!;
object? Property(object owner,string name)=>owner.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(owner);
object? Call(object owner,string method,params object?[] args)=>owner.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(owner,args);
ExportOptions Options(object profile)=>(ExportOptions)Call(profile,"CreateOptions",Path.GetFullPath("artifacts/native-performance-test-output"),"test-owned")!;
void Reject(Action action){try{action();}catch(Exception e)when(e.GetBaseException() is ExportException or ArgumentException or InvalidOperationException or InvalidDataException or JsonException){return;}throw new Exception("Invalid explicit test input was accepted.");}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message);}}

Test("no opt-in preserves legacy full-movie fixture settings",()=>{
    var profile=Parse();var o=Options(profile);Check(o.Width==640&&o.Height==360&&o.Fps==30&&o.Container=="mov"&&o.EncodingMode=="software"&&o.Encoder=="libx264"&&o.AudioQuality=="pcm16"&&o.StrictSynchronization&&!o.WriteCover,"Legacy fixture changed.");
    Check(Property(profile,"PerformanceStopFrame")==null,"Default became a performance-only run.");
});
Test("screenshot options use explicit QSV and never adopt the user's output path",()=>{
    var profile=Parse(UserJson,"360");var o=Options(profile);Check(o.Width==1920&&o.Height==1080&&o.Fps==60&&o.Container=="mp4"&&o.Codec=="h264"&&o.EncodingMode=="hardware"&&o.Encoder=="h264_qsv"&&o.BitrateKbps==4000&&o.RateControl=="vbr"&&o.AudioQuality=="aac320"&&o.AudioSampleRate==48000&&o.StrictSynchronization&&!o.SuperResolutionEnabled,"Screenshot settings changed.");
    Check(o.OutputDirectory==Path.GetFullPath("artifacts/native-performance-test-output")&&o.Title=="test-owned"&&!o.WriteCover,"Test profile adopted user's publication path or title.");
});
Test("profile rejects non-strict override",()=>Reject(()=>Options(Parse(UserJson.Replace("\"StrictSynchronization\":true","\"StrictSynchronization\":false"),"360"))));
Test("profile rejects an invalid encoder codec combination",()=>Reject(()=>Options(Parse(UserJson.Replace("h264_qsv","hevc_qsv"),"360"))));
Test("profile rejects unsupported dimensions and sample rate",()=>Reject(()=>Options(Parse(UserJson.Replace("1920","0").Replace("48000","32000"),"360"))));
Test("profile override cannot masquerade as legacy full-media acceptance",()=>Reject(()=>Parse(UserJson)));
Test("profile rejects a performance window at or before warmup",()=>Reject(()=>Parse(UserJson,"60")));
Test("profile rejects malformed performance bound",()=>Reject(()=>Parse(UserJson,"360.5")));
Test("real candidate collection admits selected QSV only after successful matching probe",()=>{
    var p=Parse(UserJson,"360");var o=Options(p);var candidates=new[]{new EncoderCapability("h264_qsv","h264",true,true,"real test passed"),new EncoderCapability("libx264","h264",false,true,"software passed")};
    Check((string)Call(p,"ValidateProbe",o,"hardware",candidates,"Intel Arc")! =="h264_qsv","Wrong encoder selected.");
});
Test("failed QSV cannot silently fall back to passed software",()=>{
    var p=Parse(UserJson,"360");Reject(()=>Call(p,"ValidateProbe",Options(p),"hardware",new[]{new EncoderCapability("h264_qsv","h264",true,false,"failed"),new EncoderCapability("libx264","h264",false,true,"passed")},"Intel Arc"));
});
Test("stale software-mode probe cannot admit hardware profile",()=>{
    var p=Parse(UserJson,"360");Reject(()=>Call(p,"ValidateProbe",Options(p),"software",new[]{new EncoderCapability("h264_qsv","h264",true,true,"passed")},"Intel Arc"));
});
Test("window excludes startup and requests bounded cancellation only at authored stop frame",()=>{
    var p=Parse(UserJson,"360");var window=Call(p,"CreatePerformanceWindow")!;
    Check(!(bool)Call(window,"Observe",59L,100d)!&&!(bool)Call(window,"Observe",60L,110d)!&&!(bool)Call(window,"Observe",359L,129d)!,"Window started cancellation early.");
    Check((bool)Call(window,"Observe",360L,130d)!,"Window did not request bounded cancellation.");
    var result=Call(window,"Snapshot")!;Check((long)Property(result,"StartFrame")! ==60&&(long)Property(result,"StopFrame")! ==360&&(long)Property(result,"MeasuredFrames")! ==300&&(double)Property(result,"ElapsedSeconds")! ==20&&(double)Property(result,"FramesPerSecond")! ==15,"Startup contaminated measured window.");
});
Test("window reports actual frame delta when native updates slightly overshoot boundaries",()=>{
    var window=Call(Parse(UserJson,"360"),"CreatePerformanceWindow")!;Call(window,"Observe",61L,110d);Check((bool)Call(window,"Observe",362L,130d)!,"Overshoot did not request cancellation.");
    var result=Call(window,"Snapshot")!;Check((long)Property(result,"StartFrame")! ==61&&(long)Property(result,"StopFrame")! ==362&&(long)Property(result,"MeasuredFrames")! ==301&&(double)Property(result,"FramesPerSecond")! ==15.05,"Overshoot was reported as an invented 60-to-360 interval.");
});
Test("effective host and session must retain profile and actual hardware encoder",()=>{
    var p=Parse(UserJson,"360");var o=Options(p);Call(p,"ValidateEffectiveOptions",o,o,"h264_qsv","h264_qsv",Path.Combine(o.OutputDirectory,o.Title+".mp4"));
    Reject(()=>Call(p,"ValidateEffectiveOptions",o,o,"h264_qsv","libx264",Path.Combine(o.OutputDirectory,o.Title+".mp4")));
    Reject(()=>Call(p,"ValidateEffectiveOptions",o,o with{Fps=30},"h264_qsv","h264_qsv",Path.Combine(o.OutputDirectory,o.Title+".mp4")));
});
Test("window rejects backwards frame or clock state",()=>{
    var window=Call(Parse(UserJson,"360"),"CreatePerformanceWindow")!;Call(window,"Observe",60L,100d);Reject(()=>Call(window,"Observe",59L,101d));
});
object PerformanceMode(bool singlePass) => Activator.CreateInstance(Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests.StoryPerformanceMode")
    ?? throw new Exception("Missing profile-aware performance mode."), new object[] { singlePass })!;
Test("single-pass performance phase reports real render and encoding work", () => {
    var mode = PerformanceMode(true); Check((string)Property(mode,"Phase")! == "single-pass-render-encode", "Single-pass phase is mislabeled as audio-only.");
    Call(mode,"ValidateObservedPass",false,true,false,false,1); Call(mode,"ValidateStoppedWindow",360L,356L,340L);
});
Test("single-pass benchmark rejects audio preparation and repeated playback", () => {
    var mode = PerformanceMode(true); Reject(()=>Call(mode,"ValidateObservedPass",true,false,false,false,1));
    Reject(()=>Call(mode,"ValidateObservedPass",false,true,false,false,2));
});
Test("single-pass benchmark cannot claim render-encode speed with no encoded frames", () =>
    Reject(()=>Call(PerformanceMode(true),"ValidateStoppedWindow",360L,356L,0L)));
Test("legacy performance remains first-audio-preparation without encoded video", () => {
    var mode = PerformanceMode(false); Check((string)Property(mode,"Phase")! == "first-audio-preparation","Legacy phase changed.");
    Call(mode,"ValidateObservedPass",true,false,false,false,1); Call(mode,"ValidateStoppedWindow",360L,0L,0L);
    Reject(()=>Call(mode,"ValidateObservedPass",true,true,false,false,2));
});
Test("explicit whole-export benchmark accepts real QSV AAC options without a stop frame", () => {
    var p=Whole(UserJson);var o=Options(p);Check((bool)Property(p,"WholeExportBenchmark")!&&Property(p,"PerformanceStopFrame")==null
        &&o.Width==1920&&o.Height==1080&&o.Fps==60&&o.AudioQuality=="aac320"&&o.Encoder=="h264_qsv","Whole-export benchmark lost its explicit profile.");
});
Test("whole-export benchmark cannot be combined with a cancellation window",()=>Reject(()=>Whole(UserJson,"360")));
Test("whole-export benchmark requires explicit settings",()=>Reject(()=>Whole(null)));
JsonElement Probe(string codec="aac",long secondAudioPts=1024,int secondAudioCount=1024,int width=1920,long secondVideoPts=1) => JsonSerializer.SerializeToElement(new {
    streams=new object[]{new{codec_type="video",codec_name="h264",width,height=1080,time_base="1/60",start_pts=0,duration_ts=2},
        new{codec_type="audio",codec_name=codec,sample_rate=48000,channels=2,time_base="1/48000",start_pts=0,duration_ts=1600}},
    frames=new object[]{new{media_type="video",pts=0},new{media_type="audio",pts=0,nb_samples=codec=="aac"?1024:800},
        new{media_type="video",pts=secondVideoPts},new{media_type="audio",pts=secondAudioPts,nb_samples=secondAudioCount}}
});
object Inspect(JsonElement probe,ExportOptions? options=null) => Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests.NativeBenchmarkMedia")!
    .GetMethod("Inspect",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{probe,options??Options(Whole(UserJson)),2L})!;
Test("whole-export AAC inspection declares partial codec padding without PCM equality",()=>{
    var result=Inspect(Probe());Check((long)Property(result,"AudioPaddingSampleFrames")! ==448,"AAC tail padding was hidden.");
    Check(((string)Property(result,"Scope")!).Contains("no lossy-content equivalence"),"AAC was represented as a lossless oracle.");
});
Test("whole-export inspector rejects shifted video PTS and wrong dimensions",()=>{
    Reject(()=>Inspect(Probe(secondVideoPts:2)));Reject(()=>Inspect(Probe(width:1280)));
});
Test("whole-export inspector rejects audio gaps and excess padding",()=>{
    Reject(()=>Inspect(Probe(secondAudioPts:1025)));Reject(()=>Inspect(Probe(secondAudioCount:2048)));
});
Test("whole-export PCM inspection still requires exact sample count",()=>{
    var o=Options(Whole(UserJson.Replace("aac320","pcm16").Replace("mp4","mov")));
    Inspect(Probe("pcm_s16le",800,800),o);Reject(()=>Inspect(Probe("pcm_s16le",800,801),o));
});
if(args.Length>0) Test("actual independently encoded AAC movie passes format-aware inspection",()=>{
    using var actual=JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(args[0])));
    var o=new ExportOptions{Width=8,Height=4,Fps=30,Container="mp4",Codec="h264",AudioQuality="aac320",AudioSampleRate=48000,StrictSynchronization=true};
    var method=Assembly.GetExecutingAssembly().GetType("AAVideoExport.NativePath.Tests.NativeBenchmarkMedia")!.GetMethod("Inspect",BindingFlags.Static|BindingFlags.NonPublic)!;
    var result=method.Invoke(null,new object[]{actual.RootElement,o,4L})!;
    Check((long)Property(result,"Frames")! ==4&&(long)Property(result,"ExpectedAudioSampleFrames")! ==6400,"Actual AAC probe lost the authored grid.");
});
Console.WriteLine($"RESULT {passed} passed; {failed} failed; source-linked native test profile only");
return failed==0?0:1;
