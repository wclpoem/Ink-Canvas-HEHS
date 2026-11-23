using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// Independent nonactivating pen/highlighter panel with compact title actions and light dismiss.
/// Its position and content follow the floating toolbar's scale and monitor.
/// </summary>
public sealed class PenPaletteWindow : Window
{
    public void ConfigureNibMode(bool visible, bool enabled, Action<bool> changed) => _palette.ConfigureNibMode(visible, enabled, changed);
    private readonly PenPalette _palette;
    private readonly Viewbox _host;
    private double _designW, _designH;
    private double _scalePercent = 100;
    private Func<Windows.Graphics.RectInt32> _getAnchor, _getInteractionRegion;
    private bool _hasBarPosition;
    private IntPtr _ownerHwnd;
    private Action _detachAnchor;
    private XamlRoot _trackedRoot;
    private double _lastDpiScale = 1;
    private bool _closed;
    private bool _live;
    private bool _designRefreshPending;
    public bool IsDetached { get; private set; }
    public bool IsPinned => _palette.Header.IsPinned;
    public bool IsHighlighter { get; }

    public PenPaletteWindow(InkController controller, bool highlighter, int theme, double scalePercent = 100)
    {
        IsHighlighter = highlighter;
        Title = highlighter ? "荧光笔" : "墨迹选项";

        _palette = new PenPalette(highlighter) { Controller = controller };
        _palette.RequestClose = Close;
        _palette.ApplyChrome(theme);
        MeasureDesign();
        _scalePercent = Math.Clamp(scalePercent, 50, 200);
        _host = new Viewbox
        {
            Child = _palette,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = _designW * UserScale,
            Height = _designH * UserScale,
        };

        var scroll = new ScrollViewer
        {
            Content = _host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(scroll, Title);
        Content = scroll;

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        // WPF AllowsTransparency: FloatBarBackground is white@0.75 / black@0.6, so the popup
        // needs the same DesktopAcrylicBackdrop translucency as the bar panels.
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { _palette.CardSurface });
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.PlaceDip(this, 100, 100, _designW * UserScale, _designH * UserScale);
        WindowChrome.EnableDragMove(this, _palette.Header, Detach, onDragCompleted: RefreshDisplayPlacement);
        _palette.RefreshFromController();
        WireDismissal();
        OptionPopupCoordinator.Attach(this, _palette.Header);
        scroll.Loaded += (_, _) => { _live = true; AttachDpiTracking(); };
        _palette.CardSurface.SizeChanged += (_, _) =>
        {
            if (_closed || _designRefreshPending) return;
            _designRefreshPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _designRefreshPending = false;
                if (!_closed) RefreshDesignSize();
            });
        };
    }

    private double UserScale => _scalePercent / 100.0;
    private double PopupDpiScale
    {
        get
        {
            return _getAnchor is not null ? WindowChrome.GetDpiScale(_getAnchor()) : WindowChrome.GetDpiScale(this);
        }
    }

    public Action<double> InkWidthChanged { get => _palette.InkWidthChanged; set => _palette.InkWidthChanged = value; }
    public Action<int> InkStyleChanged { get => _palette.InkStyleChanged; set => _palette.InkStyleChanged = value; }
    public Action<InkCanvas.Core.Ink.PenNib> PenNibChanged { get => _palette.PenNibChanged; set => _palette.PenNibChanged = value; }
    public Action<int> InkAlphaChanged
    {
        get => _palette.InkAlphaChanged;
        set
        {
            _palette.InkAlphaChanged = value;
            // Wiring alpha collapses the "not wired" note, changing the natural height.
            RefreshDesignSize();
        }
    }
    public Action AttributesChanged { get => _palette.AttributesChanged; set => _palette.AttributesChanged = value; }
    public int HighlighterAlpha { get => _palette.HighlighterAlpha; set => _palette.HighlighterAlpha = value; }
    public bool LightColorScheme { get => _palette.LightColorScheme; set => _palette.LightColorScheme = value; }

    internal void Detach()
    {
        IsDetached = true;
        _hasBarPosition = false;
        _getAnchor = null;
        _getInteractionRegion = null;
        _detachAnchor?.Invoke();
        _detachAnchor = null;
        _palette.Header.IsPinned = true;
    }

    public void RefreshFromController() => _palette.RefreshFromController();

    /// <summary>
    /// WPF has no dedicated menu-size setting: palettes live inside the scaled tools Viewbox
    /// (FloatingBarScale in desktop mode, BlackboardScale in board mode). The shell calls this
    /// when the scale slider changes so an open menu resizes live.
    /// </summary>
    public void ApplyScale(double scalePercent)
    {
        _scalePercent = Math.Clamp(scalePercent, 50, 200);
        if (_hasBarPosition) Reanchor();
        else if (_live) ResizeAtCurrentPosition();
        else
        {
            _host.Width = _designW * UserScale;
            _host.Height = _designH * UserScale;
            WindowChrome.PlaceDip(this, 100, 100, _designW * UserScale, _designH * UserScale);
        }
    }

    /// <summary>Shell overlay-input hook: WPF HideSubPanels always hides PenPalette on inking.</summary>
    public void DismissOnOverlayInput()
    {
        if (!_closed && !_palette.Header.IsPinned) Close();
    }

    /// <summary>
    /// Natural design size measured while unrooted (a rooted measure is constrained by the
    /// window). Fallback constants cover the rare case where text measurement is not ready.
    /// </summary>
    private void MeasureDesign()
    {
        if (_palette.CardSurface.XamlRoot is not null && _palette.CardSurface.ActualWidth > 0
            && _palette.CardSurface.ActualHeight > 0)
        {
            _designW = _palette.CardSurface.ActualWidth;
            _designH = _palette.CardSurface.ActualHeight;
            return;
        }
        _palette.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _designW = _palette.DesiredSize.Width;
        _designH = _palette.DesiredSize.Height;
        if (_designW < 50 || _designH < 50)
        {
            _designW = _palette.DesignWidthDip;
            _designH = _palette.DesignHeightDip;
        }
    }

    private void RefreshDesignSize()
    {
        double oldW = _designW, oldH = _designH;
        MeasureDesign();
        if (Math.Abs(oldW - _designW) < 0.5 && Math.Abs(oldH - _designH) < 0.5) return;
        if (_hasBarPosition) Reanchor();
        else if (_live) ResizeAtCurrentPosition();
        else
        {
            _host.Width = _designW * UserScale;
            _host.Height = _designH * UserScale;
            WindowChrome.PlaceDip(this, 100, 100, _designW * UserScale, _designH * UserScale);
        }
    }

    private (int W, int H) PhysicalSize()
    {
        var work = _getAnchor is null ? WindowChrome.GetWorkArea(this) : WindowChrome.GetWorkArea(_getAnchor());
        double dpi = PopupDpiScale;
        int w = Math.Min((int)Math.Ceiling(_designW * UserScale * dpi), work.Width);
        int h = Math.Min((int)Math.Ceiling(_designH * UserScale * dpi), work.Height);
        return (w, h);
    }

    /// <summary>Place at physical px and fit the Viewbox host to the (possibly clamped) window.</summary>
    private void PlaceFitted(int x, int y)
    {
        var (w, h) = PhysicalSize();
        double dpi = PopupDpiScale;
        _host.Width = w / dpi;
        _host.Height = h / dpi;
        WindowChrome.PlaceClient(this, x, y, w, h);
        // Re-assert topmost Z after placing: MoveAndResize can leave the window below the overlay.
        WindowChrome.EnsureAbove(this);
    }

    private void ResizeAtCurrentPosition()
    {
        var pos = WindowChrome.GetClientPosition(this);
        var work = WindowChrome.GetWorkArea(this);
        var (w, h) = PhysicalSize();
        PlaceFitted(Math.Clamp(pos.X, work.X, work.X + work.Width - w), Math.Clamp(pos.Y, work.Y, work.Y + work.Height - h));
    }

    private void AttachDpiTracking()
    {
        _trackedRoot = (Content as FrameworkElement)?.XamlRoot;
        _lastDpiScale = PopupDpiScale;
        if (_trackedRoot is not null) _trackedRoot.Changed += (_, _) =>
        {
            // Re-anchor only on a real DPI change; XamlRoot.Changed also fires for ordinary
            // window moves, and re-placing there would snap a dragged window back to the bar.
            double scale = PopupDpiScale;
            if (Math.Abs(scale - _lastDpiScale) < 0.001) return;
            _lastDpiScale = scale;
            if (_hasBarPosition) Reanchor();
            else ResizeAtCurrentPosition();
        };
    }

    /// <summary>
    /// Anchor over the floating bar and clamp to the current work area. Optionally record the
    /// bar HWND so clicks on the bar itself do not count as "outside" for the dismissal poll
    /// (BarPanelWindow parity); the shell may pass WindowChrome.GetHwnd(barWindow).
    /// </summary>
    public void PlaceAboveBar(Func<Windows.Graphics.RectInt32> getAnchor, IntPtr ownerHwnd = default,
        Func<Windows.Graphics.RectInt32> interactionRegion = null)
    {
        _getInteractionRegion = interactionRegion;
        _getAnchor = getAnchor; _hasBarPosition = true;
        if (ownerHwnd != IntPtr.Zero && _ownerHwnd != ownerHwnd)
        {
            _detachAnchor?.Invoke();
            _ownerHwnd = ownerHwnd;
            _detachAnchor = WindowChrome.TrackAnchor(this, ownerHwnd, () => { if (!_closed) Reanchor(); });
        }
        Reanchor();
    }

    public void RefreshDisplayPlacement()
    {
        if (_hasBarPosition) Reanchor();
        else ResizeAtCurrentPosition();
    }

    private void Reanchor()
    {
        if (IsDetached) return;
        double dpi = PopupDpiScale;
        var (w, h) = PhysicalSize();
        int gap = (int)Math.Ceiling(8 * dpi);
        var position = WindowChrome.GetPopupPosition(_getAnchor(), w, h, gap);
        PlaceFitted(position.X, position.Y);
    }

    private void WireDismissal()
    {
        Content.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; if (!_closed) Close(); }
        };
        TransientWindowDismissal.Attach(this, () => _ownerHwnd, () => !_palette.Header.IsPinned,
            () => _getAnchor?.Invoke() ?? default, () => _getInteractionRegion?.Invoke() ?? default);
        Closed += (_, _) => { _closed = true; _detachAnchor?.Invoke(); };
    }
}

/// <summary>
/// Nonactivating shape panel with pin/close actions, light dismiss and toolbar anchoring.
/// </summary>
public sealed class ShapePickerWindow : Window
{
    private readonly ShapePreview _preview;
    private readonly Viewbox _host;
    private double _designW, _designH;
    private double _scalePercent = 100;
    private Func<Windows.Graphics.RectInt32> _getAnchor, _getInteractionRegion;
    private bool _hasBarPosition;
    private IntPtr _ownerHwnd;
    private Action _detachAnchor;
    private XamlRoot _trackedRoot;
    private double _lastDpiScale = 1;
    private bool _closed;
    private bool _designRefreshPending;
    public bool IsDetached { get; private set; }

    public ShapePickerWindow(InkController controller, Action onPicked, int theme,
        double scalePercent = 100, bool boardMode = false)
    {
        Title = "图形";

        _preview = new ShapePreview(controller, theme, boardMode);
        _preview.Header.CloseRequested += Close;
        _preview.SelectionMade += (_, sticky) =>
        {
            onPicked?.Invoke();
            if (_preview.AutoHide) Close();
        };

        // Natural design size measured while unrooted (a rooted measure is constrained by the
        // window and cannot discover the grid's true size). Fallback constants match the real
        // design: band ~33 + Viewbox-200 body ~197 + border 2 (screen) / band ~35 + 240-wide
        // body ~247 + border 2 (board); the band text is slightly wider than the body.
        _preview.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _designW = _preview.DesiredSize.Width;
        _designH = _preview.DesiredSize.Height;
        if (_designW < 50 || _designH < 50)
        {
            _designW = boardMode ? 254 : 208;
            _designH = 37 + 34 + ShapeMenuCatalog.Categories.Max(category => category.Rows.Length) * 42
                * (boardMode ? 240 : 200) / 230d;
        }

        _scalePercent = Math.Clamp(scalePercent, 50, 200);
        _host = new Viewbox
        {
            Child = _preview,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = _designW * UserScale,
            Height = _designH * UserScale,
        };

        var scroll = new ScrollViewer
        {
            Content = _host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        AutomationProperties.SetName(scroll, "图形");
        Content = scroll;

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        FloatingBarWindow.EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => new[] { (Border)_preview.Content });
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.PlaceDip(this, 100, 100, _designW * UserScale, _designH * UserScale);
        WindowChrome.EnableDragMove(this, _preview.Header, Detach, onDragCompleted: RefreshDisplayPlacement);
        WireDismissal();
        OptionPopupCoordinator.Attach(this, _preview.Header);
        scroll.Loaded += (_, _) => AttachDpiTracking();
        ((Border)_preview.Content).SizeChanged += (_, _) =>
        {
            if (_closed || _designRefreshPending) return;
            _designRefreshPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _designRefreshPending = false;
                if (_closed) return;
                var card = (Border)_preview.Content;
                if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return;
                if (Math.Abs(_designW - card.ActualWidth) < 0.5 && Math.Abs(_designH - card.ActualHeight) < 0.5) return;
                _designW = card.ActualWidth;
                _designH = card.ActualHeight;
                if (_hasBarPosition) Reanchor();
                else ResizeAtCurrentPosition();
            });
        };
    }

    private double UserScale => _scalePercent / 100.0;
    private double PopupDpiScale
    {
        get
        {
            return _getAnchor is not null ? WindowChrome.GetDpiScale(_getAnchor()) : WindowChrome.GetDpiScale(this);
        }
    }

    /// <summary>Pin state: true = auto-hide after a pick (WPF default), false = stay open.</summary>
    public bool AutoHide
    {
        get => _preview.AutoHide;
        set => _preview.AutoHide = value;
    }

    internal void Detach()
    {
        IsDetached = true;
        _hasBarPosition = false;
        _getAnchor = null;
        _getInteractionRegion = null;
        _detachAnchor?.Invoke();
        _detachAnchor = null;
        _preview.Header.IsPinned = true;
    }

    /// <summary>Live-resize with the floating-bar / blackboard scale setting (WPF ApplyScaling).</summary>
    public void ApplyScale(double scalePercent)
    {
        _scalePercent = Math.Clamp(scalePercent, 50, 200);
        if (_hasBarPosition) Reanchor();
        else ResizeAtCurrentPosition();
    }

    /// <summary>
    /// Shell overlay-input hook: HideSubPanels hides BorderDrawShape only while auto-hide is
    /// armed; a pinned picker stays open while the teacher draws shapes.
    /// </summary>
    public void DismissOnOverlayInput()
    {
        if (!_closed && _preview.AutoHide) Close();
    }

    private (int W, int H) PhysicalSize()
    {
        var work = _getAnchor is null ? WindowChrome.GetWorkArea(this) : WindowChrome.GetWorkArea(_getAnchor());
        double dpi = PopupDpiScale;
        int w = Math.Min((int)Math.Ceiling(_designW * UserScale * dpi), work.Width);
        int h = Math.Min((int)Math.Ceiling(_designH * UserScale * dpi), work.Height);
        return (w, h);
    }

    private void PlaceFitted(int x, int y)
    {
        var (w, h) = PhysicalSize();
        double dpi = PopupDpiScale;
        _host.Width = w / dpi;
        _host.Height = h / dpi;
        WindowChrome.PlaceClient(this, x, y, w, h);
        // Re-assert topmost Z after placing: MoveAndResize can leave the window below the overlay.
        WindowChrome.EnsureAbove(this);
    }

    private void ResizeAtCurrentPosition()
    {
        var pos = WindowChrome.GetClientPosition(this);
        var work = WindowChrome.GetWorkArea(this);
        var (w, h) = PhysicalSize();
        PlaceFitted(Math.Clamp(pos.X, work.X, work.X + work.Width - w), Math.Clamp(pos.Y, work.Y, work.Y + work.Height - h));
    }

    private void AttachDpiTracking()
    {
        _trackedRoot = (Content as FrameworkElement)?.XamlRoot;
        _lastDpiScale = PopupDpiScale;
        if (_trackedRoot is not null) _trackedRoot.Changed += (_, _) =>
        {
            // Re-anchor only on a real DPI change (ordinary moves must not snap back).
            double scale = PopupDpiScale;
            if (Math.Abs(scale - _lastDpiScale) < 0.001) return;
            _lastDpiScale = scale;
            if (_hasBarPosition) Reanchor();
            else ResizeAtCurrentPosition();
        };
    }

    /// <summary>Anchor over the floating bar (shape button side) and clamp to the work area.</summary>
    public void PlaceAboveBar(Func<Windows.Graphics.RectInt32> getAnchor, IntPtr ownerHwnd = default,
        Func<Windows.Graphics.RectInt32> interactionRegion = null)
    {
        _getInteractionRegion = interactionRegion;
        _getAnchor = getAnchor; _hasBarPosition = true;
        if (ownerHwnd != IntPtr.Zero && _ownerHwnd != ownerHwnd)
        {
            _detachAnchor?.Invoke();
            _ownerHwnd = ownerHwnd;
            _detachAnchor = WindowChrome.TrackAnchor(this, ownerHwnd, () => { if (!_closed) Reanchor(); });
        }
        Reanchor();
    }

    public void RefreshDisplayPlacement()
    {
        if (_hasBarPosition) Reanchor();
        else ResizeAtCurrentPosition();
    }

    private void Reanchor()
    {
        if (IsDetached) return;
        double dpi = PopupDpiScale;
        var (w, h) = PhysicalSize();
        int gap = (int)Math.Ceiling(8 * dpi);
        var position = WindowChrome.GetPopupPosition(_getAnchor(), w, h, gap);
        PlaceFitted(position.X, position.Y);
    }

    private void WireDismissal()
    {
        Content.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; if (!_closed) Close(); }
        };
        TransientWindowDismissal.Attach(this, () => _ownerHwnd, () => _preview.AutoHide,
            () => _getAnchor?.Invoke() ?? default, () => _getInteractionRegion?.Invoke() ?? default);
        Closed += (_, _) => { _closed = true; _detachAnchor?.Invoke(); };
    }
}
