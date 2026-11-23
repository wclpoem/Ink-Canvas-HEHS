using InkCanvas.Controls.Theming;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

/// <summary>The shared second-level eraser menu for desktop annotation and the board.</summary>
public sealed class EraserPalette : UserControl
{
    private readonly List<(InkEraserMode Mode, TouchIconButton Button)> _modes = new();
    public event Action<InkEraserMode> ModePicked;
    public event Action<int> SizePicked;
    public event Action ClearRequested;

    public EraserPalette(InkController controller, int size, int theme, bool includeClear = false)
    {
        var foreground = PopupBrushes.FloatBarForeground(theme);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (mode, label, badge) in new[]
        {
            (InkEraserMode.Area, "面积擦", Glyphs.EraserOverlayCircle),
            (InkEraserMode.WholeStroke, "墨迹擦", Glyphs.EraserOverlayStroke),
            (InkEraserMode.Lasso, "套索擦", Glyphs.Select),
        })
        {
            var button = new TouchIconButton
            {
                Glyph = Glyphs.Eraser, Label = label, Width = 68, Height = 62, Padding = new Thickness(2),
                IconSize = 24, LabelFontSize = 12, ContentForeground = foreground,
                SelectionBackground = PopupBrushes.FloatBarSelected(),
                HoverBackground = PopupBrushes.FloatBarButtonHover(theme),
                PressedBackground = PopupBrushes.FloatBarButtonPressed(theme),
                IsSelected = controller.EraserMode == mode,
            };
            var icon = new Grid { Width = 30, Height = 30 };
            var eraser = new FontIcon { Glyph = Glyphs.Eraser, FontFamily = new(IconFontStyle.Family), FontSize = 24 };
            var overlay = new FontIcon { Glyph = badge, FontFamily = eraser.FontFamily, FontSize = mode == InkEraserMode.WholeStroke ? 10 : 14,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            icon.Children.Add(eraser); icon.Children.Add(overlay);
            button.SetContent(icon); button.AddFontIcon(eraser); button.AddFontIcon(overlay);
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                foreach (var item in _modes) item.Button.IsSelected = item.Mode == mode;
                ModePicked?.Invoke(mode);
            };
            _modes.Add((mode, button)); modes.Children.Add(button);
        }
        var sizes = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        foreach (string label in OriginalMenuCatalog.EraserSizes) sizes.Items.Add(label);
        sizes.SelectedIndex = Math.Clamp(size, 0, sizes.Items.Count - 1);
        AutomationProperties.SetName(sizes, "面积擦大小");
        sizes.SelectionChanged += (_, _) => { if (sizes.SelectedIndex >= 0) SizePicked?.Invoke(sizes.SelectedIndex); };
        var sizeRow = new Grid { ColumnSpacing = 12 };
        sizeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.Children.Add(new TextBlock { Text = "面积擦大小", Foreground = foreground, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(sizes, 1); sizeRow.Children.Add(sizes);
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(12), Width = 240 };
        panel.Children.Add(modes); panel.Children.Add(sizeRow);
        if (includeClear)
        {
            var clear = new Button { Content = "清空画布…", HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = foreground };
            clear.Click += (_, _) => ClearRequested?.Invoke();
            panel.Children.Add(clear);
        }
        Content = panel;
    }
}
