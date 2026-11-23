using System.Numerics;
using InkCanvas.Core.Imaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Rect = InkCanvas.Core.Ink.Rect;

namespace InkCanvas.Ink.Rendering;

/// <summary>Decodes each image once and shares its bitmap or SVG document between frames and clones.</summary>
public sealed class ImageSceneRenderer : IDisposable
{
    private sealed class Entry : IDisposable
    {
        public readonly CancellationTokenSource Cancellation = new();
        public byte[] Data;
        public CanvasBitmap Bitmap;
        public CanvasSvgDocument Vector;
        public Vector2 VectorSize;
        public string ContentType;
        public Task Loading;
        public long LastUsed;
        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
            Bitmap?.Dispose();
            Vector?.Dispose();
        }
    }

    private readonly Dictionary<byte[], Entry> _images = new();
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    private bool _disposed;
    private long _clock;
    private const long WarmBitmapBytes = 64 * 1024 * 1024;
    private const int WarmImageCount = 12;
    public long CachedBitmapBytes => _images.Values.Sum(entry => entry.Bitmap is null ? 0
        : (long)entry.Bitmap.SizeInPixels.Width * entry.Bitmap.SizeInPixels.Height * 4);
    public int CachedBitmapCount => _images.Values.Count(entry => entry.Bitmap is not null || entry.Vector is not null);
    public int CachedVectorCount => _images.Values.Count(entry => entry.Vector is not null);
    public event Action Changed;
    public uint MaximumDecodeDimension { get; init; }

    public static IEnumerable<IImageSelectionElement> Images(InkController controller) =>
        controller.GetSelectableElements?.Invoke().OfType<IImageSelectionElement>()
            .Where(image => image.IsPresent && image.ImageData is { Length: > 0 }) ?? Enumerable.Empty<IImageSelectionElement>();

    public Task PrepareAsync(CanvasDevice device, InkController controller) => PrepareAsync(device, Images(controller));

    public async Task PrepareAsync(CanvasDevice device, IEnumerable<IImageSelectionElement> images)
    {
        var loads = images.Select(image => Get(device, image).Loading).Distinct().ToArray();
        await Task.WhenAll(loads);
    }

    public void Draw(CanvasDrawingSession session, InkController controller, Matrix3x2 view)
    {
        var images = Images(controller).ToArray();
        Retain(images);
        foreach (var image in images) Draw(session, image, view);
    }

    public void Draw(CanvasDrawingSession session, IImageSelectionElement image, Matrix3x2 view)
        => Draw(session, image, view, null);

    public void Draw(CanvasDrawingSession session, IImageSelectionElement image, Matrix3x2 view, Rect? viewport)
    {
        if (viewport is { } area && !IsNearViewport(image, view, area)) return;
        var entry = Get(session.Device, image);
        if (entry.Bitmap is null && entry.Vector is null) return;
        var previous = session.Transform;
        try
        {
            var frame = image.Frame;
            if (entry.Vector is not null)
            {
                session.Transform = Matrix3x2.CreateScale(frame.Bounds.Width / entry.VectorSize.X, frame.Bounds.Height / entry.VectorSize.Y)
                    * Matrix3x2.CreateTranslation(frame.Bounds.Left, frame.Bounds.Top) * frame.Transform * view;
                using var opacity = session.CreateLayer(Math.Clamp(image.Opacity, 0, 1));
                session.DrawSvg(entry.Vector, new Windows.Foundation.Size(entry.VectorSize.X, entry.VectorSize.Y));
                return;
            }
            session.Transform = frame.Transform * view;
            session.DrawImage(entry.Bitmap,
                new Windows.Foundation.Rect(frame.Bounds.Left, frame.Bounds.Top, frame.Bounds.Width, frame.Bounds.Height),
                new Windows.Foundation.Rect(0, 0, entry.Bitmap.Size.Width, entry.Bitmap.Size.Height),
                Math.Clamp(image.Opacity, 0, 1), CanvasImageInterpolation.Linear);
            var annotations = image.ImageAnnotations;
            if (annotations.Count == 0) return;
            using var annotationOpacity = session.CreateLayer(Math.Clamp(image.Opacity, 0, 1));
            var color = Windows.UI.Color.FromArgb(255, 39, 128, 235);
            foreach (var line in annotations)
                for (int index = 1; index < line.Count; index++)
                    session.DrawLine(
                        frame.Bounds.Left + (float)line[index - 1].X * frame.Bounds.Width,
                        frame.Bounds.Top + (float)line[index - 1].Y * frame.Bounds.Height,
                        frame.Bounds.Left + (float)line[index].X * frame.Bounds.Width,
                        frame.Bounds.Top + (float)line[index].Y * frame.Bounds.Height, color, 2f);
        }
        finally { session.Transform = previous; }
    }

    public void Retain(InkController controller) => Retain(Images(controller));

    public void TrimOutsideViewport(InkController controller, Matrix3x2 view, Rect viewport)
    {
        long bytes = CachedBitmapBytes;
        if (_images.Count <= WarmImageCount && bytes <= WarmBitmapBytes) return;
        var visible = Images(controller).Where(image => IsNearViewport(image, view, viewport))
            .Select(image => image.ImageData).ToHashSet();
        foreach (var entry in _images.Values.Where(entry => !visible.Contains(entry.Data)).OrderBy(entry => entry.LastUsed).ToArray())
        {
            if (_images.Count <= WarmImageCount && bytes <= WarmBitmapBytes) break;
            if (entry.Bitmap is not null) bytes -= (long)entry.Bitmap.SizeInPixels.Width * entry.Bitmap.SizeInPixels.Height * 4;
            entry.Dispose();
            _images.Remove(entry.Data);
        }
    }

    private static bool IsNearViewport(IImageSelectionElement image, Matrix3x2 view, Rect viewport)
    {
        var frame = image.Frame;
        var transform = frame.Transform * view;
        var first = Vector2.Transform(new(frame.Bounds.Left, frame.Bounds.Top), transform);
        var second = Vector2.Transform(new(frame.Bounds.Right, frame.Bounds.Top), transform);
        var third = Vector2.Transform(new(frame.Bounds.Right, frame.Bounds.Bottom), transform);
        var fourth = Vector2.Transform(new(frame.Bounds.Left, frame.Bounds.Bottom), transform);
        var minimum = Vector2.Min(Vector2.Min(first, second), Vector2.Min(third, fourth));
        var maximum = Vector2.Max(Vector2.Max(first, second), Vector2.Max(third, fourth));
        return new Rect(minimum.X, minimum.Y, maximum.X, maximum.Y).Intersects(viewport.Inflate(128));
    }

    private void Retain(IEnumerable<IImageSelectionElement> images)
    {
        var present = images.Select(image => image.ImageData).ToHashSet();
        foreach (var data in _images.Keys.Where(data => !present.Contains(data)).ToArray())
        {
            _images[data].Dispose();
            _images.Remove(data);
        }
    }

    private Entry Get(CanvasDevice device, IImageSelectionElement image)
    {
        if (_images.TryGetValue(image.ImageData, out var entry)) { entry.LastUsed = ++_clock; return entry; }
        entry = new Entry { Data = image.ImageData, ContentType = image.ImageContentType, LastUsed = ++_clock };
        _images[image.ImageData] = entry;
        entry.Loading = LoadAsync(device, entry);
        return entry;
    }

    private async Task LoadAsync(CanvasDevice device, Entry entry)
    {
        var token = entry.Cancellation.Token;
        try
        {
            var decoded = await Task.Run(async () =>
            {
                await DecodeGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (string.Equals(entry.ContentType, SvgImage.ContentType, StringComparison.OrdinalIgnoreCase))
                    {
                        using var data = new MemoryStream(entry.Data, writable: false);
                        var image = SvgImage.Read(data);
                        return (Bitmap: (CanvasBitmap)null, Vector: CanvasSvgDocument.LoadFromXml(device, image.Xml), Size: image.Size);
                    }
                    using var managed = new MemoryStream(entry.Data, writable: false);
                    using var stream = managed.AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                    var transform = new BitmapTransform();
                    uint extent = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                    if (MaximumDecodeDimension > 0 && extent > MaximumDecodeDimension)
                    {
                        double scale = (double)MaximumDecodeDimension / extent;
                        transform.ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
                        transform.ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
                    }
                    using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
                    token.ThrowIfCancellationRequested();
                    return (Bitmap: CanvasBitmap.CreateFromSoftwareBitmap(device, software), Vector: (CanvasSvgDocument)null, Size: Vector2.Zero);
                }
                finally { DecodeGate.Release(); }
            });
            if (!IsCurrent(entry))
            {
                decoded.Bitmap?.Dispose();
                decoded.Vector?.Dispose();
                return;
            }
            entry.Bitmap = decoded.Bitmap;
            entry.Vector = decoded.Vector;
            entry.VectorSize = decoded.Size;
            Changed?.Invoke();
        }
        catch
        {
            // Failed images retain their entry until replaced, avoiding repeated decode attempts.
        }
    }

    private bool IsCurrent(Entry entry) => !_disposed && _images.TryGetValue(entry.Data, out var current) && ReferenceEquals(current, entry);

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }

    public void Clear()
    {
        foreach (var entry in _images.Values)
        {
            entry.Dispose();
        }
        _images.Clear();
    }
}
