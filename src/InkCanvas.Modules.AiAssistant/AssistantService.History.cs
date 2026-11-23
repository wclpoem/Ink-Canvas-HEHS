using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.AiAssistant;

internal sealed partial class AssistantService
{
    internal void RegisterHistoryTool(IModuleContext context)
    {
        AssistantPlugin.Tool(context, new("assistant_history_read", "读取当前对话的完整原始记录。offset 是从 0 开始的回合位置。turnId 可读取某回合正文，start 是字符偏移，attachment 是从 0 开始的附件索引。可在上下文压缩后恢复原文或重新查看图片和文本附件。", """
            {"type":"object","properties":{"offset":{"type":"integer","minimum":0},"turnId":{"type":"string"},"start":{"type":"integer","minimum":0},"attachment":{"type":"integer","minimum":0}},"additionalProperties":false}
            """) { Owner = "inkcanvas.ai-assistant", ReadOnly = true }, async (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            var chat = _workspace?.Active ?? throw new InvalidOperationException("当前没有对话。");
            if (!args.TryGetProperty("turnId", out var id))
            {
                int offset = args.TryGetProperty("offset", out var index) ? index.GetInt32() : 0;
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                return new(JsonSerializer.Serialize(new { total = chat.Turns.Count, offset,
                    turns = chat.Turns.Skip(offset).Take(20).Select(turn => new { id = turn.Id, question = turn.Question,
                        state = turn.State, attachments = turn.Attachments.Select(attachment => attachment.Name) }) }));
            }
            var selected = chat.Turns.FirstOrDefault(turn => turn.Id.ToString() == id.GetString()) ?? throw new InvalidOperationException("回合不存在。");
            string text;
            if (args.TryGetProperty("attachment", out var attachmentIndex))
            {
                int index = attachmentIndex.GetInt32();
                if (index < 0 || index >= selected.Attachments.Count) throw new ArgumentOutOfRangeException(nameof(index));
                var attachment = selected.Attachments[index];
                if (attachment.IsImage)
                    return new(attachment.Name) { Images = new[] { new AssistantToolImage(Convert.FromBase64String(attachment.Image[(attachment.Image.IndexOf(',') + 1)..]), "image/jpeg") } };
                text = await AssistantAttachments.ReadTextAsync(attachment.Path, token);
                if (string.IsNullOrEmpty(text)) return new("此附件无法提取文字。原路径 " + attachment.Path) { IsError = true };
            }
            else text = "老师\n" + selected.Question + "\n助手\n" + selected.Answer + "\n工具记录\n"
                + string.Join("\n", selected.Tools.Select(tool => tool.Tool + "\n" + tool.Arguments + "\n" + tool.Result));
            int start = args.TryGetProperty("start", out var position) ? position.GetInt32() : 0;
            if (start < 0 || start > text.Length) throw new ArgumentOutOfRangeException(nameof(start));
            int end = Math.Min(text.Length, start + 12000);
            return new(JsonSerializer.Serialize(new { start, nextStart = end < text.Length ? (int?)end : null, text = text[start..end] }));
        });
    }
}
