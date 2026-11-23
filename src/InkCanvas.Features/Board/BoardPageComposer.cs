using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Board;

/// <summary>
/// Single source of truth for composing a whole board page offscreen: transform-aware content
/// bounds (strokes + images + media posters + native text/formula), paper background, grid and
/// media-poster drawing. Handout board pages, library covers, side-board paste-back and the
/// offscreen renderer all share these primitives so a page never clips or diverges per caller.
/// </summary>
public static class BoardPageComposer
{
    public const float DefaultPadding = 24f;

    private static readonly Windows.UI.Color BlackPaper = Windows.UI.Color.FromArgb(255, 10, 10, 10);
    private static readonly Windows.UI.Color LightPaper = Windows.UI.Color.FromArgb(255, 250, 250, 250);
    private static readonly Windows.UI.Color GreenPaper = Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F);
    private static readonly Windows.UI.Color CreamPaper = Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC);
    private static readonly Windows.UI.Color DarkGridLine = Windows.UI.Color.FromArgb(70, 235, 235, 235);
    private static readonly Windows.UI.Color LightGridLine = Windows.UI.Color.FromArgb(55, 92, 113, 121);
    private static readonly Windows.UI.Color DarkAxis = Windows.UI.Color.FromArgb(200, 230, 230, 230);
    private static readonly Windows.UI.Color LightAxis = Windows.UI.Color.FromArgb(190, 60, 80, 90);

    /// <summary>Content bounds for a page, including rotated image/media hulls and native text/formula.</summary>
    public static Rect ContentBounds(BoardPageInfo page)
    {
        if (page is null) return Rect.Empty;
        InkController controller = page.Controller;
        if (controller is null) return Rect.Empty;

        Rect bounds = Rect.Empty;
        foreach (InkStroke stroke in controller.Document.Strokes)
            bounds = bounds.Union(stroke.Bounds);

        foreach (BoardImageAsset image in page.Images ?? Array.Empty<BoardImageAsset>())
        {
            if (image.Data is not { Length: > 0 } || image.Width <= 0 || image.Height <= 0) continue;
            bounds = bounds.Union(BoardElementMath.TransformedBounds(
                image.Left, image.Top, image.Width, image.Height, image.Transform));
        }

        foreach (BoardMediaAsset media in page.Media ?? Array.Empty<BoardMediaAsset>())
        {
            if (media.Width <= 0 || media.Height <= 0) continue;
            bounds = bounds.Union(BoardElementMath.TransformedBounds(
                media.Left, media.Top, media.Width, media.Height, media.Transform));
        }

        foreach (AiObjects.BoardTextObject text in page.TextObjects ?? Array.Empty<AiObjects.BoardTextObject>())
        {
            if (text.Width <= 0 || text.Height <= 0) continue;
            bounds = bounds.Union(text.Bounds);
        }

        foreach (AiObjects.BoardFormulaObject formula in page.FormulaObjects ?? Array.Empty<AiObjects.BoardFormulaObject>())
        {
            if (formula.Width <= 0 || formula.Height <= 0) continue;
            bounds = bounds.Union(formula.Bounds);
        }

        return bounds;
    }

    /// <summary>Content bounds for a page index; out-of-range pages return <see cref="Rect.Empty"/>.</summary>
    public static Rect ContentBounds(BoardService board, int pageIndex)
    {
        if (board is null || pageIndex < 0 || pageIndex >= board.PageCount) return Rect.Empty;
        return ContentBounds(board.GetPageInfo(pageIndex));
    }

    /// <summary>Draw every ordered drawing object on a page through the shared renderer.</summary>
    public static void DrawContent(
        CanvasDrawingSession session, InkSceneRenderer renderer, InkController controller, Matrix3x2 view)
    {
        if (session is null || renderer is null || controller is null) return;
        foreach (DrawingObject item in controller.DrawingObjects)
            renderer.DrawObject(session, controller, item, view);
    }

    /// <summary>Paper colour, grid and coordinate axes; <paramref name="transparent"/> clears only.</summary>
    public static void DrawPaper(
        CanvasDrawingSession session, BoardBackground background, float gridSpacing,
        int width, int height, double scale, bool transparent)
    {
        if (transparent)
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        bool dark = background is BoardBackground.Black or BoardBackground.Green;
        Windows.UI.Color paper = background switch
        {
            BoardBackground.Black => BlackPaper,
            BoardBackground.Green => GreenPaper,
            BoardBackground.Cream => CreamPaper,
            _ => LightPaper,
        };
        session.Clear(paper);

        float spacing = (float)Math.Clamp(gridSpacing, 12f, 160f) * (float)scale;
        if (spacing <= 0) return;
        Windows.UI.Color line = dark ? DarkGridLine : LightGridLine;
        Windows.UI.Color axis = dark ? DarkAxis : LightAxis;

        switch (background)
        {
            case BoardBackground.Grid:
                for (float y = spacing; y < height; y += spacing)
                    session.DrawLine(0, y, width, y, line, 1f);
                for (float x = spacing; x < width; x += spacing)
                    session.DrawLine(x, 0, x, height, line, 1f);
                break;

            case BoardBackground.Lined:
                for (float y = spacing; y < height; y += spacing)
                    session.DrawLine(0, y, width, y, line, 1f);
                break;

            case BoardBackground.Coordinate:
                DrawCoordinates(session, width, height, spacing, axis, line);
                break;
        }
    }

    /// <summary>Draw the poster frame of every media asset that has one, applying its transform.</summary>
    public static async Task DrawMediaPostersAsync(
        CanvasDrawingSession session, CanvasDevice device,
        IReadOnlyList<BoardMediaAsset> mediaObjects, Matrix3x2 view)
    {
        if (mediaObjects is null) return;
        foreach (BoardMediaAsset media in mediaObjects)
        {
            if (media.PosterData is not { Length: > 0 } || media.Width <= 0 || media.Height <= 0) continue;

            CanvasBitmap bitmap = await LoadBitmapAsync(device, media.PosterData);
            if (bitmap is null) continue;
            using (bitmap)
            {
                var previous = session.Transform;
                try
                {
                    session.Transform = ReadTransform(media.Transform) * view;
                    session.DrawImage(
                        bitmap,
                        new Windows.Foundation.Rect(
                            media.Left, media.Top, media.Left + media.Width, media.Top + media.Height),
                        new Windows.Foundation.Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height));
                }
                finally
                {
                    session.Transform = previous;
                }
            }
        }
    }

    private static void DrawCoordinates(
        CanvasDrawingSession session, int width, int height, float spacing,
        Windows.UI.Color axis, Windows.UI.Color tick)
    {
        float originX = width * 0.5f;
        float originY = height * 0.5f;
        const float tickHalf = 4f;

        session.DrawLine(0, originY, width, originY, axis, 1.6f);
        session.DrawLine(originX, 0, originX, height, axis, 1.6f);

        for (float x = originX + spacing; x < width; x += spacing)
            session.DrawLine(x, originY - tickHalf, x, originY + tickHalf, tick, 1f);
        for (float x = originX - spacing; x > 0; x -= spacing)
            session.DrawLine(x, originY - tickHalf, x, originY + tickHalf, tick, 1f);
        for (float y = originY + spacing; y < height; y += spacing)
            session.DrawLine(originX - tickHalf, y, originX + tickHalf, y, tick, 1f);
        for (float y = originY - spacing; y > 0; y -= spacing)
            session.DrawLine(originX - tickHalf, y, originX + tickHalf, y, tick, 1f);
    }

    private static async Task<CanvasBitmap> LoadBitmapAsync(CanvasDevice device, byte[] data)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            return await CanvasBitmap.LoadAsync(device, stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read a recorded 6-element transform; null / malformed falls back to identity.</summary>
    public static Matrix3x2 ReadTransform(float[] values)
        => values is { Length: 6 }
            ? new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5])
            : Matrix3x2.Identity;

    /// <summary>
    /// Win2D shared device may be unavailable on headless / remote sessions; fall back to the
    /// software renderer so handout and replay rendering still complete. Failures are logged.
    /// </summary>
    public static CanvasDevice GetDevice()
    {
        try
        {
            return CanvasDevice.GetSharedDevice();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] Win2D 共享设备不可用，回退软件渲染：{ex.Message}", Core.Diagnostics.LogType.Info);
            return new CanvasDevice(forceSoftwareRenderer: true);
        }
    }
}