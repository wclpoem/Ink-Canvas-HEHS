using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class CloudSyncRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "boards");
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        SettingsWindow window = null;
        try
        {
            Check("cloud module exports its optional service", services.Modules.HasExport<ICloudSyncService>());
            Check("cloud module contributes its own settings page", services.ModuleSettings.Pages.Any(page => page.Name == "云同步"));
            window = new SettingsWindow(services, shell: shell); window.Activate(); window.SelectCategory("云同步");
            await Task.Delay(120);
            var root = (FrameworkElement)window.Content;
            var endpoint = Descendants(root).OfType<TextBox>().Single(box => box.Header?.ToString() == "HTTPS 同步服务地址");
            Check("distributed plugin has no built-in service address", string.IsNullOrEmpty(endpoint.Text));
            Check("cloud setup requires explicit opt-in", !Toggle("自动备份白板到云端").IsOn && !Toggle("同步个人书写和外观偏好").IsOn);
            var host = new CloudSyncHost(services);
            using (host)
            {
                var portable = JsonDocument.Parse(host.ExportPreferences());
                Check("preferences exclude credentials device paths and rosters", portable.RootElement.EnumerateObject().All(property =>
                    new[] { "appearance", "canvas", "gesture", "inkToShape", "behavior", "features" }.Contains(property.Name)));
                bool rejected = false;
                try { host.ImportPreferences("{\"ai\":{\"endpoint\":\"blocked\"}}"); }
                catch (InvalidDataException) { rejected = true; }
                Check("import rejects sensitive settings groups", rejected);
            }
            string configuration = Environment.GetEnvironmentVariable("INKCANVAS_CLOUD_TEST_CONNECTION");
            if (!string.IsNullOrEmpty(configuration))
            {
                using var testConnection = JsonDocument.Parse(await File.ReadAllTextAsync(configuration));
                endpoint.Text = testConnection.RootElement.GetProperty("serviceUrl").GetString();
                Descendants(root).OfType<TextBox>().Single(box => box.Header?.ToString() == "账号").Text = testConnection.RootElement.GetProperty("email").GetString();
                Descendants(root).OfType<PasswordBox>().Single(box => box.Header?.ToString() == "密码").Password = testConnection.RootElement.GetProperty("password").GetString();
                Invoke(Button("登录 / 使用邀请码注册"));
                await Until(() => StatusStarts("已登录"));
                Check("native settings sign in over HTTPS", Toggle("自动备份白板到云端").IsEnabled);
                var stroke = new InkStroke(new StrokeAttributes()); stroke.AddPoint(40, 40); stroke.AddPoint(240, 140); stroke.Complete();
                services.Board.Current.Document.Add(stroke);
                Toggle("同步个人书写和外观偏好").IsOn = true;
                Invoke(Button("立即同步"));
                await Until(() => StatusStarts("同步完成"));
                Check("personal preferences sync independently without saving boards", !Directory.Exists(services.Settings.Archive.DefaultDirectory)
                    || Directory.GetFiles(services.Settings.Archive.DefaultDirectory, "*.icink", SearchOption.AllDirectories).Length == 0);
                Toggle("自动备份白板到云端").IsOn = true;
                await Until(() => Directory.Exists(services.Settings.Archive.DefaultDirectory)
                    && Directory.GetFiles(services.Settings.Archive.DefaultDirectory, "*.icink", SearchOption.AllDirectories).Length > 0 && StatusStarts("同步完成"));
                Check("automatic cloud sync saves an editable local board", Directory.GetFiles(services.Settings.Archive.DefaultDirectory, "*.icink", SearchOption.AllDirectories).Length > 0);
                Invoke(Button("查看云端文稿"));
                await Until(() => Descendants(root).OfType<SettingsRow>().Any());
                Check("cloud files are listed in native settings controls", Descendants(root).OfType<SettingsRow>().Any(row => row.Title.EndsWith(".icink")));
                Invoke(Descendants(root).OfType<Button>().First(button => button.Content?.ToString() == "恢复副本"));
                await Until(() => StatusStarts("已恢复副本"));
                string downloaded = Directory.GetFiles(Path.Combine(services.Settings.Archive.DefaultDirectory, "CloudDownloads"), "*.icink").Single();
                var restored = new Features.Board.BoardService();
                await restored.LoadBoardAsync(downloaded);
                Check("restored board retains editable ink", restored.Current.Document.Count == 1);
                foreach (var page in restored.Pages) page.Dispose();
                string state = File.ReadAllText(Path.Combine(services.Store.Directory, "PluginData", "inkcanvas.cloud-sync", "sync.json"));
                Check("plugin JSON contains no password or access tokens", !state.Contains(testConnection.RootElement.GetProperty("password").GetString())
                    && !state.Contains("access_token") && !state.Contains("refresh_token"));
                Toggle("自动备份白板到云端").IsOn = false;
                Invoke(Button("注销当前设备"));
                await Until(() => StatusStarts("已注销"));
                Check("logout disables cloud uploads", !Toggle("自动备份白板到云端").IsOn && !Toggle("自动备份白板到云端").IsEnabled);
            }
            Button Button(string label) => Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetName(button) == label);
            ToggleSwitch Toggle(string header) => Descendants(root).OfType<ToggleSwitch>().Single(toggle => toggle.Header?.ToString() == header);
            bool StatusStarts(string text) => Descendants(root).OfType<TextBlock>().Any(block => block.Text.StartsWith(text, StringComparison.Ordinal));
        }
        catch (Exception error) { checks.Add(new { check = "cloud sync regression exception", passed = false, error = error.GetType().Name }); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "cloud-sync.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
    }
    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 1500; attempt++) { if (condition()) return; await Task.Delay(40); }
        throw new TimeoutException();
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
