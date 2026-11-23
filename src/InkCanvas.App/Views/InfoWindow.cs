using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Release notes with left navigation and a shared WinUI title bar.</summary>
public sealed class InfoWindow : Window
{
    private readonly List<Action<int>> _themed = new();
    private bool _entranceShown;
    private MinSizeHook _minSize;

    /// <summary>Release notes displayed in a scrollable workspace.</summary>
    public InfoWindow(string title, string markdown, int theme)
    {
        Title = title;

        var body = new MarkdownView(theme) { Padding = new Thickness(24) };
        string version = typeof(InfoWindow).Assembly.GetName().Version?.ToString(3) ?? "3.7.3";
        body.SetMarkdown(markdown, new Uri("https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v" + version + "/"));

        var workspace = new WindowWorkspace { IsTitleBarAutoPaddingEnabled = false };
        workspace.AddPage("版本更新", "\uF379", () => body);
        workspace.SelectPage("版本更新");
        FinishChrome(workspace, theme, 980, 680);
    }

    /// <summary>Guide pages preserve the original keyboard shortcuts.</summary>
    private InfoWindow(int theme)
    {
        Title = "Ink Canvas Artistry 使用指南";

        var rows = new StackPanel();
        // The exact 12 WPF rows plus the local Alt+A AI 助教 row: key, second key, global badge, description.
        AddRow(rows, "Ctrl", "Z", false, " ——  撤销");
        AddRow(rows, "Ctrl", "Y", false, " ——  重做");
        AddRow(rows, "Ctrl", "E", false, " ——  清屏");
        AddRow(rows, "Alt", "C", true, " ——  截屏（Capture）", extraTop: true);
        AddRow(rows, "Alt", "V", true, " ——  显示/隐藏 笑脸右侧工具栏（Visibility）");
        AddRow(rows, "Alt", "D", true, " ——  切换至批注模式 / 显示或关闭调色板（Draw）");
        AddRow(rows, "Alt", "Q", true, " ——  退出批注模式（Quit）");
        AddRow(rows, "Alt", "B", true, " ——  进入/退出 画板（Board）");
        AddRow(rows, "Alt", "N", true, " ——  板中板（侧边子展板）");
        AddRow(rows, "Alt", "A", true, " ——  AI 助教");
        AddRow(rows, "Alt", "S", false, " ——  切换至选择模式（Select）");
        AddRow(rows, "Alt", "E", false, " ——  切换至面积擦/墨迹擦功能（Eraser）");
        AddRow(rows, "Alt", "L", false, " ——  切换至单次直线绘制功能（Line）");
        AddRow(rows, "Shift", "Esc", true, " ——  退出 PPT 放映");
        rows.Children.Add(Paragraph("撤销、重做、清屏与工具快捷键仅在书写或选择状态下生效。鼠标模式和输入框中的按键由当前应用或输入框处理。"));

        var workspace = new WindowWorkspace { IsTitleBarAutoPaddingEnabled = false };
        workspace.AddPage("快捷键", "\uF4C3", () => rows);
        workspace.AddPage("书写与选择", "\uF3DD", () => new StackPanel { Spacing = 16, Children =
        {
            Paragraph("选择画笔或荧光笔即可书写，再次点击工具可调整颜色、粗细、笔尖与笔锋。"),
            Paragraph("使用选择工具拖出选框。选中后可拖动移动，使用边角控制柄缩放，拖动顶部旋转柄旋转。"),
            Paragraph("选择菜单提供克隆、样式、变换与删除。开启克隆模式后，每次拖动会保留原对象并产生副本。"),
            Paragraph("双指可平移和缩放画布。手势选项可控制双指操作与手掌防误触。"),
        } });
        workspace.SelectPage("快捷键");
        FinishChrome(workspace, theme, 1000, 680);
    }

    private void FinishChrome(FrameworkElement content, int theme, int widthDip, int heightDip)
    {
        var root = new Grid { Background = PopupBrushes.WindowSurface(theme) };
        root.Children.Add(content);
        Content = root;
        WindowChrome.MakeStandard(this, widthDip, heightDip);
        WindowFrame.Attach(this, root);
        _minSize = new MinSizeHook(this, 540, 360);
        WindowChrome.ApplyTheme(this, theme);
        ApplyPalette(theme);
        root.ActualThemeChanged += (_, _) =>
        {
            int resolved = root.ActualTheme == ElementTheme.Dark ? 2 : 1;
            root.Background = PopupBrushes.WindowSurface(resolved);
            ApplyPalette(resolved);
        };
        root.Loaded += (_, _) =>
        {
            if (_entranceShown) return;
            _entranceShown = true;
            Motion.PlayEntrance(content, 10f);
        };
        Closed += (_, _) => { _minSize?.Dispose(); _minSize = null; };
    }


    private void AddRow(StackPanel parent, string key, string second, bool global, string description, bool extraTop = false)
    {
        var row = new Grid { MinHeight = 44, ColumnSpacing = 12, Margin = new Thickness(0, extraTop ? 12 : 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shortcut = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        shortcut.Children.Add(Keycap(key));
        shortcut.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center });
        shortcut.Children.Add(Keycap(second));
        row.Children.Add(shortcut);
        if (global)
        {
            var text = new TextBlock { Text = "全局快捷键", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = text };
            _themed.Add(theme => { badge.Background = PopupBrushes.BadgeBackground(theme); text.Foreground = PopupBrushes.BadgeForeground(theme); });
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
        }
        var label = new TextBlock { Text = description.Trim(' ', '—'), FontSize = 14,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        ThemeFg(label);
        Grid.SetColumn(label, 2);
        row.Children.Add(label);
        parent.Children.Add(row);
    }

    private Border Keycap(string key)
    {
        var text = new TextBlock { Text = key, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ThemeFg(text);
        var cap = new Border { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Child = text };
        _themed.Add(theme => cap.BorderBrush = PopupBrushes.Border(theme));
        return cap;
    }

    private TextBlock Paragraph(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        ThemeFg(tb);
        return tb;
    }

    private void ThemeSurface(Border border)
        => _themed.Add(t => { border.Background = PopupBrushes.Surface(t); border.BorderBrush = PopupBrushes.Border(t); });

    private void ThemeFg(TextBlock text) => _themed.Add(t => text.Foreground = PopupBrushes.Foreground(t));

    private void ThemeIcon(FontIcon icon) => _themed.Add(t => icon.Foreground = PopupBrushes.Foreground(t));

    private void ApplyPalette(int theme)
    {
        foreach (var apply in _themed) apply(theme);
    }

    public static InfoWindow OpenGuide(int theme) => new(theme);

    public static InfoWindow OpenChangeLog(int theme)
    {
        string text;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            text = File.Exists(path) ? File.ReadAllText(path) : "未找到更新日志文件。";
        }
        catch
        {
            text = "无法读取更新日志。";
        }
        return new InfoWindow("版本更新 - Ink Canvas Artistry", text, theme);
    }
}
