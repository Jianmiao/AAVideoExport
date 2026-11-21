# Strict export session media tests

```powershell
dotnet run --project tests/VideoExport.StrictSession.Tests -c Release -- <ffmpeg-and-ffprobe-directory>
```

These tests execute the production ExportSession and PreparedAudioTrack with
actual FFmpeg and ffprobe, without Unity. Nonzero PCM pulses at frame boundaries
and the beginning/end must keep their exact sample positions after disk-backed
preparation, split/merged mixer blocks, replay, encoding, muxing and decoding.
The matrix covers 13 frames at 24/30/60 fps, AAC with a non-millisecond endpoint,
48 kHz to 44.1 kHz conversion, and preparation/finalization errors of one sample.
No mismatched result may be committed to the final output path.

Generated outputs and decoded sample artifacts are retained below the test
executable's `strict-session-media` folder. This validates the media pipeline;
it does not establish that native AA playback visits the same story events in
both passes.
