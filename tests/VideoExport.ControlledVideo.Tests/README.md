# Controlled video frame store acceptance

Imported from the runtime-work branch on 2026-10-30. Historical runs named
below belong to that branch. Main-source verification is recorded separately in
`artifacts/background-migration-20261030/`; no native acceptance transfers with
the source copy.

This project tests the actual FFmpeg/ffprobe media backend. It creates lossless
four-frame colored fixtures, including a variable-PTS video, and compares every
decoded RGBA byte with independent authored pixels.

Run:

```powershell
dotnet run --project tests/VideoExport.ControlledVideo.Tests -c Release -- "F:/AzureArchive_101/mods/AAVideoExport/0.2.4/tools"
```

Each run creates a new directory beneath its build output's `artifacts/`.
The initial stub run is retained at
`controlled-video-f800bc32a650488bb6fcf3c592dc43b9/results.json` (0 passed / 6
failed). Later runs write `results.json` plus input videos and completed stores.
No existing AA installation or Mod is written by these tests.

## API for the native texture adapter

- `ControlledVideoFrameStore.PrepareAsync(source, ffmpeg, ffprobe, cacheRoot,
  cancellationToken, maxDecodedBytes, processTimeout)`: validate metadata and
  fully decode first video stream; only then publish one new `video-<guid>/`
  directory with `index.json` and `frames.rgba`. Optional defaults are 2 GiB
  decoded bytes and five minutes per tool process. Invoke off the Unity thread
  while export time is held. No audio stream is decoded.
- `SelectFrame(outputFrame, outputFps, loop)`: select an exact source interval
  using integer/rational arithmetic. The first source PTS maps to playback zero.
  `loop=true` wraps at the final frame's declared end. `false` holds last frame.
- `CopyFrameForOutput(outputFrame, outputFps, loop, rgba)`: fill a reusable
  byte array of `FrameByteCount` bytes. The caller uploads it to an RGBA32
  texture on the Unity thread. Frame bytes are row-major FFmpeg RGBA; no
  scaling, vertical flip, rotation or frame interpolation is performed.
- `ValidateSourceIdentityAsync(token)`: require current source SHA-256 to match
  the store's input digest before reuse/capture.
- `Frames` is a read-only source PTS/duration/byte-offset index; all timestamp
  values use `TimeBaseNumerator / TimeBaseDenominator` seconds per tick.
- `Dispose()` closes the raw-file read lock; the caller retains ownership of
  published cache files and their later cleanup.

The production source is opened read-only and held against writes/deletion on
Windows while probing and decoding; its hash is checked again before publish.
Failure/cancellation removes only the build's own unpublished GUID directory.
The published frame file stays read-locked while the store is open.

## Limits

This is a decoder/index backend, not the completed Flandre Mod adapter or an OS
sandbox. Native texture upload/orientation, layer blending, transitions,
VideoPlayer state restoration and full story output still need separate
acceptance. Unsupported/absent timestamps, nonmonotonic timestamps, unknown
final-frame duration, dimension changes or budget overflow are reported rather
than guessed. Dynamic resolution, source rotation/SAR display policy and decoder
color-conversion differences require an explicit future native adapter policy.
Identical decoded source frames across arbitrary FFmpeg builds are not promised.
