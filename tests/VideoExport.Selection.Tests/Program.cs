using AAVideoExport.Core;
using AAVideoExport.Plugin;
using HarmonyLib;

int passed = 0, failed = 0;
NativeAutoSelection? active = null;
var errors = new List<Exception>();
var harmony = new Harmony("aave.selection.regression");
AutoSelectionHooks.Initialize(harmony, () => active, ex => { errors.Add(ex); active = null; }, _ => { });
Run("AUTO countdown keeps its original delay and skips the simulated press", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { });
    active.Validate(); co._elapsed_5__3 = 1.9f;
    Check(co.MoveNext() && choice.Submissions == 0, "choice submitted before countdown ended");
    co._elapsed_5__3 = 2;
    Check(!co.MoveNext() && choice.Submissions == 1 && choice.button!.NativePressCalls == 0, "AUTO relied on simulated click");
    co.MoveNext(); Check(choice.Submissions == 1, "button prefix and countdown postfix double-submitted");
});
Run("finished countdown recovers a dropped simulated click", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { }); co.DropClick = true;
    co._elapsed_5__3 = 2; co.MoveNext(); Check(choice.Submissions == 1, "finished AUTO countdown remained stuck");
});
Run("cancelled countdown and stale branch cannot submit", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { }); co.DropClick = true;
    co._elapsed_5__3 = 1; // An ended/cancelled iterator is checked at the actual hook boundary below.
    typeof(AutoSelectionHooks).GetMethod("CountdownFinished", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { co, false });
    Check(choice.Submissions == 0, "cancelled countdown skipped the delay");
    co._elapsed_5__3 = 2; co.index = 1; co.MoveNext(); Check(choice.Submissions == 0, "stale default index was submitted");
    co.index = 0; co._elem_5__2 = new(); co.MoveNext(); Check(choice.Submissions == 0, "stale element was submitted");
});
Run("manual buttons, another player and ordinary playback use native code", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { });
    var manual = new UI.MXButton(); manual.SimulateClick(); Check(manual.NativePressCalls == 1, "manual button was intercepted");
    var (other, otherChoice, otherCo) = Fixture(); otherCo._elapsed_5__3 = 2; otherCo.MoveNext();
    Check(otherChoice.Submissions == 0 && otherChoice.button!.NativePressCalls == 1, "another player was affected");
    active = null; co._elapsed_5__3 = 2; co.MoveNext();
    Check(choice.Submissions == 0 && choice.button!.NativePressCalls == 1, "normal playback changed");
});
Run("inactive, disabled and non-AUTO choices cannot be submitted", () =>
{
    foreach (int mode in Enumerable.Range(0, 5))
    {
        var (player, choice, co) = Fixture(); active = new(player, _ => { }); co.DropClick = true; co._elapsed_5__3 = 2;
        if (mode == 0) choice.gameObject.activeInHierarchy = false;
        if (mode == 1) choice.button!.disabled = true;
        if (mode == 2) choice.button!.gameObject.activeInHierarchy = false;
        if (mode == 3) player.IsAutoEnabled = false;
        if (mode == 4) player.selectionManager!.autoModeEnabled = false;
        co.MoveNext(); Check(choice.Submissions == 0, "invalid choice submitted: " + mode);
    }
});
Run("missing or out-of-range AUTO fails without choosing a branch", () =>
{
    foreach (int index in new[] { -1, 5 })
    {
        var (player, choice, _) = Fixture(); active = new(player, _ => { }); player.selectionManager!.defaultSelectionIndex = index;
        try { active.Validate(); throw new Exception("missing AUTO was accepted"); }
        catch (ExportException ex) { Check(ex.Code == "selection_default_missing" && choice.Submissions == 0, "wrong missing-AUTO failure"); }
    }
});
Run("native selection failure stops hook ownership", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { }); choice.Throw = true; co._elapsed_5__3 = 2;
    int before = errors.Count; co.MoveNext();
    Check(active == null && errors.Count == before + 1 && errors.Last() is ExportException { Code: "selection_submit_failed" }, "native exception did not stop export");
});
Run("a new selection resets duplicate protection even when AA reuses an element", () =>
{
    var (player, choice, co) = Fixture(); active = new(player, _ => { }); co.DropClick = true; co._elapsed_5__3 = 2;
    co.MoveNext(); choice.button!.disabled = false; player.selectionManager!.CreateSelection(); co.MoveNext();
    Check(choice.Submissions == 2, "new choice retained old submission state");
});
Run("unbound coroutine API fails clearly instead of silently recording choices", () =>
{
    AutoSelectionHooks.Shutdown(); var (player, _, _) = Fixture(); var session = new NativeAutoSelection(player, _ => { });
    try { session.Validate(); throw new Exception("unsupported API was accepted"); }
    catch (ExportException ex) { Check(ex.Code == "selection_api_unsupported", "wrong compatibility error"); }
});
harmony.UnpatchSelf(); AutoSelectionHooks.Shutdown();
Console.WriteLine($"{passed}/{passed + failed} passed (production AUTO hooks; managed native-boundary fixtures)");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    finally { active = null; }
}
static (Test, SelectionElement, SelectionManager._CoAutoSelect_d__16) Fixture()
{
    var player = new Test { selectionManager = new() }; var choice = new SelectionElement();
    player.selectionManager.elements!.Add(choice); choice.button!.OnClicked = choice.OnSelect;
    return (player, choice, new() { __4__this = player.selectionManager, _elem_5__2 = choice });
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
