# Core integration tests

For the 720p fix, `--proxy-only` checks the new relative tiers and `--panel-layout-only`
checks the single-page, fixed-proportion panel policy. Both are pure functional tests.
`--sr720-media-only` runs exactly three synthetic frames per quality/balanced tier
through the real Anime4K Direct3D path and Intel QSV at 1280x720, checking output
dimensions, decoded frame count, quadrant orientation/color and BT.709 limited range.
It requires that hardware/path (failure is not skipped) and performs no throughput
benchmark. Use the FFmpeg directory as the first argument for that media check.

This is a .NET 6 console assertion harness without a separate test-framework dependency. Its media branches invoke an actual
FFmpeg and ffprobe installation; no Unity process, desktop capture or audio
loopback is involved.

```powershell
dotnet run --project tests/VideoExport.Core.Tests/VideoExport.Core.Tests.csproj -c Release
```

For media tests, put both executables on `PATH`, set `FFMPEG_DIR` to their
directory, or pass that directory as the first argument. An explicit argument
takes priority over `FFMPEG_DIR`; there is no developer-specific fallback path.
The pure `--*-only` branches listed below return before resolving FFmpeg.

The harness exits nonzero on failure. It checks invalid settings and buffers,
including rejection of legacy 2x supersampling (rendering is fixed at 1x),
real encoder capability probes, GPU preference and CPU fallback;
hardware-only availability, stable duplicate-free choices, vendor preference,
explicit software rejection and absence of CPU fallback; a controlled FFmpeg
fixture that advertises a hardware encoder but fails its sample encode, proving
that listing alone cannot make an encoder selectable and software is never
probed by hardware-only discovery; actual local hardware-only discovery with
results retained in `hardware-capabilities.json`;
settings/progress/cancel-confirmation/cleanup page transitions, repeated and
invalid actions, completion while confirmation is open, and retained settings;
one-second H.264/AAC MP4 with bottom-up source orientation and per-frame markers;
qtrle/PCM16 MOV with every decoded RGBA byte and PCM sample checked; H.264/PCM24
MKV with decoded PCM samples checked; H.264/PCM16 and PCM24 MP4 at both 44.1 and
48 kHz with every decoded PCM sample, bit depth, start/end timing, and `ipcm`
sample entry checked; Rec.709 metadata; MP4/MOV fast-start;
cover dimensions and orientation; explicit audio disabling; 48 kHz to 44.1 kHz
resampling; Intel QSV 1080p60 VBR/CBR encoding when available; cancellation with temporary-file cleanup; and preservation of existing
files, including PCM MP4 and a collision that appears after capture starts.
A failing muxer fixture verifies that unsupported audio/container combinations
fail during construction, clean up their temporary files, and preserve unrelated
files. Both flipped and unflipped source paths are exercised.

Add `--pcm-only` to run the MP4 PCM cases and failing audio/container preflight
case without encoder discovery or the rest of the integration suite.

Add `--ui-flow-only` to run the four page-flow regression cases without FFmpeg,
media encoding, Unity, or a running application.

Add `--panel-layout-only` to run the fixed-shell settings layout/navigation,
bitrate and export-message regressions without FFmpeg or Unity. The layout cases
cover all 32 flag combinations, compact disabled super resolution, a unified
video/audio list, an inline Advanced accordion, anchored footer, the 980:800
shell fitting 90% of available width/height, content overflow and scroll clamping,
and small/short/ultrawide/portrait viewports. Navigation cases cover Advanced
expansion with fractional scroll preserved, repeated toggles, nonfinite scrolling,
collapse clamping and the absence of stale page history. Slider callback guard
cases reject unchanged activation before/after delayed synchronization, accept
all bitrate/RCAS pointer steps, and recover from nonfinite native events. These exercise the
production geometry/navigation models; native clipping, hit-testing, dropdown
layers, slider activation and preservation of in-progress text edits still need
AA runtime acceptance.

Add `--d3d-policy-only`, `--gpu-device-only`, `--proxy-only`, or
`--anime-compute-only` for the corresponding pure policy/geometry/shader checks.
The shader preflight guard rejects a zero-exit encode if libplacebo warns that
a required pass has no hooked textures; unrelated warnings remain permitted.

Add `--d3d-media` for actual NVIDIA and AMD D3D11 exports. It compares GPU NV12
with CPU Rec.709/bicubic conversion in landscape, portrait, square and widths
not divisible by four; verifies native-pointer writes, cancellation and same-name
retry; checks plain/zero RCAS equivalence, real sharpening and pre-CNN orientation.
Controlled driver-refusal fixtures reject NV12, then both D3D preflights, and
verify real RGBA/Vulkan fallback encodes with the same model and hardware encoder.
The requested GPUs must exist; missing hardware fails the suite. Intel is not
claimed as tested by this NVIDIA/AMD suite. An SDK-generated test apphost or the
offline build's Framework bridge is required for the refusal fixtures.

Add `--proxy-gpu-media` for the existing 24 actual NVENC/AMF proxy-model,
orientation, color, metadata, cover and audio checks. Synthetic fixtures verify
correctness; they do not establish full AA throughput.

Add `--ui-refresh-only` to check the production wall-clock presentation cadence:
6000 exporter updates over ten seconds request about 100 UI refreshes; cancel,
continue, completion, visibility and resize invalidations remain immediate.
These three pure tests verify scheduling, not native rendering throughput. The
native progress camera still renders normally; an AA A/B export is required to
measure any actual improvement.

Add `--canvas-only` for six pure fit/fill geometry cases, or `--canvas-media-only`
for four real FFmpeg exports. The latter decodes every pixel in six frames and
their covers to check black padding, centred cropping, unchanged proportions,
vertical orientation and final dimensions. These do not exercise AA itself.

Add `--playback-launch-only` to run eight one-click native-launch boundary cases
without FFmpeg or Unity: a frozen selected story is loaded exactly once when
preparation completes; synchronous native callbacks work; cancellation rejects
late preparation and player-start callbacks; loader failure and timeout allow
retry; duplicate starts cannot replace the selected source. These tests exercise
the production `PlaybackLaunchGate` called by the host. Native AA loading,
resource completeness, rendered frames and audio still require in-application
acceptance and are not claimed by these boundary tests.

Each successful encoding reports measured end-to-end frames per second,
including capture writes, encoding and core verification. These small synthetic
fixtures establish correctness, not production resolution throughput or native
AA rendering correctness. Every run uses its own temporary directory and prints
its location. Artifacts are retained for inspection.

Run only the longer throughput benchmark (without repeating the integration suite):

```powershell
dotnet run --project tests/VideoExport.Core.Tests/VideoExport.Core.Tests.csproj -c Release -- --benchmark
```

It sends 600 synthetic RGBA frames at 1920 × 1080 and 60 fps to Intel QSV in VBR
and CBR modes. It reports encoder preflight, frame writes plus encoder drain,
finalization, and total elapsed time separately. Independent full-decode frame
count and first-frame color checks occur after timing. The changing central
marker and mostly static gradient do not represent AA scene complexity; source
rendering and audio are excluded. All future harness outputs and JSON benchmark
reports are retained under the repository's `artifacts/tests/` directory.
