using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
namespace AAVideoExport.LegacyBenchmark.Tests;

// Observe the installed Azurite consumer. Never acquire a synthetic lease or
// apply an FPS setting: root's guarded launcher owns global setting restoration.
internal sealed class RenderControlObservation:IDisposable
{
 private readonly Type control;
 private object plugin=null!,host=null!,fps=null!;
 private readonly List<object> events=new();
 private readonly List<string> errors=new();
 private int acquired,released,thread;
 private bool owned;
 private RenderState baseline=null!,acquireBaseline=null!;
 internal RenderControlObservation(Assembly provider){control=provider.GetType("AAVideoExport.Integration.RenderControlV1",true)!;}
 internal static bool Idle(Assembly provider)
 {
  var control=provider.GetType("AAVideoExport.Integration.RenderControlV1",true)!;
  if(!R.Bool(control,"IsReady")||R.Bool(control,"IsRenderingOwned"))return false;
  var a=AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(x=>x.GetName().Name=="Azurite")??throw new InvalidOperationException("The actual Azurite consumer is not loaded.");
  var plugin=R.Get(a.GetType("Azurite.Plugin",true)!,"_current");if(plugin==null)return false;
  var host=R.Get(plugin,"_host");var fps=R.Get(plugin,"_fps");
  return host!=null&&fps!=null&&R.Bool(host,"HostSupported")&&R.Bool(host,"HasPublicRenderControl")&&R.Bool(fps,"IsOperational")&&R.Bool(fps,"IsMappingApplied")&&!R.Bool(plugin,"_exportSuspended")&&!R.Bool(fps,"_exportSuspended");
 }
 internal void Begin()
 {
  if(!Idle(control.Assembly)||R.Long(control,"ProtocolVersion")!=1)throw new InvalidOperationException("Actual Azurite v1 binding and mapping are not ready.");
  var a=AppDomain.CurrentDomain.GetAssemblies().Single(x=>x.GetName().Name=="Azurite");plugin=R.Get(a.GetType("Azurite.Plugin",true)!,"_current")!;host=R.Get(plugin,"_host")!;fps=R.Get(plugin,"_fps")!;
  thread=Environment.CurrentManagedThreadId;baseline=State();
  control.GetEvent("BeforeAcquire",R.Flags)!.AddEventHandler(null,(Action)BeforeAcquire);control.GetEvent("AfterRelease",R.Flags)!.AddEventHandler(null,(Action)AfterRelease);Record("before-export");
 }
 internal void ObserveOwned()
 {
  if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
  if(!R.Bool(control,"IsRenderingOwned"))return;
  Require(acquired>released&&R.Bool(plugin,"_exportSuspended")&&R.Bool(fps,"_exportSuspended")&&!R.Bool(fps,"IsMappingApplied")&&R.Bool(host,"PublicRenderingBlocked"),"Installed Azurite did not stay suspended during export.");
  if(!owned){owned=true;Record("owned-export");}
 }
 internal bool Restored(bool requireWork=true)
 {
  if(baseline==null)return !R.Bool(control,"IsRenderingOwned");
  if(errors.Count>0&&requireWork)throw new InvalidOperationException(string.Join("; ",errors));
  bool restored=acquired==released&&(!requireWork||acquired>0&&owned)&&Idle(control.Assembly)&&State()==baseline;
  if(restored&&!events.Any(x=>R.Text(x,"Kind")=="settled-restoration"))Record("settled-restoration");return restored;
 }
 private void BeforeAcquire()=>Guard(()=>{
  Require(Environment.CurrentManagedThreadId==thread,"Render acquire is off the main thread.");
  Require(R.Bool(plugin,"_exportSuspended")&&R.Bool(fps,"_exportSuspended")&&!R.Bool(fps,"IsMappingApplied"),"Azurite did not suspend its mapping before acquire.");
  acquireBaseline=State();acquired++;Record("before-acquire");
 });
 private void AfterRelease()=>Guard(()=>{
  Require(Environment.CurrentManagedThreadId==thread&&acquired>released&&!R.Bool(control,"IsRenderingOwned"),"Unbalanced render release.");
  var current=State();Require(acquireBaseline!=null&&current.CaptureDelta==acquireBaseline.CaptureDelta&&current.TimeScale==acquireBaseline.TimeScale&&current.RenderInterval==acquireBaseline.RenderInterval&&current.RunInBackground==acquireBaseline.RunInBackground,"Unity capture state was not restored before public release.");
  released++;Record("after-release");
 });
 private void Record(string kind)=>events.Add(new{Kind=kind,Acquired=acquired,Released=released,Thread=Environment.CurrentManagedThreadId,State=State(),Supported=R.Bool(host,"HostSupported"),PublicBound=R.Bool(host,"HasPublicRenderControl"),Mapped=R.Bool(fps,"IsMappingApplied"),Operational=R.Bool(fps,"IsOperational"),PluginSuspended=R.Bool(plugin,"_exportSuspended"),FpsSuspended=R.Bool(fps,"_exportSuspended"),Owned=R.Bool(control,"IsRenderingOwned")});
 private static RenderState State()=>new(Application.targetFrameRate,QualitySettings.vSyncCount,UserSettings.Instance.fpsTier,Time.captureDeltaTime,Time.timeScale,OnDemandRendering.renderFrameInterval,Application.runInBackground);
 internal object Evidence()=>new{Acquired=acquired,Released=released,ObservedOwnedWork=owned,Events=events,Errors=errors};
 private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
 private void Guard(Action action){try{action();}catch(Exception e){errors.Add(e.GetBaseException().Message);}}
 public void Dispose(){control.GetEvent("BeforeAcquire",R.Flags)!.RemoveEventHandler(null,(Action)BeforeAcquire);control.GetEvent("AfterRelease",R.Flags)!.RemoveEventHandler(null,(Action)AfterRelease);}
 private sealed record RenderState(int Fps,int Vsync,int Tier,float CaptureDelta,float TimeScale,int RenderInterval,bool RunInBackground);
}
