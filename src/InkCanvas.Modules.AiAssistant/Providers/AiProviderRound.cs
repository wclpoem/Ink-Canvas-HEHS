using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InkCanvas.Modules.AiAssistant.Providers;

public sealed record AiToolCall(string Id, string Name, JsonElement Arguments);
public sealed record AiProviderRound(string Text, JsonArray Continuation, IReadOnlyList<AiToolCall> Calls);

public sealed partial class AiProviderClient
{
    public async Task<AiProviderRound> RunRoundAsync(AiConnection connection, string key, AiProviderRequest input,
        Action<string> delta, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(120));
        using var request = AiProviderPayload.Create(connection, key, input, stream: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        var accumulator = new RoundAccumulator(connection.Format, delta);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response, idle.Token).ConfigureAwait(false));
            accumulator.Complete(document.RootElement);
            return accumulator.Finish();
        }
        using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var packet = new StringBuilder();
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(120));
            string line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            if (line is { Length: > 4194304 } || packet.Length > 4194304) throw new InvalidDataException("模型事件过大。");
            if (line is null || line.Length == 0)
            {
                if (packet.Length > 0)
                {
                    string json = packet.ToString(); packet.Clear();
                    if (json == "[DONE]") break;
                    using var document = JsonDocument.Parse(json);
                    accumulator.Push(document.RootElement);
                }
                if (line is null) break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (packet.Length > 0) packet.Append('\n');
                packet.Append(line[5..].TrimStart());
            }
        }
        return accumulator.Finish();
    }

    private sealed class RoundAccumulator(AiApiFormat format, Action<string> delta)
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private readonly SortedDictionary<int, JsonObject> _blocks = new();
        private readonly SortedDictionary<int, StringBuilder> _arguments = new();
        private readonly JsonArray _output = new();
        private readonly List<AiToolCall> _calls = new();
        private readonly HashSet<string> _citations = new(StringComparer.Ordinal);

        public void Push(JsonElement root)
        {
            string text = AiProviderResponse.Text(root, streaming: true);
            Append(text);
            foreach (string url in AiProviderResponse.Citations(root)) _citations.Add(url);
            switch (format)
            {
                case AiApiFormat.ChatCompletions:
                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0
                        || !choices[0].TryGetProperty("delta", out var change)) break;
                    _reasoning.Append(String(change, "reasoning_content"));
                    if (change.TryGetProperty("tool_calls", out var calls))
                        foreach (var call in calls.EnumerateArray())
                        {
                            int index = call.GetProperty("index").GetInt32();
                            if (!_blocks.TryGetValue(index, out var block))
                            {
                                _blocks[index] = block = new JsonObject { ["id"] = "", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                                _arguments[index] = new StringBuilder();
                            }
                            if (call.TryGetProperty("id", out var id)) block["id"] = id.GetString();
                            if (call.TryGetProperty("function", out var function))
                            {
                                if (function.TryGetProperty("name", out var name)) block["function"]["name"] = name.GetString();
                                _arguments[index].Append(String(function, "arguments"));
                            }
                        }
                    break;
                case AiApiFormat.Responses:
                    if (String(root, "type") == "response.output_item.done" && root.TryGetProperty("item", out var item)) _output.Add(JsonNode.Parse(item.GetRawText()));
                    if (String(root, "type") == "response.completed" && root.TryGetProperty("response", out var response)
                        && response.TryGetProperty("output", out var output))
                    { _output.Clear(); foreach (var value in output.EnumerateArray()) _output.Add(JsonNode.Parse(value.GetRawText())); }
                    break;
                case AiApiFormat.Anthropic:
                    string type = String(root, "type");
                    if (type == "content_block_start")
                    {
                        int index = root.GetProperty("index").GetInt32();
                        _blocks[index] = JsonNode.Parse(root.GetProperty("content_block").GetRawText()).AsObject();
                        _arguments[index] = new StringBuilder();
                    }
                    if (type == "content_block_delta")
                    {
                        int index = root.GetProperty("index").GetInt32();
                        var block = _blocks[index]; var changeBlock = root.GetProperty("delta");
                        switch (String(changeBlock, "type"))
                        {
                            case "input_json_delta": _arguments[index].Append(String(changeBlock, "partial_json")); break;
                            case "thinking_delta": block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + String(changeBlock, "thinking"); break;
                            case "signature_delta": block["signature"] = (block["signature"]?.GetValue<string>() ?? "") + String(changeBlock, "signature"); break;
                            case "text_delta": block["text"] = (block["text"]?.GetValue<string>() ?? "") + String(changeBlock, "text"); break;
                        }
                    }
                    break;
                case AiApiFormat.Google:
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
                        && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                        foreach (var part in parts.EnumerateArray()) _output.Add(JsonNode.Parse(part.GetRawText()));
                    break;
            }
        }

        public void Complete(JsonElement root)
        {
            Append(AiProviderResponse.Text(root, streaming: false));
            foreach (string url in AiProviderResponse.Citations(root)) _citations.Add(url);
            switch (format)
            {
                case AiApiFormat.ChatCompletions:
                    var message = root.GetProperty("choices")[0].GetProperty("message");
                    _reasoning.Append(String(message, "reasoning_content"));
                    if (message.TryGetProperty("tool_calls", out var calls))
                        foreach (var call in calls.EnumerateArray())
                        {
                            int index = _blocks.Count;
                            _blocks[index] = JsonNode.Parse(call.GetRawText()).AsObject();
                            _arguments[index] = new StringBuilder(String(call.GetProperty("function"), "arguments"));
                        }
                    break;
                case AiApiFormat.Responses:
                    if (root.TryGetProperty("output", out var output))
                        foreach (var item in output.EnumerateArray()) _output.Add(JsonNode.Parse(item.GetRawText()));
                    break;
                case AiApiFormat.Anthropic:
                    foreach (var block in root.GetProperty("content").EnumerateArray())
                        _blocks[_blocks.Count] = JsonNode.Parse(block.GetRawText()).AsObject();
                    break;
                case AiApiFormat.Google:
                    foreach (var part in root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray())
                        _output.Add(JsonNode.Parse(part.GetRawText()));
                    break;
            }
        }

        public AiProviderRound Finish()
        {
            var continuation = new JsonArray();
            switch (format)
            {
                case AiApiFormat.ChatCompletions:
                    var calls = new JsonArray();
                    foreach (var entry in _blocks)
                    {
                        string arguments = _arguments[entry.Key].ToString();
                        entry.Value["function"]["arguments"] = arguments;
                        AddCall(entry.Value["id"]?.GetValue<string>(), entry.Value["function"]["name"]?.GetValue<string>(), arguments);
                        calls.Add(entry.Value.DeepClone());
                    }
                    var message = new JsonObject { ["role"] = "assistant", ["content"] = _text.ToString() };
                    if (calls.Count > 0) message["tool_calls"] = calls;
                    if (_reasoning.Length > 0) message["reasoning_content"] = _reasoning.ToString();
                    continuation.Add(message);
                    break;
                case AiApiFormat.Responses:
                    foreach (var item in _output)
                    {
                        if (item["type"]?.GetValue<string>() == "function_call")
                            AddCall(item["call_id"]?.GetValue<string>(), item["name"]?.GetValue<string>(), item["arguments"]?.GetValue<string>());
                        continuation.Add(item.DeepClone());
                    }
                    break;
                case AiApiFormat.Anthropic:
                    var content = new JsonArray();
                    foreach (var entry in _blocks)
                    {
                        var block = entry.Value;
                        if (block["type"]?.GetValue<string>() == "tool_use")
                        {
                            string arguments = _arguments.TryGetValue(entry.Key, out var builder) && builder.Length > 0 ? builder.ToString() : block["input"]?.ToJsonString() ?? "{}";
                            block["input"] = JsonNode.Parse(arguments);
                            AddCall(block["id"]?.GetValue<string>(), block["name"]?.GetValue<string>(), arguments);
                        }
                        content.Add(block.DeepClone());
                    }
                    continuation.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                case AiApiFormat.Google:
                    foreach (var item in _output)
                        if (item["functionCall"] is JsonObject function)
                            AddCall(function["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), function["name"]?.GetValue<string>(), function["args"]?.ToJsonString() ?? "{}");
                    continuation.Add(new JsonObject { ["role"] = "model", ["parts"] = _output.DeepClone() });
                    break;
            }
            if (_calls.Count == 0 && _text.Length == 0) throw new InvalidDataException("模型未返回可用内容。");
            if (_calls.Count == 0 && _citations.Count > 0) Append("\n\n" + string.Join("\n", _citations.Select((url, index) => $"- [{index + 1}]({url})")));
            return new(_text.ToString(), continuation, _calls);
        }

        private void AddCall(string id, string name, string arguments)
        {
            if (_calls.Count >= 32 || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("模型工具调用无效。");
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("工具参数必须为对象。");
            _calls.Add(new(id, name, document.RootElement.Clone()));
        }
        private void Append(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_text.Length + text.Length > 2 * 1024 * 1024) throw new InvalidDataException("回复过长，请缩小任务范围。");
            _text.Append(text); delta?.Invoke(text);
        }
    }
}
