# AUTO selection regression tests

This harness links the production selection controller and Harmony hooks. Its
managed fixtures model the installed AA coroutine property layout, including a
finished AUTO countdown whose simulated click never submits the choice. Actual
Harmony patches must recover that path, keep the native delay, and leave other
players and ordinary playback alone. This is a functional regression test, not
a full native video or synchronization test.

Run with `dotnet run --project tests/VideoExport.Selection.Tests -c Release`.
The project uses the same pinned HarmonyX dependency as compatibility tests, or
the configured local AA Harmony DLL. The portable runner includes this group;
the offline builder also compiles it and stages its runtime dependencies under
the ignored build output. No AA binding or game binary is distributed.

Native isolation results and scope are documented in
[AUTO export repair](../../docs/auto-selection-export.md).
