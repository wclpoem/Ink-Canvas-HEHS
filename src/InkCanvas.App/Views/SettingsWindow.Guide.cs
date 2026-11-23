using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private static readonly string[] GuidePages = { "操作指南", "快捷键", "书写与选择" };

    private void AddGuidePages()
    {
        _workspace.AddSubPage("关于与更新", "操作指南", Glyphs.Help, () => BuildPage("操作指南"));
        _workspace.AddSubPage("操作指南", "快捷键", "\uF4C3", () => BuildPage("快捷键"));
        _workspace.AddSubPage("操作指南", "书写与选择", Glyphs.Pen, () => BuildPage("书写与选择"));
    }

    private void BuildGuide()
    {
        _content.Children.Add(ButtonRow("快捷键", "书写、工具切换、画板与课件放映", () => _workspace.SelectPage("快捷键")));
        _content.Children.Add(ButtonRow("书写与选择", "画笔、选择、克隆与触控手势", () => _workspace.SelectPage("书写与选择")));
    }

    private void BuildShortcutGuide()
    {
        AddShortcut("Ctrl", "Z", false, "撤销");
        AddShortcut("Ctrl", "Y", false, "重做");
        AddShortcut("Ctrl", "E", false, "清屏");
        AddShortcut("Alt", "C", true, "截屏（Capture）");
        AddShortcut("Alt", "V", true, "显示/隐藏 笑脸右侧工具栏（Visibility）");
        AddShortcut("Alt", "D", true, "切换至批注模式 / 显示或关闭调色板（Draw）");
        AddShortcut("Alt", "Q", true, "退出批注模式（Quit）");
        AddShortcut("Alt", "B", true, "进入/退出 画板（Board）");
        AddShortcut("Alt", "S", false, "切换至选择模式（Select）");
        AddShortcut("Alt", "E", false, "切换至面积擦/墨迹擦功能（Eraser）");
        AddShortcut("Alt", "L", false, "切换至单次直线绘制功能（Line）");
        AddShortcut("Shift", "Esc", true, "退出 PPT 放映");
        _content.Children.Add(InfoRow("撤销、重做、清屏与工具快捷键仅在书写或选择状态下生效。鼠标模式和输入框中的按键由当前应用或输入框处理。"));
    }

    private void AddShortcut(string key, string second, bool global, string description)
    {
        var row = new Grid { MinHeight = 44, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        keys.Children.Add(GuideKeycap(key));
        keys.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center });
        keys.Children.Add(GuideKeycap(second));
        row.Children.Add(keys);
        var detail = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        detail.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        if (global)
            detail.Children.Add(new Border
            {
                Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left, Background = PopupBrushes.BadgeBackground(BrushTheme),
                Child = new TextBlock { Text = "全局快捷键", FontSize = 12, Foreground = PopupBrushes.BadgeForeground(BrushTheme) },
            });
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        _content.Children.Add(Searchable(Card(row), description, $"{key} {second} 快捷键"));
    }

    private Border GuideKeycap(string key) => new()
    {
        Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4), BorderBrush = PopupBrushes.Border(BrushTheme),
        Child = new TextBlock { Text = key, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
    };

    private void BuildWritingGuide()
    {
        _content.Children.Add(Searchable(Card(InfoRow("选择画笔或荧光笔即可书写，再次点击工具可调整颜色、粗细、笔尖与笔锋。")), "画笔与荧光笔"));
        _content.Children.Add(Searchable(Card(InfoRow("使用选择工具拖出选框。选中后可拖动移动，使用边角控制柄缩放，拖动顶部旋转柄旋转。")), "选择与变换"));
        _content.Children.Add(Searchable(Card(InfoRow("选择菜单提供克隆、样式、变换与删除。开启克隆模式后，每次拖动会保留原对象并产生副本。")), "选择菜单与克隆"));
        _content.Children.Add(Searchable(Card(InfoRow("双指可平移和缩放画布。手势选项可控制双指操作与手掌防误触。")), "触控手势"));
    }
}
