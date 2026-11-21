# Sealed preparation timeline and final render boundary

Run without AA or FFmpeg:

```powershell
dotnet run --project tests/VideoExport.StrictTimeline.Tests/VideoExport.StrictTimeline.Tests.csproj -c Release
```

The test executable source-links `StrictFramePlan` and `ExportEndFence`. It
checks exact integer frame/sample budgets, frozen source/clock identity, and
the same final frame when AA signals `End` before versus after a render
callback in that Unity frame. It also rejects overrun, underflow, repeated
boundary extension, and a single missing/extra audio sample.

The end fence does **not** determine a story's duration independently. A
preparation pass must create the immutable plan. The video pass must validate
the source identity and its real native event sequence against that plan;
comparing final counts alone does not establish content synchronization.

These tests do not execute AA, Harmony, Unity's mixer, a real scenario, or
encoding. They cannot demonstrate real-world sample origin, repeat-export
determinism, or audiovisual synchronization by themselves.
