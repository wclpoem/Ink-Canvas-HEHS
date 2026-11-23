using System.Diagnostics;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace InkCanvas.App.Views;

public enum PptNavigationPlacement { BottomLeft, BottomRight, SideLeft, SideRight }

/// <summary>The four WPF-style previous/page/next panels, with independent idle opacity.</summary>
public sealed class PptNavigationWindow : Window
{
    private const double CellSize = FloatingBar.ChromeHeight - 2 * FloatingBar.ChromeBorderThickness;
    private readonly FloatingBarWindow _owner;
    private readonly PptPreviewContext _previewContext;
    private readonly Canvas _layout = new();
    private readonly Viewbox _cardHost;
    private readonly Border _previewSurface;
    private PptPageNavigator _preview;
    private bool _previewOpen;
    private int _previewAnimation;
    private readonly int _theme;
    private readonly Border _card;
    private readonly Viewbox _scaled;
    private readonly Button _counter;
    private readonly TextBlock _counterText;
    private readonly FontIcon _previousIcon, _nextIcon;
    private readonly Rectangle _separator1, _separator2;
    private readonly DispatcherQueueTimer _idleTimer, _fadeTimer;
    private readonly Stopwatch _fadeClock = new();
    private Action _detachAnchor;
    private XamlRoot _trackedRoot;
    private PptSnapshot _snapshot;
    private bool _closed, _placing, _placeQueued;
    private double _opacity = 1, _fadeFrom = 1, _fadeTo = 1;

    public PptNavigationPlacement Placement { get; }
    internal double WindowOpacity => _opacity;
    internal double TargetOpacity => _fadeTo;
    internal Border NavigationCard => _card;
    internal Border PreviewSurface => _previewSurface;
    internal PptPageNavigator Preview => _preview;
    internal bool PreviewOpen => _previewOpen;
    private bool IsVertical => Placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;

    public PptNavigationWindow(PptNavigationPlacement placement, IShellActions shell,
        int theme, FloatingBarWindow owner = null)
        : this(placement, shell, theme, owner, null) { }

    internal PptNavigationWindow(PptNavigationPlacement placement, IShellActions shell,
        int theme, FloatingBarWindow owner, PptPreviewContext previewContext)
    {
        Placement = placement;
        _owner = owner;
        _previewContext = previewContext;
        _theme = theme;
        Title = "PowerPoint 导航";
        _previousIcon = Icon(IsVertical ? "\uF2B7" : "\uF2AB");
        _nextIcon = Icon(IsVertical ? "\uF2A4" : "\uF2B1");
        _counterText = new TextBlock { Text = "0/0", FontSize = 12, IsTextScaleFactorEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = IsVertical ? Orientation.Vertical : Orientation.Horizontal };
        row.Children.Add(Cell(_previousIcon, "上一页", shell.PptPrevious));
        var count = new Grid();
        _separator1 = new Rectangle { Width = IsVertical ? 26 : 1, Height = IsVertical ? 1 : 26, Opacity = .35 };
        _separator2 = new Rectangle { Width = _separator1.Width, Height = _separator1.Height, Opacity = _separator1.Opacity };
        if (IsVertical)
        {
            count.RowDefinitions.Add(new() { Height = new(1) });
            count.RowDefinitions.Add(new() { Height = new(CellSize - 2) });
            count.RowDefinitions.Add(new() { Height = new(1) });
            Grid.SetRow(_counterText, 1); Grid.SetRow(_separator2, 2);
        }
        else
        {
            count.ColumnDefinitions.Add(new() { Width = new(1) });
            count.ColumnDefinitions.Add(new() { Width = new(CellSize + 10) });
            count.ColumnDefinitions.Add(new() { Width = new(1) });
            Grid.SetColumn(_counterText, 1); Grid.SetColumn(_separator2, 2);
        }
        count.Children.Add(_separator1); count.Children.Add(_counterText); count.Children.Add(_separator2);
        _counter = Cell(count, "幻灯片页码", () =>
        {
            if (_previewContext is null) shell.PptOpenNavigation();
            else SetPreviewOpen(!PreviewOpen);
        });
        _counter.Width = IsVertical ? CellSize : CellSize + 12;
        row.Children.Add(_counter);
        row.Children.Add(Cell(_nextIcon, "下一页", shell.PptNext));
        _card = new Border { Child = row, BorderThickness = new(FloatingBar.ChromeBorderThickness), CornerRadius = new(FloatingBar.ChromeCornerRadius) };
        _cardHost = new Viewbox { Child = _card, Stretch = Stretch.Uniform };
        _previewSurface = new Border { BorderThickness = new(1), CornerRadius = new(12), Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _layout.Children.Add(_cardHost); _layout.Children.Add(_previewSurface);
        _scaled = new Viewbox { Child = _layout, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Content = _scaled;
        _idleTimer = DispatcherQueue.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(5);
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => { if (!PreviewOpen) FadeTo(.75); };
        _fadeTimer = DispatcherQueue.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _fadeTimer.Tick += (_, _) => AdvanceFade();
        _scaled.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => RecordInteraction()), true);
        _scaled.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => RecordInteraction()), true);
        _scaled.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => RecordInteraction()), true);
        _scaled.KeyDown += (_, _) => RecordInteraction();
        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        WindowChrome.ClipToSurfaces(this, () => _preview?.IsOverview == true && _previewSurface.Visibility == Visibility.Visible
            ? new[] { _previewSurface } : new[] { _card, _previewSurface });
        ApplyChrome();
        if (_owner is not null)
        {
            _owner.Bar.ThemeRefreshed += ApplyChrome;
            _detachAnchor = WindowChrome.TrackAnchor(this, WindowChrome.GetHwnd(_owner), Place);
        }
        _scaled.Loaded += (_, _) =>
        {
            _trackedRoot = _scaled.XamlRoot;
            if (_trackedRoot is not null) _trackedRoot.Changed += OnRootChanged;
            Place();
        };
        _card.SizeChanged += (_, _) => QueuePlace();
        Closed += (_, _) =>
        {
            _closed = true;
            _preview?.Dispose();
            _idleTimer.Stop(); _fadeTimer.Stop();
            _detachAnchor?.Invoke(); _detachAnchor = null;
            if (_owner is not null) _owner.Bar.ThemeRefreshed -= ApplyChrome;
            if (_trackedRoot is not null) _trackedRoot.Changed -= OnRootChanged;
        };
        Place();
    }

    private static FontIcon Icon(string glyph) => new()
    {
        Glyph = glyph, FontFamily = new(IconFontStyle.Family),
        FontSize = 16 * FloatingBar.WpfScale, Width = 28, Height = 28, IsTextScaleFactorEnabled = false,
    };

    private Button Cell(UIElement content, string name, Action invoke)
    {
        var cell = new Button { Content = content, Width = CellSize, Height = CellSize, MinWidth = 0, MinHeight = 0,
            Padding = new(0), BorderThickness = new(0), CornerRadius = new(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        cell.Resources["ButtonBackgroundPointerOver"] = cell.Background;
        cell.Resources["ButtonBackgroundPressed"] = cell.Background;
        cell.Resources["ButtonBorderBrushPointerOver"] = cell.Background;
        cell.Resources["ButtonBorderBrushPressed"] = cell.Background;
        AutomationProperties.SetName(cell, name);
        ToolTipService.SetToolTip(cell, name);
        cell.Click += (_, _) => { RecordInteraction(); invoke(); };
        return cell;
    }

    private void ApplyChrome()
    {
        int theme = _owner?.Bar.Settings?.Appearance.Theme ?? _theme;
        if (theme == 0) theme = _owner?.Bar.Theme ?? 1;
        WindowChrome.ApplyTheme(this, theme);
        _card.Background = _owner?.Bar.ColorfulBackground == true ? PopupBrushes.FloatBarColorful() : PopupBrushes.FloatBarBackground(theme);
        _card.BorderBrush = PopupBrushes.FloatBarBorder(theme);
        var foreground = PopupBrushes.FloatBarForeground(theme);
        _previousIcon.Foreground = _nextIcon.Foreground = _counterText.Foreground = foreground;
        _separator1.Fill = _separator2.Fill = foreground;
        _previewSurface.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        _preview?.ApplyTheme(theme);
    }

    public void Update(PptSnapshot snapshot, bool panelVisible, bool counterVisible)
    {
        if (PreviewOpen && (!panelVisible || !counterVisible || snapshot?.IsSlideShowRunning != true)) SetPreviewOpen(false);
        _snapshot = snapshot;
        _preview?.Update(snapshot);
        _counter.Visibility = counterVisible ? Visibility.Visible : Visibility.Collapsed;
        int position = snapshot?.ShowPosition > 0 ? snapshot.ShowPosition : snapshot?.Slide?.Index ?? 0;
        string text = $"{position}/{snapshot?.Presentation?.SlideCount ?? 0}";
        if (_counterText.Text != text) _counterText.Text = text;
        Place();
        if (panelVisible == AppWindow.IsVisible) return;
        if (panelVisible)
        {
            WindowChrome.ShowWithoutActivation(this);
            SetOpacity(1);
            RecordInteraction();
        }
        else
        {
            _idleTimer.Stop(); _fadeTimer.Stop();
            AppWindow.Hide();
        }
    }

    internal void RecordInteraction()
    {
        if (_closed || !AppWindow.IsVisible) return;
        FadeTo(1);
        _idleTimer.Stop(); _idleTimer.Start();
    }

    internal void SetPreviewOpen(bool open)
    {
        if (_closed || open == _previewOpen || open && (_previewContext is null || _snapshot?.Presentation is null)) return;
        _previewOpen = open;
        int generation = ++_previewAnimation;
        if (open)
        {
            _previewContext.Opening(this);
            if (_preview is null)
            {
                _preview = new PptPageNavigator(_previewContext, _owner?.Bar.Theme ?? _theme);
                TransientWindowDismissal.Attach(this,
                    () => _snapshot?.WindowHandle ?? WindowChrome.GetHwnd((Window)_owner ?? this), () => PreviewOpen,
                    dismiss: () => SetPreviewOpen(false));
            }
            _preview.CloseRequested -= ClosePreview;
            _preview.CloseRequested += ClosePreview;
            _preview.ViewModeChanged -= Place;
            _preview.ViewModeChanged += Place;
            _previewSurface.Child = _preview;
            _previewSurface.Visibility = Visibility.Visible;
            SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            _preview.SetOpen(true, _snapshot);
            _idleTimer.Stop(); _fadeTimer.Stop(); SetOpacity(1);
        }
        else _preview?.SetOpen(false, null);
        _previewSurface.IsHitTestVisible = open;
        Place();
        _ = AnimatePreviewAsync(open, generation);
    }

    private void ClosePreview() => SetPreviewOpen(false);

    private async Task AnimatePreviewAsync(bool open, int generation)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_previewSurface, true);
        var visual = ElementCompositionPreview.GetElementVisual(_previewSurface);
        visual.StopAnimation("Translation"); visual.StopAnimation("Opacity");
        if (Motion.AnimationsEnabled)
        {
            float offset = Placement is PptNavigationPlacement.BottomLeft or PptNavigationPlacement.SideLeft ? -24 : 24;
            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0, new Vector3(open ? offset : 0, 0, 0));
            slide.InsertKeyFrame(1, new Vector3(open ? 0 : offset, 0, 0)); slide.Duration = Motion.Normal;
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, open ? 0 : 1); fade.InsertKeyFrame(1, open ? 1 : 0); fade.Duration = Motion.Normal;
            visual.StartAnimation("Translation", slide); visual.StartAnimation("Opacity", fade);
            await Task.Delay(Motion.Normal);
        }
        if (_closed || generation != _previewAnimation) return;
        visual.StopAnimation("Translation"); visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", Vector3.Zero); visual.Opacity = 1;
        if (!open)
        {
            _previewSurface.Visibility = Visibility.Collapsed;
            _preview.SetOverview(false);
            SystemBackdrop = null;
            Place(); RecordInteraction();
        }
    }

    private void FadeTo(double target)
    {
        _fadeTimer.Stop();
        _fadeFrom = _opacity; _fadeTo = target;
        if (!Motion.AnimationsEnabled || Math.Abs(_opacity - target) < .001) { SetOpacity(target); return; }
        _fadeClock.Restart();
        _fadeTimer.Start();
    }

    private void AdvanceFade()
    {
        double progress = Math.Clamp(_fadeClock.Elapsed.TotalMilliseconds / Math.Max(1, Motion.Normal.TotalMilliseconds), 0, 1);
        double eased = progress * progress * (3 - 2 * progress);
        SetOpacity(_fadeFrom + (_fadeTo - _fadeFrom) * eased);
        if (progress >= 1) _fadeTimer.Stop();
    }

    private void SetOpacity(double opacity)
    {
        _opacity = opacity;
        var hwnd = WindowChrome.GetHwnd(this);
        NativeMethods.AddExStyle(hwnd, NativeMethods.WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * 255), 2 /* LWA_ALPHA */);
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueuePlace();
    private void QueuePlace()
    {
        if (_closed || _placeQueued) return;
        _placeQueued = true;
        DispatcherQueue.TryEnqueue(() => { _placeQueued = false; Place(); });
    }

    private void Place()
    {
        if (_closed || _placing) return;
        _placing = true;
        try
        {
            var monitor = ScreenService.FromWindow(_snapshot?.WindowHandle ?? WindowChrome.GetHwnd((Window)_owner ?? this));
            var area = _owner is not null ? _owner.GetPlacementArea()
                : _snapshot?.IsWindowedShow == true ? WindowChrome.GetClientBounds(_snapshot.WindowHandle).FitInside(monitor.WorkArea)
                : monitor.Bounds;
            WindowChrome.MoveToMonitor(this, monitor);
            double dpi = WindowChrome.GetDpiScale(this);
            _card.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var design = _card.DesiredSize;
            double scale = Math.Min(_owner?.EffectiveScale ?? 1, Math.Min(area.Width / (design.Width * dpi), area.Height / (design.Height * dpi)));
            _cardHost.Width = design.Width * scale; _cardHost.Height = design.Height * scale;
            ScreenBox toolbar = default;
            if (_owner?.AppWindow.IsVisible == true)
            {
                var origin = WindowChrome.GetClientPosition(_owner);
                var size = _owner.AppWindow.ClientSize;
                toolbar = new(origin.X, origin.Y, size.Width, size.Height);
            }
            int gap = (int)Math.Round((IsVertical ? 6 : Placement == PptNavigationPlacement.BottomLeft ? 2 : 0) * dpi);
            var placed = NavigationLayout.Place((NavigationCorner)Placement, new(area.X, area.Y, area.Width, area.Height),
                (int)Math.Ceiling(_cardHost.Width * dpi), (int)Math.Ceiling(_cardHost.Height * dpi), gap, toolbar);
            bool showingOverview = _preview?.IsOverview == true && _previewSurface.Visibility == Visibility.Visible;
            _cardHost.Opacity = showingOverview ? 0 : 1;
            _cardHost.IsHitTestVisible = !showingOverview;
            int x = placed.X, y = placed.Y, right = x + placed.Width, bottom = y + placed.Height;
            if (_previewSurface.Visibility == Visibility.Visible)
            {
                bool overview = _preview?.IsOverview == true;
                int inset = overview ? 0 : (int)Math.Ceiling(8 * dpi);
                int previewWidth = overview ? area.Width : Math.Min((int)Math.Ceiling(BoardPageNavigator.SidebarWidth * dpi), Math.Max(1, area.Width - 2 * inset));
                int previewBottom = overview || IsVertical ? area.Y + area.Height - inset : placed.Y - inset;
                int previewY = area.Y + inset;
                int previewHeight = Math.Max(1, previewBottom - previewY);
                bool left = Placement is PptNavigationPlacement.BottomLeft or PptNavigationPlacement.SideLeft;
                int previewX = overview ? area.X : IsVertical ? left ? right + inset : x - inset - previewWidth
                    : left ? area.X + inset : area.X + area.Width - inset - previewWidth;
                previewX = Math.Clamp(previewX, area.X, area.X + area.Width - previewWidth);
                x = Math.Min(x, previewX); y = Math.Min(y, previewY);
                right = Math.Max(right, previewX + previewWidth); bottom = Math.Max(bottom, previewY + previewHeight);
                _previewSurface.Width = previewWidth / dpi; _previewSurface.Height = previewHeight / dpi;
                _previewSurface.CornerRadius = new CornerRadius(overview ? 0 : 12);
                Canvas.SetLeft(_previewSurface, (previewX - x) / dpi); Canvas.SetTop(_previewSurface, (previewY - y) / dpi);
            }
            Canvas.SetLeft(_cardHost, (placed.X - x) / dpi); Canvas.SetTop(_cardHost, (placed.Y - y) / dpi);
            _layout.Width = _scaled.Width = (right - x) / dpi;
            _layout.Height = _scaled.Height = (bottom - y) / dpi;
            var current = WindowChrome.GetClientPosition(this);
            if (current.X != x || current.Y != y || AppWindow.ClientSize.Width != right - x || AppWindow.ClientSize.Height != bottom - y)
                WindowChrome.PlaceClient(this, x, y, right - x, bottom - y);
        }
        finally { _placing = false; }
    }

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
}
