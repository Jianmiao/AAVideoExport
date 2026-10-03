# Encoder core

Independent .NET 6 library, with no Unity or game assembly dependency. FFmpeg
and ffprobe must be supplied by the caller; they are never downloaded or bundled.

`FfmpegCapabilities.ProbeAsync` checks advertised encoders by actually encoding
three frames. Probes run at most two at a time with a ten-second deadline each.
`PickEncoder` prefers matching GPU hardware, other working hardware, then CPU.
The plugin uses `ProbeHardwareAsync`, which runs sample encodes only for hardware
candidates, and offers only `HardwareEncoders` that passed. `PickHardwareEncoder`
prefers the matching GPU vendor, then other working hardware, and never falls
back to software. Explicit software selections fail with
`hardware_encoder_required`; missing or failed hardware selections fail with
`hardware_encoder_unavailable`. `HardwareEncoderPolicy` exposes the same sorted,
duplicate-free availability and selection rules for deterministic testing.
`ExportSession` preflights the chosen dimensions and rate settings, then muxes
a short audio sample into the selected container before any captured frame is
accepted. This checks the installed FFmpeg build's audio/container support,
including MP4 PCM (`ipcm`) support. Unsupported combinations fail before capture;
PCM is never silently replaced with AAC. MP4 PCM playback support still depends
on the consuming player. The actual video encoder is returned in `ExportResult`.

Pass exactly `GetCanvasLayout().CaptureWidth * CaptureHeight * 4` RGBA bytes
to each `WriteFrame` (use both dimensions from that same canvas). Larger rented
arrays are supported through the explicit length argument. Unity bottom-up
capture needs the default `flipVertical: true`; top-down frames pass false.
Native frames match the output canvas; optional proxy frames use the selected
internal short edge and are upscaled to that canvas. The legacy `Supersampling`
option accepts only `1`; other values fail validation. Frame writes are serialized by the
caller and block for encoder backpressure (maximum 60 seconds per frame).

On Windows, eligible Anime4K proxy outputs at or below the 1080p pixel budget
use an independently owned D3D11 device selected by the encoder vendor and,
when matching AA's renderer, PCI device ID. All ten bundled CNN passes run with
FP32 arithmetic and FP16 feature textures, followed by linear-light Lanczos and
optional RCAS. GPU Rec.709 NV12 conversion reduces readback bytes. Unaligned
rows or NV12 encoder preflight refusal retain RGBA; D3D refusal retries the
original Vulkan implementation of the same model before capture. Larger
canvases and other GPU models keep Vulkan. `UpscaleExecution`, `UpscaleAdapter`
and `UpscaleFallbackReason` report the actual path. No native capture pointer is
treated as a GPU texture, and no cross-API adapter index is reused.

`WriteAudio` accepts complete interleaved float sample frames at the capture
sample rate/channel count supplied to the constructor. It writes a temporary
raw audio stream on the output drive. Video and audio writers can use separate
threads because they use separate streams. Each stream must have only one
writer. Do not call `Complete` or `Dispose` until both writers have stopped.
`Cancel` may run concurrently and kills FFmpeg to release a blocked frame write.

At completion, the captured audio timeline must match video within one frame.
FFmpeg then resamples/downmixes to stereo, pads/trims within that small tolerance,
and muxes it with copied video packets. Video is encoded only once. H.264/HEVC/AV1
use explicit full-range RGB to limited-range Rec.709 SDR conversion. QTRLE uses
lossless ARGB in MOV with no bitrate flags; meaningful alpha still depends on
the caller's rendered source.

Final verification checks codec, dimensions, frame rate, frame count, duration,
audio codec/sample rate/stereo channels and audio start offset (AAC priming
tolerance). MP4/MOV sample counts avoid a full media scan; MKV uses a packet
count, without decoding. Supported encoders emit one encoded packet per frame.
The separate integration harness fully decodes synthetic videos and compares
frames, orientation, color markers and PCM samples. Verification is evidence
about the encoded media, not proof of game capture correctness.

Final video and optional first-frame cover are created in a unique temporary
child folder of the destination. Only after verification are they moved without
overwrite. Existing video/cover files cause `output_exists`. A cancelled or failed
session produces no nominal successful output. Dispose removes only the session's
own temporary folder; close-file failures can leave that folder for diagnosis.
FFmpeg stdout/stderr are bounded and process invocations use argument lists,
not shell command strings.

See `tests/VideoExport.Core.Tests` for the executable integration harness. Its
reported FPS measures synthetic encoding and finalization, never AA playback
or full game export performance.
