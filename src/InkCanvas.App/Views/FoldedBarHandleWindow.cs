using InkCanvas.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Controls.Controls;

namespace InkCanvas.App.Views;

/// <summary>WPF-style edge handle shown while the floating toolbar is folded.</summary>
internal sealed class FoldedBarHandleWindow : Window
{
    private readonly FloatingBarWindow _owner;

    public FoldedBarHandleWindow(FloatingBarWindow owner, bool left, int width, int height, int x, int y)
    {
        _owner = owner;
        var icon = new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = "",
            FontFamily = IconFontStyle.Font,
            FontSize = 16,
            // Icon sits on the protruding sliver (the rest of the window is off-screen).
            HorizontalAlignment = left ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = left ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(185, 235, 238, 240)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 80, 95, 100)),
            BorderThickness = new Thickness(1),
            CornerRadius = left ? new CornerRadius(0, 8, 8, 0) : new CornerRadius(8, 0, 0, 8),
            Child = icon,
        };
        // A real Button gives the sliver an UIA Invoke pattern (automation/headless tests) and
        // a screen-reader name; the whole protruding sliver is the click target.
        var hit = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Content = root,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(hit, "展开工具栏");
        // Use the shell action so the manual fold override changes too. Otherwise Poll
        // reapplies the previous manual=true state and immediately hides the restored bar.
        hit.Click += (_, _) => _owner.Bar.Shell?.ToggleFold();
        Content = hit;
        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { root });
        WindowChrome.Place(this, x, y, width, height);
    }

    public void ShowHandle()
    {
        WindowChrome.ShowWithoutActivation(this);
        WindowChrome.EnsureAbove(this);
    }
}
