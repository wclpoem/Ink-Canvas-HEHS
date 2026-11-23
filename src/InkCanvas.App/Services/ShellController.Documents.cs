using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private IDocumentWorkspace _documentWorkspace;
    /// <summary>当前文档页的统一上下文页身份；Attach 时生成，文档翻页（ControllerChanged）时重新生成。</summary>
    private Guid _documentPageId;
    private readonly List<Window> _documentWindows = new();
    private bool _documentFocused;
    private bool DocumentActive => _documentWorkspace is not null && _documentFocused && _board is null;
    private nint _documentForegroundHook;
    private ForegroundCallback _documentForegroundCallback;
    internal IDocumentWorkspaceHost WorkspaceHost => new DocumentHost(this);
    private void ApplyDocumentSettings() => _documentWorkspace?.ApplySettings();
    private void BindToolbar(InkController controller)
    {
        if (_bar is null) return;
        _bar.Bar.GestureSettingsOverride = DocumentActive ? _documentWorkspace.Gestures : null;
        _bar.Bar.SupportsViewRotation = !DocumentActive || _documentWorkspace.SupportsViewRotation;
        _bar.Bar.Controller = controller;
        _bar.Bar.RefreshSettingsVisuals();
        CanvasToolTargetChanged?.Invoke();
        QueueDesktopMedia();
    }

    public async Task OpenPdfAsync(string path = null)
    {
        var service = Services.Modules.GetExport<IPdfWorkspaceService>();
        if (service is null) { await NotifyAsync("PDF 模块", "请将 PDF 模块包解压到程序的 Modules 目录，重新启动后使用。"); return; }
        if (_board is not null && !await _board.CloseWithArchiveAsync()) return;
        try { await service.OpenAsync(path); }
        catch (Exception ex) { await NotifyAsync("打开 PDF 失败", ex.GetBaseException().Message); }
    }

    private void AttachDocument(IDocumentWorkspace workspace)
    {
        _imageImportCancellation?.Cancel();
        _videoImportCancellation?.Cancel();
        if (_board is not null) _board.Close();
        _replay?.Close(); _palette?.Close(); _shapePicker?.Close(); _bar?.ClosePanels();
        _selectionToolbar?.Hide();
        _documentWorkspace = workspace;
        _documentPageId = Guid.NewGuid();
        _documentFocused = true;
        _lastSlideShowState = false;
        _bar?.Bar.SetPptModeVisible(false);
        _bar?.SetPresentationMode(false);
        ClosePptNavigationWindows();
        workspace.ControllerChanged += OnDocumentPageChanged;
        workspace.InputBegan += OnBoardInputBegan;
        workspace.Window.Activated += OnDocumentActivated;
        _documentForegroundCallback = (_, _, _, _, _, _, _) => _dispatcher.TryEnqueue(RefreshDocumentTarget);
        _documentForegroundHook = SetWinEventHook(3, 3, 0, _documentForegroundCallback, 0, 0, 0);
        OnDocumentPageChanged();
        Overlay.Hide();
        if (_bar is not null && !_bar.IsFolded) WindowChrome.ShowWithoutActivation(_bar);
        Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
    }
    private void OnDocumentActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            SetDocumentTarget(true);
            if (DocumentActive) RaiseChromeAboveOverlay();
        }
        else _dispatcher.TryEnqueue(RefreshDocumentTarget);
    }

    private void RefreshDocumentTarget()
        => RefreshDocumentTarget(NativeMethods.GetForegroundWindow());

    internal void RefreshDocumentTarget(nint foreground)
    {
        if (_disposed || _exiting || _documentWorkspace is null || _board is not null) return;
        if (!_documentWorkspace.Window.AppWindow.IsVisible
            || _documentWorkspace.Window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        { SetDocumentTarget(false); return; }
        if (foreground == 0) return;
        if (foreground == WindowChrome.GetHwnd(_documentWorkspace.Window)) { SetDocumentTarget(true); return; }
        NativeMethods.GetWindowThreadProcessId(foreground, out uint process);
        // Tool palettes and the nonactivating toolbar keep the current drawing target.
        if (process != Environment.ProcessId) SetDocumentTarget(false);
    }

    private void SetDocumentTarget(bool focused)
    {
        if (_documentWorkspace is null || _documentFocused == focused) return;
        _documentFocused = focused;
        if (_board is not null) return;
        _replay?.Close(); _palette?.Close(); _shapePicker?.Close(); _bar?.ClosePanels();
        _selectionToolbar?.Hide();
        BindBoardController(DocumentActive ? _documentWorkspace.Controller : null);
        BindToolbar(ActiveController);
        if (DocumentActive) Overlay.Hide();
        else Overlay.Show();
        OnControllerToolChanged();
        Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
    }

    private void StopDocumentForegroundTracking()
    {
        if (_documentForegroundHook != 0) UnhookWinEvent(_documentForegroundHook);
        _documentForegroundHook = 0;
        _documentForegroundCallback = null;
    }

    private delegate void ForegroundCallback(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        ForegroundCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);

    private void OnDocumentPageChanged()
    {
        if (!DocumentActive) return;
        // 文档翻页即换页身份：旧页的上下文不再满足写回页校验。
        _documentPageId = Guid.NewGuid();
        _replay?.Close(); _palette?.Close(); _shapePicker?.Close();
        BindBoardController(_documentWorkspace.Controller);
        BindToolbar(_documentWorkspace.Controller);
        OnControllerToolChanged();
    }

    private void DetachDocument(IDocumentWorkspace workspace)
    {
        if (!ReferenceEquals(_documentWorkspace, workspace)) return;
        workspace.ControllerChanged -= OnDocumentPageChanged;
        workspace.InputBegan -= OnBoardInputBegan;
        workspace.Window.Activated -= OnDocumentActivated;
        StopDocumentForegroundTracking();
        _documentWorkspace = null;
        _documentFocused = false;
        _documentPageId = Guid.Empty;
        _replay?.Close(); _palette?.Close(); _shapePicker?.Close();
        BindBoardController(_board?.CurrentController);
        BindToolbar(ActiveController);
        OnControllerToolChanged();
        if (_board is null && !_exiting) Overlay.Show();
        Services.Modules.Invoke<IScreenTrackingService>(tracking => tracking.Refresh());
    }

    private sealed class DocumentHost(ShellController shell) : IDocumentWorkspaceHost
    {
        public IAiDocumentBridge DocumentAi => shell._documentAi;
        public Window Owner => shell.DocumentActive ? shell._documentWorkspace.Window : (Window)shell._board ?? shell._bar;
        public Core.Settings.AppSettings Settings => shell.Services.Settings;
        public Core.Ink.Rect ToolbarScreenBounds => shell.DesktopToolbarBounds;
        public Features.Board.BoardService Board => shell.Services.Board;
        public void ConfigureWindow(Window window, int width = 1120, int height = 820)
        {
            shell._documentWindows.Add(window);
            window.Closed += (_, _) => shell._documentWindows.Remove(window);
            WindowChrome.MakeStandard(window, width, height);
            WindowChrome.ApplyTheme(window, Settings.Appearance.Theme);
            if (Owner is not null) WindowChrome.CenterOnOwner(window, Owner);
            shell.NotifyWindowOpened(window);
        }
        public void ConfigureController(InkController controller) => shell.Services.ConfigureController(controller);
        public void Attach(IDocumentWorkspace workspace) => shell.AttachDocument(workspace);
        public void Detach(IDocumentWorkspace workspace) => shell.DetachDocument(workspace);
        public void ShowBoard() { if (shell._board is null) shell.ToggleBoard(); }
    }
}
