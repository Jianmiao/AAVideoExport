namespace AAVideoExport.LegacyBenchmark.Tests;
internal sealed record Counters(long Captured,long Accepted,long Written,long AudioSamples);
internal sealed record WindowMeasurement(long StartFrame,long StopFrame,long Frames,long AcceptedFrames,long WrittenFrames,long AudioSamples,double Seconds,double FramesPerSecond);
internal static class BenchmarkContract
{
 internal static string ParseMode(string? mode)=>mode is "whole" or "window" ? mode : throw new InvalidOperationException("AA_VIDEOEXPORT_BENCHMARK_MODE must be whole or window.");
 internal static bool ExpectedForeground(bool unityFocused,bool nativeForegroundOwnProcess)=>unityFocused&&nativeForegroundOwnProcess;
 internal static bool SamePath(string? first,string? second)
 {
  if(string.IsNullOrWhiteSpace(first)||string.IsNullOrWhiteSpace(second))return false;
  try{return string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase);}
  catch(ArgumentException){return false;}
  catch(NotSupportedException){return false;}
  catch(PathTooLongException){return false;}
 }
}
internal sealed class BenchmarkWindow
{
 internal WindowMeasurement? Measurement {get;private set;}
 private Counters? start,previous;
 private double startSeconds,previousSeconds=-1;
 internal bool Observe(Counters counters,double seconds)
 {
  if(Measurement!=null)return true;
  if(!double.IsFinite(seconds)||seconds<0||seconds<previousSeconds||previous!=null&&(counters.Captured<previous.Captured||counters.Accepted<previous.Accepted||counters.Written<previous.Written||counters.AudioSamples<previous.AudioSamples))
   throw new InvalidOperationException("Benchmark counters or monotonic clock moved backwards.");
  if(counters.Captured<0||counters.Accepted<0||counters.Written<0||counters.Written>counters.Accepted||counters.Accepted>counters.Captured)
   throw new InvalidOperationException("Actual exporter counters are inconsistent.");
  previous=counters;previousSeconds=seconds;
  if(counters.Captured<60)return false;
  if(start==null){if(counters.Captured>=360)throw new InvalidOperationException("Missed the entire frame 60 through 360 interval.");start=counters;startSeconds=seconds;}
  if(counters.Captured<360)return false;
  double elapsed=seconds-startSeconds;long frames=counters.Captured-start.Captured,accepted=counters.Accepted-start.Accepted,written=counters.Written-start.Written,audio=counters.AudioSamples-start.AudioSamples;
  if(elapsed<=0||frames<=0||accepted<=0||written<=0||audio<=0)throw new InvalidOperationException("Window did not observe actual render, encode and audio work.");
  Measurement=new(start.Captured,counters.Captured,frames,accepted,written,audio,elapsed,frames/elapsed);return true;
 }
}

// Test-only readiness contract, kept independent of Unity/Win32 for regression checks.
internal sealed class BenchmarkForeground
{
 private const double AcquisitionSeconds=5;
 private double activationSeconds=-1,confirmedSeconds=-1,measurementSeconds=-1,completedSeconds=-1,previousSeconds=-1;
 internal bool ActivationRequested {get;private set;}
 internal bool Confirmed {get;private set;}
 internal bool Measuring {get;private set;}
 internal bool Completed {get;private set;}
 internal bool FocusLost {get;private set;}
 internal string Failure {get;private set;}="";
 internal bool PollReadiness(bool nativeReady,bool focused,double seconds,Action activate)
 {
  ObserveTime(seconds);RequireNotFailed();
  if(Measuring||Completed)Reject("Foreground readiness cannot be restarted after measurement begins.");
  if(!ActivationRequested)
  {
   if(!nativeReady)return false;
   ActivationRequested=true;activationSeconds=seconds;
   try{activate();}catch(Exception error){Reject("The one own-window activation failed: "+error.Message);}
   // Confirm actual Unity focus in a subsequent update, not the stale value
   // sampled before ShowWindow/SetForegroundWindow made their single attempt.
   return false;
  }
  if(seconds-activationSeconds>=AcquisitionSeconds)Reject("The own AA window did not establish native readiness and foreground focus within 5 seconds; no timed export was started.");
  if(!nativeReady||!focused)return false;
  Confirmed=true;confirmedSeconds=seconds;return true;
 }
 internal void BeginMeasurement(bool focused,double seconds)
 {
  ObserveTime(seconds);RequireNotFailed();
  if(!Confirmed||!focused||Measuring||Completed)Reject("Foreground focus was not confirmed at the actual timed Prepare boundary.");
  Measuring=true;measurementSeconds=seconds;
 }
 internal void ObserveMeasurement(bool focused,double seconds)
 {
  ObserveTime(seconds);RequireNotFailed();
  if(!Measuring||focused)return;
  FocusLost=true;Reject("AA lost foreground focus during the measured export; this timing is not comparable.");
 }
 internal void CompleteMeasurement(bool focused,double seconds)
 {
  ObserveMeasurement(focused,seconds);
  if(!Measuring)Reject("A foreground measurement must be active at its actual completion boundary.");
  Measuring=false;Completed=true;completedSeconds=seconds;
 }
 internal object Evidence()=>new{ExpectedFocused=true,ActivationRequested,ActivationAttemptLimit=1,AcquisitionTimeoutSeconds=AcquisitionSeconds,
  Confirmed,Measuring,Completed,FocusLost,Failure,ActivationLifetimeSeconds=activationSeconds,ConfirmedLifetimeSeconds=confirmedSeconds,
  MeasurementLifetimeSeconds=measurementSeconds,CompletionLifetimeSeconds=completedSeconds,Comparable=Completed&&!FocusLost&&Failure.Length==0};
 private void ObserveTime(double seconds)
 {
  if(!double.IsFinite(seconds)||seconds<0||seconds<previousSeconds)Reject("Foreground observation requires a monotonic lifetime clock.");
  previousSeconds=seconds;
 }
 private void RequireNotFailed(){if(Failure.Length!=0)throw new InvalidOperationException(Failure);}
 private void Reject(string reason){if(Failure.Length==0)Failure=reason;Measuring=false;throw new InvalidOperationException(Failure);}
}

internal sealed class BenchmarkWindowForeground
{
 private long previousFrame=-1,activationFrame=-1,confirmedFrame=-1,startFrame=-1,stopFrame=-1;
 private double previousSeconds=-1,activationSeconds=-1,confirmedSeconds=-1,startSeconds=-1,stopSeconds=-1;
 internal bool ActivationRequested {get;private set;}
 internal bool Confirmed {get;private set;}
 internal bool Measuring {get;private set;}
 internal bool Completed {get;private set;}
 internal bool FocusLost {get;private set;}
 internal string Failure {get;private set;}="";
 internal void ObserveCapture(bool capturing,long captured,bool focused,double seconds,Action activate)
 {
  ObserveTime(seconds);RequireNotFailed();
  if(captured<0||captured<previousFrame)Reject("Window foreground observation saw a backwards native capture counter.");
  previousFrame=captured;
  if(Completed)return;
  if(Measuring){ObserveFocus(focused,seconds);return;}
  if(!capturing)
  {
   if(captured>=60)Reject("The fixed frame-60 window began without confirmed native capture/focus readiness.");
   return;
  }
  if(!ActivationRequested)
  {
   if(captured>=60)Reject("The own-window activation opportunity before frame 60 was missed; the benchmark window will not shift.");
   ActivationRequested=true;activationFrame=captured;activationSeconds=seconds;
   try{activate();}catch(Exception error){Reject("The one capture-window activation failed: "+error.Message);}
   // The fresh scene HWND is activated once during excluded warmup. Never use
   // the stale focus sample from before activation as confirmation.
   return;
  }
  if(focused&&!Confirmed){Confirmed=true;confirmedFrame=captured;confirmedSeconds=seconds;}
  if(captured<60)return;
  if(captured!=60)Reject("The actual frame-60 boundary was missed; the foreground benchmark will not shift its interval.");
  if(!Confirmed||!focused)Reject("Foreground focus was not confirmed by actual frame 60; the window is not comparable.");
  Measuring=true;startFrame=captured;startSeconds=seconds;
 }
 internal void ObserveFocus(bool focused,double seconds)
 {
  if(!Measuring)return;
  ObserveTime(seconds);RequireNotFailed();
  if(focused)return;
  FocusLost=true;Reject("AA lost foreground focus inside the actual frame 60 through 360 interval; this window is not comparable.");
 }
 internal void CompleteMeasurement(long captured,bool focused,double seconds)
 {
  ObserveTime(seconds);RequireNotFailed();ObserveFocus(focused,seconds);
  if(!Measuring||captured!=360)Reject("The foreground window must complete at the actual frame-360 boundary.");
  Measuring=false;Completed=true;stopFrame=captured;stopSeconds=seconds;
 }
 internal object Evidence()=>new{ExpectedFocused=true,Scope="Only actual captured frame 60 through 360; preparation and warmup excluded",
  ActivationPhase="first-actual-capturing-before-frame-60",ActivationRequested,ActivationAttemptLimit=1,ConfirmationDeadlineFrame=60,
  Confirmed,Measuring,Completed,FocusLost,Failure,ActivationFrame=activationFrame,ConfirmedFrame=confirmedFrame,
  MeasurementStartFrame=startFrame,MeasurementStopFrame=stopFrame,ActivationLifetimeSeconds=activationSeconds,
  ConfirmedLifetimeSeconds=confirmedSeconds,MeasurementLifetimeSeconds=startSeconds,CompletionLifetimeSeconds=stopSeconds,
  Comparable=Completed&&!FocusLost&&Failure.Length==0&&startFrame==60&&stopFrame==360};
 private void ObserveTime(double seconds)
 {
  if(!double.IsFinite(seconds)||seconds<0||seconds<previousSeconds)Reject("Window foreground observation requires a monotonic lifetime clock.");
  previousSeconds=seconds;
 }
 private void RequireNotFailed(){if(Failure.Length!=0)throw new InvalidOperationException(Failure);}
 private void Reject(string reason){if(Failure.Length==0)Failure=reason;Measuring=false;throw new InvalidOperationException(Failure);}
}
