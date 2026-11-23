using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace InkCanvas.App.Services;

/// <summary>
/// Theme-tinted acrylic with an opaque XAML fallback for unsupported desktop composition paths.
/// </summary>
internal sealed class ChromeBackdrop : SystemBackdrop
{
    private readonly bool _enableAcrylic;
    private bool _connected;
    private DesktopAcrylicController _controller;
    private SystemBackdropConfiguration _configuration;
    private SystemBackdropConfiguration _defaults;
    private int _theme;
    private Windows.UI.Composition.Compositor _fallbackCompositor;
    private Windows.UI.Composition.CompositionColorBrush _fallback;
    private Func<IEnumerable<Border>> _surfaces;
    private readonly Dictionary<Border, (Brush Original, Brush Replacement)> _fallbackSurfaces = new();

    public ChromeBackdrop(bool enableAcrylic = true)
    {
        _enableAcrylic = enableAcrylic;
    }

    public string State => _controller?.State.ToString() ?? (_fallback is null ? "Disconnected" : "Fallback");

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Initializes the configuration and subscribes to OS theme/high-contrast changes.
        base.OnTargetConnected(target, xamlRoot);
        _connected = true;
        _defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _configuration = new SystemBackdropConfiguration();
        RefreshConfiguration();
        if (_enableAcrylic && DesktopAcrylicController.IsSupported())
        {
            try
            {
                _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
                _controller.SetSystemBackdropConfiguration(_configuration);
                ApplyColors();
                if (!_controller.AddSystemBackdropTarget(target)) ReleaseController(target);
                else _controller.StateChanged += OnStateChanged;
            }
            catch (COMException) { ReleaseController(target); }
        }
        if (_controller is null)
        {
            _fallbackCompositor = new Windows.UI.Composition.Compositor();
            _fallback = _fallbackCompositor.CreateColorBrush();
            target.SystemBackdrop = _fallback;
        }
        ApplyColors();
        UpdateSurfaceFallback();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // The base class can notify while connecting or after the target disconnected.
        // Its default configuration is only available for an attached target.
        if (!_connected) return;
        _defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        RefreshConfiguration();
        ApplyColors();
        UpdateSurfaceFallback();
    }

    public void SetTheme(int theme)
    {
        _theme = theme;
        RefreshConfiguration();
        ApplyColors();
        UpdateSurfaceFallback();
    }

    private void ApplyColors()
    {
        if (_configuration is null) return;
        RestoreSurfaceBrushes();
        bool dark = _configuration.Theme == SystemBackdropTheme.Dark;
        var themeColor = dark ? Windows.UI.Color.FromArgb(255, 31, 31, 31) : Microsoft.UI.Colors.White;
        var color = _configuration.IsHighContrast ? _configuration.HighContrastBackgroundColor ?? themeColor : themeColor;
        if (_fallback is not null) _fallback.Color = color;
        if (_controller is null) return;
        _controller.FallbackColor = color;
        // A zero-luminosity material can expose a black foundation on clipped Windows 11
        // tool windows. Give the material its own theme tint before XAML is composited.
        _controller.TintColor = color;
        _controller.TintOpacity = dark ? .5f : .65f;
        _controller.LuminosityOpacity = .9f;
    }

    private void OnStateChanged(ISystemBackdropController sender, object args)
        => DispatcherQueue.TryEnqueue(UpdateSurfaceFallback);

    private void ReleaseController(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller is null) return;
        _controller.StateChanged -= OnStateChanged;
        _controller.RemoveSystemBackdropTarget(target);
        _controller.Dispose();
        _controller = null;
    }

    internal void TrackSurfaces(Func<IEnumerable<Border>> surfaces)
    {
        _surfaces = surfaces;
        UpdateSurfaceFallback();
    }

    internal void UpdateSurfaceFallback()
    {
        if (!_connected || _surfaces is null) return;
        bool opaque = _configuration.IsHighContrast || _controller?.State != SystemBackdropState.Active;
        if (!opaque)
        {
            RestoreSurfaceBrushes();
            return;
        }
        foreach (var surface in _surfaces())
        {
            if (_fallbackSurfaces.TryGetValue(surface, out var saved) && ReferenceEquals(surface.Background, saved.Replacement))
            {
                if (_configuration.IsHighContrast && saved.Replacement is SolidColorBrush highContrast)
                    highContrast.Color = _configuration.HighContrastBackgroundColor ?? Microsoft.UI.Colors.White;
                continue;
            }
            var original = surface.Background;
            Brush replacement;
            if (_configuration.IsHighContrast)
                replacement = new SolidColorBrush(_configuration.HighContrastBackgroundColor ?? Microsoft.UI.Colors.White);
            else if (original is SolidColorBrush solid && solid.Color.A > 0)
            {
                var color = solid.Color;
                color.A = 255;
                replacement = new SolidColorBrush(color);
            }
            else if (original is LinearGradientBrush gradient)
            {
                var copy = new LinearGradientBrush { StartPoint = gradient.StartPoint, EndPoint = gradient.EndPoint };
                foreach (var stop in gradient.GradientStops)
                {
                    var color = stop.Color; color.A = 255;
                    copy.GradientStops.Add(new GradientStop { Color = color, Offset = stop.Offset });
                }
                replacement = copy;
            }
            else replacement = new SolidColorBrush(_configuration.Theme == SystemBackdropTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 31, 31, 31) : Microsoft.UI.Colors.White);
            _fallbackSurfaces[surface] = (original, replacement);
            surface.Background = replacement;
        }
    }

    private void RestoreSurfaceBrushes()
    {
        foreach (var pair in _fallbackSurfaces)
            if (ReferenceEquals(pair.Key.Background, pair.Value.Replacement)) pair.Key.Background = pair.Value.Original;
        _fallbackSurfaces.Clear();
    }

    private void RefreshConfiguration()
    {
        if (_configuration is null || _defaults is null) return;
        _configuration.Theme = _theme switch
        {
            1 => SystemBackdropTheme.Light,
            2 => SystemBackdropTheme.Dark,
            _ => _defaults.Theme == SystemBackdropTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        };
        _configuration.IsHighContrast = _defaults.IsHighContrast;
        _configuration.HighContrastBackgroundColor = _defaults.HighContrastBackgroundColor;
        _configuration.IsInputActive = true;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _connected = false;
        RestoreSurfaceBrushes();
        ReleaseController(target);
        if (_fallback is not null)
        {
            target.SystemBackdrop = null;
            _fallback.Dispose();
            _fallback = null;
            _fallbackCompositor.Dispose();
            _fallbackCompositor = null;
        }
        _configuration = null;
        _defaults = null;
        base.OnTargetDisconnected(target);
    }
}
