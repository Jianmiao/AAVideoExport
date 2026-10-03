using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestCanvasLayouts()
    {
        await Test("same-aspect canvas is identity at each output resolution", () => { TestCanvasIdentity(); return Task.CompletedTask; });
        await Test("viewport renders exact selected landscape, portrait, square and 4K targets without geometry filters", () => { TestCanvasViewport(); return Task.CompletedTask; });
        await Test("fit preserves full landscape/portrait composition and centers the padding", () => { TestCanvasFit(); return Task.CompletedTask; });
        await Test("fill crops the center of landscape/portrait composition without scaling", () => { TestCanvasFill(); return Task.CompletedTask; });
        await Test("canvas pixel quantization preserves intended aspect within one pixel", () => { TestCanvasAspectQuantization(); return Task.CompletedTask; });
        await Test("canvas settings default to viewport and accept odd source aspect inputs", () => { TestCanvasSettingsCompatibility(); return Task.CompletedTask; });
        await Test("invalid canvas choices fail before allocation and extreme fill offers a safe alternative", () => { TestCanvasInvalid(); return Task.CompletedTask; });
    }

    private static void TestCanvasIdentity()
    {
        foreach (string mode in new[] { "viewport", "fit", "fill" })
        foreach (var output in new[] { (1280, 720), (1920, 1080), (3840, 2160), (7680, 4320) })
        {
            var layout = CanvasLayout.Create(1920, 1080, output.Item1, output.Item2, mode);
            Equal(output.Item1, layout.CaptureWidth, "identity capture width");
            Equal(output.Item2, layout.CaptureHeight, "identity capture height");
            Check(layout.IsIdentity, "resolution-only changes must not add padding or cropping");
            Check(layout.VideoFilter is null, "same aspect must bypass all geometry filters");
            Equal(0, layout.OffsetX, "identity x");
            Equal(0, layout.OffsetY, "identity y");
        }
    }

    private static void TestCanvasViewport()
    {
        foreach (var source in new[] { (2880, 1800), (1365, 767) })
        foreach (var output in new[] { (1920, 1080), (1080, 1920), (1080, 1080), (3840, 2160) })
        {
            var options = Options("viewport-settings") with
            {
                SourceWidth = source.Item1, SourceHeight = source.Item2,
                Width = output.Item1, Height = output.Item2
            };
            var original = options with { };
            options.Validate();
            var layout = options.GetCanvasLayout();
            Equal("viewport", layout.Mode, "default mode renders the selected target viewport");
            Equal(output.Item1, layout.CaptureWidth, "viewport capture uses the selected width regardless of source aspect");
            Equal(output.Item2, layout.CaptureHeight, "viewport capture uses the selected height regardless of source aspect");
            Equal(output.Item1, layout.OutputWidth, "viewport output retains selected width");
            Equal(output.Item2, layout.OutputHeight, "viewport output retains selected height");
            Equal(0, layout.OffsetX, "viewport has no horizontal crop or padding");
            Equal(0, layout.OffsetY, "viewport has no vertical crop or padding");
            Check(layout.IsIdentity, "viewport capture is already the exact output size");
            Check(layout.VideoFilter is null, "viewport must not crop, pad or scale the rendered frame");
            Equal(original, options, "viewport layout cannot rewrite the requested resolution or source dimensions");
        }
    }

    private static void TestCanvasFit()
    {
        var wide = CanvasLayout.Create(1920, 1080, 1080, 1080, "fit");
        Equal(1080, wide.CaptureWidth, "fit wide width");
        Equal(608, wide.CaptureHeight, "fit wide height rounded to even pixel");
        Equal("pad=1080:1080:0:236:color=black,setsar=1", wide.VideoFilter, "fit wide centered padding");
        var tall = CanvasLayout.Create(1080, 1920, 1080, 1080, "fit");
        Equal(608, tall.CaptureWidth, "fit tall width");
        Equal(1080, tall.CaptureHeight, "fit tall height");
        Equal("pad=1080:1080:236:0:color=black,setsar=1", tall.VideoFilter, "fit tall centered padding");
        var portraitCanvas = CanvasLayout.Create(1920, 1080, 1080, 1920, "fit");
        Equal("pad=1080:1920:0:656:color=black,setsar=1", portraitCanvas.VideoFilter, "landscape on portrait canvas");
    }

    private static void TestCanvasFill()
    {
        var wide = CanvasLayout.Create(1920, 1080, 1080, 1080, "fill");
        Equal(1920, wide.CaptureWidth, "fill wide width");
        Equal(1080, wide.CaptureHeight, "fill wide height");
        Equal("crop=1080:1080:420:0,setsar=1", wide.VideoFilter, "fill removes equal left and right regions");
        var tall = CanvasLayout.Create(1080, 1920, 1080, 1080, "fill");
        Equal("crop=1080:1080:0:420,setsar=1", tall.VideoFilter, "fill removes equal top and bottom regions");
        var portraitCanvas = CanvasLayout.Create(1920, 1080, 1080, 1920, "fill");
        Equal(3414, portraitCanvas.CaptureWidth, "fill renders enough width at the final pixel density");
        Equal(1920, portraitCanvas.CaptureHeight, "fill output height");
        Equal("crop=1080:1920:1167:0,setsar=1", portraitCanvas.VideoFilter, "portrait fill is centered");
    }

    private static void TestCanvasAspectQuantization()
    {
        foreach (var source in new[] { (1920, 1080), (1080, 1920), (1440, 1080), (1365, 767), (2880, 1704) })
        foreach (var output in new[] { (1080, 1080), (1920, 1080), (1080, 1920), (1280, 960) })
        foreach (string mode in new[] { "fit", "fill" })
        {
            var layout = CanvasLayout.Create(source.Item1, source.Item2, output.Item1, output.Item2, mode);
            double widthError = Math.Abs(layout.CaptureWidth - layout.CaptureHeight * (double)source.Item1 / source.Item2);
            double heightError = Math.Abs(layout.CaptureHeight - layout.CaptureWidth * (double)source.Item2 / source.Item1);
            Check(Math.Min(widthError, heightError) <= 1.000001, "geometry may only quantize one axis by up to one pixel");
            Equal(0, layout.CaptureWidth % 2, "even width");
            Equal(0, layout.CaptureHeight % 2, "even height");
            Check(mode == "fit" ? layout.CaptureWidth <= output.Item1 && layout.CaptureHeight <= output.Item2
                : layout.CaptureWidth >= output.Item1 && layout.CaptureHeight >= output.Item2,
                "fit must contain the full image and fill must cover the canvas");
        }
    }

    private static void TestCanvasSettingsCompatibility()
    {
        var options = Options("canvas-settings");
        options.Validate();
        Equal("viewport", options.CanvasMode, "default renders a viewport at the selected resolution");
        var legacy = options.GetCanvasLayout();
        Equal(Width, legacy.CaptureWidth, "legacy raw width");
        Equal(Height, legacy.CaptureHeight, "legacy raw height");
        Check(legacy.VideoFilter is null, "legacy callers need no new geometry filter");
        var oddSource = options with { SourceWidth = 1365, SourceHeight = 767, Width = 1080, Height = 1080, CanvasMode = "fit" };
        oddSource.Validate();
        Equal(606, oddSource.GetCanvasLayout().CaptureHeight, "source dimensions are an aspect ratio, not even raw pixel dimensions");
        var frozen = oddSource with { };
        _ = oddSource.GetCanvasLayout();
        Equal(frozen, oddSource, "layout calculation cannot mutate user options");
    }

    private static void TestCanvasInvalid()
    {
        var options = Options("invalid-canvas");
        foreach (var invalid in new[]
        {
            options with { CanvasMode = "stretch" }, options with { CanvasMode = "" },
            options with { SourceWidth = 1920 }, options with { SourceHeight = 1080 },
            options with { SourceWidth = -1920, SourceHeight = 1080 }
        }) ThrowsCode("invalid_settings", invalid.Validate, "invalid canvas setting");
        ThrowsCode("invalid_settings", () => CanvasLayout.Create(1920, 1080, 1079, 1080), "odd output");
        ThrowsCode("invalid_settings", () => CanvasLayout.Create(1920, 1080, 0, 1080), "zero output");
        ThrowsCode("invalid_settings", () => CanvasLayout.Create(1920, 1080, 4320, 7680, "fill"), "8K portrait fill cannot allocate a 13654-wide render target");
        var safe = CanvasLayout.Create(1920, 1080, 4320, 7680, "fit");
        Equal(4320, safe.CaptureWidth, "fit remains available for the same 8K portrait file");
        ThrowsCode("invalid_settings", () => CanvasLayout.Create(int.MaxValue, 1, 1080, 1080, "fill"), "extreme source aspect cannot overflow allocation checks");
    }
}
