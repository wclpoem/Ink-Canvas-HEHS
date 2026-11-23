using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Library;

internal sealed partial class LibraryService
{
    internal void RegisterAssistant(IModuleContext context)
    {
        const string owner = "inkcanvas.library";
        AssistantPlugin.Context(context, owner, async token =>
        {
            await _store.LoadAsync().WaitAsync(token);
            return JsonSerializer.Serialize(new { folder = _store.State.LastFolder ?? host.DefaultDirectory,
                locations = _store.State.Locations, recent = _store.State.Recent.Take(8), favorites = _store.State.Favorites.Take(8) });
        });
        AssistantPlugin.Tool(context, new("library_list", "在文稿库列出或搜索白板文件。folder 可省略，query 为名称关键词，recursive 指定子文件夹搜索，offset 用于分页。", """
            {"type":"object","properties":{"folder":{"type":"string"},"query":{"type":"string"},"recursive":{"type":"boolean"},"offset":{"type":"integer","minimum":0}},"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            await _store.LoadAsync().WaitAsync(token);
            string folder = args.TryGetProperty("folder", out var directory) ? directory.GetString() : _store.State.LastFolder ?? host.DefaultDirectory;
            string query = args.TryGetProperty("query", out var search) ? search.GetString() : "";
            int offset = args.TryGetProperty("offset", out var start) ? start.GetInt32() : 0;
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var entries = await LibraryStore.ListAsync(folder, null, query, args.TryGetProperty("recursive", out var recursive) && recursive.GetBoolean(), token);
            return new(JsonSerializer.Serialize(new { total = entries.Count, offset, nextOffset = offset + 80 < entries.Count ? (int?)(offset + 80) : null,
                entries = entries.OrderBy(entry => entry.Name).Skip(offset).Take(80).Select(entry => new { entry.Path, entry.Name, entry.IsFolder, entry.Length, entry.Modified }) }));
        });
        AssistantPlugin.Tool(context, new("library_preview", "读取白板文件的存档信息和指定页预览。page 从 1 开始，默认第 1 页。", """
            {"type":"object","properties":{"path":{"type":"string"},"page":{"type":"integer","minimum":1}},"required":["path"],"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            string path = BoardPath(args); int page = args.TryGetProperty("page", out var index) ? index.GetInt32() - 1 : 0;
            if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
            var preview = await _store.PreviewAsync(path, page, token);
            var metadata = await BoardService.ReadArchiveMetadataAsync(path, token);
            return new(JsonSerializer.Serialize(new { path, page = page + 1, pages = preview.Pages, metadata }), preview.Png);
        });
        AssistantPlugin.Tool(context, new("library_open", "通过文稿库打开指定本地白板文件，切换到该文件的内容和对话。", """
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """) { Owner = owner, ChangesTarget = true }, async (args, token) =>
        {
            token.ThrowIfCancellationRequested(); string path = BoardPath(args);
            bool opened = await host.OpenBoardAsync(path);
            if (opened) { await _store.LoadAsync(); _store.Remember(path); await _store.SaveAsync(); }
            return new(opened ? "已打开白板。" + path : "白板打开未完成。") { IsError = !opened };
        });
    }
    private static string BoardPath(JsonElement args)
    {
        string path = Path.GetFullPath(args.GetProperty("path").GetString());
        if (!Path.GetExtension(path).Equals(".icink", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 .icink 白板文件。");
        return path;
    }
}
