using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Security.Cryptography.DataProtection;

namespace InkCanvas.Modules.Mcp;

public sealed class McpConfiguration
{
    public bool ExposeProjectTools { get; set; }
    public Dictionary<string, McpServerConfiguration> McpServers { get; set; } = new(StringComparer.Ordinal);
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static McpConfiguration Parse(string json)
    {
        var result = JsonSerializer.Deserialize<McpConfiguration>(json, Json) ?? throw new InvalidDataException("MCP 配置为空。");
        if (result.McpServers is null) throw new InvalidDataException("mcpServers 需要为对象。");
        foreach (var entry in result.McpServers)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 || entry.Value is null) throw new InvalidDataException("MCP 服务名称无效。");
            var server = entry.Value;
            server.Args ??= Array.Empty<string>(); server.Env ??= new(); server.Headers ??= new();
            if (server.Args.Any(value => value is null) || server.Env.Any(pair => pair.Value is null) || server.Headers.Any(pair => pair.Value is null))
                throw new InvalidDataException("MCP 参数、环境变量和请求头不能为 null。");
            if (server.TimeoutSeconds is < 5 or > 600) throw new InvalidDataException("MCP 超时须在 5–600 秒之间。");
            if (!server.Enabled) continue;
            bool remote = !string.IsNullOrWhiteSpace(server.Url);
            if (remote == !string.IsNullOrWhiteSpace(server.Command)) throw new InvalidDataException("每个 MCP 服务须提供 url 或 command 中的一项。");
            if (remote && (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
                throw new InvalidDataException("MCP url 需要有效的 HTTP 或 HTTPS 地址。");
            if (server.Transport is not ("auto" or "stdio" or "http" or "sse")) throw new InvalidDataException("MCP transport 支持 auto、stdio、http 和 sse。");
            if (remote && server.Transport == "stdio" || !remote && server.Transport is "http" or "sse") throw new InvalidDataException("MCP 传输方式与地址不匹配。");
        }
        return result;
    }
}

public sealed class McpServerConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = "auto";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string WorkingDirectory { get; set; }
    public string Url { get; set; } = "";
    public Dictionary<string, string> Env { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;
}

internal sealed class McpConfigurationStore(string directory)
{
    private string FilePath => Path.Combine(directory, "connections.dat");
    public async Task<McpConfiguration> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(FilePath)) return new();
        byte[] encrypted = await File.ReadAllBytesAsync(FilePath, token);
        var decrypted = await new DataProtectionProvider().UnprotectAsync(encrypted.AsBuffer()).AsTask(token);
        return McpConfiguration.Parse(Encoding.UTF8.GetString(decrypted.ToArray()));
    }
    public async Task SaveAsync(McpConfiguration configuration, CancellationToken token)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(configuration, McpConfiguration.Json);
        var encrypted = await new DataProtectionProvider("LOCAL=user").ProtectAsync(json.AsBuffer()).AsTask(token);
        Directory.CreateDirectory(directory);
        string temporary = FilePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted.ToArray(), token);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
