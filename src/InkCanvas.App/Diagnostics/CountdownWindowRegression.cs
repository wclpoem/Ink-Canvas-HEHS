using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class CountdownWindowRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var keeper = new Window { Content = new Grid() }; keeper.Activate(); keeper.AppWindow.Hide();
        try { foreach (int theme in new[] { 1, 2 }) await CheckAsync(theme, directory, checks); }
        catch (Exception exception) { checks.Add(new { check = "timer regression exception", passed = false, detail = exception.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "countdown.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            keeper.Close(); Application.Current.Exit();
        }
    }

    internal static async Task CheckAsync(int theme, string directory, List<object> checks)
    {
        using var timer = new CountdownWindow(theme, 3);
        timer.Activate(); await Task.Delay(250);
        var frame = (WindowFrame)timer.Content;
        var ring = Descendants(frame).OfType<CountdownRing>().Single();
        var presets = Descendants(frame).OfType<ToggleButton>().Where(button => AutomationProperties.GetName(button).EndsWith("分钟")
            || AutomationProperties.GetName(button) == "30秒").ToArray();
        try
        {
            Check("native WinUI caption and Mica", timer.ExtendsContentIntoTitleBar && timer.SystemBackdrop is MicaBackdrop
                && frame.Header.RightHeader is StackPanel && ClassroomWindowRegression.CheckNativeFrame(timer, true));
            Check("three-minute preset and ready progress agree", timer.ConfiguredSeconds == 180 && ring.Value == 1
                && presets.Count(button => button.IsChecked == true) == 1
                && presets.Single(button => button.IsChecked == true).Content.ToString() == "3 分钟");
            Check("idle timer does not run a refresh loop", !timer.RefreshTimerRunning);
            await Save("ready");
            foreach (var size in new[] { (480, 600), (640, 600), (1100, 800) })
            {
                double dpi = WindowChrome.GetDpiScale(timer);
                timer.AppWindow.Resize(new((int)(size.Item1 * dpi), (int)(size.Item2 * dpi)));
                await Task.Delay(100);
                Check("resized controls remain visible " + size.Item1, Descendants(frame).OfType<TouchIconButton>().Where(Visible)
                    .All(button => button.ActualWidth >= 40 && button.ActualHeight >= 40 && Fits(button, frame)));
                Check("preset pills retain touch targets " + size.Item1, presets.All(button => button.ActualHeight >= 40));
                await Save("size-" + size.Item1);
            }
            timer.AppWindow.Resize(new(820, 740)); await Task.Delay(80);
            timer.ToggleEdit(); await Task.Delay(80);
            Check("time opens duration flyout", timer.EditorOpen);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot).Single(popup => Descendants(popup.Child).OfType<NumberBox>().Count() == 3);
            var inputs = Descendants(popup.Child).OfType<NumberBox>().ToArray();
            var confirm = Descendants(popup.Child).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "确定");
            foreach (var input in inputs) input.Value = 0;
            Invoke(confirm); await Task.Delay(30);
            Check("zero duration shows validation without altering timer", timer.EditorOpen && timer.ConfiguredSeconds == 180);
            inputs.Single(input => AutomationProperties.GetName(input) == "分钟").Value = 5;
            Invoke(confirm); await Task.Delay(50);
            Check("duration editor applies to timer and preset", !timer.EditorOpen && timer.ConfiguredSeconds == 300
                && presets.Single(button => button.IsChecked == true).Content.ToString() == "5 分钟");
            timer.ToggleEdit(); await Task.Delay(60); await BoardWindowRegression.SaveVisual((FrameworkElement)popup.Child, Path.Combine(directory, $"timer-editor-{theme}.png")); timer.ToggleEdit();
            int starts = 0, pauses = 0, ends = 0;
            timer.TimerStarted += _ => starts++; timer.TimerStopped += _ => pauses++; timer.TimerEnded += () => ends++;
            timer.SetDuration(180); timer.StartPause(); await Task.Delay(150);
            Check("start changes controls and progress", timer.IsRunning && timer.RefreshTimerRunning && ring.Value is > 0 and < 1);
            timer.StartPause(); double paused = ring.Value; await Task.Delay(120);
            Check("pause stops refresh with stable progress", timer.IsPaused && !timer.RefreshTimerRunning && ring.Value == paused);
            int remaining = timer.DisplaySeconds; timer.AddTime(60);
            Check("extend keeps paused session and original reset duration", timer.DisplaySeconds == remaining + 60 && timer.ConfiguredSeconds == 180 && timer.IsPaused);
            timer.StartPause(); await Task.Delay(100); timer.Reset();
            Check("reset restores ready ring and classroom events", timer.DisplaySeconds == 180 && ring.Value == 1 && !timer.IsRunning && starts == 2 && pauses == 2);
            timer.SetDuration(8); timer.StartPause(); await Task.Delay(120);
            Check("last ten seconds use warning progress", ring.IsWarning); await Save("warning"); timer.Reset();

            var origin = timer.AppWindow.Position; var originalSize = timer.AppWindow.Size;
            timer.SetDuration(60); timer.StartPause();
            timer.ToggleCompact(); await Task.Delay(150);
            Check("compact view uses system CompactOverlay and keeps counting", timer.IsCompact && timer.AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay && timer.IsRunning);
            Check("compact controls retain touch targets", Descendants(frame).OfType<TouchIconButton>().Where(Visible).All(button => button.ActualHeight >= 40 && Fits(button, frame)));
            await Save("compact");
            timer.ToggleCompact(); await Task.Delay(150);
            Check("compact returns to original placement and title bar", timer.AppWindow.Position == origin && timer.AppWindow.Size == originalSize && timer.ExtendsContentIntoTitleBar);
            timer.ToggleFullScreen(); await Task.Delay(150);
            Check("presentation uses real fullscreen", timer.IsFullScreen && timer.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen && timer.IsRunning);
            await Save("fullscreen"); timer.ToggleFullScreen(); await Task.Delay(100);
            Check("fullscreen returns without resetting countdown", timer.IsRunning && timer.ConfiguredSeconds == 60 && timer.AppWindow.Size == originalSize);
            timer.Reset();
            var sound = Descendants(frame).OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "结束提示音");
            var repeat = Descendants(frame).OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "循环计时");
            sound.IsChecked = false; timer.SetDuration(1); timer.StartPause(); await Task.Delay(1200);
            Check("finish emits event and stops refresh", !timer.IsRunning && timer.DisplaySeconds == 0 && ends == 1 && !timer.RefreshTimerRunning && !timer.SoundStarted);
            repeat.IsChecked = true; timer.StartPause(); await Task.Delay(1200);
            Check("repeat starts next round", timer.IsRunning && timer.Round >= 2 && ends == 2); timer.Reset(); repeat.IsChecked = false;
            sound.IsChecked = true; timer.StartPause(); await Task.Delay(1200);
            Check("finish uses bundled sound", timer.SoundStarted); timer.Reset();
            sound.IsChecked = false;
            var modeSelector = Descendants(frame).OfType<SegmentedSelector>().Single(selector => AutomationProperties.GetName(selector) == "计时模式");
            modeSelector.SelectedIndex = 1;
            Check("stopwatch starts at zero with refresh stopped", timer.IsStopwatch && timer.StopwatchElapsed == TimeSpan.Zero && !timer.RefreshTimerRunning);
            int countdownStarts = starts;
            timer.StartPause(); await Task.Delay(180); timer.RecordLap(); await Task.Delay(80); timer.RecordLap();
            Check("stopwatch records split and cumulative laps", timer.StopwatchLaps.Count == 2
                && timer.StopwatchLaps[0].Elapsed > TimeSpan.Zero && timer.StopwatchLaps[1].Elapsed > timer.StopwatchLaps[0].Elapsed
                && timer.StopwatchLaps[1].Split > TimeSpan.Zero && starts == countdownStarts);
            timer.StartPause(); var elapsed = timer.StopwatchElapsed; await Task.Delay(100);
            Check("paused stopwatch is stable and stops refresh", timer.StopwatchElapsed == elapsed && !timer.RefreshTimerRunning);
            await Save("stopwatch-laps");
            timer.SwitchTimerMode(false); timer.SetDuration(1); timer.StartPause();
            int previousEnds = ends; timer.SwitchTimerMode(true); await Task.Delay(1200);
            Check("background countdown completes while stopwatch stays paused", timer.IsStopwatch && ends == previousEnds + 1
                && timer.StopwatchElapsed == elapsed && timer.StopwatchLaps.Count == 2);
            timer.StartPause(); await Task.Delay(80); timer.ToggleCompact(); await Task.Delay(100);
            Check("stopwatch continues in compact overlay", timer.IsCompact && timer.IsStopwatch && timer.StopwatchElapsed > elapsed);
            Check("stopwatch compact controls fit", Descendants(frame).OfType<TouchIconButton>().Where(Visible).All(button => Fits(button, frame)));
            timer.RecordLap(); await Save("stopwatch-compact"); timer.ToggleCompact(); await Task.Delay(100);
            timer.ToggleFullScreen(); await Task.Delay(100); await Save("stopwatch-fullscreen"); timer.ToggleFullScreen(); await Task.Delay(100);
            var beforeHidden = timer.StopwatchElapsed; timer.AppWindow.Hide(); await Task.Delay(100);
            Check("hidden stopwatch keeps time without UI refresh", timer.StopwatchElapsed > beforeHidden && !timer.RefreshTimerRunning);
            timer.Activate(); await Task.Delay(80); Check("restored stopwatch resumes visual refresh", timer.RefreshTimerRunning);
            timer.SwitchTimerMode(false); await Task.Delay(60);
            Check("background-only stopwatch does not run UI refresh", !timer.RefreshTimerRunning);
            timer.SwitchTimerMode(true); await Task.Delay(60);
            timer.StartPause(); timer.Reset();
            Check("stopwatch reset clears laps and running refresh", timer.StopwatchElapsed == TimeSpan.Zero && timer.StopwatchLaps.Count == 0 && !timer.RefreshTimerRunning);
            timer.SwitchTimerMode(false);
            Check("returning to countdown retains its last duration", timer.ConfiguredSeconds == 1 && !timer.IsRunning);
        }
        finally { timer.Close(); }

        void Check(string check, bool passed) => checks.Add(new { check, theme, passed });
        async Task<bool> Save(string name)
        {
            // RenderTargetBitmap omits the native backdrop. Supply its theme fallback
            // for screenshots, then restore the real Mica background immediately.
            var background = frame.Background;
            frame.Background = InkCanvas.Controls.Theming.PopupBrushes.WindowSurface(theme);
            try { return await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"timer-{name}-{theme}.png")); }
            finally { frame.Background = background; }
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static bool Visible(FrameworkElement element)
    {
        for (DependencyObject node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item;
    }
}
