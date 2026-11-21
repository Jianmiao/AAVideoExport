# Controlled runtime checkpoint — 2026-10-30 15:38 +08:00

Goal remains active. No whole-Default-profile repeated movie has passed yet.
Continue the original goal, not a smaller refusal-only or probe-only target.

## Authoritative locations and current safety state
- Source: `E:/aamod/AAVideoExport-0.2.4-source` (not a Git checkout).
- Primary AA: `F:/AzureArchive_101`; no candidate was installed there.
- Lab: `artifacts/native-lab-20261029-bootstrap`, unchanged Default selection (16 configured entries, 15 present Mods).
- No AA process was running at this checkpoint. All completed native runs used `tools/Run-GuardedNativeAcceptance.ps1` and restored settings/Sfx/typed PlayerPrefs. Last verified user workspace is `E:/AAdata`, voiceVolume=0, settings SHA `750578DAF16694EB9750BFF62A01C1CD657F5EC2C0AB6C4377577A81B2B781C7`.
- Lab-only background backup path now points to `profiles/Default/configs/AAVE-Acceptance-VideoBackup`; ProjectSideCopy remains true. Primary Mod configs were not rewritten.
- Runner now archives the previous BepInEx log before launch; a crash without a fresh log is explicitly recorded, rather than copying stale evidence.

## Verified progress since the prior continuation report
- Host/Menu/Voice/Recipe integration is present, including retained controllers, automatic preload, matching voice event receipts, commit guards and movie-linked runtime recipe sidecar. See `docs/retained-host-integration-20261030.md`. Its managed tests do not substitute for native movie acceptance.
- The old global Emit-hook recipe observer crashed in actual AA (run27, CoreCLR c0000005). Dump and Windows events are preserved locally. A1B8 candidate with recipes disabled survived to the native helper (run29); this expected helper refusal is not a completed export.
- Replaced that implementation with final-IL validation before materialization, actual resolver/delegate owner confirmation afterward, and continued validation. No global Emit detours remain (14 narrow hooks). New policy: `aa101.interop-initializer.intptr.final-il.v2`; evidence kind: `PRE_MATERIALIZATION_FINAL_IL`; global coverage/authorization flags stay false.
- Actual AA run31 passed 8/8: 95 current recipes qualified, all historical audited target types covered, real cached delegates/holder identity verified, seal/worker guard/consumption/reacquisition passed. Process exit0, no timeout, state restored. See `artifacts/acceptance-20261030-31-final-il-native`.
- Frozen run31 candidate is `artifacts/runtime-recipes-final-il-20261030/native-candidate`: bootstrap `9C5B0193275150DB2EAC589F4B272A495D563C2F82F6D3A65F98A4AA835DA62F`, Plugin `5335F6E2CA045E3150B43466069B1820BDAFA9C16504AFE999108AC9C1B5F2E1`, Core `18C25E9587FFBBCA4CD5497F5291F74DFCC7E30ABAD7A167C317FBDFB1D6E6F8`. Later native helper builds may differ; inspect current hashes before reuse.

## Real mixed-effect input and its current blocker
- Original manual Generic AAS fixture is retained as a negative/input-learning artifact. Raw `#aavt` instructions inside compiled ScriptKr are invalid; three initial preset duration parameters were also invalid. Do not reuse that fixture as a positive effect proof.
- Correct input: `artifacts/controlled-authoring-workspace-20261030`, marker schema2. It copies the small original AAP2, uses actual ScriptData.additionalPrompt and native `SaveProject -> AuthoringCompiler.CompileToSaves`, with the installed Mods owning sanitization/voice binding.
- Run25 passed native authoring validation: paired AAP2/AAS, eight compiled rows (title plus seven authored rows), three owned voice identifiers, no raw aavt instructions in compiled ScriptKr. Story SHA `78D2B7D2C2DDDD87145E5C301703F7B04AE45A55AFB19236150084C1FED3EF22`; original template unchanged. Four valid presets include Y-axis spin.
- Two 128x64 Constrained Baseline H264 files and actual voice WAV are in the independent workspace. MP4 companions were explicitly copied alongside the compiler's PNG outputs; `background-sidecar-packaging.json` records them. The compiled AAS and official build manifest were not patched.
- Ordinary playback run26/run28/run30 stops at the title. AUTO is actually enabled; time, cameras and rendering advance. TitleTask performs one MoveNext (state0->1), yields WaitUntil, and predicate0 remains false. Do not delete the title or force Complete/Advance.
- Offline exact GameAssembly/map analysis: predicate0 RVA0x00889400 returns byte at `owner+0x138`; native background rendering callbacks set that byte true. The expected managed field is `isBackgroundLatest`; next helper uses real IL2CPP field offsets to confirm it. Evidence: `artifacts/controlled-authoring-20261030/native-title-predicate-findings.md`.
- This is a normal/background-rendering receipt question, not yet proof that AAVE's controlled-background path also hangs. The controlled adapter preserves the original widget and may already restore the natural receipt; test the actual path, never fabricate the bit.

## Active delegated work — resume existing agents, do not duplicate edits
1. `/root/inspect_source_status` owns shared Host/Plugin/Core/Inventory and the menu controller. Fixes actual native false rejection/log flood: BepInEx Bind rewrites the cfg during startup; file mtime alone is not rename evidence. Implement semantic early snapshot + live value/install-directory/worker proof, and bounded retry/log-once. Authorized to add a read-only StartupConfigSnapshot file and one initial call to existing bootstrap Initialize (no new detours/second patcher). Coordinate with the bootstrap agent.
2. `/root/bootstrap_native_diagnosis` owns independent `FrameworkGeneratedAssemblyScope.cs` and tests for the two remaining real pathless MonoMod helper assemblies. Bind actual private cached Type/Method/Assembly references, full generated metadata/IL and physical producer/runtime pins; reject same-name spoofs/extra members/late patches. No name whitelist. First 25 tests reported green; final proof/commit/coexistence work remains. Bootstrap final-IL code is otherwise frozen except the coordinated read-only snapshot addition.
3. `/root/native_voice_fixture` owns the ordinary mixed-story playback diagnostic helper. Confirm the native background-ready field and inspect actual original/replacement widget callbacks. Do not patch both Test callback aliases sharing RVA0x007D45B0. No forced completion, field writes or extra scenario advance.

## Immediate execution sequence
- Check live agents and actual files/processes first; a prior message or state file is not a live process.
- Finish the semantic startup proof/throttling and the two finite framework helper scopes; integrate their retained proof without relaxing unknown-module admission.
- Use the new original-title/background diagnostics, then the actual controlled ExportHost path. Root owns all AA staging/runs; do not run AA concurrently.
- Rebuild Plugin/Core/Bootstrap/Native consistently after source changes. Native fixture references new Core sidecar APIs; using old Core causes missing-type build errors.
- Use guarded fixture workspace + temporary TestVoiceVolume=1 for audible cases, restore original0. Keep successful muted cases distinct.
- Eventually run the actual mixed scene through both passes and a second full export, checking decoded pixels/PCM/PTS, stable voice/events, every authored effect, cancellation/retry, asset hashes and Azurite restoration. Do not mark complete before these are real and verified.

The prior automatically rejected temporary-directory cleanup remains preserved in the earlier report; do not work around that rejection. No goal pause/complete/blocked status was set.
