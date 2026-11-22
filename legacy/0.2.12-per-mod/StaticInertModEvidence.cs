using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilAssembly = Mono.Cecil.AssemblyDefinition;
using RuntimeAssembly = System.Reflection.Assembly;

namespace AAVideoExport.Plugin;

// Evidence for three inspected empty plugin payloads, not a general classifier.
// Never constructs a Mod or calls Load. The inherited BepInEx constructor is
// explicitly a trusted framework boundary, not claimed to be side-effect free.
internal static class StaticInertModEvidence
{
    internal const string Scope = "StaticInertModEvidence.empty-load.v1";
    private const string PluginBase = "BepInEx.Unity.IL2CPP.BasePlugin";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Build[] Builds =
    {
        new("ExampleCore", "AzureArchive.ExampleMods.Core.ExampleCorePlugin", "azurearchive.example.core", "c1dd90cf-a65b-4ddd-8f18-8dbe25e0354c", "4A209AD52EDD4A5BEB1F9EF2801527AEABD2F49A5522710F7F359EDB3D7F25FB", false),
        new("ExampleDependent", "AzureArchive.ExampleMods.Dependent.ExampleDependentPlugin", "azurearchive.example.dependent", "86858117-c7ed-4c49-870b-7bcb1eef4616", "3E387C762544C3A7315F2DB5972BA106B1AFFBA402BBA42D6C250272579DC8A8", true),
        new("ExampleStandalone", "AzureArchive.ExampleMods.Standalone.ExampleStandalonePlugin", "azurearchive.example.standalone", "3177bafb-71a1-4a63-bf4d-71a4278c0382", "82620F29C8F75B37DFD67286C549A08A9974CE00D8BE44160268E75BDE596188", false)
    };
    private static readonly string[] CompilerTypes =
    {
        "Microsoft.CodeAnalysis.EmbeddedAttribute", "System.Runtime.CompilerServices.NullableAttribute", "System.Runtime.CompilerServices.NullableContextAttribute"
    };
    private static readonly ConditionalWeakTable<RuntimeAssembly, Receipt> Cached = new();
    private static readonly object CacheGate = new();
    private static long imageReads;
    internal static long ImageReadCount => System.Threading.Interlocked.Read(ref imageReads);
    internal static bool IsSupportedName(string? name) => Builds.Any(b => b.Name == name);
    internal static RuntimeAssembly[] LoadedSupportedAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => IsSupportedName(a.GetName().Name)).ToArray();

    internal static bool TryCreateReceipt(RuntimeAssembly assembly, out Receipt? receipt, out string reason)
    {
        receipt = null; reason = "";
        try
        {
            ArgumentNullException.ThrowIfNull(assembly);
            Build? build = Builds.SingleOrDefault(b => b.Name == assembly.GetName().Name);
            if (build == null) { reason = "No inspected empty-payload scope for this assembly."; return false; }
            lock (CacheGate)
            {
                if (Cached.TryGetValue(assembly, out var existing) && existing.TryValidate(out reason))
                { receipt = existing; return true; }
                Cached.Remove(assembly);
                var candidate = Receipt.Inspect(assembly);
                if (!candidate.TryValidate(out reason)) return false;
                Cached.Add(assembly, candidate); receipt = candidate; return true;
            }
        }
        catch (Exception error) { reason = error.GetType().Name + ": " + error.Message; return false; }
    }

    private static InspectedPayload Inspect(RuntimeAssembly assembly, Build build)
    {
        byte[] image = ReadValidatedImage(assembly, build.Name, new Version(1, 0, 0, 0), build.Mvid, build.Hash);
        using var metadata = CecilAssembly.ReadAssembly(new MemoryStream(image, false), new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        ValidateMetadata(metadata, build);
        using var pe = new PEReader(new MemoryStream(image, false));
        var expectedTypes = CompilerTypes.Append(build.PluginType).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var actualTypes = assembly.GetTypes();
        Require(actualTypes.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(expectedTypes), "Loaded type set differs from the inspected payload.");
        Require(assembly.GetModules().Length == 1 && assembly.ManifestModule.GetMethods().Length == 0, "Extra module/global methods are unsupported.");
        var methods = new List<MethodBase>();
        foreach (var definition in metadata.MainModule.Types.Where(t => t.Name != "<Module>"))
        {
            var type = actualTypes.Single(t => t.FullName == definition.FullName);
            Require(type.TypeInitializer == null && !type.IsGenericType && type.GetNestedTypes(Declared).Length == 0,
                "Loaded initializer, generic or nested type is unsupported: " + type.FullName);
            var loaded = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToArray();
            Require(loaded.Length == definition.Methods.Count, "Loaded method set changed: " + type.FullName);
            Require(type.GetFields(Declared).Length == definition.Fields.Count && type.GetProperties(Declared).Length == 0 && type.GetEvents(Declared).Length == 0,
                "Loaded fields/properties/events changed: " + type.FullName);
            foreach (var method in definition.Methods)
            {
                var runtime = loaded.Single(m => m.MetadataToken == method.MetadataToken.ToInt32());
                byte[]? liveIl = runtime.GetMethodBody()?.GetILAsByteArray();
                byte[]? diskIl = pe.GetMethodBody(method.RVA).GetILBytes();
                Require(liveIl != null && diskIl != null && liveIl.SequenceEqual(diskIl), "Loaded method IL differs from the inspected file: " + runtime.Name);
                Require(runtime.GetMethodBody()!.ExceptionHandlingClauses.Count == 0 && runtime.GetMethodBody()!.LocalVariables.Count == 0,
                    "Unexpected executable method metadata: " + runtime.Name);
                EnsureUnpatched(runtime); methods.Add(runtime);
            }
        }
        var plugin = actualTypes.Single(t => t.FullName == build.PluginType);
        ValidateRuntimeAttributes(plugin, build);
        var constants = new Dictionary<string, string> { ["PluginGuid"] = build.PluginGuid, ["PluginName"] = build.Name, ["PluginVersion"] = "1.0.0" };
        Require(plugin.GetFields(Declared).All(f => f.IsPublic && f.IsStatic && f.IsLiteral && f.FieldType == typeof(string)
            && constants.TryGetValue(f.Name, out string? value) && Equals(f.GetRawConstantValue(), value)), "Loaded plugin constant metadata changed.");
        Require(plugin.BaseType?.FullName == PluginBase, "Plugin base type changed.");
        var framework = plugin.BaseType!.Assembly;
        ReadValidatedImage(framework, "BepInEx.Unity.IL2CPP", new Version(6, 0, 0, 0), new Guid("bbc7391c-010a-4f1b-8966-22a54b549a1d"),
            "62C8246A3076BFE459EC80C482381228D0EB1DDE3434FE97AE5255FEF406FE72");
        var frameworkCtor = plugin.BaseType.GetConstructor(Declared, null, Type.EmptyTypes, null)
            ?? throw new NotSupportedException("Inspected BasePlugin constructor is unavailable.");
        Require(frameworkCtor.MetadataToken == 0x06000074, "BasePlugin constructor token changed.");
        EnsureUnpatched(frameworkCtor); methods.Add(frameworkCtor);
        var pluginMetadata = plugin.GetCustomAttributesData().Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin").AttributeType.Assembly;
        ReadValidatedImage(pluginMetadata, "BepInEx.Core", new Version(6, 0, 0, 0), new Guid("1258560f-9c0e-485f-83dd-ca64bc82153e"),
            "352AACC2D5356489A00891F76DB28B6463C81D25BCB02F90035171A669B7E35B");
        Receipt? dependency = null;
        if (build.NeedsCore)
        {
            var cores = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "ExampleCore").ToArray();
            Require(cores.Length == 1, "ExampleDependent requires exactly one inspected loaded ExampleCore.");
            if (!TryCreateReceipt(cores[0], out dependency, out string failure)) throw new NotSupportedException("ExampleCore dependency has no proof: " + failure);
        }
        return new InspectedPayload(methods.ToArray(), framework, pluginMetadata, dependency);
    }

    private static byte[] ReadValidatedImage(RuntimeAssembly assembly, string name, Version version, Guid mvid, string hash)
    {
        Require(!assembly.IsDynamic && assembly.GetName().Name == name && assembly.GetName().Version == version
            && assembly.ManifestModule.ModuleVersionId == mvid && (assembly.GetName().GetPublicKeyToken()?.Length ?? 0) == 0,
            "Assembly version/MVID/name identity changed: " + name);
        Require(!string.IsNullOrWhiteSpace(assembly.Location), "Assembly has no verifiable payload path: " + name);
        byte[] bytes = File.ReadAllBytes(assembly.Location);
        System.Threading.Interlocked.Increment(ref imageReads);
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == hash, "Assembly payload SHA-256 changed: " + name);
        return bytes;
    }

    private static void ValidateMetadata(CecilAssembly assembly, Build build)
    {
        var module = assembly.MainModule;
        Require(assembly.Modules.Count == 1 && assembly.EntryPoint == null, "Extra executable module/entry point is unsupported.");
        string[] names = CompilerTypes.Append("<Module>").Append(build.PluginType).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Require(module.Types.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(names), "Extra executable type is unsupported.");
        string[] references = { "BepInEx.Core", "BepInEx.Unity.IL2CPP", "System.Runtime" };
        Require(module.AssemblyReferences.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(references), "Assembly dependency list changed.");
        foreach (var type in module.Types)
        {
            Require(!type.HasNestedTypes && !type.HasGenericParameters && !type.HasInterfaces && !type.HasProperties && !type.HasEvents,
                "Unexpected executable type structure: " + type.FullName);
            foreach (var method in type.Methods)
                Require(!method.IsStatic && !method.IsPInvokeImpl && !method.IsAbstract && !method.HasGenericParameters
                    && method.HasBody && method.Body.ExceptionHandlers.Count == 0 && method.Body.Variables.Count == 0,
                    "Initializer/native/additional code is unsupported: " + method.FullName);
            if (type.Name == "<Module>") { Require(!type.HasMethods && !type.HasFields, "Module initializer/state is unsupported."); continue; }
            if (type.FullName == build.PluginType)
            {
                Require(type.BaseType?.FullName == PluginBase && type.Methods.Count == 2, "Plugin entry type changed.");
                var load = type.Methods.Single(m => m.Name == "Load");
                Require(load.IsPublic && load.IsVirtual && load.Parameters.Count == 0 && load.ReturnType.FullName == "System.Void"
                    && load.Body.Instructions.Count == 1 && load.Body.Instructions[0].OpCode == OpCodes.Ret, "Load is not the exact empty return.");
                var ctor = type.Methods.Single(m => m.Name == ".ctor");
                Require(ctor.IsPublic && ctor.Parameters.Count == 0, "Plugin constructor shape changed.");
                Require(InstructionShape(ctor) == "ldarg.0|call:System.Void BepInEx.Unity.IL2CPP.BasePlugin::.ctor()|ret", "Plugin constructor contains additional behavior.");
                var values = new Dictionary<string,string> { ["PluginGuid"] = build.PluginGuid, ["PluginName"] = build.Name, ["PluginVersion"] = "1.0.0" };
                Require(type.Fields.Count == values.Count && type.Fields.All(f => f.IsPublic && f.IsStatic && f.IsLiteral && f.HasConstant
                    && f.FieldType.FullName == "System.String" && values.TryGetValue(f.Name, out string? value) && Equals(f.Constant, value)), "Plugin fields/state changed.");
                var pluginAttributes = type.CustomAttributes.Where(a => a.AttributeType.FullName.StartsWith("BepInEx.", StringComparison.Ordinal)).ToArray();
                Require(pluginAttributes.Length == (build.NeedsCore ? 2 : 1), "Plugin/dependency attribute set changed.");
                Require(CecilAttribute(pluginAttributes, "BepInEx.BepInPlugin", build.PluginGuid, build.Name, "1.0.0"), "BepInPlugin metadata changed.");
                if (build.NeedsCore) Require(CecilAttribute(pluginAttributes, "BepInEx.BepInDependency", "azurearchive.example.core", "1.0.0"), "BepInDependency metadata changed.");
                continue;
            }
            Require(type.BaseType?.FullName == "System.Attribute", "Compiler support type is no longer an attribute.");
            string[] expected = type.FullName switch
            {
                "Microsoft.CodeAnalysis.EmbeddedAttribute" => new[] { "ldarg.0|call:System.Void System.Attribute::.ctor()|ret" },
                "System.Runtime.CompilerServices.NullableAttribute" => new[] {
                    "ldarg.0|call:System.Void System.Attribute::.ctor()|ldarg.0|ldc.i4.1|newarr:System.Byte|dup|ldc.i4.0|ldarg.1|stelem.i1|stfld:System.Byte[] System.Runtime.CompilerServices.NullableAttribute::NullableFlags|ret",
                    "ldarg.0|call:System.Void System.Attribute::.ctor()|ldarg.0|ldarg.1|stfld:System.Byte[] System.Runtime.CompilerServices.NullableAttribute::NullableFlags|ret" },
                _ => new[] { "ldarg.0|call:System.Void System.Attribute::.ctor()|ldarg.0|ldarg.1|stfld:System.Byte System.Runtime.CompilerServices.NullableContextAttribute::Flag|ret" }
            };
            Require(type.Methods.All(m => m.Name == ".ctor") && type.Methods.Select(InstructionShape).SequenceEqual(expected), "Compiler attribute code changed.");
            int fields = type.FullName == "Microsoft.CodeAnalysis.EmbeddedAttribute" ? 0 : 1;
            Require(type.Fields.Count == fields && type.Fields.All(f => f.IsPublic && f.IsInitOnly && !f.IsStatic), "Compiler metadata attribute fields changed.");
        }
    }

    private static string InstructionShape(Mono.Cecil.MethodDefinition method) => string.Join("|", method.Body.Instructions.Select(i => i.OpCode.Name + (i.Operand == null ? "" : ":" + i.Operand)));
    private static bool CecilAttribute(IEnumerable<Mono.Cecil.CustomAttribute> attributes, string name, params string[] expected)
    {
        var found = attributes.Where(a => a.AttributeType.FullName == name).ToArray();
        return found.Length == 1 && !found[0].HasFields && !found[0].HasProperties
            && found[0].ConstructorArguments.Select(a => a.Value as string).SequenceEqual(expected);
    }
    private static void ValidateRuntimeAttributes(Type plugin, Build build)
    {
        var attributes = plugin.GetCustomAttributesData().Where(a => a.AttributeType.FullName?.StartsWith("BepInEx.", StringComparison.Ordinal) == true).ToArray();
        Require(attributes.Length == (build.NeedsCore ? 2 : 1), "Loaded plugin/dependency attribute set changed.");
        bool Matches(string name, params string[] values)
        {
            var found = attributes.Where(a => a.AttributeType.FullName == name).ToArray();
            return found.Length == 1 && found[0].NamedArguments.Count == 0 && found[0].ConstructorArguments.Select(a => a.Value as string).SequenceEqual(values);
        }
        Require(Matches("BepInEx.BepInPlugin", build.PluginGuid, build.Name, "1.0.0"), "Loaded BepInPlugin metadata changed.");
        if (build.NeedsCore) Require(Matches("BepInEx.BepInDependency", "azurearchive.example.core", "1.0.0"), "Loaded dependency metadata changed.");
    }
    private static void EnsureUnpatched(MethodBase method)
    {
        var patches = Harmony.GetPatchInfo(method);
        Require(patches == null || !(patches.Prefixes.Any() || patches.Postfixes.Any() || patches.Transpilers.Any()
            || patches.Finalizers.Any() || patches.ILManipulators.Any() || patches.Owners.Any()), "Executable boundary has a Harmony owner/patch: " + method.DeclaringType?.FullName + "." + method.Name);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new NotSupportedException(message); }
    private sealed record InspectedPayload(MethodBase[] Methods, RuntimeAssembly Framework, RuntimeAssembly MetadataFramework, Receipt? Dependency);
    private sealed record Build(string Name, string PluginType, string PluginGuid, Guid Mvid, string Hash, bool NeedsCore)
    {
        internal Build(string name,string type,string guid,string mvid,string hash,bool dependency) : this(name,type,guid,new Guid(mvid),hash,dependency) { }
    }

    internal sealed class Receipt
    {
        private readonly RuntimeAssembly assembly, framework, metadataFramework;
        private readonly Build build;
        private readonly MethodBase[] methods;
        private readonly Receipt? dependency;
        private readonly object gate = new();
        private string? fault;
        internal string Scope => StaticInertModEvidence.Scope;
        internal string ModuleName => build.Name;
        internal string PayloadSha256 => build.Hash;
        internal string PluginGuid => build.PluginGuid;
        internal bool CanReusePreflight => false;
        internal bool CertifiesNativeMovie => false;
        internal RuntimeAssembly Assembly => assembly;
        // Keep this exact receipt throughout an export. After a failure it must
        // never revive; a fresh future operation can request a new clean scope.
        private Receipt(RuntimeAssembly assembly, Build build, MethodBase[] methods, RuntimeAssembly framework, RuntimeAssembly metadataFramework, Receipt? dependency)
            => (this.assembly,this.build,this.methods,this.framework,this.metadataFramework,this.dependency) = (assembly,build,methods,framework,metadataFramework,dependency);
        internal static Receipt Inspect(RuntimeAssembly assembly)
        {
            var build = Builds.Single(b => b.Name == assembly.GetName().Name);
            var payload = StaticInertModEvidence.Inspect(assembly, build);
            return new Receipt(assembly, build, payload.Methods, payload.Framework, payload.MetadataFramework, payload.Dependency);
        }
        internal bool TryValidate(out string reason) => Validate(true, out reason);
        // Loaded metadata is immutable for this supported managed image. Frame
        // checks inspect actual patch/dependency state without disk/PE rereads.
        // Acquisition and commit still rehash every pinned on-disk identity.
        internal bool TryValidateFrame(out string reason) => Validate(false, out reason);
        private bool Validate(bool includeImages, out string reason)
        {
            lock (gate)
            {
                if(fault != null){reason=fault;return false;}
                try
                {
                    Require(AppDomain.CurrentDomain.GetAssemblies().Count(a=>a.GetName().Name==build.Name)==1,"Multiple same-name loaded assemblies invalidate the scope: "+build.Name);
                    if(includeImages)
                    {
                        ReadValidatedImage(assembly,build.Name,new Version(1,0,0,0),build.Mvid,build.Hash);
                        ReadValidatedImage(framework,"BepInEx.Unity.IL2CPP",new Version(6,0,0,0),new Guid("bbc7391c-010a-4f1b-8966-22a54b549a1d"),"62C8246A3076BFE459EC80C482381228D0EB1DDE3434FE97AE5255FEF406FE72");
                        ReadValidatedImage(metadataFramework,"BepInEx.Core",new Version(6,0,0,0),new Guid("1258560f-9c0e-485f-83dd-ca64bc82153e"),"352AACC2D5356489A00891F76DB28B6463C81D25BCB02F90035171A669B7E35B");
                    }
                    foreach(var method in methods)EnsureUnpatched(method);
                    if(dependency != null && !dependency.Validate(includeImages,out string dependencyFailure))throw new NotSupportedException("ExampleCore retained dependency proof failed: "+dependencyFailure);
                    reason="";return true;
                }
                catch(Exception error){fault=error.GetType().Name+": "+error.Message;reason=fault;return false;}
            }
        }
    }
}
