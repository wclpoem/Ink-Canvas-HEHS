using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

/// <summary>
/// Single source for the bundled icon font family (Fluent System Icons, MIT).
/// The style toggle (Settings.json <c>appearance.isIconRegularStyle</c>) switches between the
/// Filled and Regular faces of the SAME MIT font set, keeping icons identical across
/// Windows 10/11 and the HarmonyOS port. Both faces are always deployed; reads are cheap
/// and cached. The bundled Regular cmap uses the same semantic IDs as Filled.
/// </summary>
public static class IconFontStyle
{
    private const string FilledFace = "ms-appx:///Assets/FluentSystemIcons.ttf#FluentSystemIcons-Filled";
    private const string RegularFace = "ms-appx:///Assets/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    private static FontFamily _filled;
    private static FontFamily _regular;
    private static Func<bool> _regularRequested;
    private static bool RegularRequested => _regularRequested?.Invoke() ?? true;

    /// <summary>Injects the settings probe (avoids a hard dependency on Features from Controls).</summary>
    public static void BindStyleSource(Func<bool> isRegularRequested)
    {
        _regularRequested = isRegularRequested;
    }

    /// <summary>The cached font family for the current style.</summary>
    public static FontFamily Font => RegularRequested
        ? _regular ??= new FontFamily(RegularFace)
        : _filled ??= new FontFamily(FilledFace);

    /// <summary>Family string for direct use where a <see cref="FontFamily"/> object isn't suitable.</summary>
    public static string Family => RegularRequested ? RegularFace : FilledFace;

    /// <summary>Apply a style change to already-open windows and panels.</summary>
    public static void Refresh(DependencyObject root)
    {
        if (root is null) return;
        if (root is FontIcon icon && IsBundled(icon.FontFamily)) icon.FontFamily = Font;
        if (root is TextBlock text && IsBundled(text.FontFamily)) text.FontFamily = Font;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++) Refresh(VisualTreeHelper.GetChild(root, index));
    }

    private static bool IsBundled(FontFamily font) => font?.Source is FilledFace or RegularFace;
}
