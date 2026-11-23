using InkCanvas.Core.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.Graphics.Canvas;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;

namespace InkCanvas.Features.Tools;

/// <summary>Countdown with pause/resume. Ticks arrive on a thread-pool thread; callers marshal.</summary>
public sealed class CountdownTimer : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private DateTime _endTime;
    private TimeSpan _pausedRemaining;
    private bool _running;

    public CountdownTimer()
    {
        _timer = new System.Threading.Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan Total { get; private set; }

    public TimeSpan Remaining => _running
        ? Max(TimeSpan.Zero, _endTime - DateTime.UtcNow)
        : _pausedRemaining;

    public bool IsRunning => _running;

    public event Action<TimeSpan> Tick;
    public event Action Finished;

    public void Start(TimeSpan duration)
    {
        Total = duration;
        _pausedRemaining = duration;
        Resume();
    }

    public void Resume()
    {
        if (_running || _pausedRemaining <= TimeSpan.Zero) return;
        _endTime = DateTime.UtcNow + _pausedRemaining;
        _running = true;
        _timer.Change(0, 100);
    }

    public void Pause()
    {
        if (!_running) return;
        _pausedRemaining = Remaining;
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Stop()
    {
        _running = false;
        _pausedRemaining = TimeSpan.Zero;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick()
    {
        if (!_running) return;
        var remaining = Remaining;
        Tick?.Invoke(remaining);
        if (remaining <= TimeSpan.Zero)
        {
            Stop();
            Finished?.Invoke();
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void Dispose() => _timer.Dispose();
}

public sealed record LotteryDraw(DateTimeOffset Time, IReadOnlyList<string> Names);

/// <summary>Screen capture to PNG, used by the screenshot tool and PPT auto-save.</summary>
public static class ScreenshotService
{
    public static (byte[] bgra, int width, int height) Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return (Array.Empty<byte>(), 0, 0);
        var pixels = new byte[checked(width * height * 4)];
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法访问桌面截图表面。");
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) throw new InvalidOperationException("无法创建截图设备上下文。");
            bmp = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bmp == IntPtr.Zero) throw new InvalidOperationException("无法分配截图位图。");
            var selected = NativeMethods.SelectObject(memDc, bmp);
            if (selected == IntPtr.Zero || selected == new IntPtr(-1)) throw new InvalidOperationException("无法初始化截图位图。");
            old = selected;
            // CAPTUREBLT includes the transparent layered annotation window on every monitor.
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, 0x00CC0020 | 0x40000000))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取桌面截图。");

            // GetDIBits requires the bitmap to be deselected from its device context.
            NativeMethods.SelectObject(memDc, old);
            old = IntPtr.Zero;
            var bi = new NativeMethods.BITMAPINFO
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFO>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            if (NativeMethods.GetDIBits(memDc, bmp, 0, (uint)height, pixels, ref bi, 0) != height)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "截图像素读取不完整。");
            // GDI's unused fourth byte is not an alpha channel. The desktop image is opaque.
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return (pixels, width, height);
        }
        finally
        {
            if (old != IntPtr.Zero) NativeMethods.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) NativeMethods.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static async Task SavePngAsync(string path, byte[] bgra, int width, int height)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var device = CanvasDevice.GetSharedDevice();
        using var bitmap = CanvasBitmap.CreateFromBytes(device, bgra, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }

    public static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), bgra, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var bytes = new byte[checked((int)stream.Size)];
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length); reader.ReadBytes(bytes);
        return bytes;
    }

    public static async Task<string> CaptureToFileAsync(string directory, int x, int y, int width, int height,
        bool dateFolders = false)
    {
        var (pixels, w, h) = Capture(x, y, width, height);
        if (w == 0) return null;
        string dir = dateFolders ? Path.Combine(directory, DateTime.Now.ToString("yyyy-MM-dd")) : directory;
        string path = Path.Combine(dir, $"Screenshot-{DateTime.Now:HHmmss}.png");
        await SavePngAsync(path, pixels, w, h);
        return path;
    }
}
