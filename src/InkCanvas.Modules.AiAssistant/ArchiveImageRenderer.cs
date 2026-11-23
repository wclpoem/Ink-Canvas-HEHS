using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AiAssistant;

public static class ArchiveImageRenderer
{
    public static async IAsyncEnumerable<AiDocumentImage> ExportAsync(string path, string directory, [EnumeratorCancellation] CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        int pageNumber = 0;
        await foreach (var board in BoardService.ReadVisualPagesAsync(path, token).ConfigureAwait(false))
        {
            pageNumber++;
            var page = board.GetPageInfo(0);
            var controller = page.Controller;
            Matrix3x2 sourceView = controller.ViewTransform;
            float pixelScale = 1;
            bool screen = board.ArchiveMetadata?.Kind is "screen" or "annotation";
            foreach (var image in page.Images.Where(image => !string.IsNullOrEmpty(image.Metadata)))
            {
                try
                {
                    using var info = JsonDocument.Parse(image.Metadata);
                    if (info.RootElement.TryGetProperty("Kind", out var kind) && kind.GetString() == "screen-capture-background")
                    {
                        screen = true;
                        if (info.RootElement.TryGetProperty("DpiScale", out var dpi) && dpi.TryGetSingle(out float value) && value is > 0 and < 8) pixelScale = value;
                    }
                }
                catch (JsonException) { }
            }
            if (!screen) sourceView = Matrix3x2.Identity;
            sourceView *= Matrix3x2.CreateScale(pixelScale);
            var objects = controller.DrawingObjects.ToArray();
            Rect Transform(Rect bounds, Matrix3x2 matrix)
            {
                var corners = new[] { new Vector2(bounds.Left, bounds.Top), new Vector2(bounds.Right, bounds.Top), new Vector2(bounds.Right, bounds.Bottom), new Vector2(bounds.Left, bounds.Bottom) }.Select(point => Vector2.Transform(point, matrix)).ToArray();
                return new(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Max(point => point.X), corners.Max(point => point.Y));
            }
            var ink = controller.Document.Strokes.Select(stroke => (Bounds: Transform(stroke.Bounds, sourceView), Points: stroke.Count)).ToArray();
            var extents = ink.Select(item => item.Bounds.Inflate(8)).Concat(page.Images.Select(image => Transform(BoardElementMath.TransformedBounds(image.Left, image.Top, image.Width, image.Height, image.Transform), sourceView)))
                .Concat(page.Media.Select(media => Transform(BoardElementMath.TransformedBounds(media.Left, media.Top, media.Width, media.Height, media.Transform), sourceView)))
                .Where(bounds => !bounds.IsEmpty && float.IsFinite(bounds.Left) && float.IsFinite(bounds.Top) && float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom)).ToList();
            var objectBounds = extents.ToArray();
            if (!screen || extents.Count == 0) extents.Add(new(0, 0, controller.ViewCenter.X * 2 * pixelScale, controller.ViewCenter.Y * 2 * pixelScale));
            var content = new Rect(extents.Min(rect => rect.Left), extents.Min(rect => rect.Top), extents.Max(rect => rect.Right), extents.Max(rect => rect.Bottom));
            var regions = ArchiveRenderPlan.Create(content, screen, controller.ViewCenter.X * 2 * pixelScale, controller.ViewCenter.Y * 2 * pixelScale, ink);
            using var scene = new InkSceneRenderer(3840) { ShowSelectionOutline = false };
            var posters = page.Media.Where(media => media.PosterData is { Length: > 0 }).ToDictionary(media => media.Id,
                media => new BoardSelectionElement(board, new BoardImageAsset { Data = media.PosterData, Left = media.Left, Top = media.Top, Width = media.Width, Height = media.Height, Transform = media.Transform }, controller));
            int segment = 0;
            foreach (var region in regions)
            {
                token.ThrowIfCancellationRequested();
                if (regions.Count > 1 && objectBounds.Length > 0 && !objectBounds.Any(bounds => bounds.Intersects(region.Bounds))) continue;
                float scale = Math.Min(region.Width / region.Bounds.Width, region.Height / region.Bounds.Height);
                var view = sourceView * Matrix3x2.CreateTranslation(-region.Bounds.Left, -region.Bounds.Top) * Matrix3x2.CreateScale(scale);
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), region.Width, region.Height, 96);
                CanvasDrawingSession draw = target.CreateDrawingSession();
                try
                {
                    draw.Clear(page.Background switch
                    {
                        BoardBackground.Black => Windows.UI.Color.FromArgb(255, 31, 31, 31),
                        BoardBackground.Green => Windows.UI.Color.FromArgb(255, 30, 59, 47),
                        BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 245, 239, 220),
                        _ => Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    });
                    if (page.Background is BoardBackground.Grid or BoardBackground.Lined or BoardBackground.Coordinate)
                    {
                        float step = Math.Max(12, page.GridSpacing * scale * MathF.Sqrt(MathF.Abs(sourceView.GetDeterminant())));
                        var color = Windows.UI.Color.FromArgb(50, 100, 115, 130);
                        float startX = ((sourceView.M31 - region.Bounds.Left) * scale % step + step) % step;
                        float startY = ((sourceView.M32 - region.Bounds.Top) * scale % step + step) % step;
                        if (page.Background != BoardBackground.Lined)
                            for (float x = startX; x < region.Width; x += step) draw.DrawLine(x, 0, x, region.Height, color);
                        for (float y = startY; y < region.Height; y += step) draw.DrawLine(0, y, region.Width, y, color);
                        if (page.Background == BoardBackground.Coordinate)
                        {
                            var origin = Vector2.Transform(Vector2.Zero, view);
                            draw.DrawLine(0, origin.Y, region.Width, origin.Y, color, 1.5f);
                            draw.DrawLine(origin.X, 0, origin.X, region.Height, color, 1.5f);
                        }
                    }
                    foreach (var item in objects)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Stroke is { } stroke && !Transform(stroke.Bounds, sourceView).Intersects(region.Bounds)) continue;
                        if (item.Stroke is { } inkStroke)
                        {
                            if (region.Role == "overview")
                            {
                                float effectiveScale = MathF.Sqrt(MathF.Abs(view.GetDeterminant()));
                                float minimum = .85f / Math.Max(.0001f, effectiveScale);
                                if (inkStroke.Points.Any(point => point.Width < minimum))
                                {
                                    var overview = inkStroke.Clone();
                                    overview.RestorePoints(inkStroke.Points.Select(point => { point.Width = Math.Max(point.Width, minimum); return point; }));
                                    overview.Complete(); scene.Strokes.Draw(draw, overview, view);
                                }
                                else scene.Strokes.Draw(draw, inkStroke, view);
                            }
                            else scene.Strokes.Draw(draw, inkStroke, view);
                        }
                        var image = item.Element as IImageSelectionElement;
                        if (posters.TryGetValue(item.Id, out var poster)) image = poster;
                        if (image is not null)
                        {
                            var bounds = Transform(image.Frame.Bounds, image.Frame.Transform * sourceView);
                            if (!bounds.Intersects(region.Bounds)) continue;
                            draw.Dispose(); draw = null;
                            uint dimension = (uint)Math.Clamp(Math.Ceiling(Math.Max(bounds.Width, bounds.Height) * scale), 16, 3840);
                            using var imageRenderer = new ImageSceneRenderer { MaximumDecodeDimension = dimension };
                            await imageRenderer.PrepareAsync(target.Device, new[] { image }).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            draw = target.CreateDrawingSession();
                            imageRenderer.Draw(draw, image, view);
                        }
                        else if (item.Element is BoardSelectionElement { Media: { } media })
                        {
                            draw.Transform = BoardElementMath.Read(media.Transform) * view;
                            draw.FillRectangle(media.Left, media.Top, media.Width, media.Height, Windows.UI.Color.FromArgb(255, 48, 52, 60));
                            draw.Transform = Matrix3x2.Identity;
                        }
                    }
                }
                finally { draw?.Dispose(); }
                string output = Path.Combine(directory, $"page-{pageNumber:D4}-{++segment:D4}-{region.Role}.png");
                await target.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask(token).ConfigureAwait(false);
                target.Dispose();
                yield return new(output, pageNumber, region.Role, region.Width, region.Height) { SourceRegion = region.Bounds };
            }
        }
    }
}
