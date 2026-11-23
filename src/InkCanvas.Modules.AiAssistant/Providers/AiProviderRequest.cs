using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Modules.AiAssistant.Providers;

public sealed record AiProviderRequest(string System, string Prompt, IReadOnlyList<AssistantMessage> Messages,
    IReadOnlyList<string> Images)
{
    public IReadOnlyList<AiFileInput> Files { get; init; } = Array.Empty<AiFileInput>();
    public IReadOnlyList<Host.Contracts.AssistantToolDefinition> Tools { get; init; } = Array.Empty<Host.Contracts.AssistantToolDefinition>();
    public System.Text.Json.Nodes.JsonArray ToolHistory { get; init; } = new();
    public bool? Thinking { get; init; }
    public string Effort { get; init; } = "high";
}

public sealed record AiFileInput(string Name, string Mime, string Base64, int EstimatedTokens = 1600);

public static class AiEndpoint
{
    public static Uri Validate(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 接口地址。");
        return uri;
    }

    public static Uri Completion(AiConnection connection, bool stream)
    {
        var original = Validate(connection.BaseUrl);
        var builder = new UriBuilder(original);
        string path = BasePath(builder.Path);
        if (connection.Provider == "opencode" && original.Host == "opencode.ai"
            && path.StartsWith("/inference/", StringComparison.Ordinal))
            path = connection.Format switch
            {
                AiApiFormat.Anthropic => "/inference/anthropic/v1",
                AiApiFormat.Google => "/inference/google/v1beta",
                _ => "/inference/openai/v1",
            };
        if (connection.Provider == "deepseek" && original.Host == "api.deepseek.com"
            && connection.Format == AiApiFormat.Anthropic && path == "/v1") path = "/anthropic/v1";
        builder.Path = path + (connection.Format switch
        {
            AiApiFormat.Responses => "/responses",
            AiApiFormat.Anthropic => "/messages",
            AiApiFormat.Google => "/models/" + Uri.EscapeDataString(connection.Model.Replace("models/", "", StringComparison.Ordinal))
                + (stream ? ":streamGenerateContent" : ":generateContent"),
            _ => "/chat/completions",
        });
        if (connection.Format == AiApiFormat.Google && stream)
            builder.Query = string.IsNullOrEmpty(builder.Query) ? "alt=sse" : builder.Query.TrimStart('?') + "&alt=sse";
        return builder.Uri;
    }

    public static Uri Models(AiConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.ModelsUrl)) return Validate(connection.ModelsUrl);
        var builder = new UriBuilder(Validate(connection.BaseUrl));
        builder.Path = BasePath(builder.Path) + "/models";
        return builder.Uri;
    }

    private static string BasePath(string path)
    {
        path = path.TrimEnd('/');
        int google = path.IndexOf("/models/", StringComparison.Ordinal);
        if (google >= 0 && (path.Contains(":generateContent", StringComparison.Ordinal)
            || path.Contains(":streamGenerateContent", StringComparison.Ordinal))) return path[..google];
        foreach (string ending in new[] { "/chat/completions", "/responses", "/messages", "/models" })
            if (path.EndsWith(ending, StringComparison.Ordinal)) return path[..^ending.Length];
        return path;
    }
}
