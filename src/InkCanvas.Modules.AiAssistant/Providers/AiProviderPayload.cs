using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InkCanvas.Modules.AiAssistant.Providers;

public static class AiProviderPayload
{
    public static bool SupportsReasoning(AiConnection connection) => connection.Reasoning == "supported"
        || connection.Reasoning != "unsupported" && (connection.Provider is "deepseek" or "qwen"
            || connection.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || connection.Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || connection.Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || connection.Model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || connection.Model.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || connection.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)
            || connection.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase));
    public static HttpRequestMessage Create(AiConnection connection, string key, AiProviderRequest input, bool stream)
    {
        if (string.IsNullOrWhiteSpace(connection.Model)) throw new InvalidOperationException("请先选择或填写模型。");
        var request = new HttpRequestMessage(HttpMethod.Post, AiEndpoint.Completion(connection, stream));
        Authenticate(request, connection, key);
        var body = new JsonObject { ["model"] = connection.Model, ["stream"] = stream };
        switch (connection.Format)
        {
            case AiApiFormat.ChatCompletions:
                body["messages"] = Messages(input, connection.SendImages, response: false);
                body[connection.Provider is "openai" or "openai-responses" ? "max_completion_tokens" : "max_tokens"] = connection.MaxOutputTokens;
                break;
            case AiApiFormat.Responses:
                body["instructions"] = input.System;
                body["input"] = Messages(input, connection.SendImages, response: true);
                body["max_output_tokens"] = connection.MaxOutputTokens;
                body["store"] = false;
                break;
            case AiApiFormat.Anthropic:
                body["system"] = input.System;
                body["max_tokens"] = connection.MaxOutputTokens;
                body["messages"] = Messages(input, connection.SendImages, anthropic: true);
                break;
            case AiApiFormat.Google:
                body.Remove("model"); body.Remove("stream");
                body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = input.System }) };
                var contents = new JsonArray();
                foreach (var message in input.Messages.Where(message => message.Role is "user" or "assistant"))
                    contents.Add(new JsonObject { ["role"] = message.Role == "assistant" ? "model" : "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content }) });
                var parts = new JsonArray(new JsonObject { ["text"] = input.Prompt });
                if (connection.SendImages)
                    foreach (string image in input.Images)
                    {
                        var (mime, data) = Image(image);
                        parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = data } });
                    }
                foreach (var file in input.Files)
                    parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = file.Mime, ["data"] = file.Base64 } });
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                foreach (var item in input.ToolHistory) contents.Add(item.DeepClone());
                body["contents"] = contents;
                body["generationConfig"] = new JsonObject { ["maxOutputTokens"] = connection.MaxOutputTokens };
                break;
        }
        ConfigureReasoning(body, connection, input);
        ConfigureTools(body, connection, input);
        if (connection.Search)
        {
            if (!AiProviderCatalog.SupportsSearch(connection)) throw new InvalidOperationException("当前供应商与协议尚未配置联网搜索，请关闭此选项。");
            if (connection.Provider == "qwen")
            {
                if (connection.Format == AiApiFormat.ChatCompletions) body["enable_search"] = true;
                else AddTool(body, new JsonObject { ["type"] = "web_search" });
            }
            else if (connection.Provider == "deepseek")
            {
                var tool = new JsonObject { ["type"] = "deepseek_web_search" };
                if (connection.Format == AiApiFormat.Anthropic) tool["name"] = "deepseek_web_search";
                AddTool(body, tool);
            }
            else if (connection.Format == AiApiFormat.Google)
                AddTool(body, new JsonObject { ["google_search"] = new JsonObject() });
            else AddTool(body, new JsonObject { ["type"] = "web_search" });
        }
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return request;
    }

    public static void Authenticate(HttpRequestMessage request, AiConnection connection, string key)
    {
        if (connection.Provider == "opencode")
        {
            if (connection.Format == AiApiFormat.Anthropic) request.Headers.Add("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return;
        }
        if (connection.Format == AiApiFormat.Anthropic)
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-api-key", key);
        }
        else if (connection.Format == AiApiFormat.Google)
        {
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-goog-api-key", key);
        }
        else if (connection.Provider == "mimo")
        {
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("api-key", key);
        }
        else if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static JsonArray Messages(AiProviderRequest input, bool images, bool response = false, bool anthropic = false)
    {
        var messages = new JsonArray();
        if (!response && !anthropic) messages.Add(new JsonObject { ["role"] = "system", ["content"] = input.System });
        foreach (var message in input.Messages.Where(message => message.Role is "user" or "assistant"))
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
        var content = new JsonArray(new JsonObject { ["type"] = response ? "input_text" : "text", ["text"] = input.Prompt });
        if (images)
            foreach (string image in input.Images)
            {
                var (mime, data) = Image(image);
                if (anthropic) content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject
                    { ["type"] = "base64", ["media_type"] = mime, ["data"] = data } });
                else if (response) content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{mime};base64,{data}" });
                else content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{data}" } });
            }
        foreach (var file in input.Files)
        {
            if (anthropic) content.Add(new JsonObject { ["type"] = "document", ["source"] = new JsonObject
                { ["type"] = "base64", ["media_type"] = file.Mime, ["data"] = file.Base64 } });
            else if (response) content.Add(new JsonObject { ["type"] = "input_file", ["filename"] = file.Name,
                ["file_data"] = $"data:{file.Mime};base64,{file.Base64}" });
            else content.Add(new JsonObject { ["type"] = "file", ["file"] = new JsonObject
                { ["filename"] = file.Name, ["file_data"] = $"data:{file.Mime};base64,{file.Base64}" } });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        foreach (var item in input.ToolHistory) messages.Add(item.DeepClone());
        return messages;
    }

    private static (string Mime, string Data) Image(string image)
    {
        if (image.StartsWith("data:", StringComparison.Ordinal))
        {
            int separator = image.IndexOf(";base64,", StringComparison.Ordinal);
            if (separator < 0) throw new InvalidDataException("图片需要使用 Base64 编码。");
            return (image[5..separator], image[(separator + 8)..]);
        }
        return ("image/png", image);
    }

    private static void AddTool(JsonObject body, JsonObject tool)
    {
        if (body["tools"] is not JsonArray list) body["tools"] = list = new JsonArray();
        list.Add(tool);
    }

    private static void ConfigureTools(JsonObject body, AiConnection connection, AiProviderRequest input)
    {
        foreach (var tool in input.Tools)
        {
            var schema = JsonNode.Parse(tool.Parameters);
            var function = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = schema };
            switch (connection.Format)
            {
                case AiApiFormat.Responses:
                    function["type"] = "function";
                    AddTool(body, function);
                    break;
                case AiApiFormat.Anthropic:
                    function.Remove("parameters"); function["input_schema"] = schema;
                    AddTool(body, function);
                    break;
                case AiApiFormat.Google:
                    // Function declarations share a single entry in the tools list.
                    if (body["tools"] is not JsonArray tools) body["tools"] = tools = new JsonArray();
                    if (tools.Count == 0) tools.Add(new JsonObject { ["functionDeclarations"] = new JsonArray() });
                    ((JsonArray)tools[0]["functionDeclarations"]).Add(function);
                    break;
                default:
                    AddTool(body, new JsonObject { ["type"] = "function", ["function"] = function });
                    break;
            }
        }
    }

    private static void ConfigureReasoning(JsonObject body, AiConnection connection, AiProviderRequest input)
    {
        if (input.Thinking is null || !SupportsReasoning(connection)) return;
        bool thinking = input.Thinking.Value;
        string effort = input.Effort is "low" or "high" or "xhigh" ? input.Effort : "high";
        switch (connection.Format)
        {
            case AiApiFormat.Responses:
                body["reasoning"] = new JsonObject { ["effort"] = thinking ? effort : "none" };
                // Preserve encrypted reasoning items when continuing function calls without server storage.
                body["include"] = new JsonArray("reasoning.encrypted_content");
                break;
            case AiApiFormat.Anthropic:
                bool adaptive = connection.Model.Contains("4-6", StringComparison.OrdinalIgnoreCase)
                    || connection.Model.Contains("4.6", StringComparison.OrdinalIgnoreCase);
                if (thinking && adaptive)
                {
                    body["thinking"] = new JsonObject { ["type"] = "adaptive" };
                    body["output_config"] = new JsonObject { ["effort"] = effort == "xhigh"
                        ? connection.Model.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "max" : "high" : effort };
                }
                else if (thinking)
                {
                    int budget = effort switch { "low" => 1024, "xhigh" => 16384, _ => 4096 };
                    body["max_tokens"] = Math.Max(connection.MaxOutputTokens, budget + 2048);
                    body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
                }
                else body["thinking"] = new JsonObject { ["type"] = "disabled" };
                break;
            case AiApiFormat.Google:
                var config = (JsonObject)body["generationConfig"];
                bool level = connection.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase);
                config["thinkingConfig"] = level
                    ? new JsonObject { ["thinkingLevel"] = thinking ? effort == "low" ? "low" : "high" : "minimal" }
                    : new JsonObject { ["thinkingBudget"] = thinking ? effort switch { "low" => 1024, "xhigh" => 24576, _ => 8192 } : 0 };
                break;
            default:
                if (connection.Provider == "qwen") body["enable_thinking"] = thinking;
                else if (connection.Provider == "deepseek") body["thinking"] = new JsonObject { ["type"] = thinking ? "enabled" : "disabled" };
                else body["reasoning_effort"] = thinking ? effort : "none";
                break;
        }
    }
}
