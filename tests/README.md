# Validation

From the repository root, run the portable functional suite with PowerShell 7
and a .NET SDK capable of building `net6.0` (plus the .NET 6 runtime):

```powershell
./tools/Test-PortableReview.ps1
```

The runner covers 23 groups for export geometry/settings, 720p SR tiers,
encoder-selection policy, launch/cancellation flow, output path retention,
audio/control visibility, AUTO selection, frame pipeline and queued readback lifecycle. It uses
synthetic/stubbed inputs and does not launch AA, call a GPU encoder or run a
performance benchmark. First restore requires the project's pinned NuGet
dependencies. Logs and the JSON result are written under `artifacts/portable-tests`
or an explicit `-OutputDirectory`.

The Core harness also contains real FFmpeg/GPU integration tests, including an
explicit benchmark mode. See [Core test requirements](VideoExport.Core.Tests/README.md)
before selecting these branches. They are not part of the portable runner.

Actual AA UI acceptance is a separately enabled developer plugin. Its setup,
diagnostic directory and cleanup requirements are documented in
[Native UI fixtures](VideoExport.NativePath.Tests/README.md). The test DLL is
never part of `0.1.0` or a release package.
