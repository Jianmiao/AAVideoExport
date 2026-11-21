using System.Text.Json;
using System.Text.Json.Nodes;
using AAVideoExport.NativePath.Tests;

// Hand-authored receipt-shaped inputs exercise the acceptance comparator only.
// They are not native-export, effect-rendering, or restoration evidence.
int passed = 0, failed = 0;
var results = new List<object>();
void Test(string name, Action run)
{
    try { run(); passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Reject(Action run, string detail)
{
    try { run(); }
    catch (InvalidDataException error) { if (!error.Message.Contains(detail, StringComparison.OrdinalIgnoreCase)) throw new Exception("Wrong failure: " + error.Message); return; }
    throw new Exception("Missing rejection: " + detail);
}
ControlledStoryPassTrace Trace(int attempt, bool preparation, Action<JsonObject>? edit = null)
{
    var events = new object[] {
        new { kind = "pass-begin", frame = 0, detail = new { Attempt = attempt, Preparation = preparation, Fps = 30 } },
        new { kind = "native-row", frame = 0, detail = new { row = 0, isBackgroundLatest = false } },
        new { kind = "original-layer-rendered", frame = 1, detail = new { isBackgroundLatest = true, visible = true,
            layer = "layer-" + attempt + preparation, image = "image-" + attempt + preparation, texture = "owned-background", material = "UI/Default" } },
        new { kind = "original-title-ready", frame = 1, detail = new { isBackgroundLatest = true } },
        new { kind = "original-title-complete", frame = 3, detail = new { row = 0, hasCompleted = true } },
        new { kind = "native-row", frame = 4, detail = new { row = 1, isBackgroundLatest = true } },
        new { kind = "original-preset-success", frame = 4, detail = new { kind = "spin", directive = "aavt;character;spin;1;100", row = 1 } },
        new { kind = "voice-receipt-sealed", frame = 12, detail = new { trace = new string('a', 64) } },
        new { kind = "host-pass-finished", frame = 12, detail = new { CapturedFrames = 12, hasVoiceReceipt = true } }
    };
    var receipt = new {
        Scope = "known-voice-scope", StorySha256 = new string('1', 64), Fps = 30, SampleRate = 48000,
        SettingsIdentity = "volume=1", ManifestSha256 = new string('2', 64), NoOwnedVoiceExpected = false,
        Assets = new[] { new { Identifier = "aavt/voice/fixture", FileSha256 = new string('3', 64), DecodedPcmSha256 = new string('4', 64),
            SampleFrames = 1600, Channels = 2, SampleRate = 48000, AssetId = new string('5', 64) } },
        Events = new[] { new { Index = 0, RequestOrdinal = 0, SourceId = "selected-story", VoiceIdentifier = "aavt/voice/fixture", AssetId = new string('5', 64),
            Operation = "Request", Position = new { Frame = 4, SampleFrame = 6400, Row = 1 }, PostDelaySeconds = .3, Volume = (float?)1, Pitch = (float?)1 } },
        End = new { Frame = 12, SampleFrame = 19200, Row = 1 }, TraceSha256 = new string('a', 64)
    };
    var commands = new[] { new { Sequence = 0, Frame = 0, Operation = "Start", Layer = 0, Resource = "authored/background", SourceSha256 = new string('6', 64), SourceFrame = 0 } };
    var payload = JsonSerializer.SerializeToNode(new { Events = events, VoiceReceipt = receipt, BackgroundCommands = commands })!.AsObject();
    edit?.Invoke(payload);
    return new ControlledStoryPassTrace(attempt, preparation, 30, 12,
        JsonSerializer.SerializeToElement(payload["Events"]), JsonSerializer.SerializeToElement(payload["VoiceReceipt"]), JsonSerializer.SerializeToElement(payload["BackgroundCommands"]));
}
ControlledStoryPassTrace[] Pair() => new[] { Trace(1, true), Trace(1, false), Trace(2, true), Trace(2, false) };
void RequireChangedRejected(Action<JsonObject> edit, string detail, bool bothSecondPasses = true)
{
    var passes = Pair(); passes[3] = Trace(2, false, edit);
    if (bothSecondPasses) passes[2] = Trace(2, true, edit);
    Reject(() => ControlledStoryTrajectory.Validate(passes, 2), detail);
}
Test("identical authored events survive native pointers and attempt metadata changes", () => ControlledStoryTrajectory.Validate(Pair(), 2));
Test("one-frame authored-event drift between preparation and replay is rejected", () =>
    RequireChangedRejected(p => p["Events"]![6]!["frame"] = 5, "authored events", false));
Test("the same one-frame drift in both passes of the second movie is rejected", () =>
    RequireChangedRejected(p => p["Events"]![6]!["frame"] = 5, "authored events"));
Test("missing authored event is rejected despite equal movie length", () =>
    RequireChangedRejected(p => p["Events"]!.AsArray().RemoveAt(6), "authored events"));
Test("duplicate authored event is rejected despite equal movie length", () =>
    RequireChangedRejected(p => p["Events"]!.AsArray().Insert(6, JsonNode.Parse(p["Events"]![6]!.ToJsonString())), "authored events"));
Test("reordered events at the same frame are rejected", () =>
    RequireChangedRejected(p => { var values = p["Events"]!.AsArray(); var row = values[5]; values.RemoveAt(5); values.Insert(6, row); }, "authored events"));
Test("changed effect directive is rejected at an unchanged frame", () =>
    RequireChangedRejected(p => p["Events"]![6]!["detail"]!["directive"] = "aavt;character;spin;2;100", "authored events"));
Test("native visibility changes remain part of event comparison", () =>
    RequireChangedRejected(p => p["Events"]![2]!["detail"]!["visible"] = false, "authored events"));
Test("changed voice receipt in both passes cannot choose a fresh baseline", () =>
    RequireChangedRejected(p => p["VoiceReceipt"]!["TraceSha256"] = new string('b', 64), "voice receipt"));
Test("voice sample timing is checked even when the reported digest is unchanged", () =>
    RequireChangedRejected(p => p["VoiceReceipt"]!["Events"]![0]!["Position"]!["SampleFrame"] = 6401, "voice receipt"));
Test("voice asset identity is compared independently of the trace digest", () =>
    RequireChangedRejected(p => p["VoiceReceipt"]!["Assets"]![0]!["FileSha256"] = new string('b', 64), "voice receipt"));
Test("changed background command frame in both passes is rejected", () =>
    RequireChangedRejected(p => p["BackgroundCommands"]![0]!["Frame"] = 1, "background commands"));
Test("background source-frame changes are not normalized away", () =>
    RequireChangedRejected(p => p["BackgroundCommands"]![0]!["SourceFrame"] = 1, "background commands"));
Test("unknown observed event kinds fail closed", () =>
    RequireChangedRejected(p => p["Events"]![6]!["kind"] = "unreviewed-event", "unknown event"));
Test("unknown event-detail fields cannot silently disappear", () =>
    RequireChangedRejected(p => p["Events"]![6]!["detail"]!["newTiming"] = 8, "schema"));
Test("negative event frames fail closed", () =>
    RequireChangedRejected(p => p["Events"]![6]!["frame"] = -1, "frame"));
Test("empty event trajectories cannot prove repeated behavior", () =>
    RequireChangedRejected(p => p["Events"] = new JsonArray(), "empty"));
Test("incomplete second attempt cannot pass repeat acceptance", () =>
    Reject(() => ControlledStoryTrajectory.Validate(Pair().Take(3).ToArray(), 2), "attempt"));
Test("evidence keeps normalized frame and sample records for inspection", () =>
{
    var evidence = ControlledStoryTrajectory.Validate(Pair(), 2);
    if (evidence.GetProperty("Passes").GetArrayLength() != 4 || evidence.GetProperty("BaselineSha256").GetString()!.Length != 64)
        throw new Exception("Completed trajectory evidence omitted passes or digest.");
});
ControlledStoryPassTrace ProductionTrace(int attempt,bool preparation,Action<JsonObject>? edit=null)
{
    var original=Trace(attempt,preparation);
    var events=JsonNode.Parse(original.Events.GetRawText())!.AsArray();
    events.RemoveAt(7);
    events[7]!["kind"]="host-production-pass-finished";
    events[7]!["detail"]=new JsonObject{["CapturedFrames"]=12,["hasVoiceObservation"]=true};
    events.Insert(7,JsonSerializer.SerializeToNode(new{kind="voice-observation-complete",frame=12,detail=new{requests=1}}));
    var observation=JsonSerializer.SerializeToNode(new{
        Schema="aave.native-test-voice.v1",Fps=30,SampleRate=48000,
        Assets=new[]{new{Identifier="aavt/voice/fixture",FileSha256=new string('3',64),DecodedPcmSha256=new string('4',64),SampleFrames=1600,Channels=2,SampleRate=48000}},
        Events=new[]{"Request","NativeStart","NativeEnd","DelayComplete"}.Select((operation,index)=>new{Index=index,RequestOrdinal=0,Operation=operation,VoiceIdentifier="aavt/voice/fixture",Position=new{Frame=4+index,SampleFrame=(4+index)*1600,Row=1},PostDelaySeconds=.3,Volume=operation=="NativeStart"?(float?)1:null,Pitch=operation=="NativeStart"?(float?)1:null}).ToArray(),
        End=new{Frame=12,SampleFrame=19200,Row=1}
    })!.AsObject();edit?.Invoke(observation);
    var trace=new ControlledStoryPassTrace(attempt,preparation,30,12,JsonSerializer.SerializeToElement(events),default,original.BackgroundCommands);
    var property=typeof(ControlledStoryPassTrace).GetProperty("VoiceObservation")??throw new Exception("External production voice evidence is not supported.");
    property.SetValue(trace,JsonSerializer.SerializeToElement(observation));
    typeof(ControlledStoryPassTrace).GetProperty("PcmObservation")!.SetValue(trace,JsonSerializer.SerializeToElement(new{Schema="aave.native-test-pcm.v1",Frames=12,Fps=30,SampleRate=48000,Channels=2,SampleFrames=19200,ContentSha256=new string('c',64),RawSampleFrames=19456,TailSampleFrames=256}));return trace;
}
Test("production trajectories compare external native observations without product receipts",()=>ControlledStoryTrajectory.Validate(new[]{ProductionTrace(1,true),ProductionTrace(1,false),ProductionTrace(2,true),ProductionTrace(2,false)},2));
Test("external native voice sample drift is rejected",()=>{
    var p=new[]{ProductionTrace(1,true),ProductionTrace(1,false),ProductionTrace(2,true),ProductionTrace(2,false,o=>o["Events"]![1]!["Position"]!["SampleFrame"]=8001)};
    Reject(()=>ControlledStoryTrajectory.Validate(p,2),"external voice observation");
});
Test("all production passes missing a native voice end cannot pass by matching each other",()=>{
    var p=new[]{ProductionTrace(1,true,o=>o["Events"]!.AsArray().RemoveAt(2)),ProductionTrace(1,false,o=>o["Events"]!.AsArray().RemoveAt(2))};
    Reject(()=>ControlledStoryTrajectory.Validate(p,1),"lifecycle");
});
Test("production native replay PCM change is rejected independently of movie output",()=>{
    var p=new[]{ProductionTrace(1,true),ProductionTrace(1,false)};var pcm=JsonNode.Parse(p[1].PcmObservation!.Value.GetRawText())!.AsObject();pcm["ContentSha256"]=new string('d',64);p[1]=p[1] with{PcmObservation=JsonSerializer.SerializeToElement(pcm)};Reject(()=>ControlledStoryTrajectory.Validate(p,1),"native authored PCM");
});
JsonElement SinglePass(IReadOnlyList<ControlledStoryPassTrace> passes, int expected)
{
    var method = typeof(ControlledStoryTrajectory).GetMethod("ValidateSinglePass", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("Missing explicit single-pass trajectory comparator.");
    try { return (JsonElement)method.Invoke(null, new object[] { passes, expected })!; }
    catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException invalid) { throw invalid; }
}
Test("single-pass accepts exactly one original native trajectory per completed movie", () =>
    SinglePass(new[] { ProductionTrace(1, false), ProductionTrace(2, false) }, 2));
Test("single-pass cannot reinterpret preparation and replay as independent success", () =>
    Reject(() => SinglePass(new[] { ProductionTrace(1, true), ProductionTrace(1, false) }, 1), "single"));
Test("single-pass rejects missing repeated attempt", () =>
    Reject(() => SinglePass(new[] { ProductionTrace(1, false) }, 2), "single"));
Test("single-pass repeat retains voice sample timing", () =>
    Reject(() => SinglePass(new[] { ProductionTrace(1, false), ProductionTrace(2, false, o => o["Events"]![1]!["Position"]!["SampleFrame"] = 8001) }, 2), "external voice observation"));
Console.WriteLine($"RESULT {passed} passed; {failed} failed");
if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new {
    Passed = passed, Failed = failed, Tests = results,
    Scope = "Pure event/receipt comparison regression only; no real AA export or restoration claim."
}, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
