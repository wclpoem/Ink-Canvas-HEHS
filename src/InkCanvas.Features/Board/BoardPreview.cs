using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Board;

public sealed record BoardPreviewInfo(int Version, int PageCount, string Title);

/// <summary>Small, self-contained cover shared by the library and the isolated Shell handlers.</summary>
public static class BoardPreview
{
    public const string CoverEntry = "preview/cover.png";
    public const int MaximumCoverBytes = 2 * 1024 * 1024;
    public const int MaximumManifestBytes = 32 * 1024 * 1024;

    public static async Task<(BoardPreviewInfo Info, byte[] Cover)> ReadAsync(string path, CancellationToken token = default)
    {
        return await Task.Run(async () =>
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.ReadByte() != 'P' || input.ReadByte() != 'K') return (null, (byte[])null);
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            BoardPreviewInfo info = null;
            var entry = zip.GetEntry("preview/info.json");
            if (entry is { Length: > 0 and <= 16384 })
            {
                using var details = entry.Open();
                var json = new byte[entry.Length];
                await details.ReadExactlyAsync(json, token);
                info = JsonSerializer.Deserialize<BoardPreviewInfo>(json);
            }
            entry = zip.GetEntry(CoverEntry);
            if (entry is not { Length: > 0 and <= MaximumCoverBytes }) return (info, (byte[])null);
            using var content = entry.Open();
            byte[] bytes = new byte[entry.Length];
            await content.ReadExactlyAsync(bytes, token);
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("封面不是有效的 PNG 图像。");
            uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 8 * 1024 * 1024)
                throw new InvalidDataException("封面图像尺寸超出预览限制。");
            return (info, bytes);
        }, token);
    }

    public static async Task<byte[]> RenderAsync(BoardService board, int index = 0, CancellationToken token = default)
    {
        const int width = 768, height = 432;
        var page = board.GetPageInfo(index);
        var objects = page.Controller.DrawingObjects.ToArray();
        var bounds = page.Controller.Document.Strokes.Select(stroke => stroke.Bounds)
            .Concat(page.Images.Select(image => BoardElementMath.TransformedBounds(image.Left, image.Top, image.Width, image.Height, image.Transform)))
            .Concat(page.Media.Select(media => BoardElementMath.TransformedBounds(media.Left, media.Top, media.Width, media.Height, media.Transform)))
            .Where(rect => !rect.IsEmpty && float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom)).ToArray();
        var area = bounds.Length == 0 ? new Rect(0, 0, 1920, 1080)
            : new Rect(bounds.Min(rect => rect.Left), bounds.Min(rect => rect.Top), bounds.Max(rect => rect.Right), bounds.Max(rect => rect.Bottom)).Inflate(32);
        float scale = Math.Min(width / Math.Max(1, area.Width), height / Math.Max(1, area.Height));
        var view = Matrix3x2.CreateTranslation(-area.Left, -area.Top) * Matrix3x2.CreateScale(scale)
            * Matrix3x2.CreateTranslation((width - area.Width * scale) / 2, (height - area.Height * scale) / 2);
        using var scene = new InkSceneRenderer(width);
        var posters = page.Media.Where(media => media.PosterData is { Length: > 0 }).ToDictionary(media => media.Id, media =>
            new BoardSelectionElement(board, new BoardImageAsset { Data = media.PosterData, Left = media.Left, Top = media.Top,
                Width = media.Width, Height = media.Height, Transform = media.Transform }, page.Controller));
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        await scene.Images.PrepareAsync(target.Device, ImageSceneRenderer.Images(page.Controller).Concat(posters.Values));
        token.ThrowIfCancellationRequested();
        using (var draw = target.CreateDrawingSession())
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
                // Keep grid work proportional to preview pixels, even for a very large canvas.
                float step = Math.Max(8, page.GridSpacing * scale);
                var color = Windows.UI.Color.FromArgb(45, 110, 120, 130);
                if (page.Background != BoardBackground.Lined)
                    for (float x = 0; x < width; x += step) draw.DrawLine(x, 0, x, height, color);
                for (float y = 0; y < height; y += step) draw.DrawLine(0, y, width, y, color);
            }
            foreach (var item in objects)
            {
                token.ThrowIfCancellationRequested();
                scene.DrawObject(draw, page.Controller, item, view);
                if (item.Element is BoardSelectionElement { Media: { } media })
                {
                    if (posters.TryGetValue(media.Id, out var poster)) scene.Images.Draw(draw, poster, view);
                    else
                    {
                        draw.Transform = BoardElementMath.Read(media.Transform) * view;
                        draw.FillRectangle(media.Left, media.Top, media.Width, media.Height, Windows.UI.Color.FromArgb(255, 48, 52, 60));
                        draw.Transform = Matrix3x2.Identity;
                    }
                }
            }
        }
        using var output = new InMemoryRandomAccessStream();
        await target.SaveAsync(output, CanvasBitmapFileFormat.Png);
        if (output.Size > MaximumCoverBytes) throw new InvalidDataException("预览图超过大小限制。");
        output.Seek(0);
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        var png = new byte[output.Size];
        reader.ReadBytes(png);
        return png;
    }
}
