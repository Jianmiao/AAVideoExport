using AAVideoExport.RuntimeBootstrap;
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_RECIPES","1");
Environment.SetEnvironmentVariable("AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION","1");
new RuntimeAttributionPatcher().Initialize();
#if AAVE_DEVELOPMENT_AUDIT
bool expected=true;
#else
bool expected=false;
#endif
bool actual=StartupConfigSnapshot.Calls!=0||StartupTrace.Calls!=0||RuntimeAttributionV1.Calls!=0||RuntimeRecipeV2.Calls!=0;
if(actual!=expected){Console.WriteLine("FAIL production bootstrap executed audit initialization under ambient flags.");return 1;}
Console.WriteLine("PASS source-linked bootstrap initialization audit="+actual+"; ambient flags cannot enable a production build.");return 0;
namespace BepInEx{public static class Paths{public static string GameRootPath=>"synthetic-no-files";}}
namespace BepInEx.Preloader.Core.Patching{
    [AttributeUsage(AttributeTargets.Class)]public sealed class PatcherPluginInfoAttribute:Attribute{public PatcherPluginInfoAttribute(string id,string name,string version){}}
    public class BasePatcher{protected Logger Log=new();public virtual void Initialize(){}}
    public sealed class Logger{public void LogInfo(string value){}public void LogWarning(string value){}}
}
namespace AAVideoExport.RuntimeBootstrap{
    public static class StartupConfigSnapshot{public static int Calls;public static void Capture(string path)=>Calls++;}
    public static class StartupTrace{public static int Calls;public static void Write(string name,string? detail=null)=>Calls++;}
    public static class RuntimeAttributionV1{public static int Calls;public static bool WasRequested=>true;public static bool IsInstalled=>true;public static void InitializeFromEnvironment()=>Calls++;}
    public static class RuntimeRecipeV2{public static int Calls;public static void InitializeFromEnvironment()=>Calls++;}
}
