using System.Numerics;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;

namespace InkCanvas.App.Views;

/// <summary>
/// 放大镜。系统 Magnification API 需要以原生子窗口作为放大宿主，直接嵌入 WinUI 3 的
/// XAML 树较复杂且缺少实机验证，因此这里采用稳定的降级路径：以约 30fps 定时对鼠标周围的
/// <b>小范围</b>区域做 BitBlt 截图（避免高频全屏截图），再用 Win2D 放大绘制到圆形取景框。
/// 支持倍率 1.5x/2x/3x/4x、尺寸调整、跟随/固定鼠标、Esc 退出，并按窗口 DPI 适配。
/// </summary>
public sealed class MagnifierWindow : Window
{
    private const double ToolbarHeight = 52;

    private static readonly bool MagnificationApiAvailable = ProbeMagnification();

    private readonly Grid _root = new();
    private readonly CanvasControl _lens;
    private readonly Border _lensHost;
    private readonly DispatcherQueueTimer _timer;
    private CanvasBitmap _frame;
    private double _zoom = 2.0;
    private double _lensSize = 260;
    private bool _follow = true;
    private int _lastX;
    private int _lastY;
    private bool _hasLast;
    private bool _disposed;
    private readonly bool _aiAvailable;
    private Button _askAi;
    public void SetAiAvailable(bool available) { if (_askAi is not null) _askAi.Visibility = available ? Visibility.Visible : Visibility.Collapsed; }
    private ScreenRect _sourceRect;

    /// <summary>请求把当前取景贴到白板（false）或交给 AI 识别（true）。</summary>
    public event Action<bool> CaptureRequested;

    /// <summary>当前取景的屏幕像素矩形；尚未取景时为空矩形。</summary>
    public ScreenRect CurrentScreenRect => _sourceRect;

    public int PositionX => AppWindow.Position.X;

    public int PositionY => AppWindow.Position.Y;

    public void MoveTo(int x, int y)
    {
        try { AppWindow.Move(new PointInt32(x, y)); }
        catch (Exception) { }
    }

    /// <summary>截图前隐藏透镜窗口，避免取景框本身被截进取景内容。</summary>
    public void HideForCapture()
    {
        InkCanvas.Platform.Interop.NativeMethods.ShowWindow(
            WindowChrome.GetHwnd(this), InkCanvas.Platform.Interop.NativeMethods.SW_HIDE);
    }

    public void ShowAfterCapture()
    {
        InkCanvas.Platform.Interop.NativeMethods.ShowWindow(
            WindowChrome.GetHwnd(this), InkCanvas.Platform.Interop.NativeMethods.SW_SHOWNOACTIVATE);
    }

    public MagnifierWindow(bool aiAvailable = false)
    {
        _aiAvailable = aiAvailable;
        Title = "放大镜";

        _lens = new CanvasControl { ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _lens.Draw += OnDraw;

        _lensHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(999),
            Child = _lens,
        };

        var lensRow = new Grid();
        lensRow.Children.Add(_lensHost);

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = BuildToolbar();
        Grid.SetRow(lensRow, 0);
        Grid.SetRow(toolbar, 1);
        _root.Children.Add(lensRow);
        _root.Children.Add(toolbar);

        var outer = new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8, 8, 8, 6),
            Child = _root,
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        outer.KeyboardAccelerators.Add(escape);

        Content = outer;

        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        LayoutLens();
        WindowChrome.CenterOnPrimary(this, (int)(_lensSize + 20), (int)(_lensSize + ToolbarHeight + 20));

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        FileLog.WriteLogToFile(
            $"放大镜：Magnification API {(MagnificationApiAvailable ? "可用" : "不可用")}，采用降级截图放大路径",
            LogType.Info);

        Closed += (_, _) => DisposeResources();
    }

    private FrameworkElement BuildToolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };

        foreach (double zoom in new[] { 1.5, 2.0, 3.0, 4.0 })
        {
            double captured = zoom;
            var button = SmallButton($"{zoom:0.#}x");
            button.Click += (_, _) =>
            {
                _zoom = captured;
                _frame?.Dispose();
                _frame = null;
            };
            row.Children.Add(button);
        }

        var smaller = SmallButton("−");
        smaller.Click += (_, _) => ResizeLens(-40);
        var bigger = SmallButton("＋");
        bigger.Click += (_, _) => ResizeLens(40);

        var follow = SmallButton("固定");
        follow.Click += (_, _) =>
        {
            _follow = !_follow;
            follow.Content = _follow ? "固定" : "跟随";
        };

        var close = SmallButton("✕");
        close.Click += (_, _) => Close();

        var paste = SmallButton("贴图");
        paste.Click += (_, _) => CaptureRequested?.Invoke(false);

        var ask = SmallButton("问 AI");
        _askAi = ask;
        SetAiAvailable(_aiAvailable);
        ask.Click += (_, _) => CaptureRequested?.Invoke(true);

        row.Children.Add(smaller);
        row.Children.Add(bigger);
        row.Children.Add(follow);
        row.Children.Add(paste);
        row.Children.Add(ask);
        row.Children.Add(close);
        return row;
    }

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        MinWidth = 40,
        MinHeight = 34,
        Padding = new Thickness(6, 0, 6, 0),
        FontSize = 12,
        CornerRadius = new CornerRadius(6),
    };

    private void ResizeLens(double delta)
    {
        _lensSize = Math.Clamp(_lensSize + delta, 160, 480);
        LayoutLens();
        WindowChrome.CenterOnPrimary(this, (int)(_lensSize + 20), (int)(_lensSize + ToolbarHeight + 20));
    }

    private void LayoutLens()
    {
        _lensHost.Width = _lensSize;
        _lensHost.Height = _lensSize;
        _lens.Width = _lensSize;
        _lens.Height = _lensSize;

    }

    private void OnTick()
    {
        if (_disposed) return;
        var device = _lens.Device;
        if (device is null) return;
        if (!GetCursorPos(out POINT cursor)) return;

        try
        {
            double dpi = WindowChrome.GetDpiScale(this);
            if (dpi <= 0) dpi = 1.0;

            int lensPx = Math.Max(24, (int)Math.Round(_lensSize * dpi));
            int srcSize = Math.Max(16, (int)Math.Round(lensPx / _zoom));

            var screen = ScreenService.Virtual;
            int sx = cursor.X - srcSize / 2;
            int sy = cursor.Y - srcSize / 2;
            sx = Math.Clamp(sx, screen.X, Math.Max(screen.X, screen.X + screen.Width - srcSize));
            sy = Math.Clamp(sy, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - srcSize));

            var (pixels, width, height) = ScreenshotService.Capture(sx, sy, srcSize, srcSize);
            if (width <= 0 || height <= 0) return;

            _sourceRect = new ScreenRect(sx, sy, width, height);

            var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _frame?.Dispose();
            _frame = bitmap;
            _lens.Invalidate();

            if (_follow) FollowCursor(cursor, dpi);
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"放大镜取景失败：{ex.Message}", LogType.Error);
        }
    }

    private void FollowCursor(POINT cursor, double dpi)
    {
        if (_hasLast && Math.Abs(cursor.X - _lastX) < 2 && Math.Abs(cursor.Y - _lastY) < 2) return;
        _lastX = cursor.X;
        _lastY = cursor.Y;
        _hasLast = true;

        var size = AppWindow.Size;
        var work = ScreenService.WorkArea;
        int x = cursor.X + (int)Math.Round(28 * dpi);
        int y = cursor.Y + (int)Math.Round(40 * dpi);
        x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - size.Width));
        y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - size.Height));
        AppWindow.Move(new PointInt32(x, y));
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_frame is null) return;
        var destination = new Rect(0, 0, sender.ActualWidth, sender.ActualHeight);
        var source = new Rect(0, 0, _frame.SizeInPixels.Width, _frame.SizeInPixels.Height);
        float radius = (float)(Math.Min(sender.ActualWidth, sender.ActualHeight) / 2.0) - 2f;
        if (radius <= 1f) { args.DrawingSession.DrawImage(_frame, destination, source); return; }

        using var circle = CanvasGeometry.CreateCircle(
            _frame.Device, new Vector2((float)sender.ActualWidth / 2f, (float)sender.ActualHeight / 2f), radius);
        using var layer = args.DrawingSession.CreateLayer(1f, circle);
        args.DrawingSession.DrawImage(_frame, destination, source);
    }

    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _frame?.Dispose();
        _frame = null;
    }

    private static bool ProbeMagnification()
    {
        try
        {
            return NativeLibrary.TryLoad("Magnification.dll", out _);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
