using AAVideoExport.Core;

var passed = 0;
var failed = 0;
Run("play and stop produce one exact segment", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 100, StrictAudioOperation.Play));
    model.Apply(C(1, 300, StrictAudioOperation.Stop));
    Equal(1, model.Segments.Count); Equal(100L, model.Segments[0].StartSample); Equal(300L, model.Segments[0].EndSample);
});
Run("pause and unpause split the source at exact samples", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 10, StrictAudioOperation.Play)); model.Apply(C(1, 40, StrictAudioOperation.Pause));
    model.Apply(C(2, 70, StrictAudioOperation.UnPause)); model.Apply(C(3, 100, StrictAudioOperation.Stop));
    Equal(2, model.Segments.Count); Equal(10L, model.Segments[0].StartSample); Equal(40L, model.Segments[0].EndSample);
    Equal(70L, model.Segments[1].StartSample); Equal(100L, model.Segments[1].EndSample);
});
Run("overlapping OneShot remains an independent exact segment", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 0, StrictAudioOperation.Play));
    model.Apply(new StrictAudioCommand(1, 20, "source", StrictAudioOperation.OneShot, "sfx", 80));
    model.Apply(C(2, 100, StrictAudioOperation.Stop));
    Equal(2, model.Segments.Count); Check(model.Segments[0].OneShot, "OneShot was merged into the source segment");
    Equal(20L, model.Segments[0].StartSample); Equal(100L, model.Segments[0].EndSample);
    Equal(0L, model.Segments[1].StartSample); Equal(100L, model.Segments[1].EndSample);
});
Run("seek closes the old segment and starts at the new clip offset", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 10, StrictAudioOperation.Play));
    model.Apply(new StrictAudioCommand(1, 50, "source", StrictAudioOperation.Seek, "clip", 0, 900));
    model.Apply(C(2, 80, StrictAudioOperation.Stop));
    Equal(2, model.Segments.Count); Equal(900L, model.Segments[1].ClipOffsetSamples);
});
Run("volume and pitch changes split segments at exact samples", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 0, StrictAudioOperation.Play));
    model.Apply(new StrictAudioCommand(1, 40, "source", StrictAudioOperation.SetVolume, "clip", parameter: .5f));
    model.Apply(new StrictAudioCommand(2, 70, "source", StrictAudioOperation.SetPitch, "clip", parameter: 1.25f));
    model.Apply(C(3, 100, StrictAudioOperation.Stop));
    Equal(3, model.Segments.Count); Equal(1f, model.Segments[0].Volume); Equal(.5f, model.Segments[1].Volume);
    Equal(1.25f, model.Segments[2].Pitch); Equal(40L, model.Segments[1].StartSample); Equal(70L, model.Segments[2].StartSample);
});
Run("invalid volume and pitch fail without mutating the command sequence", () =>
{
    var model = new StrictAudioCommandModel(); model.Apply(C(0, 0, StrictAudioOperation.Play));
    Throws("audio_command_parameter", () => model.Apply(new StrictAudioCommand(1, 10, "source", StrictAudioOperation.SetVolume, "clip", parameter: -1f)));
    model.Apply(new StrictAudioCommand(1, 10, "source", StrictAudioOperation.SetVolume, "clip", parameter: .5f));
    Throws("audio_command_parameter", () => model.Apply(new StrictAudioCommand(2, 20, "source", StrictAudioOperation.SetPitch, "clip", parameter: 0f)));
});
Run("playing a new clip closes the reused source segment", () =>
{
    var model = new StrictAudioCommandModel();
    model.Apply(C(0, 0, StrictAudioOperation.Play));
    model.Apply(new StrictAudioCommand(1, 50, "source", StrictAudioOperation.Play, "clip-2"));
    model.Apply(new StrictAudioCommand(2, 90, "source", StrictAudioOperation.Stop, "clip-2"));
    Equal(2, model.Segments.Count); Equal("clip", model.Segments[0].ClipId); Equal("clip-2", model.Segments[1].ClipId);
    Equal(50L, model.Segments[1].StartSample); Equal(90L, model.Segments[1].EndSample);
});
Run("invalid state and order fail closed", () =>
{
    var model = new StrictAudioCommandModel();
    Throws("audio_command_state", () => model.Apply(C(0, 1, StrictAudioOperation.Stop)));
    model.Apply(C(0, 10, StrictAudioOperation.Play));
    Throws("audio_command_order", () => model.Apply(C(2, 9, StrictAudioOperation.Stop)));
});
Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

StrictAudioCommand C(long sequence, long sample, StrictAudioOperation operation) => new(sequence, sample, "source", operation, "clip");
void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
void Throws(string code, Action action) { try { action(); } catch (ExportException error) { if (error.Code == code) return; throw; } throw new Exception("Expected " + code + "."); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
void Check(bool value, string message) { if (!value) throw new Exception(message); }
