using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Extensions;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ExtensionUpdateRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var keeper = new Window { Content = new Grid() }; keeper.Activate(); keeper.AppWindow.Hide();
        var settingsDirectory = Path.Combine(directory, "settings");
        using var services = new AppServices(settingsDirectory);
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        SettingsWindow settings = null;
        try
        {
            var updates = services.ExtensionUpdates;
            await updates.CheckAsync();
            Check("repository API discovers actual release extension packages", updates.Catalog.Count >= 8 && updates.Catalog.Any(item => item.Id == "inkcanvas.pdf")
                && updates.Catalog.All(item => ExtensionFeed.Trusted(item.Asset.Url, "QiXinia/Ink-Canvas-HEHS")), updates.Status);
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("模块与插件"); await Task.Delay(200);
            Check("settings navigation uses the bundled Regular icon family", Descendants(settings.Content).OfType<NavigationViewItem>()
                .Where(item => item.Icon is FontIcon).All(item => ((FontIcon)item.Icon).FontFamily.Source == IconFontStyle.Family));
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                settings.GetType().GetMethod("RebuildContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(settings, null);
                settings.SelectCategory("模块与插件"); WindowChrome.ApplyTheme(settings, theme); await Task.Delay(60);
                foreach (int width in new[] { 720, 1100 })
                {
                    settings.AppWindow.Resize(new(width, 820)); await Task.Delay(100);
                    Check($"repository actions visible at width {width} theme {theme}", Descendants(settings.Content).OfType<Button>()
                        .Count(button => AutomationProperties.GetName(button) is "检查插件更新" or "一键更新全部插件") == 2);
                    var description = Descendants(settings.Content).OfType<TextBlock>().First(text => text.Text.Contains("已安装") && text.Text.Contains("仓库"));
                    var ink = ((SolidColorBrush)description.Foreground).Color;
                    Check($"repository descriptions have theme contrast {width} theme {theme}", theme == 1 ? ink.R < 100 : ink.R > 200);
                    var frame = (WindowFrame)settings.Content; var background = frame.Background;
                    frame.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.WhiteSmoke : Windows.UI.Color.FromArgb(255,32,32,32));
                    try { await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"plugins-{theme}-{width}.png")); }
                    finally { frame.Background = background; }
                }
            }
            var sample = updates.Catalog.Single(item => item.Id == "sample.guide-lines");
            Check("new repository plugin is installable", updates.CanInstall(sample));
            var install = Descendants(settings.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == sample.Name + "下载与更新");
            ((IInvokeProvider)new ButtonAutomationPeer(install).GetPattern(PatternInterface.Invoke)).Invoke();
            for (int poll = 0; poll < 300 && updates.IsBusy; poll++) await Task.Delay(100);
            Check("settings button downloads validates and stages a real repository plugin", !updates.IsBusy && updates.Pending.Any(item => item.Id == sample.Id), updates.Status);
            Check("plugin install keeps existing enable preferences", !services.ExtensionPreferences.EnabledPlugins.Contains(sample.Id));
            for (int poll = 0; poll < 20 && !Descendants(settings.Content).OfType<Button>()
                .Any(button => AutomationProperties.GetName(button) == "重启并应用插件" && button.Visibility == Visibility.Visible); poll++)
                await Task.Delay(50);
            Check("settings offers a single restart after preparation", Descendants(settings.Content).OfType<Button>()
                .Any(button => AutomationProperties.GetName(button) == "重启并应用插件" && button.Visibility == Visibility.Visible));
            settings.Close(); settings = null;
            // Startup applies the user's staged package before discovery, without touching the program's package tree.
            using var restarted = new AppServices(settingsDirectory);
            Check("restart applies prepared package", restarted.ExtensionInstalls.Pending.Count == 0 && restarted.Plugins.Status.Any(item => item.Id == sample.Id && item.Version == sample.Version));
            Check("downloaded external plugin stays disabled until enabled", !restarted.ExtensionPreferences.EnabledPlugins.Contains(sample.Id));
            await restarted.Plugins.SetEnabledAsync(sample.Id, true);
            Check("installed package starts through the real worker process", restarted.Plugins.Status.Single(item => item.Id == sample.Id).State == ExtensionState.Running);
            await restarted.Plugins.SetEnabledAsync(sample.Id, false);

            var newer = sample with { Version = "99.0.0", HostVersion = new AppVersion(99,0,0,0), Kind = RepositoryExtensionKind.Module };
            Check("incompatible native package explains host update requirement", updates.UnavailableReason(newer)?.Contains("需要先将主程序更新") == true);
            var tampered = new RepositoryAsset("bad.zip", sample.Asset.Url, 10, new string('0',64), null);
            using var downloader = new UpdateService(transport: new UpdateTransport(new BytesHandler()), cache: new UpdateCache(Path.Combine(directory, "bad-cache")));
            bool rejected = false;
            try { await downloader.DownloadAssetAsync(tampered); } catch (HttpRequestException) { rejected = true; }
            Check("shared update downloader rejects corrupt plugins", rejected);
            await CheckBatch(services, directory, checks);
        }
        catch (Exception exception) { Check("plugin update regression exception", false, exception.ToString()); }
        finally
        {
            settings?.Close(); shell.Dispose();
            await File.WriteAllTextAsync(Path.Combine(directory, "plugin-updates.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            keeper.Close(); Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }

    private static async Task CheckBatch(AppServices services, string directory, List<object> checks)
    {
        string libraryVersion = NextVersion("inkcanvas.library");
        string pdfVersion = NextVersion("inkcanvas.pdf");
        byte[] library = Package("inkcanvas.library", libraryVersion);
        byte[] pdf = Package("inkcanvas.pdf", pdfVersion);
        string hash = Convert.ToHexStringLower(SHA256.HashData(library));
        const string repository = "QiXinia/Ink-Canvas-HEHS";
        object Asset(string id, string version, byte[] data, string digest) => new { name = id + "-" + version + ".zip", size = data.Length,
            browser_download_url = $"https://github.com/{repository}/releases/download/v3.8.0/{id}-{version}.zip", digest = "sha256:" + digest };
        string feed = JsonSerializer.Serialize(new[] { new { tag_name = "v3.8.0", draft = false, prerelease = false,
            assets = new[] { Asset("inkcanvas.library", libraryVersion, library, hash), Asset("inkcanvas.pdf", pdfVersion, pdf, new string('0',64)) } } });
        using var coordinator = new ExtensionUpdateCoordinator(services, new UpdateService(transport: new UpdateTransport(new BatchHandler(feed, library, pdf), Array.Empty<string>()),
            cache: new UpdateCache(Path.Combine(directory, "batch-cache"))));
        await coordinator.CheckAsync();
        Check("installed package versions are compared independently", coordinator.Catalog.Count(item => coordinator.HasUpdate(item)) == 2);
        await coordinator.UpdateAllAsync();
        Check("update all preserves successful packages when another package fails", coordinator.Pending.Any(item => item.Id == "inkcanvas.library")
            && !coordinator.Pending.Any(item => item.Id == "inkcanvas.pdf") && coordinator.Status.Contains("1 个未完成"));
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        static string NextVersion(string id)
        {
            var manifest = Extensions.ExtensionPackages.Read(Path.Combine(AppContext.BaseDirectory, "Modules", id, "module.json"));
            if (!AppVersion.TryParse(manifest.Version, out var version)) throw new InvalidDataException("Invalid installed module version.");
            return new AppVersion(version.Major, version.Minor, checked(version.Patch + 1), 0).ToString();
        }
        static byte[] Package(string id, string version)
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = Extensions.ExtensionPackages.Read(Path.Combine(AppContext.BaseDirectory, "Modules", id, "module.json")) with { Version = version };
                using (var writer = new StreamWriter(archive.CreateEntry(id + "/module.json").Open())) writer.Write(JsonSerializer.Serialize(manifest));
                using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Modules", id, manifest.EntryAssembly));
                using var output = archive.CreateEntry(id + "/" + manifest.EntryAssembly).Open(); input.CopyTo(output);
            }
            return memory.ToArray();
        }
    }
    private sealed class BatchHandler(string feed, byte[] library, byte[] pdf) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri.Host == "api.github.com"
                ? new StringContent(feed) : new ByteArrayContent(request.RequestUri.AbsolutePath.Contains("library") ? library : pdf) });
    }
    private sealed class BytesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
