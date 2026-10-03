using AAVideoExport.Core;

var tests = new (string Name, Action Run)[]
{
    ("16:10 is recognised without reducing the user-facing label", () =>
    {
        Equal("16:10", OutputDimensions.AspectKey(2880, 1800, 0, 0));
        Equal("16:10", OutputDimensions.RatioLabel(2880, 1800));
        Equal("16:10", OutputDimensions.RatioLabel(1440, 900));
    }),
    ("named ratio preserves width and computes an even height", () =>
    {
        Equal((2880, 1620), OutputDimensions.ApplyAspect(2880, 1800, "16:9", 0, 0));
        Equal((2880, 2880), OutputDimensions.ApplyAspect(2880, 1800, "1:1", 0, 0));
        Equal((2880, 1800), OutputDimensions.ApplyAspect(2880, 1800, "16:10", 0, 0));
    }),
    ("native ratio uses the supplied source dimensions", () =>
    {
        Equal("native", OutputDimensions.AspectKey(1366, 768, 1366, 768));
        Equal((2880, 1620), OutputDimensions.ApplyAspect(2880, 1800, "native", 16, 9));
        Equal((2880, 1620), OutputDimensions.ApplyAspect(2880, 1800, "original", 16, 9));
    }),
    ("custom preserves manually entered dimensions", () =>
    {
        Equal((1610, 1000), OutputDimensions.ApplyAspect(1610, 1000, "custom", 1920, 1080));
        Equal("custom", OutputDimensions.AspectKey(1610, 1000, 1920, 1080));
        Equal("161:100", OutputDimensions.RatioLabel(1610, 1000));
    }),
    ("unsupported ratios and dimensions fail before allocation", () =>
    {
        Throws(() => OutputDimensions.ApplyAspect(1920, 1080, "2:1", 0, 0));
        Throws(() => OutputDimensions.ApplyAspect(7680, 4320, "1:1", 0, 0));
        Throws(() => OutputDimensions.ApplyAspect(1920, 1080, "native", 0, 0));
    })
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, actual {actual}.");
}
static void Throws(Action action)
{
    try { action(); throw new Exception("Expected ExportException."); }
    catch (ExportException error) when (error.Code == "invalid_settings") { }
}
