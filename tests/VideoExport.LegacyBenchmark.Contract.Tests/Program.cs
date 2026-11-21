using AAVideoExport.LegacyBenchmark.Tests;
using Mono.Cecil;

int passed=0,failed=0;
void Check(bool value,string message){if(!value)throw new Exception(message);}
void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected rejection.");}
void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);}}
Test("window measures actual 60 through 360 work and requests cancellation",()=>{
 var w=new BenchmarkWindow(); Check(!w.Observe(new(59,59,57,94400),1),"early");Check(!w.Observe(new(60,60,58,96000),2),"early");
 Check(w.Observe(new(360,360,358,576000),5),"did not request cancellation");
 var m=w.Measurement!;Check(m.Frames==300&&m.Seconds==3&&m.FramesPerSecond==100&&m.AcceptedFrames==300&&m.WrittenFrames==300,"invalid actual counters");
});
Test("window rejects preparation with zero encoder work",()=>{var w=new BenchmarkWindow();w.Observe(new(60,0,0,96000),2);Reject(()=>w.Observe(new(360,0,0,576000),5));});
Test("window rejects backwards frame and clock",()=>{var w=new BenchmarkWindow();w.Observe(new(60,60,58,96000),2);Reject(()=>w.Observe(new(59,60,58,96000),3));var x=new BenchmarkWindow();x.Observe(new(60,60,58,96000),2);Reject(()=>x.Observe(new(61,61,59,97600),1));});
Test("window rejects missing interval",()=>Reject(()=>new BenchmarkWindow().Observe(new(361,361,359,577600),2)));
Test("full and window modes are explicit",()=>{Check(BenchmarkContract.ParseMode("whole")=="whole","whole");Check(BenchmarkContract.ParseMode("window")=="window","window");Reject(()=>BenchmarkContract.ParseMode(""));});
Test("unpopulated native catalog paths are non-matches",()=>{
 string source=Path.GetFullPath("source.aas");
 foreach(string? path in new string?[]{null,""," ","\0"})Check(!BenchmarkContract.SamePath(path,source),"Incomplete path matched a real story.");
 Check(!BenchmarkContract.SamePath(source,null),"Missing target matched a story.");
});
Test("matching passes over incomplete cards until exact native path populates",()=>{
 string source=Path.GetFullPath("source.aas");string?[] paths={null,"",Path.GetFullPath("other.aas")};
 Check(paths.FirstOrDefault(path=>BenchmarkContract.SamePath(path,source))==null,"Incomplete catalog invented a matching detail.");
 paths[0]=source;Check(paths.FirstOrDefault(path=>BenchmarkContract.SamePath(path,source))==source,"Populated exact detail was not found.");
 Check(BenchmarkContract.SamePath(source,source.ToUpperInvariant()),"Native case-insensitive identity changed.");
});
Test("foreground activation waits for exact native readiness and happens once",()=>{
 var f=new BenchmarkForeground();int activations=0;
 Check(!f.PollReadiness(false,false,0,()=>activations++),"Unready catalog admitted timing.");
 Check(activations==0,"Activated before native selection readiness.");
 Check(!f.PollReadiness(true,false,1,()=>activations++),"Unfocused activation call admitted timing.");
 Check(!f.PollReadiness(true,false,2,()=>activations++),"Background window admitted timing.");
 Check(f.PollReadiness(true,true,3,()=>activations++),"Confirmed foreground window was rejected.");
 Check(activations==1,"Foreground activation retried.");
});
Test("Unity focus alone cannot hide a foreign foreground window",()=>{
 Check(!BenchmarkContract.ExpectedForeground(true,false),"Stale Unity focus admitted another process's foreground window.");
 Check(!BenchmarkContract.ExpectedForeground(false,true),"Native foreground ownership admitted an unfocused Unity application.");
 Check(!BenchmarkContract.ExpectedForeground(false,false)&&BenchmarkContract.ExpectedForeground(true,true),"Combined foreground expectation is inconsistent.");
});
Test("foreground timeout rejects late focus without reactivation",()=>{
 var f=new BenchmarkForeground();int activations=0;f.PollReadiness(true,false,1,()=>activations++);
 Reject(()=>f.PollReadiness(true,false,6,()=>activations++));
 Reject(()=>f.PollReadiness(true,true,7,()=>activations++));
 Check(!f.Measuring&&!f.Completed&&activations==1&&f.Failure.Length>0,"Timed-out focus became comparable.");
});
Test("measurement requires confirmed focus at actual start",()=>{
 var early=new BenchmarkForeground();Reject(()=>early.BeginMeasurement(true,0));
 var f=new BenchmarkForeground();f.PollReadiness(true,true,0,()=>{});f.PollReadiness(true,true,1,()=>{});
 Reject(()=>f.BeginMeasurement(false,1.1));Check(!f.Measuring,"Background start began timing.");
});
Test("focus loss latches during measurement even if focus returns",()=>{
 var f=new BenchmarkForeground();f.PollReadiness(true,false,0,()=>{});f.PollReadiness(true,true,1,()=>{});f.BeginMeasurement(true,1.1);
 f.ObserveMeasurement(true,2);Reject(()=>f.ObserveMeasurement(false,2.1));
 Reject(()=>f.CompleteMeasurement(true,3));Check(f.FocusLost&&!f.Completed,"Mixed-focus timing became comparable.");
});
Test("completion boundary rejects focus loss and keeps measurement invalid",()=>{
 var f=new BenchmarkForeground();f.PollReadiness(true,true,0,()=>{});f.PollReadiness(true,true,1,()=>{});f.BeginMeasurement(true,1.1);
 Reject(()=>f.CompleteMeasurement(false,2));Check(f.FocusLost&&!f.Completed,"Background completion accepted.");
});
Test("foreground completion does not reclassify later restoration focus",()=>{
 var f=new BenchmarkForeground();f.PollReadiness(true,true,0,()=>{});f.PollReadiness(true,true,1,()=>{});f.BeginMeasurement(true,1.1);
 f.ObserveMeasurement(true,2);f.CompleteMeasurement(true,3);f.ObserveMeasurement(false,4);
 Check(f.Completed&&!f.Measuring&&!f.FocusLost&&f.Failure.Length==0,"Post-measurement focus changed a valid interval.");
});
Test("failed activation cannot repeat or permit measurement",()=>{
 var f=new BenchmarkForeground();int activations=0;Reject(()=>f.PollReadiness(true,false,0,()=>{activations++;throw new InvalidOperationException("denied");}));
 Reject(()=>f.PollReadiness(true,true,1,()=>activations++));Check(activations==1&&!f.Confirmed,"Failed activation was retried or accepted.");
});
Test("window setup focus changes neither activate nor invalidate excluded preparation",()=>{
 var f=new BenchmarkWindowForeground();int activations=0;
 f.ObserveCapture(false,0,true,0,()=>activations++);f.ObserveFocus(false,1);f.ObserveCapture(false,0,false,2,()=>activations++);
 Check(activations==0&&!f.ActivationRequested&&!f.Measuring&&f.Failure.Length==0,"Excluded preparation used the window focus contract.");
});
Test("window activates once during actual capture and begins only at frame 60",()=>{
 var f=new BenchmarkWindowForeground();int activations=0;
 f.ObserveCapture(true,0,false,0,()=>activations++);f.ObserveCapture(true,1,true,1,()=>activations++);
 f.ObserveFocus(false,2);f.ObserveCapture(true,59,true,3,()=>activations++);
 Check(!f.Measuring&&f.Failure.Length==0,"Warmup was counted as measured work.");
 f.ObserveCapture(true,60,true,4,()=>activations++);
 Check(f.Measuring&&f.Confirmed&&activations==1,"Frame 60 did not begin one confirmed foreground window.");
});
Test("window cannot confirm late by moving the frame 60 boundary",()=>{
 var f=new BenchmarkWindowForeground();int activations=0;f.ObserveCapture(true,0,false,0,()=>activations++);
 f.ObserveCapture(true,59,false,1,()=>activations++);
 Reject(()=>f.ObserveCapture(true,60,false,2,()=>activations++));
 Reject(()=>f.ObserveCapture(true,61,true,3,()=>activations++));
 Check(!f.Measuring&&!f.Completed&&activations==1,"Late confirmation shifted or retried the window.");
});
Test("window refuses first activation at or beyond its measured start",()=>{
 var f=new BenchmarkWindowForeground();int activations=0;
 Reject(()=>f.ObserveCapture(true,60,true,0,()=>activations++));Check(activations==0,"Late activation stole focus inside the measured window.");
});
Test("window does not shift a skipped frame 60 even after warmup confirmation",()=>{
 var f=new BenchmarkWindowForeground();f.ObserveCapture(true,0,false,0,()=>{});f.ObserveCapture(true,59,true,1,()=>{});
 Reject(()=>f.ObserveCapture(true,61,true,2,()=>{}));Check(!f.Measuring&&!f.Completed,"Missed frame 60 shifted a comparable interval.");
});
Test("window measurement focus loss stays fatal despite earlier warmup allowance",()=>{
 var f=new BenchmarkWindowForeground();f.ObserveCapture(true,0,false,0,()=>{});f.ObserveCapture(true,59,true,1,()=>{});f.ObserveCapture(true,60,true,2,()=>{});
 Reject(()=>f.ObserveFocus(false,2.1));Reject(()=>f.CompleteMeasurement(360,true,3));
 Check(f.FocusLost&&!f.Completed,"Measured foreground loss became comparable.");
});
Test("window foreground spans exactly the observed 60 to 360 measurement",()=>{
 var f=new BenchmarkWindowForeground();var w=new BenchmarkWindow();int activations=0;
 f.ObserveCapture(false,0,false,0,()=>activations++);f.ObserveCapture(true,0,false,1,()=>activations++);
 f.ObserveCapture(true,59,false,2,()=>activations++);f.ObserveCapture(true,60,true,3,()=>activations++);
 Check(!w.Observe(new(60,60,58,96000),3),"Window closed at start.");
 f.ObserveCapture(true,360,true,6,()=>activations++);Check(w.Observe(new(360,360,358,576000),6),"Actual frame window missing.");
 f.CompleteMeasurement(360,true,6);f.ObserveFocus(false,7);
 Check(f.Completed&&!f.FocusLost&&activations==1&&w.Measurement!.Frames==300&&w.Measurement.Seconds==3,"Setup focus altered actual measured interval.");
});
if(args.Length>=1)
{
 Test("actual installed provider has original reflection launch contract",()=>{
  using var a=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"AAVideoExport.dll"));
  var t=a.MainModule.Types.Single(x=>x.FullName=="AAVideoExport.Plugin.ExportHost");
  foreach(string name in new[]{"Prepare","Probe","ResolveStory","Fail","Cancel","ReportCompleted"})Check(t.Methods.Count(m=>m.Name==name)==1,"missing or ambiguous "+name);
  Check(t.Methods.Single(m=>m.Name=="Prepare").Parameters.Single().ParameterType.FullName=="AAVideoExport.Core.ExportOptions","wrong normal Prepare options");
  foreach(string name in new[]{"Current","_session","_captured","_panel","_selectedStory","_selectedStoryKey","_capabilities","_capabilitiesMode","_prepare","_finalize","_probe"})Check(t.Fields.Any(f=>f.Name==name),"missing field "+name);
  var control=a.MainModule.Types.Single(x=>x.FullName=="AAVideoExport.Integration.RenderControlV1");
  foreach(string name in new[]{"BeforeAcquire","AfterRelease"})Check(control.Events.Any(e=>e.Name==name&&e.EventType.FullName=="System.Action"),"missing v1 managed event "+name);
 });
 Test("actual installed core exposes real encoder and PCM counters",()=>{
  using var a=AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"AAVideoExport.Core.dll"));
  var s=a.MainModule.Types.Single(t=>t.FullName=="AAVideoExport.Core.ExportSession");
  foreach(string name in new[]{"FramesAccepted","FramesWritten","AudioSampleFrames","PreflightDuration","EncodingDuration","FinalizationDuration"})Check(s.Properties.Any(p=>p.Name==name),"missing actual session counter "+name);
  var o=a.MainModule.Types.Single(t=>t.FullName=="AAVideoExport.Core.ExportOptions");
  foreach(string name in new[]{"Width","Height","Fps","Encoder","EncodingMode","OutputDirectory","Title","WriteCover"})Check(o.Properties.Any(p=>p.Name==name&&p.SetMethod!=null),"missing writable original option "+name);
 });
}
if(args.Length>=2)Test("compiled benchmark has no hard AAVE or audit dependency",()=>{
 using var a=AssemblyDefinition.ReadAssembly(args[1]);
 Check(!a.MainModule.AssemblyReferences.Any(x=>x.Name.StartsWith("AAVideoExport",StringComparison.Ordinal)),"benchmark hard-links AAVE");
 Check(a.MainModule.Types.Any(t=>t.FullName=="AAVideoExport.LegacyBenchmark.Tests.LegacyBenchmarkRunner"),"runner missing");
});
Console.WriteLine($"RESULT passed={passed} failed={failed}");return failed==0?0:1;
