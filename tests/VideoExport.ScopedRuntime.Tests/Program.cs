using System.Reflection;
using HarmonyLib;
using MonoMod.Cil;
using AAVideoExport.Plugin;
using AAVideoExport.Integration;

if (args.Length > 0 && args[0] == "effects-preload")
    return EffectsDependencyChecks.Run(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "effects-source-callbacks")
    return EffectsSourceCallbackChecks.Run();

int passed = 0, failed = 0;
void Test(string name, Action run)
{
    Fixture.ResetCoordinator();
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Test("ten translation call sites retain original effects under frame-owned pumping", () => Fixture.Translation((hooks, foreign) =>
{
    DriverFixture.Update(); Fixture.Check(TranslationFixture.Ticks == 1 && DriverFixture.Calls == 1, "ordinary driver was changed");
    using var lease = Fixture.Lease(); TranslationPlugin.Current.Paused = true;
    MultilingualCaptureAdapter.BeginExport();
    Fixture.Check(TranslationFixture.Refreshes == 1, "begin skipped original refresh");
    DriverFixture.Update(); TranslationFixture.Tick();
    Fixture.Check(TranslationFixture.Ticks == 1 && DriverFixture.Calls == 1, "autonomous scheduling escaped the owned pump");
    var label = new UILabel { panel = new UIPanel() }; TranslationFixture.Pending["label"] = label;
    MultilingualCaptureAdapter.Pump(0); MultilingualCaptureAdapter.Pump(0);
    Fixture.Check(TranslationFixture.Ticks == 2 && label.Text == "translated" && label.panel.RefreshCount == 1, "frame pump lost or repeated original translation/layout");
    foreach (string name in new[] { "SurfaceLifecyclePostfix", "SettingPanelLifecyclePostfix", "PopupLifecyclePostfix", "TranslateSystemKey", "LabelTextPostfix", "RefreshTree", "RefreshSettingModManagerEntry", "ObserveLabel" })
        typeof(TranslationFixture).GetMethod(name)!.Invoke(null, null);
    Fixture.Check(TranslationFixture.Effects == 8, "one or more original translation effects remained paused");
    MultilingualCaptureAdapter.EndExport(); DriverFixture.Update();
    Fixture.Check(DriverFixture.Calls == 2 && TranslationFixture.Ticks == 2, "end did not restore original paused behavior");
    TranslationPlugin.Current.Paused = false; DriverFixture.Update();
    Fixture.Check(TranslationFixture.Ticks == 3, "ordinary post-export translation did not resume");
}));
Test("translation new pending work in the same frame is preserved", () => Fixture.Translation((hooks, foreign) =>
{
    using var lease = Fixture.Lease(); TranslationPlugin.Current.Paused = true; MultilingualCaptureAdapter.BeginExport();
    MultilingualCaptureAdapter.Pump(0); var label = new UILabel(); TranslationFixture.Pending["late"] = label;
    MultilingualCaptureAdapter.Pump(0); Fixture.Check(label.Text == "translated" && TranslationFixture.Ticks == 2, "late same-frame translation was suppressed");
    Fixture.Reject(() => MultilingualCaptureAdapter.Pump(-1));
}));
Test("translation worker calls preserve original pause and reject pump ownership", () => Fixture.Translation((hooks, foreign) =>
{
    using var lease = Fixture.Lease(); TranslationPlugin.Current.Paused = true; MultilingualCaptureAdapter.BeginExport();
    Task.Run(() => TranslationFixture.SurfaceLifecyclePostfix()).GetAwaiter().GetResult();
    Fixture.Check(TranslationFixture.Effects == 0, "worker received the export-thread pause override");
    Task.Run(() => Fixture.Reject(() => MultilingualCaptureAdapter.Pump(0))).GetAwaiter().GetResult();
    TranslationPlugin.Current.Locale = "ja-JP"; Fixture.Reject(MultilingualCaptureAdapter.ValidateActive);
}));
Test("translation rejects an IL manipulator before installing any controlled patch", () => Fixture.Translation((hooks, foreign) => { },
    beforeBind: foreign => foreign.Patch(Fixture.Tick, ilmanipulator: Fixture.Manipulator), expectBindFailure: true));
Test("translation rejects a late IL manipulator while retaining its foreign owner", () => Fixture.Translation((hooks, foreign) =>
{
    using var lease = Fixture.Lease(); MultilingualCaptureAdapter.BeginExport();
    foreign.Patch(Fixture.Tick, ilmanipulator: Fixture.Manipulator);
    Fixture.Reject(MultilingualCaptureAdapter.ValidateActive);
    Fixture.Check(Harmony.GetPatchInfo(Fixture.Tick)!.Owners.Contains(foreign.Id), "validation removed a foreign patch");
}));
Test("translation shutdown retains retry bindings after partial unpatch", () => Fixture.Translation((hooks, foreign) =>
{
    using (Fixture.InjectUnpatch(Fixture.Tick))
    {
        Fixture.Check(Fixture.Call(typeof(MultilingualCaptureAdapter), "Shutdown") is false, "translation shutdown ignored unpatch failure");
        Fixture.Check(!MultilingualCaptureAdapter.IsReady && ReferenceEquals(Fixture.Get(typeof(MultilingualCaptureAdapter), "harmony"), hooks), "translation cleanup lost ownership or remained ready");
    }
    Fixture.Check(Fixture.Call(typeof(MultilingualCaptureAdapter), "Shutdown") is true, "translation cleanup could not retry");
}));
Test("Rukari idle surfaces pause while resource queue remains active and resume on end", () => Fixture.Runtime((hooks, foreign) =>
{
    Fixture.Call(typeof(RuntimeModBoundaries), "Freeze", typeof(SurfaceFixture), nameof(SurfaceFixture.Tick));
    SurfaceFixture.Tick(); using var lease = Fixture.Lease(); RuntimeModBoundaries.BeginExport();
    SurfaceFixture.Tick(); SurfaceFixture.ResourceQueue();
    Fixture.Check(SurfaceFixture.SurfaceCalls == 1 && SurfaceFixture.QueueCalls == 1, "idle-surface boundary stopped resource work or failed to retain idle state");
    RuntimeModBoundaries.EndExport(); SurfaceFixture.Tick(); Fixture.Check(SurfaceFixture.SurfaceCalls == 2, "surface did not resume");
}));
Test("Rukari boundary refuses activation without a real render lease", () => Fixture.Runtime((hooks, foreign) => Fixture.Reject(RuntimeModBoundaries.BeginExport)));
Test("Rukari frozen callback rejects late IL manipulator conflicts", () => Fixture.Runtime((hooks, foreign) =>
{
    Fixture.Call(typeof(RuntimeModBoundaries), "Freeze", typeof(SurfaceFixture), nameof(SurfaceFixture.Tick));
    var method = typeof(SurfaceFixture).GetMethod(nameof(SurfaceFixture.Tick))!;
    foreign.Patch(method, ilmanipulator: Fixture.Manipulator);
    Fixture.Reject(() => Fixture.Call(typeof(RuntimeModBoundaries), "ValidatePatch", method));
}));
Test("Rukari partial cleanup reports failure and retains retry ownership", () => Fixture.Runtime((hooks, foreign) =>
{
    Fixture.Call(typeof(RuntimeModBoundaries), "Freeze", typeof(SurfaceFixture), nameof(SurfaceFixture.Tick));
    Fixture.Call(typeof(RuntimeModBoundaries), "Freeze", typeof(SurfaceFixture), nameof(SurfaceFixture.Draw));
    using (Fixture.InjectUnpatch(typeof(SurfaceFixture).GetMethod(nameof(SurfaceFixture.Draw))!))
    {
        Fixture.Check(Fixture.Call(typeof(RuntimeModBoundaries), "Shutdown") is false, "Rukari shutdown failed to report pending cleanup");
        Fixture.Check(ReferenceEquals(Fixture.Get(typeof(RuntimeModBoundaries), "_harmony"), hooks), "Rukari cleanup lost retry owner");
    }
    Fixture.Check(Fixture.Call(typeof(RuntimeModBoundaries), "Shutdown") is true, "Rukari cleanup did not retry");
}));
Test("Rukari shutdown preserves a foreign owner using the same prefix", () => Fixture.Runtime((hooks, foreign) =>
{
    Fixture.Call(typeof(RuntimeModBoundaries), "Freeze", typeof(SurfaceFixture), nameof(SurfaceFixture.Tick));
    var target = typeof(SurfaceFixture).GetMethod(nameof(SurfaceFixture.Tick))!;
    var prefix = typeof(RuntimeModBoundaries).GetMethod("AllowSurfaceTick", Fixture.Flags)!;
    foreign.Patch(target, prefix: new HarmonyMethod(prefix));
    Fixture.Call(typeof(RuntimeModBoundaries), "Shutdown");
    Fixture.Check(Harmony.GetPatchInfo(target)?.Owners.Contains(foreign.Id) == true, "Rukari shutdown removed another owner");
}));
Test("reported full effects clock cannot substitute for exact loaded dependency closure", () => Fixture.Runtime((hooks, foreign) =>
{
    MoreEffectsCompatibility.FullClockCoverage = true;
    Fixture.Reject(() => Fixture.Call(typeof(RuntimeModBoundaries), "ValidateEffectsDependencies"));
}));
Test("exact build binding requires payload hash and MVID as well as version", () =>
{
    var assembly = Assembly.GetExecutingAssembly();
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
    var build = new CompatibilityBuild(assembly.GetName().Name!, assembly.GetName().Version!, assembly.ManifestModule.ModuleVersionId, hash);
    build.Validate(assembly);
    Fixture.Reject(() => (build with { Sha256 = new string('0', 64) }).Validate(assembly));
    Fixture.Reject(() => (build with { Module = Guid.NewGuid() }).Validate(assembly));
});
Test("unvalidated known-name dependencies remain rejected and TitlePage stays outside this adapter", () => Fixture.Runtime((hooks, foreign) =>
{
    var wrongDependency = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Rukari.Lib"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
    Fixture.Check(RuntimeModBoundaries.TryClassify(wrongDependency, out var level, out _) && level == AAVideoExport.Core.StrictCapabilityLevel.Rejected,
        "known dependency name bypassed exact build validation");
    var title = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TitlePageMod"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
    Fixture.Check(!RuntimeModBoundaries.TryClassify(title, out _, out _), "TitlePage was added to the scoped adapter");
}));
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static class Fixture
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    internal static MethodInfo Tick => typeof(TranslationFixture).GetMethod(nameof(TranslationFixture.Tick))!;
    internal static HarmonyMethod Manipulator => new(typeof(Fixture), nameof(Manipulate));
    public static void Manipulate(ILContext context) { }
    internal static void Translation(Action<Harmony, Harmony> action, Action<Harmony>? beforeBind = null, bool expectBindFailure = false)
    {
        TranslationFixture.Reset(); DriverFixture.Calls = 0;
        var owned = new Harmony("aave.test.translation-owned"); var foreign = new Harmony("aave.test.translation-foreign");
        MultilingualCaptureAdapter.Initialize(owned, _ => { });
        try
        {
            beforeBind?.Invoke(foreign);
            Action bind = () => Call(typeof(MultilingualCaptureAdapter), "BindAndPatch", typeof(TranslationPlugin), typeof(TranslationFixture), typeof(DriverFixture));
            if (expectBindFailure) { Reject(bind); return; }
            bind(); action(owned, foreign);
        }
        finally
        {
            UnpatchFailure.Target = null; owned.UnpatchSelf(); foreign.UnpatchSelf();
            MultilingualCaptureAdapter.EndExport(); Call(typeof(MultilingualCaptureAdapter), "Shutdown");
            Set(typeof(MultilingualCaptureAdapter), "restorationFailed", false);
        }
    }
    internal static void Runtime(Action<Harmony, Harmony> action)
    {
        SurfaceFixture.SurfaceCalls = SurfaceFixture.QueueCalls = 0;
        var owned = new Harmony("aave.test.runtime-owned"); var foreign = new Harmony("aave.test.runtime-foreign");
        RuntimeModBoundaries.Initialize(owned, _ => { });
        try { action(owned, foreign); }
        finally { UnpatchFailure.Target = null; owned.UnpatchSelf(); foreign.UnpatchSelf(); RuntimeModBoundaries.EndExport(); Call(typeof(RuntimeModBoundaries), "Shutdown"); }
    }
    internal static IDisposable Lease() { RenderControlV1.BeginHostInitialization(); RenderControlV1.CompleteHostInitialization(); return RenderControlV1.Acquire(); }
    internal static object? Get(Type type, string name) => type.GetField(name, Flags)!.GetValue(null);
    internal static void Set(Type type, string name, object? value) => type.GetField(name, Flags)!.SetValue(null, value);
    internal static object? Call(Type type, string name, params object?[] args)
    {
        var method = type.GetMethod(name, Flags); Check(method != null, "missing production boundary seam: " + name);
        try { return method!.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    internal static void ResetCoordinator()
    {
        foreach (var field in typeof(RenderControlV1).GetFields(Flags))
            if (!field.IsLiteral && !field.IsInitOnly) field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
    }
    internal static void Reject(Action action) { try { action(); } catch (Exception error) when (!error.Message.StartsWith("missing production boundary seam", StringComparison.Ordinal)) { return; } throw new Exception("unsafe boundary was accepted"); }
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static IDisposable InjectUnpatch(MethodInfo target)
    {
        var injector = new Harmony("aave.test.scoped-unpatch-failure");
        foreach (var parameters in new[] { new[] { typeof(HarmonyPatchType), typeof(string) }, new[] { typeof(MethodInfo) } })
            injector.Patch(typeof(PatchProcessor).GetMethod(nameof(PatchProcessor.Unpatch), parameters)!, prefix: new HarmonyMethod(typeof(UnpatchFailure), nameof(UnpatchFailure.Before)));
        UnpatchFailure.Target = target;
        return new Cleanup(() => { UnpatchFailure.Target = null; injector.UnpatchSelf(); });
    }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    private static class UnpatchFailure
    {
        internal static MethodBase? Target;
        public static void Before(PatchProcessor __instance)
        {
            var target = typeof(PatchProcessor).GetField("original", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(__instance);
            if (Target != null && Equals(target, Target)) throw new InvalidOperationException("injected scoped unpatch failure");
        }
    }
}
