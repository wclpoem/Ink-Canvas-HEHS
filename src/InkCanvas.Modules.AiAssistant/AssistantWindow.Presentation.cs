using System.Runtime.InteropServices;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace InkCanvas.Modules.AiAssistant;

public sealed partial class AssistantWindow
{
    private Button _pinButton;
    private WindowFrame _frame;
    private Window _edgeHandle;
    private bool _collapsed, _pinned = true, _dockedRight = true, _positioned;
    private bool _sizeConstraintQueued;
    private RectInt32 _expandedBounds;
    private double Dpi => Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d);

    public void UseNativeFrame()
    {
        _frame = WindowFrame.Attach(this, _root).UseAcrylic();
        _frame.Header.IconSource = null;
        Activated += (_, args) =>
        {
            if (_positioned || args.WindowActivationState == WindowActivationState.Deactivated) return;
            _positioned = true;
            DispatcherQueue.TryEnqueue(() => { if (!_closed) DockPanel(true); });
        };
        AppWindow.Changed += (_, args) =>
        {
            if (_closed || _collapsed || !args.DidSizeChange || _sizeConstraintQueued) return;
            _sizeConstraintQueued = true;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                _sizeConstraintQueued = false;
                if (_closed || _collapsed || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
                // Read the current size when applying the constraint. Resize events can
                // otherwise enqueue stale sizes that undo a later drag or maximize.
                var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                int width = Math.Min(work.Width, Math.Max((int)(360 * Dpi), AppWindow.Size.Width));
                int height = Math.Min(work.Height, Math.Max((int)(480 * Dpi), AppWindow.Size.Height));
                if (width != AppWindow.Size.Width || height != AppWindow.Size.Height) AppWindow.Resize(new(width, height));
            })) _sizeConstraintQueued = false;
        };
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _pinned;
        _pinButton.Opacity = _pinned ? 1 : .55;
        AutomationProperties.SetName(_pinButton, _pinned ? "取消窗口置顶" : "窗口置顶");
        ToolTipService.SetToolTip(_pinButton, _pinned ? "取消窗口置顶" : "窗口置顶");
    }

    public void DockPanel(bool right)
    {
        _dockedRight = right;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int margin = (int)(12 * Dpi);
        margin = Math.Min(margin, Math.Min(work.Width, work.Height) / 20);
        int width = Math.Min(work.Width - margin * 2, (int)(380 * Dpi)), height = work.Height - margin * 2;
        AppWindow.MoveAndResize(new(right ? work.X + work.Width - width - margin : work.X + margin,
            work.Y + Math.Max(0, (work.Height - height) / 2), width, height));
    }

    public void CollapseToEdge()
    {
        if (_collapsed || _closed) return;
        _expandedBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _collapsed = true;
        var handle = new Window { Title = "展开 AI 助手", SystemBackdrop = new DesktopAcrylicBackdrop() };
        _edgeHandle = handle;
        var button = new TouchIconButton { Width = 44, Height = 64, Content = AiGlyph.CreateFramed(24, 24),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, "展开 AI 助手"); ToolTipService.SetToolTip(button, "展开 AI 助手");
        button.Click += (_, _) => ShowPanel();
        handle.Content = new Grid { RequestedTheme = _frame.RequestedTheme, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { button } };
        var presenter = (OverlappedPresenter)handle.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = _pinned; presenter.IsResizable = false;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = (int)(52 * Dpi), height = (int)(80 * Dpi);
        handle.AppWindow.MoveAndResize(new(_dockedRight ? work.X + work.Width - width : work.X,
            Math.Clamp(_expandedBounds.Y + _expandedBounds.Height / 2 - height / 2, work.Y, work.Y + work.Height - height), width, height));
        handle.Activate(); AppWindow.Hide();
    }

    public void ShowPanel()
    {
        if (_closed) return;
        if (_collapsed)
        {
            _collapsed = false; CloseEdgeHandle();
            var work = DisplayArea.GetFromRect(_expandedBounds, DisplayAreaFallback.Nearest).WorkArea;
            int width = Math.Min(_expandedBounds.Width, work.Width), height = Math.Min(_expandedBounds.Height, work.Height);
            AppWindow.MoveAndResize(new(Math.Clamp(_expandedBounds.X, work.X, work.X + work.Width - width),
                Math.Clamp(_expandedBounds.Y, work.Y, work.Y + work.Height - height), width, height));
        }
        Activate();
        foreach (var turn in _turns) turn.Refresh();
        Motion.PlayEntrance(_root, 0);
    }

    private void CloseEdgeHandle()
    { var handle = _edgeHandle; _edgeHandle = null; handle?.Close(); }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
