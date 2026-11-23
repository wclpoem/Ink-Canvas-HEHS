using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Compact title and actions shared by floating tool panels and canvas flyouts.</summary>
public sealed class PopupHeader : Grid
{
    private readonly TextBlock _title;
    private readonly ToggleButton _pin;
    private readonly Button _close;
    public event Action PinChanged;
    public event Action CloseRequested;

    public PopupHeader(string title, int theme, bool canPin = true)
    {
        MinHeight = 40;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Padding = new Thickness(12, 2, 4, 2);
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title = new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        _pin = new ToggleButton { Width = 36, Height = 36, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), Visibility = canPin ? Visibility.Visible : Visibility.Collapsed };
        _close = new Button { Width = 36, Height = 36, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), Content = Icon("\uF36A") };
        AutomationProperties.SetName(_pin, "固定面板");
        AutomationProperties.SetName(_close, "关闭面板");
        ToolTipService.SetToolTip(_close, "关闭");
        _pin.Checked += (_, _) => OnPinChanged();
        _pin.Unchecked += (_, _) => OnPinChanged();
        _close.Click += (_, _) => CloseRequested?.Invoke();
        SetColumn(_pin, 1); SetColumn(_close, 2);
        Children.Add(_title); Children.Add(_pin); Children.Add(_close);
        ApplyTheme(theme);
        RefreshPin();
    }

    public bool IsPinned { get => _pin.IsChecked == true; set => _pin.IsChecked = value; }

    public void ApplyTheme(int theme)
    {
        RequestedTheme = theme == 2 ? ElementTheme.Dark : ElementTheme.Light;
        _title.Foreground = _pin.Foreground = _close.Foreground = PopupBrushes.Foreground(theme);
        _pin.Background = _close.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void OnPinChanged() { RefreshPin(); PinChanged?.Invoke(); }
    private void RefreshPin()
    {
        _pin.Content = Icon(IsPinned ? "\uF60E" : "\uF60C");
        ToolTipService.SetToolTip(_pin, IsPinned ? "取消固定，点击外部自动收起" : "固定面板");
        AutomationProperties.SetItemStatus(_pin, IsPinned ? "已固定" : "自动收起");
    }
    private static FontIcon Icon(string glyph) => new() { Glyph = glyph, FontSize = 14,
        FontFamily = IconFontStyle.Font };
}
