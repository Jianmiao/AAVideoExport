using AAVideoExport.Plugin;
using AAVideoExport.RuntimeBootstrap;
int passed=0,failed=0;
void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.Message);}}
Test("one thousand idle ticks perform bounded probes and report one unchanged failure",()=>
{
    var schedule=new RetainedInstallSchedule();int probes=0,attempts=0,reports=0;
    for(int tick=0;tick<1000;tick++){double now=tick/1000d;if(!schedule.ShouldProbe(now))continue;probes++;if(!schedule.ShouldAttempt(now,"same-config-and-assembly"))continue;attempts++;if(schedule.Failed(now,"same failure"))reports++;}
    Check(probes<=2&&attempts==1&&reports==1,$"unbounded idle work: probes={probes},attempts={attempts},reports={reports}");
});
Test("stable success does not repeatedly reinstall or rehash the same payload",()=>
{
    var schedule=new RetainedInstallSchedule();Check(schedule.ShouldAttempt(0,"state-a"),"initial install suppressed");schedule.Succeeded();
    for(int tick=1;tick<=1000;tick++)Check(!schedule.ShouldAttempt(tick,"state-a"),"stable install was repeated");
    Check(schedule.ShouldAttempt(1001,"state-b"),"real state change was ignored");
});
Test("unchanged failure retries with bounded backoff but does not spam diagnostics",()=>
{
    var schedule=new RetainedInstallSchedule();int attempts=0,reports=0;
    for(int second=0;second<120;second++){if(!schedule.ShouldAttempt(second,"state-a"))continue;attempts++;if(schedule.Failed(second,"same"))reports++;}
    Check(attempts<=10&&attempts>=4&&reports==1,$"retry/report flood: {attempts}/{reports}");
    Check(schedule.ShouldAttempt(120,"actual-config-change"),"changed state did not enable retry");
});
string root=Path.Combine(Path.GetTempPath(),"aave-menu-startup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    string name="可更改的主菜单音乐",version="1.7.3",mods=Path.Combine(root,"mods"),src=Path.Combine(mods,name,version),cfg=Path.Combine(root,"profiles","Default","configs","foxxlight.azurearchive.menubgmselector.cfg");
    Directory.CreateDirectory(src);Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);File.WriteAllText(Path.Combine(root,"ActiveProfile.txt"),"Default");string dll=Path.Combine(src,"MenuBgmSelector.dll");File.WriteAllBytes(dll,new byte[]{1});
    void Write(string value)=>File.WriteAllText(cfg,"# startup comments\r\n[Name]\r\nDisplayName = "+value+"\r\n");
    MenuStartupConfigEvidence Snapshot(){var s=StartupConfigSnapshot.Inspect(root,()=>false);Check(s.Available,s.Reason);return new(s.Available,s.BeforeMenuAssemblyLoad,s.ConfigPath,s.Exists,s.RawSha256,s.HasDisplayName,s.DisplayName);}
    void Validate(MenuStartupConfigEvidence initial,string current,string bound,string? directory=null)=>MenuStartupNameProof.Validate(initial,cfg,dll,directory??src,mods,version,name,bound,true,current,DateTime.UtcNow.AddHours(-1));
    Test("startup Bind equivalent rewrite does not become a rename merely because mtime is newer",()=>{Write(name);var initial=Snapshot();File.WriteAllText(cfg,"# BepInEx regenerated header\n[Name]\nDisplayName="+name+"\n");File.SetLastWriteTimeUtc(cfg,DateTime.UtcNow);Validate(initial,name,name);});
    Test("initial rename request stays rejected even if current file changes back",()=>{Write("OtherName");var initial=Snapshot();Write(name);File.SetLastWriteTimeUtc(cfg,DateTime.UtcNow.AddHours(-2));Reject(()=>Validate(initial,name,name));});
    Test("current real rename request remains rejected",()=>{Write(name);var initial=Snapshot();Write("OtherName");Reject(()=>Validate(initial,"OtherName","OtherName"));});
    Test("runtime install metadata must match the actual loaded DLL directory",()=>{Write(name);var initial=Snapshot();File.SetLastWriteTimeUtc(cfg,DateTime.UtcNow.AddHours(-2));Reject(()=>Validate(initial,name,name,Path.Combine(root,"wrong")));});
    Test("missing early evidence never silently authorizes a rename boundary",()=>{Write(name);var initial=Snapshot() with{Available=false,BeforeMenuAssemblyLoad=false};File.SetLastWriteTimeUtc(cfg,DateTime.UtcNow.AddHours(-2));Reject(()=>Validate(initial,name,name));});
    Test("startup capture rejects already loaded menu or ambiguous profile and name",()=>{Write(name);Check(!StartupConfigSnapshot.Inspect(root,()=>true).Available,"late snapshot accepted");File.WriteAllText(Path.Combine(root,"ActiveProfile.txt"),"Default\nOther");Check(!StartupConfigSnapshot.Inspect(root,()=>false).Available,"ambiguous profile accepted");File.WriteAllText(Path.Combine(root,"ActiveProfile.txt"),"Default");File.WriteAllText(cfg,"[Name]\nDisplayName=a\nDisplayName=b\n");Check(!StartupConfigSnapshot.Inspect(root,()=>false).Available,"duplicate name accepted");});
}
finally{Directory.Delete(root,true);}
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");return failed==0?0:1;
static void Check(bool value,string why){if(!value)throw new Exception(why);}
static void Reject(Action action){try{action();}catch{return;}throw new Exception("unknown/changed rename context was accepted");}
