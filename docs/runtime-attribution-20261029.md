# Early DynamicMethod provenance: isolated feasibility evidence, 2026-10-29

## Result and scope

The new `tools/RuntimeAttributionProbe` is an executable first step toward per-method
provenance. It observes **real DynamicMethod constructor and CreateDelegate calls**, rather
than treating `Anonymously Hosted DynamicMethods Assembly` or its first AssemblyLoad event
as the producer. Its 19 bounded checks passed on the actual .NET **6.0.7** runtime under
`F:\AzureArchive_101\dotnet`, using the installation's **Harmony 2.10.2.0** and MonoMod
binaries. This is an isolated console result, not an installed AAVE feature, AA startup
test, Azurite integration result, or proof that arbitrary Mod effects are preserved.

No existing AAVE source, other Mod files, installed BepInEx files, or AA configuration were
changed. No patcher was installed and AA was not started. The eight reference DLL hashes
were identical before and after the final bounded run.

## Implementation

- `RuntimeObserver.cs` installs postfixes on all eight public DynamicMethod constructors
  and both non-generic `CreateDelegate` overloads. The original implementations still run;
  the postfixes neither replace IL nor skip calls nor substitute return values.
- A `ConditionalWeakTable<DynamicMethod, Attribution>` assigns a process-local ordinal to
  each actual method object. Two methods with the same name and holder remain distinct.
  Stored records contain text and counters, not strong references to DynamicMethod objects.
- A second weak-key table correlates the **actual returned Delegate** to the same record.
  Consumers with a captured delegate can retrieve its observed provenance without needing
  the fixture's direct DynamicMethod reference. Delegate MethodInfo type/name are recorded
  for diagnostics, not used as unique keys.
- A thread-local recursion guard protects observation. Capture is disabled during Harmony
  installation/removal, and callback exceptions increment an explicit failure counter rather
  than changing the delegate's result. Such failures cannot justify strict coverage.
- Constructor observation preserves the creation stack and selects the first non-framework,
  non-observer, non-Harmony/MonoMod assembly frame as the **observed immediate caller**.
  The fields are named `ProducerAssembly` / `ProducerMethod`; they are stack evidence, not
  cryptographic ownership, causal provenance across async boundaries, or proof of semantics.
- Materialization has a separate event count and stack. If a method existed before capture,
  a later CreateDelegate event leaves creator fields **UNKNOWN** and ConstructorEvents=0.
- Two separately built fixture assemblies (`AAVE.Probe.ProducerA` and `ProducerB`) both
  create a method named `SameName` in the anonymous shared holder, returning different sums.
  Fixture entry points have NoInlining so caller-stack assertions are deterministic. This
  qualification must not be silently generalized to optimized third-party code.
- `HostSeamInspector.cs` reads the three actual BepInEx DLLs with Mono.Cecil and writes
  method-call IL offsets. It does not load or execute their plugin chainloader.

## Reproduction and actual evidence

From `E:\aamod\AAVideoExport-0.2.4-source`:

```powershell
pwsh -NoProfile -File tools/RuntimeAttributionProbe/Run-Probe.ps1 -AAReferenceRoot F:\AzureArchive_101
```

The runner needs PowerShell 7, the .NET SDK/targeting pack, and an installed .NET 6 host
resolver. It creates an isolated host layout inside the tool's ignored `bin` directory,
using the installed 6.0.36 host resolver to start **AA's unchanged 6.0.7 coreclr and corelib**
through a junction. It does not confuse the resolver version with the executing runtime.
The runtime version is asserted from inside the child process. Child probe/control/seam
processes have a 30-second limit; the isolated build has a 120-second limit.

Final verified run: **2026-10-29T14:21:04Z** (`evidence/summary.json`):

| Assertion | Actual result |
| --- | --- |
| Runtime reported inside process | .NET 6.0.7 / X64 |
| Harmony patches installed | 8 public constructors + 2 CreateDelegate overloads |
| Uninstrumented negative control | exit 1, 11 expected attribution failures |
| Instrumented checks | exit 0, 19 passed, 0 failed |
| Producer A / B in same holder | distinct IDs 1 / 2; correct assembly and method stacks |
| Late creation after first holder load | captured; **zero AssemblyLoad events** during late creation |
| Constructor overloads | all 8 observed; fixture outputs unchanged |
| CreateDelegate(Type, Object) | observed; bound delegate outputs unchanged |
| Actual returned delegates | A, B, and bound delegate map to their own creation identities |
| Delegate behavior comparison | 2,412 integer input/output comparisons unchanged |
| Preexisting method later materialized | creator remains UNKNOWN; constructor count 0 |
| DynamicMethod.Invoke | returns 42; constructor observed, **no CreateDelegate event** |
| Callback errors / reentrant callbacks skipped | 0 / 0 in these bounded fixtures |
| Patch cleanup | no remaining Harmony patches owned by the probe; later fixture works |
| Installed reference hashes | all 8 unchanged |

The earlier `red-baseline.json` proved missing attribution with six failures before the
observer implementation existed. `constructor-attempt.json` proved the first constructor
and CreateDelegate patches on installed runtime 6.0.36. `overloads-red.json` then caught
incomplete constructor coverage (one failed check), followed by `overloads-green.json`
passing all 17 checks at that stage. `aa-runtime-607.json` repeats that stage on AA's runtime.
`delegate-correlation-red.json` then proved that returned-delegate correlation was absent
(one failed check) before adding the second weak-key table. The final `evidence` reports add
cleanup verification and delegate correlation and are the reproducible current result.

An initial harness setup attempt failed before patching with a missing dependency:
`MonoMod.RuntimeDetour, Version=22.3.23.4`. The AA binary is version 22.7.31.1. Copying DLLs
as content did not put them in the .NET dependency graph; explicit project references fixed
resolution. This was a harness dependency failure, **not** evidence that framework patching
was impossible. Actual framework patching subsequently succeeded; no fallback provenance
claim was fabricated.

Primary artifacts:

- `tools/RuntimeAttributionProbe/evidence/summary.json`
- `tools/RuntimeAttributionProbe/evidence/instrumented.stdout.json`
- `tools/RuntimeAttributionProbe/evidence/control.stdout.json`
- `tools/RuntimeAttributionProbe/evidence/host-seam.stdout.json`

Reference SHA-256 values (full set is in the summary):

| File | SHA-256 |
| --- | --- |
| `dotnet/coreclr.dll` | `04377968C540F704AAE92ECB0EC9BBFD5FDDFE226DFCA30AA3EEB52006A44FD8` |
| `dotnet/System.Private.CoreLib.dll` | `2CAA46D2AE726E135592033B6C682232A08698C23DBD1A9207F86C4CCA081AD7` |
| `BepInEx/core/0Harmony.dll` | `6C898933B52149E8BCF6722305C7E4D94ADD47C9E53B364096F91721624C0877` |
| `BepInEx/core/BepInEx.Preloader.Core.dll` | `EC168752C9A885D9FE035370AC2DBD8B52B91B24CB60FBC425CFCBEE5436DAE9` |
| `BepInEx/core/BepInEx.Unity.IL2CPP.dll` | `62C8246A3076BFE459EC80C482381228D0EB1DDE3434FE97AE5255FEF406FE72` |

## Exact BepInEx integration seam

The supported extension seam is an implementation of
`BepInEx.Preloader.Core.Patching.BasePatcher.Initialize()` registered as a BepInEx 6
patcher. The installed XML states that Initialize runs before patches from any plugin are
applied. The **actual installed IL** establishes the relevant ordering:

| Method | Offset | Call |
| --- | --- | --- |
| `BepInEx.Unity.IL2CPP.Preloader.Run` | `IL_01E2` | `Il2CppInteropManager.Initialize` |
| same | `IL_0214` | `AssemblyPatcher.AddPatchersFromDirectory` |
| same | `IL_029B` | `AssemblyPatcher.LoadAssemblyDirectories` |
| same | `IL_02E1` | `AssemblyPatcher.PatchAndLoad` |
| `AssemblyPatcher.PatchAndLoad` | `IL_003D` | each `BasePatcher.Initialize` |
| same | `IL_0212`, `IL_0267` | patch execution helpers |
| same | `IL_06DE` | each `BasePatcher.Finalizer` |
| `Preloader.Run` | `IL_0304` | `IL2CPPChainloader` constructor |
| same | `IL_0314` | chainloader `Initialize` |
| `BaseChainloader<T>.Execute` | `IL_005F` | `LoadPlugins` |
| `BaseChainloader<T>.LoadPlugins(IList<PluginInfo>)` | `IL_02D1` | `Assembly.LoadFrom` for normal plugins |
| same | `IL_02FB` | `TryRunModuleCtor` |
| same | `IL_0307` | `LoadPlugin` |
| `IL2CPPChainloader.LoadPlugin` | `IL_000C`, `IL_002C` | `Activator.CreateInstance`, then `BasePlugin.Load` |

Thus BasePatcher.Initialize is early enough to install observation before **normal
chainloader plugins are loaded/initialized**, including plugin module constructors handled
by this path. An ordinary AAVE plugin Load callback is too late to establish that guarantee
for earlier plugins. A future patcher must publish a registry and an explicit coverage-start
marker for AAVE to consume; it must not silently declare the pre-start interval covered.

This seam is **not** earlier than all executable managed code:

- `AddPatchersFromDirectory` already calls `Assembly.LoadFrom` at `IL_006D` and
  `Activator.CreateInstance` at `IL_009A`, before any Initialize callbacks. Patcher assembly
  loading/static initialization/constructors can create code before this observer starts.
- Interop initialization occurs earlier at `IL_01E2`; the observer's own Harmony setup also
  emits code while observation is disabled. No claim covers either interval.
- Ordering among independent patcher Initialize callbacks is not established by this probe.
  The intended coverage boundary must explicitly account for other patchers, rather than
  relying on filename sorting or assuming this patcher executes first.
- An instrumentation requirement that includes all preloader code needs an even earlier
  host bootstrap hook / runtime profiler strategy. That integration was not implemented or
  proven here. Do not edit or weaken other Mods to hide this boundary.

## Coverage limits and strict-policy consequence

This experiment removes the specific impossibility of distinguishing same-holder methods
for the observed fixtures. It does **not** authorize replacing strict unknowns with success.

1. **CreateDelegate is not a universal materialization boundary.** A direct `DynamicMethod.Invoke`
   fixture executes successfully without it. Private compilation paths, expression emitters,
   delegate reuse, dynamic assembly MethodBuilder, native detours, and other code generation
   mechanisms need separate instrumentation and tests.
2. **Creation is not effect classification.** Recording a caller and method identity cannot
   establish whether generated code reads wall clocks, accesses IO/native state, changes
   render state, or is safe for deterministic export. Provenance must lead to an audited
   producer contract and effect-preserving control path. A recognized shared holder is not
   permission to exclude it, skip it, or drop effects.
3. **Stack attribution has limits.** Wrappers, optimized/inlined calls, async work queues,
   stripped stack information, or emission delegated to a shared library can hide the Mod
   responsible. Preserve full evidence and UNKNOWN when the required owner cannot be proven.
4. **Callbacks are observational but intrusive.** Stack capture allocates and costs time;
   Harmony installation itself changes runtime dispatch. This bounded arithmetic comparison
   does not prove timing neutrality, workload-scale performance, unload behavior, concurrency
   safety, or coexistence with all other patches.
5. **Early and late completeness must be explicit.** Any method existing before the coverage
   marker, missed callback, failed patch, removal/tampering, or unsupported emission route
   invalidates a whole-process completeness claim. The tool tests neither hostile Mods nor
   runtime tampering resistance.
6. **Host acceptance remains separate.** This work does not modify `RenderControlV1` or its
   protocol. Real AA/Azurite host recognition, installed Profile, binding, render handoff,
   export effects, and replay acceptance still require the normal end-to-end checks before
   any release. Those checks were intentionally not simulated by this console harness.

The next implementation step is an opt-in early observer registry with a measured start
boundary and loss counters, followed by tests against specific cooperative Mod emitters.
Strict export should still fail closed for unproven dynamic/native producers. Its goal is
to keep all admitted Mod effects under control, not to achieve acceptance by removing them.
