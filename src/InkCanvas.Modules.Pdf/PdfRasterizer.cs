using System.Runtime.InteropServices.WindowsRuntime;
using System.Numerics;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;
using Rect = Windows.Foundation.Rect;

namespace InkCanvas.Modules.Pdf;

/// <summary>Serial native PDF rendering with bounded previews and explicit stream lifetime.</summary>
internal sealed class PdfRasterizer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (byte[] Data, long Used)> _previews = new();
    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    private Windows.Data.Pdf.PdfDocument _document;
    private PdfNativeRenderer _renderer;
    private long _clock;
    private bool _disposed;
    public long CachedBytes => _previews.Values.Sum(item => (long)item.Data.Length);
    internal CanvasDevice Device => _renderer.Device;

    private PdfRasterizer(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _stream = _file.AsRandomAccessStream();
    }
    public static Task<PdfRasterizer> OpenAsync(PdfAnnotationFile file, CancellationToken token)
        => OpenAsync(file.RenderPath, file.Password, token);
    internal static async Task<PdfRasterizer> OpenAsync(string path, string password, CancellationToken token)
    {
        var renderer = new PdfRasterizer(path);
        try
        {
            token.ThrowIfCancellationRequested();
            renderer._document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(renderer._stream, password ?? "").AsTask().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            renderer._renderer = new PdfNativeRenderer();
            return renderer;
        }
        catch { await renderer.DisposeAsync(); throw; }
    }
    public async Task<byte[]> PreviewAsync(int index, PdfPageGeometry geometry, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previews.TryGetValue(index, out var cached)) { _previews[index] = (cached.Data, ++_clock); return cached.Data; }
            double scale = Math.Min(1.4, 1400 / Math.Max(geometry.DipWidth, geometry.DipHeight));
            var data = await RenderCore(index, geometry, new Rect(0, 0, geometry.DipWidth, geometry.DipHeight), scale, token);
            _previews[index] = (data, ++_clock);
            while (_previews.Count > 3 || CachedBytes > 16 * 1024 * 1024)
            {
                int oldest = _previews.MinBy(pair => pair.Value.Used).Key;
                _previews.Remove(oldest);
            }
            return data;
        }
        finally { _gate.Release(); }
    }
    public async Task<byte[]> RenderAsync(int index, PdfPageGeometry geometry, Rect source, double scale, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await RenderCore(index, geometry, source, scale, token); }
        finally { _gate.Release(); }
    }
    private async Task<byte[]> RenderCore(int index, PdfPageGeometry geometry, Rect source, double scale, CancellationToken token)
        => await RenderCore(index, geometry, source, scale, _renderer.RenderAsync, token).ConfigureAwait(false);

    internal async Task<CanvasBitmap> RenderBitmapAsync(int index, PdfPageGeometry geometry, Rect source, double scale, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await RenderCore(index, geometry, source, scale, _renderer.RenderBitmapAsync, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RenderCore<T>(int index, PdfPageGeometry geometry, Rect source, double scale,
        Func<Windows.Data.Pdf.PdfPage, Vector4, uint, uint, CancellationToken, Task<T>> render, CancellationToken token)
    {
        using var page = _document.GetPage((uint)index);
        // Keep both the GPU upload and the native output allocation below 32 MB.
        scale = Math.Min(scale, Math.Sqrt(8_000_000 / Math.Max(1, source.Width * source.Height)));
        scale = Math.Min(scale, 8192 / Math.Max(source.Width, source.Height));
        var rectangle = new Vector4((float)(source.X * page.Size.Width / geometry.DipWidth),
            (float)(source.Y * page.Size.Height / geometry.DipHeight),
            (float)((source.X + source.Width) * page.Size.Width / geometry.DipWidth),
            (float)((source.Y + source.Height) * page.Size.Height / geometry.DipHeight));
        await page.PreparePageAsync().AsTask().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        uint width = (uint)Math.Max(1, Math.Ceiling(source.Width * scale)), height = (uint)Math.Max(1, Math.Ceiling(source.Height * scale));
        return await render(page, rectangle, width, height, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true; _previews.Clear(); _document = null;
            _renderer?.Dispose(); _renderer = null;
            _stream.Dispose(); _file.Dispose();
        }
        finally { _gate.Release(); }
    }
}
