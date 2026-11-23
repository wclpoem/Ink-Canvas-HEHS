using InkCanvas.Core.Diagnostics;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using InkCanvas.Platform.System;
using InkCanvas.Controls.Controls;

namespace InkCanvas.App.Services;

/// <summary>
/// Native frames for application windows and borderless chrome for floating tools.
/// </summary>
public static class WindowChrome
{
    private const string LogPrefix = "[WindowChrome]";

    // 关闭板时浮条会被多次重排；同一原因在一秒内只记一次 Trace，避免刷日志。
    private static string _lastGuardKey;
    private static long _lastGuardTicks;

    public static IntPtr GetHwnd(Window window)
    {
        if (window is null) return IntPtr.Zero;
        // WinUI 在窗口已关闭后取 HWND 会抛 COMException；统一按「无 HWND」处理。
        try { return WindowNative.GetWindowHandle(window); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>读取 AppWindow，并吞掉「窗口已关闭」时 WinUI 抛出的 COMException。</summary>
    private static bool TryGetAppWindow(Window window, out AppWindow appWindow)
    {
        appWindow = null;
        if (window is null)
        {
            LogPlacementSkipped(nameof(TryGetAppWindow), WindowPlacementReasons.WindowMissing);
            return false;
        }
        try { appWindow = window.AppWindow; }
        catch (Exception ex)
        {
            LogPlacementSkipped(nameof(TryGetAppWindow), WindowPlacementReasons.AppWindowUnavailable, ex);
            return false;
        }
        if (appWindow is null)
        {
            LogPlacementSkipped(nameof(TryGetAppWindow), WindowPlacementReasons.AppWindowMissing);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 放置窗口前的统一守卫：窗口为 null、已关闭、AppWindow 不可用或 HWND 已销毁时
    /// 返回 false 并记 Trace，调用方必须直接 return，绝不抛给 UI 事件链。
    /// </summary>
    private static bool TryGetPlaceable(Window window, string operation, out AppWindow appWindow, out IntPtr hwnd)
    {
        appWindow = null;
        hwnd = IntPtr.Zero;
        bool hasAppWindow = TryGetAppWindow(window, out appWindow);
        if (hasAppWindow) hwnd = GetHwnd(window);
        bool alive = hwnd != IntPtr.Zero && IsWindow(hwnd);
        var state = WindowPlacementGuard.FromHwnd(window is not null, hasAppWindow, hwnd, alive);
        if (state.CanPlace) return true;
        LogPlacementSkipped(operation, state.Reason);
        return false;
    }

    private static void LogPlacementSkipped(string operation, string reason, Exception exception = null)
    {
        if (!FileLog.Enabled) return;
        string key = $"{operation}:{reason}";
        long now = Environment.TickCount64;
        if (key == _lastGuardKey && now - _lastGuardTicks < 1000) return;
        _lastGuardKey = key;
        _lastGuardTicks = now;
        string detail = exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})";
        FileLog.WriteLogToFile($"{LogPrefix} 跳过窗口放置：操作={operation}，原因={reason}{detail}", LogType.Trace);
    }

    private sealed class Placement
    {
        public int Kind; // 0 = physical px, 1 = DIP rect, 2 = DIP centre-on-primary
        public double X, Y, W, H;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Placement> Placements = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, ClippedWindowFrame> ClippedFrames = new();
    private readonly record struct SurfaceRect(double Left, double Top, double Right, double Bottom,
        double TopLeft, double TopRight, double BottomRight, double BottomLeft);

    public static void MakeBorderless(Window window, bool topmost = true, bool resizable = false, bool noActivate = false)
    {
        var appWindow = window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter)
        {
            // Configure before attachment so WinUI applies one native frame change.
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
            presenter.IsMinimizable = resizable;
            presenter.IsAlwaysOnTop = topmost;
            appWindow.SetPresenter(presenter);
        }
        // No system backdrop: these windows paint their own tokenised surface.
        window.SystemBackdrop = null;
        SetRoundedCorners(window);
        if (noActivate)
        {
            var activation = new Platform.Windows.NonActivatingWindow(GetHwnd(window));
            if (window.Content is FrameworkElement content)
            {
                // Inherited by controls in the XAML island. Pointer interaction must not request focus.
                content.AllowFocusOnInteraction = false;
                content.Loaded += (_, _) => activation.RefreshChildren();
            }
            window.Closed += (_, _) => activation.Dispose();
        }
        // Constructors place windows before the HWND exists, so GetDpiForWindow returns 0 and
        // DIP placements land at scale 1 (clipped content on 125%/150% monitors). Re-apply the
        // recorded DIP placement once, on first activation, when the monitor DPI is known.
        bool closed = false, initialPlacementApplied = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            Core.Diagnostics.FileLog.WriteLogToFile($"window Closed: {window.Title}");
        };
        void ApplyInitialPlacement()
        {
            if (closed) return;
            if (!initialPlacementApplied && Placements.TryGetValue(window, out _))
            {
                initialPlacementApplied = true;
                // MoveAndResize throws ArgumentException inside the Activated callback itself;
                // defer one dispatcher tick so the window finishes activating first.
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    // Re-read the CURRENT record: the shell may have placed the window explicitly
                    // (PlaceAboveBar etc.) between activation and this tick; re-applying the stale
                    // constructor placement would snap popups back to (100,100).
                    if (closed || !Placements.TryGetValue(window, out var cur)) return;
                    if (cur.Kind == 1) PlaceDip(window, cur.X, cur.Y, cur.W, cur.H);
                    else if (cur.Kind == 2) CenterOnPrimary(window, (int)cur.W, (int)cur.H);
                    // Kind 0 = explicit physical placement already applied by the caller.
                });
            }
        }
        window.Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) ApplyInitialPlacement(); };
        if (window.Content is FrameworkElement root) root.Loaded += (_, _) => ApplyInitialPlacement();
    }

    /// <summary>
    /// Win32 兜底隐藏：WinUI 的 AppWindow.Hide 在 owner 关系下偶发不生效时，
    /// 直接 ShowWindow(SW_HIDE)，并清 WS_VISIBLE。
    /// </summary>
    public static void HideWindow(Window window)
    {
        var hwnd = GetHwnd(window);
        if (hwnd != IntPtr.Zero) NativeMethods.ShowWindow(hwnd, 0 /* SW_HIDE */);
    }

    public static void ShowWithoutActivation(Window window)
    {
        if (!TryGetPlaceable(window, nameof(ShowWithoutActivation), out var appWindow, out _)) return;
        try { appWindow.Show(activateWindow: false); }
        catch (Exception ex) { LogPlacementSkipped(nameof(ShowWithoutActivation), "show", ex); return; }
        EnsureAbove(window);
    }

    public static void MakeStandard(Window window, int widthDip = 1000, int heightDip = 700, bool resizable = true, bool topmost = true)
    {
        bool wasClipped = ClippedFrames.TryGetValue(window, out var clippedFrame);
        if (wasClipped)
        {
            clippedFrame.Dispose();
            ClippedFrames.Remove(window);
        }
        window.ExtendsContentIntoTitleBar = false;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
            presenter.IsMinimizable = true;
            presenter.IsAlwaysOnTop = topmost;
        }
        var hwnd = GetHwnd(window);
        if (wasClipped)
        {
            // The presenter may restore only WS_BORDER after a manually cleared native frame.
            int style = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_STYLE);
            NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_STYLE, style | 0x00C00000 /* WS_CAPTION */);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE
                | 0x0004 /* SWP_NOZORDER */ | 0x0020 /* SWP_FRAMECHANGED */);
        }
        SetWindowRgn(hwnd, IntPtr.Zero, true);
        int preference = 0, policy = 0;
        int border = unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        DwmSetWindowAttribute(hwnd, 2, ref policy, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        int transitions = 0;
        DwmSetWindowAttribute(hwnd, 3, ref transitions, sizeof(int));
        bool placed = false, closed = false;
        window.Closed += (_, _) => closed = true;
        if (window.Content is FrameworkElement root)
        {
            void UpdateCaptionTheme()
            {
                if (closed) return;
                int dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            }
            root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
            root.Loaded += (_, _) => UpdateCaptionTheme();
        }
        void Fit()
        {
            if (placed) return;
            placed = true;
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (closed) return;
                var work = GetWorkArea(window);
                double dpi = GetDpiScale(window);
                int width = Math.Min(work.Width, (int)Math.Ceiling(widthDip * dpi));
                int height = Math.Min(work.Height, (int)Math.Ceiling(heightDip * dpi));
                Place(window, work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
            });
        }
        window.Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) Fit(); };
    }

    public static void Place(Window window, int x, int y, int width, int height)
    {
        // 关闭板后浮条重排会从 Closed 回调链进入这里；窗口已销毁时直接丢弃本次放置。
        if (!TryGetPlaceable(window, nameof(Place), out _, out _)) return;
        Placements.AddOrUpdate(window, new Placement { Kind = 0, X = x, Y = y, W = width, H = height });
        PlaceCore(window, x, y, width, height);
    }

    /// <summary>Place content-sized chrome using physical client pixels, excluding the native frame.</summary>
    public static void PlaceClient(Window window, int x, int y, int width, int height)
    {
        if (!TryGetPlaceable(window, nameof(PlaceClient), out var appWindow, out var hwnd)) return;
        // Establish the target DPI before WinUI calculates non-client metrics.
        var target = ScreenService.FromRect(new(x, y, width, height));
        try
        {
            if (ScreenService.FromWindow(hwnd).Handle != target.Handle)
                appWindow.Move(new PointInt32(target.WorkArea.X, target.WorkArea.Y));
            // A borderless presenter can retain a few pixels of non-client frame. MoveAndResize
            // sizes the outer HWND, so using a measured XAML size there cuts off the bottom/right.
            var client = appWindow.ClientSize;
            if (client.Width != width || client.Height != height)
                appWindow.ResizeClient(new SizeInt32(width, height));
            var outer = appWindow.Size;
            var origin = new NativeMethods.POINT();
            if (!GetWindowRect(hwnd, out var frame) || !ClientToScreen(hwnd, ref origin)) return;
            Place(window, x - (origin.X - frame.Left), y - (origin.Y - frame.Top), outer.Width, outer.Height);
        }
        catch (Exception ex)
        {
            LogPlacementSkipped(nameof(PlaceClient), "move-or-resize", ex);
        }
    }

    /// <summary>Actual painted element bounds in physical screen pixels, including Viewbox scaling.</summary>
    public static PointInt32 GetClientPosition(Window window)
    {
        var hwnd = GetHwnd(window);
        var origin = new NativeMethods.POINT();
        if (hwnd != IntPtr.Zero && ClientToScreen(hwnd, ref origin)) return new PointInt32(origin.X, origin.Y);
        // 窗口已关闭时不能回读 AppWindow.Position（COMException）；退回原点由调用方再判空。
        return TryGetAppWindow(window, out var appWindow) ? appWindow.Position : default;
    }

    /// <summary>Actual painted element bounds in physical screen pixels, including Viewbox scaling.</summary>
    public static RectInt32 GetScreenBounds(Window window, FrameworkElement element)
    {
        if (window?.Content is not FrameworkElement root || element is null) return default;
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return default;
        var origin = new NativeMethods.POINT();
        if (!ClientToScreen(hwnd, ref origin)) return default;
        double dpi = root.XamlRoot?.RasterizationScale ?? GetDpiScale(window);
        var bounds = element.TransformToVisual(root).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        int left = origin.X + (int)Math.Floor(bounds.Left * dpi);
        int top = origin.Y + (int)Math.Floor(bounds.Top * dpi);
        return new RectInt32(left, top, (int)Math.Ceiling(bounds.Right * dpi) + origin.X - left,
            (int)Math.Ceiling(bounds.Bottom * dpi) + origin.Y - top);
    }

    /// <summary>整个窗口的物理屏幕矩形（Win32 外框），用于拖放后的边界夹取。</summary>
    public static RectInt32 GetWindowBounds(Window window)
    {
        if (!TryGetPlaceable(window, nameof(GetWindowBounds), out var appWindow, out var hwnd)) return default;
        try
        {
            if (GetWindowRect(hwnd, out var frame))
                return new RectInt32(frame.Left, frame.Top,
                    Math.Max(0, frame.Right - frame.Left), Math.Max(0, frame.Bottom - frame.Top));
            var position = appWindow.Position;
            var size = appWindow.Size;
            return new RectInt32(position.X, position.Y, size.Width, size.Height);
        }
        catch (Exception ex)
        {
            LogPlacementSkipped(nameof(GetWindowBounds), "read", ex);
            return default;
        }
    }

    public static RectInt32 GetWorkArea(RectInt32 anchor)
        => DisplayArea.GetFromRect(anchor, DisplayAreaFallback.Nearest).WorkArea;

    public static RectInt32 GetWorkArea(Window window)
    {
        if (!TryGetPlaceable(window, nameof(GetWorkArea), out var appWindow, out _))
        {
            var fallback = Platform.System.ScreenService.WorkArea;
            return new RectInt32(fallback.X, fallback.Y, fallback.Width, fallback.Height);
        }
        try { return DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea; }
        catch (Exception ex)
        {
            LogPlacementSkipped(nameof(GetWorkArea), "display-area", ex);
            var fallback = Platform.System.ScreenService.WorkArea;
            return new RectInt32(fallback.X, fallback.Y, fallback.Width, fallback.Height);
        }
    }

    public static double GetDpiScale(RectInt32 anchor)
        => ScreenService.FromRect(new(anchor.X, anchor.Y, anchor.Width, anchor.Height)).DpiScale;

    public static Platform.System.ScreenRect GetClientBounds(IntPtr hwnd)
    {
        var origin = new NativeMethods.POINT();
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var bounds) || !ClientToScreen(hwnd, ref origin))
            return ScreenService.FromWindow(hwnd).Bounds;
        return new(origin.X, origin.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    public static void MoveToMonitor(Window window, ScreenInfo target)
    {
        if (!TryGetPlaceable(window, nameof(MoveToMonitor), out var appWindow, out var hwnd)) return;
        try
        {
            if (ScreenService.FromWindow(hwnd).Handle == target.Handle) return;
            appWindow.Move(new PointInt32(target.WorkArea.X, target.WorkArea.Y));
        }
        catch (Exception ex) { LogPlacementSkipped(nameof(MoveToMonitor), "move", ex); }
    }

    public static void CenterOnOwner(Window window, Window owner)
    {
        if (!TryGetPlaceable(window, nameof(CenterOnOwner), out var appWindow, out _)) return;
        var ownerHwnd = GetHwnd(owner);
        if (ownerHwnd == IntPtr.Zero) return;
        var target = ScreenService.FromWindow(ownerHwnd);
        double previousDpi = GetDpiScale(window);
        if (previousDpi <= 0) previousDpi = 1;
        var size = appWindow.Size;
        MoveToMonitor(window, target);
        int width = (int)Math.Ceiling(size.Width / previousDpi * target.DpiScale);
        int height = (int)Math.Ceiling(size.Height / previousDpi * target.DpiScale);
        var work = target.WorkArea;
        var fit = new ScreenRect(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height).FitInside(work);
        Place(window, fit.X, fit.Y, fit.Width, fit.Height);
    }

    public static void KeepVisible(Window window)
    {
        if (!TryGetPlaceable(window, nameof(KeepVisible), out var appWindow, out _)) return;
        var pos = appWindow.Position;
        var size = appWindow.Size;
        var rect = new ScreenRect(pos.X, pos.Y, size.Width, size.Height);
        var fit = rect.FitInside(ScreenService.FromRect(rect).WorkArea);
        if (rect != fit) Place(window, fit.X, fit.Y, fit.Width, fit.Height);
    }

    /// <summary>Keep resizing available inside a clipped borderless client surface.</summary>
    public static void EnableClientResize(Window window)
    {
        var root = (FrameworkElement)window.Content;
        var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        var kinds = new[] { Microsoft.UI.Input.NonClientRegionKind.LeftBorder, Microsoft.UI.Input.NonClientRegionKind.RightBorder,
            Microsoft.UI.Input.NonClientRegionKind.TopBorder, Microsoft.UI.Input.NonClientRegionKind.BottomBorder };
        (int Width, int Height, int Grip, bool Maximized) previous = default;
        void Update(object sender, object args)
        {
            if (root.XamlRoot is null) return;
            double dpi = root.XamlRoot.RasterizationScale;
            int width = (int)Math.Round(root.ActualWidth * dpi), height = (int)Math.Round(root.ActualHeight * dpi);
            int grip = (int)Math.Ceiling(7 * dpi);
            bool maximized = window.AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
            if (width <= 0 || height <= 0 || previous == (width, height, grip, maximized)) return;
            previous = (width, height, grip, maximized);
            if (maximized)
            {
                foreach (var kind in kinds) input.ClearRegionRects(kind);
                return;
            }
            input.SetRegionRects(kinds[0], new[] { new RectInt32(0, 0, grip, height) });
            input.SetRegionRects(kinds[1], new[] { new RectInt32(width - grip, 0, grip, height) });
            input.SetRegionRects(kinds[2], new[] { new RectInt32(0, 0, width, grip) });
            input.SetRegionRects(kinds[3], new[] { new RectInt32(0, height - grip, width, grip) });
        }
        root.LayoutUpdated += Update;
        window.Closed += (_, _) => root.LayoutUpdated -= Update;
    }

    public static PointInt32 GetPopupPosition(RectInt32 anchor, int width, int height, int gap)
    {
        var work = GetWorkArea(anchor);
        int x = Math.Clamp(anchor.X + (anchor.Width - width) / 2,
            work.X, work.X + Math.Max(0, work.Width - width));
        int y = anchor.Y - height - gap;
        if (y < work.Y) y = anchor.Y + anchor.Height + gap;
        return new PointInt32(x, Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height)));
    }

    public static Action TrackAnchor(Window popup, IntPtr ownerHwnd, Action reanchor)
    {
        var owner = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        bool detached = false, pending = false;
        void Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (detached || pending || (!args.DidPositionChange && !args.DidSizeChange)) return;
            pending = true;
            if (!popup.DispatcherQueue.TryEnqueue(() =>
            {
                pending = false;
                if (!detached) reanchor();
            })) pending = false;
        }
        owner.Changed += Changed;
        return () => { detached = true; owner.Changed -= Changed; };
    }

    /// <summary>
    /// Clip the native window and its acrylic backdrop to the painted XAML cards. A transparent
    /// XAML background alone still leaves acrylic in margins, rounded corners and toolbar gaps.
    /// </summary>
    public static void ClipToSurfaces(Window window, Func<IEnumerable<Border>> getSurfaces)
    {
        (window.SystemBackdrop as ChromeBackdrop)?.TrackSurfaces(getSurfaces);
        var clippedFrame = ClippedFrames.GetValue(window, static current => new ClippedWindowFrame(current));
        clippedFrame.StopSurfaceTracking?.Invoke();
        var root = (FrameworkElement)window.Content;
        string lastRegion = null;
        bool closed = false, pending = false;
        void Update(object sender, object args)
        {
            (window.SystemBackdrop as ChromeBackdrop)?.TrackSurfaces(getSurfaces);
            if (root.XamlRoot is null) return;
            var hwnd = GetHwnd(window);
            if (!GetWindowRect(hwnd, out var frame)) return;
            var clientOrigin = new NativeMethods.POINT();
            if (!ClientToScreen(hwnd, ref clientOrigin)) return;
            double dpi = root.XamlRoot.RasterizationScale;
            var rects = new List<SurfaceRect>();
            foreach (var surface in getSurfaces())
            {
                if (surface.Visibility != Visibility.Visible || surface.ActualWidth <= 0 || surface.ActualHeight <= 0) continue;
                var bounds = surface.TransformToVisual(root).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                double scale = bounds.Width / surface.ActualWidth;
                int x = clientOrigin.X - frame.Left, y = clientOrigin.Y - frame.Top;
                rects.Add(new SurfaceRect(x + bounds.Left * dpi, y + bounds.Top * dpi,
                    x + bounds.Right * dpi, y + bounds.Bottom * dpi,
                    surface.CornerRadius.TopLeft * scale * dpi, surface.CornerRadius.TopRight * scale * dpi,
                    surface.CornerRadius.BottomRight * scale * dpi, surface.CornerRadius.BottomLeft * scale * dpi));
            }
            if (rects.Count == 0) return; // wait for the first real layout
            string key = string.Join(";", rects);
            if (key == lastRegion) return;
            var region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) return;
            try
            {
                foreach (var r in rects)
                {
                    // Match pixel centres to the actual fractional XAML bounds and each corner.
                    // GDI RoundRectRgn rounds radii differently and leaves a one-pixel backdrop
                    // fringe at fractional DPI / Viewbox scales.
                    foreach (var strip in SurfaceStrips(r))
                    {
                        var part = CreateRectRgn(strip.X, strip.Y, strip.X + strip.Width, strip.Y + strip.Height);
                        if (part == IntPtr.Zero) return;
                        try { if (CombineRgn(region, region, part, 2 /* RGN_OR */) == 0) return; }
                        finally { NativeMethods.DeleteObject(part); }
                    }
                }
                if (SetWindowRgn(hwnd, region, true) == 0) return;
                region = IntPtr.Zero; // Windows owns the region after a successful SetWindowRgn.
                lastRegion = key;
            }
            finally { if (region != IntPtr.Zero) NativeMethods.DeleteObject(region); }
        }
        void OnLayoutUpdated(object sender, object args)
        {
            if (closed || pending) return;
            pending = true;
            window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                pending = false;
                if (!closed) Update(null, null);
            });
        }
        root.LayoutUpdated += OnLayoutUpdated;
        clippedFrame.StopSurfaceTracking = () => { closed = true; root.LayoutUpdated -= OnLayoutUpdated; };
    }

    private static IEnumerable<RectInt32> SurfaceStrips(SurfaceRect r)
    {
        double maxRadius = Math.Max(0, Math.Min(r.Right - r.Left, r.Bottom - r.Top) / 2);
        double tl = Math.Clamp(r.TopLeft, 0, maxRadius), tr = Math.Clamp(r.TopRight, 0, maxRadius);
        double bl = Math.Clamp(r.BottomLeft, 0, maxRadius), br = Math.Clamp(r.BottomRight, 0, maxRadius);
        RectInt32 pending = default;
        for (int y = (int)Math.Ceiling(r.Top - 0.5); y < (int)Math.Ceiling(r.Bottom - 0.5); y++)
        {
            double cy = y + 0.5;
            double leftInset = Inset(cy, r.Top, r.Bottom, tl, bl);
            double rightInset = Inset(cy, r.Top, r.Bottom, tr, br);
            int left = (int)Math.Ceiling(r.Left + leftInset - 0.5);
            int right = (int)Math.Ceiling(r.Right - rightInset - 0.5);
            if (right <= left) continue;
            if (pending.Height > 0 && pending.X == left && pending.Width == right - left
                && pending.Y + pending.Height == y)
                pending.Height++;
            else
            {
                if (pending.Height > 0) yield return pending;
                pending = new RectInt32(left, y, right - left, 1);
            }
        }
        if (pending.Height > 0) yield return pending;

        static double Inset(double cy, double top, double bottom, double topRadius, double bottomRadius)
        {
            double radius, dy;
            if (topRadius > 0 && cy < top + topRadius)
            { radius = topRadius; dy = top + radius - cy; }
            else if (bottomRadius > 0 && cy > bottom - bottomRadius)
            { radius = bottomRadius; dy = cy - bottom + radius; }
            else return 0;
            return radius - Math.Sqrt(Math.Max(0, radius * radius - dy * dy));
        }
    }

    private static void PlaceCore(Window window, int x, int y, int width, int height)
    {
        if (!TryGetPlaceable(window, nameof(PlaceCore), out var current, out _)) return;
        try
        {
            if (current.Position.X != x || current.Position.Y != y || current.Size.Width != width || current.Size.Height != height)
                current.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex) { LogPlacementSkipped(nameof(PlaceCore), "move-and-resize", ex); }
    }

    /// <summary>Current monitor scale factor (logical DIPs per physical pixel).</summary>
    public static double GetDpiScale(Window window)
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(GetHwnd(window));
            return dpi is 0 ? 1.0 : dpi / 96.0;
        }
        catch { return 1.0; }
    }

    /// <summary>
    /// Place a window using XAML logical coordinates. AppWindow expects physical pixels,
    /// therefore convert using that window's own monitor DPI.
    /// </summary>
    public static void PlaceDip(Window window, double xDip, double yDip, double widthDip, double heightDip)
    {
        if (!TryGetPlaceable(window, nameof(PlaceDip), out _, out _)) return;
        double scale = GetDpiScale(window);
        PlaceClient(window,
            (int)Math.Round(xDip * scale), (int)Math.Round(yDip * scale),
            (int)Math.Ceiling(widthDip * scale), (int)Math.Ceiling(heightDip * scale));
        // Preserve DIP placement so first activation can reapply it at the settled monitor DPI.
        Placements.AddOrUpdate(window, new Placement { Kind = 1, X = xDip, Y = yDip, W = widthDip, H = heightDip });
    }

    /// <summary>
    /// Drag a borderless window from a designated surface. WinUI 3's island WndProc does not run
    /// the native HTCAPTION move loop, and it stops delivering PointerMoved once AppWindow.Move
    /// repositions the island mid-gesture, so a dispatcher timer polls the native cursor between
    /// press and release and moves the window by the physical-pixel delta (8 DIP tap threshold).
    /// <para>
    /// <paramref name="fromAnywhere"/> (WPF floating-bar feel): presses on buttons also arm the
    /// drag; once the threshold crosses, the surface steals pointer capture so the button's
    /// Click never fires, while a clean tap still clicks normally.
    /// </para>
    /// </summary>
    public static void EnableDragMove(Window window, UIElement dragSurface, Action onDragMoved = null, bool fromAnywhere = false,
        Action onDragCompleted = null)
    {
        bool pressed = false;
        bool dragging = false;
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs pressedArgs = null;
        NativeMethods.POINT originCursor = default;
        PointInt32 originPx = default;

        void Complete()
        {
            bool moved = dragging;
            var pointer = pressedArgs?.Pointer;
            pressed = false;
            dragging = false;
            pressedArgs = null;
            if (pointer is not null) dragSurface.ReleasePointerCapture(pointer);
            if (moved)
            {
                KeepVisible(window);
                onDragCompleted?.Invoke();
            }
        }

        // WinUI 3 stops delivering PointerMoved once AppWindow.Move repositions the island
        // mid-gesture, so the drag is polled from a dispatcher timer instead: press/release
        // start/stop it and the native cursor is the position source.
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            if (!pressed) { timer.Stop(); return; }
            if (pressedArgs?.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && NativeMethods.GetKeyState(0x01) >= 0)
            { Complete(); timer.Stop(); return; }
            NativeMethods.GetCursorPos(out var cur);
            int dx = cur.X - originCursor.X, dy = cur.Y - originCursor.Y;
            if (!dragging)
            {
                double threshold = 8 * GetDpiScale(window);
                if (dx * dx + dy * dy < threshold * threshold) return; // 8 DIP threshold
                dragging = true;
                // Steal capture from a pressed button so its Click is suppressed for this gesture.
                if (fromAnywhere && pressedArgs is not null) dragSurface.CapturePointer(pressedArgs.Pointer);
                onDragMoved?.Invoke();
            }
            window.AppWindow.Move(new PointInt32(originPx.X + dx, originPx.Y + dy));
        };

        void OnPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && !e.GetCurrentPoint(dragSurface).Properties.IsLeftButtonPressed) return;
            if (!fromAnywhere && HasInteractiveAncestor(e.OriginalSource as DependencyObject, dragSurface)) return;
            pressed = true;
            dragging = false;
            pressedArgs = e;
            NativeMethods.GetCursorPos(out originCursor);
            originPx = window.AppWindow.Position;
            if (!fromAnywhere) dragSurface.CapturePointer(e.Pointer);
            timer.Start();
        }

        void OnReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!pressed) return;
            timer.Stop();
            if (dragging) e.Handled = true;
            Complete();
            dragSurface.ReleasePointerCapture(e.Pointer);
        }

        // handledEventsToo so button-handled presses/releases are seen in fromAnywhere mode.
        dragSurface.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPressed), fromAnywhere);
        dragSurface.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnReleased), fromAnywhere);
        dragSurface.PointerCanceled += (_, _) => { Complete(); timer.Stop(); };
        dragSurface.PointerCaptureLost += (_, _) =>
        {
            // Moving the XAML island can drop capture while the mouse is still dragging.
            // Keep following the native cursor until button-up instead of ending after one step.
            if (pressedArgs?.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && NativeMethods.GetKeyState(0x01) < 0) return;
            Complete(); timer.Stop();
        };
        window.Closed += (_, _) => { pressed = false; pressedArgs = null; timer.Stop(); };
    }

    private static bool HasInteractiveAncestor(DependencyObject current, DependencyObject boundary)
    {
        while (current is not null)
        {
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.TextBox
                or Microsoft.UI.Xaml.Controls.ComboBox
                or Microsoft.UI.Xaml.Controls.Slider
                or Microsoft.UI.Xaml.Controls.ToggleSwitch)
                return true;
            if (ReferenceEquals(current, boundary)) break;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// Adds a draggable title row to a window that the original displayed as a movable window.
    /// The returned Grid owns both title bar and content; pass it as Window.Content.
    /// Mirrors the WPF iNKORE modern chrome: 32-DIP caption, title left, caption buttons right
    /// (minimize/maximize only where the original allowed resizing, close always).
    /// </summary>
    public static Grid AddTitleBar(Window window, string title, FrameworkElement content, bool closeButton = true, bool minMax = false,
        Microsoft.UI.Xaml.Media.Brush background = null)
    {
        var root = new Grid
        {
            // The window has no system backdrop; without this the client area behind transparent
            // children (e.g. a ScrollViewer) paints black.
            Background = background ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["IcSurfaceBrush"],
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
        };
        var header = new Grid
        {
            MinHeight = 32,
            Background = background ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["IcSurfaceBrush"],
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        header.Children.Add(titleText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        Microsoft.UI.Xaml.Controls.Button CaptionButton(string glyph, string tooltip)
        {
            var b = new Microsoft.UI.Xaml.Controls.Button
            {
                Content = new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(IconFontStyle.Family),
                    FontSize = 10,
                },
                MinWidth = 46,
                MinHeight = 32,
                Padding = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            };
            ToolTipService.SetToolTip(b, tooltip);
            return b;
        }

        void ToggleMaximize()
        {
            if (window.AppWindow.Presenter is OverlappedPresenter p)
            {
                if (p.State == OverlappedPresenterState.Maximized) p.Restore();
                else p.Maximize();
            }
        }

        if (minMax)
        {
            var min = CaptionButton("", "最小化");
            min.Click += (_, _) =>
            {
                if (window.AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
            };
            buttons.Children.Add(min);

            var max = CaptionButton("", "最大化");
            max.Click += (_, _) => ToggleMaximize();
            window.AppWindow.Changed += (_, args) =>
            {
                if (!args.DidSizeChange) return;
                bool maximized = window.AppWindow.Presenter is OverlappedPresenter p
                    && p.State == OverlappedPresenterState.Maximized;
                ((Microsoft.UI.Xaml.Controls.FontIcon)max.Content).Glyph = maximized ? "" : "";
                ToolTipService.SetToolTip(max, maximized ? "还原" : "最大化");
            };
            buttons.Children.Add(max);

            // Native caption semantics: double-click the caption toggles maximize.
            header.DoubleTapped += (_, e) =>
            {
                if (HasInteractiveAncestor(e.OriginalSource as DependencyObject, header)) return;
                ToggleMaximize();
                e.Handled = true;
            };
        }

        if (closeButton)
        {
            var close = new Microsoft.UI.Xaml.Controls.Button
            {
                Content = new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = "",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(IconFontStyle.Family),
                    FontSize = 12,
                },
                MinWidth = 46,
                MinHeight = 32,
                Padding = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            };
            close.Click += (_, _) => window.Close();
            close.PointerEntered += (_, _) =>
            {
                close.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35));
                close.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            };
            close.PointerExited += (_, _) =>
            {
                close.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                close.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
            };
            buttons.Children.Add(close);
        }

        EnableDragMove(window, header);

        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(header);
        root.Children.Add(content);
        return root;
    }

    /// <summary>
    /// Suppress touch/pen contact visuals and press-and-hold right-click feedback on a window
    /// (same set the Win32 overlay suppresses) so classroom touch writes without rings/dots.
    /// </summary>
    public static void SuppressTouchFeedback(Window window)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return;
        int disabled = 0;
        uint[] kinds = { 1, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        foreach (var kind in kinds)
            SetWindowFeedbackSetting(hwnd, kind, 0, sizeof(int), ref disabled);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowFeedbackSetting(IntPtr h, uint feedback, uint flags, uint size, ref int configuration);

    /// <summary>0 = follow system, 1 = light, 2 = dark.</summary>
    public static void ApplyTheme(Window window, int theme)
    {
        if (window.Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        (window.SystemBackdrop as ChromeBackdrop)?.SetTheme(theme);
        var hwnd = GetHwnd(window);
        if ((NativeMethods.GetWindowLongW(hwnd, -16) & 0x00C00000) == 0x00C00000)
        {
            int dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
    }

    /// <summary>
    /// 把浮窗挂到当前画布窗口之下（owner）。全屏置顶的板窗口若只是同层 topmost，
    /// 仍可能压住浮窗；owned window 始终位于 owner 之上。AppWindow 无 owner API，用 GWLP_HWNDPARENT。
    /// </summary>
    public static void SetOwner(Window window, IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero) return;
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero || hwnd == ownerHwnd) return;
        SetWindowLongPtrW(hwnd, -8 /* GWLP_HWNDPARENT */, ownerHwnd);
    }

    /// <summary>读取 Win32 owner（GWLP_HWNDPARENT），用于诊断浮窗 owner 是否生效。</summary>
    public static IntPtr GetOwner(Window window)
    {
        var hwnd = GetHwnd(window);
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetWindowLongPtrW(hwnd, -8 /* GWLP_HWNDPARENT */);
    }

    /// <summary>
    /// Re-assert topmost without activating. Both the overlay and the chrome are topmost, so
    /// this keeps the chrome above the ink surface without stealing focus from a slideshow.
    /// </summary>
    public static void EnsureAbove(Window window, IntPtr overlay = default)
    {
        if (!TryGetPlaceable(window, nameof(EnsureAbove), out var appWindow, out var hwnd)) return;
        try
        {
            if (appWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: false }) return;
        }
        catch (Exception ex)
        {
            LogPlacementSkipped(nameof(EnsureAbove), "presenter", ex);
            return;
        }
        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        if (overlay != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindowVisible(overlay)) return;
            // Preserve popups (especially IME candidates) above our chrome. Only repair Z
            // order when the overlay actually covers this window, never raise it every poll.
            bool belowOverlay = false;
            for (var above = NativeMethods.GetWindow(hwnd, 3 /* GW_HWNDPREV */);
                 above != IntPtr.Zero; above = NativeMethods.GetWindow(above, 3))
            {
                if (above != overlay) continue;
                belowOverlay = true;
                break;
            }
            if (!belowOverlay) return;
        }
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static void CenterOnPrimary(Window window, int widthDip, int heightDip)
    {
        if (!TryGetPlaceable(window, nameof(CenterOnPrimary), out _, out _)) return;
        Placements.AddOrUpdate(window, new Placement { Kind = 2, W = widthDip, H = heightDip });
        double scale = GetDpiScale(window);
        var work = Platform.System.ScreenService.WorkArea;
        int widthPx = (int)Math.Ceiling(widthDip * scale);
        int heightPx = (int)Math.Ceiling(heightDip * scale);
        PlaceCore(window, work.X + (work.Width - widthPx) / 2,
                      work.Y + (work.Height - heightPx) / 2, widthPx, heightPx);
    }

    /// <summary>
    /// Configure rounded borderless content without extending a glass frame into XAML.
    /// Card-shaped tools install ClippedWindowFrame when their native region is attached.
    /// </summary>
    public static void SetRoundedCorners(Window window)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return;
        int preference = 2; // DWMWCP_ROUND
        int policy = 1;     // DWMNCRP_DISABLED: XAML owns the entire borderless client surface.
        int borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no rectangular white halo
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        DwmSetWindowAttribute(hwnd, 2 /* DWMWA_NCRENDERING_POLICY */, ref policy, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
}
