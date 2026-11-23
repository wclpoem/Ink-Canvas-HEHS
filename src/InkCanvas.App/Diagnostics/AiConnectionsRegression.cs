using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Security.Credentials;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class AiConnectionsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        using var server = new FixtureServer();
        SettingsWindow settings = null;
        string configuration = Path.Combine(services.Store.Directory, "PluginData", "inkcanvas.ai-assistant", "Documents", "Connections", "connections.json");
        try
        {
            Check("connection settings exported by existing assistant plugin", services.Modules.GetExport<IAiConnectionSettings>() is not null);
            Check("assistant plugin registers its settings page", services.ModuleSettings.Pages.Any(page => page.Id == "inkcanvas.ai-assistant.settings"));
            Check("screen tracking plugin registers its settings page", services.ModuleSettings.Pages.Any(page => page.Id == "inkcanvas.screen-tracking.settings"));
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("AI 助手");
            await Task.Delay(200);
            var root = (FrameworkElement)settings.Content;
            Check("settings contains all supplier presets", Find<ComboBox>(root, "供应商预设").Items.Count == 17);
            Check("plugin navigation has a dedicated group", Descendants(root).OfType<NavigationView>().Single().MenuItems
                .OfType<NavigationViewItemHeader>().Any(item => item.Content?.ToString() == "插件设置"));
            Check("AI archive settings live in the assistant page", settings.SearchSettings("AI 存档命名与描述").Single().Category == "AI 助手");
            var preset = Find<ComboBox>(root, "供应商预设");
            var plugin = services.Modules.GetExport<IAiConnectionSettings>().GetType().Assembly;
            foreach (string resource in plugin.GetManifestResourceNames().Where(name => name.StartsWith("InkCanvas.AiIcons.", StringComparison.Ordinal)))
            {
                using var icon = plugin.GetManifestResourceStream(resource);
                using var stream = new InMemoryRandomAccessStream();
                using var output = stream.AsStreamForWrite();
                await icon.CopyToAsync(output); await output.FlushAsync(); stream.Seek(0);
                var image = new SvgImageSource();
                Check("supplier icon decodes " + resource, await image.SetSourceAsync(stream) == SvgImageSourceLoadStatus.Success);
            }
            preset.SelectedIndex = 0;
            foreach (int format in new[] { 0, 1, 2, 3 })
            {
                Find<TextBox>(root, "连接名称").Text = "测试连接 " + format;
                Find<TextBox>(root, "API 地址").Text = server.Address + "/gateway/v1";
                Find<ComboBox>(root, "API 格式").SelectedIndex = format;
                Find<PasswordBox>(root, "API Key").Password = FixtureServer.Key;
                Invoke(Find<Button>(root, "获取模型列表"));
                await Wait(() => Find<Button>(root, "获取模型列表").IsEnabled);
                var model = Find<ComboBox>(root, "模型");
                Check("model list fetched through settings " + format, model.Items.Count == 2);
                model.SelectedItem = model.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "fixture-model");
                Invoke(Find<Button>(root, "保存并使用"));
                Check("connection saved without key in JSON " + format, File.Exists(configuration)
                    && !File.ReadAllText(configuration).Contains(FixtureServer.Key, StringComparison.Ordinal));
                Invoke(Find<Button>(root, "测试连接"));
                await Wait(() => Find<Button>(root, "测试连接").IsEnabled);
                Check("connection test invokes selected API " + format, server.Posts > format * 2);

                var assistant = services.Modules.GetExport<IAiAssistantService>();
                assistant.Review(0, 0, 0, "本地联调内容"); await Task.Delay(70);
                var window = shell.OpenWindows.Single(item => item.GetType().Name == "AssistantWindow");
                var assistantRoot = (FrameworkElement)window.Content;
                var question = Find<TextBox>(assistantRoot, "消息内容"); question.Text = "验证接口 " + format;
                Invoke(Find<Button>(assistantRoot, "发送 (Enter)"));
                var serviceField = assistant.GetType().GetField("_activeTurn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await Wait(() => serviceField.GetValue(assistant) is null);
                Check("assistant normal entry uses configured streaming API " + format, server.Streamed >= format + 1);
                Check("assistant uses touch components", Descendants(assistantRoot).OfType<TouchIconButton>().Any());
                window.Close();
            }
            Check("protocols send correct authentication headers", server.AuthenticatedFormats.Count == 4);
            settings.Close();
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("AI 助手");
            await Task.Delay(80); root = (FrameworkElement)settings.Content;
            Check("saved endpoint and model survive reopening settings", Find<TextBox>(root, "API 地址").Text == server.Address + "/gateway/v1"
                && Find<ComboBox>(root, "模型").Text == "fixture-model");
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(settings, theme);
                foreach (int width in new[] { 640, 1150 })
                {
                    settings.AppWindow.Resize(new((int)(width * WindowChrome.GetDpiScale(settings)), 900));
                    await Task.Delay(80);
                    Check("settings controls stay in content bounds " + theme + " " + width,
                        Find<ComboBox>(root, "已保存连接").ActualWidth > 100 && Find<Button>(root, "保存并使用").ActualWidth > 100);
                    var frame = (WindowFrame)settings.Content;
                    var background = frame.Background;
                    frame.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.WhiteSmoke : Microsoft.UI.Colors.Black);
                    await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"connections-{theme}-{width}.png"));
                    frame.Background = background;
                }
            }
            settings.SelectCategory("实验性设置"); await Task.Delay(80);
            root = (FrameworkElement)settings.Content;
            Check("legacy experimental entry opens plugin settings", Descendants(root).OfType<WindowWorkspace>().Single().SelectedPage == "屏幕内容跟随");
            var follow = Find<ToggleSwitch>(root, "启用屏幕内容跟随（实验性）");
            follow.IsOn = true;
            Check("plugin follow controls persist and enable dependencies", services.Store.Load().Experimental.ScreenContentTracking
                && Find<ToggleSwitch>(root, "批注跟随正文滚动").IsEnabled);
            follow.IsOn = false;
            settings.SelectCategory("AI 助手");
            services.Modules.Invoke<IAiAssistantService>(_ => throw new InvalidOperationException("diagnostic plugin failure"));
            await Task.Delay(120);
            root = (FrameworkElement)settings.Content;
            Check("assistant failure removes only its settings page", !services.ModuleSettings.Pages.Any(page => page.Name == "AI 助手")
                && services.ModuleSettings.Pages.Any(page => page.Name == "屏幕内容跟随"));
            Check("assistant failure clears sidebar and search", !Descendants(root).OfType<NavigationViewItem>().Any(item => item.Content?.ToString() == "AI 助手")
                && !settings.SearchSettings("API 连接与模型").Any() && !settings.SearchSettings("AI 存档命名与描述").Any());
            Check("assistant failure returns to module management", Descendants(root).OfType<WindowWorkspace>().Single().SelectedPage == "模块与插件");
        }
        catch (Exception error) { Check("AI connection regression exception", false, error.ToString()); }
        finally
        {
            settings?.Close();
            if (File.Exists(configuration))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(configuration));
                foreach (var connection in json.RootElement.GetProperty("Connections").EnumerateArray())
                {
                    try { var vault = new PasswordVault(); vault.Remove(vault.Retrieve("InkCanvas.AiConnections", connection.GetProperty("Id").GetString())); }
                    catch (Exception) { }
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ai-connections.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (int poll = 0; poll < 300; poll++) { if (condition()) return; await Task.Delay(20); }
        throw new TimeoutException("AI settings operation timed out.");
    }
    private static T Find<T>(DependencyObject root, string name) where T : DependencyObject
        => Descendants(root).OfType<T>().Single(item => AutomationProperties.GetName(item) == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class FixtureServer : IDisposable
    {
        public const string Key = "inkcanvas-local-fixture-key";
        private readonly HttpListener _listener = new();
        public string Address { get; }
        public int Posts, Streamed;
        public HashSet<int> AuthenticatedFormats { get; } = new();
        public FixtureServer()
        {
            var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            Address = "http://127.0.0.1:" + number;
            _listener.Prefixes.Add(Address + "/"); _listener.Start(); _ = Run();
        }
        private async Task Run()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    string path = context.Request.Url.AbsolutePath;
                    string reply;
                    if (context.Request.HttpMethod == "GET") reply = "{\"data\":[{\"id\":\"fixture-model\"},{\"id\":\"another-model\"}]}";
                    else
                    {
                        Posts++;
                        using var reader = new StreamReader(context.Request.InputStream);
                        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                        int format = path.EndsWith("responses", StringComparison.Ordinal) ? 1 : path.EndsWith("messages", StringComparison.Ordinal) ? 2
                            : path.Contains("GenerateContent", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                        string header = context.Request.Headers[format == 2 ? "x-api-key" : format == 3 ? "x-goog-api-key" : "Authorization"];
                        if (header == (format < 2 ? "Bearer " : "") + Key) AuthenticatedFormats.Add(format);
                        bool streaming = json.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True
                            || path.Contains("streamGenerateContent", StringComparison.Ordinal);
                        string answer = "连接成功";
                        object packet = format switch
                        {
                            1 => streaming ? new { type = "response.output_text.delta", delta = answer } : new { output_text = answer },
                            2 => streaming ? new { type = "content_block_delta", delta = new { type = "text_delta", text = answer } }
                                : new { content = new[] { new { type = "text", text = answer } } },
                            3 => new { candidates = new[] { new { content = new { parts = new[] { new { text = answer } } } } } },
                            _ => streaming ? new { choices = new[] { new { delta = new { content = answer } } } }
                                : new { choices = new[] { new { message = new { content = answer } } } },
                        };
                        reply = JsonSerializer.Serialize(packet);
                        if (streaming)
                        {
                            Streamed++;
                            string start = format == 2 ? "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" : "";
                            if (format == 2) reply = reply.Insert(1, "\"index\":0,");
                            reply = start + "data: " + reply + "\n\ndata: [DONE]\n\n";
                            context.Response.ContentType = "text/event-stream";
                        }
                    }
                    context.Response.ContentType ??= "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(reply); context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
            }
            catch (Exception) when (!_listener.IsListening) { }
        }
        public void Dispose() => _listener.Close();
    }
}
