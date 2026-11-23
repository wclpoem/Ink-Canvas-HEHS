using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Storage.Streams;

namespace InkCanvas.Features.Board;

public sealed class BoardPageRenderResult
{
    public byte[] Png { get; init; } = Array.Empty<byte>();

    public int WidthPx { get; init; }

    public int HeightPx { get; init; }

    public string Error { get; init; } = "";

    public bool Success => Png is { Length: > 0 } && WidthPx > 0 && HeightPx > 0;
}

/// <summary>
/// 白板页离屏渲染：背景纸/网格 + 笔画 + 图片（含 AI 卡片 PNG）+ 媒体海报。
/// 边界、纸张与媒体绘制统一委托给 <see cref="BoardPageComposer"/>，笔画/图片/原生文本/公式
/// 复用 <see cref="InkSceneRenderer"/>，因此与画布、讲义、子板贴回和时光机的对象渲染同源。
/// 无 UI 线程依赖（Win2D 共享设备），失败返回错误而不抛异常。
/// </summary>
public static class BoardPageRenderer
{
    private const float Padding = BoardPageComposer.DefaultPadding;
    private const int MaxPixels = 8192;

    /// <summary>
    /// 渲染指定页为 PNG。输出宽度不超过 <paramref name="targetWidthPx"/>，默认 2x DPI，
    /// 长边不超过 8192；<paramref name="transparentBackground"/> 为 true 时不画纸背景与网格。
    /// </summary>
    public static async Task<BoardPageRenderResult> RenderPagePngAsync(
        BoardService board,
        int pageIndex,
        int targetWidthPx,
        double scale = 2.0,
        bool transparentBackground = false)
    {
        if (board is null)
            return Fail("画板服务为空。");
        if (pageIndex < 0 || pageIndex >= board.PageCount)
            return Fail("画板页索引越界。");
        if (!board.PageHasContent(pageIndex))
            return Fail("画板页没有可渲染内容。");

        try
        {
            BoardPageInfo info = board.GetPageInfo(pageIndex);
            InkController controller = info.Controller;
            Rect bounds = BoardPageComposer.ContentBounds(info);
            if (bounds.IsEmpty)
                return Fail("画板页内容包围盒为空。");

            double requestedScale = scale > 0 ? scale : 2.0;
            double widthDip = Math.Max(1f, bounds.Width + Padding * 2);
            double heightDip = Math.Max(1f, bounds.Height + Padding * 2);
            double maxWidth = Math.Clamp(targetWidthPx <= 0 ? 1240 : targetWidthPx, 320, MaxPixels);

            double renderScale = requestedScale;
            double desiredWidth = widthDip * renderScale;
            if (desiredWidth > maxWidth)
                renderScale *= maxWidth / desiredWidth;

            int pixelWidth = Math.Max(1, (int)Math.Round(widthDip * renderScale));
            int pixelHeight = Math.Max(1, (int)Math.Round(heightDip * renderScale));
            if (pixelWidth > MaxPixels || pixelHeight > MaxPixels)
            {
                double factor = MaxPixels / (double)Math.Max(pixelWidth, pixelHeight);
                renderScale *= factor;
                pixelWidth = Math.Max(1, (int)Math.Round(widthDip * renderScale));
                pixelHeight = Math.Max(1, (int)Math.Round(heightDip * renderScale));
            }

            if (pixelWidth <= 0 || pixelHeight <= 0)
                return Fail("画板页目标尺寸无效。");

            double actualScale = pixelWidth / widthDip;
            Matrix3x2 view = Matrix3x2.CreateTranslation(
                    (float)(Padding - bounds.Left), (float)(Padding - bounds.Top))
                * Matrix3x2.CreateScale((float)actualScale);

            var device = BoardPageComposer.GetDevice();
            using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
            await renderer.Images.PrepareAsync(device, controller);

            using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, 96);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                BoardPageComposer.DrawPaper(session, info.Background, info.GridSpacing, pixelWidth, pixelHeight,
                    actualScale, transparentBackground);

                foreach (DrawingObject item in controller.DrawingObjects)
                {
                    if (item.Stroke is { } stroke)
                        renderer.Strokes.Draw(session, stroke, view);
                    else if (item.Element is IBoardTextElement text)
                        renderer.TextObjects.Draw(session, text, view);
                    else if (item.Element is IBoardFormulaElement formula)
                        renderer.Formulas.Draw(session, formula, view);
                    else if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
                        renderer.Images.Draw(session, image, view);
                }

                await BoardPageComposer.DrawMediaPostersAsync(session, device, info.Media, view);
            }

            using var stream = new InMemoryRandomAccessStream();
            await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
            int length = (int)stream.Size;
            var bytes = new byte[length];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)length);
            reader.ReadBytes(bytes);

            return new BoardPageRenderResult
            {
                Png = bytes,
                WidthPx = pixelWidth,
                HeightPx = pixelHeight,
            };
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 板书页离屏渲染失败：page={pageIndex}，error={ex.Message}",
                Core.Diagnostics.LogType.Error);
            return Fail(ex.Message ?? "板书页离屏渲染失败。");
        }
    }

    private static BoardPageRenderResult Fail(string error)
        => new() { Error = error ?? "板书页离屏渲染失败。" };
}