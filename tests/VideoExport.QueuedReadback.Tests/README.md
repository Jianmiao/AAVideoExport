# Queued GPU readback lifecycle regressions (portable)

Run from the repository root:

```sh
dotnet run --project tests/VideoExport.QueuedReadback.Tests -c Release
```

This .NET 6 console harness has no NuGet, GPU, Unity or FFmpeg dependency. It
links the **production** `QueuedVideoFrameWriter`, `ExportSession`, options and
canvas source. `BoundaryStubs.cs` explicitly replaces the OS eligibility check,
D3D GPU, encoder preflight/process, encoder arguments and final media verifier.
Those fakes let Linux exercise the production session lifecycle; they are not
proof of native D3D correctness, real media validity or AA audio synchronization.

Coverage:

- One-slot compatibility and four-slot batching, short 1/2/3-frame tails,
  wraparound, 10,000-frame bounded FIFO, input snapshot and reusable output.
- Managed and native-pointer input; caller reuses/frees input after submission.
- Accepted versus pipe-written counts and capture-clock audio duration.
- Complete drains before closing video input and verifying the final frame count.
- Cancellation before/after submit, before/after receive, during sink and during
  tail drain; no drain from abort/dispose and no subsequent sink output.
- Cross-thread cancellation while a mock synchronous pipe blocks, during both
  capture and completion; Kill releases the writer before owner disposal.
- Submit/readback/pipe/mux failures, owned-resource cleanup and same-name retry;
  short-tail pipe errors retain encoder-failed/disk-full diagnostics.
- Preflight four-slot refusal retries one slot; NV12 refusal retries RGBA;
  complete D3D refusal preserves the existing external upscaler fallback.
- Stable one-slot default and rejection of unsupported public option values.

Actual GPU byte/order tests are added to `VideoExport.Core.Tests --d3d-media`.
That hardware suite requires Windows plus NVIDIA and AMD; missing hardware is
an error. The suite now compares 1/4 slots against the one-frame reference for
landscape, portrait and 1918-pixel-wide RGBA fallback, and exercises actual
session short-tail completion/cancel/retry with both slot counts. Actual
NVIDIA/AMD 1/7-frame clips additionally verify decoded frame markers plus every
PCM sample for one and four slots. Full AA
story A/B, long audio sync and Intel remain separate acceptance requirements.
