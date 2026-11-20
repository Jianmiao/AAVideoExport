using System.Reflection;

namespace ManagedOriginalCallWitness;

// Input-only contract for the pinned stock DynamicMethod emitter. The frozen
// MonoMod generator constructs a CLR DynamicMethod and never copies Cecil's
// InitLocals. Both pinned CoreLib DynamicMethod.Init implementations set
// m_fInitLocals from ldc.i4.1 at IL_0193/IL_0194. This is a prediction from fixed
// implementation identities, not a read of a generated target or its flags.
internal static class PinnedEmitterFlags
{
    internal static bool GeneratorInitLocals()
    {
        DynamicEmitExpectation.RequirePinned();
        _=DynamicMethodBodyReader.CoreLibHash;
        return true;
    }
    internal static void RequireOriginalSource(MethodInfo original)
    {
        var body=original.GetMethodBody()??throw new NotSupportedException("Original source has no reflection body.");
        RequireInitializedExistingLocals(body.InitLocals,body.LocalVariables.Count);
    }
    internal static void RequirePreparedSource(Mono.Cecil.MethodDefinition source)
        =>RequireInitializedExistingLocals(source.Body.InitLocals,source.Body.Variables.Count);
    private static void RequireInitializedExistingLocals(bool initialized,int count)
    {
        // Never qualify a source whose existing uninitialized-local semantics
        // the pinned emitter would change. Rewriter's zero-original-local case
        // already initializes only the hook locals it introduces.
        if(!initialized&&count!=0)throw new NotSupportedException("Original uninitialized locals unsupported by the pinned stock emitter.");
    }
}
