using InkCanvas.App.Views;
using InkCanvas.Core.AiAssistant;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

/// <summary>板中板窗口的 Shell 接入：单实例、Show/Hide 保留内容、随主展板一并关闭。</summary>
public sealed partial class ShellController
{
    private SideBoardWindow _sideBoard;

    /// <summary>诊断/自测入口：当前存活的子板窗口（未创建时为 null），不参与产品逻辑。</summary>
    internal SideBoardWindow CurrentSideBoard => _sideBoard;

    public void ToggleSideBoard()
    {
        if (_disposed || _exiting) return;
        if (_board is null)
        {
            ToggleBoard();
            if (_board is null) return;
        }
        if (_sideBoard is null)
        {
            var window = new SideBoardWindow(Services, this, _board);
            window.Closed += OnSideBoardClosed;
            window.SideBoardVisibilityChanged += OnSideBoardVisibilityChanged;
            window.SideBoardPageChanged += NotifySideBoardPageChanged;
            _sideBoard = window;
            NotifyWindowOpened(window);
        }
        if (_sideBoard.IsShown)
        {
            _sideBoard.RequestHide();
            return;
        }
        _sideBoard.ShowBoard();
        WindowChrome.EnsureAbove(_sideBoard, WindowChrome.GetHwnd(_board));
        _sideBoard.Activate();
    }

    /// <summary>统一子板显示/收回联动：隐藏时同步隐藏 AI 浮窗（不销毁会话），显示时恢复。</summary>
    private void OnSideBoardVisibilityChanged(bool visible)
    {
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[SideBoard] Shell 收到可见性：visible={visible}，assistant={_aiAssistant is not null}，"
            + $"belongs={AssistantBelongsToSideBoard}，hiddenFlag={_aiHiddenWithSideBoard}，"
            + $"target={AssistantTargetPolicy.Describe(_aiTarget)}",
            Core.Diagnostics.LogType.Info);
        // 显示时把输入/AI 目标切到子板控制器，收回时回到主展板；ActiveController 由 IsShown 决定。
        BindBoardController(visible ? _sideBoard?.CurrentController : Services.Board.Current);
        BindToolbar(ActiveController);
        if (visible) ShowAssistantForSideBoard();
        else HideAssistantForSideBoard();
    }

    /// <summary>
    /// 子板翻页后刷新统一上下文与 AI 浮窗锚点：只在上下文锚定子板时更新页身份，
    /// 过期提示由后续流式/写回校验按需触发，这里不逐次弹窗。
    /// </summary>
    internal void NotifySideBoardPageChanged()
    {
        if (UnifiedContextEnabled && _aiContext?.Kind == BoardCanvasKind.SideBoard)
        {
            _aiContext = _aiContext with
            {
                PageId = Services.SideBoard.CurrentPageId,
                PageIndex = Services.SideBoard.CurrentIndex,
            };
            SyncRecorderContext();
        }

        Assistant?.NotifyBoardChanged();
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"{ClassroomContextPolicy.Describe(_aiContext)} 子板翻页：board=side，page={Services.SideBoard.CurrentIndex}，"
            + $"pageId={Services.SideBoard.CurrentPageId}",
            Core.Diagnostics.LogType.Info);
    }

    private void OnSideBoardClosed(object sender, WindowEventArgs args)
    {
        if (_sideBoard is null || !ReferenceEquals(sender, _sideBoard)) return;
        CloseAssistantForSideBoard();
        _sideBoard.SideBoardVisibilityChanged -= OnSideBoardVisibilityChanged;
        _sideBoard.SideBoardPageChanged -= NotifySideBoardPageChanged;
        _sideBoard.Closed -= OnSideBoardClosed;
        _sideBoard = null;
    }

    /// <summary>主展板关闭时关闭子板窗口；BoardService 仍保留，重新打开时内容恢复。</summary>
    internal void CloseSideBoardWithBoard() => CloseSideBoard();

    /// <summary>应用退出时释放子板窗口与事件订阅。</summary>
    internal void CloseSideBoardForExit() => CloseSideBoard();

    internal void EnsureSideBoardOnScreen() => _sideBoard?.EnsureOnScreen();

    private void CloseSideBoard()
    {
        if (_sideBoard is null) return;
        var window = _sideBoard;
        CloseAssistantForSideBoard();
        _sideBoard = null;
        window.SideBoardVisibilityChanged -= OnSideBoardVisibilityChanged;
        window.SideBoardPageChanged -= NotifySideBoardPageChanged;
        window.Closed -= OnSideBoardClosed;
        window.Close();
    }

    /// <summary>子板收回：属于子板的 AI 浮窗同步隐藏，保留会话与流不销毁。</summary>
    private void HideAssistantForSideBoard()
    {
        if (_aiAssistant is null)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                "[SideBoard] 隐藏 AI 浮窗跳过：_aiAssistant 为 null",
                Core.Diagnostics.LogType.Info);
            return;
        }
        if (!AssistantBelongsToSideBoard)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[SideBoard] 隐藏 AI 浮窗跳过：不属于子板（fromSideBoard={_aiAssistantFromSideBoard}，"
                + $"target={AssistantTargetPolicy.Describe(_aiTarget)}）",
                Core.Diagnostics.LogType.Info);
            return;
        }
        _aiHiddenWithSideBoard = true;
        _aiAssistant.AppWindow.Hide();
        bool visibleAfter = _aiAssistant.AppWindow.IsVisible;
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[SideBoard] AppWindow.Hide 后：isVisible={visibleAfter}，hwnd=0x{WindowChrome.GetHwnd(_aiAssistant).ToInt64():X}",
            Core.Diagnostics.LogType.Info);
        if (visibleAfter)
        {
            WindowChrome.HideWindow(_aiAssistant);
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[SideBoard] Win32 兜底后：isVisible={_aiAssistant.AppWindow.IsVisible}",
                Core.Diagnostics.LogType.Info);
        }
    }

    /// <summary>子板再次拉出：恢复属于它的 AI 浮窗，不重建会话。</summary>
    private void ShowAssistantForSideBoard()
    {
        if (_aiAssistant is null || !_aiHiddenWithSideBoard || !AssistantBelongsToSideBoard || _sideBoard is null)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[SideBoard] 恢复 AI 浮窗跳过：assistant={_aiAssistant is not null}，hiddenFlag={_aiHiddenWithSideBoard}，"
                + $"belongs={AssistantBelongsToSideBoard}，sideBoard={_sideBoard is not null}",
                Core.Diagnostics.LogType.Info);
            return;
        }
        _aiHiddenWithSideBoard = false;
        _aiAssistant.AppWindow.Show(activateWindow: false);
        WindowChrome.EnsureAbove(_aiAssistant, WindowChrome.GetHwnd(_sideBoard));
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"[SideBoard] 恢复 AI 浮窗后：isVisible={_aiAssistant.AppWindow.IsVisible}",
            Core.Diagnostics.LogType.Info);
    }

    /// <summary>子板关闭/主展板关闭/退出：关闭属于子板的 AI 浮窗并清理流与会话。</summary>
    private void CloseAssistantForSideBoard()
    {
        if (_aiAssistant is null || !AssistantBelongsToSideBoard) return;
        var window = _aiAssistant;
        _aiHiddenWithSideBoard = false;
        _aiTarget = null;
        _aiAssistantFromSideBoard = false;
        window.Close(); // 触发 OnAiWindowClosed：停流、清会话、置空 _aiAssistant
    }
}