using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Pdf;

internal sealed partial class PdfWorkspaceService
{
    internal void RegisterAssistant(IModuleContext context)
    {
        const string owner = "inkcanvas.pdf";
        AssistantPlugin.Context(context, owner, _ => Task.FromResult(_window?.AssistantState() ?? "PDF 阅读器未打开。"));
        AssistantPlugin.Tool(context, new("open_pdf", "用 PDF 插件打开本地 PDF，省略 path 时由老师选择文件。", """
            {"type":"object","properties":{"path":{"type":"string"}},"additionalProperties":false}
            """) { Owner = owner, ChangesTarget = true }, async (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (_busy) throw new InvalidOperationException("PDF 正在处理其他操作。");
            await OpenForAssistantAsync(args.TryGetProperty("path", out var path) ? path.GetString() : null, token);
            return new(_window?.AssistantState() ?? "未打开 PDF，文件选择已取消或打开失败。") { IsError = _window is null };
        });
        AssistantPlugin.Tool(context, new("pdf_read", "读取当前 PDF 的状态、指定页文字或搜索结果。page 从 1 开始，query 为全文搜索词。扫描页可用 pdf_capture 获取图像。", """
            {"type":"object","properties":{"page":{"type":"integer","minimum":1},"query":{"type":"string"}},"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            var window = RequireWindow();
            if (args.TryGetProperty("query", out var query)) return new(await window.AssistantSearchAsync(query.GetString(), token));
            if (args.TryGetProperty("page", out var page)) return new(await window.AssistantReadPageAsync(page.GetInt32() - 1, token));
            return new(window.AssistantState());
        });
        AssistantPlugin.Tool(context, new("pdf_navigate", "跳转当前 PDF 的指定页，page 从 1 开始。", """
            {"type":"object","properties":{"page":{"type":"integer","minimum":1}},"required":["page"],"additionalProperties":false}
            """) { Owner = owner, ChangesTarget = true }, (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            var window = RequireWindow(); window.AssistantGoToPage(args.GetProperty("page").GetInt32() - 1);
            return Task.FromResult(new AssistantToolResult(window.AssistantState()));
        });
        AssistantPlugin.Tool(context, new("pdf_capture", "获取当前 PDF 指定页的图像与墨迹批注，省略 page 使用当前页。", """
            {"type":"object","properties":{"page":{"type":"integer","minimum":1}},"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            var window = RequireWindow(); int index = args.TryGetProperty("page", out var page) ? page.GetInt32() - 1 : window.PageIndex;
            return new("PDF 第 " + (index + 1) + " 页。", await window.AssistantCaptureAsync(index, token));
        });
        AssistantPlugin.Tool(context, new("pdf_save", "将当前 PDF 的墨迹批注保存到当前文件，结果反映实际保存状态。", AssistantPlugin.EmptyParameters)
            { Owner = owner }, async (_, token) =>
        {
            token.ThrowIfCancellationRequested(); bool saved = await RequireWindow().SaveAsync();
            return new(saved ? "PDF 已保存。" : "PDF 保存未完成。") { IsError = !saved };
        });
    }

    private PdfWindow RequireWindow() => !_disposed && _window is not null ? _window : throw new InvalidOperationException("请先打开 PDF 文件。");
}

public sealed partial class PdfWindow
{
    internal string AssistantState() => JsonSerializer.Serialize(new { file = FilePath, page = PageIndex + 1, pages = _file.Pages.Count, editable = CanEdit, unsaved = IsDirty });
    internal Task<string> AssistantReadPageAsync(int index, CancellationToken token) => _file.ReadPageTextAsync(index, token);
    internal async Task<string> AssistantSearchAsync(string query, CancellationToken token)
        => JsonSerializer.Serialize((await _file.SearchAsync(query, token)).Select(hit => new { page = hit.PageIndex + 1, hit.Preview }).Take(80));
    internal void AssistantGoToPage(int index)
    {
        if (_closed || index < 0 || index >= _file.Pages.Count) throw new ArgumentOutOfRangeException(nameof(index), "PDF 页码不存在。");
        if (Controller.HasActiveInteraction) throw new InvalidOperationException("请结束当前书写后再翻页。");
        ActivatePage(index, false);
    }
    internal async Task<byte[]> AssistantCaptureAsync(int index, CancellationToken token)
    {
        if (_closed || index < 0 || index >= _file.Pages.Count) throw new ArgumentOutOfRangeException(nameof(index), "PDF 页码不存在。");
        var page = _file.Pages[index];
        double scale = Math.Min(1.5, 2048d / Math.Max(page.DipWidth, page.DipHeight));
        byte[] png = await _rasterizer.RenderAsync(index, page, new(0, 0, page.DipWidth, page.DipHeight), scale, token);
        token.ThrowIfCancellationRequested();
        var strokes = _controllers.TryGetValue(index, out var controller) ? controller.Document.Strokes : _file.GetInk(index);
        if (strokes.Count == 0) return png;
        using var bitmap = await LoadBitmapAsync(png, token);
        using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(bitmap.Device, (float)bitmap.Size.Width, (float)bitmap.Size.Height, 96);
        using var scene = new InkCanvas.Ink.Rendering.InkSceneRenderer();
        using (var draw = target.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.White); draw.DrawImage(bitmap);
            var transform = System.Numerics.Matrix3x2.CreateScale((float)bitmap.Size.Width / page.DipWidth, (float)bitmap.Size.Height / page.DipHeight);
            foreach (var stroke in strokes) scene.Strokes.Draw(draw, stroke, transform);
        }
        using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await target.SaveAsync(output, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png).AsTask(token);
        output.Seek(0); using var reader = new Windows.Storage.Streams.DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(token);
        var result = new byte[checked((int)output.Size)]; reader.ReadBytes(result); return result;
    }
}
