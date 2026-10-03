using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestPanelLayouts()
    {
        await Test("all 32 setting combinations preserve the original modal shell and footer", () => { TestPanelShellInvariants(); return Task.CompletedTask; });
        await Test("video audio and advanced sections share one reachable scrolling list", () => { TestPanelContentGeometry(); return Task.CompletedTask; });
        await Test("advanced expansion preserves all earlier settings and their positions", () => { TestPanelInlineAdvanced(); return Task.CompletedTask; });
        await Test("disabled super resolution remains compact in the unified settings list", () => { TestPanelCompactSuperResolution(); return Task.CompletedTask; });
        await Test("advanced bitrate mode cannot rearrange its controls", () => { TestPanelAdvancedBitrate(); return Task.CompletedTask; });
        await Test("scroll clamps to the combined content and adjusts when rows collapse", () => { TestPanelScrollBounds(); return Task.CompletedTask; });
        await Test("the original wide proportions fit within 90 percent of every viewport", () => { TestPanelResponsiveViewports(); return Task.CompletedTask; });
        await Test("large windows can scale beyond 1.2 without growing the design shell", () => { TestPanelResponsiveScale(); return Task.CompletedTask; });
        await Test("native UIRoot scaling preserves the same proportions and margins", () => { TestPanelRootScaling(); return Task.CompletedTask; });
        await Test("legacy height requests cannot stretch the fixed design shell", () => { TestPanelHeightBounds(); return Task.CompletedTask; });
        await TestSettingsNavigation();
        await TestSliderEditGuard();
    }

    private static ExportPanelLayout PanelLayoutForMask(int mask, int height = ExportPanelLayout.DefaultPanelHeight)
    {
        bool Flag(int bit) => (mask & (1 << bit)) != 0;
        return ExportPanelLayout.Create(Flag(0), Flag(1), Flag(2), Flag(3), Flag(4), height);
    }

    private static void TestPanelShellInvariants()
    {
        var baseline = PanelLayoutForMask(0);
        for (int mask = 0; mask < 32; mask++)
        {
            var layout = PanelLayoutForMask(mask);
            string context = $"flags {mask}";
            Equal(980, ExportPanelLayout.PanelWidth, context + " original width");
            Equal(800, layout.PanelHeight, context + " original height");
            Equal(680, layout.FooterY, context + " export actions stay anchored");
            Equal(426, layout.ViewportHeight, context + " scrolling viewport is stable");
            Equal(120, layout.PanelHeight - layout.FooterY, context + " reserves the complete footer");
            Equal(14, layout.FooterY - ExportPanelLayout.ContentTop - layout.ViewportHeight, context + " separates content from footer actions");
            foreach (var viewport in new[] { (640f, 360f), (1280f, 720f), (2560f, 1440f) })
                Equal(baseline.FitScale(viewport.Item1, viewport.Item2), layout.FitScale(viewport.Item1, viewport.Item2),
                    context + " settings cannot rescale controls");
        }
    }

    private static void TestPanelContentGeometry()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            var layout = PanelLayoutForMask(mask);
            var sections = new[]
            {
                (layout.OutputY, layout.OutputHeight),
                (layout.SuperResolutionY, layout.SuperResolutionHeight),
                (layout.AudioY, layout.AudioHeight),
                (layout.AdvancedY, layout.AdvancedHeight)
            };
            int bottom = ExportPanelLayout.ContentTop;
            foreach (var (top, sectionHeight) in sections)
            {
                Check(sectionHeight > 0 && top >= bottom, $"sections overlap for flags {mask}");
                bottom = top + sectionHeight;
            }
            Equal(240, layout.OutputY, "output always starts the combined content");
            Equal(172, layout.AudioHeight, "audio always reserves its complete controls");
            Equal(layout.AdvancedY + 44, layout.AdvancedBodyY, "advanced body follows its permanent entry header");
            Equal(ExportPanelLayout.ContentTop + layout.ContentHeight, bottom, "scroll extent includes the complete final section");
            float lastBottom = bottom - layout.ClampScroll(float.MaxValue);
            Check(lastBottom <= ExportPanelLayout.ContentTop + layout.ViewportHeight, "bottom scroll reveals the final section");
            Check(lastBottom < layout.FooterY, "scrolled content stops before the fixed footer");
            Equal(0f, layout.ClampScroll(float.MinValue), "top content is reachable");
            if (layout.MaximumScroll > 0)
                Equal(666f, lastBottom, "overflow aligns exactly with viewport bottom");
        }
    }

    private static void TestPanelInlineAdvanced()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            var layout = PanelLayoutForMask(mask);
            var collapsed = PanelLayoutForMask(mask & ~8);
            var expanded = PanelLayoutForMask(mask | 8);
            Equal((mask & 1) != 0, layout.ShowCustomDimensions, "custom output fields stay available while Advanced is open");
            Equal((mask & 2) != 0, layout.ShowSuperResolutionControls, "super-resolution controls stay available while Advanced is open");
            Equal((mask & 6) == 6, layout.ShowSharpness, "enabled RCAS exposes sharpness on the same page");
            Equal((mask & 8) != 0, layout.ShowAdvanced, "only the advanced body follows expansion state");
            Equal(collapsed.OutputY, expanded.OutputY, "opening Advanced preserves output position");
            Equal(collapsed.OutputHeight, expanded.OutputHeight, "opening Advanced preserves output size");
            Equal(collapsed.SuperResolutionY, expanded.SuperResolutionY, "opening Advanced preserves super-resolution position");
            Equal(collapsed.SuperResolutionHeight, expanded.SuperResolutionHeight, "opening Advanced preserves super-resolution size");
            Equal(collapsed.AudioY, expanded.AudioY, "opening Advanced preserves audio position");
            Equal(collapsed.AudioHeight, expanded.AudioHeight, "opening Advanced preserves audio size");
            Equal(collapsed.AdvancedY, expanded.AdvancedY, "the advanced entry header never jumps to the top");
            Equal(44, collapsed.AdvancedHeight, "collapsed Advanced keeps its entry header");
            Equal(322, expanded.AdvancedHeight, "expanded Advanced adds its controls below the header");
            Equal(collapsed.ContentHeight + 278, expanded.ContentHeight, "expansion only adds its own body to the scrolling extent");
        }
    }

    private static void TestPanelCompactSuperResolution()
    {
        var compact = PanelLayoutForMask(0);
        var retainedRcas = PanelLayoutForMask(4 | 16);
        var anime = PanelLayoutForMask(2);
        var rcas = PanelLayoutForMask(6);
        var customRcas = PanelLayoutForMask(7);
        Equal(60, compact.SuperResolutionHeight, "disabled super resolution occupies one compact row");
        Equal(compact, retainedRcas, "hidden RCAS and bitrate preferences reserve no empty rows");
        Check(compact.ContentHeight < anime.ContentHeight && anime.ContentHeight < rcas.ContentHeight
            && rcas.ContentHeight < customRcas.ContentHeight, "visible controls add scrolling content");
        foreach (var layout in new[] { anime, rcas, customRcas })
        {
            Equal(compact.PanelHeight, layout.PanelHeight, "expanded content cannot grow the shell");
            Equal(compact.FooterY, layout.FooterY, "expanded content cannot move export actions");
        }
        Equal(0, compact.MaximumScroll, "the compact video audio and advanced header fit without scrolling");
        Check(anime.MaximumScroll > 0 && rcas.MaximumScroll > anime.MaximumScroll && customRcas.MaximumScroll > rcas.MaximumScroll,
            "expanded video controls overflow internally alongside audio");
    }

    private static void TestPanelAdvancedBitrate()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            var layout = PanelLayoutForMask(mask);
            Equal((mask & 8) != 0, layout.ShowCustomBitrate, "expanded Advanced always exposes editable bitrate controls");
            Equal(layout, PanelLayoutForMask(mask ^ 16), "switching recommended/custom bitrate cannot rearrange controls");
        }
    }

    private static void TestPanelScrollBounds()
    {
        // Independently specified content sizes detect accidental empty rows or
        // missing sections in the combined video/audio/advanced list.
        foreach (var (mask, contentHeight, maximumScroll) in new[]
        {
            (0, 410, 0), (1, 474, 48), (2, 514, 88), (6, 576, 150),
            (7, 640, 214), (8, 688, 262), (15, 918, 492)
        })
        {
            var layout = PanelLayoutForMask(mask);
            Equal(contentHeight, layout.ContentHeight, $"flags {mask} combined content height");
            Equal(maximumScroll, layout.MaximumScroll, $"flags {mask} real overflow");
            Equal(0f, layout.ClampScroll(-10), "negative scroll clamps at the top");
            Equal((float)maximumScroll, layout.ClampScroll(float.MaxValue), "overscroll clamps at the last row");
            float middle = maximumScroll / 2f;
            Equal(middle, layout.ClampScroll(middle), "fractional scroll within bounds is preserved");
            Equal(layout.ClampScroll(float.MaxValue), layout.ClampScroll(layout.ClampScroll(float.MaxValue)), "clamping is idempotent");
        }
        Equal(214f, PanelLayoutForMask(7).ClampScroll(492), "collapsing Advanced reveals the end of the remaining settings");
        Equal(0f, PanelLayoutForMask(0).ClampScroll(492), "collapsing all optional controls cannot leave blank scrolled content");
    }

    private static void TestPanelResponsiveViewports()
    {
        foreach (var (width, height) in new[]
        {
            (320f, 240f), (640f, 360f), (800f, 600f), (1280f, 720f), (1366f, 768f),
            (1920f, 1080f), (2560f, 720f), (3440f, 1440f), (720f, 1280f), (480f, 1920f), (128f, 96f)
        })
        {
            int panelHeight = ExportPanelLayout.HeightForViewport(width, height);
            Equal(800, panelHeight, "window size cannot stretch the design shell");
            var baseline = PanelLayoutForMask(0, panelHeight);
            float baselineScale = baseline.FitScale(width, height);
            for (int mask = 0; mask < 32; mask++)
            {
                var layout = PanelLayoutForMask(mask, panelHeight);
                float scale = layout.FitScale(width, height);
                float visibleWidth = ExportPanelLayout.PanelWidth * scale;
                float visibleHeight = layout.PanelHeight * scale;
                Check(float.IsFinite(scale) && scale > 0, $"invalid scale at {width} x {height}, flags {mask}");
                Check(visibleWidth <= width * .9f + .01f && visibleHeight <= height * .9f + .01f,
                    $"modal exceeds 90 percent of {width} x {height}, flags {mask}");
                Check(Math.Abs(visibleWidth - width * .9f) < .01f || Math.Abs(visibleHeight - height * .9f) < .01f,
                    "the fitting edge uses all of its available space");
                Check(Math.Abs(visibleWidth / visibleHeight - 1.225f) < .0001f, "the original wide aspect ratio is preserved");
                Equal(baselineScale, scale, "expansion cannot zoom controls");
                Equal(baseline.FooterY * baselineScale, layout.FooterY * scale, "footer retains its screen-space position");
            }
        }
    }

    private static void TestPanelResponsiveScale()
    {
        var layout = PanelLayoutForMask(15);
        Check(Math.Abs(layout.FitScale(1280, 720) - .81f) < .0001f, "720p uses uniform 90 percent vertical fitting");
        Check(Math.Abs(layout.FitScale(2560, 1440) - 1.62f) < .0001f, "1440p grows beyond the old 1.2 scale cap");
        Check(Math.Abs(layout.FitScale(2560, 1368) - 1.539f) < .0001f, "the reported client size keeps the original proportions");
        Equal(800, ExportPanelLayout.HeightForViewport(480, 1920), "portrait windows retain the same design height");
        foreach (var (width, height) in new[] { (0f, 0f), (-1f, 800f), (980f, -1f), (.01f, .01f) })
            Equal(.001f, layout.FitScale(width, height), "degenerate viewports keep a positive minimum scale");
    }

    private static void TestPanelRootScaling()
    {
        foreach (var (width, height, activeHeight) in new[]
        {
            (2560f, 1368f, 1368f), (1920f, 1080f, 1080f), (3440f, 1440f, 1440f),
            (1280f, 720f, 720f), (640f, 360f, 360f), (720f, 1280f, 1280f),
            (480f, 1920f, 1920f), (128f, 96f, 320f), (7680f, 8640f, 4320f)
        })
        {
            float activeWidth = activeHeight * width / height;
            var layout = PanelLayoutForMask(15, ExportPanelLayout.HeightForViewport(activeWidth, activeHeight));
            float pixelScale = layout.FitScale(activeWidth, activeHeight) * height / activeHeight;
            float visibleWidth = ExportPanelLayout.PanelWidth * pixelScale;
            float visibleHeight = layout.PanelHeight * pixelScale;
            string context = $"{width} x {height}, root {activeHeight}";
            Check(visibleWidth <= width * .90f + .01f && visibleHeight <= height * .90f + .01f,
                context + " retains margins after native root scaling");
            Check(Math.Abs(visibleWidth - width * .9f) < .01f || Math.Abs(visibleHeight - height * .9f) < .01f,
                context + " uses 90 percent of the fitting edge");
            Check(Math.Abs(visibleWidth / visibleHeight - 1.225f) < .0001f,
                context + " retains the original aspect ratio after root scaling");
        }
    }

    private static void TestPanelHeightBounds()
    {
        Equal(800, ExportPanelLayout.MinimumPanelHeight, "minimum design height preserves the original proportions");
        foreach (int requested in new[] { int.MinValue, -1, 0, 600, 799, 800, 801, 1094, 4181, int.MaxValue })
        for (int mask = 0; mask < 32; mask++)
            Equal(PanelLayoutForMask(mask), PanelLayoutForMask(mask, requested),
                $"legacy height {requested} cannot alter the original shell, flags {mask}");
    }
}
