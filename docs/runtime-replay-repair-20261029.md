# Replay lifetime repair — 2026-10-29

The complete controlled-Mod export objective remains unachieved. This increment repairs prerequisites that previously allowed prepared command validation and the admitted module snapshot to disappear on the second pass.

## Changed behavior

- Preserve the admitted capability snapshot and sealed command trace across LaunchStrictReplay; only final disposal/cancellation clears them.
- Use a phase-aware trace sequence cursor instead of the full recorded command count during replay. Reject a nonzero first sequence and prevent external mutation of the recorded list.
- Give the selected story player a source-derived logical ID across scene reloads. Ignore unrelated players, record row/choice state, and route command mismatch through the host cancellation path before continuing the native event.
- After primer startup's native AdvanceScenario returns, stop Update if the callback cancelled capture, preserving the original divergence diagnostic.

## Verification

Red/green logs in `artifacts/runtime-replay-20261029/` cover missing replay commands, exact matching replay, second-pass newly loaded module, cancellation/retry state, different native player addresses, unrelated players, and synchronous cancellation from primer startup.

| Check | Fresh result |
|---|---|
| Host lifecycle/helper suite | 50/50 |
| StrictCapability / trace | 15/15 |
| StoryPath | 5/5 |
| PCM StrictReplay | 12/12 |
| Clock | 13/13 |
| RenderControl | 12/12 |
| Plugin / NativePath build | 0 warnings, 0 errors |
| Native AA clocks / ownership diagnostic | 8/8 |

Final native evidence: `artifacts/acceptance-20261029-02-replay-clocks-final`.
Provider SHA-256: `A061EF25E83C9598BD5005EA67CB0858E87D081250A3D71D9C3DB6D31B6F9286`.
Core SHA-256: `EEA21FB3662449CB7E780916713B1D26C20FFF06AD03C65EDD264703722D6AE3`.
Profile AaveRepeat in a new independent lab. Logs confirm the rebuilt provider path,
AA1.0.1 recognition, Azurite rendering-write suspension and restoration.

## Not yet proved

No successful full story movie was exported by this increment. The production
story/audio tracing flags and preflight cache reuse flag remain false. The host
tests source-link production lifecycle/helper code but do not install real Harmony
story prefixes. Script override serialization, audio source/clip stable IDs,
end-event callback-order normalization, dynamic method provenance and complete
third-party scheduling remain open. Native clock diagnostics do not prove these.

The 2026-10-29 handoff ZIP is retained unchanged as the pre-repair snapshot.
