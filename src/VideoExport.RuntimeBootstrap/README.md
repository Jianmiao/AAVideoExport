# AAVE early runtime attribution bootstrap (opt-in diagnostic)

This standalone **BepInEx 6 preloader patcher** observes actual `DynamicMethod`
objects before ordinary plugins load. It is not an ordinary AAVE plugin and is
not part of `RenderControlV1`. It neither disables Mods nor suppresses effects,
replaces dynamic IL, changes delegate results, or grants strict export admission.

The exact opt-in is `AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION=1` in the AA process
environment. Unset, `0`, `true`, and whitespace-padded values install no hooks.
The intended staged location is
`BepInEx/patchers/AAVE.RuntimeAttribution/AAVideoExport.RuntimeBootstrap.dll`.
Building this project does not install it or launch AA. Host references use
`Private=false`; do not ship another copy of BepInEx, Harmony, or MonoMod.

## Verified host entry point

The implementation references the actual binaries under
`F:\AzureArchive_101\BepInEx\core` (override with `-p:AAReferenceRoot=...`).
The installed XML documents `BasePatcher.Initialize()` as executing before any
patches from any plugin are applied. Inspection of the same installed IL found:

- `AssemblyPatcher.AddPatchersFromDirectory` constructs the `BasePatcher` and
  adds it to `PatcherContext.PatcherPlugins` at `IL_00c0`, before searching for
  assembly rewrite methods. This observer needs no assembly rewrite method.
- `AssemblyPatcher.PatchAndLoad` calls `BasePatcher.Initialize` at `IL_003d`.
- The installed IL2CPP preloader calls `PatchAndLoad` before chainloader
  `Initialize`; ordinary plugin loading follows the chainloader startup.
- `BasePatcher.Finalizer` is an end-of-preloader callback. It is deliberately not
  overridden to remove hooks, so later DynamicMethods remain observable.

These facts establish the local entry-point contract, not a claim that isolated
console tests prove successful startup for every AA installation or Mod profile.

## Public protocol

Assembly simple name: `AAVideoExport.RuntimeBootstrap`.
Public static type: `AAVideoExport.RuntimeBootstrap.RuntimeAttributionV1`.

| Member | Meaning |
| --- | --- |
| `int ProtocolVersion` | `1` |
| `bool IsInstalled` | All requested runtime hooks installed successfully |
| `string CoverageStartedUtc` | ISO 8601 UTC instant capture became active, or empty |
| `string GetSnapshotJson()` | Detached snapshot encoded as camelCase JSON |
| `string GetAttributionJson(Delegate value)` | Returned-object identity lookup; `UNKNOWN` if not observed |
| `void Mark(string name)` | Optional bounded diagnostic marker; ignored while disabled |

Snapshot schema: `aave.runtime-attribution.v1`. Fields include `installed`,
`coverageStartedUtc`, `runtime` (`framework`, `version`, `architecture`, `harmony`),
`startAssemblies` (sorted full assembly-name strings), `targets`, `records`,
`markers`, `lossCounters`, `limits`, `installationError`, `invocationSemantics`.
`preexistingCoverage`, `allCodeCoverage`, and `permitsStrictExport` are always
**false**. `evidenceKind` is always `STACK_OBSERVATION_ONLY`.

Delegate schema: `aave.runtime-attribution.delegate.v1`. `status` is `OBSERVED`
or `UNKNOWN`; `record` is null for unknown. It also contains
`coverageStartedUtc`, `evidenceKind`, and `permitsStrictExport=false`.
Tracking applies to the exact returned delegate object, not a name match,
`Delegate.Method` comparison, or shared anonymous holder. Combining a delegate
or wrapping it does not automatically make the new delegate tracked.

Records contain `id`, `name`, `holder`, `firstObservedUtc`, `creatorAssembly`,
`creatorMethod`, `constructorEvents`, `createDelegateEvents`, `invokeEvents`,
`creationStack`, `materializationStack`, `invocationStack`,
`delegateMethodType`, and `delegateMethodName`. Creator fields describe only
the first observed eligible stack frame. They are not authenticated ownership,
causal attribution across asynchronous boundaries, or an effects classification.
A preexisting method first seen in CreateDelegate/Invoke retains creator UNKNOWN.

All eight public constructors and both non-generic public CreateDelegate
overloads receive postfixes. The declared public DynamicMethod.Invoke overload
receives an observational prefix: `invokeEvents` counts **attempts**, including
calls that throw. Inherited convenience Invoke overloads dispatch to it.
Ordinary delegate execution is not intercepted.

## Bounds and loss reporting

Defaults: 4096 method records, 8192 distinct returned-delegate admissions over
the observer lifetime, 256 markers, 32 retained frames per stack, 512 characters
per text field, and 512 startup assembly identities. Reaching a cap drops new
diagnostics; it never blocks the original method/delegate. Previously admitted
method records keep updating their bounded latest stacks and counters.

`lossCounters` contains `droppedRecordEvents`, `droppedDelegateAssociations`,
`droppedMarkers`, `droppedStartAssemblies`, `reentrantCallbacksSkipped`,
`callbackFailures`, `installFailures`, `rollbackFailures`, `truncatedStacks`,
and `truncatedText`. Constructor overload chaining can produce more than one
event for the same object. Dropped-record counts are events, not distinct keys.
Markers are `{name,timestampUtc}`. Startup identities are captured immediately
before activation; observations predating activation are not recovered.

Both DynamicMethod and returned Delegate lookup tables use weak keys. Retained
records contain strings/counters only, never those runtime objects. Record and
marker snapshot collections are detached read-only copies. Stack walking and
reflection metadata capture happen outside the collector lock; JSON encoding
happens only after snapshot copying releases it. Callback failures are swallowed
and counted. A partially failed install removes all owned hooks and reports any
rollback failure; capture stays disabled. There is no automatic retry loop.

## Reproduction

From the repository root:

```powershell
pwsh -NoProfile -File tests/VideoExport.RuntimeBootstrap.Tests/Run-Tests.ps1 -AAReferenceRoot F:\AzureArchive_101
```

This builds against installed host references and runs bounded isolated child
processes on the unchanged AA .NET 6 runtime through a private junction under
the test `bin` folder. It verifies runtime version from inside the process and
compares eight host runtime/reference hashes before and after. A negative control
leaves hooks off and must fail the installation assertion while original delegate
output remains correct. The instrumented suite exercises constructors,
CreateDelegate/Invoke, original outputs and exceptions, late creation without
AssemblyLoad, identity, weak-key GC, parallel capture, snapshot immutability,
caps, callback failures, and partial real Harmony rollback.

Evidence is written to `tests/VideoExport.RuntimeBootstrap.Tests/evidence`.
Native startup, current-profile preservation, Azurite recognition/binding, and
actual export-time FPS/VSync handoff remain separate acceptance gates.
