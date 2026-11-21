using System.Reflection;
using System.Runtime.Loader;
using AAVideoExport.Plugin;
using HarmonyLib;

internal static class EffectsSourceCallbackChecks
{
    internal static int Run()
    {
        const string root = @"F:\AzureArchive_101";
        string effects = Path.Combine(root, @"mods\更多的画面效果\1.6.7");
        string lib = Path.Combine(root, @"mods\RukariLib\0.4.6");
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string directory in new[] { effects, lib, Path.Combine(root, "BepInEx/core"), Path.Combine(root, "BepInEx/interop") })
            { string path = Path.Combine(directory, name.Name + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        var harmony = new Harmony("aave.test.effects-source-projection");
        IDisposable? service = null;
        int passed = 0, failed = 0;
        void Test(string name, Action run)
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
        }
        void Reject(Action run) { try { run(); } catch (Exception error) when (error.GetBaseException() is AAVideoExport.Core.ExportException or NotSupportedException) { return; } throw new Exception("Unapproved callback was accepted."); }
        try
        {
            foreach (string name in new[] { "AzureArchive.VideoTools.Core", "AzureArchive.VideoTools.Formats", "Rukari.MoreEffects" })
                _ = Assembly.LoadFrom(Path.Combine(effects, name + ".dll"));
            var library = Assembly.LoadFrom(Path.Combine(lib, "Rukari.Lib.dll"));
            Type type = library.GetType("Rukari.Lib.Commands.EmbeddedDirectiveService", true)!;
            service = (IDisposable)Activator.CreateInstance(type, (Func<bool>)(() => true), (Func<bool>)(() => false), (Action<string>)(_ => { }))!;
            object sanitizer = type.GetMethod("CaptureSanitizer")!.Invoke(service, null)!;
            MethodInfo method = sanitizer.GetType().GetMethod("Sanitize")!;
            // Match the real caller's interface ldvirtftn dispatch, rather
            // than constructing a delegate only from the concrete type.
            MethodInfo contract = library.GetType("Rukari.Lib.Commands.IEmbeddedDirectiveSanitizer", true)!.GetMethod("Sanitize")!;
            var projection = (Func<string, string>)contract.CreateDelegate(typeof(Func<string, string>), sanitizer);
            var bridge = new EffectsSourcePreparation();
            MethodInfo validate = typeof(EffectsSourcePreparation).GetMethod("ValidateCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Validate(Delegate callback) => validate.Invoke(bridge, new object[] { callback });
            Test("real shared immutable sanitizer callback is accepted by exact physical method identity", () =>
            {
                Validate(projection);
                if (projection("native script remains intact") != "native script remains intact") throw new Exception("Original callback was replaced or changed.");
            });
            Test("another method in the same pinned shared library does not receive broad trust", () =>
            {
                var liveService = type.GetMethod("Sanitize")!.CreateDelegate(typeof(Func<string, string>), service);
                Reject(() => Validate(liveService));
            });
            Test("foreign patch on the actual sanitizer prevents callback acceptance", () =>
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(EffectsSourceCallbackChecks), nameof(Noop)));
                try { Reject(() => Validate(projection)); }
                finally { harmony.UnpatchSelf(); }
            });
            Test("multicast projection cannot conceal an additional callback", () => Reject(() => Validate(projection + projection)));
        }
        finally { harmony.UnpatchSelf(); service?.Dispose(); AssemblyLoadContext.Default.Resolving -= Resolve; }
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; actual shared service callback only; no AA or Unity startup");
        return failed == 0 ? 0 : 1;
    }
    public static void Noop() { }
}
