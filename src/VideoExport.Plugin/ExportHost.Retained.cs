using System.Diagnostics;
using AAVideoExport.Core;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private IDisposable? _retainedMenuLease;
    private ExportOptions? _retainedPreparationRequest;
    private string _retainedPreparationStoryKey = "";
    private readonly Stopwatch _retainedPreparationWait = new();
    private Exception? _retainedFault;
    internal bool RetainedCleanupPending => _retainedMenuLease != null || _voiceSession != null || _staticInertScope != null || _recipeScope != null || _frameworkScope != null || _effectsSourceInput != null;

    [HideFromIl2Cpp]
    internal void RetainedBoundaryFailed(Exception error)
    {
        if (_shutdown || _failureHandling) return;
        if (Busy || RetainedCleanupPending)
            Interlocked.CompareExchange(ref _retainedFault, error, null);
        // The originating observer has already latched its publication fault.
        // Do not Dispose another controller while inside an unknown guard stack.
    }
    [HideFromIl2Cpp]
    private void ThrowRetainedFault()
    {
        if (!_strictStartupBoundaryCancelling && (Busy || RetainedCleanupPending) && Volatile.Read(ref _retainedFault) is { } error)
            throw error is ExportException ? error : new ExportException("strict_retained_boundary_invalid", error.Message, error);
    }
    [HideFromIl2Cpp]
    private bool AcquireRetainedBoundaries(ExportOptions options)
    {
        AcquireVoiceController();
        if (_retainedMenuLease != null) { RetainedMenuPlaybackBoundary.ValidateActive(); return true; }
        if (RetainedMenuPlaybackBoundary.TryAcquireRetained(out var lease))
        {
            _retainedMenuLease = lease ?? throw new InvalidOperationException("Menu acquisition returned no retained lease.");
            _retainedPreparationRequest = null; _retainedPreparationWait.Stop(); return true;
        }
        if (_retainedPreparationRequest == null)
        {
            _retainedPreparationRequest = options;
            _retainedPreparationStoryKey = _selectedStoryKey;
            _retainedPreparationWait.Restart();
        }
        _status = "正在等待原生菜单音乐加载完成；尚未读取剧情或开始计帧，可随时取消。";
        return false;
    }
    [HideFromIl2Cpp]
    private void PumpRetainedPreparation()
    {
        if (_retainedPreparationRequest == null) return;
        if (_retainedPreparationWait.Elapsed.TotalSeconds >= 120)
            throw new ExportException("retained_menu_preload_timeout", "原生菜单音频准备超时，导出已停止。");
        if (!string.Equals(_retainedPreparationStoryKey, _selectedStoryKey, StringComparison.OrdinalIgnoreCase))
            throw new ExportException("story_selection_changed", "等待音频期间所选剧情已改变，请重新选择后导出。");
        var request = _retainedPreparationRequest;
        if (!AcquireRetainedBoundaries(request)) return;
        _retainedPreparationRequest = null;
        Prepare(request);
    }
    [HideFromIl2Cpp]
    private void ValidateRetainedControllers()
    {
        if (_retainedMenuLease != null) RetainedMenuPlaybackBoundary.ValidateActive();
        if (_voiceSession != null)
        {
            _voiceSession.ValidateController();
            CharacterVoiceDelayClock.ValidateBinding();
        }
    }
    [HideFromIl2Cpp]
    private void EndRetainedPlayback()
    {
        _voiceWarmup = false;
        var errors = new List<Exception>();
        try { _voiceSession?.EndPass(); } catch (Exception error) { errors.Add(error); }
        try { if (_retainedMenuLease != null) RetainedMenuPlaybackBoundary.EndPlayback(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Retained playback restoration is incomplete.", errors);
    }
    [HideFromIl2Cpp]
    private void ReleaseRetainedBoundaries()
    {
        _retainedPreparationRequest = null; _retainedPreparationWait.Stop(); _voiceWarmup = false;
        var errors = new List<Exception>();
        if (_recipeScope != null)
        {
            try { _recipeScope.Dispose(); _recipeScope = null; _recipeHandedToWorker = false; }
            catch (Exception error) { errors.Add(error); }
        }
        if (_frameworkScope != null)
        {
            try { _frameworkScope.Dispose(); _frameworkScope = null; _frameworkHandedToWorker = false; }
            catch (Exception error) { errors.Add(error); }
        }
        if (_effectsSourceInput != null)
        {
            try { RuntimeModBoundaries.ReleaseEffectsSourceInput(_effectsSourceInput); _effectsSourceInput = null; }
            catch (Exception error) { errors.Add(error); }
        }
        if (_voiceSession != null)
        {
            try { _voiceSession.Dispose(); _voiceSession = null; }
            catch (Exception error) { errors.Add(error); }
        }
        if (_retainedMenuLease != null)
        {
            try { _retainedMenuLease.Dispose(); _retainedMenuLease = null; }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0)
        {
            _renderRestorationFailed = true; RenderControlV1.RestorationFailed();
            throw new AggregateException("Retained controller cleanup remains pending; retry references are preserved.", errors);
        }
        _voicePreparedReceipt = null; _voiceKnownIdentifiers = Array.Empty<string>();
    }
    [HideFromIl2Cpp]
    private void WireRetainedPublication(ExportSession session)
    {
        if (!ExportBuildProfile.DevelopmentAuditEnabled) return;
        var voice = _voiceSession;
        bool menu = _retainedMenuLease != null;
        var previous = session.BeforeOutputCommit;
        session.BeforeOutputCommit = () =>
        {
            if (Volatile.Read(ref _retainedFault) is { } error) throw new ExportException("strict_retained_boundary_invalid", error.Message, error);
            if (menu) RetainedMenuPlaybackBoundary.ValidateForCommit();
            voice?.ValidateForCommit();
            previous?.Invoke();
        };
        var preceding = session.AcquireOutputCommitScope;
        session.AcquireOutputCommitScope = () =>
        {
            var scopes = new List<IDisposable>();
            try
            {
                if (preceding?.Invoke() is { } prior) scopes.Add(prior);
                if (menu) scopes.Add(RetainedMenuPlaybackBoundary.AcquireCommitScope());
                if (voice != null) scopes.Add(voice.AcquireCommitScope());
                if (Volatile.Read(ref _retainedFault) is { } error) throw new ExportException("strict_retained_boundary_invalid", error.Message, error);
                return new RetainedCommitGroup(scopes);
            }
            catch { for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose(); throw; }
        };
    }
    private sealed class RetainedCommitGroup(List<IDisposable> scopes) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            List<Exception>? errors = null;
            for (int i = scopes.Count - 1; i >= 0; i--)
                try { scopes[i].Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
            disposed = true;
            if (errors != null) throw new AggregateException(errors);
        }
    }
}
