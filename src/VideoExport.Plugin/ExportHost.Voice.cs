using AAVideoExport.Core;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private CharacterVoiceSessionAdapter? _voiceSession;
    private CharacterVoiceReceipt? _voicePreparedReceipt;
    private string[] _voiceKnownIdentifiers = Array.Empty<string>();
    private bool _voiceWarmup;

    [HideFromIl2Cpp]
    private void AcquireVoiceController()
    {
        if (_voiceSession == null && CharacterVoiceSessionAdapter.IsProviderLoaded)
            _voiceSession = CharacterVoiceSessionAdapter.Attach(RetainedBoundaryFailed,
                message => UnityEngine.Debug.Log("AA Video Export: " + message));
        if (_voiceSession != null)
        {
            CharacterVoiceDelayClock.ValidateBinding();
            _voiceSession.ValidateController();
        }
        else CharacterVoiceCompatibility.EnsureStrictExportAllowed();
    }
    [HideFromIl2Cpp]
    private void BindFrozenVoiceStory()
    {
        if (_voiceSession == null) return;
        _voiceSession.ValidateController();
        _voiceKnownIdentifiers = CharacterVoiceSessionAdapter.CollectKnownIdentifiers(_frozenSave
            ?? throw new ExportException("story_missing", "语音绑定缺少已冻结的原生剧情表。"));
        _voiceSession.BindStory(ExportBuildProfile.DevelopmentAuditEnabled ? _strictStoryHash : _selectedStoryKey, _voiceKnownIdentifiers);
    }
    [HideFromIl2Cpp]
    private void ValidateReplayVoiceStory()
    {
        if (_voiceSession == null) return;
        var replay = CharacterVoiceSessionAdapter.CollectKnownIdentifiers(_frozenSave
            ?? throw new ExportException("story_missing", "语音重放缺少真实原生剧情表。"));
        if (!_voiceKnownIdentifiers.SequenceEqual(replay, StringComparer.Ordinal))
            throw new ExportException("voice_story_changed", "重放的原生语音标识与准备阶段不一致。");
    }
    [HideFromIl2Cpp]
    private bool PrepareVoiceWarmup()
    {
        if (_voiceSession == null) return true;
        _voiceSession.PreparePass(); _voiceWarmup = true;
        return PumpVoiceWarmup();
    }
    [HideFromIl2Cpp]
    private bool PumpVoiceWarmup()
    {
        if (!_voiceWarmup) return true;
        var scope = _voiceSession ?? throw new InvalidOperationException("Voice warmup lost its retained controller.");
        if (!scope.PumpPreload())
        {
            _status = "正在通过原生资源服务预加载剧情语音；尚未开始离线音频或提交画面。";
            return false;
        }
        _voiceWarmup = false;
        return true;
    }
    [HideFromIl2Cpp]
    private CharacterVoicePosition VoicePosition() => new(_captured,
        _native?.AudioRawSampleFrames ?? throw new InvalidOperationException("Voice position has no authored PCM clock."),
        _player?.cur ?? throw new InvalidOperationException("Voice position has no owned story player."));
    [HideFromIl2Cpp]
    private void ArmVoicePass()
    {
        if (_voiceSession == null) return;
        _voiceSession.BeginPass(_player!, _options!.Fps, _native!.SampleRate, VoicePosition,
            !ExportBuildProfile.DevelopmentAuditEnabled || _strictPreparing ? null : _voicePreparedReceipt ?? throw new InvalidOperationException("Replay has no prepared voice receipt."));
    }
    [HideFromIl2Cpp]
    private void ValidateVoiceFrame()
    {
        CharacterVoiceDelayClock.ValidateBinding();
        if (_voiceSession?.IsArmed == true) _voiceSession.ValidateFrame();
    }
    [HideFromIl2Cpp]
    private void SealVoicePass()
    {
        if (_voiceSession == null) return;
        if (!ExportBuildProfile.DevelopmentAuditEnabled) { _voiceSession.CompletePass(VoicePosition()); return; }
        var receipt = _voiceSession.SealPass(VoicePosition());
        if (_strictPreparing) _voicePreparedReceipt = receipt;
    }
}
