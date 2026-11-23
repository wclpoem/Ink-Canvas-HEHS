using System.Text.Json;

namespace InkCanvas.Modules.AiAssistant.Providers;

public static class AiProviderResponse
{
    public static string Text(JsonElement root, bool streaming)
    {
        string type = AiProviderClient.String(root, "type");
        if (root.TryGetProperty("error", out _) || type is "error" or "response.failed")
            throw new InvalidDataException("模型服务返回错误，请检查接口、模型及请求参数。");
        if (type == "response.incomplete") throw new InvalidDataException("模型输出未完成，请增大输出长度或重试。");
        if (type == "message_delta" && root.TryGetProperty("delta", out var state)
            && AiProviderClient.String(state, "stop_reason") == "max_tokens") throw new InvalidDataException("模型输出达到长度上限，请增大输出长度。");
        if (type == "response.output_text.delta") return AiProviderClient.String(root, "delta");
        if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta)
            && AiProviderClient.String(delta, "type") == "text_delta") return AiProviderClient.String(delta, "text");
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            string finish = AiProviderClient.String(choice, "finish_reason");
            if (finish is "length" or "content_filter") throw new InvalidDataException("模型输出被截断或过滤，请调整设置后重试。");
            if (choice.TryGetProperty(streaming ? "delta" : "message", out var message) && message.TryGetProperty("content", out var content))
                return ContentText(content);
        }
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            string finish = AiProviderClient.String(candidate, "finishReason");
            if (finish is "MAX_TOKENS" or "SAFETY" or "RECITATION") throw new InvalidDataException("模型输出未完成或受到内容限制。");
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                return string.Concat(parts.EnumerateArray().Where(part => !part.TryGetProperty("thought", out var thought)
                    || thought.ValueKind != JsonValueKind.True).Select(part => AiProviderClient.String(part, "text")));
        }
        if (streaming) return "";
        if (AiProviderClient.String(root, "status") is "failed" or "incomplete") throw new InvalidDataException("模型输出未完成。");
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String) return outputText.GetString();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            return string.Concat(output.EnumerateArray().Where(item => AiProviderClient.String(item, "type") == "message")
                .Select(item => item.TryGetProperty("content", out var content) ? ContentText(content) : ""));
        if (root.TryGetProperty("content", out var blocks)) return ContentText(blocks);
        return "";
    }

    private static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(content.EnumerateArray().Where(item => AiProviderClient.String(item, "type") is "text" or "output_text")
            .Select(item => AiProviderClient.String(item, "text")));
    }

    public static IEnumerable<string> Citations(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string type = AiProviderClient.String(value, "type");
            if (type is "url_citation" or "web_search_result" || value.TryGetProperty("title", out _))
            {
                string url = AiProviderClient.String(value, "url");
                if (url.Length == 0) url = AiProviderClient.String(value, "uri");
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") yield return uri.AbsoluteUri;
            }
            foreach (var property in value.EnumerateObject())
                foreach (string url in Citations(property.Value)) yield return url;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                foreach (string url in Citations(item)) yield return url;
    }
}
