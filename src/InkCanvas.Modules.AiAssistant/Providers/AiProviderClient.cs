using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace InkCanvas.Modules.AiAssistant.Providers;

public sealed partial class AiProviderClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public AiProviderClient(HttpClient http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<AiModel>> ModelsAsync(AiConnection connection, string key, CancellationToken token = default)
    {
        var results = new Dictionary<string, AiModel>(StringComparer.Ordinal);
        var next = AiEndpoint.Models(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        for (int page = 0; page < 20 && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            AiProviderPayload.Authenticate(request, connection, key);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            string json = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            foreach (var model in ParseModels(document.RootElement)) results.TryAdd(model.Id, model);
            var root = document.RootElement;
            string nextToken = String(root, "nextPageToken");
            string after = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ? String(root, "last_id") : "";
            if (nextToken.Length == 0 && after.Length == 0) break;
            var builder = new UriBuilder(AiEndpoint.Models(connection));
            builder.Query = builder.Query.TrimStart('?') + (builder.Query.Length == 0 ? "" : "&")
                + (nextToken.Length > 0 ? "pageToken=" + Uri.EscapeDataString(nextToken) : "after_id=" + Uri.EscapeDataString(after));
            next = builder.Uri;
        }
        if (results.Count == 0) throw new InvalidDataException("接口未返回可用模型，可手动填写模型 ID。");
        return results.Values.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<AiModel> ParseModels(JsonElement root)
    {
        JsonElement list = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("data", out list) && !root.TryGetProperty("models", out list)) return Array.Empty<AiModel>();
        }
        if (list.ValueKind != JsonValueKind.Array) return Array.Empty<AiModel>();
        var models = new List<AiModel>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("supportedGenerationMethods", out var methods)
                && methods.ValueKind == JsonValueKind.Array && !methods.EnumerateArray().Any(method => method.GetString() == "generateContent")) continue;
            string id = item.ValueKind == JsonValueKind.String ? item.GetString() : String(item, "id");
            if (string.IsNullOrEmpty(id)) id = String(item, "name");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512) continue;
            if (id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
            string name = String(item, "displayName");
            if (name.Length == 0) name = String(item, "display_name");
            models.Add(new(id, string.IsNullOrWhiteSpace(name) ? id : name));
        }
        return models.DistinctBy(model => model.Id).ToArray();
    }

    public async Task<string> CompleteAsync(AiConnection connection, string key, AiProviderRequest input, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = AiProviderPayload.Create(connection, key, input, stream: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        string json = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        string text = AiProviderResponse.Text(document.RootElement, streaming: false);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("模型未返回文字，请检查模型能力或截图输入设置。");
        return text;
    }

    public async IAsyncEnumerable<string> StreamAsync(AiConnection connection, string key, AiProviderRequest input,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = AiProviderPayload.Create(connection, key, input, stream: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response, idle.Token).ConfigureAwait(false));
            string text = AiProviderResponse.Text(json.RootElement, streaming: false);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("模型未返回文字。");
            yield return text;
            yield break;
        }
        using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var citations = new HashSet<string>(StringComparer.Ordinal);
        bool received = false;
        int total = 0;
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(90));
            string line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            if (line is { Length: > 1048576 } || data.Length > 1048576) throw new InvalidDataException("模型流式事件超过大小限制。");
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    string packet = data.ToString(); data.Clear();
                    if (packet == "[DONE]") break;
                    using var json = JsonDocument.Parse(packet);
                    string text = AiProviderResponse.Text(json.RootElement, streaming: true);
                    foreach (string citation in AiProviderResponse.Citations(json.RootElement)) citations.Add(citation);
                    if (text.Length > 0)
                    {
                        received = true; total += text.Length;
                        if (total > 4 * 1024 * 1024) throw new InvalidDataException("模型回复超过大小限制。");
                        yield return text;
                    }
                }
                if (line is null) break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (!received) throw new InvalidDataException("模型未返回文字，请检查模型能力或截图输入设置。");
        if (citations.Count > 0) yield return "\n\n参考来源\n" + string.Join("\n", citations.Select((url, index) => $"- [{index + 1}]({url})"));
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + length > 4 * 1024 * 1024) throw new InvalidDataException("接口响应超过大小限制。");
            result.Write(buffer, 0, length);
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, (int)result.Length);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("AI 接口返回 HTTP " + (int)response.StatusCode
            + ((int)response.StatusCode is 401 or 403 ? "，请检查 API Key、模型权限与服务区域。" : "，请检查端点地址、协议与模型。"), null, response.StatusCode);
    }
    internal static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
