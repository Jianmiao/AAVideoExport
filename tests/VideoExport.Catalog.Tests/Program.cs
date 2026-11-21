using AAVideoExport.Plugin;
using UnityEngine;
using Object = UnityEngine.Object;

// Links the real catalog action. Unity/NGUI fakes simulate scene destruction and
// native object null semantics; actual native drawing still needs AA acceptance.
var tests = new (string Name, Action Run)[]
{
    ("toolbar returns after export clock advances then restores", () =>
    {
        var first = Catalog();
        using var action = new CatalogExportAction(_ => { });
        Time.unscaledTime = 100;
        action.Tick(false);
        Check(Button(first) != null, "initial toolbar action missing");
        TickAfterScanInterval(action, 100000);
        Object.Destroy(first.gameObject);
        var second = Catalog();
        TickAfterScanInterval(action, 101);
        Check(Button(second)?.activeInHierarchy == true, "export action did not return after scene reload and clock restoration");
    }),
    ("replaced native toolbar anchor is rebound without duplicate action", () =>
    {
        var info = Catalog();
        using var action = new CatalogExportAction(_ => { });
        action.Tick(false);
        var grid = info.controlPanel.transform.Find("Grid")!;
        Object.Destroy(grid.Find("DeleteBtn")!.gameObject);
        var replacement = Child(grid, "DeleteBtn");
        replacement.transform.localPosition = new Vector3(40, 0, 0);
        replacement.AddComponent<UIWidget>();
        TickAfterScanInterval(action, 1000);
        Check(Button(info)?.transform.localPosition.x == 70, "action kept a dead native anchor");
        Check(grid.Children.Count(t => t.gameObject.name == "AAVideoExport.ToolbarAction" && t.gameObject != null) == 1,
            "replacement left duplicate action objects");
    }),
    ("replaced control panel owns the only action and correct selected project", () =>
    {
        var info = Catalog();
        CatalogFileInfo? opened = null;
        using var action = new CatalogExportAction(selected => opened = selected);
        action.Tick(false);
        var stale = Button(info)!;
        info.controlPanel = Panel(info.transform);
        TickAfterScanInterval(action, 1000);
        var replacement = Button(info);
        Check(stale == null, "action in replaced toolbar was not removed");
        Check(replacement?.activeInHierarchy == true, "replacement panel did not get its action");
        UIEventListener.Get(replacement!).onClick!(replacement!);
        Check(opened == info, "replacement action opened a stale selection");
    }),
    ("lost icon is restored for surviving toolbar entries", () =>
    {
        var info = Catalog();
        using var action = new CatalogExportAction(_ => { });
        action.Tick(false);
        var graphic = Button(info)!.GetComponent<UITexture>()!;
        Object.Destroy(graphic.mainTexture!);
        TickAfterScanInterval(action, 1000);
        Check(graphic.mainTexture != null, "surviving action remained invisible after its texture was unloaded");
        Check((graphic.mainTexture!.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0,
            "icon is not protected from scene resource unloading");
    }),
    ("hidden catalog stays hidden and disposal releases owned objects", () =>
    {
        var info = Catalog();
        var action = new CatalogExportAction(_ => { });
        action.Tick(false);
        var button = Button(info)!;
        var texture = button.GetComponent<UITexture>()!.mainTexture!;
        action.Tick(true);
        Check(!button.activeSelf, "export should hide the launcher");
        action.Tick(false);
        Check(button.activeSelf, "launcher did not restore after export");
        action.Dispose();
        Check(button == null && texture == null, "disposal leaked owned objects");
    }),
    ("null block path does not break the catalog toolbar", () =>
    {
        var info = Catalog();
        info.block!.path = null!;
        using var action = new CatalogExportAction(_ => { });
        action.Tick(false);
        Check(Button(info)?.activeInHierarchy != true, "invalid path should not expose an export action");
    })
};
int failed = 0;
foreach (var test in tests)
{
    Object.ResetRegistry(); Time.unscaledTime = 0;
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed (production catalog lifecycle; native visuals require AA acceptance)");
return failed == 0 ? 0 : 1;

static void TickAfterScanInterval(CatalogExportAction action, float unityTime)
{
    // Real elapsed time intentionally stays independent of the virtual AA clock.
    Thread.Sleep(600);
    Time.unscaledTime = unityTime;
    action.Tick(false);
}
static CatalogFileInfo Catalog()
{
    var info = new GameObject("Catalog").AddComponent<CatalogFileInfo>();
    info.controlPanel = Panel(info.transform);
    info.block = new CatalogBlock { path = "fixture.aas" };
    return info;
}
static GameObject Panel(Transform parent)
{
    var panel = Child(parent, "ControlPanel");
    var grid = Child(panel.transform, "Grid");
    var share = Child(grid.transform, "ShareBtn"); share.transform.localPosition = new Vector3(10, 0, 0);
    var delete = Child(grid.transform, "DeleteBtn"); delete.transform.localPosition = new Vector3(20, 0, 0);
    delete.AddComponent<UIWidget>();
    return panel;
}
static GameObject Child(Transform parent, string name)
{
    var child = new GameObject(name); child.transform.SetParent(parent, false); return child;
}
static GameObject? Button(CatalogFileInfo info) => info.controlPanel.transform.Find("Grid")?.Find("AAVideoExport.ToolbarAction")?.gameObject;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
