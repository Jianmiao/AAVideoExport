using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace AAVideoExport.Plugin;

internal sealed partial class FrameworkGeneratedAssemblyScope
{
    // Only exact, already-loaded non-dynamic CLR declaration images qualify.
    // This is a local byte-integrity receipt, never an export identity or a
    // cached successful validation. Dynamic trampoline modules remain on the
    // original full inspection path and every supported callback rereads bytes.
    private sealed class HarmonyMetadataReceipt
    {
        private const int MaximumMetadataBytes=256*1024;
        private const int MaximumRetainedBytes=8*1024*1024;
        private static readonly Type RuntimeAssemblyType=typeof(object).Assembly.GetType();
        private static readonly Type RuntimeType=typeof(object).GetType();
        private readonly Type type;
        private readonly Assembly assembly;
        private readonly Module module;
        private readonly CallingConvention? convention;
        private readonly byte[] metadata;
        private readonly string graph;
        private readonly MethodBase[] methods;
        internal string Graph=>graph;

        internal HarmonyMetadataReceipt(Type type,CallingConvention? convention,byte[] metadata,string graph,MethodBase[] methods)
        {this.type=type;assembly=type.Assembly;module=type.Module;this.convention=convention;this.metadata=metadata;this.graph=graph;this.methods=methods;}

        private static bool Supported(Type candidate)=>candidate.GetType()==RuntimeType&&candidate.Assembly.GetType()==RuntimeAssemblyType
            &&!candidate.Assembly.IsDynamic&&candidate.Assembly.Location.Length==0&&!MetadataUpdater.IsSupported;

        internal static unsafe byte[]? CaptureBefore(Type type,int retainedBytes)
        {
            if(!Supported(type)||!type.Assembly.TryGetRawMetadata(out byte* bytes,out int length))return null;
            // Oversized/unsupported images still receive complete semantics on
            // every callback; they never gain an unchecked cached permission.
            if(bytes==null||length<=0||length>MaximumMetadataBytes||retainedBytes>MaximumRetainedBytes-length)return null;
            return new ReadOnlySpan<byte>(bytes,length).ToArray();
        }

        internal unsafe bool ValidateCurrent(Type candidate,CallingConvention? currentConvention)
        {
            Require(ReferenceEquals(candidate,type)&&ReferenceEquals(candidate.Assembly,assembly)&&ReferenceEquals(candidate.Module,module),
                "Harmony metadata receipt identity changed.");
            Require(currentConvention==convention,"Harmony metadata receipt calling convention changed.");
            if(!Supported(candidate)||!assembly.TryGetRawMetadata(out byte* bytes,out int length))return false;
            Require(bytes!=null&&length==metadata.Length&&new ReadOnlySpan<byte>(bytes,length).SequenceEqual(metadata),
                "Retained Harmony declaration metadata bytes changed.");

            // Preserve the current runtime/container facts not authorized by a
            // metadata blob, and the exact original runtime-method constraint.
            var modules=assembly.GetModules();var types=assembly.GetTypes();
            Require(assembly.EntryPoint==null&&modules.Length==1&&ReferenceEquals(modules[0],module)
                &&ReferenceEquals(assembly.ManifestModule,module)&&types.Length==1&&ReferenceEquals(types[0],type),
                "Harmony declaration runtime module/type identity changed.");
            Require(type.Attributes==(TypeAttributes.Public|TypeAttributes.Sealed)&&type.BaseType==typeof(MulticastDelegate)&&!type.IsGenericType,
                "Harmony declaration runtime type shape changed.");
            foreach(var method in methods)
                Require(method.GetMethodImplementationFlags()==MethodImplAttributes.Runtime&&method.GetMethodBody()==null&&!method.IsGenericMethod,
                    "Harmony declaration contains a changed runtime method body.");
            InspectModule(assembly);
            return true;
        }
        internal void AppendMethods(List<MethodBase> destination)=>destination.AddRange(methods);
    }
}
