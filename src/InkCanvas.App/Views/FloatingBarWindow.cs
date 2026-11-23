using InkCanvas.Controls.Controls;
using InkCanvas.App.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using InkCanvas.Platform.System;
using InkCanvas.Controls.Lifecycle;

namespace InkCanvas.App.Views;

/// <summary>
/// Borderless, topmost, semi-transparent WinUI 3 window hosting ONLY the floating bar row.
/// Draggable by its background. The 更多设置 / 手势选项 popups live in separate
/// <see cref="BarPanelWindow"/>s so opening them never resizes or shifts this row.
/// </summary>
public sealed class FloatingBarWindow : Window
{
    private readonly FloatingBar _bar = new();
    private readonly Viewbox _scaledBar;
    private readonly Border _root;
    private readonly List<FoldedBarHandleWindow> _foldHandles = new();
    private readonly Dictionary<BarPanelKind, BarPanelWindow> _panels = new();
    private bool _isFolded;
    private bool _userPlaced;
    private bool _presentationMode, _windowedPresentation, _desktopUserPlaced;
    private nint _presentationWindow;
    private Windows.Graphics.PointInt32 _desktopPosition;
    private ScreenRect _presentationArea;
    private double _bottomMargin = 100, _scalePercent = 100;
    private bool _initialDpiPlacementDone;
    private double _lastDpiScale = 1;
    private XamlRoot _trackedXamlRoot;
    private readonly WindowIdleResources _idleResources;
    private bool _closed;

    public FloatingBarWindow()
    {
        Title = "Ink Canvas 工具栏";
        _scaledBar = new Viewbox
        {
            Child = _bar,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0),
            Child = _scaledBar,
        };
        Content = _root;

        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        EnableTransparency(this);
        WindowChrome.ClipToSurfaces(this, () => _bar.ChromeSurfaces);
        _idleResources = new WindowIdleResources(this, () => SystemBackdrop = null, () =>
        {
            if (SystemBackdrop is null) EnableTransparency(this);
            UpdateAcrylicTint(this, _bar.Theme);
        });
        WindowChrome.MoveToMonitor(this, ScreenService.AtCursor());
        WindowChrome.EnableDragMove(this, _root, () => { _userPlaced = true; _tapSource = null; }, fromAnywhere: true,
            onDragCompleted: RefreshDisplayPlacement);
        AttachTapRouting();
        _root.Loaded += (_, _) =>
        {
            AttachDpiTracking();
            if (!_initialDpiPlacementDone)
            {
                _initialDpiPlacementDone = true;
                Reposition(_bottomMargin, _scalePercent);
                UpdateAcrylicTint(this, _bar.Theme);
            }
        };

        _bar.LayoutUpdated += OnBarLayoutUpdated;
        _bar.MainCollapseAnimationChanged += OnMainCollapseAnimationChanged;
        _bar.PanelRequested += OnPanelRequested;
        _bar.SwitchSideRequested += SwitchSide;
        _bar.ThemeRefreshed += () => UpdateAcrylicTint(this, _bar.Theme);

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (!_initialDpiPlacementDone)
            {
                _initialDpiPlacementDone = true;
                Reposition(_bottomMargin, _scalePercent);
                UpdateAcrylicTint(this, _bar.Theme);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _bar.FinishMainCollapseAnimation();
            if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed -= OnXamlRootChanged;
            _bar.LayoutUpdated -= OnBarLayoutUpdated;
            _bar.MainCollapseAnimationChanged -= OnMainCollapseAnimationChanged;
            _bar.PanelRequested -= OnPanelRequested;
            _bar.SwitchSideRequested -= SwitchSide;
            ClosePanels(includeDetached: true);
            CloseFoldHandles();
        };
    }

    internal double UserScale => Math.Clamp(_scalePercent, 50, 200) / 100.0;
    internal double EffectiveScale => _scaledBar.Height > 0 ? _scaledBar.Height / _bar.ComputeLayoutSize().Height : UserScale;

    /// <summary>
    /// 浮条窗口是否仍可重排。关闭板会经 FloatingBar.set_Controller → LayoutChanged 重排浮条，
    /// 若此时浮条已随应用退出而关闭，必须直接跳过，否则 Place 会拿到空的 AppWindow。
    /// </summary>
    private bool CanReposition()
    {
        if (_closed) return false;
        try
        {
            if (AppWindow is null || WindowChrome.GetHwnd(this) == IntPtr.Zero) return false;
            return true;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[FloatingBar] 跳过重排：窗口已关闭，{ex.GetType().Name}: {ex.Message}",
                Core.Diagnostics.LogType.Trace);
            return false;
        }
    }

    private void SwitchSide()
    {
        if (!CanReposition()) return;
        var work = WindowChrome.GetWorkArea(this);
        var position = AppWindow.Position;
        int width = AppWindow.Size.Width;
        int mirrored = work.X + work.Width - (position.X - work.X) - width;
        if (Math.Abs(mirrored - position.X) < 2) mirrored = work.X + work.Width - width - 12;
        _userPlaced = true;
        ClosePanels();
        WindowChrome.Place(this, Math.Clamp(mirrored, work.X, work.X + Math.Max(0, work.Width - width)),
            position.Y, width, AppWindow.Size.Height);
    }

    private void AttachDpiTracking()
    {
        if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed -= OnXamlRootChanged;
        _trackedXamlRoot = _root.XamlRoot;
        if (_trackedXamlRoot is not null) _trackedXamlRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!CanReposition()) return;
        double newScale = WindowChrome.GetDpiScale(this);
        if (Math.Abs(newScale - _lastDpiScale) < 0.001) return;
        _lastDpiScale = newScale;
        Reposition(_bottomMargin, _scalePercent, recenter: false);
    }

    public FloatingBar Bar => _bar;

    public Windows.Graphics.RectInt32 GetToolAnchor(InkCanvas.Ink.InkTool tool)
        => WindowChrome.GetScreenBounds(this, _bar.GetToolButton(tool));

    public bool IsFolded => _isFolded;
    internal IReadOnlyList<FoldedBarHandleWindow> FoldHandles => _foldHandles;

    /// <summary>Diagnostics only: the presentation state used to place the bar.</summary>
    internal string DescribePlacement()
    {
        var area = GetPlacementArea();
        var position = WindowChrome.GetClientPosition(this);
        return $"mode={( _presentationMode ? "presentation" : "desktop")}" +
            $" windowed={_windowedPresentation} placement=({area.X},{area.Y},{area.Width},{area.Height})" +
            $" desktop=({_desktopPosition.X},{_desktopPosition.Y}) userPlaced={_userPlaced}" +
            $" bottomMargin={_bottomMargin} scale={_scalePercent} client=({position.X},{position.Y})" +
            $" size=({AppWindow.ClientSize.Width},{AppWindow.ClientSize.Height})";
    }

    public void SetPresentationMode(bool active, nint window = default, bool windowed = false)
    {
        if (!CanReposition()) return;
        if (!active)
        {
            if (!_presentationMode) return;
            _presentationMode = false;
            _presentationWindow = 0;
            _userPlaced = _desktopUserPlaced;
            ClosePanels();
            var screen = ScreenService.FromRect(new(_desktopPosition.X, _desktopPosition.Y, 1, 1));
            WindowChrome.MoveToMonitor(this, screen);
            var size = GetPhysicalSize(WindowChrome.GetDpiScale(this));
            var restored = new ScreenRect(_desktopPosition.X, _desktopPosition.Y, size.Width, size.Height).FitInside(screen.WorkArea);
            WindowChrome.PlaceClient(this, restored.X, restored.Y, restored.Width, restored.Height);
            RefreshFoldHandles();
            return;
        }
        bool targetChanged = !_presentationMode || _presentationWindow != window || _windowedPresentation != windowed;
        if (!_presentationMode)
        {
            _desktopPosition = WindowChrome.GetClientPosition(this);
            _desktopUserPlaced = _userPlaced;
        }
        _presentationMode = true;
        _presentationWindow = window;
        _windowedPresentation = windowed;
        var area = GetPlacementArea();
        if (!targetChanged && area == _presentationArea) return;
        _presentationArea = area;
        if (targetChanged)
        {
            _userPlaced = false;
            ClosePanels();
            WindowChrome.MoveToMonitor(this, ScreenService.FromWindow(window));
        }
        Reposition(_bottomMargin, _scalePercent, recenter: false);
        RefreshFoldHandles();
    }

    private void RefreshFoldHandles()
    {
        if (!_isFolded) return;
        CloseFoldHandles();
        CreateFoldHandles();
    }

    internal ScreenRect GetPlacementArea()
    {
        if (!_presentationMode)
        {
            var work = WindowChrome.GetWorkArea(this);
            return new(work.X, work.Y, work.Width, work.Height);
        }
        var screen = ScreenService.FromWindow(_presentationWindow);
        if (!_windowedPresentation) return screen.Bounds;
        var client = WindowChrome.GetClientBounds(_presentationWindow);
        int left = Math.Max(client.X, screen.WorkArea.X), top = Math.Max(client.Y, screen.WorkArea.Y);
        int right = Math.Min(client.X + client.Width, screen.WorkArea.X + screen.WorkArea.Width);
        int bottom = Math.Min(client.Y + client.Height, screen.WorkArea.Y + screen.WorkArea.Height);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : screen.WorkArea;
    }

    public void SetFolded(bool folded)
    {
        if (_closed || _isFolded == folded) return;
        _isFolded = folded;
        if (folded)
        {
            _bar.FinishMainCollapseAnimation();
            ClosePanels();
            CloseFoldHandles();
            AppWindow.Hide();
            CreateFoldHandles();
            _bar.SetFoldedVisual(true);
        }
        else
        {
            CloseFoldHandles();
            _bar.SetFoldedVisual(false);
            Reposition(_bottomMargin, _scalePercent, recenter: false);
            WindowChrome.ShowWithoutActivation(this);
        }
    }

    private void CreateFoldHandles()
    {
        var screen = ScreenService.FromWindow(WindowChrome.GetHwnd(this));
        var work = screen.WorkArea;
        int width = (int)Math.Ceiling(30 * screen.DpiScale), height = (int)Math.Ceiling(50 * screen.DpiScale);
        int y = work.Y + (work.Height - height) / 2;
        // Keep the whole HWND on this display. Off-screen handles overlap adjacent monitors.
        _foldHandles.Add(new FoldedBarHandleWindow(this, true, width, height, work.X, y));
        _foldHandles.Add(new FoldedBarHandleWindow(this, false, width, height, work.X + work.Width - width, y));
        foreach (var handle in _foldHandles) handle.ShowHandle();
    }

    public void RefreshDisplayPlacement()
    {
        if (!CanReposition()) return;
        Reposition(_bottomMargin, _scalePercent, recenter: false);
        if (_isFolded)
        {
            CloseFoldHandles();
            CreateFoldHandles();
        }
    }

    private void CloseFoldHandles()
    {
        foreach (var handle in _foldHandles.ToArray()) handle.Close();
        _foldHandles.Clear();
    }

    public void Configure(InkCanvas.Ink.InkController controller, IShellActions shell)
    {
        _bar.Controller = controller;
        _bar.Shell = shell;
        _bar.Build();
    }

    /// <summary>
    /// Applies an explicit appearance theme to the window chrome and to the hosted bar.
    /// 0 = follow system, 1 = light, 2 = dark.
    /// </summary>
    public void ApplyTheme(int theme)
    {
        WindowChrome.ApplyTheme(this, theme);
        _bar.ApplyTheme(theme);
    }

    /// <summary>
    /// Size the window to the bar's measured content and place it bottom-centre. Sizing to
    /// content matters: any leftover window area is transparent but still hit-testable.
    /// </summary>
    public void Reposition(double bottomMargin, double scalePercent, bool recenter = true)
    {
        if (!CanReposition()) return;
        _bottomMargin = bottomMargin;
        _scalePercent = scalePercent;
        if (recenter) _userPlaced = false;
        double dpiScale = WindowChrome.GetDpiScale(this);
        _lastDpiScale = dpiScale;
        var (widthPx, heightPx, _) = GetPhysicalSize(dpiScale);
        PlaceBottomCentre(widthPx, heightPx, dpiScale, keepPosition: !recenter && _initialDpiPlacementDone);
        WindowChrome.EnsureAbove(this);
        ReanchorPanels();
    }

    private (int Width, int Height, double DpiScale) GetPhysicalSize(double dpiScale)
    {
        double userScale = UserScale;
        var (widthDip, heightDip) = _bar.ComputeLayoutSize();
        var expanded = _bar.ComputeLayoutSize(expanded: true);
        if (_bar.IsMainCollapseAnimating)
        {
            widthDip = expanded.Width;
            _bar.Width = widthDip;
        }
        else _bar.Width = double.NaN;

        // 启动阶段 WindowChrome.GetDpiScale 可能仍返回 1，而 XAML 内容已按真实 DPI 栅格化。
        // 优先用 XamlRoot.RasterizationScale 换算物理像素，避免窗口偏小、右侧按钮被裁切。
        double raster = _bar.XamlRoot?.RasterizationScale ?? dpiScale;
        if (raster <= 0) raster = dpiScale > 0 ? dpiScale : 1;

        var work = GetPlacementArea();
        // 工作区不足时只降低本次渲染比例（上限 150%），不改用户设置的原值 _scalePercent。
        double maxScaleByWidth = work.Width / (expanded.Width * raster);
        double maxScaleByHeight = work.Height / (heightDip * raster);
        double effectiveScale = Math.Min(userScale, Math.Min(maxScaleByWidth, maxScaleByHeight));
        effectiveScale = Math.Clamp(effectiveScale, 0.5, Math.Min(1.5, userScale));

        // Viewbox measures the design at its natural size before scaling. RenderTransform
        // scales after layout, so small windows clip the bar before the transform is applied.
        _scaledBar.Width = widthDip * effectiveScale;
        _scaledBar.Height = heightDip * effectiveScale;
        int widthPx = (int)Math.Ceiling(widthDip * effectiveScale * raster);
        int heightPx = (int)Math.Ceiling(heightDip * effectiveScale * raster);
        return (widthPx, heightPx, dpiScale);
    }

    private void PlaceBottomCentre(int width, int height, double dpiScale, bool keepPosition = false)
    {
        if (!CanReposition()) return;
        var work = GetPlacementArea();
        bool keep = _presentationMode ? _userPlaced : keepPosition || _userPlaced || (_isFolded && _foldHandles.Count > 0);
        var current = WindowChrome.GetClientPosition(this);
        int x = keep ? current.X : work.X + (work.Width - width) / 2;
        int y = keep ? current.Y
            : work.Y + work.Height - height - (int)Math.Ceiling((_presentationMode ? 8 * UserScale : Math.Max(0, _bottomMargin)) * dpiScale);
        var fit = new ScreenRect(x, y, width, height).FitInside(new(work.X, work.Y, work.Width, work.Height));
        WindowChrome.PlaceClient(this, fit.X, fit.Y, fit.Width, fit.Height);
    }

    private int _lastButtons = -1, _lastSeparators = -1;
    private bool _lastExpanded, _lastCollapsed;
    private double _lastMainExpansion = -1;

    private void OnMainCollapseAnimationChanged()
    {
        if (!CanReposition() || !_initialDpiPlacementDone) return;
        double dpi = WindowChrome.GetDpiScale(this);
        var size = GetPhysicalSize(dpi);
        PlaceBottomCentre(size.Width, size.Height, dpi, keepPosition: true);
    }

    private void OnBarLayoutUpdated(object sender, object e)
    {
        if (!CanReposition() || !_initialDpiPlacementDone) return;
        var (buttons, separators) = _bar.CountVisibleItems();
        bool expanded = _bar.IsExpanded;
        bool collapsed = _bar.IsMainCollapsed;
        double mainExpansion = _bar.MainExpansion;
        if (buttons == _lastButtons && separators == _lastSeparators && expanded == _lastExpanded && collapsed == _lastCollapsed
            && mainExpansion == _lastMainExpansion) return;
        _lastButtons = buttons;
        _lastSeparators = separators;
        _lastExpanded = expanded;
        _lastCollapsed = collapsed;
        _lastMainExpansion = mainExpansion;
        // The swap chain stays at its expanded size for the entire transition. Only the
        // painted XAML cards and their native region change while the smiley animates.
        if (_bar.IsMainCollapseAnimating) return;
        // Refit in place: keep the left/top edge so the primary buttons never shift; a smiley
        // collapse shrinks the window from the RIGHT only.
        double dpi = WindowChrome.GetDpiScale(this);
        var (w, h, _) = GetPhysicalSize(dpi);
        PlaceBottomCentre(w, h, dpi, keepPosition: true);
        WindowChrome.EnsureAbove(this);
        ReanchorPanels();
    }

    // ---- sub-popups (own windows, anchored above the bar) ----

    private void OnPanelRequested(BarPanelKind kind, bool open)
    {
        if (!open)
        {
            if (_panels.TryGetValue(kind, out var closing)) closing.DismissOnInput();
            return;
        }
        foreach (var other in _panels.Where(pair => pair.Key != kind).Select(pair => pair.Value).ToArray()) other.DismissOnInput();
        if (!_panels.TryGetValue(kind, out var panel))
        {
            panel = new BarPanelWindow(this, _bar, kind);
            _panels.Add(kind, panel);
        }
        if (!CanReposition()) return;
        var position = AppWindow.Position;
        panel.ShowAboveBar(position.X, position.Y, AppWindow.Size.Width);
    }

    internal void OnPanelClosed(BarPanelKind kind, BarPanelWindow window)
    {
        if (!_panels.TryGetValue(kind, out var current) || !ReferenceEquals(current, window)) return;
        _panels.Remove(kind);
        _bar.NotifyPanelClosed(kind);
    }

    internal BarPanelWindow GetOpenPanel(BarPanelKind kind) => _panels.GetValueOrDefault(kind);

    internal void EnsureHandlesAbove(IntPtr overlay = default)
    {
        foreach (var h in _foldHandles) WindowChrome.EnsureAbove(h, overlay);
        foreach (var panel in _panels.Values) WindowChrome.EnsureAbove(panel, overlay);
    }

    internal void ClosePanels(bool includeDetached = false)
    {
        foreach (var panel in _panels.Values.ToArray())
            if (includeDetached || !panel.IsDetached) panel.Close();
    }

    internal void DismissPanelsOnInput()
    {
        foreach (var panel in _panels.Values.ToArray()) panel.DismissOnInput();
    }

    private void ReanchorPanels()
    {
        if (!CanReposition()) return;
        var position = AppWindow.Position;
        foreach (var panel in _panels.Values.ToArray())
            if (!panel.IsDetached) panel.PlaceAboveBar(position.X, position.Y, AppWindow.Size.Width);
    }

    // ---- tap routing for smiley / PPT plate ----

    private DependencyObject _tapSource;
    private Windows.Foundation.Point _tapPos;

    private void AttachTapRouting()
    {
        _root.PointerPressed += (_, e) =>
        {
            _tapSource = e.OriginalSource as DependencyObject;
            _tapPos = e.GetCurrentPoint(_root).Position;
        };
        _root.PointerReleased += (_, e) =>
        {
            var source = _tapSource;
            _tapSource = null;
            if (source is null) return;
            var p = e.GetCurrentPoint(_root).Position;
            if (Math.Abs(p.X - _tapPos.X) <= 10 && Math.Abs(p.Y - _tapPos.Y) <= 10)
                _bar.TryHandleTap(source);
        };
        _root.PointerCanceled += (_, _) => _tapSource = null;
    }

    /// <summary>Use themed acrylic with an opaque card fallback when desktop composition is unavailable.</summary>
    public static void EnableTransparency(Window window)
    {
        var backdrop = new ChromeBackdrop();
        int theme = (window.Content as FrameworkElement)?.RequestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        backdrop.SetTheme(theme);
        window.SystemBackdrop = backdrop;
    }

    /// <summary>
    /// Keep the material and its fallback in the same theme as the painted surface.
    /// </summary>
    public static void UpdateAcrylicTint(Window window, int theme)
    {
        (window.SystemBackdrop as ChromeBackdrop)?.SetTheme(theme);
    }
}
