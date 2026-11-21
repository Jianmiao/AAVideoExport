# Scoped runtime adapter regression

```powershell
dotnet run --project tests/VideoExport.ScopedRuntime.Tests -c Release -p:AAInstallPath=F:/AzureArchive_101
```

This project source-links production `MultilingualCaptureAdapter`,
`RuntimeModBoundaries`, `CompatibilityBuild`, and `RenderControlV1`. It uses
the installed Harmony with managed translator/driver/surface fixtures and small
UILabel/UIPanel substitutes. It never launches AA or instantiates native Unity.

The exact-build guard remains in production TryInstall. The translation fixture
uses the shared BindAndPatch engine only after fixture construction; it does
not impersonate AAMultilingual for production classification.

Checks cover all ten pause-call sites preserving original translator effects,
driver/Tick scheduling, same-frame pending work, worker isolation, locale
change rejection, real Harmony ILManipulator conflicts, owner-scoped cleanup
and partial-cleanup retry, resource-queue continuity, actual render-lease
requirements, exact hash/MVID checks, unvalidated known-name rejection and
TitlePage exclusion. The helper must prove its dependency closure even when
the owner reports full clock coverage.

Fresh evidence and red/green results are under
`artifacts/scoped-runtime-migration-20261030/`. Native translation pixels,
combined story rendering, actual dependency state and Azurite handoff remain
separate native acceptance gates.
