using System.Collections;
using System.Runtime.CompilerServices;

// UI-only boundaries for the source-linked adapter. No native AA object starts.
public sealed class UIPanel { public int RefreshCount; public void Refresh() => RefreshCount++; }
public sealed class UILabel { public UIPanel? panel; public string Text = "original"; }
namespace AAVideoExport.Plugin
{
    internal static class MoreEffectsCompatibility { internal static bool FullClockCoverage = true; }
}
public sealed class TranslationPlugin
{
    public static TranslationPlugin Current { get; set; } = new();
    public bool Paused;
    public string Locale = "zh-CN";
    public bool IsTranslationPaused { [MethodImpl(MethodImplOptions.NoInlining)] get => Paused; }
    public string LocaleId => Locale;
}
public static class TranslationFixture
{
    public static IDictionary Pending = new Hashtable(), Bound = new Hashtable();
    public static bool _deferredRefresh;
    public static List<object> PendingSettings = new();
    public static int Ticks, Refreshes, Effects;
    public static void Reset() { Pending.Clear(); Bound.Clear(); PendingSettings.Clear(); _deferredRefresh = false; Ticks = Refreshes = Effects = 0; TranslationPlugin.Current = new(); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void SurfaceLifecyclePostfix() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void SettingPanelLifecyclePostfix() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void PopupLifecyclePostfix() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void TranslateSystemKey() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void LabelTextPostfix() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Tick()
    {
        if (TranslationPlugin.Current.IsTranslationPaused) return;
        Ticks++;
        foreach (UILabel label in Pending.Values) label.Text = "translated";
        Pending.Clear(); PendingSettings.Clear(); _deferredRefresh = false;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void RefreshAll(string locale) { if (TranslationPlugin.Current.IsTranslationPaused) return; Refreshes++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void RefreshTree() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void RefreshSettingModManagerEntry() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ObserveLabel() { if (TranslationPlugin.Current.IsTranslationPaused) return; Effects++; }
}
public static class DriverFixture
{
    public static int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Update() { Calls++; TranslationFixture.Tick(); }
}
public static class SurfaceFixture
{
    public static int SurfaceCalls, QueueCalls;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Tick() => SurfaceCalls++;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Draw() => SurfaceCalls++;
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ResourceQueue() => QueueCalls++;
}
