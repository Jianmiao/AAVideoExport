using System.Reflection;
using AAVideoExport.Plugin;

internal static class RecipeStatusTransportChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("frame validation selects compact status while commit retains the full sealed endpoint", () =>
        {
            string frame = Invoke(typeof(Current), false), commit = Invoke(typeof(Current), true);
            Check(frame == "compact:actual-token" && commit == "commit:actual-token", "Wrong live validation transport selected.");
        });
        test("legacy v2 without a compact endpoint falls back to its original full validator", () =>
            Check(Invoke(typeof(Legacy), false) == "full:actual-token", "Legacy live validator did not run."));
        test("an incompatible compact endpoint fails closed instead of masking it with legacy fallback", () =>
        {
            Reject(() => Invoke(typeof(WrongReturn), false)); Reject(() => Invoke(typeof(WrongParameter), false));
        });
        test("a throwing compact validator cannot be healed by calling the full endpoint", () =>
            Reject(() => Invoke(typeof(Throwing), false)));
    }
    private static string Invoke(Type type, bool commit)
    {
        var resolve = typeof(RuntimeAttributionClient).GetMethod("RecipeValidationMethod", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("Compact validation transport selection is missing.");
        var method = (MethodInfo)resolve.Invoke(null, new object[] { type, commit })!;
        return (string)method.Invoke(null, new object[] { "actual-token" })!;
    }
    private static void Reject(Action run) { try { run(); } catch (TargetInvocationException) { return; } throw new Exception("Invalid compact transport was accepted or fell back."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static class Current
    {
        public static string ValidateScopeStatusJson(string token) => "compact:" + token;
        public static string ValidateScopeJson(string token) => throw new Exception("Full diagnostics must not run for a compact-capable frame.");
        public static string ValidateScopeForCommitJson(string token) => "commit:" + token;
    }
    public static class Legacy { public static string ValidateScopeJson(string token) => "full:" + token; }
    public static class WrongReturn
    {
        public static bool ValidateScopeStatusJson(string token) => true;
        public static string ValidateScopeJson(string token) => "unsafe-fallback";
    }
    public static class WrongParameter
    {
        public static string ValidateScopeStatusJson(int token) => "wrong-shape";
        public static string ValidateScopeJson(string token) => "unsafe-fallback";
    }
    public static class Throwing
    {
        public static string ValidateScopeStatusJson(string token) => throw new InvalidOperationException("live validation refused");
        public static string ValidateScopeJson(string token) => "unsafe-fallback";
    }
}
