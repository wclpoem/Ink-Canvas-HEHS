using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ArchiveRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Archive = new ArchiveSettings { DefaultDirectory = Path.Combine(directory, "archives"),
            AutomaticDirectory = Path.Combine(directory, "automatic"), Title = "初三函数复习", ClassName = "初三一班", Teacher = "王老师",
            FileNameTemplate = "{date}_{class}_{title}_{pages}页_{reason}", SaveOnExit = true };
        services.ApplySettings();
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        SettingsWindow settings = null;
        bool exitTest = Environment.GetCommandLineArgs().Contains("--archive-exit");
        try
        {
            shell.ToggleBoard(); await Task.Delay(200);
            Draw(); services.Board.Background = BoardBackground.Grid;
            services.Board.AddImage(new() { Name = "示例图片", Data = new byte[] { 1, 2, 3 }, Width = 40, Height = 40 });
            services.Board.InsertPageAfterCurrent(); Draw();
            services.Board.Background = BoardBackground.White;
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            if (exitTest)
            {
                services.SaveSettings();
                await WriteChecks();
                shell.Exit();
                return;
            }
            settings = new SettingsWindow(services, shell: shell);
            settings.Activate(); settings.SelectCategory("保存与备份"); await Task.Delay(200);
            var root = (FrameworkElement)settings.Content;
            Check("save settings expose real controls", Descendants(root).OfType<TextBox>().Count() >= 7
                && Descendants(root).OfType<ToggleSwitch>().Count() == 11 && Descendants(root).OfType<NumberBox>().Count() == 4);
            var title = Descendants(root).OfType<TextBox>().Single(item => AutomationProperties.GetName(item) == "课程或存档标题");
            title.Text = "函数与图像";
            await Task.Delay(400);
            Check("archive settings persist through the normal editor", services.Store.Load().Archive.Title == "函数与图像");
            Check("search includes filename and exit save settings", settings.SearchSettings("退出时自动保存").Any(item => item.Category == "保存与备份")
                && settings.SearchSettings("文件名模板").Any(item => item.Category == "保存与备份"));
            var save = Descendants(settings.Content).OfType<Button>().Single(item => AutomationProperties.GetName(item) == "立即备份完整白板");
            ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntil(() => services.Archives.LastAutomaticSave is not null);
            var result = services.Archives.LastAutomaticSave;
            Check("save now produces a complete archive", result is { PageCount: 2 } && File.Exists(result.Path));
            var metadata = await BoardService.ReadArchiveMetadataAsync(result.Path);
            Check("archive captures classroom information", metadata.Title == "函数与图像" && metadata.ClassName == "初三一班" && metadata.Teacher == "王老师");
            foreach (int theme in new[] { 1, 2 })
            {
                settings.Close();
                services.Settings.Appearance.Theme = theme;
                settings = new SettingsWindow(services, shell: shell);
                settings.Activate(); settings.SelectCategory("保存与备份"); await Task.Delay(300);
                var frame = (Controls.Controls.WindowFrame)settings.Content;
                frame.Background = new SolidColorBrush(theme == 1 ? Windows.UI.Color.FromArgb(255, 243, 243, 243) : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, "archive-settings-" + theme + ".png"));
                var scroll = Descendants(frame).OfType<ScrollViewer>().Single(item => item.Content is Border { Child: StackPanel panel }
                    && panel.Children.OfType<TextBlock>().Any(text => text.Text == "白板自动保存"));
                scroll.ChangeView(null, 560, null, true); await Task.Delay(80);
                frame.Background = new SolidColorBrush(theme == 1 ? Windows.UI.Color.FromArgb(255, 243, 243, 243) : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, "archive-settings-options-" + theme + ".png"));
            }
            settings.Close(); settings = null;
            Draw();
            board.Close();
            await WaitUntil(() => !shell.OpenWindows.OfType<BoardWindow>().Any());
            Check("normal board close waits for exit archive", services.Archives.LastAutomaticSave.Reason == "退出白板"
                && services.Archives.LastAutomaticSave.Path != result.Path);
            var restored = new BoardService();
            try
            {
                await restored.LoadBoardAsync(services.Archives.LastAutomaticSave.Path);
                Check("exit archive reopens all pages and papers", restored.PageCount == 2 && restored.PageBackground(0) == BoardBackground.Grid
                    && restored.PageBackground(1) == BoardBackground.White && restored.Pages[1].Document.Count == 2);
            }
            finally { foreach (var page in restored.Pages) page.Dispose(); }
            shell.ToggleBoard(); await Task.Delay(100); Draw();
            board = shell.OpenWindows.OfType<BoardWindow>().Single();
            string blocker = Path.Combine(directory, "blocked"); await File.WriteAllTextAsync(blocker, "keep");
            services.Settings.Archive.AutomaticDirectory = Path.Combine(blocker, "folder");
            var close = board.CloseWithArchiveAsync();
            await Task.Delay(160);
            Check("unwritable archive path keeps board open", shell.OpenWindows.OfType<BoardWindow>().Contains(board) && services.Archives.LastError is not null);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)board.Content).XamlRoot))
                foreach (var dialog in Descendants(popup.Child).OfType<ContentDialog>()) dialog.Hide();
            await close.WaitAsync(TimeSpan.FromSeconds(3));
            services.Settings.Archive.AutomaticDirectory = Path.Combine(directory, "automatic");
            Check("failed exit can be retried", await board.CloseWithArchiveAsync() && services.Archives.LastError is null);
        }
        catch (Exception ex) { checks.Add(new { check = "archive regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            if (!exitTest)
            {
                settings?.Close();
                services.Settings.Archive.SaveOnExit = false;
                foreach (var board in shell.OpenWindows.OfType<BoardWindow>().ToArray()) board.Close();
                await WriteChecks();
                shell.Dispose(); Application.Current.Exit();
            }
        }
        void Draw()
        {
            var stroke = new InkStroke(new StrokeAttributes());
            stroke.AddPoint(120, 130); stroke.AddPoint(230, 240); stroke.Complete(); services.Board.Current.Document.Add(stroke);
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        Task WriteChecks() => File.WriteAllTextAsync(Path.Combine(directory, "archive-check.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000) throw new TimeoutException("存档验证超时。");
            await Task.Delay(40);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
