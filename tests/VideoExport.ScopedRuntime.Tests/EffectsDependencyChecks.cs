using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using AAVideoExport.Plugin;
using HarmonyLib;

internal static class EffectsDependencyChecks
{
    internal static int Run(string[] args)
    {
        string scenario = args[0], source = Path.GetFullPath(args[1]);
        string directory = Path.Combine(Path.GetTempPath(), "aave-effects-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var harmony = new Harmony("aave.test.effects-preload");
        try
        {
            string core = Path.Combine(directory, "AzureArchive.VideoTools.Core.dll");
            string formats = Path.Combine(directory, "AzureArchive.VideoTools.Formats.dll");
            File.Copy(Path.Combine(source, Path.GetFileName(core)), core);
            if (scenario != "missing") File.Copy(Path.Combine(source, Path.GetFileName(formats)), formats);
            if (scenario == "corrupt")
            {
                var bytes = File.ReadAllBytes(formats); bytes[^1] ^= 1; File.WriteAllBytes(formats, bytes);
            }
            AssemblyLoadContext.Default.LoadFromAssemblyPath(core);
            RuntimeModBoundaries.Initialize(harmony, Console.WriteLine);
            Fixture.Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "AzureArchive.VideoTools.Formats"),
                "ordinary adapter initialization unexpectedly loaded the optional serializer");
            if (scenario == "foreign-loaded")
                AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AzureArchive.VideoTools.Formats"), AssemblyBuilderAccess.Run);
            Action prepare = () => Fixture.Call(typeof(RuntimeModBoundaries), "PrepareExportDependencies");
            if (scenario == "valid")
            {
                prepare(); prepare();
                var formatsAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AzureArchive.VideoTools.Formats");
                Fixture.Check(formatsAssembly.Location == formats, "export preflight did not load the exact packaged dependency");
                var validated = (HashSet<Assembly>)Fixture.Get(typeof(RuntimeModBoundaries), "Validated")!;
                Fixture.Check(validated.Contains(formatsAssembly), "preloaded dependency never passed its original build validator");
            }
            else
            {
                Fixture.Reject(prepare);
                Fixture.Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "AzureArchive.VideoTools.Formats" && !a.IsDynamic),
                    "invalid dependency was materialized before rejection");
            }
            Console.WriteLine("PASS effects-preload " + scenario); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL effects-preload " + scenario + ": " + error); return 1; }
        finally
        {
            RuntimeModBoundaries.Shutdown(); harmony.UnpatchSelf();
            try { Directory.Delete(directory, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.WriteLine("Retained loaded fixture for caller cleanup after process exit: " + directory); }
        }
    }
}
