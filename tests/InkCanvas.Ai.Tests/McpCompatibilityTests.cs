using System.IO.Pipes;
using System.Text.Json;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class McpCompatibilityTests
{
    [Fact]
    public async Task RealMcpConnectionListsCallsReadsResourcesAndReflectsPluginRemoval()
    {
        string name = "InkCanvas-Mcp-Test-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverPipe.WaitForConnectionAsync(cancellation.Token); await clientPipe.ConnectAsync(cancellation.Token); await accept;
        var registry = new Registry();
        using var registration = registry.Register(new("pdf_read", "PDF页", AssistantPlugin.EmptyParameters) { Owner = "inkcanvas.pdf", ReadOnly = true },
            (_, _) => Task.FromResult(new AssistantToolResult("第三页内容", new byte[] { 137, 80, 78, 71 })));
        using var remote = registry.Register(new("remote_tool", "external", AssistantPlugin.EmptyParameters) { Owner = "mcp:external" }, (_, _) => Task.FromResult(new AssistantToolResult("excluded")));
        using var context = registry.RegisterContext("inkcanvas.pdf", _ => Task.FromResult("当前PDF第3页"));
        await using var server = McpProjectServer.Create(serverPipe, serverPipe, registry);
        var serving = server.RunAsync(cancellation.Token);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientPipe, clientPipe), cancellationToken: cancellation.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
            Assert.Equal("pdf_read", Assert.Single(tools).Name);
            var result = await client.CallToolAsync("pdf_read", cancellationToken: cancellation.Token);
            Assert.Equal("第三页内容", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, Assert.IsType<ImageContentBlock>(result.Content[1]).DecodedData.ToArray());
            var resources = await client.ListResourcesAsync(cancellationToken: cancellation.Token);
            var resource = Assert.Single(resources);
            var content = await client.ReadResourceAsync(resource.Uri, cancellationToken: cancellation.Token);
            Assert.Equal("当前PDF第3页", Assert.IsType<TextResourceContents>(Assert.Single(content.Contents)).Text);
            registration.Dispose(); Assert.Empty(await client.ListToolsAsync(cancellationToken: cancellation.Token));
        }
        cancellation.Cancel();
        try { await serving; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void ImageContentRoundTripsRawBytesAndStructuredErrors()
    {
        var bytes = new byte[] { 255, 216, 255, 224, 1, 2, 3 };
        var input = new AssistantToolResult("failure", bytes) { IsError = true, Images = new[] { new AssistantToolImage(bytes, "image/jpeg") } };
        var result = McpResults.FromProtocol(McpResults.ToProtocol(input));
        Assert.True(result.IsError); Assert.Equal(2, result.Images.Count); Assert.All(result.Images, image => Assert.Equal(bytes, image.Data));
    }

    [Theory]
    [InlineData("""{"mcpServers":{"local":{"command":"python","args":["server.py"]},"remote":{"url":"https://example.com/mcp","headers":{"Authorization":"Bearer test"}}}}""")]
    [InlineData("""{"mcpServers":{"disabled":{"enabled":false}}}""")]
    public void ConfigurationAcceptsCommonClientFormat(string json) => Assert.NotNull(McpConfiguration.Parse(json));

    [Theory]
    [InlineData("""{"mcpServers":{"x":{"url":"file:///test"}}}""")]
    [InlineData("""{"mcpServers":{"x":{"url":"https://example.com","command":"cmd"}}}""")]
    [InlineData("""{"mcpServers":{"x":{"command":"cmd","transport":"http"}}}""")]
    public void InvalidConfigurationsAreRejectedBeforeConnecting(string json) => Assert.Throws<InvalidDataException>(() => McpConfiguration.Parse(json));

    [Fact]
    public void RemoteToolNamesAreStableBoundedAndDoNotCollideAcrossServers()
    {
        string name = McpService.ToolName("server", new string('x', 100));
        Assert.InRange(name.Length, 1, 64); Assert.Equal(name, McpService.ToolName("server", new string('x', 100)));
        Assert.NotEqual(name, McpService.ToolName("other", new string('x', 100)));
    }

    private sealed class Registry : IAssistantToolRegistry
    {
        private readonly Dictionary<string, (AssistantToolDefinition Definition, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> Execute)> _tools = new();
        private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _contexts = new();
        public IReadOnlyList<AssistantToolDefinition> Definitions => _tools.Values.Select(entry => entry.Definition).ToArray();
        public event Action Changed;
        public IDisposable Register(AssistantToolDefinition definition, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> execute)
        {
            _tools.Add(definition.Name, (definition, execute)); Changed?.Invoke();
            return new Registration(() => { _tools.Remove(definition.Name); Changed?.Invoke(); });
        }
        public IDisposable RegisterContext(string owner, Func<CancellationToken, Task<string>> read)
        { _contexts.Add(owner, read); return new Registration(() => _contexts.Remove(owner)); }
        public async Task<IReadOnlyList<AssistantContextSection>> ReadContextAsync(CancellationToken token)
        {
            var result = new List<AssistantContextSection>();
            foreach (var context in _contexts) result.Add(new(context.Key, await context.Value(token)));
            return result;
        }
        public Task<AssistantToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken token) => _tools[name].Execute(args, token);
        private sealed class Registration(Action release) : IDisposable { public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
    }
}
