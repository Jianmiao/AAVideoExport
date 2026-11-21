using System.Reflection;
using AAVideoExport.Core;
namespace AAVideoExport.Plugin;

// Unrelated inventory adapters are absent in this actual-framework harness.
internal static class StaticInertModEvidence { internal static bool IsSupportedName(string name) => false; }
internal sealed class StaticInertScope { internal IReadOnlyList<string> CaptureProof(Assembly assembly) => throw new InvalidOperationException("not exercised"); }
internal sealed class RetainedRecipeScope
{
    internal bool TryClassifyHolder(Assembly assembly, out StrictCapability? capability) { capability = null; return false; }
}
