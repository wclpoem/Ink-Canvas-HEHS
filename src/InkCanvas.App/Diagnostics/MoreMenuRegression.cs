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

namespace InkCanvas.App.Diagnostics;

internal static class MoreMenuRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "documents");
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        bool disabled = Environment.GetCommandLineArgs().Contains("--moremenu-disabled");
        if (disabled)
            foreach (string module in new[] { "inkcanvas.library", "inkcanvas.ai-assistant", "inkcanvas.pdf" })
                services.ExtensionPreferences.DisabledModules.Add(module);
        using var shell = new ShellController(services);
        try
        {
            shell.Start(); shell.StartInClickThrough();
            await Task.Delay(250);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            foreach (int theme in new[] { 1, 2 })
            foreach (int scale in new[] { 50, 100, 150 })
            {
                services.Settings.Appearance.Theme = theme;
                services.Settings.Appearance.FloatingBarScale = scale;
                services.ApplySettings();
                bar.Reposition(services.Settings.Appearance.FloatingBarBottomMargin, scale, recenter: false);
                Invoke(Buttons(bar.Bar).Single(button => button.Label == "工具"));
                await Task.Delay(150);
                var panel = bar.GetOpenPanel(BarPanelKind.More);
                var buttons = Buttons(panel.Content).ToArray();
                var labels = buttons.Select(button => button.Label).ToArray();
                Check($"tools precede files and application actions {theme}-{scale}", labels.TakeLast(4)
                    .SequenceEqual(new[] { "保存墨迹", "打开文件", "设置", "退出" }) && labels[0] == "倒计时");
                Check($"desktop menu consolidates insertion and duplicate plugins {theme}-{scale}", labels.Contains("插入")
                    && !labels.Any(label => label is "插入图片" or "插入视频" or "PDF 导入画板" or "AI 助手" or "文稿库" or "指南"));
                Check($"PDF viewer remains a tool {theme}-{scale}", disabled || Array.IndexOf(labels, "打开 PDF") is var pdf && pdf >= 0 && pdf < Array.IndexOf(labels, "保存墨迹"));
                await BoardWindowRegression.SaveVisual((FrameworkElement)panel.Content, Path.Combine(directory, $"more-{theme}-{scale}.png"));
                Invoke(buttons.Single(button => button.Label == "插入"));
                await Task.Delay(120);
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)panel.Content).XamlRoot)
                    .Single(item => Buttons(item.Child).Any(button => button.Label == "插入图片"));
                var insert = Buttons(popup.Child).ToArray();
                Check($"insertion opens a secondary menu {theme}-{scale}", insert.Select(button => button.Label)
                    .SequenceEqual(new[] { "插入图片", "插入视频", "插入 PDF" }) && bar.Bar.IsExpanded
                    && insert.Single(button => button.Label == "插入 PDF").IsEnabled == !disabled);
                Invoke(Descendants(popup.Child).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "关闭面板"));
                await Task.Delay(40);
                Check($"closing insertion returns to more tools {theme}-{scale}", bar.Bar.IsExpanded && !popup.IsOpen);
                bar.ClosePanels();
            }

            if (!disabled)
            {
                shell.OpenStrokes();
                var library = await LibraryWindow();
                Check("open file enters the library browser", library.GetType().GetProperty("Mode")?.GetValue(library)?.ToString() == "Browse");
                library.Close();
                shell.SaveStrokes();
                library = await LibraryWindow();
                Check("save ink uses the library save window", library.GetType().GetProperty("Mode")?.GetValue(library)?.ToString() == "Save");
                library.Close();
                await Task.Delay(40);
            }

            shell.OpenGuide();
            await Task.Delay(180);
            var settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            var workspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            Check("guide opens within settings", workspace.SelectedPage == "操作指南" && !shell.OpenWindows.OfType<InfoWindow>().Any());
            settings.SelectCategory("快捷键");
            await Task.Delay(80);
            Check("shortcut guide preserves all keyboard commands", Descendants(settings.Content).OfType<TextBlock>().Count(text => text.Text == "+") == 12);
            Check("guide shows three navigation levels", Descendants(settings.Content).OfType<BreadcrumbBar>().Single().ItemsSource is IEnumerable<string> path
                && path.SequenceEqual(new[] { "关于与更新", "操作指南", "快捷键" }));
            Check("back restores the parent guide", workspace.GoBack() && workspace.SelectedPage == "操作指南");
            settings.SelectCategory("书写与选择");
            Check("writing guide participates in settings search", settings.SearchSettings("克隆").Any(entry => entry.Category == "书写与选择"));
            services.Settings.Appearance.Theme = 1;
            services.ApplySettings();
            typeof(SettingsWindow).GetMethod("RebuildContent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(settings, null);
            await Task.Delay(120);
            workspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            Check("theme changes preserve nested navigation history", workspace.SelectedPage == "书写与选择" && workspace.GoBack()
                && workspace.SelectedPage == "操作指南");
            settings.SelectCategory("快捷键");
            await Task.Delay(80);
            await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "settings-guide.png"));
            settings.Close();

            shell.ToggleBoard();
            await Task.Delay(180);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            Invoke(Descendants(board.Content).OfType<Button>().First(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "更多")));
            await Task.Delay(120);
            var boardPanel = board.ToolPanels.Single();
            await BoardWindowRegression.SaveVisual((FrameworkElement)boardPanel.Content, Path.Combine(directory, "board-more.png"));
            var boardLabels = Descendants(boardPanel.Content).OfType<Button>().Select(button => ToolTipService.GetToolTip(button)?.ToString()
                ?? (button.Content as StackPanel)?.Children.OfType<TextBlock>().Select(text => text.Text).FirstOrDefault()).Where(text => text is not null).ToArray();
            Check("board retains its AI tool when available", disabled || boardLabels.Contains("AI 助手"));
            Check("board files and settings follow plugin tools", boardLabels.TakeLast(5)
                .SequenceEqual(new[] { "保存墨迹", "打开文件", "文件选项", "设置", "退出" }));
            if (!disabled)
            {
                Invoke(Descendants(boardPanel.Content).OfType<Button>().Single(button => button.Content is StackPanel row
                    && row.Children.OfType<TextBlock>().Any(text => text.Text == "打开文件")));
                var library = await LibraryWindow();
                Check("board open file enters the library browser", library.GetType().GetProperty("Mode")?.GetValue(library)?.ToString() == "Browse");
                library.Close();
                Invoke(Descendants(board.Content).OfType<Button>().First(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "更多")));
                await Task.Delay(120);
                boardPanel = board.ToolPanels.Single();
            }
            var boardInsert = Descendants(boardPanel.Content).OfType<Button>().Single(button => ToolTipService.GetToolTip(button)?.ToString() == "插入");
            Invoke(boardInsert);
            await Task.Delay(120);
            var boardInsertion = board.ToolPanels.Single();
            Check("board shares the insertion submenu", Buttons(boardInsertion.Content).Select(button => button.Label)
                .SequenceEqual(new[] { "插入图片", "插入视频", "插入 PDF" }));
            await BoardWindowRegression.SaveVisual((FrameworkElement)boardInsertion.Content, Path.Combine(directory, "board-insert.png"));
        }
        catch (Exception ex) { checks.Add(new { check = "more menu exception", passed = false, error = ex.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.Where(window => window.GetType().Name == "LibraryWindow").ToArray()) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "more-menu.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        async Task<Window> LibraryWindow()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                var window = shell.OpenWindows.FirstOrDefault(window => window.GetType().Name == "LibraryWindow");
                if (window is not null && (window.Content as FrameworkElement)?.XamlRoot is not null) return window;
                await Task.Delay(40);
            }
            throw new TimeoutException("Library window did not open.");
        }
    }

    internal static async Task<DependencyObject> OpenInsertionMenu(DependencyObject content)
    {
        Invoke(Buttons(content).Single(button => button.Label == "插入"));
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(30);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)content).XamlRoot)
                .FirstOrDefault(item => Buttons(item.Child).Any(button => button.Label == "插入图片"));
            if (popup is not null) return popup.Child;
        }
        throw new TimeoutException("Insertion menu did not open.");
    }

    private static IEnumerable<TouchIconButton> Buttons(DependencyObject root) => Descendants(root).OfType<TouchIconButton>();
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
