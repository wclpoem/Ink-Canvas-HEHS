using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.TimeMachine;

public sealed partial class TimeMachineService
{
    internal void RegisterAssistant(IModuleContext context)
    {
        const string owner = "inkcanvas.time-machine";
        AssistantPlugin.Context(context, owner, _ => Task.FromResult(JsonSerializer.Serialize(new { recording = IsRecording, directory = RecordingsDirectory() })));
        AssistantPlugin.Tool(context, new("recording_list", "列出板书时光机已有课堂录制的 ID、名称、时长和文件路径。offset 用于分页。", """
            {"type":"object","properties":{"offset":{"type":"integer","minimum":0}},"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            int offset = args.TryGetProperty("offset", out var field) ? field.GetInt32() : 0;
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var entries = await Task.Run(ListRecordings, token);
            return new(JsonSerializer.Serialize(new { total = entries.Count, offset, entries = entries.OrderByDescending(entry => entry.CreatedAt).Skip(offset).Take(50) }));
        });
        AssistantPlugin.Tool(context, new("recording_control", "开始或停止课堂录制。开始前需要打开白板或课件。停止后录制由时光机保存。", """
            {"type":"object","properties":{"action":{"type":"string","enum":["start","stop"]}},"required":["action"],"additionalProperties":false}
            """) { Owner = owner }, (args, token) =>
        {
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
            string action = args.GetProperty("action").GetString();
            if (action == "start")
            {
                if (!_host.BoardOpen && _host.Ppt?.Snapshot?.IsAttached != true) throw new InvalidOperationException("请先打开白板或课件。");
                StartRecording();
            }
            else if (action == "stop") StopRecording();
            else throw new ArgumentException("录制操作无效。");
            return Task.FromResult(new AssistantToolResult(JsonSerializer.Serialize(new { recording = IsRecording, saveError = LastRecordingSaveError }))
                { IsError = action == "stop" && LastRecordingSaveError is not null });
        });
        AssistantPlugin.Tool(context, new("recording_read", "读取指定课堂录制的章节与时间线摘要。id 使用 recording_list 返回的标识。offset 用于分页，省略时从头开始。", """
            {"type":"object","properties":{"id":{"type":"string"},"offset":{"type":"integer","minimum":0}},"required":["id"],"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            string id = args.GetProperty("id").GetString(); int offset = args.TryGetProperty("offset", out var field) ? field.GetInt32() : 0;
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var info = ListRecordings().FirstOrDefault(entry => entry.Id == id) ?? throw new FileNotFoundException("课堂录制不存在。");
            var session = await Task.Run(() => OpenSession(info), token);
            return new(JsonSerializer.Serialize(new { info.Name, info.DurationMs, events = session.Events.Count,
                timeline = session.Events.Skip(offset).Take(80).Select(entry => new { entry.TimeMs, entry.PageIndex, kind = entry.Kind.ToString(), entry.Payload }) }));
        });
    }
}
