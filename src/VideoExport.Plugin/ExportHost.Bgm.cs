using AAVideoExport.Core;
using AAVideoExport.Integration;
using Il2CppInterop.Runtime.Attributes;

namespace AAVideoExport.Plugin;

public sealed partial class ExportHost
{
    private NativeBgmSessionAdapter? _bgmSession;
    private NativeBgmSessionAdapter? _bgmCleanup;
    private Exception? _bgmCleanupFailure;
    private bool _bgmWarmup;

    [HideFromIl2Cpp]
    private void BeginBgmPreparation()
    {
        if (!ExportBuildProfile.SinglePassEnabled || _bgmSession != null) return;
        // The loader releases _frozenSave after Begin returns, even when voice
        // loading continues. Retain the actual BGM inputs before that boundary.
        _bgmSession = NativeBgmSessionAdapter.Prepare(_frozenSave
            ?? throw new ExportException("story_missing", "背景音乐预加载缺少所选原生剧情表。"),
            RetainedBoundaryFailed, message => UnityEngine.Debug.Log("AA Video Export: " + message), BgmCleanupFailed);
        _bgmWarmup = true;
    }

    [HideFromIl2Cpp]
    private bool PumpBgmPreparation()
    {
        if (!ExportBuildProfile.SinglePassEnabled) return true;
        var scope = _bgmSession ?? throw new ExportException("invalid_state", "背景音乐预加载会话已丢失。");
        if (!scope.PumpPreload())
        {
            _bgmWarmup = true;
            _status = "正在预加载剧情背景音乐；准备完成后开始同步导出，可随时取消。";
            return false;
        }
        _bgmWarmup = false;
        return true;
    }

    [HideFromIl2Cpp]
    private void DisposeBgmSession()
    {
        _bgmWarmup = false;
        var scope = _bgmSession;
        _bgmSession = null;
        if (scope == null) return;
        try { scope.Dispose(); }
        finally { if (scope.CleanupPending) _bgmCleanup = scope; }
    }

    [HideFromIl2Cpp]
    private void PumpBgmCleanup()
    {
        if (_bgmCleanupFailure != null)
        {
            BgmCleanupFailed(_bgmCleanupFailure);
            return;
        }
        if (_bgmCleanup == null) return;
        if (_bgmCleanup.CleanupFailure is { } failure)
        {
            // Native release can throw after taking effect. Keep ownership and
            // require restart instead of risking a duplicate release.
            BgmCleanupFailed(failure);
            return;
        }
        // The original custom loader publishes into its cache after returning
        // from our callback. A later native Update is the safe release point.
        if (!_bgmCleanup.CleanupPending) _bgmCleanup = null;
        else _status = "已取消，正在等待背景音乐加载结束后释放原生资源…";
    }

    [HideFromIl2Cpp]
    private void BgmCleanupFailed(Exception error)
    {
        if (_bgmCleanupFailure == null)
        {
            _bgmCleanupFailure = error;
            RenderControlV1.RestorationFailed();
            UnityEngine.Debug.LogError("AA Video Export: native BGM resource restoration failed: " + error);
        }
        _renderRestorationFailed = true;
        _statusIsError = true;
        _status = "背景音乐资源恢复失败，无法继续导出。请重启 AA 后重试。";
    }
}
