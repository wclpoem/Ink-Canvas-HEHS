using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private void RegisterAssistantTools()
    {
        var registry = Services.AssistantTools;
        const string empty = """{"type":"object","properties":{},"additionalProperties":false}""";
        registry.Register(new("tool_catalog", "查找已安装插件与 MCP 服务提供的工具，返回可调用的真实名称、用途和参数。query 使用简短的功能、插件或资源关键词，offset 用于分页。", """
            {"type":"object","properties":{"query":{"type":"string"},"offset":{"type":"integer","minimum":0}},"additionalProperties":false}
            """) { ReadOnly = true }, (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            string query = args.TryGetProperty("query", out var field) ? field.GetString() : "";
            int offset = args.TryGetProperty("offset", out var start) ? start.GetInt32() : 0;
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var matches = registry.Definitions.Where(tool => string.IsNullOrWhiteSpace(query)
                || (tool.Name + " " + tool.Description + " " + tool.Owner).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult(new AssistantToolResult(JsonSerializer.Serialize(new
            {
                total = matches.Length, nextOffset = offset + 16 < matches.Length ? (int?)(offset + 16) : null,
                tools = matches.Skip(offset).Take(16).Select(tool => new { name = tool.Name, description = tool.Description, parameters = JsonSerializer.Deserialize<JsonElement>(tool.Parameters), owner = tool.Owner }),
            })));
        });
        void Add(string name, string description, string schema, Func<JsonElement, AssistantToolResult> run)
            => registry.Register(new(name, description, schema) { ReadOnly = name == "read_workspace", ChangesTarget = name is "board_page" or "open_project_tool" },
                (args, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(run(args)); });
        Add("read_workspace", "读取当前白板页、画布对象、选区、课件文本和可用插件工具。坐标为画布坐标。", empty, _ =>
        {
            var controller = ActiveController;
            var source = new AssistantHost(this).Source;
            return new(JsonSerializer.Serialize(new
            {
                kind = new CanvasToolsHost(this).CaptureTarget().Kind,
                page = Services.Board.CurrentIndex + 1,
                pages = Enumerable.Range(0, Services.Board.PageCount).Select(index => new { page = index + 1, name = Services.Board.PageName(index) }),
                strokes = controller.Document.Strokes.Take(500).Select(stroke => new { stroke.Id, stroke.Bounds, selected = controller.Selection.Contains(stroke) }),
                objects = (controller.GetSelectableElements?.Invoke() ?? Array.Empty<ISelectionElement>()).Select(element => new { element.Id, element.Bounds }),
                course = BuildSlideContext(source.SlideId, source.SlideIndex),
                tools = registry.Definitions.Select(tool => new { tool.Name, tool.Description }),
                plugins = ExtensionCommands.Where(command => command.PluginId != "inkcanvas.ai-assistant")
                    .Select(command => new { plugin = command.PluginId, command = command.CommandId, command.Title, command.Description }),
            }));
        });
        Add("board_page", "管理白板页面。action 为 add、go、rename、duplicate、move，page 为从 1 开始的页码，name 为页面名称，target 为移动目标页码。", """
            {"type":"object","properties":{"action":{"type":"string","enum":["add","go","rename","duplicate","move"]},"page":{"type":"integer","minimum":1},"name":{"type":"string"},"target":{"type":"integer","minimum":1}},"required":["action"],"additionalProperties":false}
            """, args =>
        {
            if (_board is null) ToggleBoard();
            var board = Services.Board;
            int page = args.TryGetProperty("page", out var index) ? index.GetInt32() - 1 : board.CurrentIndex;
            if (page < 0 || page >= board.PageCount) throw new ArgumentException("页码不存在。");
            bool success = true;
            switch (args.GetProperty("action").GetString())
            {
                case "add": success = board.InsertPageAfterCurrent(); break;
                case "go": board.GoTo(page); break;
                case "rename": success = board.RenamePage(page, args.GetProperty("name").GetString()); break;
                case "duplicate": board.GoTo(page); success = board.DuplicateCurrentPage(); break;
                case "move": success = board.MovePage(page, args.GetProperty("target").GetInt32() - 1); break;
                default: throw new ArgumentException("页面操作无效。");
            }
            return new(JsonSerializer.Serialize(new { success, page = board.CurrentIndex + 1, pages = board.PageCount }));
        });
        Add("edit_canvas", "操作当前画布，undo 撤销，redo 重做，delete_selection 删除选区，transform_selection 平移缩放选区。", """
            {"type":"object","properties":{"action":{"type":"string","enum":["undo","redo","delete_selection","transform_selection"]},"dx":{"type":"number"},"dy":{"type":"number"},"scale":{"type":"number","minimum":0.05,"maximum":20}},"required":["action"],"additionalProperties":false}
            """, args =>
        {
            var target = new CanvasToolsHost(this).CaptureTarget();
            var controller = target.Controller;
            if (!target.CanEdit || controller.HasActiveInteraction || controller.Playback is not null) throw new InvalidOperationException("画布当前不可编辑。");
            switch (args.GetProperty("action").GetString())
            {
                case "undo": controller.Undo(); break;
                case "redo": controller.Redo(); break;
                case "delete_selection": if (!controller.HasSelection) throw new InvalidOperationException("尚未选中对象。"); controller.DeleteSelection(); break;
                case "transform_selection":
                    if (!controller.HasSelection) throw new InvalidOperationException("尚未选中对象。");
                    float dx = Number(args, "dx", 0), dy = Number(args, "dy", 0), scale = Number(args, "scale", 1);
                    if (scale is < .05f or > 20f) throw new ArgumentException("缩放比例无效。");
                    var center = controller.SelectionBounds.Center;
                    controller.TransformSelectionUndoable(Matrix3x2.CreateScale(scale, center) * Matrix3x2.CreateTranslation(dx, dy), "AI 调整选区");
                    break;
                default: throw new ArgumentException("画布操作无效。");
            }
            return new("操作已完成。");
        });
        Add("draw_paths", "在当前画布创建可编辑的线段或多段线。paths 为二维点数组，每条路径至少两个点，坐标单位 DIP。", """
            {"type":"object","properties":{"paths":{"type":"array","maxItems":100,"items":{"type":"array","minItems":2,"maxItems":1000,"items":{"type":"array","minItems":2,"maxItems":2,"items":{"type":"number"}}}},"width":{"type":"number","minimum":0.5,"maximum":20}},"required":["paths"],"additionalProperties":false}
            """, args =>
        {
            var paths = args.GetProperty("paths");
            if (paths.GetArrayLength() is < 1 or > 100) throw new ArgumentException("路径数量无效。");
            float width = Number(args, "width", 2);
            if (width is < .5f or > 20) throw new ArgumentException("线宽无效。");
            var strokes = paths.EnumerateArray().Select(path =>
            {
                if (path.GetArrayLength() is < 2 or > 1000) throw new ArgumentException("路径点数无效。");
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = width, Color = ActiveController.Document.Strokes.LastOrDefault()?.Attributes.Color ?? InkColor.FromPackedArgb(0xff4687ff) });
                stroke.RestorePoints(path.EnumerateArray().Select(point =>
                {
                    float x = point[0].GetSingle(), y = point[1].GetSingle();
                    if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 100000 || Math.Abs(y) > 100000) throw new ArgumentException("坐标无效。");
                    return new InkPoint(x, y, .5f, width, 0);
                }));
                stroke.Complete(); return stroke;
            }).ToArray();
            var host = new CanvasToolsHost(this);
            var target = host.CaptureTarget();
            bool success = host.CommitShape(target, strokes);
            if (success) host.EnsureVisible(target);
            return new(success ? "已创建图形，可在画布上编辑或撤销。" : "画布当前不可编辑。");
        });
        Add("open_project_tool", "打开应用提供的课堂工具或已安装插件。timer 计时器，lottery 点名，roster 名单，spotlight 聚光灯，magnifier 放大镜，toolkit 工具箱，examples 例题库，resources 资源包，groups 小组积分，handout 讲义。", """
            {"type":"object","properties":{"tool":{"type":"string","enum":["timer","lottery","roster","spotlight","magnifier","toolkit","examples","resources","groups","handout"]}},"required":["tool"],"additionalProperties":false}
            """, args =>
        {
            switch (args.GetProperty("tool").GetString())
            {
                case "timer": OpenTimer(); break;
                case "lottery": OpenLottery(); break;
                case "roster": OpenRoster(); break;
                case "spotlight": OpenSpotlight(); break;
                case "magnifier": OpenMagnifier(); break;
                case "toolkit": OpenToolkit(); break;
                case "examples": OpenExampleLibrary(); break;
                case "resources": OpenResourceBundle(); break;
                case "groups": OpenGroupScore(); break;
                case "handout": OpenHandout(); break;
                default: throw new ArgumentException("工具名称无效。");
            }
            return new("已调用工具入口。请在打开的工具中继续操作。");
        });
        registry.Register(new("capture_canvas", "获取当前白板页面的图像，供识别板书或查看布局。", empty) { ReadOnly = true }, async (_, token) =>
            new AssistantToolResult("当前白板页面截图。", await BoardPreview.RenderAsync(Services.Board, token: token)));
        registry.Register(new("invoke_plugin_command", "调用 read_workspace 返回的可用插件命令。需使用实际的 plugin 和 command 标识。可打开文稿库、PDF、解析几何、时光机及其他已安装插件。", """
            {"type":"object","properties":{"plugin":{"type":"string"},"command":{"type":"string"}},"required":["plugin","command"],"additionalProperties":false}
            """) { ChangesTarget = true }, async (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            string plugin = args.GetProperty("plugin").GetString(), commandId = args.GetProperty("command").GetString();
            if (plugin == "inkcanvas.ai-assistant") throw new ArgumentException("助手已经打开。");
            var command = ExtensionCommands.FirstOrDefault(item => item.PluginId == plugin && item.CommandId == commandId)
                ?? throw new InvalidOperationException("插件命令当前不可用。");
            await ExecuteExtensionCommandAsync(command);
            return new("已执行插件入口。" + command.Title);
        });
    }

    private static float Number(JsonElement args, string name, float fallback)
    {
        float value = args.TryGetProperty(name, out var field) ? field.GetSingle() : fallback;
        return float.IsFinite(value) ? value : throw new ArgumentException("数值必须有限。");
    }
}
