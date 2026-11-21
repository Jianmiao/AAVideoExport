# Prepared background preservation behavior tests

This project compiles the production `ControlledBackgroundVideoAdapter.cs` unchanged against an explicit **managed Unity boundary fixture**. It does not load a real Unity decoder, launch AA, modify installed Mods, or constitute native acceptance. The fixture models the Pause/Stop state transitions measured by native runs 44–48. Its local function pointers only exercise the production reader's checked binding shape.

Run from the repository root:

```powershell
dotnet run --project tests/VideoExport.BackgroundPreservation.Tests/VideoExport.BackgroundPreservation.Tests.csproj -c Release -p:AAInstallPath=F:\AzureArchive_101 -- F:\AzureArchive_101
```

The tests execute production snapshot capture, Pause, Pump, EndExport, a second BeginExport, and the public render lease's AfterRelease. They check:

- Prepared playing/paused state survives the entire held lease at the actual frozen nonzero position; original playing state resumes only after release.
- Natural advancement during Pause is captured after the pause boundary. A failed Pause retains its rollback owner and recovers the original mode only after release.
- Queued, unprepared starts are stopped and deactivated, then wait for preparation before resuming.
- Frame, autonomous playback, and settings drift fail the production boundary, including non-active intervals between passes.
- Original root callbacks between passes clear only the export scope's cache; later BeginExport never re-imports a stale original-context cache, even after returning to the original context.
- Same/no-context notifications are harmless; active-pass context changes latch failure; unowned restoration requests replay in order after detachment; queue overflow is explicit.
- Existing native callback subscribers survive observer installation/removal.

Each run writes a scoped `results.json` below its output directory. Native evidence remains separate: the real helper must independently observe the frozen playhead across ownership, cancellation/replay gaps, release, and actual post-release progress, plus the real Flandre root callback and Azurite FPS/VSync restoration.

Recorded red-capable evidence during development:

- `preservation-7953a8fec71e4906b7fcb5553c752130`: pre-Pause position capture fails when the player advances from 0.3/frame 3 to 0.4/frame 4 during Pause.
- `preservation-4f6e7eaad2df4bef8aff63e6065ad354`: the unguarded original root callback destroys the retained native playhead between passes.
- `preservation-985e6ce07a074b5cb015925c7111f85c`: a test-only production-source copy with preservation disabled produces five behavior failures. The production source was not modified for that mutation.

These are intermediate implementation checks. Repeated full AA movie export and real native restoration remain mandatory acceptance.
