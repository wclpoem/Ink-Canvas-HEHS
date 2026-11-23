using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared WinUI title bar with native caption buttons and a replaceable content area.</summary>
public sealed class WindowFrame : Grid
{
    private readonly Window _window;
    private FrameworkElement _body;
    private SystemBackdrop _backdrop;
    public TitleBar Header { get; } = new()
    {
        MinHeight = 48,
        IconSource = new BitmapIconSource { UriSource = new Uri("ms-appx:///Assets/InkCanvas.ico"), ShowAsMonochrome = false },
    };

    private WindowFrame(Window window)
    {
        _window = window;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(Header);
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(Header);
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        window.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        window.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        window.Closed += OnClosed;
        RefreshTitle();
    }

    public static WindowFrame Attach(Window window, FrameworkElement body)
    {
        var frame = window.Content as WindowFrame ?? new WindowFrame(window);
        if (frame._body is not null)
        {
            frame._body.ActualThemeChanged -= frame.OnThemeChanged;
            frame.Children.Remove(frame._body);
        }
        // Detach before parenting a window's former root into this frame.
        if (ReferenceEquals(window.Content, body)) window.Content = null;
        frame._body = body;
        SetRow(body, 1);
        frame.Children.Add(body);
        body.ActualThemeChanged += frame.OnThemeChanged;
        frame.OnThemeChanged(body, null);
        window.Content = frame;
        return frame;
    }

    public void RefreshTitle() => Header.Title = _window.Title;

    /// <summary>Use the native window material, including system theme and accessibility fallbacks.</summary>
    public WindowFrame UseMica() => UseBackdrop(_backdrop as MicaBackdrop ?? new MicaBackdrop());

    public WindowFrame UseAcrylic() => UseBackdrop(_backdrop as DesktopAcrylicBackdrop ?? new DesktopAcrylicBackdrop());

    private WindowFrame UseBackdrop(SystemBackdrop backdrop)
    {
        RequestedTheme = _body.RequestedTheme;
        _body.RequestedTheme = ElementTheme.Default;
        _backdrop = backdrop;
        _window.SystemBackdrop = _backdrop;
        OnThemeChanged(_body, null);
        return this;
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        bool dark = sender.ActualTheme == ElementTheme.Dark;
        Header.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Background = new SolidColorBrush(_backdrop is not null ? Microsoft.UI.Colors.Transparent
            : dark ? Windows.UI.Color.FromArgb(255, 31, 31, 31) : Windows.UI.Color.FromArgb(255, 240, 243, 249));
        if (_backdrop is not null) Header.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _window.AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _window.AppWindow.TitleBar.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 156, 156, 156) : Windows.UI.Color.FromArgb(255, 112, 112, 112);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _window.Closed -= OnClosed;
        if (_backdrop is not null)
        {
            if (ReferenceEquals(_window.SystemBackdrop, _backdrop)) _window.SystemBackdrop = null;
            _backdrop = null;
        }
        if (_body is not null) _body.ActualThemeChanged -= OnThemeChanged;
    }
}
