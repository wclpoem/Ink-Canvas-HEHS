using System.Text.Json;
using InkCanvas.Host.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InkCanvas.Modules.Mcp;

public static class McpProjectServer
{
    public static McpServer Create(Stream input, Stream output, IAssistantToolRegistry registry)
    {
        IEnumerable<AssistantToolDefinition> Tools() => registry.Definitions.Where(tool => !tool.Owner.StartsWith("mcp:", StringComparison.Ordinal));
        return McpServer.Create(new StreamServerTransport(input, output, "InkCanvas"), new()
        {
            ServerInfo = new() { Name = "InkCanvas", Version = "1.0.0" },
            ServerInstructions = "工具由已安装的原生插件提供，插件停用后相应工具不可用。请依据真实工具结果继续任务。",
            Capabilities = new() { Tools = new(), Resources = new() },
            Handlers = new()
            {
                ListToolsHandler = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new ListToolsResult { Tools = Tools().Select(tool => new Tool
                    {
                        Name = tool.Name, Description = tool.Description,
                        InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.Parameters),
                        Annotations = new() { ReadOnlyHint = tool.ReadOnly, DestructiveHint = !tool.ReadOnly, OpenWorldHint = true },
                    }).ToArray() });
                },
                CallToolHandler = async (request, token) =>
                {
                    string name = request.Params?.Name ?? throw new ArgumentException("缺少工具名称。");
                    if (!Tools().Any(tool => tool.Name == name)) throw new InvalidOperationException("该插件工具当前不可用。");
                    try
                    {
                        var arguments = JsonSerializer.SerializeToElement(request.Params.Arguments ?? new Dictionary<string, JsonElement>());
                        return McpResults.ToProtocol(await registry.ExecuteAsync(name, arguments, token));
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { return McpResults.ToProtocol(new(error.Message) { IsError = true }); }
                },
                ListResourcesHandler = async (_, token) => new ListResourcesResult
                {
                    Resources = (await registry.ReadContextAsync(token)).Where(section => !section.Owner.StartsWith("mcp:", StringComparison.Ordinal))
                        .Select(section => new Resource { Name = section.Owner, Uri = "inkcanvas://context/" + Uri.EscapeDataString(section.Owner), MimeType = "text/plain" }).ToArray(),
                },
                ReadResourceHandler = async (request, token) =>
                {
                    string uri = request.Params?.Uri ?? "";
                    var section = (await registry.ReadContextAsync(token)).FirstOrDefault(section => !section.Owner.StartsWith("mcp:", StringComparison.Ordinal)
                        && "inkcanvas://context/" + Uri.EscapeDataString(section.Owner) == uri) ?? throw new FileNotFoundException("插件上下文资源不可用。");
                    return new ReadResourceResult { Contents = new ResourceContents[] { new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = section.Text } } };
                },
            },
        });
    }
}
