using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Diagnostics;

internal static class AssistantCompatibilityRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        if (Environment.GetCommandLineArgs().Contains("--without-ai")) services.ExtensionPreferences.DisabledModules.Add("inkcanvas.ai-assistant");
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var httpFixture = new HttpMcpFixture();
        var registry = services.AssistantTools;
        try
        {
            var expected = new Dictionary<string, string[]>
            {
                ["inkcanvas.pdf"] = new[] { "pdf_read", "pdf_capture", "pdf_navigate", "pdf_save", "open_pdf" },
                ["inkcanvas.library"] = new[] { "library_list", "library_open", "library_preview" },
                ["inkcanvas.analytic-geometry"] = new[] { "geometry_read", "geometry_execute", "geometry_calculate", "geometry_insert" },
                ["inkcanvas.time-machine"] = new[] { "recording_list", "recording_control", "recording_read" },
                ["inkcanvas.cloud-sync"] = new[] { "cloud_sync_state", "cloud_sync_run" },
                ["inkcanvas.screen-tracking"] = new[] { "screen_tracking_state", "screen_tracking_reanchor" },
            };
            foreach (var plugin in expected)
                Check("plugin owns its tools " + plugin.Key, plugin.Value.All(name => registry.Definitions.Any(tool => tool.Name == name && tool.Owner == plugin.Key)));
            var contexts = await registry.ReadContextAsync(cancellation.Token);
            Check("plugins supply context without AI dependency", expected.Keys.All(owner => contexts.Any(context => context.Owner == owner)));
            var read = await Call("geometry_calculate", new { expression = "Solve(x^2=4,x)" });
            Check("geometry native symbolic engine", read.Text.Contains("-2", StringComparison.Ordinal) && read.Text.Contains('2'));
            var constructed = await Call("geometry_execute", new { commands = "A=(1,2);B=(3,4);l=Line(A,B)" });
            Check("geometry construction updates native workspace", constructed.Text.Contains("Line(A,B)", StringComparison.Ordinal));
            var list = await Call("library_list", new { folder = directory, query = "" });
            using (var json = JsonDocument.Parse(list.Text)) Check("library native directory listing", json.RootElement.GetProperty("total").GetInt32() >= 0);
            var tracking = await Call("screen_tracking_state", new { });
            Check("screen tracking state tool", tracking.Text.Contains("enabled", StringComparison.Ordinal));
            var cloud = await Call("cloud_sync_state", new { });
            Check("cloud sync status excludes credentials", !cloud.Text.Contains("accessToken", StringComparison.Ordinal) && !cloud.Text.Contains("Email", StringComparison.Ordinal));
            var recordings = await Call("recording_list", new { });
            using (var json = JsonDocument.Parse(recordings.Text)) Check("recording list tool", json.RootElement.TryGetProperty("entries", out _));
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string pdfPath = Path.Combine(directory, "lesson.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { pdfPath, 6 });
            await Call("open_pdf", new { path = pdfPath });
            var pageText = await Call("pdf_read", new { page = 6 });
            Check("PDF adapter reads actual page text", pageText.Text.Contains("InkCanvas classroom PDF", StringComparison.Ordinal));
            await Call("pdf_navigate", new { page = 2 });
            var pdfState = await Call("pdf_read", new { });
            using (var json = JsonDocument.Parse(pdfState.Text)) Check("PDF navigation uses native workspace", json.RootElement.GetProperty("page").GetInt32() == 2);
            var capture = await Call("pdf_capture", new { });
            Check("PDF adapter renders current page", capture.Image is { Length: > 100 });
            var saved = await Call("pdf_save", new { });
            Check("PDF adapter reports actual save result", !saved.IsError);
            // Test expiry while a tool is awaiting I/O. Removing its registration cancels the active invocation.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var registration = registry.Register(new("diagnostic_lifetime", "test", AssistantPlugin.EmptyParameters), async (_, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new("unexpected"); }))
            {
                var operation = Call("diagnostic_lifetime", new { }); await entered.Task;
                registration.Dispose(); bool canceled = false;
                try { await operation; } catch (OperationCanceledException) { canceled = true; }
                Check("unregister cancels an in-flight tool", canceled);
            }
            var mcp = services.Modules.GetExport<IMcpService>() ?? throw new InvalidOperationException("MCP module did not load.");
            var settings = (StackPanel)mcp.GetType().GetMethod("CreateSettingsView").Invoke(mcp, null);
            var root = new Window { Title = "MCP 验收", Content = settings }; root.Activate();
            try
            {
                settings.Children.OfType<ToggleSwitch>().Single().IsOn = true;
                var config = settings.Children.OfType<TextBox>().Single(); config.Text = "{\"mcpServers\":{}}";
                var save = settings.Children.OfType<Button>().Single(button => button.Content?.ToString() == "保存并连接");
                Invoke(save); await Wait(() => save.IsEnabled);
                string bridge = Path.Combine(AppContext.BaseDirectory, "Modules", "inkcanvas.mcp", "InkCanvas.McpHost.exe");
                using var process = Process.Start(new ProcessStartInfo(bridge)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    ArgumentList = { "--pipe", mcp.PipeName },
                }) ?? throw new IOException("MCP bridge did not start.");
                int id = 0;
                async Task<JsonElement> Rpc(string method, object arguments)
                {
                    int requestId = ++id;
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, method, @params = arguments }));
                    await process.StandardInput.FlushAsync();
                    while (true)
                    {
                        string line = await process.StandardOutput.ReadLineAsync(cancellation.Token) ?? throw new IOException("MCP bridge closed.");
                        using var json = JsonDocument.Parse(line);
                        if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out int value) && value == requestId)
                        {
                            if (json.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetRawText());
                            return json.RootElement.GetProperty("result").Clone();
                        }
                    }
                }
                var initialized = await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "InkCanvas-Regression", version = "1.0" } });
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"); await process.StandardInput.FlushAsync();
                Check("external stdio bridge initializes MCP", initialized.GetProperty("serverInfo").GetProperty("name").GetString() == "InkCanvas");
                var tools = await Rpc("tools/list", new { });
                Check("MCP exposes plugin tools", tools.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "geometry_calculate"));
                var computed = await Rpc("tools/call", new { name = "geometry_calculate", arguments = new { expression = "2+3" } });
                Check("external MCP executes plugin tool", computed.GetProperty("content")[0].GetProperty("text").GetString().Contains('5'));
                var resources = await Rpc("resources/list", new { });
                Check("MCP exposes native plugin contexts", resources.GetProperty("resources").GetArrayLength() >= 6);
                config.Text = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
                {
                    ["local-app"] = new { command = bridge, args = new[] { "--pipe", mcp.PipeName } },
                    ["http-fixture"] = new { url = httpFixture.Address, transport = "http", headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixture" } },
                } });
                Invoke(save); await Wait(() => save.IsEnabled && registry.Definitions.Any(tool => tool.Owner == "mcp:local-app")
                    && registry.Definitions.Any(tool => tool.Owner == "mcp:http-fixture"));
                var imported = registry.Definitions.Single(tool => tool.Owner == "mcp:local-app" && tool.Description.Contains("本地符号引擎", StringComparison.Ordinal));
                var remote = await Call(imported.Name, new { expression = "3*7" });
                Check("MCP client imports and calls stdio server", remote.Text.Contains("21", StringComparison.Ordinal));
                var httpTool = registry.Definitions.Single(tool => tool.Owner == "mcp:http-fixture");
                var echoed = await Call(httpTool.Name, new { lesson = "形成性评价" });
                Check("MCP HTTP client negotiates and authenticates", httpFixture.Authenticated && echoed.Text.Contains("形成性评价", StringComparison.Ordinal));
                config.Text = "{\"mcpServers\":{}}"; Invoke(save); await Wait(() => save.IsEnabled);
                Check("MCP refresh removes disconnected tools", !registry.Definitions.Any(tool => tool.Owner.StartsWith("mcp:", StringComparison.Ordinal)));
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellation.Token);
            }
            finally { root.Close(); }
            foreach (var plugin in expected)
            {
                if (plugin.Key == "inkcanvas.pdf") services.Modules.Invoke<IPdfWorkspaceService>(_ => throw new InvalidOperationException("diagnostic stop"));
                else if (plugin.Key == "inkcanvas.analytic-geometry") services.Modules.Invoke<IAnalyticGeometryService>(_ => throw new InvalidOperationException("diagnostic stop"));
                else if (plugin.Key == "inkcanvas.library") services.Modules.Invoke<IBoardLibraryService>(_ => throw new InvalidOperationException("diagnostic stop"));
                else if (plugin.Key == "inkcanvas.time-machine") services.Modules.Invoke<ITimeMachineService>(_ => throw new InvalidOperationException("diagnostic stop"));
                else if (plugin.Key == "inkcanvas.cloud-sync") services.Modules.Invoke<ICloudSyncService>(_ => throw new InvalidOperationException("diagnostic stop"));
                else services.Modules.Invoke<IScreenTrackingService>(_ => throw new InvalidOperationException("diagnostic stop"));
                Check("plugin failure removes owned tools " + plugin.Key, !registry.Definitions.Any(tool => tool.Owner == plugin.Key));
            }
        }
        catch (Exception error) { checks.Add(new { check = "assistant plugin compatibility", passed = false, detail = error.ToString(), modules = services.Modules.Status }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "assistant-compatibility.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        Task<AssistantToolResult> Call(string name, object arguments) => registry.ExecuteAsync(name, JsonSerializer.SerializeToElement(arguments), cancellation.Token);
        void Check(string check, bool passed) => checks.Add(new { check, passed });
        async Task Wait(Func<bool> predicate)
        { while (!predicate()) { cancellation.Token.ThrowIfCancellationRequested(); await Task.Delay(20, cancellation.Token); } }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private sealed class HttpMcpFixture : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Address { get; }
        public bool Authenticated;
        public HttpMcpFixture()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Address = "http://127.0.0.1:" + port + "/mcp/"; _listener.Prefixes.Add(Address); _listener.Start(); _ = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); continue; }
                    Authenticated |= context.Request.Headers["Authorization"] == "Bearer fixture";
                    using var reader = new StreamReader(context.Request.InputStream); using var request = JsonDocument.Parse(await reader.ReadToEndAsync());
                    string method = request.RootElement.GetProperty("method").GetString();
                    if (!request.RootElement.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; context.Response.Close(); continue; }
                    object result = method switch
                    {
                        "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "HTTP Fixture", version = "1.0" } },
                        "tools/list" => new { tools = new[] { new { name = "echo_lesson", description = "HTTP echo", inputSchema = new { type = "object", properties = new { lesson = new { type = "string" } }, required = new[] { "lesson" } } } } },
                        "tools/call" => new { content = new[] { new { type = "text", text = request.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("lesson").GetString() } } },
                        "ping" => new { },
                        _ => null,
                    };
                    object response = result is null ? new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" } }
                        : new { jsonrpc = "2.0", id = id.Clone(), result };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                    context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch (Exception) when (!_listener.IsListening) { }
        }
        public void Dispose() => _listener.Close();
    }
}
