using System.Reflection;
using System.Runtime.CompilerServices;

namespace AAVideoExport.Plugin;

// A CLR runtime assembly's simple name is immutable. Keep only that string,
// keyed by the exact assembly object; never cache the live assembly inventory,
// uniqueness, validation, metadata graphs, patch state or mutable cache values.
internal static class AssemblySimpleNameCache
{
    private static readonly Type RuntimeAssemblyType = typeof(object).Assembly.GetType();
    private static readonly ConditionalWeakTable<Assembly, NameValue> Names = new();

    internal static string? Get(Assembly assembly) => assembly.GetType() == RuntimeAssemblyType
        ? Names.GetValue(assembly, static value => new NameValue(value.GetName().Name)).Name
        : assembly.GetName().Name; // Custom Assembly overrides may be mutable.

    private sealed class NameValue(string? name)
    {
        internal readonly string? Name = name;
    }
}
