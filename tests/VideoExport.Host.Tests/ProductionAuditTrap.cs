internal static class ProductionAuditTrap
{
    internal static bool Enabled;
    internal static readonly List<string> Calls = new();
    internal static void Check(string name)
    {
        if (!Enabled) return;
        Calls.Add(name);
        throw new Exception("Production invoked audit entry point: " + name);
    }
}
