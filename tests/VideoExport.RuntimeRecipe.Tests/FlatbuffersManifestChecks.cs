using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using AAVideoExport.RuntimeBootstrap;

internal static class FlatbuffersManifestChecks
{
    internal static int Run()
    {
        const string root=@"F:\AzureArchive_101";
        Assembly? Resolve(AssemblyLoadContext context,AssemblyName name)
        {
            foreach(var folder in new[]{"BepInEx/interop","BepInEx/core"})
            {string path=Path.Combine(root,folder,(name.Name??"")+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}
            return null;
        }
        AssemblyLoadContext.Default.Resolving+=Resolve;
        int passed=0,failed=0;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);}
        void Test(string name,Action run){try{run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message);}}
        void Reject(Action run,string message){try{run();}catch(NotSupportedException){return;}throw new Exception(message);}
        try
        {
            // Metadata reads only: no generated type initializer, constructor,
            // class-pointer store or actual FlatBuffers factory is invoked here.
            var assembly=Assembly.LoadFrom(Path.Combine(root,"BepInEx/interop/Google.FlatBuffers.dll"));
            var policy=InteropRecipePolicy.ForTests();
            var byteBuffer=assembly.GetType("Google.FlatBuffers.ByteBuffer",true)!;
            Test("all twenty-two explicit interop pins match current physical SHA MVID and version",()=>
            {
                string[] names={"Assembly-CSharp","Il2Cppmscorlib","spine-csharp","spine-unity","UnityEngine.AssetBundleModule","UnityEngine.AudioModule","UnityEngine.CoreModule","UnityEngine.ParticleSystemModule","UnityEngine.PhysicsModule","UnityEngine.TextRenderingModule","UnityEngine.UI","UnityEngine.UIModule","Google.FlatBuffers","UnityEngine.VideoModule","Unity.RenderPipelines.Universal.Runtime","UnityEngine.InputLegacyModule","Il2CppSystem.Core","UnityEngine.ImageConversionModule","Newtonsoft.Json","UnityEngine.AnimationModule","UnityEngine.UnityWebRequestModule","UnityEngine.UnityWebRequestAudioModule"};
                Check(InteropImageManifest.Count==names.Length,"Manifest silently changed its finite image count");
                foreach(var name in names)
                {
                    Check(InteropImageManifest.TryGet(name,out var expected),"Missing explicit image "+name);
                    string path=Path.Combine(root,"BepInEx/interop",name+".dll");using var stream=File.OpenRead(path);using var pe=new PEReader(stream);var metadata=pe.GetMetadataReader();
                    Check(InteropRecipePolicy.HashFile(path)==expected!.Sha256&&metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString()==expected.Mvid
                        &&metadata.GetAssemblyDefinition().Version.ToString()==expected.Version,"Current explicit physical image differs: "+name);
                }
                Check(!InteropImageManifest.Contains("BestHTTP")&&!InteropImageManifest.Contains("Unity.RenderPipelines.Core.Runtime"),"Uninspected transitive/directory membership widened manifest");
            });
            Test("actual ByteBuffer direct constructor closure is admitted by its exact image",()=>
            {
                var proof=policy.AuditConstructorMetadata(byteBuffer.GetConstructor(new[]{typeof(IntPtr)})!);
                Check(proof.Constructors.Count==4&&proof.Constructors[0].AssemblySha256=="780191D7C243192F822FF17E8CD229DD5CBB3933E1961C8E056A4D5CCB28F4ED"
                    &&proof.Constructors[0].Mvid=="14c3a961-6c51-4333-8b8b-3872ee01c951","Actual physical ByteBuffer identity or closure differs");
                Check(proof.Initializers.Any(m=>m.DeclaringType.StartsWith("Google.FlatBuffers.ByteBuffer,",StringComparison.Ordinal)),"Actual ByteBuffer cctor identity was omitted");
            });
            Test("all nine closed FlatBuffers IntPtr definitions retain exact constructor closure",()=>
            {
                int count=0;
                foreach(var type in assembly.GetTypes().Where(t=>!t.ContainsGenericParameters))
                    if(type.GetConstructor(new[]{typeof(IntPtr)}) is {} ctor){_=policy.AuditConstructorMetadata(ctor);count++;}
                Check(count==9,"Closed FlatBuffers constructor count changed: "+count);
            });
            Test("open generic Offset remains outside exact runtime recipes",()=>
            {
                var type=assembly.GetTypes().Single(t=>t.Name=="Offset`1");Reject(()=>policy.AuditConstructorMetadata(type.GetConstructor(new[]{typeof(IntPtr)})!),"Open generic received a constructor receipt");
            });
            Test("new image constructors and cctors join protected mutation history",()=>
            {
                Check(policy.IsPotentialConstructor(byteBuffer.GetConstructor(new[]{typeof(IntPtr)})!)&&policy.IsPotentialConstructor(byteBuffer.TypeInitializer!),"New image escaped constructor/cctor patch tracking");
            });
            Test("same-name dynamic image does not inherit FlatBuffers pin",()=>
            {
                var fake=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Google.FlatBuffers"),AssemblyBuilderAccess.Run);
                var type=fake.DefineDynamicModule("counterfeit").DefineType("Google.FlatBuffers.ByteBuffer",TypeAttributes.Public,typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase));
                var ctor=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,new[]{typeof(IntPtr)});var il=ctor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetConstructor(new[]{typeof(IntPtr)})!);il.Emit(OpCodes.Ret);
                var actual=type.CreateType()!;Reject(()=>policy.AuditConstructorMetadata(actual.GetConstructor(new[]{typeof(IntPtr)})!),"Name-only counterfeit image received a pin");
            });
            Test("an unrelated current interop image is not trusted by directory membership",()=>
            {
                var unlisted=Assembly.LoadFrom(Path.Combine(root,"BepInEx/interop/BestHTTP.dll"));
                var type=unlisted.GetType("BestHTTP.HTTPRequest",true)!;
                Reject(()=>policy.AuditConstructorMetadata(type.GetConstructor(new[]{typeof(IntPtr)})!),"Unlisted network image received a directory-based pin");
            });
            Test("same-name same-MVID physical payload with changed bytes is rejected",()=>
            {
                string folder=Path.GetFullPath("artifacts/runtime-recipes-flatbuffers-20261030/negative-image");Directory.CreateDirectory(folder);
                string path=Path.Combine(folder,"Google.FlatBuffers.dll");var bytes=File.ReadAllBytes(assembly.Location);File.WriteAllBytes(path,bytes.Append((byte)0).ToArray());
                var context=new ExistingDependencyContext();
                try
                {
                    var mutant=context.LoadFromAssemblyPath(path);Check(mutant.GetName().Name==assembly.GetName().Name&&mutant.ManifestModule.ModuleVersionId==assembly.ManifestModule.ModuleVersionId,"Mutant did not retain name/MVID");
                    var ctor=mutant.GetType("Google.FlatBuffers.ByteBuffer",true)!.GetConstructor(new[]{typeof(IntPtr)})!;
                    Reject(()=>policy.AuditConstructorMetadata(ctor),"Changed physical payload acquired original pin");
                }
                finally{context.Unload();}
            });
        }
        finally{AssemblyLoadContext.Default.Resolving-=Resolve;}
        Console.WriteLine($"RESULT {passed} passed; {failed} failed; runtime={Environment.Version}; metadata only, no FlatBuffers cctor/factory/constructor executed");
        return failed==0?0:1;
    }
    private sealed class ExistingDependencyContext():AssemblyLoadContext(isCollectible:true)
    {
        protected override Assembly? Load(AssemblyName name)=>AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>!a.IsDynamic&&a.GetName().FullName==name.FullName);
    }
}
