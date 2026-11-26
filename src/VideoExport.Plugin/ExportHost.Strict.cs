using System.Globalization;
using System.Security.Cryptography;
using AAVideoExport.Core;
using FlatData;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private bool _strictPreparing, _strictReplayPending, _strictPriming, _strictStartReady,
        _strictAwaitingStart, _strictFinishPending, _strictDraining, _strictRandomOwned;
    private int _strictSeed;
    private long _strictPrimerSamples;
    private int _strictDrainUpdates;
    private double _strictDrainStarted;
    private string _strictIdentity = "", _strictStoryHash = "", _strictSettings = "", _strictModFingerprint = "";
    private string _strictAudioSettings = "";
    private string _strictSettingsWithoutEffectsSource = "";
    private bool _effectsSourcePending;
    private bool _effectsSourceWaiting;
    private float _effectsSourceWaitStarted;
    private EffectsSourceInput? _effectsSourceInput;
    private StrictFramePlan? _strictPlan;
    private PreparedAudioTrack? _preparedAudio;
    private RawAudioPass? _rawAudio;
    private StrictReplayLedger? _strictLedger;
    private PreparedFrameSchedule? _preparedFrameSchedule;
    private RepeatExportBaseline? _repeatBaseline;
    private StrictPreflightReceipt? _strictPreflightReceipt;
    private StrictPreflightCacheFiles? _strictPreflightFiles;
    private StrictCapabilitySnapshot? _strictCapabilitySnapshot;
    private int _strictCapabilityGeneration;
    private StrictSandboxTrace? _strictSandboxTrace;
    private bool _strictSandboxEndRecorded;
    private ExportEndFence _endFence = new();
    private UnityEngine.Random.State _randomBeforePass;
    private Task<string>? _strictReplaySourceCheck;
    private readonly System.Diagnostics.Stopwatch _strictTotalElapsed = new();

    [HideFromIl2Cpp]
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    [HideFromIl2Cpp]
    private static string PlaybackSettingsIdentity() => PlaybackSettingsIdentityCore(includeEffectsSource: true);
    [HideFromIl2Cpp]
    private static string PlaybackSettingsIdentityCore(bool includeEffectsSource)
    {
        var data = UserSettings.Instance?.data;
        if (data == null) throw new ExportException("story_resources_unavailable", "Native playback settings are unavailable.");
        string native = FormattableString.Invariant($"{data.autoDelay}|{data.masterVolume:R}|{data.voiceVolume:R}|{data.bgmVolume:R}|{data.soundEffectVolume:R}|{data.stEnabled}|{data.fontIndex}");
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
            return native + "|" + (includeEffectsSource ? RuntimeModBoundaries.PlaybackSettingsIdentity() : RuntimeModBoundaries.PlaybackSettingsWithoutEffectsSource());
        return native
            + "|" + (includeEffectsSource ? RuntimeModBoundaries.PlaybackSettingsIdentity() : RuntimeModBoundaries.PlaybackSettingsWithoutEffectsSource())
            + "|" + MultilingualCaptureAdapter.PlaybackSettingsIdentity()
            + "|" + RetainedMenuPlaybackBoundary.PlaybackSettingsIdentity();
    }

    [HideFromIl2Cpp]
    private void InitializeStrictPreparation(string storyPath)
    {
        // Prepare captured and admitted this snapshot before native story
        // loading. Dispose only the previous pass state, not this new lease's
        // admission identity.
        var capabilities = _strictCapabilitySnapshot;
        int generation = _strictCapabilityGeneration;
        // The new acquisition precedes source loading. Clear only old media
        // state here; the same retained receipts must survive both passes.
        var retainedInert = _staticInertScope; _staticInertScope = null;
        var retainedVoice = _voiceSession; _voiceSession = null;
        var retainedMenu = _retainedMenuLease; _retainedMenuLease = null;
        var retainedRecipes = _recipeScope; _recipeScope = null;
        var retainedFramework = _frameworkScope; _frameworkScope = null;
        try { DisposeStrictPreparation(); }
        finally { _staticInertScope = retainedInert; _voiceSession = retainedVoice; _retainedMenuLease = retainedMenu; _recipeScope = retainedRecipes; _frameworkScope = retainedFramework; }
        _strictCapabilitySnapshot = capabilities;
        _strictCapabilityGeneration = generation;
        _strictPreparing = !ExportBuildProfile.SinglePassEnabled;
        _effectsSourcePending = RuntimeModBoundaries.RequiresEffectsSourcePreparation();
        _strictSettings = PlaybackSettingsIdentity();
        _strictSettingsWithoutEffectsSource = PlaybackSettingsIdentityCore(includeEffectsSource: false);
        _strictAudioSettings = AudioSettings.outputSampleRate + "|" + AudioSettings.speakerMode;
        _strictTotalElapsed.Restart();
    }

    [HideFromIl2Cpp]
    private void PrepareStrictIdentity(string storyPath)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled)
        {
            if (!File.Exists(storyPath)) throw new ExportException("story_missing", "所选剧情文件不存在。");
            _strictIdentity = Path.GetFullPath(storyPath); // Execution selection key, not a provenance digest.
            _strictStoryHash = ""; _strictModFingerprint = "";
            // Preserve the established visual RNG seed. This one scenario read
            // is an execution algorithm: no digest is compared, retained as a
            // provenance identity, logged, or written to a receipt.
            using (var stream = File.OpenRead(storyPath))
            using (var seed = SHA256.Create()) _strictSeed = BitConverter.ToInt32(seed.ComputeHash(stream), 0);
            _repeatBaseline = null; _strictPreflightReceipt = null; _strictPreflightFiles = null;
            return;
        }
        // Called by the encoder preparation worker before any native playback.
        _strictStoryHash = HashFile(storyPath);
        // The runtime capability inventory will replace this conservative
        // fingerprint. Keep background preparation free of BepInEx path calls;
        // those Unity-owned path providers are not safe to query from the
        // encoder worker on every host.
        _strictModFingerprint = _strictCapabilitySnapshot?.Fingerprint ?? HashText(Plugin.HostIdentity + "|" + Plugin.Version);
        BuildStrictInputIdentity();
        _strictSeed = BitConverter.ToInt32(Convert.FromHexString(_strictStoryHash), 0);
        // An unresolved effects input has a diagnostic provisional key only.
        // No committable baseline exists until its actual source is frozen on
        // the main thread after this worker completes, before authored frame 0.
        _repeatBaseline = _effectsSourcePending ? null : new RepeatExportBaseline(Plugin.TimelineReceiptDirectory, _strictIdentity);
        // The current preflight protocol stores row/PCM proof only. It cannot
        // reconstruct the observed command, voice and background receipts now
        // required for every completed baseline. Preserve those cache files,
        // but always observe a fresh preparation until a complete cache
        // protocol can replay and validate all three output fingerprints.
        _strictPreflightReceipt = null;
        _strictPreflightFiles = null;
    }

    [HideFromIl2Cpp]
    private void BuildStrictInputIdentity()
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        string values = _strictStoryHash + "|" + Plugin.HostIdentity + "|" + _strictModFingerprint + "|" + _strictSettings + "|" + _strictAudioSettings + "|" +
            System.Text.Json.JsonSerializer.Serialize(_options! with { Title = "", OutputDirectory = "" });
        if (_effectsSourceInput != null) values += "|effects-source=" + _effectsSourceInput.Identity;
        _strictIdentity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(values)));
    }

    [HideFromIl2Cpp]
    private void CompleteStrictSourcePreparation()
    {
        if (_options?.StrictSynchronization != true) return;
        if (_effectsSourcePending)
        {
            if ((!_strictPreparing && !ExportBuildProfile.SinglePassEnabled) || Capturing || _captured != 0 || _prepare != null || _session == null)
                throw new ExportException("invalid_state", "Effects input completion must follow encoder preparation and precede any authored capture.");
            ValidateUnchangedPreparationInputs();
            _effectsSourceInput = RuntimeModBoundaries.PrepareEffectsSourceInput(_selectedStoryKey, _strictStoryHash);
            ValidateUnchangedPreparationInputs();
            _strictSettings = PlaybackSettingsIdentity();
            _effectsSourcePending = false;
            BuildStrictInputIdentity();
            if (ExportBuildProfile.DevelopmentAuditEnabled) _repeatBaseline = new RepeatExportBaseline(Plugin.TimelineReceiptDirectory, _strictIdentity);
        }
        else
        {
            if (_effectsSourceInput != null) RuntimeModBoundaries.RevalidateEffectsSourceInput(_effectsSourceInput);
            if (_strictSettings != PlaybackSettingsIdentity() || (ExportBuildProfile.DevelopmentAuditEnabled ? _strictStoryHash != HashFile(_selectedStoryKey) : !File.Exists(_selectedStoryKey))
                || _strictAudioSettings != AudioSettings.outputSampleRate + "|" + AudioSettings.speakerMode)
                throw new ExportException("timeline_source_changed", "A frozen input changed before the next authored pass.");
        }
    }

    [HideFromIl2Cpp]
    private void ValidateUnchangedPreparationInputs()
    {
        if (_strictSettingsWithoutEffectsSource != PlaybackSettingsIdentityCore(includeEffectsSource: false)
            || (ExportBuildProfile.DevelopmentAuditEnabled ? _strictStoryHash != HashFile(_selectedStoryKey) : !File.Exists(_selectedStoryKey))
            || _strictAudioSettings != AudioSettings.outputSampleRate + "|" + AudioSettings.speakerMode)
            throw new ExportException("timeline_source_changed", "An already frozen story, playback setting or audio input changed while the effects source was being discovered.");
    }

    [HideFromIl2Cpp]
    private void WireEffectsSourcePublication(ExportSession session)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var source = _effectsSourceInput;
        if (source == null) return;
        source.ValidateCurrent(requireActive: false);
        var precedingValidation = session.BeforeOutputCommit;
        session.BeforeOutputCommit = () => { source.ValidateFiles(); precedingValidation?.Invoke(); };
        var precedingScope = session.AcquireOutputCommitScope;
        session.AcquireOutputCommitScope = () =>
        {
            var scopes = new List<IDisposable>();
            try
            {
                if (precedingScope?.Invoke() is { } outer) scopes.Add(outer);
                scopes.Add(source.AcquireCommitScope());
                return new RetainedCommitGroup(scopes);
            }
            catch { for (int index = scopes.Count - 1; index >= 0; index--) scopes[index].Dispose(); throw; }
        };
    }

    [HideFromIl2Cpp]
    private void BeginStrictPass()
    {
        if (_options?.StrictSynchronization != true) return;
        if (_effectsSourcePending || ExportBuildProfile.DevelopmentAuditEnabled && _repeatBaseline == null)
            throw new ExportException("strict_effects_source_unavailable", "Authored capture cannot begin with an unresolved effective source input.");
        if (_strictSettings != PlaybackSettingsIdentity() || _strictAudioSettings != AudioSettings.outputSampleRate + "|" + AudioSettings.speakerMode)
            throw new ExportException("timeline_source_changed", "The selected story or playback settings changed during export.");
        _captured = 0;
        _endAt = -1;
        _endFence = new ExportEndFence();
        _strictFinishPending = _strictStartReady = false;
        _strictDraining = false;
        _strictDrainUpdates = 0;
        _strictDrainStarted = 0;
        _strictAwaitingStart = _strictPriming = true;
        _strictPrimerSamples = 0;
        _randomBeforePass = UnityEngine.Random.state;
        _strictRandomOwned = true;
        _strictSandboxEndRecorded = false;
        if (_strictPreflightReceipt != null || _strictPreflightFiles != null)
            throw new ExportException("preflight_cache_invalid", "A row/PCM-only preflight cannot replace the required observed event, voice and background proof. A fresh complete preparation is required; existing cache files were preserved.");
        if (ExportBuildProfile.SinglePassEnabled)
        {
            _session!.BeginLiveTimeline();
            Debug.Log($"AA Video Export phase=single-pass fps={_options.Fps} rate={_native!.SampleRate} channels={_native.Channels}; one authored playback with native offline audio.");
            return;
        }
        if (_strictPreparing)
        {
            if (ExportBuildProfile.DevelopmentAuditEnabled) _strictSandboxTrace ??= new StrictSandboxTrace();
            _preparedAudio = _session!.CreateAudioPreparationTrack();
            if (ExportBuildProfile.DevelopmentAuditEnabled)
                _strictLedger = new StrictReplayLedger(Path.Combine(_options.OutputDirectory,
                    ".aave-transcript-" + Guid.NewGuid().ToString("N")), _native!.Channels);
            else _preparedFrameSchedule = new PreparedFrameSchedule();
        }
        else
            _strictPlan!.ValidateCaptureIdentity(_strictIdentity, _options.Fps, _native!.SampleRate, _native.Channels);
        Debug.Log($"AA Video Export strict phase={(_strictPreparing ? "audio-preparation" : "video-replay")} fps={_options.Fps} rate={_native!.SampleRate} channels={_native.Channels} planned_frames={_strictPlan?.FrameCount ?? -1}; authored start waits for mixer cycle.");
    }

    [HideFromIl2Cpp]
    internal void RecordStrictSandboxCommand(string apiFamily, string operation, string targetId, string argumentsHash,
        bool once = false)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled || !Capturing || _strictSandboxTrace == null) return;
        if (once && _strictSandboxEndRecorded) return;
        long ticks = Math.Max(0L, (long)Math.Round(NativeExportClock.Now * TimeSpan.TicksPerSecond));
        var command = new StrictSandboxCommand(_strictSandboxTrace.NextSequence, _captured, ticks,
            _strictModFingerprint, apiFamily, operation, targetId, argumentsHash, null, null);
        if (_strictPreparing) _strictSandboxTrace.Append(command);
        else _strictSandboxTrace.Expect(command);
        if (once) _strictSandboxEndRecorded = true;
    }

    [HideFromIl2Cpp]
    internal bool RecordStrictStoryCommand(Test player, string operation, bool once = false)
    {
        // A scene reload changes native addresses. The selected story is the
        // stable identity of this single playback root, not the Test pointer.
        // Events from preview/other players must remain outside this trace.
        if (!Capturing || _player != player || _strictAwaitingStart || _strictDraining) return true;
        try
        {
            bool selection = player.hasSelection;
            int choice = selection && player.selectionManager != null ? player.selectionManager.defaultSelectionIndex : -1;
            string arguments = FormattableString.Invariant($"{operation}|row={player.cur}|selection={selection}|choice={choice}");
            RecordStrictSandboxCommand("Story", operation, "story-player/" + _strictStoryHash, HashText(arguments), once);
            return true;
        }
        catch (Exception error)
        {
            // Harmony observers must not merely log a mismatch and let the
            // authored event continue with a still-live export session.
            Fail(error);
            return false;
        }
    }

    [HideFromIl2Cpp]
    private void BeginStrictAudioTimeline()
    {
        if (_options?.StrictSynchronization != true || _native == null) return;
        _native.ResetAudioTimeline();
        _rawAudio = null;
        Debug.Log($"AA Video Export strict audio raw pass armed: rate={_native.SampleRate}, channels={_native.Channels}; epoch is anchored at the first authored native block.");
    }

    [HideFromIl2Cpp]
    internal bool AllowStrictScenarioAdvance(Test player, IScenarioScriptExcel? scriptOverride)
    {
        if (_player == player && (_voiceWarmup || _bgmWarmup || _effectsSourceWaiting)) return false;
        if (!Capturing || _player != player) return true;
        if (_strictDraining) return false;
        // The fixed replay frame count can be reached one callback before AA
        // dispatches the story's terminal End. Keep the terminal advance
        // observable so BeforePlayerEnd can seal the native fence. Once End
        // has been observed, the pending finish remains a hard boundary.
        if (_strictFinishPending && _endFence.FrameCount >= 0) return false;
        if (!_strictAwaitingStart) return true;
        if (scriptOverride != null)
            Fail(new ExportException("timeline_source_changed", "Strict capture requires the story's initial native advance without an override."));
        return false;
    }

    [HideFromIl2Cpp]
    private bool AcceptStrictPrimer(int floatCount)
    {
        _strictPrimerSamples = checked(_strictPrimerSamples + floatCount / _native!.Channels);
        int a = _native.SampleRate, b = _options!.Fps;
        while (b != 0) { int r = a % b; a = b; b = r; }
        // Start at a complete rational audio/video cycle. For 44.1k/24 this
        // is 3675 samples (1837+1838), not the first non-empty mixer callback.
        long samplesPerCycle = _native.SampleRate / a;
        return _strictPrimerSamples > 0 && _strictPrimerSamples % samplesPerCycle == 0;
    }

    [HideFromIl2Cpp]
    private void CaptureStrictAudioAndState(float[] samples, int count)
    {
        if (_options?.StrictSynchronization != true)
        {
            if (_options?.AudioQuality != "none") _session!.WriteAudioFrame(_captured, samples, count);
            return;
        }
        int row = _player!.cur;
        bool selection = _player.hasSelection;
        int choice = selection && _player.selectionManager != null ? _player.selectionManager.defaultSelectionIndex : -1;
        EnsureStrictAudioPass();
        _rawAudio!.Append(_native!.LastAudioRawStartSample, samples, count);
        if (ExportBuildProfile.SinglePassEnabled) return;
        if (_strictPreparing)
        {
            if (ExportBuildProfile.DevelopmentAuditEnabled) _strictLedger!.PrepareState(row, selection, choice);
            else (_preparedFrameSchedule ?? throw new ExportException("invalid_state", "Prepared audio has no native execution schedule.")).Record(_captured, row, selection, choice);
        }
        else
        {
            // Replay audio continues driving AA voice/auto waits. The output
            // uses immutable prepared PCM; raw replay PCM is checked after the
            // bounded drain because native callback boundaries can differ.
            if (ExportBuildProfile.DevelopmentAuditEnabled) _strictLedger!.ValidateState(row, selection, choice);
            else (_preparedFrameSchedule ?? throw new ExportException("invalid_state", "Replay has no prepared native execution schedule.")).Consume(_captured, row, selection, choice);
            var prepared = _preparedAudio!.ReadFrame(_captured);
            if (_options.AudioQuality != "none")
                _session!.WriteAudioFrame(_captured, prepared.Samples, prepared.Samples.Length);
        }
    }

    [HideFromIl2Cpp]
    private void EnsureStrictAudioPass()
    {
        if (_rawAudio != null || _native == null || _options == null) return;
        string path = Path.Combine(_options.OutputDirectory, ".aave-raw-audio-" + Guid.NewGuid().ToString("N") + ".pcm");
        _rawAudio = new RawAudioPass(path, _native.SampleRate, _native.Channels,
            _native.AudioRawDspEpochSeconds, leadSamples: 0);
        Debug.Log($"AA Video Export strict audio raw pass started: epoch={_native.AudioRawDspEpochSeconds:R}, rate={_native.SampleRate}, channels={_native.Channels}.");
    }

    [HideFromIl2Cpp]
    private bool DrainStrictAudioStep()
    {
        if (!_strictDraining || _rawAudio == null || _native == null || _options == null)
            throw new ExportException("invalid_state", "Strict audio drain is not initialized.");
        if (_rawAudio.CanSeal(_captured, _options.Fps)) return true;
        if (_strictDrainUpdates++ >= 120 || _strictDrainStarted > 0 &&
            (Time.realtimeSinceStartup - _strictDrainStarted) >= 10d)
            throw new ExportException("audio_timeline_incomplete",
                $"Native audio did not reach the fixed story end after {_strictDrainUpdates} drain updates: received={_rawAudio.RawSampleFrames}, required={_rawAudio.RequiredRawSampleFrames(_captured, _options.Fps)}.");
        var drained = _native.RenderAudio();
        _rawAudio.Append(_native.LastAudioRawStartSample, drained.Samples, drained.Count);
        return _rawAudio.CanSeal(_captured, _options.Fps);
    }

    [HideFromIl2Cpp]
    private void SealStrictAudio()
    {
        if (_rawAudio == null || _native == null || _options == null)
            throw new ExportException("invalid_state", "Strict audio pass is not initialized.");
        _rawAudio.Seal(_captured, _options.Fps);
        if (ExportBuildProfile.SinglePassEnabled)
        {
            if (_options.AudioQuality != "none")
                _rawAudio.CopyContentTo(slice =>
                {
                    if (slice.Array != null && slice.Count != 0)
                        _session!.WriteAudio(slice.Array, slice.Offset, slice.Count);
                });
            Debug.Log($"AA Video Export strict audio content: raw={_rawAudio.RawSampleFrames} first_content={_rawAudio.FirstContentFrame} content={_rawAudio.Receipt?.ContentSampleFrames} tail={_rawAudio.Receipt?.TailSampleFrames}.");
        }
        else if (_strictPreparing)
        {
            _rawAudio.CopyContentTo(slice =>
            {
                if (slice.Array == null || slice.Count == 0) return;
                _preparedAudio!.Append(slice.Array, slice.Offset, slice.Count);
                if (ExportBuildProfile.DevelopmentAuditEnabled) _strictLedger!.AppendPreparationPcm(slice.Array, slice.Offset, slice.Count);
            });
            _preparedAudio!.Seal(_captured);
        }
        else if (ExportBuildProfile.DevelopmentAuditEnabled)
        {
            _rawAudio.CopyContentTo(slice =>
            {
                if (slice.Array != null && slice.Count != 0)
                    _strictLedger!.AppendReplayPcm(slice.Array, slice.Offset, slice.Count);
            });
        }
        _rawAudio.Dispose();
        _rawAudio = null;
    }

    [HideFromIl2Cpp]
    private void FinishStrictPass()
    {
        if (_options?.StrictSynchronization != true) { Finish(); return; }
        if (_strictStartupBoundaryAcquired) AASkipIntroExportBoundary.ValidateActive();
        if (ExportBuildProfile.DevelopmentAuditEnabled) RequireStaticInertScope().ValidateForSeal();
        if (!_endFence.IsComplete(_captured))
            throw new ExportException("video_timeline_mismatch", "Replay reached the fixed frame count without the matching native story end.");
        ValidateRetainedControllers();
        ValidateFrameworkFrame(required: true, rehashPhysicalImages: true);
        ValidateRecipeFrame(required: true);
        SealVoicePass();
        if (ExportBuildProfile.SinglePassEnabled)
        {
            FinishSinglePass();
            return;
        }
        if (_strictPreparing)
        {
            if (ExportBuildProfile.DevelopmentAuditEnabled)
            {
            // Background texture injection was removed from the runtime. The
            // empty trace is a fixed identity, not a per-Mod observation.
            // Input identity was frozen before playback. Output events must
            // never choose a new baseline when timing changes under that input.
            // Hash actual empty/null observations too; do not invent a zero
            // digest for a missing voice provider or an unused background.
            string eventHash = (_strictSandboxTrace ?? throw new ExportException("invalid_state", "Strict preparation has no observed command trace.")).TraceSha256;
            string voiceHash = HashText(System.Text.Json.JsonSerializer.Serialize(_voicePreparedReceipt));
            string backgroundHash = HashText("[]");
            Debug.Log($"AA Video Export strict phase=seal frames={_captured} expected_samples={ExportTimeline.AudioSampleFramesForVideoFrames(_captured, _options.Fps, _native!.SampleRate)}; raw content was sealed without source padding or trimming.");
            _strictLedger!.BeginReplay();
            _strictSandboxTrace?.BeginReplay();
            _strictPlan = new StrictFramePlan(_strictIdentity, _captured, _options.Fps, _native!.SampleRate, _native.Channels);
            _repeatBaseline!.Validate(_strictPlan.FrameCount, _strictLedger.FrameTraceSha256, _preparedAudio!.SamplesSha256,
                eventHash, voiceHash, backgroundHash);
            string cachePcm = Path.Combine(_options.OutputDirectory, ".aave-preflight-" + Guid.NewGuid().ToString("N") + ".pcm");
            string cacheTranscript = Path.Combine(_options.OutputDirectory, ".aave-preflight-" + Guid.NewGuid().ToString("N") + ".trace");
            try
            {
                _preparedAudio!.CopySealedTo(cachePcm);
                _strictLedger.CopyTranscriptTo(cacheTranscript);
                string transcriptHash = HashFile(cacheTranscript);
                var preflight = StrictPreflightReceipt.Create(_strictIdentity, _strictStoryHash, Plugin.HostIdentity,
                    _strictModFingerprint, HashText(_strictSettings + "|" + _strictAudioSettings), _strictPlan.FrameCount,
                    _strictPlan.Fps, _strictPlan.SampleRate, _strictPlan.Channels, _strictLedger.FrameTraceSha256,
                    _preparedAudio.SamplesSha256, transcriptHash, _strictPlan.AudioSampleFrameCount);
                new StrictPreflightCache(Plugin.TimelineReceiptDirectory, _strictIdentity).Publish(preflight, cachePcm, cacheTranscript);
                _strictPreflightReceipt = preflight;
            }
            finally
            {
                try { File.Delete(cachePcm); } catch { }
                try { File.Delete(cacheTranscript); } catch { }
            }
            _session!.BindStrictTimeline(_strictPlan);
            // Capture immutable values for a worker callback; no Unity access
            // and no lifetime race with cancellation/scene restoration.
            var baseline = _repeatBaseline;
            long frames = _strictPlan.FrameCount;
            string traceHash = _strictLedger.FrameTraceSha256, pcmHash = _preparedAudio.SamplesSha256;
            _session.BeforeOutputCommit = () => baseline.Commit(frames, traceHash, pcmHash, eventHash, voiceHash, backgroundHash);
            }
            else
            {
                (_preparedFrameSchedule ?? throw new ExportException("invalid_state", "Prepared audio has no native execution schedule.")).Seal(_captured);
                _strictPlan = StrictFramePlan.CreateExecution(_captured, _options.Fps, _native!.SampleRate, _native.Channels);
                _strictPlan.ValidateAudioComplete(_preparedAudio!.ReceivedSampleFrames);
                _session!.BindStrictTimeline(_strictPlan);
            }
            _strictPreparing = false;
            _strictReplayPending = true;
            string sourcePath = _selectedStoryKey;
            _strictReplaySourceCheck = ExportBuildProfile.DevelopmentAuditEnabled
                ? Task.Run(() => HashFile(sourcePath)) : Task.FromResult(Path.GetFullPath(sourcePath));
            _ = _strictReplaySourceCheck.ContinueWith(task => { _ = task.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            RestoreNativeCapture();
            RestoreStrictRandom();
            var player = _player; _player = null;
            _returningToCatalog = true;
            _loadingTimer.Restart();
            _status = "音频预渲染完成，正在按固定时间轴重新加载画面…";
            // Retain the outer RenderControlV1 lease, encoder, immutable PCM,
            // and frame ring across this scene round trip.
            player!.End();
        }
        else
        {
            _strictPlan!.ValidateVideoEnd(_endFence.FrameCount);
            _strictPlan.ValidateVideoComplete(_captured);
            _preparedAudio!.CompleteReplay();
            if (!ExportBuildProfile.DevelopmentAuditEnabled)
                (_preparedFrameSchedule ?? throw new ExportException("invalid_state", "Replay has no prepared native execution schedule.")).Complete(_captured);
            if (ExportBuildProfile.DevelopmentAuditEnabled) { _strictLedger!.CompleteReplay(); _strictSandboxTrace?.CompleteReplay(); }
            _strictSandboxTrace = null;
            // ExportSession's finalizer removes its private directory. Release
            // read handles before its worker performs that cleanup on Windows.
            _preparedAudio.Dispose(); _preparedAudio = null;
            _strictLedger?.Dispose(); _strictLedger = null;
            _preparedFrameSchedule = null;
            Finish();
            RestoreStrictRandom();
        }
    }

    [HideFromIl2Cpp]
    private void LaunchStrictReplay()
    {
        if (_strictReplaySourceCheck == null || !_strictReplaySourceCheck.IsCompleted) return;
        if (ExportBuildProfile.DevelopmentAuditEnabled) RequireStaticInertScope().ValidateFrame();
        string expectedSource = ExportBuildProfile.DevelopmentAuditEnabled ? _strictStoryHash : Path.GetFullPath(_selectedStoryKey);
        if (expectedSource != _strictReplaySourceCheck.GetAwaiter().GetResult() || !File.Exists(_selectedStoryKey) || _strictSettings != PlaybackSettingsIdentity())
            throw new ExportException("timeline_source_changed", "Story or settings changed between the audio and video passes.");
        _strictReplaySourceCheck = null;
        _strictPreflightReceipt = null; _strictPreflightFiles = null;
        // The scene is replaced, but this is still the same export. Preserve
        // its admitted module set and the sealed trace now in replay phase.
        _strictSandboxEndRecorded = false;
        var (card, key) = ResolveStory();
        if (!string.Equals(Path.GetFullPath(key), Path.GetFullPath(_selectedStoryKey), StringComparison.OrdinalIgnoreCase))
            throw new ExportException("story_selection_changed", "The selected story changed before video replay.");
        if (!StorySourcePath.TryGetScenarioName(key,
            name => ScenarioResourceManager.GetPersistentFilePath("saves", name, ".aas"), out string name))
            throw new ExportException("story_location_unsupported", "The prepared story cannot be reloaded through AA.");
        _frozenSave = (ScenarioResourceManager.Instance ?? throw new ExportException("story_resources_unavailable", "AA 的资源服务暂不可用。")).LoadGenericScenario(name);
        ValidateReplayVoiceStory();
        _loadingStory = card;
        _launchTicket = _launch.Begin(key);
        _strictReplayPending = false;
        _launch.CompletePreparation(_launchTicket, path =>
        {
            _armed = true; _eligiblePlayer = IntPtr.Zero;
            PersistentData.saveData = _frozenSave; PersistentData.projectPath = path;
            _loadingTimer.Restart(); _nativeLoadPending = true; _nativeLoadCancelled = false;
            try { _loadingCoroutine = card.StartCoroutine(card.CoLoadSave()); }
            catch { _nativeLoadPending = false; throw; }
        });
    }

    [HideFromIl2Cpp]
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [HideFromIl2Cpp]
    private void RestoreStrictRandom()
    {
        if (!_strictRandomOwned) return;
        UnityEngine.Random.state = _randomBeforePass;
        _strictRandomOwned = false;
    }

    [HideFromIl2Cpp]
    private void DisposeStrictPreparation()
    {
        _effectsSourceWaiting = false; _effectsSourceWaitStarted = 0;
        Exception? retainedCleanupError = null;
        try { DisposeBgmSession(); } catch (Exception error) { retainedCleanupError = error; }
        try { ReleaseRetainedBoundaries(); }
        catch (Exception error) { retainedCleanupError = retainedCleanupError == null ? error : new AggregateException(retainedCleanupError, error); }
        // Invalidate before any fallible media cleanup so cancellation cannot
        // leave a late worker holding a usable publication receipt.
        ReleaseStaticInertScope();
        RestoreStrictRandom();
        _rawAudio?.Dispose(); _rawAudio = null;
        _preparedAudio?.Dispose(); _preparedAudio = null;
        _strictLedger?.Dispose(); _strictLedger = null;
        _preparedFrameSchedule = null;
        _strictPlan = null; _repeatBaseline = null;
        _effectsSourcePending = false; _strictSettingsWithoutEffectsSource = "";
        _strictReplaySourceCheck = null;
        _strictPreflightReceipt = null; _strictPreflightFiles = null;
        _strictCapabilitySnapshot = null; _strictCapabilityGeneration = 0;
        _strictSandboxTrace = null; _strictSandboxEndRecorded = false;
        _strictPreparing = _strictReplayPending = _strictFinishPending = _strictDraining = _strictAwaitingStart = _strictStartReady = _strictPriming = false;
        if (retainedCleanupError != null) throw retainedCleanupError;
    }
}
