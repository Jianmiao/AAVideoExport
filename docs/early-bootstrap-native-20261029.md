# Early observer installed in real AA — 2026-10-29

Status: concrete native bootstrap integration; full controlled-Mod movie export
is still pending. No strict-admission promotion, cache enablement, Mod disabling,
or assertion of arbitrary future-binary compatibility was made.

## Implementation

`src/VideoExport.RuntimeBootstrap` is a standalone BepInEx 6 patcher, explicitly
enabled only by `AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION=1`. Its Initialize installs
observation on eight public DynamicMethod constructors, two CreateDelegate
overloads and public Invoke attempts. Original implementations run normally;
the callbacks collect bounded stack evidence and never replace delegate output.

The v1 diagnostic API exposes immutable snapshot copies, weak-key method and
delegate correlations, start-assembly and marker inventories, and explicit loss
counters. It always reports `preexistingCoverage=false`, `allCodeCoverage=false`
and `permitsStrictExport=false`. Creator stacks are not proof of effect semantics
or causal ownership across optimized/shared/async code.

The AAVE plugin has an optional reflection bridge that publishes load-enter and
load-ready markers. A failing observer or reporting callback does not disable
normal AAVE startup. Source-linked logger regression: 2/3 red -> 3/3 green.

## Verified host seam

The exact installed BepInEx PatchAndLoad implementation completes all patcher
Initialize callbacks before any Finalizer callbacks. ModTheAzureArchive installs
its ordinary-Mod discovery hooks only from Finalizer. Its constructor/static
constructor/Initialize closure does not execute ordinary Mod code in the audited
build. This establishes the relevant ordering without a filename-sort assumption.

Earlier interop initialization, patcher constructors, observer setup and unknown
independent preloaders remain outside coverage. Evidence:
`artifacts/preloader-order-audit-20261029/installed-bootstrap-il.json`.

## Native environment and fidelity

Fresh independent lab: `artifacts/native-lab-20261029-bootstrap`, copied from
current `F:/AzureArchive_101`, not from the old reduced AaveRepeat profile.
Active Profile is **Default**: 16 configured entries, 15 present Mods, including
CharacterVoice. The configured custom-entry-animation directory was already
missing and is recorded as such; it was not silently removed or substituted.

The Default modconfig bytes remained identical to the primary installation:
SHA-256 `549F74941E247B545747B331E1C713F795C1F26AA96BFFEC77343F76AB005C54`.
BepInEx confirms 15 successfully loaded Mods, plus the temporary native test
plugin. Loading is not a proof that every effect played or was synchronized.
No primary-install files were staged or changed by this integration.

## Results and limits

- `acceptance-20261029-05-bootstrap-absent-red`: 0 passed / 1 failed, expected
  missing-observer failure. This was a real startup negative control.
- `acceptance-20261029-06-bootstrap-enabled`: 12/12 for the initial fixture.
  Its claim about Azurite was too broad: it only checked the AAVE lease.
  After that result, the log reported FPS mapping yielded while suspended.
  Preserve this as an early-before-initial-FPS-mapping edge, not a compatibility
  success or a proven observer regression.
- `acceptance-20261029-07-bootstrap-fps-handoff`: **15/15**, process 61856,
  exit 0, no timeout. The revised fixture requires supported/bound Azurite with
  FPS mapping actually applied before acquisition, checks suspension over 15
  Updates, waits 15 further Updates after release, and verifies mapping resumed.
  FPS -1, VSync 1 and selected tier 1 matched the settled before/after baseline.
  No FPS mapping-yield warning was recorded in this run.
- Final snapshot: **1,299 records**, including direct or stacked paths from
  Flandre background, AAMultilingual, AAMenuPrototype, Azurite, CharacterVoice
  and More Effects through interop helpers. Two same-name fixture methods
  retained distinct IDs; late creation produced zero AssemblyLoad events and
  preserved 130 arithmetic results.
- All 11 targets were installed. Callback/install/rollback failures, reentrant
  skips and dropped records/delegate associations/markers/start assemblies
  were zero. **21 stacks and 3 text values were truncated** by explicit caps;
  this is not a zero-loss/full-coverage claim.
- Bootstrap isolated tests: **36/36 on the actual AA .NET 6.0.7 runtime**;
  negative control failed as expected, eight installed reference hashes unchanged.
  Build and the native test build: 0 warnings/errors. RenderControlV1 ABI remains v1.

Native snapshots and the explicit FPS/VSync evidence are in run07; the compact
cross-check is `artifacts/bootstrap-attribution-20261029/native-summary.json`.
Current raw logs decode as UTF-8. Windows console encoding can garble printed
Chinese even when the file is correct; use `python -X utf8` for inspection and
retain original bytes. None of the 15 selected ordinary assembly names was
present in the observed coverage-start assembly inventory.

## Exact candidate identities

| Component | SHA-256 |
|---|---|
| Runtime bootstrap | `21C8FB6113BF8C785DC8D508DD22346694671A5893F6F026D492CAD16B5F8714` |
| AAVE provider | `206A4DE6EC2A8A6A11AD8605BFC1C28331DC2C3CB1227E5B8BDFD8F2B73D8793` |
| Core | `98960814500D2FF17E32D68A96E26B23A438F2438EED736C34C09DF0958FDFF4` |
| Native fixture | `92A65A8E426AC10BEA7B347AECF50B44512DD59C927263249BDD38F9FB979255` |

## Next required work

Connect observed generated methods/delegates to an actual controlled effect
adapter, starting with a currently blocked producer such as CharacterVoice's
playback gate and audio scheduling. Do not replace the anonymous-holder refusal
with a blanket name exception: MethodBuilder/private/native emission and ordinary
delegate execution are not universally intercepted, and stack evidence alone
does not make wall-clock or audio behavior deterministic.

The first full positive native story/movie export with effects intact, repeated
frame/PCM/event/PTS agreement, cancellation recovery and Azurite restoration
remains the success criterion. No movie was produced by these observer tests.
