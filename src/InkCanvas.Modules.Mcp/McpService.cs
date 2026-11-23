using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace InkCanvas.Modules.Mcp;

public sealed partial class McpService : IMcpService, IDisposable
{
    private sealed class Connection(string name, McpClient client)
    {
        public string Name { get; } = name;
        public McpClient Client { get; } = client;
        public IAsyncDisposable ListChanges;
        public List<IDisposable> Registrations { get; } = new();
        public void Unregister()
        {
            if (ListChanges is not null) { _ = ListChanges.DisposeAsync(); ListChanges = null; }
            foreach (var registration in Registrations) registration.Dispose();
            Registrations.Clear();
        }
    }

    private readonly IAssistantToolRegistry _registry;
    private readonly McpConfigurationStore _store;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueueTimer _catalogRefresh;
    private readonly List<Connection> _connections = new();
    private readonly Dictionary<string, string> _statuses = new(StringComparer.Ordinal);
    private McpConfiguration _configuration = new();
    private string _configurationError = "";
    private bool _disposed;
    public string PipeName { get; } = "InkCanvas-Mcp-" + Environment.ProcessId;
    private event Action Changed;

    public McpService(IMcpHost host, IAssistantToolRegistry registry)
    {
        _registry = registry; _store = new(host.DataDirectory);
        _catalogRefresh = _dispatcher.CreateTimer(); _catalogRefresh.IsRepeating = false; _catalogRefresh.Interval = TimeSpan.FromMilliseconds(500);
        _catalogRefresh.Tick += (_, _) => _ = RefreshOnStartAsync();
    }
    public void Start() => _ = RefreshOnStartAsync();
    private async Task RefreshOnStartAsync()
    {
        try { await RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _configurationError = error.Message; NotifyChanged(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _refreshGate.WaitAsync(operation.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var configuration = await _store.LoadAsync(operation.Token);
            var previousConnections = _connections.ToArray(); _connections.Clear();
            foreach (var connection in previousConnections) connection.Unregister();
            foreach (var connection in previousConnections) await connection.Client.DisposeAsync();
            _statuses.Clear(); _configuration = configuration; _configurationError = "";
            ConfigureProjectServer(configuration.ExposeProjectTools);
            foreach (var entry in configuration.McpServers.Where(entry => entry.Value.Enabled))
            {
                operation.Token.ThrowIfCancellationRequested();
                _statuses[entry.Key] = "正在连接…"; NotifyChanged();
                try { await ConnectAsync(entry.Key, entry.Value, operation.Token); }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception error) { _statuses[entry.Key] = "连接失败。" + error.Message; }
                NotifyChanged();
            }
        }
        finally { _refreshGate.Release(); NotifyChanged(); }
    }

    private async Task ConnectAsync(string name, McpServerConfiguration configuration, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(configuration.TimeoutSeconds, 45)));
        IClientTransport transport;
        if (!string.IsNullOrWhiteSpace(configuration.Url))
        {
            transport = new HttpClientTransport(new()
            {
                Name = name, Endpoint = new(configuration.Url), AdditionalHeaders = configuration.Headers,
                TransportMode = configuration.Transport == "sse" ? HttpTransportMode.Sse
                    : configuration.Transport == "http" ? HttpTransportMode.StreamableHttp : HttpTransportMode.AutoDetect,
            });
        }
        else transport = new StdioClientTransport(new()
        {
            Name = name, Command = configuration.Command, Arguments = configuration.Args, WorkingDirectory = configuration.WorkingDirectory,
            InheritEnvironmentVariables = false, EnvironmentVariables = ProcessEnvironment(configuration.Env),
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var connection = new Connection(name, client);
        try
        {
            string owner = "mcp:" + name;
            if (client.ServerCapabilities.Tools is not null)
                foreach (var tool in await client.ListToolsAsync(cancellationToken: timeout.Token))
                {
                    string remoteName = tool.Name;
                    var definition = new AssistantToolDefinition(ToolName(name, remoteName), name + " · " + (tool.Description ?? remoteName), tool.JsonSchema.GetRawText())
                    { Owner = owner, ReadOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint == true };
                    connection.Registrations.Add(_registry.Register(definition, async (args, cancellation) =>
                    {
                        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
                        call.CancelAfter(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
                        var arguments = args.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone());
                        return McpResults.FromProtocol(await client.CallToolAsync(remoteName, arguments, cancellationToken: call.Token));
                    }));
                }
            token.ThrowIfCancellationRequested();
            RegisterResources(connection, owner);
            RegisterPrompts(connection, owner);
            connection.Registrations.Add(_registry.RegisterContext(owner, _ => Task.FromResult(
                "已连接 MCP 服务 " + name + "，共有 " + _registry.Definitions.Count(tool => tool.Owner == owner) + " 个工具。可用 tool_catalog 查找具体能力。")));
            _connections.Add(connection); _statuses[name] = "已连接 · " + connection.Registrations.Count + " 项能力";
            connection.ListChanges = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
            {
                _dispatcher.TryEnqueue(() => { if (!_disposed) { _catalogRefresh.Stop(); _catalogRefresh.Start(); } });
                return ValueTask.CompletedTask;
            });
            _ = ObserveConnectionAsync(connection);
        }
        catch { connection.Unregister(); await client.DisposeAsync(); throw; }
    }

    private async Task ObserveConnectionAsync(Connection connection)
    {
        await connection.Client.Completion.ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !_connections.Remove(connection)) return;
            connection.Unregister();
            _statuses[connection.Name] = "连接已断开，可在设置中重新连接。"; NotifyChanged();
            _ = connection.Client.DisposeAsync();
        });
    }

    private void RegisterResources(Connection connection, string owner)
    {
        var client = connection.Client;
        if (client.ServerCapabilities.Resources is null) return;
        connection.Registrations.Add(_registry.Register(new(ToolName(connection.Name, "resources_list"), connection.Name + " · 列出 MCP 资源及可读取 URI。", AssistantPlugin.EmptyParameters)
            { Owner = owner, ReadOnly = true }, async (_, token) => new(JsonSerializer.Serialize((await client.ListResourcesAsync(cancellationToken: token)).Select(resource => resource.ProtocolResource)))));
        connection.Registrations.Add(_registry.Register(new(ToolName(connection.Name, "resource_read"), connection.Name + " · 读取指定 URI 的 MCP 资源。", """
            {"type":"object","properties":{"uri":{"type":"string"}},"required":["uri"],"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            var resource = await client.ReadResourceAsync(args.GetProperty("uri").GetString(), cancellationToken: token);
            var text = new StringBuilder(); var images = new List<AssistantToolImage>();
            foreach (var content in resource.Contents)
            {
                if (content is TextResourceContents textual) text.AppendLine(textual.Uri + "\n" + textual.Text);
                else if (content is BlobResourceContents binary && binary.MimeType?.StartsWith("image/", StringComparison.Ordinal) == true)
                    images.Add(new(binary.DecodedData.ToArray(), binary.MimeType));
                else text.AppendLine(content.Uri + " 的资源内容为当前助手无法直接读取的二进制数据。");
            }
            return new(text.ToString()) { Images = images };
        }));
    }

    private void RegisterPrompts(Connection connection, string owner)
    {
        var client = connection.Client;
        if (client.ServerCapabilities.Prompts is null) return;
        connection.Registrations.Add(_registry.Register(new(ToolName(connection.Name, "prompts_list"), connection.Name + " · 列出可用 MCP 提示词及参数。", AssistantPlugin.EmptyParameters)
            { Owner = owner, ReadOnly = true }, async (_, token) => new(JsonSerializer.Serialize((await client.ListPromptsAsync(cancellationToken: token)).Select(prompt => prompt.ProtocolPrompt)))));
        connection.Registrations.Add(_registry.Register(new(ToolName(connection.Name, "prompt_get"), connection.Name + " · 读取指定 MCP 提示词模板，作为任务参考资料。", """
            {"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"object","additionalProperties":{"type":"string"}}},"required":["name"],"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            var arguments = args.TryGetProperty("arguments", out var values)
                ? values.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.GetString()) : new();
            var prompt = await client.GetPromptAsync(args.GetProperty("name").GetString(), arguments, cancellationToken: token);
            var text = new StringBuilder(prompt.Description); var images = new List<AssistantToolImage>();
            foreach (var message in prompt.Messages)
            {
                var content = McpResults.FromProtocol(new CallToolResult { Content = new[] { message.Content } });
                text.AppendLine("\n" + message.Role + "\n" + content.Text); images.AddRange(content.Images);
            }
            return new(text.ToString()) { Images = images };
        }));
    }

    public static string ToolName(string server, string tool)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + tool)))[..12].ToLowerInvariant();
        string label = new(tool.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(45).ToArray());
        return "mcp_" + hash + "_" + label;
    }
    private static Dictionary<string, string> ProcessEnvironment(Dictionary<string, string> configured)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "COMSPEC" })
            if (Environment.GetEnvironmentVariable(key) is { } value) environment[key] = value;
        foreach (var entry in configured) environment[entry.Key] = entry.Value;
        return environment;
    }
    private void NotifyChanged() => _dispatcher.TryEnqueue(() => { if (!_disposed) Changed?.Invoke(); });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _catalogRefresh.Stop(); _lifetime.Cancel(); StopProjectServer();
        foreach (var connection in _connections) connection.Unregister();
        _ = DisposeClientsAsync();
    }
    private async Task DisposeClientsAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var connection in _connections)
                try { await connection.Client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            _connections.Clear();
        }
        finally { _refreshGate.Release(); }
    }
}
