using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class WindowResizeRegression
{
    internal static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var errors = new List<string>();
        int frames = 0;
        void Frame(object sender, object args) => frames++;
        void Exception(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            errors.Add(args.Exception.ToString());
            string phase = Path.Combine(directory, "phase.json");
            File.AppendAllText(Path.Combine(directory, "unhandled.txt"), (File.Exists(phase) ? File.ReadAllText(phase) : "opening " + directory)
                + Environment.NewLine + args.Exception + Environment.NewLine + Environment.StackTrace + Environment.NewLine);
        }
        Application.Current.UnhandledException += Exception;
        CompositionTarget.Rendering += Frame;
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services) { RosterDirectory = Path.Combine(directory, "roster") };
        shell.Start(); shell.StartInClickThrough();
        Window window = null;
        string target = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--resize-window="))?["--resize-window=".Length..] ?? "lottery";
        try
        {
            switch (target)
            {
                case "lottery":
                    services.Lottery.LoadNames(Enumerable.Range(1, 40).Select(index => "同学 " + index));
                    window = new LotteryWindow(services.Lottery, dataDirectory: shell.RosterDirectory);
                    break;
                case "pdf":
                    var pdf = services.Modules.GetExport<IPdfWorkspaceService>() ?? throw new InvalidOperationException("PDF module missing");
                    string fixture = Path.Combine(directory, "resize.pdf");
                    pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics", true)!
                        .GetMethod("CreateFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { fixture, 8 });
                    await pdf.OpenAsync(fixture);
                    window = shell.OpenWindows.OfType<IDocumentWorkspace>().Single().Window;
                    break;
                case "settings": window = new SettingsWindow(services, shell: shell); break;
                case "timer": window = new CountdownWindow(); break;
                case "guide":
                    var guideSettings = new SettingsWindow(services);
                    guideSettings.SelectCategory("操作指南");
                    window = guideSettings;
                    break;
                case "geometry":
                    services.Modules.GetExport<IAnalyticGeometryService>().Open();
                    window = shell.OpenWindows.Single(item => item.GetType().Name == "GeometryWorkbenchWindow");
                    break;
                case "assistant":
                    services.Modules.GetExport<IAiAssistantService>().Review(1, 1, 0, "# 测试讲解\n这是用于窗口缩放验收的本地内容。");
                    await Task.Delay(100);
                    window = shell.OpenWindows.Single(item => item.GetType().Name == "AssistantWindow");
                    break;
                default: throw new ArgumentException("Unknown resize target " + target);
            }
            window.Activate();
            await Task.Delay(350);
            var surface = Descendants(window.Content).OfType<InkSurface>().FirstOrDefault();
            int inkFrames = 0;
            if (surface is not null) surface.FrameDrawn += () => inkFrames++;
            var sizes = target == "lottery"
                ? new[] { (320, 360), (360, 480), (439, 540), (440, 540), (600, 540), (719, 600), (720, 600), (1100, 800), (1920, 1080), (439, 420), (720, 480), (1100, 720) }
                : new[] { (480, 360), (560, 480), (599, 540), (600, 600), (699, 540), (700, 700), (759, 600), (760, 700), (819, 600), (820, 700), (919, 600), (920, 700), (1039, 600), (1040, 700), (1440, 900), (560, 400), (1120, 820) };
            int cycles = Environment.GetCommandLineArgs().Contains("--resize-quick") ? 1 : 4;
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(window, theme);
                for (int cycle = 0; cycle < cycles; cycle++)
                foreach (var (width, height) in sizes)
                {
                    File.WriteAllText(Path.Combine(directory, "phase.json"), JsonSerializer.Serialize(new { target, theme, cycle, width, height, frames, inkFrames }));
                    int before = frames;
                    double dpi = WindowChrome.GetDpiScale(window);
                    window.AppWindow.ResizeClient(new((int)Math.Round(width * dpi), (int)Math.Round(height * dpi)));
                    await Task.Delay(90);
                    var root = (FrameworkElement)window.Content;
                    Check("resize settles and shared dispatcher renders", frames > before && errors.Count == 0,
                        new { target, theme, cycle, width, height, frames, inkFrames, actualWidth = root.ActualWidth, actualHeight = root.ActualHeight });
                    var outside = Descendants(root).OfType<Button>().Where(button => IsVisibleOutsideScroll(button)
                        && !Fits(button, root)).Select(button => AutomationProperties.GetName(button) is { Length: > 0 } name
                            ? name : button.Content?.ToString()).ToArray();
                    Check("visible window actions fit the resized content", outside.Length == 0, new { target, width, height, outside });
                    if (width is 480 or 600 or 820 or 1440 && cycle == 0)
                        await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"{target}-{theme}-{width}.png"));
                    if (window is LotteryWindow lottery && width >= 320)
                    {
                        var plus = Descendants(root).OfType<TouchIconButton>().Single(button => AutomationProperties.GetName(button) == "增加人数");
                        var minus = Descendants(root).OfType<TouchIconButton>().Single(button => AutomationProperties.GetName(button) == "减少人数");
                        Invoke(plus); Invoke(minus);
                        Check("lottery controls remain reachable", Fits(plus, root) && Fits(minus, root), new { width, height });
                    }
                }
                if (window is LotteryWindow draw)
                {
                    foreach (int count in new[] { 1, 10, 30 })
                    {
                        draw.ChangeCount(count); await draw.RollAsync();
                        Check("lottery still produces results after resize cycles", draw.LastResult.Count == count, count);
                    }
                }
                if (window is IDocumentWorkspace workspace)
                {
                    int previousFrames = inkFrames;
                    var input = new InkCanvas.Platform.Windows.OverlayPointer(28031, 240, 260, .5f, 1, 1, InkCanvas.Platform.Windows.OverlayPointerKind.Mouse, 1000);
                    workspace.Controller.Tool = InkTool.Pen;
                    workspace.Controller.OnPointerDown(input);
                    workspace.Controller.OnPointerMove(input with { X = 340, Y = 320, TimeMs = 1050 });
                    workspace.Controller.OnPointerUp(input with { X = 380, Y = 330, TimeMs = 1080 });
                    await Task.Delay(120);
                    Check("PDF ink renders after resize cycles", inkFrames > previousFrames && workspace.Controller.Document.Count > 0);
                    workspace.Controller.Undo();
                    await workspace.SaveAsync();
                }
                var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
                bar.Bar.Controller.Tool = InkTool.Pen;
                Invoke((Button)bar.Bar.GetToolButton(InkTool.Highlighter));
                await Task.Delay(70);
                Check("main toolbar updates after independent window resizing", bar.Bar.GetToolButton(InkTool.Highlighter) is TouchIconButton { IsSelected: true } && errors.Count == 0);
                await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, target + "-" + theme + ".png"));
            }
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize(); await Task.Delay(100); presenter.Restore();
                await Task.Delay(150);
                presenter.Maximize(); await Task.Delay(100); presenter.Restore();
                await Task.Delay(150);
                Check("minimize maximize and restore preserve rendering", errors.Count == 0 && frames > 0);
            }
        }
        catch (System.Exception error) { Check("resize regression exception", false, error.ToString()); }
        finally
        {
            Application.Current.UnhandledException -= Exception;
            CompositionTarget.Rendering -= Frame;
            if (window is IDocumentWorkspace workspace) await workspace.CloseAsync();
            else window?.Close();
            File.WriteAllText(Path.Combine(directory, "resize.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail = null)
        {
            var result = new { check, passed, detail };
            checks.Add(result);
            File.AppendAllText(Path.Combine(directory, "checks.jsonl"), JsonSerializer.Serialize(result) + Environment.NewLine);
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static bool IsVisibleOutsideScroll(FrameworkElement element)
    {
        for (DependencyObject current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Visibility: Visibility.Collapsed } or ScrollViewer) return false;
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
