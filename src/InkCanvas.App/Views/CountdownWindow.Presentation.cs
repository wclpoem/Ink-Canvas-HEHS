using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace InkCanvas.App.Views;

public sealed partial class CountdownWindow
{
    private enum TimerView { Normal, Compact, FullScreen }
    private TimerView _mode;
    private MinSizeHook _minSize;
    private OverlappedPresenter _normalPresenter;
    private RectInt32 _normalBounds;
    private bool _normalMaximized;
    internal bool IsCompact => _mode == TimerView.Compact;
    internal bool IsFullScreen => _mode == TimerView.FullScreen;

    internal void ToggleCompact() => ChangeView(_mode == TimerView.Compact ? TimerView.Normal : TimerView.Compact);
    internal void ToggleFullScreen() => ChangeView(_mode == TimerView.FullScreen ? TimerView.Normal : TimerView.FullScreen);

    private void ChangeView(TimerView next)
    {
        if (_disposed || _mode == next) return;
        _durationFlyout.Hide();
        if (_mode == TimerView.Normal && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            _normalPresenter = presenter;
            _normalMaximized = presenter.State == OverlappedPresenterState.Maximized;
            if (_normalMaximized) presenter.Restore();
            _normalBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
        _minSize?.Dispose(); _minSize = null;
        if (next == TimerView.Normal)
        {
            AppWindow.SetPresenter(_normalPresenter);
            ExtendsContentIntoTitleBar = true; SetTitleBar(_frame.Header);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            _minSize = new MinSizeHook(this, 480, 600);
            var work = WindowChrome.GetWorkArea(this);
            int width = Math.Min(_normalBounds.Width, work.Width), height = Math.Min(_normalBounds.Height, work.Height);
            WindowChrome.Place(this, Math.Clamp(_normalBounds.X, work.X, work.X + work.Width - width),
                Math.Clamp(_normalBounds.Y, work.Y, work.Y + work.Height - height), width, height);
            if (_normalMaximized) _normalPresenter.Maximize();
        }
        else if (next == TimerView.Compact)
        {
            ExtendsContentIntoTitleBar = false;
            SetTitleBar(null);
            AppWindow.TitleBar.ResetToDefault();
            var compact = CompactOverlayPresenter.Create(); compact.InitialSize = CompactOverlaySize.Medium;
            AppWindow.SetPresenter(compact);
        }
        else AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _mode = next;
        RefreshPresentation();
    }

    private void PresenterChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_disposed) return;
        SyncRefreshTimer();
        if (args.DidPresenterChange) DispatcherQueue.TryEnqueue(() => { if (!_disposed) RefreshPresentation(); });
    }

    private void RefreshPresentation()
    {
        _frame.Header.Visibility = _mode == TimerView.Normal ? Visibility.Visible : Visibility.Collapsed;
        _expanded.Visibility = _mode == TimerView.Compact ? Visibility.Collapsed : Visibility.Visible;
        _compact.Visibility = _mode == TimerView.Compact ? Visibility.Visible : Visibility.Collapsed;
        _fullScreenExit.Visibility = _mode == TimerView.FullScreen ? Visibility.Visible : Visibility.Collapsed;
        _hero.MaxWidth = _hero.MaxHeight = _mode == TimerView.FullScreen ? 960 : 520;
        _expanded.Margin = _mode == TimerView.FullScreen ? new Thickness(40, 72, 40, 32) : new Thickness(28, 16, 28, 24);
        PlaceTimerModes();
        RefreshTime();
    }

    private void ConfigureOption(Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button, string name, string label, string glyph)
    {
        button.MinWidth = button.MinHeight = 44; button.Padding = new Thickness(12, 8, 12, 8); button.CornerRadius = new CornerRadius(10);
        var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }; _optionLabels.Add(text);
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(glyph), text } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled) return;
        if (args.Key == VirtualKey.Escape)
        {
            if (_editorOpen) _durationFlyout.Hide();
            else if (_mode != TimerView.Normal) ChangeView(TimerView.Normal);
            else return;
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.F11) { ToggleFullScreen(); args.Handled = true; }
        else if (args.Key == VirtualKey.Space && !KeyboardShortcutScope.IsTextInput(args.OriginalSource as DependencyObject)
            && !KeyboardShortcutScope.HasTextInputFocus(_root)
            && FocusManager.GetFocusedElement(_root.XamlRoot) is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        { StartPause(); args.Handled = true; }
    }
}
