using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Color _pageEdgeColor;

    private void ConfigureAppearance()
    {
        _root.RequestedTheme = _host.Settings.Appearance.Theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        _root.ActualThemeChanged += AppearanceThemeChanged;
        _uiSettings.AdvancedEffectsEnabledChanged += TransparencyChanged;
        _uiSettings.ColorValuesChanged += SystemColorsChanged;
        ApplyAppearance();
    }

    private void AppearanceThemeChanged(FrameworkElement sender, object args) => ApplyAppearance();
    private void TransparencyChanged(UISettings sender, object args) => QueueAppearance();
    private void SystemColorsChanged(UISettings sender, object args) => QueueAppearance();
    private void QueueAppearance() => DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplyAppearance(); });

    private void ApplyAppearance()
    {
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        bool highContrast = _accessibility.HighContrast;
        bool material = !highContrast && SystemBackdrop is MicaBackdrop;
        var color = highContrast ? _uiSettings.GetColorValue(UIColorType.Background)
            : dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = highContrast ? _uiSettings.GetColorValue(UIColorType.Foreground)
            : dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (Content is WindowFrame frame)
        {
            frame.RequestedTheme = _root.RequestedTheme;
            frame.Background = new SolidColorBrush(material ? Microsoft.UI.Colors.Transparent : color);
            frame.Header.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        _commandHost.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var surface = material ? dark ? Color.FromArgb(76, 58, 58, 58) : Color.FromArgb(128, 255, 255, 255) : color;
        _viewport.Background = new SolidColorBrush(surface);
        _sidebar.Background = new SolidColorBrush(_split.DisplayMode == Microsoft.UI.Xaml.Controls.SplitViewDisplayMode.Overlay
            ? color : Microsoft.UI.Colors.Transparent);
        _split.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _split.PaneBackground = _sidebar.Background;
        _status.Foreground = new SolidColorBrush(foreground);
        _documentName.Foreground = _saveState.Foreground = new SolidColorBrush(foreground);
        _saveBadge.Background = new SolidColorBrush(highContrast ? color : Color.FromArgb(20, foreground.R, foreground.G, foreground.B));
        _floatingSearch.Background = new SolidColorBrush(color);
        _floatingSearch.BorderBrush = new SolidColorBrush(highContrast ? foreground : dark ? Color.FromArgb(255, 72, 72, 72) : Color.FromArgb(255, 216, 216, 216));
        _thumbnailBorder.Color = highContrast ? foreground : dark ? Color.FromArgb(255, 96, 96, 96) : Color.FromArgb(255, 206, 206, 206);
        _thumbnailAccent.Color = highContrast ? foreground
            : _uiSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.Accent);
        _zoom.Foreground = _pageCount.Foreground = new SolidColorBrush(foreground);
        _pageNumber.Background = new SolidColorBrush(highContrast ? color : dark ? Color.FromArgb(255, 32, 32, 32) : Microsoft.UI.Colors.White);
        _pageNumber.Foreground = new SolidColorBrush(foreground);
        foreach (var button in _commandButtons)
        {
            button.ContentForeground = new SolidColorBrush(foreground);
            button.SelectionBackground = highContrast ? new SolidColorBrush(color) : PopupBrushes.FloatBarSelected();
            button.HoverBackground = new SolidColorBrush(Color.FromArgb(24, foreground.R, foreground.G, foreground.B));
            button.PressedBackground = new SolidColorBrush(Color.FromArgb(40, foreground.R, foreground.G, foreground.B));
            button.ApplyHoverResources(button.ContentForeground);
            button.RefreshVisual();
        }
        var thumb = new SolidColorBrush(highContrast ? foreground : dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 112, 112, 112));
        foreach (var scrollbar in new[] { _scrollbar, _horizontalScrollbar })
        {
            scrollbar.Foreground = thumb;
            scrollbar.Resources["ScrollBarThumbFill"] = thumb;
            scrollbar.Resources["ScrollBarThumbFillPointerOver"] = new SolidColorBrush(foreground);
            scrollbar.Resources["ScrollBarThumbFillPressed"] = new SolidColorBrush(foreground);
        }
        _pageEdgeColor = highContrast ? foreground : dark ? Color.FromArgb(255, 104, 104, 104) : Color.FromArgb(255, 196, 196, 196);
        _navigationPictureDirty = true;
        _selection.ApplyTheme(dark ? 2 : 1);
        _selectionBorder.Background = highContrast ? new SolidColorBrush(color) : PopupBrushes.Surface(dark ? 2 : 1);
        _paper.Invalidate();
    }

    private void ReleaseAppearance()
    {
        _root.ActualThemeChanged -= AppearanceThemeChanged;
        _uiSettings.AdvancedEffectsEnabledChanged -= TransparencyChanged;
        _uiSettings.ColorValuesChanged -= SystemColorsChanged;
        SystemBackdrop = null;
    }
}
