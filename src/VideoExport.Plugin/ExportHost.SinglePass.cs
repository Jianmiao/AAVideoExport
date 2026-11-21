using AAVideoExport.Core;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    // The authored playback is already complete. Video was submitted while it
    // ran; SealStrictAudio copied the actual contiguous native PCM content.
    // Pending GPU readbacks finish through the existing bounded frame pipeline.
    [HideFromIl2Cpp]
    private void FinishSinglePass()
    {
        if (_options == null || _native == null || _session == null)
            throw new ExportException("invalid_state", "Single-pass completion lost its capture session.");
        _strictPlan = StrictFramePlan.CreateExecution(_captured, _options.Fps, _native.SampleRate, _native.Channels);
        _session.SealLiveTimeline(_strictPlan);
        Finish();
        RestoreStrictRandom();
    }
}
