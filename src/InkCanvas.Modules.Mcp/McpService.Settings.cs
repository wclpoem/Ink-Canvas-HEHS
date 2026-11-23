using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace InkCanvas.Modules.Mcp;

public sealed partial class McpService
{
    public FrameworkElement CreateSettingsView()
    {
        var view = new StackPanel { Spacing = 12 };
        var expose = new ToggleSwitch { Header = "向外部 MCP 客户端提供应用工具", OnContent = "开启", OffContent = "关闭", IsOn = _configuration.ExposeProjectTools };
        var configuration = new TextBox { Header = "MCP 服务配置（JSON）", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            MinHeight = 200, MaxHeight = 500, Text = JsonSerializer.Serialize(_configuration, McpConfiguration.Json) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var save = new Button { Content = "保存并连接", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        var refresh = new Button { Content = "重新连接并刷新工具", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        var copy = new Button { Content = "复制外部客户端的 stdio 配置", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        void Update() => status.Text = _configurationError + (_configurationError.Length > 0 ? "\n" : "")
            + string.Join("\n", _statuses.Select(entry => entry.Key + " · " + entry.Value))
            + (_configuration.ExposeProjectTools ? "\n应用工具已通过当前用户的本地连接提供给 MCP 客户端。" : "");
        async Task RunAsync(Func<Task> run)
        {
            save.IsEnabled = refresh.IsEnabled = false;
            try { await run(); _configurationError = ""; }
            catch (Exception error) { _configurationError = error.Message; }
            finally { save.IsEnabled = refresh.IsEnabled = true; Update(); }
        }
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var parsed = McpConfiguration.Parse(configuration.Text); parsed.ExposeProjectTools = expose.IsOn;
            await _store.SaveAsync(parsed, _lifetime.Token); await RefreshAsync(_lifetime.Token);
        });
        refresh.Click += async (_, _) => await RunAsync(() => RefreshAsync(_lifetime.Token));
        copy.Click += (_, _) =>
        {
            string host = Path.Combine(Path.GetDirectoryName(typeof(McpModule).Assembly.Location)!, "InkCanvas.McpHost.exe");
            var payload = new DataPackage();
            payload.SetText(JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
                { ["inkcanvas"] = new { command = host, args = new[] { "--pipe", PipeName } } } }, McpConfiguration.Json));
            Clipboard.SetContent(payload);
        };
        view.Children.Add(new TextBlock { Text = "配置 mcpServers 下的服务。远程服务使用 url 和可选 headers，本地服务使用 command、args 和可选 env。支持 stdio、Streamable HTTP 与 SSE。", TextWrapping = TextWrapping.Wrap });
        view.Children.Add(configuration); view.Children.Add(expose); view.Children.Add(save); view.Children.Add(refresh); view.Children.Add(copy); view.Children.Add(status);
        view.Loaded += (_, _) => { Changed -= Update; Changed += Update; Update(); };
        view.Unloaded += (_, _) => Changed -= Update;
        Update(); return view;
    }
}
