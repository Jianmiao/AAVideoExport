using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestSettingsNavigation()
    {
        await Test("settings start with Advanced collapsed and one shared scroll offset", () => { TestSettingsNavigationInitialState(); return Task.CompletedTask; });
        await Test("inline Advanced expansion preserves the current fractional scroll offset", () => { TestSettingsNavigationExpansion(); return Task.CompletedTask; });
        await Test("repeated Advanced opens and Back preserve the current list position", () => { TestSettingsNavigationRepeatedOpen(); return Task.CompletedTask; });
        await Test("navigation clamps invalid scroll and collapsed content to visible settings", () => { TestSettingsNavigationScrollBounds(); return Task.CompletedTask; });
        await Test("repeated inline toggles preserve the shared prefix without stale page history", () => { TestSettingsNavigationRepeatedTrace(); return Task.CompletedTask; });
    }

    private static void AssertSettingsState(ExportSettingsNavigation navigation, bool advanced, float offset, string context)
    {
        Equal(advanced, navigation.IsAdvanced, context + " expansion");
        Equal(offset, navigation.ScrollOffset, context + " scroll");
    }

    private static void TestSettingsNavigationInitialState()
    {
        var navigation = new ExportSettingsNavigation();
        AssertSettingsState(navigation, false, 0, "initial state");
        Check(!navigation.Back() && !navigation.Back(), "Back is ignored when Advanced is collapsed");
        navigation.ScrollTo(61.25f, PanelLayoutForMask(7));
        Check(!navigation.Back(), "Back does not leave the unified settings list");
        AssertSettingsState(navigation, false, 61.25f, "unused Back preserves the list position");
    }

    private static void TestSettingsNavigationExpansion()
    {
        for (int mask = 0; mask < 8; mask++)
        {
            var navigation = new ExportSettingsNavigation();
            var collapsed = PanelLayoutForMask(mask);
            var expanded = PanelLayoutForMask(mask | 8);
            float offset = collapsed.MaximumScroll / 2f;
            navigation.ScrollTo(offset, collapsed);
            navigation.ToggleAdvanced();
            navigation.ScrollTo(navigation.ScrollOffset, expanded);
            AssertSettingsState(navigation, true, offset, "expansion retains the exact current list position");
            Equal(collapsed.OutputY - offset, expanded.OutputY - navigation.ScrollOffset, "output retains its screen position");
            Equal(collapsed.AudioY - offset, expanded.AudioY - navigation.ScrollOffset, "audio retains its screen position");
            Equal(collapsed.AdvancedY - offset, expanded.AdvancedY - navigation.ScrollOffset, "the toggled header retains its screen position");
            navigation.ToggleAdvanced();
            navigation.ScrollTo(navigation.ScrollOffset, collapsed);
            AssertSettingsState(navigation, false, offset, "collapse preserves positions within the shared prefix");
        }
    }

    private static void TestSettingsNavigationRepeatedOpen()
    {
        var navigation = new ExportSettingsNavigation();
        navigation.ScrollTo(111.5f, PanelLayoutForMask(7));
        navigation.OpenAdvanced();
        AssertSettingsState(navigation, true, 111.5f, "opening Advanced keeps the unified list position");
        navigation.ScrollTo(193.25f, PanelLayoutForMask(15));
        navigation.OpenAdvanced();
        navigation.OpenAdvanced();
        AssertSettingsState(navigation, true, 193.25f, "duplicate opens cannot reset the current list position");
        Check(navigation.Back(), "Back collapses the open Advanced body");
        AssertSettingsState(navigation, false, 193.25f, "Back retains the latest position instead of restoring an earlier page");
        Check(!navigation.Back(), "duplicate Back has no stale page history");
        navigation.OpenAdvanced();
        AssertSettingsState(navigation, true, 193.25f, "reopening retains the single shared offset");
    }

    private static void TestSettingsNavigationScrollBounds()
    {
        var navigation = new ExportSettingsNavigation();
        foreach (int mask in new[] { 2, 7, 8, 15 })
        {
            var layout = PanelLayoutForMask(mask);
            foreach (float offset in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                navigation.ScrollTo(73.5f, layout);
                navigation.ScrollTo(offset, layout);
                Equal(0f, navigation.ScrollOffset, "nonfinite scroll resets safely");
            }
            navigation.ScrollTo(float.MaxValue, layout);
            Equal((float)layout.MaximumScroll, navigation.ScrollOffset, "large wheel movement cannot overscroll");
            navigation.ScrollTo(-.25f, layout);
            Equal(0f, navigation.ScrollOffset, "negative wheel movement cannot expose blank content");
            navigation.ScrollTo(73.5f, layout);
            Equal(73.5f, navigation.ScrollOffset, "fractional pointer scrolling remains smooth");
        }
        navigation.OpenAdvanced();
        navigation.ScrollTo(float.MaxValue, PanelLayoutForMask(15));
        AssertSettingsState(navigation, true, 556, "expanded body can be scrolled to its final row");
        navigation.ToggleAdvanced();
        navigation.ScrollTo(navigation.ScrollOffset, PanelLayoutForMask(7));
        AssertSettingsState(navigation, false, 278, "collapse clamps to the end of the remaining video and audio content");
        navigation.ScrollTo(navigation.ScrollOffset, PanelLayoutForMask(0));
        AssertSettingsState(navigation, false, 48, "collapsing optional video controls clears stale overflow");
    }

    private static void TestSettingsNavigationRepeatedTrace()
    {
        var navigation = new ExportSettingsNavigation();
        for (int round = 0; round < 100; round++)
        {
            float offset = 20.25f + round;
            navigation.ScrollTo(offset, PanelLayoutForMask(7));
            for (int visit = 0; visit < 3; visit++)
            {
                navigation.ToggleAdvanced();
                AssertSettingsState(navigation, true, offset, "each expansion retains the shared list position");
                float nextOffset = offset + .5f;
                navigation.ScrollTo(nextOffset, PanelLayoutForMask(15));
                navigation.OpenAdvanced();
                AssertSettingsState(navigation, true, nextOffset, "duplicate open preserves the latest pointer position");
                if (visit % 2 == 0)
                    navigation.ToggleAdvanced();
                else
                    Check(navigation.Back(), "Back collapses exactly once");
                navigation.ScrollTo(navigation.ScrollOffset, PanelLayoutForMask(7));
                AssertSettingsState(navigation, false, nextOffset, "collapse retains the latest shared position");
                Check(!navigation.Back(), "collapsed state has no stale return destination");
                offset = nextOffset;
            }
        }
        // ExportOptions and uncommitted text remain owned by NativeExportPanel;
        // their retention requires native integration coverage.
    }
}
