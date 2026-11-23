using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using InkCanvas.Controls.Controls;

namespace InkCanvas.App.Views;

/// <summary>
/// WPF YesOrNoNotificationWindow parity: 450x160 NoResize topmost notification with a wrapped
/// message, green 是 / default 否 (100 wide each). Yes/No invoke their action then close; the
/// caption X closes invoking NEITHER. Own window + XamlRoot, so PPT notices work even while
/// the floating bar is folded.
/// </summary>
public sealed class YesOrNoWindow : Window
{
    private static FontFamily IconFont => IconFontStyle.Font;
    private readonly Action _yesAction;
    private readonly Action _noAction;
    private bool _entranceShown;
    private readonly DispatcherQueueTimer _keepAbove;

    public YesOrNoWindow(string text, Action yesAction = null, Action noAction = null, int theme = 0)
    {
        _yesAction = yesAction;
        _noAction = noAction;
        Title = "演示文档设置 - Ink Canvas Artistry";

        // FloatBarBackground-like surface: the floating bar paints white (light) / #2B2C2F (dark).
        Brush surface = PopupBrushes.IsDark(theme)
            ? new SolidColorBrush(Color.FromArgb(255, 43, 44, 47))
            : new SolidColorBrush(Microsoft.UI.Colors.White);
        Brush foreground = PopupBrushes.Foreground(theme);

        var icon = new FontIcon
        {
            Glyph = "",
            FontFamily = IconFont,
            FontSize = 20,
            Foreground = foreground,
        };
        var iconBox = new Viewbox
        {
            Height = 30,
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = icon,
        };
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(60, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground,
        };

        var yes = new Button { Content = "是", Width = 100, Margin = new Thickness(0) };
        // WPF overrides the button theme brushes: normal #15803d, hover #166534, pressed #306534.
        yes.Resources["ButtonBackground"] = PopupBrushes.YesGreen();
        yes.Resources["ButtonBackgroundPointerOver"] = PopupBrushes.YesGreenHover();
        yes.Resources["ButtonBackgroundPressed"] = PopupBrushes.YesGreenPressed();
        yes.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        yes.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        yes.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.White);
        yes.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        yes.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        yes.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        yes.Click += (_, _) => { _yesAction?.Invoke(); Close(); };

        var no = new Button { Content = "否", Width = 100, Margin = new Thickness(0) };
        no.Click += (_, _) => { _noAction?.Invoke(); Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { yes, no },
        };

        var inner = new Grid { Margin = new Thickness(10) };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        Grid.SetRow(iconBox, 0);
        Grid.SetRow(label, 0);
        Grid.SetRow(buttons, 1);
        inner.Children.Add(iconBox);
        inner.Children.Add(label);
        inner.Children.Add(buttons);

        var surfaceGrid = new Grid { Background = surface };
        surfaceGrid.Children.Add(inner);

        // NoResize → close-only caption; X closes without invoking either action.
        Content = surfaceGrid;
        WindowChrome.MakeStandard(this, 500, 220, resizable: false);
        WindowChrome.ApplyTheme(this, theme);

        if (Content is Grid root)
        {
            root.Loaded += (_, _) =>
            {
                if (_entranceShown) return;
                _entranceShown = true;
                Motion.PlayEntrance(surfaceGrid, 10f);
            };
        }

        // Transient window outside ShellController.RaiseChromeAboveOverlay: keep ourselves
        // above the ink overlay while open (overlay input raises it over topmost chrome).
        _keepAbove = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _keepAbove.Interval = TimeSpan.FromMilliseconds(900);
        _keepAbove.Tick += (_, _) => WindowChrome.EnsureAbove(this);
        _keepAbove.Start();
        Closed += (_, _) => _keepAbove.Stop();
    }

    /// <summary>
    /// Modal-style yes/no ask on the owner's dispatcher; true only for 是. Caption X or 否
    /// resolve false. Works with any owner window (including a folded floating bar) because
    /// the dialog is its own topmost window with its own XamlRoot.
    /// </summary>
    public static Task<bool> AskAsync(Window owner, string text)
        => AskAsync(owner, text, null);

    public static Task<bool> AskAsync(Window owner, string text, string title)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue queue = owner?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            tcs.TrySetResult(false);
            return tcs.Task;
        }
        bool enqueued = queue.TryEnqueue(() =>
        {
            int theme = (owner?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark ? 2 : 1;
            var window = new YesOrNoWindow(text,
                yesAction: () => tcs.TrySetResult(true),
                noAction: () => tcs.TrySetResult(false),
                theme: theme);
            if (!string.IsNullOrWhiteSpace(title)) window.Title = title;
            window.Closed += (_, _) => tcs.TrySetResult(false);
            window.Activate();
            WindowChrome.EnsureAbove(window);
        });
        if (!enqueued) tcs.TrySetResult(false);
        return tcs.Task;
    }
}
