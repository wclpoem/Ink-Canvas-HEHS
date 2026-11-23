using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared media insertion menu for the annotation and board toolbars.</summary>
public sealed class InsertFlyout : Flyout
{
    public InsertFlyout(int theme, Action image, Action video, Action pdf = null)
    {
        Placement = FlyoutPlacementMode.Top;
        var title = new PopupHeader("插入", theme, canPin: false);
        title.CloseRequested += Hide;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        Add(row, Glyphs.InsertMedia, "插入图片", image, theme);
        Add(row, Glyphs.Video, "插入视频", video, theme);
        Add(row, Glyphs.Pdf, "插入 PDF", pdf, theme);
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(row);
        Content = new Border
        {
            RequestedTheme = theme == 2 ? ElementTheme.Dark : ElementTheme.Light,
            Background = PopupBrushes.FloatBarBackground(theme), BorderBrush = PopupBrushes.ToolPanelBorder(theme),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel,
        };
        FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter));
        FlyoutPresenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        FlyoutPresenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        FlyoutPresenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        FlyoutPresenterStyle.Setters.Add(new Setter(Control.MinWidthProperty, 0d));
    }

    private void Add(StackPanel row, string glyph, string label, Action action, int theme)
    {
        var button = new TouchIconButton
        {
            Glyph = glyph, Label = label, Width = 84, Height = 76, IconSize = 28,
            IconAreaHeight = 36, LabelFontSize = 13, CornerRadius = new CornerRadius(6),
            IsEnabled = action is not null, ContentForeground = PopupBrushes.FloatBarForeground(theme),
            HoverBackground = PopupBrushes.FloatBarButtonHover(theme), PressedBackground = PopupBrushes.FloatBarButtonPressed(theme),
        };
        ToolTipService.SetToolTip(button, action is null ? "启用 PDF 插件后可用" : label);
        button.Click += (_, _) => { Hide(); action?.Invoke(); };
        row.Children.Add(button);
    }
}
