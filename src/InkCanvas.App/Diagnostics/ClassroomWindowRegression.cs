using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;

namespace InkCanvas.App.Diagnostics;

internal static class ClassroomWindowRegression
{
    public static async Task Run(string directory, bool exitApplication = false)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var keepAlive = new Window { Content = new Grid() };
        keepAlive.Activate();
        keepAlive.AppWindow.Hide();
        try
        {
            foreach (int theme in new[] { 1, 2 })
            {
                using (var services = new AppServices(Path.Combine(directory, "settings-" + theme)))
                {
                    services.Settings.Startup.IsAutoUpdate = false;
                    services.Settings.Appearance.Theme = theme;
                    var settings = new SettingsWindow(services);
                    settings.Activate(); await Task.Delay(250);
                    try
                    {
                        var root = (Grid)settings.Content;
                        var caption = ((WindowFrame)root).Header;
                        bool captionFits = caption.ActualHeight >= 48 && Fits(caption, root);
                        bool native = CheckNativeFrame(settings, extended: true);
                        settings.SelectCategory("画板选项");
                        root.UpdateLayout();
                        bool navigation = Descendants(root).OfType<NavigationView>().Count() == 1;
                        checks.Add(new { check = "settings uses extended Windows caption and retains navigation", theme,
                            passed = native && captionFits && navigation, native, captionFits, navigation });
                        await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"settings-caption-{theme}.png"));
                        Resize(settings, 640, 480); await Task.Delay(80);
                        checks.Add(new { check = "settings caption fits narrow window", theme, passed = Fits(caption, root) });
                        await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"settings-narrow-{theme}.png"));
                    }
                    finally { settings.Close(); }
                }
                await CountdownWindowRegression.CheckAsync(theme, directory, checks);

                var lotteryService = new LotteryService { PeopleCount = 20 };
                var lottery = new LotteryWindow(lotteryService, theme: theme,
                    dataDirectory: Path.Combine(directory, "roster-" + theme));
                lottery.Activate(); await Task.Delay(350);
                bool lotteryClosed = false;
                try
                {
                    await CheckSurface(lottery, theme, "lottery", directory, checks);
                    foreach (var size in new[] { (600, 540), (760, 600), (1100, 700) })
                    {
                        Resize(lottery, size.Item1, size.Item2); await Task.Delay(80);
                        await CheckSurface(lottery, theme, $"lottery-{size.Item1}", directory, checks);
                        bool fits = Descendants(lottery.Content).OfType<TouchIconButton>().All(button => Fits(button, (FrameworkElement)lottery.Content));
                        checks.Add(new { check = "lottery controls fit resized client", theme, size = size.Item1, passed = fits });
                        checks.Add(new { check = "lottery controls preserve large touch targets", theme, size = size.Item1,
                            passed = Descendants(lottery.Content).OfType<TouchIconButton>().Where(IsVisible)
                                .All(button => button.ActualWidth >= 40 && button.ActualHeight >= 44) });
                    }
                    var plus = Descendants(lottery.Content).OfType<TouchIconButton>().Single(button => AutomationProperties.GetName(button) == "增加人数");
                    lottery.ChangeCount(1);
                    ((IInvokeProvider)new ButtonAutomationPeer(plus).GetPattern(PatternInterface.Invoke)).Invoke();
                    Task roll = lottery.RollAsync();
                    bool locked = !plus.IsEnabled || !InkCanvas.Controls.Animation.Motion.AnimationsEnabled && roll.IsCompletedSuccessfully;
                    await roll;
                    bool result = lottery.LastResult.Count == 2 && Field<ItemsRepeater>(lottery, "_resultView").ItemsSourceView.Count == 2;
                    bool unlocked = plus.IsEnabled;
                    checks.Add(new { check = "lottery locks controls while rolling and restores them after results", theme, locked, result, unlocked, passed = locked && result && unlocked });
                    await CheckSurface(lottery, theme, "lottery-results", directory, checks);
                    lotteryService.NoRepeat = true; lottery.ChangeCount(20); await lottery.RollAsync();
                    bool exhausted = lotteryService.RemainingCount == 0 && lottery.LastResult.Count == 20
                        && !Field<TouchIconButton>(lottery, "_rollButton").IsEnabled;
                    var resetRound = Field<TouchIconButton>(lottery, "_resetRound");
                    ((IInvokeProvider)new ButtonAutomationPeer(resetRound).GetPattern(PatternInterface.Invoke)).Invoke();
                    checks.Add(new { check = "lottery reports exhausted round and resets it explicitly", theme,
                        passed = exhausted && lotteryService.RemainingCount == 20 && Field<TouchIconButton>(lottery, "_rollButton").IsEnabled });
                    var options = Field<TouchIconButton>(lottery, "_optionsButton");
                    ((IInvokeProvider)new ButtonAutomationPeer(options).GetPattern(PatternInterface.Invoke)).Invoke(); await Task.Delay(60);
                    checks.Add(new { check = "lottery secondary options remain reachable", theme, passed = options.Flyout is Flyout { IsOpen: true } });
                    options.Flyout.Hide();
                    Task canceledRoll = lottery.RollAsync();
                    lottery.Close(); lotteryClosed = true; await canceledRoll;
                    checks.Add(new { check = "closing lottery cancels pending roll", theme, passed = canceledRoll.IsCompletedSuccessfully });
                }
                finally { if (!lotteryClosed) lottery.Close(); }

                string rosterDirectory = Path.Combine(directory, "imported-roster-" + theme);
                var namesWindow = new NamesInputWindow(theme, rosterDirectory);
                namesWindow.Activate(); await Task.Delay(120);
                try
                {
                    namesWindow.RosterText = "学号,姓名,班级\n1,张三,一班\n2,李四,一班";
                    bool saved = await namesWindow.SaveAsync();
                    checks.Add(new { check = "roster editor saves parsed classroom table", theme, passed = saved
                        && File.ReadAllLines(Path.Combine(rosterDirectory, "Names.txt")).SequenceEqual(new[] { "张三", "李四" }) });
                    await BoardWindowRegression.SaveVisual((FrameworkElement)namesWindow.Content, Path.Combine(directory, $"roster-editor-{theme}.png"));
                }
                finally { namesWindow.CloseWithoutSaving(); }
            }
        }
        catch (Exception exception)
        {
            checks.Add(new { check = "classroom diagnostics exception", passed = false, error = exception.ToString() });
        }
        File.WriteAllText(Path.Combine(directory, "classroom.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        keepAlive.Close();
        if (exitApplication) Application.Current.Exit();
    }

    private static async Task CheckSurface(Window window, int theme, string name, string directory, List<object> checks)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        Color expected = PopupBrushes.WindowSurface(theme).Color;
        bool foundation = window is LotteryWindow ? window.SystemBackdrop is MicaBackdrop
            : root is Grid grid && ((SolidColorBrush)grid.Background).Color == expected;
        bool native = CheckNativeFrame(window, extended: window is CountdownWindow or LotteryWindow or NamesInputWindow);
        bool fills = Math.Abs(root.ActualWidth * root.XamlRoot.RasterizationScale - window.AppWindow.ClientSize.Width) < 2
            && Math.Abs(root.ActualHeight * root.XamlRoot.RasterizationScale - window.AppWindow.ClientSize.Height) < 2;
        checks.Add(new { check = "system frame and full client background", window = name, theme,
            native, foundation, fills, passed = native && foundation && fills });
        await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"{name}-{theme}.png"));
    }

    internal static bool CheckNativeFrame(Window window, bool extended = false)
    {
        var hwnd = WindowChrome.GetHwnd(window);
        bool caption = (NativeMethods.GetWindowLongW(hwnd, -16) & 0x00C00000) == 0x00C00000;
        IntPtr region = CreateRectRgn(0, 0, 1, 1);
        int kind = GetWindowRgn(hwnd, region);
        DeleteObject(region);
        int result = DwmGetWindowAttribute(hwnd, 33, out int corner, sizeof(int));
        return caption && kind == 0 && (result < 0 || corner == 0) && window.ExtendsContentIntoTitleBar == extended;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private static void Resize(Window window, int widthDip, int heightDip)
    {
        double dpi = WindowChrome.GetDpiScale(window); var work = WindowChrome.GetWorkArea(window);
        WindowChrome.Place(window, work.X + 30, work.Y + 30,
            Math.Min((int)Math.Ceiling(widthDip * dpi), work.Width - 60), Math.Min((int)Math.Ceiling(heightDip * dpi), work.Height - 60));
    }
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        if (!IsVisible(element)) return true;
        var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static bool IsVisible(FrameworkElement element)
    {
        for (DependencyObject current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
