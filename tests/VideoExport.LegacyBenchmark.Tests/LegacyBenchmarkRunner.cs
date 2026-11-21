using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;
namespace AAVideoExport.LegacyBenchmark.Tests;

public sealed class LegacyBenchmarkRunner:MonoBehaviour
{
 private static LegacyBenchmarkRunner? active;
 private readonly Stopwatch lifetime=Stopwatch.StartNew(),elapsed=new();
 private readonly BenchmarkWindow window=new();
 private readonly BenchmarkForeground foreground=new();
 private readonly BenchmarkWindowForeground windowForeground=new();
 private readonly OwnWindowForeground ownWindow=new();
 private readonly List<object> focusChanges=new();
 private readonly List<object> setupFocusChanges=new();
 private readonly List<string> absentOptions=new();
 private Harmony? hooks;
 private object? host,panel,options,savedOptions,savedCommit,session,result;
 private Type hostType=null!,provider=null!,optionType=null!;
 private RenderControlObservation? render;
 private string mode="",story="",storyHash="",optionsPath="",optionsHash="",outputPath="",encoder="",error="";
 private int stage,stable,passes,matchingCardFrames,omittedFocusTransitions;
 private double timeout=1200,selectionDeadline,cleanupDeadline,completedSeconds=-1,preparedSeconds=-1,firstCaptureSeconds=-1,finalizeSeconds=-1;
 private bool selected,cancelled,submitted,focus,focusRecorded,unityFocus,nativeFocus;
 private long lastRawAudioSamples,sessionAudioSamples;
 private long prepareTimestamp;
 private double restoredSeconds=-1;
 private Counters counters=new(0,0,0,0);
 private object? identity,effectiveOptions,foregroundActivation;
 private ForegroundWindowState lastForegroundWindow;
 private object? failureForegroundWindow;
 private string foregroundError="";
 private string foregroundActivationPhase="";
 private long foregroundActivationFrame=-1;
 public LegacyBenchmarkRunner(IntPtr pointer):base(pointer){}

 public void Update()
 {
  if(stage==99)return;
  try
  {
   if(stage==0)Initialize();
   ObserveFocus(Application.isFocused,"update");
   if(stage==90){SettleFailure();return;}
   if(foregroundError.Length!=0)throw new InvalidOperationException(foregroundError);
   if(lifetime.Elapsed.TotalSeconds>timeout)throw new TimeoutException("Benchmark wall deadline exceeded; stage="+stage+"; host="+(host==null?"absent":R.Text(host,"_status")));
   if(stage==1)WaitReady(); else if(stage==2)WaitProbe();else if(stage==3)SelectAndSubmit();else if(stage==6)ConfirmForegroundAndSubmit();else if(stage==4)Observe();else if(stage==5)SettleSuccess();
  }
  catch(Exception e){Fail(e.GetBaseException());}
 }
 public void OnApplicationFocus(bool hasFocus)
 {
  if(stage==99)return;
  try{ObserveFocus(hasFocus,"native-focus-event");}
  catch(Exception error){foregroundError=error.GetBaseException().Message;}
 }
 [HideFromIl2Cpp] private void Initialize()
 {
  mode=BenchmarkContract.ParseMode(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_BENCHMARK_MODE"));
  story=RequiredFile("AA_VIDEOEXPORT_TEST_STORY");optionsPath=RequiredFile("AA_VIDEOEXPORT_TEST_OPTIONS_JSON");
  if(!story.EndsWith(".aas",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("An exact native .aas story is required.");
  if(new FileInfo(optionsPath).Length>65536)throw new InvalidOperationException("Options input exceeds 64 KiB.");
  if(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_TIMEOUT_SECONDS") is {Length:>0} seconds&&(!double.TryParse(seconds,out timeout)||timeout<30||timeout>7200))throw new InvalidOperationException("Invalid timeout.");
  foreach(System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
   if(Regex.IsMatch((string)variable.Key,"^AA_VIDEOEXPORT_(RUNTIME_.*|STARTUP_TRACE_FILE|AUDIT.*|DEVELOPMENT_AUDIT)$",RegexOptions.IgnoreCase))throw new InvalidOperationException("Audit environment is forbidden during timings: "+variable.Key);
  if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name is "AAVideoExport.RuntimeBootstrap" or "AAVideoExport.NativePath.Tests"))throw new InvalidOperationException("Timing process loaded an audit bootstrap or the separate acceptance harness.");
  var assembly=AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a=>a.GetName().Name=="AAVideoExport")??throw new InvalidOperationException("Actual AAVE provider failed to load.");
  hostType=assembly.GetType("AAVideoExport.Plugin.ExportHost",true)!;provider=assembly.GetType("AAVideoExport.Plugin.Plugin",true)!;
  optionType=R.Method(hostType,"Prepare",1).GetParameters()[0].ParameterType;
  var build=optionType.Assembly.GetType("AAVideoExport.Core.ExportBuildProfile");
  if(build!=null&&R.Bool(build,"DevelopmentAuditEnabled",true))throw new InvalidOperationException("DevelopmentAudit provider cannot be benchmarked.");
  if(build!=null&&!R.Bool(build,"SinglePassEnabled"))throw new InvalidOperationException("Candidate is not the requested unified single-pass build.");
  options=CreateOptions();storyHash=R.Hash(story);optionsHash=R.Hash(optionsPath);
  identity=CreateIdentity(assembly);
  File.WriteAllText(Path.Combine(Plugin.Output,"legacy-benchmark-start.json"),R.Json(identity));
  active=this;hooks=new Harmony("halocue.aa.videoexport.legacybenchmark.observers");
  hooks.Patch(R.Method(hostType,"Fail",1),prefix:new HarmonyMethod(typeof(LegacyBenchmarkRunner),nameof(OnFailure)));
  hooks.Patch(R.Method(hostType,"ReportCompleted",1),prefix:new HarmonyMethod(typeof(LegacyBenchmarkRunner),nameof(OnCompleted)));
  var begin=hostType.GetMethods(R.Flags).SingleOrDefault(m=>m.Name=="BeginStrictPass")??hostType.GetMethods(R.Flags).SingleOrDefault(m=>m.Name=="Begin");
  if(begin==null)throw new MissingMethodException("No original authored capture begin method.");
  hooks.Patch(begin,postfix:new HarmonyMethod(typeof(LegacyBenchmarkRunner),nameof(OnPass)));
  Plugin.LogLine("LEGACY-BENCHMARK START mode="+mode+" provider="+assembly.Location+" story="+story);stage=1;
 }
 [HideFromIl2Cpp] private object CreateOptions()
 {
  var value=Activator.CreateInstance(optionType)??throw new InvalidOperationException("Cannot construct actual provider options.");
  using var document=JsonDocument.Parse(File.ReadAllText(optionsPath));
  foreach(var item in document.RootElement.EnumerateObject())
  {
   var property=optionType.GetProperty(item.Name,R.Flags);
   if(property==null)
   {
    if(item.Name=="StrictSynchronization"&&item.Value.ValueKind==JsonValueKind.True){absentOptions.Add(item.Name);continue;}
    throw new InvalidOperationException("Actual provider does not support requested option: "+item.Name);
   }
   if(property.SetMethod==null)throw new InvalidOperationException("Requested option is read-only: "+item.Name);
   property.SetValue(value,JsonSerializer.Deserialize(item.Value.GetRawText(),property.PropertyType));
  }
  foreach(string name in new[]{"Width","Height","Fps","Container","Codec","EncodingMode","Encoder","BitrateKbps","RateControl","AudioQuality","AudioSampleRate","CaptureMode","SuperResolutionEnabled"})
   if(!document.RootElement.TryGetProperty(name,out _))throw new InvalidOperationException("Comparison requires explicit option: "+name);
  if(R.Long(value,"Width")!=1920||R.Long(value,"Height")!=1080||R.Text(value,"Encoder")=="auto")throw new InvalidOperationException("Comparison requires 1920x1080 and one explicit encoder.");
  string media=Path.Combine(Plugin.Output,"benchmark-media");
  if(Directory.Exists(media)&&Directory.EnumerateFileSystemEntries(media).Any())throw new IOException("Benchmark media directory must be empty.");
  Directory.CreateDirectory(media);R.Set(value,"OutputDirectory",media);R.Set(value,"Title","benchmark-"+Environment.ProcessId);R.Set(value,"WriteCover",false);
  if(optionType.GetProperty("StrictSynchronization",R.Flags)!=null&&!R.Bool(value,"StrictSynchronization"))throw new InvalidOperationException("Candidate must use strict synchronization.");
  R.Call(value,"Validate");outputPath=Path.Combine(media,R.Text(value,"Title")+"."+R.Text(value,"Container"));return value;
 }
 [HideFromIl2Cpp] private void WaitReady()
 {
  if(!Ready()){stable=0;return;}
  host=R.Get(hostType,"Current");if(host==null||R.Bool(host,"Busy")){stable=0;return;}
  if(++stable<8)return;
  ValidateSource();panel=R.Get(host,"_panel")??throw new InvalidOperationException("Export panel missing.");
  savedOptions=R.Get(panel,"Options");savedCommit=R.Get(panel,"OutputDirectoryCommitted");
  R.Set(panel,"OutputDirectoryCommitted",null);R.Set(panel,"Options",options);R.Set(panel,"Visible",false);
  R.Call(host,"Probe");stage=2;
 }
 [HideFromIl2Cpp] private void WaitProbe()
 {
  if(R.Get(host!,"_probe")!=null)return;
  var capabilities=R.Get(host!,"_capabilities")??throw new InvalidOperationException("Real encoder probe failed: "+R.Text(host!,"_status"));
  if(R.Text(host!,"_capabilitiesMode")!=R.Text(options!,"EncodingMode"))throw new InvalidOperationException("Encoder probe mode mismatch.");
  encoder=(string)R.Call(capabilities,"PickForMode",options)!;
  if(encoder!=R.Text(options!,"Encoder"))throw new InvalidOperationException("Actual probe did not select the explicitly requested encoder.");
  stage=3;selectionDeadline=lifetime.Elapsed.TotalSeconds+15;
 }
 [HideFromIl2Cpp] private void SelectAndSubmit()
 {
  if(lifetime.Elapsed.TotalSeconds>selectionDeadline)throw new TimeoutException("Exact native catalog selection did not settle.");
  if(!Ready()){matchingCardFrames=0;return;}
  R.Set(panel!,"Visible",false);
  if(!RenderControlObservation.Idle(hostType.Assembly))return;
  var card=Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(x=>x!=null&&x.gameObject.activeInHierarchy&&x.gameObject.scene.name=="CatalogScene"&&x.block!=null&&Same(x.block.path,story));
  if(card==null)
  {
   matchingCardFrames=0;
   if(selected)return;
   var rows=Resources.FindObjectsOfTypeAll<CatalogFileItem>().Where(x=>x!=null&&x.gameObject.scene.name=="CatalogScene"&&x.infoBlock!=null&&Same(x.infoBlock.path,story)).ToList();
   var items=Catalog.Instance?.items;if(items!=null)for(int i=0;i<items.Count;i++){var x=items[i];if(x!=null&&x.infoBlock!=null&&Same(x.infoBlock.path,story)&&!rows.Any(r=>r.Pointer==x.Pointer))rows.Add(x);}
   var row=rows.OrderByDescending(x=>x.gameObject.activeInHierarchy).ThenByDescending(x=>ParentGroup(x)?.gameObject.activeInHierarchy==true).FirstOrDefault();
   // Catalog rows/details can exist before their native block/path is filled.
   // Wait within the original selection deadline, never invent a load owner.
   if(row==null)return;
   if(!row.gameObject.activeInHierarchy){var group=ParentGroup(row);if(group==null||!group.gameObject.activeInHierarchy)return;if(group.expanded)return;group.Expand();return;}
   selected=true;if(!row.Select(false))throw new InvalidOperationException("Native catalog rejected exact story selection.");return;
  }
  if(++matchingCardFrames<2)return;
  if(mode=="window"){SubmitSelectedStory(card);return;}
  stage=6;ConfirmForegroundAndSubmit();
 }
 [HideFromIl2Cpp] private void ConfirmForegroundAndSubmit()
 {
  var card=Ready()&&RenderControlObservation.Idle(hostType.Assembly)
   ?Object.FindObjectsOfType<CatalogFileInfo>().FirstOrDefault(x=>x!=null&&x.gameObject.activeInHierarchy&&x.gameObject.scene.name=="CatalogScene"&&x.block!=null&&Same(x.block.path,story)):null;
  if(!foreground.PollReadiness(card!=null,ExpectedForeground(),lifetime.Elapsed.TotalSeconds,()=>{
   ActivateForeground("whole-before-prepare",-1);
  }))return;
  SubmitSelectedStory(card!);
 }
 [HideFromIl2Cpp] private void SubmitSelectedStory(CatalogFileInfo card)
 {
  ValidateSource();R.Set(host!,"_selectedStory",card);R.Set(host!,"_selectedStoryKey",story);
  var resolved=R.Call(host!,"ResolveStory")!;if(!Same(R.Text(resolved,"Item2"),story))throw new InvalidOperationException("Original host resolved a different story.");
  render=new RenderControlObservation(hostType.Assembly);render.Begin();
  if(File.Exists(outputPath))throw new IOException("Output already exists.");
  if(mode=="whole")foreground.BeginMeasurement(ExpectedForeground(),lifetime.Elapsed.TotalSeconds);
  submitted=true;stage=4;RecordFocus(Application.isFocused,OwnWindowForeground.ReadForeground(),"prepare-boundary",true);
  prepareTimestamp=Stopwatch.GetTimestamp();elapsed.Restart();R.Call(host!,"Prepare",options);
 }
 [HideFromIl2Cpp] private void Observe()
 {
  double seconds=elapsed.Elapsed.TotalSeconds;
  render!.ObserveOwned();
  var current=R.Get(host!,"_session");if(current!=null&&session==null){session=current;preparedSeconds=seconds;ValidateEffectiveOptions();}
  bool capturing=R.Bool(host!,"Capturing");
  if(capturing&&firstCaptureSeconds<0)firstCaptureSeconds=seconds;
  if(R.Get(host!,"_finalize")!=null&&finalizeSeconds<0)finalizeSeconds=seconds;
  ReadCounters();
  if(passes>1)throw new InvalidOperationException("Benchmark observed more than one authored playback; this is not the single-pass comparison.");
  if(error.Length!=0)throw new InvalidOperationException(error);
  if(mode=="window"&&!cancelled)
  {
   if(result!=null||finalizeSeconds>=0)throw new InvalidOperationException("Story ended before bounded cancellation; requested window is not available.");
   bool wasMeasuring=windowForeground.Measuring;
   windowForeground.ObserveCapture(capturing,counters.Captured,ExpectedForeground(),lifetime.Elapsed.TotalSeconds,
    ()=>ActivateForeground("window-first-actual-capturing",counters.Captured));
   if(!wasMeasuring&&windowForeground.Measuring)RecordFocus(Application.isFocused,OwnWindowForeground.ReadForeground(),"window-frame-60-boundary",true);
   if(window.Observe(counters,seconds))
   {
    windowForeground.CompleteMeasurement(counters.Captured,ExpectedForeground(),lifetime.Elapsed.TotalSeconds);
    RecordFocus(Application.isFocused,OwnWindowForeground.ReadForeground(),"window-frame-360-boundary",true);
    cancelled=true;R.Call(host!,"Cancel");cleanupDeadline=lifetime.Elapsed.TotalSeconds+30;stage=5;
   }
  }
  if(mode=="whole"&&result!=null){elapsed.Stop();cleanupDeadline=lifetime.Elapsed.TotalSeconds+30;stage=5;}
  if(stage==4&&!R.Bool(host!,"Busy")&&result==null)throw new InvalidOperationException("Exporter stopped without successful completion: "+R.Text(host!,"_status"));
 }
 [HideFromIl2Cpp] private void SettleSuccess()
 {
  if(lifetime.Elapsed.TotalSeconds>cleanupDeadline)throw new TimeoutException("Native export cleanup/restoration did not settle.");
  if(!FocusComparable())throw new InvalidOperationException("Foreground contract did not complete successfully; timing is not comparable.");
  if(R.Bool(host!,"Busy")||!Ready())return;
  R.Set(panel!,"Visible",false);if(!render!.Restored())return;
  ReadCounters();ValidateSource();restoredSeconds=(Stopwatch.GetTimestamp()-prepareTimestamp)/(double)Stopwatch.Frequency;
  if(mode=="window")
  {
   if(!cancelled||window.Measurement==null||result!=null||File.Exists(outputPath))throw new InvalidOperationException("Bounded cancellation published a movie or did not measure the requested work.");
   if(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(outputPath)!).Any())throw new InvalidOperationException("Cancelled benchmark left media or temporary output behind.");
  }
  else
  {
   if(result==null||completedSeconds<=0||!File.Exists(outputPath)||!Same(R.Text(result,"OutputPath"),outputPath)||R.Long(result,"Frames")<=0||R.Text(result,"Encoder")!=encoder)throw new InvalidOperationException("Original host did not commit the expected complete output.");
  }
  Finish(true);
 }
 [HideFromIl2Cpp] private void ValidateEffectiveOptions()
 {
  var effective=R.Get(host!,"_options")??throw new InvalidOperationException("Actual host options absent.");
  var sessionOptions=R.Get(session!,"options")??throw new InvalidOperationException("Actual encoder session options absent.");
  foreach(string name in new[]{"Width","Height","Fps","Container","Codec","EncodingMode","Encoder","BitrateKbps","RateControl","AudioQuality","AudioSampleRate","CaptureMode","SuperResolutionEnabled","OutputDirectory","Title","WriteCover"})
   if(!Equals(R.Get(options!,name),R.Get(effective,name))||!Equals(R.Get(options!,name),R.Get(sessionOptions,name)))throw new InvalidOperationException("Actual host/session changed requested option: "+name);
  if(R.Text(session!,"encoder")!=encoder)throw new InvalidOperationException("Actual session encoder differs from real probe selection.");
  effectiveOptions=new{Host=R.Scalars(effective),Session=R.Scalars(sessionOptions)};
 }
 [HideFromIl2Cpp] private void ReadCounters()
 {
  if(session==null)return;
  sessionAudioSamples=R.Long(session,"AudioSampleFrames");
  // Unified capture spools real PCM before copying it into ExportSession at
  // sealing. Observe that original producer without treating zero muxed audio
  // as zero generated audio, and keep its last value after native disposal.
  var raw=R.Get(host!,"_rawAudio",true);if(raw!=null)lastRawAudioSamples=R.Long(raw,"RawSampleFrames");
  counters=new(R.Long(host!,"_captured"),R.Long(session,"FramesAccepted"),R.Long(session,"FramesWritten"),Math.Max(lastRawAudioSamples,sessionAudioSamples));
 }
 [HideFromIl2Cpp] private void Fail(Exception failure)
 {
  if(stage==99)return;error=error.Length==0?failure.ToString():error+"\n"+failure;
  CaptureFailureForeground(OwnWindowForeground.ReadForeground());
  elapsed.Stop();Plugin.LogLine("LEGACY-BENCHMARK FAILURE "+failure.Message);
  try{if(host!=null&&R.Bool(host,"Busy"))R.Call(host,"Cancel");}catch(Exception e){error+="\nCancellation failed: "+e.GetBaseException();}
  cleanupDeadline=lifetime.Elapsed.TotalSeconds+30;stage=90;
 }
 [HideFromIl2Cpp] private void SettleFailure()
 {
  bool idle=host==null||!R.Bool(host,"Busy");
  if(idle&&panel!=null)R.Set(panel,"Visible",false);
  if(idle&&(render==null||render.Restored(false))||lifetime.Elapsed.TotalSeconds>=cleanupDeadline)Finish(false);
 }
 [HideFromIl2Cpp] private void Finish(bool passed)
 {
  stage=99;hooks?.UnpatchSelf();render?.Dispose();
  if(panel!=null){R.Set(panel,"Options",savedOptions);R.Set(panel,"OutputDirectoryCommitted",savedCommit);}
  object? resultData=result==null?null:R.Scalars(result);
  string? movieHash=passed&&mode=="whole"?R.Hash(outputPath):null;
  File.WriteAllText(Path.Combine(Plugin.Output,"legacy-benchmark.json"),R.Json(new{
   Passed=passed,Mode=mode,Failure=error,Identity=identity,RequestedOptions=options==null?null:R.Scalars(options),EffectiveOptions=effectiveOptions,
   UnsupportedLegacyOptions=absentOptions,Encoder=encoder,PassBegins=passes,Counters=counters,Window=window.Measurement,
   SessionAudioSampleFrames=sessionAudioSamples,LastObservedRawAudioSampleFrames=lastRawAudioSamples,AudioCounterSource=lastRawAudioSamples>0?"actual RawAudioPass.RawSampleFrames (includes original primer/tail), with session count retained separately":"actual ExportSession.AudioSampleFrames",
   WholeExportSeconds=mode=="whole"&&completedSeconds>0?(double?)completedSeconds:null,
   PrepareThroughSettledRestorationSeconds=restoredSeconds>0?(double?)restoredSeconds:null,
   WholeExportFramesPerSecond=mode=="whole"&&completedSeconds>0&&result!=null?R.Long(result,"Frames")/completedSeconds:(double?)null,
   PreparationObservedSeconds=preparedSeconds,FirstCaptureObservedSeconds=firstCaptureSeconds,FinalizationObservedSeconds=finalizeSeconds,
   SessionTiming=session==null?null:new{Preflight=R.Get(session,"PreflightDuration"),Encoding=R.Get(session,"EncodingDuration"),Finalization=R.Get(session,"FinalizationDuration")},
   ExpectedFocused=true,ForegroundActivation=foregroundActivation,ForegroundActivationPhase=foregroundActivationPhase,ForegroundActivationCapturedFrame=foregroundActivationFrame,
   ForegroundContract=mode=="window"?windowForeground.Evidence():foreground.Evidence(),WindowComparable=mode=="window"?(bool?)(passed&&FocusComparable()&&window.Measurement!=null):null,
   FailureForegroundWindow=failureForegroundWindow,SetupFocusChanges=setupFocusChanges,
   FocusChanges=focusChanges,FocusTransitionLimit=64,OmittedFocusTransitions=omittedFocusTransitions,StopwatchFrequency=Stopwatch.Frequency,CompletedOutput=resultData,OutputSha256=movieHash,
   RenderControl=render?.Evidence(),SourceSha256After=File.Exists(story)?R.Hash(story):null,
   Scope=mode=="whole"?"Original Prepare entry to original ReportCompleted callback, including preparation, authored playback and finalization; media equivalence is a separate external check.":"Original export cancelled after actual frame 60 through 360 render/encode/audio work. No movie and no whole-export throughput claim."
  }));active=null;Plugin.Finish(passed);
 }
 [HideFromIl2Cpp] private object CreateIdentity(Assembly assembly)
 {
  string executable=Process.GetCurrentProcess().MainModule!.FileName??throw new InvalidOperationException("Host executable location unavailable."),root=Path.GetDirectoryName(executable)!;
  var paths=new List<string>{executable,Path.Combine(root,"GameAssembly.dll"),Path.Combine(root,"UnityPlayer.dll"),assembly.Location,optionType.Assembly.Location,typeof(Plugin).Assembly.Location,story,optionsPath};
  foreach(string name in new[]{"FfmpegPath","FfprobePath"}){var path=R.Text(provider,name,true);if(File.Exists(path))paths.Add(path);}
  var loaded=AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic&&!string.IsNullOrEmpty(a.Location)&&(a.Location.Contains("\\mods\\",StringComparison.OrdinalIgnoreCase)||a.Location.Contains("\\plugins\\",StringComparison.OrdinalIgnoreCase))).OrderBy(a=>a.GetName().Name).ToArray();
  paths.AddRange(loaded.Select(a=>a.Location));string config=Path.Combine(root,"BepInEx","config");if(Directory.Exists(config))paths.AddRange(Directory.EnumerateFiles(config,"*",SearchOption.AllDirectories));
  string settings=Path.Combine(Application.persistentDataPath,"data","settings");if(Directory.Exists(settings))paths.AddRange(Directory.EnumerateFiles(settings,"*",SearchOption.AllDirectories));
  var context=Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_BENCHMARK_CONTEXT_JSON");if(!string.IsNullOrEmpty(context)){if(!Path.IsPathFullyQualified(context)||!File.Exists(context))throw new FileNotFoundException("Explicit benchmark context missing.",context);paths.Add(context);}
  return new{Utc=DateTime.UtcNow,Pid=Environment.ProcessId,Mode=mode,Provider=assembly.FullName,ProviderVersion=R.Get(provider,"Version",true),Core=optionType.Assembly.FullName,Story=story,StorySha256=storyHash,OptionsSha256=optionsHash,
   GraphicsDevice=SystemInfo.graphicsDeviceName,GraphicsApi=SystemInfo.graphicsDeviceType.ToString(),GraphicsVersion=SystemInfo.graphicsDeviceVersion,Screen=new{Screen.width,Screen.height},Audio=new{AudioSettings.outputSampleRate,SpeakerMode=AudioSettings.speakerMode.ToString()},
   LoadedMods=loaded.Select(a=>new{Name=a.GetName().Name,Version=a.GetName().Version?.ToString(),Path=a.Location}).ToArray(),
   Files=paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Select(x=>new{Path=x,Length=new FileInfo(x).Length,Sha256=R.Hash(x)}).ToArray(),
   Requested=R.Scalars(options!),UnsupportedLegacyOptions=absentOptions,GuardContextPath=context};
 }
 [HideFromIl2Cpp] private void ValidateSource()
 {
  string native=ScenarioResourceManager.GetPersistentFilePath("saves",Path.GetFileNameWithoutExtension(story),".aas");
  if(!Same(native,story)||R.Hash(story)!=storyHash)throw new InvalidOperationException("Exact source differs from AA workspace resolution or source hash changed.");
 }
 [HideFromIl2Cpp] private static bool Ready()
 {
  try
  {
   var resources=ScenarioResourceManager.Instance;var characters=CharacterManager.Instance;
   return SceneManager.GetActiveScene().name=="CatalogScene"&&UserSettings.Instance?.data!=null
    &&resources!=null&&resources.AreDbsLoaded&&resources.ExcelDb!=null&&resources.ScenarioCharacterNameQuery!=null&&!resources.Preloading
    &&characters!=null&&!characters.Preloading&&characters.characterHashDict!=null&&characters.characterHashDict.ByteBuffer!=null&&characters.characterHashDict.DataListLength>0
    &&Object.FindObjectsOfType<CatalogFileInfo>().Any(info=>info!=null&&info.gameObject.activeInHierarchy);
  }
  catch{return false;}
 }
 [HideFromIl2Cpp] private static EpisodeGroup? ParentGroup(CatalogFileItem item)
 {
  for(var parent=item.transform.parent;parent!=null;parent=parent.parent){var group=parent.GetComponent<EpisodeGroup>();if(group!=null)return group;}
  return null;
 }
 [HideFromIl2Cpp] private static string RequiredFile(string variable){string? value=Environment.GetEnvironmentVariable(variable);if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||!File.Exists(value))throw new FileNotFoundException(variable+" must name an existing absolute file.",value);return Path.GetFullPath(value);}
 [HideFromIl2Cpp] private static bool Same(string? a,string? b)=>BenchmarkContract.SamePath(a,b);
 [HideFromIl2Cpp] private static void OnFailure(object __instance,Exception __0){if(active?.host==__instance)active.error=__0.ToString();}
 [HideFromIl2Cpp] private void ObserveFocus(bool focused,string source)
 {
  var native=OwnWindowForeground.ReadForeground();
  RecordFocus(focused,native,source,false);
  try
  {
   bool expected=BenchmarkContract.ExpectedForeground(focused,native.IsOwn);
   if(mode=="window")windowForeground.ObserveFocus(expected,lifetime.Elapsed.TotalSeconds);
   else if(foreground.Measuring)foreground.ObserveMeasurement(expected,lifetime.Elapsed.TotalSeconds);
  }
  catch{CaptureFailureForeground(native);throw;}
 }
 [HideFromIl2Cpp] private static bool ExpectedForeground()=>BenchmarkContract.ExpectedForeground(Application.isFocused,OwnWindowForeground.IsOwnForeground());
 [HideFromIl2Cpp] private void RecordFocus(bool focused,ForegroundWindowState native,string source,bool force)
 {
  if(!force&&focusRecorded&&unityFocus==focused&&nativeFocus==native.IsOwn&&lastForegroundWindow==native)return;
  unityFocus=focused;nativeFocus=native.IsOwn;focus=BenchmarkContract.ExpectedForeground(focused,native.IsOwn);focusRecorded=true;lastForegroundWindow=native;
  if(focusChanges.Count>=64){omittedFocusTransitions++;return;}
  string phase=FocusPhase();
  var change=new{Seconds=submitted?(double?)elapsed.Elapsed.TotalSeconds:null,LifetimeSeconds=lifetime.Elapsed.TotalSeconds,
   Focused=focus,UnityFocused=focused,NativeForegroundOwnProcess=native.IsOwn,NativeWindow=OwnWindowForeground.DescribeForeground(native),
   Measured=mode=="window"?windowForeground.Measuring:foreground.Measuring,Phase=phase,CapturedFrame=counters.Captured,Source=source};
  focusChanges.Add(change);
  if(phase is "catalog-setup" or "export-preparation" or "capture-warmup" or "whole-focus-acquisition")setupFocusChanges.Add(change);
 }
 [HideFromIl2Cpp] private bool FocusComparable()=>mode=="window"
  ?windowForeground.Completed&&!windowForeground.FocusLost&&windowForeground.Failure.Length==0
  :foreground.Completed&&!foreground.FocusLost&&foreground.Failure.Length==0;
 [HideFromIl2Cpp] private string FocusPhase()=>stage==90?"failure-restoration":mode=="window"
  ?windowForeground.Completed?"restoration":windowForeground.Measuring?"window-60-through-360":windowForeground.ActivationRequested?"capture-warmup":submitted?"export-preparation":"catalog-setup"
  :foreground.Completed?"restoration":foreground.Measuring?"whole-export":submitted?"export-preparation":"whole-focus-acquisition";
 [HideFromIl2Cpp] private void ActivateForeground(string phase,long capturedFrame)
 {
  foregroundActivationPhase=phase;foregroundActivationFrame=capturedFrame;
  foregroundActivation=ownWindow.ActivateOnce();
  Plugin.LogLine("LEGACY-BENCHMARK FOREGROUND "+R.Json(new{Phase=phase,CapturedFrame=capturedFrame,Activation=foregroundActivation}));
 }
 [HideFromIl2Cpp] private void CaptureFailureForeground(ForegroundWindowState native)
 {
  if(failureForegroundWindow!=null)return;
  failureForegroundWindow=OwnWindowForeground.DescribeForeground(native);
  Plugin.LogLine("LEGACY-BENCHMARK FAILURE-FOREGROUND "+R.Json(failureForegroundWindow));
 }
 [HideFromIl2Cpp] private static void OnCompleted(object __instance,object __0)
 {
  if(active?.host!=__instance)return;
  // Preserve the original timer boundary before focus validation/receipt work.
  active.completedSeconds=active.elapsed.Elapsed.TotalSeconds;active.result=__0;
  try{active.ObserveFocus(Application.isFocused,"completed-callback");if(active.mode=="whole")active.foreground.CompleteMeasurement(ExpectedForeground(),active.lifetime.Elapsed.TotalSeconds);}
  catch(Exception error){active.CaptureFailureForeground(OwnWindowForeground.ReadForeground());active.foregroundError=error.GetBaseException().Message;active.error=active.foregroundError;}
 }
 [HideFromIl2Cpp] private static void OnPass(object __instance){if(active?.host==__instance&&active.submitted)active.passes++;}
}
