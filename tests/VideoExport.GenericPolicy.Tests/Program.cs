using AAVideoExport.Plugin;

if (args.Length != 3)
    throw new Exception("usage: GenericPolicy.Tests <passive.dll> <clock.dll> <render.dll>");

Check(true, "passive fixture must classify as static inert", args[0], GenericStaticModPolicy.EvidenceMarker);
Check(false, "clock fixture must be rejected", args[1], "managed-clock-call-sites");
Check(false, "render fixture must be rejected", args[2], "render-audio-video-references");

Console.WriteLine("GENERIC STATIC POLICY TESTS: PASS");
return;

static void Check(bool expected, string message, string path, string expectedEvidence)
{
    if (!File.Exists(path)) throw new Exception("missing fixture: " + path);
    bool actual = GenericStaticModPolicy.TryClassify(path, out var evidence);
    if (actual != expected) throw new Exception($"FAILED: {message}; actual={actual}; evidence={string.Join("|", evidence)}");
    if (!evidence.Any(value => value.Contains(expectedEvidence, StringComparison.Ordinal)))
        throw new Exception($"FAILED: {message}; missing evidence {expectedEvidence}; evidence={string.Join("|", evidence)}");
    Console.WriteLine($"PASS {Path.GetFileName(path)} => {string.Join(" | ", evidence)}");
}
