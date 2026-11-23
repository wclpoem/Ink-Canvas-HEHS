using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 统一课堂工具箱浮窗：可拖动、可收起的圆角半透明浮窗，集中提供点名、抽奖、倒计时、
/// 聚光灯、放大镜、画板、AI 助教与例题库入口。点击按钮只转发到 <see cref="IShellActions"/>
/// 或构造时传入的回调。
/// </summary>
public sealed class ToolkitWindow : Window
{
    private Button _aiEntry;
    public void SetAiAvailable(bool available) { if (_aiEntry is not null) _aiEntry.Visibility = available ? Visibility.Visible : Visibility.Collapsed; }
    private const double WindowWidth = 320;
    private const double ExpandedHeight = 434;
    private const double CollapsedHeight = 56;

    private readonly IShellActions _shell;
    private readonly Action _openExampleLibrary;
    private readonly Action _openResourceBundle;
    private readonly Border _body;
    private readonly Button _collapseButton;
    private bool _collapsed;

    public ToolkitWindow(
        IShellActions shell,
        int theme,
        Action openExampleLibrary = null,
        Action openResourceBundle = null)
    {
        _shell = shell;
        _openExampleLibrary = openExampleLibrary;
        _openResourceBundle = openResourceBundle;
        Title = "课堂工具";

        var title = new TextBlock
        {
            Text = "课堂工具",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _collapseButton = new Button
        {
            Content = "—",
            Width = 32,
            Height = 32,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _collapseButton.Click += (_, _) => ToggleCollapse();

        var close = new Button
        {
            Content = "✕",
            Width = 32,
            Height = 32,
            FontSize = 12,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close();

        var header = new Grid { Height = 44, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_collapseButton, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(title);
        header.Children.Add(_collapseButton);
        header.Children.Add(close);
        header.PointerPressed += OnHeaderPointerPressed;

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddTool(grid, 0, 0, Glyphs.People, "点名", () => _shell?.OpenRoster());
        AddTool(grid, 0, 1, Glyphs.Replay, "抽奖", () => _shell?.OpenLottery());
        AddTool(grid, 0, 2, Glyphs.Stopwatch, "倒计时", () => _shell?.OpenTimer());
        AddTool(grid, 1, 0, Glyphs.Spotlight, "聚光灯", () => _shell?.OpenSpotlight());
        AddTool(grid, 1, 1, Glyphs.Add, "放大镜", () => _shell?.OpenMagnifier());
        AddTool(grid, 1, 2, Glyphs.Board, "画板", () => _shell?.ToggleBoard());
        _aiEntry = AddTool(grid, 2, 0, Glyphs.Ai, "AI 助手", () => _shell?.OpenAiAssistant());
        SetAiAvailable(_shell?.AiAssistantAvailable == true);
        AddTool(grid, 2, 1, Glyphs.People, "分组积分", () => _shell?.OpenGroupScore());
        AddTool(grid, 2, 2, Glyphs.Download, "讲义导出", () => _shell?.OpenHandout());
        AddTool(grid, 3, 0, Glyphs.SlideList, "切换场景", () => _shell?.CyclePreset());
        AddTool(grid, 3, 1, Glyphs.Settings, "编辑场景", () => _shell?.OpenPresetEditor());
        AddTool(grid, 3, 2, Glyphs.SlideList, "例题库", () => _openExampleLibrary?.Invoke());
        AddTool(grid, 4, 0, Glyphs.Download, "资源包", () => _openResourceBundle?.Invoke());

        _body = new Border { Child = grid, Padding = new Thickness(0, 0, 0, 8) };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_body, 1);
        root.Children.Add(header);
        root.Children.Add(_body);

        bool light = theme == 1 || (theme != 2 && Application.Current.RequestedTheme == ApplicationTheme.Light);
        var surface = light
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0x20, 0x21, 0x24));

        var outer = new Border
        {
            Background = surface,
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 12, 12),
            Child = root,
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        outer.KeyboardAccelerators.Add(escape);

        Content = outer;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.CenterOnPrimary(this, (int)WindowWidth, (int)ExpandedHeight);
        Activated += (_, _) => Motion.PlayEntrance(outer, 12f);
    }

    private static Button AddTool(Grid grid, int row, int column, string glyph, string label, Action onClick)
    {
        FrameworkElement icon;
        if (glyph == Glyphs.Ai)
        {
            icon = AiGlyph.CreateFramed(AiGlyph.ToolTileWidth, AiGlyph.ToolTileHeight);
        }
        else
        {
            icon = new FontIcon
            {
                FontFamily = IconFontStyle.Font,
                FontSize = 18,
                Glyph = glyph,
            };
        }
        var text = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(text);

        var button = new Button
        {
            Content = content,
            MinHeight = 60,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
        return button;
    }

    private void OnHeaderPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        DependencyObject current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase) return;
            current = VisualTreeHelper.GetParent(current);
        }

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessageW(WindowChrome.GetHwnd(this), NativeMethods.WM_NCLBUTTONDOWN,
            new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        e.Handled = true;
    }

    private void ToggleCollapse()
    {
        SetCollapsed(!_collapsed);
    }

    public bool IsCollapsed => _collapsed;

    public int PositionX => AppWindow.Position.X;

    public int PositionY => AppWindow.Position.Y;

    public void MoveTo(int x, int y)
    {
        try { AppWindow.Move(new Windows.Graphics.PointInt32(x, y)); }
        catch (Exception) { }
    }

    public void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _body.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapseButton.Content = _collapsed ? "▢" : "—";

        double dpi = WindowChrome.GetDpiScale(this);
        if (dpi <= 0) dpi = 1.0;
        var position = AppWindow.Position;
        WindowChrome.Place(this, position.X, position.Y,
            (int)Math.Round(WindowWidth * dpi),
            (int)Math.Round((_collapsed ? CollapsedHeight : ExpandedHeight) * dpi));
    }

    /// <summary>不激活窗口地显示，避免抢走正在书写的白板焦点。</summary>
    public void ShowWithoutActivating()
    {
        NativeMethods.ShowWindow(
            WindowChrome.GetHwnd(this), NativeMethods.SW_SHOWNOACTIVATE);
    }
}
