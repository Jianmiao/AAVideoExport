using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using AAVideoExport.RuntimeBootstrap;

internal static class WebRequestManifestChecks
{
    private const string Root = @"F:\AzureArchive_101";
    private const string Lab = @"E:\aamod\AAVideoExport-0.2.4-source\artifacts\native-lab-20261029-bootstrap";
    private static readonly (string Name, string Sha256, string Mvid)[] Expected = {
        ("UnityEngine.UnityWebRequestModule", "C7082A228C543B3869F2C233590AC0CAE6243F2F1FEF5D291812FE1337B37F2D", "05d528e2-c427-4835-b58e-dce0de4ccfb9"),
        ("UnityEngine.UnityWebRequestAudioModule", "157F9E1585767DA1FF998D135DA7E1D23C3A22B29ED3590106226AA9C7527D0C", "674424bd-ea1f-4bf2-9971-6843edf6291f")
    };
    internal static int Run(string evidencePath)
    {
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            foreach (string folder in new[] { "BepInEx/interop", "BepInEx/core" })
            { string path = Path.Combine(Root, folder, (name.Name ?? "") + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        int passed = 0, failed = 0;
        var proofs = new List<object>(); var tests = new List<object>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Test(string name, Action run)
        {
            try { run(); passed++; tests.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; tests.Add(new { Name = name, Passed = false, Error = error.GetBaseException().Message }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
        }
        void Reject(Action run, string message) { try { run(); } catch (NotSupportedException) { return; } throw new Exception(message); }
        try
        {
            var policy = InteropRecipePolicy.ForTests();
            var requestAssembly = Assembly.LoadFrom(Path.Combine(Root, "BepInEx/interop/UnityEngine.UnityWebRequestModule.dll"));
            var audioAssembly = Assembly.LoadFrom(Path.Combine(Root, "BepInEx/interop/UnityEngine.UnityWebRequestAudioModule.dll"));
            var request = requestAssembly.GetType("UnityEngine.Networking.UnityWebRequest", true)!;
            var operation = requestAssembly.GetType("UnityEngine.Networking.UnityWebRequestAsyncOperation", true)!;
            var clip = audioAssembly.GetType("UnityEngine.Networking.DownloadHandlerAudioClip", true)!;
            var multimedia = audioAssembly.GetType("UnityEngine.Networking.UnityWebRequestMultimedia", true)!;
            // All inspection below is metadata-only. Do not invoke any of
            // these native wrapper factories, cctors, constructors or methods.
            Test("two explicit resource-loader image pins match F101 and native lab bytes and metadata", () =>
            {
                Check(InteropImageManifest.Count == 22, "Expected the bounded 20+2 image manifest.");
                foreach (var expected in Expected)
                {
                    Check(InteropImageManifest.TryGet(expected.Name, out var pin), "Missing explicit resource image: " + expected.Name);
                    Check(pin!.Sha256 == expected.Sha256 && pin.Mvid == expected.Mvid && pin.Version == "0.0.0.0", "Frozen resource image pin differs.");
                    foreach (string root in new[] { Root, Lab })
                    {
                        string path = Path.Combine(root, "BepInEx/interop", expected.Name + ".dll");
                        using var stream = File.OpenRead(path); using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
                        Check(InteropRecipePolicy.HashFile(path) == expected.Sha256 && metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString() == expected.Mvid
                            && metadata.GetAssemblyDefinition().Version.ToString() == "0.0.0.0", "Actual resource image differs: " + path);
                    }
                }
            });
            foreach (var target in new[] {
                (Type: request, Chain: new[] { "UnityEngine.Networking.UnityWebRequest", "Il2CppSystem.Object", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase" }),
                (Type: operation, Chain: new[] { "UnityEngine.Networking.UnityWebRequestAsyncOperation", "UnityEngine.AsyncOperation", "UnityEngine.YieldInstruction", "Il2CppSystem.Object", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase" }),
                (Type: clip, Chain: new[] { "UnityEngine.Networking.DownloadHandlerAudioClip", "UnityEngine.Networking.DownloadHandler", "Il2CppSystem.Object", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase" }),
                (Type: multimedia, Chain: new[] { "UnityEngine.Networking.UnityWebRequestMultimedia", "Il2CppSystem.Object", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase" }) })
            {
                Test("exact direct IntPtr closure and cctor identities: " + target.Type.Name, () =>
                {
                    var constructor = target.Type.GetConstructor(new[] { typeof(IntPtr) })!;
                    var proof = policy.AuditConstructorMetadata(constructor);
                    Check(proof.Constructors.Select(member => member.DeclaringType.Split(',')[0]).SequenceEqual(target.Chain), "Actual constructor base/leaf chain differs from inspected metadata.");
                    Check(proof.Initializers.Any(member => member.DeclaringType.StartsWith(target.Type.FullName + ",", StringComparison.Ordinal)), "Target cctor identity was omitted.");
                    Check(proof.Guarded.Contains(constructor) && proof.Guarded.Contains(target.Type.TypeInitializer!), "Constructor or cctor escaped mutation guard.");
                    proofs.Add(new { Type = target.Type.AssemblyQualifiedName, Constructors = proof.Constructors, TypeInitializers = proof.Initializers,
                        RawMetadata = Metadata(target.Type) });
                });
            }
            Test("new resource-loader constructors and cctors join protected history", () =>
            {
                foreach (var type in new[] { request, operation, clip, multimedia })
                    Check(policy.IsPotentialConstructor(type.GetConstructor(new[] { typeof(IntPtr) })!) && policy.IsPotentialConstructor(type.TypeInitializer!), "Resource-loader constructor/cctor is absent from protected history.");
            });
            Test("texture WWW asset-bundle and unrelated network images remain unlisted", () =>
            {
                foreach (string name in new[] { "UnityEngine.UnityWebRequestTextureModule", "UnityEngine.UnityWebRequestWWWModule", "UnityEngine.UnityWebRequestAssetBundleModule", "BestHTTP" })
                    Check(!InteropImageManifest.Contains(name), "Uninspected dependency gained directory/name trust: " + name);
                var unlisted = Assembly.LoadFrom(Path.Combine(Root, "BepInEx/interop/UnityEngine.UnityWebRequestTextureModule.dll"));
                Reject(() => policy.AuditConstructorMetadata(unlisted.GetType("UnityEngine.Networking.DownloadHandlerTexture", true)!.GetConstructor(new[] { typeof(IntPtr) })!), "Unlisted texture loader was accepted.");
            });
            Test("same-name dynamic wrapper cannot inherit the resource-loader image pin", () =>
            {
                var fake = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UnityEngine.UnityWebRequestModule"), AssemblyBuilderAccess.Run);
                var type = fake.DefineDynamicModule("fake").DefineType("UnityEngine.Networking.UnityWebRequest", TypeAttributes.Public, typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase));
                var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IntPtr) }); var il = ctor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetConstructor(new[] { typeof(IntPtr) })!); il.Emit(OpCodes.Ret);
                Reject(() => policy.AuditConstructorMetadata(type.CreateType()!.GetConstructor(new[] { typeof(IntPtr) })!), "Dynamic same-name image acquired a physical pin.");
            });
            Test("same-name same-MVID resource image with altered bytes remains rejected", () =>
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!, "negative-image"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "UnityEngine.UnityWebRequestModule.dll");
                File.WriteAllBytes(path, File.ReadAllBytes(requestAssembly.Location).Append((byte)0).ToArray());
                var context = new ExistingDependencyContext();
                try
                {
                    var altered = context.LoadFromAssemblyPath(path);
                    Check(altered.ManifestModule.ModuleVersionId == requestAssembly.ManifestModule.ModuleVersionId && altered.GetName().Name == requestAssembly.GetName().Name, "Negative fixture lost original name or MVID.");
                    Reject(() => policy.AuditConstructorMetadata(altered.GetType(request.FullName!, true)!.GetConstructor(new[] { typeof(IntPtr) })!), "Changed image bytes inherited the original pin.");
                }
                finally { context.Unload(); }
            });
            File.WriteAllText(Path.GetFullPath(evidencePath), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Images = Expected.Select(item => new { item.Name, item.Sha256, item.Mvid, Version = "0.0.0.0" }), Proofs = proofs, Tests = tests,
                Scope = "Exact loaded metadata only. No Unity constructor, cctor, class-pointer store, real factory, network request or native AA was executed. This is not a whole-image or all-code authorization." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; runtime={Environment.Version}; metadata-only resource loader checks");
        return failed == 0 ? 0 : 1;
    }
    private static object Metadata(Type type) => new {
        type.FullName, BaseType = type.BaseType?.FullName, BeforeFieldInit = (type.Attributes & TypeAttributes.BeforeFieldInit) != 0,
        Methods = new[] { type.GetConstructor(new[] { typeof(IntPtr) }), type.TypeInitializer }.Where(method => method != null).Select(method => new {
            method!.Name, Token = method.MetadataToken.ToString("X8"), IlSha256 = InteropRecipePolicy.HashIl(method), RawIlHex = Convert.ToHexString(method.GetMethodBody()!.GetILAsByteArray()!) })
    };
    private sealed class ExistingDependencyContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => !assembly.IsDynamic && assembly.GetName().FullName == name.FullName);
    }
}
