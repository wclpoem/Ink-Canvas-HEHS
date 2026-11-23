using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared assistant icon, rendered from the bundled Fluent System Icons font.</summary>
public static class AiGlyph
{
    public const string Text = "AI";
    public const double ButtonLabelSize = 20;
    public const double BadgeLabelSize = 14;
    public const double ToolTileWidth = 24;
    public const double ToolTileHeight = 22;
    public static readonly IReadOnlyList<double> IconBoxSizes = new[] { 16d, 20d, 24d };

    public static FontIcon CreateLabel(double fontSize, Brush foreground = null)
    {
        var icon = new FontIcon
        {
            Glyph = Glyphs.Assistant, FontFamily = IconFontStyle.Font,
            FontSize = double.IsFinite(fontSize) && fontSize > 0 ? fontSize : 20,
            IsTextScaleFactorEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (foreground is not null) icon.Foreground = foreground;
        return icon;
    }

    public static Grid CreateFramed(double boxWidth, double boxHeight, Brush foreground = null) => new()
    {
        Width = boxWidth, Height = boxHeight,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Children = { new Viewbox { Width = boxWidth, Height = boxHeight, Stretch = Stretch.Uniform,
            Child = CreateLabel(Math.Min(boxWidth, boxHeight), foreground) } },
    };
    public static Grid CreateFramed(double boxSize, Brush foreground = null) => CreateFramed(boxSize, boxSize, foreground);
}
