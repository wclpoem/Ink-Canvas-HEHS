using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ModuleToolbarRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Appearance.Theme = 1;
        bool disabled = Environment.GetCommandLineArgs().Contains("--without-feature-modules");
        if (disabled) services.ExtensionPreferences.DisabledModules.UnionWith(new[] { "inkcanvas.pdf", "inkcanvas.analytic-geometry" });
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        try
        {
            await Task.Delay(250);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var commands = shell.ExtensionCommands;
            bool pdf = services.Modules.HasExport<IPdfWorkspaceService>();
            bool geometry = services.Modules.HasExport<IAnalyticGeometryService>();
            Check("module entries require loaded services", commands.Count(command => command.PluginId == "inkcanvas.pdf") == (pdf ? 2 : 0)
                && commands.Any(command => command.PluginId == "inkcanvas.analytic-geometry") == geometry, services.Modules.Status);
            if (disabled) Check("disabled modules have no toolbar entries", !pdf && !geometry && commands.Count == 0);
            CheckFloatingEntries("initial floating toolbar");
            var originalPosition = bar.AppWindow.Position;
            var originalSize = bar.AppWindow.Size;
            Invoke(Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "工具"));
            await Task.Delay(150);
            var menu = bar.GetOpenPanel(BarPanelKind.More);
            Check("tool menu opens with current module entries", menu is not null);
            await BoardWindowRegression.SaveVisual((FrameworkElement)menu.Content, Path.Combine(directory, "module-toolbar.png"));
            bar.ClosePanels();

            shell.ToggleBoard(); await Task.Delay(200);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            OpenBoardMore(); await Task.Delay(150);
            var boardMenu = board.ToolPanels.Single();
            foreach (var command in commands.Where(command => command.PluginId != "inkcanvas.library"
                && !(command.PluginId == "inkcanvas.pdf" && command.CommandId == "import")))
            {
                var button = Descendants(boardMenu.Content).OfType<Button>().Single(item => item.Content is StackPanel panel
                    && panel.Children.OfType<TextBlock>().Any(text => text.Text == command.Title));
                Check("board icon matches " + command.Title,
                    Descendants(button).OfType<FontIcon>().Single().Glyph == ExtensionCommandIcons.For(command));
            }
            if (pdf)
            {
                // Keep the old row and pinned panel alive while the runtime withdraws its service.
                var stale = commands.Single(command => command.PluginId == "inkcanvas.pdf" && command.CommandId == "open");
                var staleButton = Descendants(bar.Bar.CreateMorePanelContent()).OfType<TouchIconButton>().Single(button => button.Label == stale.Title);
                Descendants(boardMenu.Content).OfType<PopupHeader>().Single().IsPinned = true;
                services.Modules.Invoke<IPdfWorkspaceService>(_ => throw new InvalidOperationException("Module toolbar regression failure"));
                await Task.Delay(150);
                Check("failed module withdraws floating entries", !services.Modules.HasExport<IPdfWorkspaceService>()
                    && !Descendants(bar.Bar.CreateMorePanelContent()).OfType<TouchIconButton>().Any(button => button.Label is "打开 PDF" or "PDF 导入画板"));
                Check("failed module closes stale pinned board menu", !board.ToolPanels.Any());
                await shell.ExecuteExtensionCommandAsync(stale);
                Invoke(staleButton); await Task.Delay(80);
                Check("stale module command does not launch a notice", !shell.OpenWindows.OfType<NoticeWindow>().Any());
                OpenBoardMore(); await Task.Delay(100);
                Check("reopened board menu excludes unavailable PDF", !board.ToolPanels.SelectMany(window => Descendants(window.Content))
                    .OfType<TextBlock>().Any(text => text.Text is "打开 PDF" or "PDF 导入画板"));
            }
            board.Close(); await Task.Delay(100);
            if (geometry)
            {
                services.Modules.Invoke<IAnalyticGeometryService>(_ => throw new InvalidOperationException("Geometry toolbar regression failure"));
                await Task.Delay(100);
                Check("geometry failure also withdraws its toolbar entry", !shell.ExtensionCommands.Any(command => command.PluginId == "inkcanvas.analytic-geometry")
                    && !Descendants(bar.Bar.CreateMorePanelContent()).OfType<TouchIconButton>().Any(button => button.Label == "解析几何"));
            }
            CheckFloatingEntries("floating toolbar after module changes");

            var openMissing = shell.OpenPdfAsync();
            await Task.Delay(150);
            var notice = shell.OpenWindows.OfType<NoticeWindow>().Single();
            var repeated = shell.OpenPdfAsync();
            Check("repeated missing module notices share one window", shell.OpenWindows.OfType<NoticeWindow>().Count() == 1);
            Check("module notice uses an independent WinUI window", notice.Content is WindowFrame && notice.ExtendsContentIntoTitleBar
                && notice.AppWindow.ClientSize.Width > 400 && notice.AppWindow.ClientSize.Height > 200
                && !ReferenceEquals(((FrameworkElement)notice.Content).XamlRoot, ((FrameworkElement)bar.Content).XamlRoot));
            var close = Descendants(notice.Content).OfType<Button>().Single(button => button.Content?.ToString() == "知道了");
            Check("module notice close button fits the visible window", IsInside(close, (FrameworkElement)notice.Content));
            Check("floating toolbar has no clipped modal dialog", !VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)bar.Content).XamlRoot)
                .Any(popup => Descendants(popup.Child).OfType<ContentDialog>().Any()));
            await BoardWindowRegression.SaveVisual((FrameworkElement)notice.Content, Path.Combine(directory, "module-notice.png"));
            Invoke(close);
            await Task.WhenAll(openMissing, repeated).WaitAsync(TimeSpan.FromSeconds(3));
            Check("closing module notice completes all pending opens", !shell.OpenWindows.OfType<NoticeWindow>().Any());

            bar.SetFolded(true);
            var foldedOpen = shell.OpenPdfAsync(); await Task.Delay(100);
            notice = shell.OpenWindows.OfType<NoticeWindow>().Single();
            Check("missing module notice remains usable with folded toolbar", notice.AppWindow.IsVisible && notice.AppWindow.ClientSize.Width > 400);
            notice.Close(); await foldedOpen.WaitAsync(TimeSpan.FromSeconds(3));
            bar.SetFolded(false); await Task.Delay(100);
            Check("module refresh and notices preserve toolbar bounds", bar.AppWindow.Position == originalPosition && bar.AppWindow.Size == originalSize,
                new { originalPosition, originalSize, position = bar.AppWindow.Position, size = bar.AppWindow.Size });

            void CheckFloatingEntries(string name)
            {
                var buttons = Descendants(bar.Bar.CreateMorePanelContent()).OfType<TouchIconButton>().ToArray();
                var current = shell.ExtensionCommands.Where(command => command.PluginId is not ("inkcanvas.library" or "inkcanvas.ai-assistant")
                    && !(command.PluginId == "inkcanvas.pdf" && command.CommandId == "import")).ToArray();
                Check(name + " matches available commands", current.All(command => buttons.Count(button => button.Label == command.Title) == 1)
                    && buttons.Count(button => button.Label is "打开 PDF" or "PDF 导入画板" or "解析几何")
                        == current.Count(command => command.PluginId is "inkcanvas.pdf" or "inkcanvas.analytic-geometry"));
                foreach (var command in current)
                    Check(name + " icon for " + command.Title,
                        buttons.Single(button => button.Label == command.Title).Glyph == ExtensionCommandIcons.For(command)
                        && ExtensionCommandIcons.For(command) != Glyphs.Tools);
            }
            void OpenBoardMore() => Invoke(Descendants(board.Content).OfType<Button>().First(button => Descendants(button)
                .OfType<TextBlock>().Any(text => text.Text == "更多")));
        }
        catch (Exception ex) { checks.Add(new { check = "module toolbar regression exception", pass = false, detail = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "module-toolbar.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            var windows = shell.OpenWindows.ToArray();
            shell.Dispose();
            foreach (var window in windows) if (window is not NoticeWindow) window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });
    }

    private static bool IsInside(FrameworkElement child, FrameworkElement root)
    {
        var origin = child.TransformToVisual(root).TransformPoint(default);
        return child.ActualWidth >= 100 && child.ActualHeight >= 40 && origin.X >= 0 && origin.Y >= 0
            && origin.X + child.ActualWidth <= root.ActualWidth + 1 && origin.Y + child.ActualHeight <= root.ActualHeight + 1;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
