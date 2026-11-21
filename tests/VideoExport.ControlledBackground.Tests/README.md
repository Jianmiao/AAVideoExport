# Controlled background adapter tests

Imported from the runtime-work branch on 2026-10-30. Historical runs named
below belong to that branch. Main-source verification is recorded separately in
`artifacts/background-migration-20261030/`; no native acceptance transfers with
the source copy.

These source-linked tests exercise the real adapter's read-only video resolver
and RGBA row conversion. The lifecycle additions execute production patch
validation, unpatch and shutdown against real installed Harmony and managed
fixture targets. They also use the actual RenderControlV1 lease for the retained
snapshot guard. They do not instantiate Unity or certify native background
layering, Flandre binding, playback or movie pixels.

Run:

```powershell
dotnet run --project tests/VideoExport.ControlledBackground.Tests -c Release -p:AAInstallPath=F:/AzureArchive_101
```

The first executable stub run was 1 passed / 6 failed at
`bin/Release/net6.0/artifacts/controlled-background-738d45a5a8034e16a751b28420395be5`.
After implementation and the invalid-config-directory regression, the suite
passes 8/8. The new fallback case was observed red (7/8) before correction.

## Parent integration contract

`ControlledBackgroundVideoAdapter` is internal to AAVE Plugin and exposes:

- `Initialize(Harmony, Action<string>)` and main-thread `TryInstall()`.
- `BeginExport(Func<long>, int fps, string cacheRoot, string ffmpeg, string ffprobe)`
  after acquiring RenderControlV1 and initializing the capture frame.
- `Pump(long frame)` immediately before camera capture; `ValidateActive()`
  may additionally run in Host.Update.
- `EndExport()` on every pass end/cancel/exception, before releasing render
  ownership; `Shutdown() -> bool` removes only this adapter owner's prefixes.
  A false result retains cleanup bindings and must prevent broad owner unpatch
  or host destruction until a later shutdown attempt succeeds.
- `PlaybackSettingsIdentity()`, `TryClassify(Assembly, out StrictCapabilityLevel,
  out IReadOnlyList<string>)`, `IsReady`, `Active`, `FrameUploadCount`,
  `FailureReason`, and a copied read-only `CommandTrace`.

The scoped admission evidence is
`ControlledBackgroundVideoAdapter.pts-frames.v1` and is Observed. The parent
must not turn it into unrestricted Certified/cache-reuse evidence.

The original Flandre native hook catches exceptions, so the AAVE adapter latches
the first failure. Its next Pump/ValidateActive throws to the Host; ignoring
that contract would permit an incorrect static fallback.

The adapter preserves the native layer alpha/geometry/ordering and creates a
separate texture per active playback timeline. A same-resource/same-file rebind
shares the playhead; a new source starts at that command's AAVE frame. Null
transition content unbinds that layer without replacing other layers. Older
layers may continue their existing video during native crossfades.

Decoder bytes are top-down RGBA; the adapter reverses rows into a distinct
reusable buffer before Unity upload. Video audio is intentionally absent because
this exact original Mod uses VideoAudioOutputMode.None.

## Limits before native acceptance

- This build is bound to FlandreScarletBackground 5.2.0.0 / MVID
  544d35a8-fc4a-434b-9a5f-fb68fcc7564a / SHA256
  779484939C616E22E77DD925880DD3EC0E47ED28643CE333D51ACC335C3313B1.
- New source decode is synchronous and bounded to 60 seconds per tool process
  on the capture thread. Media frame does not advance while decoding, but
  responsive asynchronous export preparation remains follow-up work.
- Source files are read-only and locked while used. No original backfill helper
  runs during capture. Ambiguous unrelated fallback files are reported.
- Published frame stores remain beneath the caller-owned cache root; EndExport
  closes source/store handles and destroys owned textures, not evidence files.
- Per-pass command trace includes frame, resource, source SHA and source-frame
  selection. Parent must compare preparation/replay command/resource identity;
  this adapter alone does not certify cross-pass source changes.
- A native UITexture replacement on an existing Layer/Content transfers its
  current playhead. Rebinding an outgoing resource during overlap keeps that
  layer's old playhead, instead of restarting it because another layer started.
- Native video state, existing layer textures, cached mappings and diagnostic
  deadlines are restored on release. Existing player wasPlaying resumes at its
  saved position. Full native transition, static fallback, texture orientation,
  playback restoration and pixel-output verification still belong to the
  parent's real AA fixture.
- Initial texture upload uses the source's decoded dimensions and preserves
  native UITexture geometry. Original fixed 1920x1080 RT scaling, non-square SAR,
  rotation and arbitrary decoder color display must be explicitly tested or
  adapted before claiming all video formats.
- Invalid configured video directories with file ancestors or malformed paths
  retain the original default-backup fallback without creating directories.
  File access/permission failures remain explicit backend failures.

## Read-only reviewer task

If dispatched as background_adapter_review, review only
`src/VideoExport.Plugin/ControlledBackgroundVideoAdapter.cs` and this new test
project against `docs/background-native-adapter-task-20261029.md` and the actual
`artifacts/background-takeover/FLVideoOverlay.cs`. Do not edit any source files
or start AA. Check callback fault propagation, exact-build/Harmony validation,
per-layer rebind/transition state, source resolution precedence, native state
restoration and resource ownership. Run this test suite if useful. Report
actionable blocking findings to the parent, with exact method names. Avoid
generic demands for complete arbitrary-Mod sandboxing outside this slice.
