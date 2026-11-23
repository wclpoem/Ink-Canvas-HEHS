using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class LibraryRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "文稿");
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        Directory.CreateDirectory(services.Settings.Archive.DefaultDirectory);
        bool disabled = Environment.GetCommandLineArgs().Contains("--library-disabled");
        if (disabled) services.ExtensionPreferences.DisabledModules.Add("inkcanvas.library");
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        try
        {
            var library = services.Modules.GetExport<IBoardLibraryService>();
            if (disabled)
            {
                Check("disabled library has no export or toolbar command", library is null && !shell.ExtensionCommands.Any(command => command.PluginId == "inkcanvas.library"));
                string standalone = Path.Combine(directory, "without-library.icink");
                await services.Board.SaveBoardAsync(standalone);
                Check("base board storage works without the library plugin", File.Exists(standalone));
                return;
            }
            Check("native library module loaded", library is not null);
            if (library is null) throw new InvalidOperationException(string.Join("\n", services.Modules.Status));
            shell.ToggleBoard();
            services.Board.Background = BoardBackground.White;
            for (int n = 0; n < 1000; n++)
            {
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, Color = InkColor.FromPackedArgb(0xFFE24D40) });
                stroke.AddPoint(20 + n % 50 * 20, 20 + n / 50 * 20); stroke.AddPoint(35 + n % 50 * 20, 34 + n / 50 * 20); stroke.Complete();
                services.Board.Current.Document.Add(stroke);
            }
            services.Board.InsertPageAfterCurrent(); services.Board.Background = BoardBackground.Grid;
            var line = new InkStroke(new StrokeAttributes { BaseWidth = 8 }); line.AddPoint(20, 30); line.AddPoint(500, 340); line.Complete(); services.Board.Current.Document.Add(line);
            services.Board.AddMedia(new() { Data = new byte[32 * 1024 * 1024], Name = "large.mp4" });
            string fixture = Path.Combine(services.Settings.Archive.DefaultDirectory, "函数课堂.icink");
            var watch = Stopwatch.StartNew(); await services.Board.SaveBoardAsync(fixture);
            var cover = await BoardPreview.ReadAsync(fixture);
            Check("new package embeds a bounded real cover", cover.Info?.PageCount == 2 && cover.Cover is { Length: > 100 and < 2 * 1024 * 1024 }, new { milliseconds = watch.ElapsedMilliseconds, bytes = cover.Cover?.Length });
            await File.WriteAllBytesAsync(Path.Combine(directory, "embedded-cover.png"), cover.Cover ?? Array.Empty<byte>());
            var secondPage = await BoardService.ReadPagePreviewAsync(fixture, 1);
            Check("on-demand page preview skips a 32 MB video", secondPage.PageCount == 2 && secondPage.Png is { Length: > 100 });
            string sub = Directory.CreateDirectory(Path.Combine(services.Settings.Archive.DefaultDirectory, "数学")).FullName;
            File.Copy(fixture, Path.Combine(sub, "二次函数.icink"), true);
            Check("normal toolbar exposes library command", shell.ExtensionCommands.Any(command => command.PluginId == "inkcanvas.library"));
            library.Open();
            Window window = await LibraryWindowAsync();
            var files = Descendants(window.Content).OfType<GridView>().Single();
            await Until(() => files.Items.Count == 2);
            Check("WinUI library lists folders and whiteboards", files.Items.Count == 2 && Descendants(window.Content).OfType<WindowFrame>().Any()
                && window.SystemBackdrop is MicaBackdrop);
            files.SelectedItem = files.Items.Cast<object>().Single(item => Value(item, "Path") == fixture);
            await Task.Delay(200);
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "library-initial.png"));
            var preview = (Image)window.GetType().GetField("_preview", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            await Until(() => preview.Source is not null);
            Check("selecting a real document displays its preview", preview.Source is not null);
            var selectedEntry = files.SelectedItem;
            Check("default document grid virtualizes thumbnail cards", files.ItemsPanelRoot is ItemsWrapGrid);
            Check("folder breadcrumb exposes clickable ancestry", Descendants(window.Content).OfType<BreadcrumbBar>().Single().ItemsSource is System.Collections.IEnumerable crumbs
                && crumbs.Cast<object>().Count() >= 2);
            Invoke(FindButton(window, "列表视图")); await Task.Delay(150);
            Check("list view retains selection and preview", files.ItemsPanelRoot is ItemsStackPanel && ReferenceEquals(files.SelectedItem, selectedEntry) && preview.Source is not null);
            Invoke(FindButton(window, "缩略图视图")); await Task.Delay(150);
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(window, theme); await Task.Delay(150);
                foreach (int width in new[] { 400, 620, 840, 1000, 1420 })
                {
                    double dpi = WindowChrome.GetDpiScale(window);
                    window.AppWindow.Resize(new((int)(width * dpi), (int)(680 * dpi))); await Task.Delay(100);
                    var root = (FrameworkElement)window.Content;
                    var inspector = Field<Border>(window, "_inspectorCard");
                    var content = Field<Grid>(window, "_root");
                    Check($"inspector adapts at {width} theme {theme}", inspector.Visibility == (content.ActualWidth >= 880 ? Visibility.Visible : Visibility.Collapsed));
                    if (width >= 880)
                    {
                        var color = ((SolidColorBrush)inspector.Background).Color;
                        Check($"inspector surface follows theme {theme} at {width}", theme == 1 ? color.R > 200 : color.R < 100);
                    }
                    Check($"search and view controls fit at {width} theme {theme}", new FrameworkElement[]
                        { Field<AutoSuggestBox>(window, "_search"), Field<ComboBox>(window, "_sort"), FindButton(window, "列表视图") }.All(control => Fits(control, root)));
                    Check($"library renders at {width} theme {theme}", await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"library-{theme}-{width}.png")));
                    if (width == 620)
                    {
                        Invoke(FindButton(window, "文稿详情"));
                        await Until(() => Field<bool>(window, "_inspectorInFlyout") && Field<Grid>(window, "_inspector").IsLoaded);
                        var floatingInspector = Field<Grid>(window, "_inspector");
                        Check($"compact inspector keeps the selected document theme {theme}", VisualTreeHelper.GetParent(floatingInspector) is not null && preview.Source is not null);
                        await BoardWindowRegression.SaveVisual(floatingInspector, Path.Combine(directory, $"library-inspector-{theme}.png"));
                        window.AppWindow.Resize(new((int)(1000 * dpi), (int)(680 * dpi)));
                        await Until(() => !Field<bool>(window, "_inspectorInFlyout"));
                        Check($"resizing restores the inspector from its flyout theme {theme}", Field<Border>(window, "_inspectorCard").Child == Field<Grid>(window, "_inspector"));
                    }
                }
            }
            for (int iteration = 0; iteration < 3; iteration++)
            {
                window.AppWindow.Resize(new(400, 360)); await Task.Delay(60);
                window.AppWindow.Resize(new(1240, 850)); await Task.Delay(60);
            }
            Check("repeated small-to-large resize keeps preview interactive", preview.Source is not null && FindButton(window, "预览下一页").IsEnabled);
            Invoke(FindButton(window, "预览下一页"));
            await Until(() => Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == "2 / 2"));
            Check("preview page navigation works", true);
            Invoke(FindButton(window, "收藏"));
            await Task.Delay(150);
            var locations = Descendants(window.Content).OfType<ListView>().Single(list => AutomationProperties.GetName(list) == "常用文稿位置"); locations.SelectedItem = locations.Items.Cast<object>().Single(item => Value(item, "Title") == "收藏");
            await Until(() => files.Items.Count == 1);
            Check("favorites filter uses persisted file paths", Value(files.Items[0], "Path") == fixture);
            var folders = Descendants(window.Content).OfType<ListView>().Single(list => AutomationProperties.GetName(list) == "文稿文件夹");
            folders.SelectedItem = folders.Items.Cast<object>().Single(item => Value(item, "Path") == services.Settings.Archive.DefaultDirectory);
            await Until(() => files.Items.Count == 2);
            var search = Descendants(window.Content).OfType<AutoSuggestBox>().Single(); search.Text = "二次";
            // Programmatic text changes are refreshed via the same user-facing refresh command.
            Invoke(FindButton(window, "刷新文稿"));
            await Until(() => files.Items.Count == 1);
            Check("recursive folder search finds matching whiteboards", Value(files.Items[0], "Path") == Path.Combine(sub, "二次函数.icink"));
            search.Text = "没有此文稿"; Invoke(FindButton(window, "刷新文稿")); await Until(() => files.Items.Count == 0);
            var empty = Field<StackPanel>(window, "_empty");
            Check("empty state is centered inside document canvas", empty.Visibility == Visibility.Visible && empty.ActualWidth > 0
                && Math.Abs(empty.TransformToVisual((UIElement)empty.Parent).TransformPoint(new(0, 0)).X * 2 + empty.ActualWidth - ((FrameworkElement)empty.Parent).ActualWidth) < 3);
            Check("empty selection hides invalid inspector actions", Field<Grid>(window, "_selectedDetails").Visibility == Visibility.Collapsed
                && FindButton(window, "打开文稿").Visibility == Visibility.Collapsed);
            Invoke(Descendants(window.Content).OfType<Button>().Single(button => button.Content?.ToString() == "清除筛选")); await Until(() => files.Items.Count == 2);
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "library-cleared-selection.png"));
            window.Close();
            var open = library.PickOpenAsync(new[] { ".icink" }); window = await LibraryWindowAsync();
            files = Descendants(window.Content).OfType<GridView>().Single(); await Until(() => files.Items.Count == 2);
            files.SelectedItem = files.Items.Cast<object>().Single(item => Value(item, "Path") == fixture);
            await Until(() => FindButton(window, "打开文稿").IsEnabled);
            Invoke(FindButton(window, "打开文稿"));
            Check("open picker returns selected path", await open.WaitAsync(TimeSpan.FromSeconds(5)) == fixture);
            var cancelled = library.PickOpenAsync(new[] { ".icink" }); window = await LibraryWindowAsync(); window.Close();
            Check("cancel resolves picker without altering the board", await cancelled is null && services.Board.PageCount == 2);
            shell.SaveStrokes(); window = await LibraryWindowAsync();
            var saveButton = FindButton(window, "保存文稿");
            await Until(() => saveButton.IsEnabled);
            var name = Descendants(window.Content).OfType<TextBox>().Single(input => input.Header?.ToString() == "文稿名称"); name.Text = "课堂另存";
            Invoke(saveButton);
            string saved = Path.Combine(services.Settings.Archive.DefaultDirectory, "课堂另存.icink");
            await Until(() => File.Exists(saved) && services.Archives.LastSave?.Path == saved);
            var reopened = new BoardService();
            try { await reopened.LoadBoardAsync(saved); Check("normal SaveStrokes flow preserves editable pages", reopened.PageCount == 2 && reopened.Pages[0].Document.Count == 1000 && reopened.Pages[1].Document.Count == 1); }
            finally { foreach (var page in reopened.Pages) page.Dispose(); }
            library.Open(); window = await LibraryWindowAsync();
            locations = Descendants(window.Content).OfType<ListView>().Single(list => AutomationProperties.GetName(list) == "常用文稿位置");
            await Until(() => locations.Items.Count == 2); locations.SelectedItem = locations.Items.Cast<object>().Single(item => Value(item, "Title") == "最近使用");
            files = Descendants(window.Content).OfType<GridView>().Single();
            await Until(() => files.Items.Count > 0);
            Check("saved documents appear in recent files", files.Items.Cast<object>().Any(item => Value(item, "Path") == saved));
            window.Close();
        }
        catch (Exception ex) { checks.Add(new { check = "library regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.Where(window => window.GetType().Name == "LibraryWindow").ToArray()) window.Close();
            services.Settings.Archive.SaveOnExit = false;
            foreach (var board in shell.OpenWindows.OfType<Views.BoardWindow>().ToArray()) board.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "library-check.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
        async Task<Window> LibraryWindowAsync()
        {
            await Until(() => shell.OpenWindows.Any(window => window.GetType().Name == "LibraryWindow"));
            var window = shell.OpenWindows.Single(window => window.GetType().Name == "LibraryWindow");
            await Until(() => (window.Content as FrameworkElement)?.XamlRoot is not null);
            return window;
        }
    }
    private static string Value(object item, string property) => item.GetType().GetProperty(property)?.GetValue(item)?.ToString();
    private static Button FindButton(Window window, string name) => Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
    private static T Field<T>(Window window, string name) => (T)window.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return element.ActualWidth > 0 && bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("文稿库验证超时。"); await Task.Delay(40); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
