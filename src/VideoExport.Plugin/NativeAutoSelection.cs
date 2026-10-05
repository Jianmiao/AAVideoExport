using AAVideoExport.Core;
using UnityEngine;

namespace AAVideoExport.Plugin;

// A session owns only the author's AUTO choice. AA still owns its countdown,
// selection exit animation, callback and scenario branch.
internal sealed class NativeAutoSelection
{
    private readonly Test _player;
    private readonly Action<string> _log;
    private SelectionElement? _submitted;

    internal NativeAutoSelection(Test player, Action<string> log)
    {
        _player = player;
        _log = log;
    }

    internal void Validate()
    {
        var manager = _player.selectionManager;
        if (!_player.hasSelection || manager == null || !manager.isSelectionActive)
        {
            _submitted = null;
            return;
        }
        int index = manager.defaultSelectionIndex;
        if (manager.elements == null || index < 0 || index >= manager.elements.Count)
            throw new ExportException("selection_default_missing",
                $"剧情选项未标记有效的 AUTO 分支，导出已停止。请在选项编辑器标记一个 AUTO 选项后重试。（剧情行 { _player.cur }，默认选项 {index}）");
        if (!_player.IsAutoEnabled || !manager.autoModeEnabled)
            throw new ExportException("selection_auto_disabled", "剧情停在选项处，但自动选择未开启，导出已停止。请重新加载剧情后重试。");
        AutoSelectionHooks.EnsureSupported();
    }

    internal bool Owns(SelectionManager manager) => _player != null && manager != null && _player.selectionManager == manager;

    internal void Reset(SelectionManager manager)
    {
        if (Owns(manager)) _submitted = null;
    }

    internal SelectionElement? FindChoice(SelectionManager manager, int index)
    {
        if (!Owns(manager) || !_player.hasSelection || !_player.IsAutoEnabled || !manager.isSelectionActive || !manager.autoModeEnabled
            || manager.elements == null || index != manager.defaultSelectionIndex || index < 0 || index >= manager.elements.Count)
            return null;
        var element = manager.elements[index];
        if (element == null || element == _submitted || !element.gameObject.activeInHierarchy
            || element.button == null || element.button.disabled || !element.button.gameObject.activeInHierarchy)
            return null;
        return element;
    }

    internal SelectionElement? FindButton(UI.MXButton button)
    {
        var manager = _player.selectionManager;
        if (manager == null || button == null) return null;
        var choice = FindChoice(manager, manager.defaultSelectionIndex);
        return choice != null && choice.button == button ? choice : null;
    }

    internal void Submit(SelectionElement choice, string source)
    {
        var manager = _player.selectionManager;
        if (manager == null || FindChoice(manager, manager.defaultSelectionIndex) != choice) return;
        // Mark before entering native code: the click can synchronously end a
        // choice or re-enter a hooked coroutine. Never submit it twice.
        _submitted = choice;
        _log($"AUTO selection: row={_player.cur}, index={manager.defaultSelectionIndex}, countdown={manager.autoSelectDelaySeconds}, source={source}.");
        choice.OnSelect();
    }
}
