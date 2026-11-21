# Runtime attribution feasibility probe (experimental)

This isolated net6.0 console harness patches actual `System.Reflection.Emit.DynamicMethod`
constructors and `CreateDelegate` methods using the Harmony/MonoMod binaries in the selected
AA installation. It is not an AAVE plugin, BepInEx patcher, installation script, or certification.

Run from the source root with PowerShell 7 and an installed .NET SDK / .NET 6 host resolver:

```powershell
pwsh -NoProfile -File tools/RuntimeAttributionProbe/Run-Probe.ps1 -AAReferenceRoot F:\AzureArchive_101
```

The runner builds within this directory, gives each probe process 30 seconds, and executes
both a negative control and the instrumented harness against the unchanged AA-bundled .NET
6 runtime. A private `bin/isolated-aa-runtime` host layout contains a junction to AA's `dotnet`
directory; nothing is installed or written into AA. It also reads BepInEx method IL using
Mono.Cecil. There is no AA launch, native game invocation, export, network operation, or Mod
source modification in this runner.

`evidence/summary.json` is the final run summary. `instrumented.stdout.json` contains every
check and captured stacks; `control.stdout.json` is expected to fail attribution checks and
exit 1; `host-seam.stdout.json` contains local binary method-call offsets. The runner itself
exits 0 only if the instrumented checks pass, the control is red, the requested bundled
runtime is actually used, seam inspection succeeds, and eight source-file hashes remain
unchanged. A report's `PASS_BOUNDED_FIXTURES_ONLY` is deliberately limited.

Files at this directory's root ending in `*-red.json`, `*-green.json`, `red-baseline.json`,
`constructor-attempt.json`, and `aa-runtime-607.json` preserve intermediate experiments.
The authoritative repeatable report is `evidence/summary.json` and its companion outputs.

For interpretation, the early BepInEx seam, exact verification results, and uncovered paths,
see `docs/runtime-attribution-20261029.md` at the repository root.
