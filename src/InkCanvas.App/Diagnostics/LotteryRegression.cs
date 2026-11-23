using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

namespace InkCanvas.App.Diagnostics;

internal static class LotteryRegression
{
    internal static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services) { RosterDirectory = Path.Combine(directory, "roster") };
        shell.Start(); shell.StartInClickThrough();
        bool cursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        bool systemAnimations = Motion.AnimationsEnabled;
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--lottery-results-only"))
            {
                await SetAnimations(false);
                await CheckResultIsolationAsync(services, shell, checks);
                return;
            }
            shell.OpenLottery();
            await Task.Delay(220);
            var lottery = shell.OpenWindows.OfType<LotteryWindow>().Single();
            for (int attempt = 0; attempt < 100 && Field<TouchIconButton>(lottery, "_optionsButton").XamlRoot is null; attempt++)
                await Task.Delay(20);
            var controller = services.Lottery;
            controller.LoadNames(Enumerable.Range(1, 60).Select(index => $"同学{index}"));
            var grid = Field<ItemsRepeater>(lottery, "_resultView");
            IEnumerable<LotteryWindow.ResultItem> ResultItems() => ((IEnumerable<LotteryWindow.ResultItem>)grid.ItemsSource ?? Array.Empty<LotteryWindow.ResultItem>());
            Check("lottery uses native WinUI title bar and Mica", lottery.Content is WindowFrame && lottery.ExtendsContentIntoTitleBar && lottery.SystemBackdrop is MicaBackdrop);
            Check("copy belongs to the result stage", IsDescendant(Field<TouchIconButton>(lottery, "_copy"), Field<Border>(lottery, "_results")));
            Check("roster and options share the top toolbar", IsDescendant(Field<TouchIconButton>(lottery, "_importButton"), Field<Grid>(lottery, "_topBar"))
                && IsDescendant(Field<TouchIconButton>(lottery, "_optionsButton"), Field<Grid>(lottery, "_topBar")));
            Check("soft cooldown is the default with a ten-day half-life", controller.SamplingMode == LotterySamplingMode.SoftCooldown
                && controller.CooldownHalfLifeDays == 10 && controller.CooldownPenaltyStrength == 1);
            var optionsButton = Field<TouchIconButton>(lottery, "_optionsButton");
            Invoke(optionsButton); await Task.Delay(100);
            var options = (StackPanel)((ScrollViewer)((Flyout)optionsButton.Flyout).Content).Content;
            Check("options open downward from the management toolbar", ((Flyout)optionsButton.Flyout).Placement == FlyoutPlacementMode.BottomEdgeAlignedRight);
            var modeBox = Descendants(options).OfType<ComboBox>().Single();
            var daysBox = Descendants(options).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "冷却半衰期（天）");
            daysBox.Value = 14;
            Check("popup changes and persists the cooldown period", controller.CooldownHalfLifeDays == 14 && services.Settings.Rand.CooldownHalfLifeDays == 14);
            modeBox.SelectedIndex = (int)LotterySamplingMode.Uniform;
            Check("uniform mode keeps period setting visible with an explanation", !daysBox.IsEnabled && ToolTipService.GetToolTip(daysBox) is string);
            modeBox.SelectedIndex = (int)LotterySamplingMode.SoftCooldown;
            daysBox.Value = 10;
            await BoardWindowRegression.SaveVisual(options, Path.Combine(directory, "cooldown-options.png"));
            optionsButton.Flyout.Hide();
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(lottery, theme);
                foreach (int count in new[] { 1, 2, 5, 10, 30 })
                {
                    await SetAnimations(true);
                    lottery.ChangeCount(count);
                    Task roll = lottery.RollAsync();
                    await Task.Delay(100);
                    Check($"all preview cards roll together {theme} {count}", (grid.ItemsSourceView?.Count ?? 0) == Math.Min(count, 24)
                        && !Field<TouchIconButton>(lottery, "_plusButton").IsEnabled && controller.History.Count == (theme - 1) * 5 + Array.IndexOf(new[] { 1, 2, 5, 10, 30 }, count));
                    var before = ResultItems().Select(item => item.Name).ToArray();
                    bool changed = false;
                    for (int sample = 0; sample < 4 && !changed; sample++)
                    {
                        await Task.Delay(100);
                        changed = !ResultItems().Select(item => item.Name).SequenceEqual(before);
                    }
                    Check($"preview updates without consuming draw history {theme} {count}", changed);
                    await roll;
                    Check($"final cards retain every selected name {theme} {count}", ResultItems().Select(item => item.Name).SequenceEqual(lottery.LastResult)
                        && lottery.LastResult.Count == count && lottery.LastResult.Distinct().Count() == count);
                    foreach (int width in count is 1 or 10 ? new[] { 320, 360, 439, 440, 719, 720, 1100, 1920 } : count == 30 ? new[] { 600, 1100 } : new[] { 1100 })
                    {
                        Resize(lottery, width, width < 440 ? 480 : width < 720 ? 600 : 800);
                        await Task.Delay(100);
                        Check($"result cards fit viewport {theme} {count} {width}", count == 1
                            ? Field<Viewbox>(lottery, "_singleHost").Visibility == Visibility.Visible && Fits(Field<Viewbox>(lottery, "_singleHost"), (FrameworkElement)lottery.Content)
                            : ResultCards(grid).All(item => FitsHorizontally(item, grid)));
                        Check($"touch controls fit client {theme} {count} {width}", Descendants(lottery.Content).OfType<TouchIconButton>().Where(Visible)
                            .All(button => Fits(button, (FrameworkElement)lottery.Content) && button.ActualWidth >= 40 && button.ActualHeight >= 44));
                        var controls = Field<Border>(lottery, "_controlCard");
                        Check($"three responsive layouts follow window width {theme} {count} {width}", Grid.GetRow(controls) == (width >= 720 ? 0 : 1)
                            && Field<Grid>(lottery, "_quickRow").Visibility == (width < 440 ? Visibility.Collapsed : Visibility.Visible)
                            && Field<TouchIconButton>(lottery, "_strategyChip").Visibility == (width < 440 ? Visibility.Collapsed : Visibility.Visible));
                        if (count == 10 && width is 320 or 440 or 1100)
                        {
                            Invoke(optionsButton); await Task.Delay(80);
                            var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)lottery.Content).XamlRoot)
                                .SelectMany(popup => Descendants(popup.Child)).OfType<FlyoutPresenter>().Single();
                            var position = presenter.TransformToVisual(lottery.Content as UIElement).TransformBounds(new(0, 0, presenter.ActualWidth, presenter.ActualHeight));
                            var root = (FrameworkElement)lottery.Content;
                            var rollBounds = Field<TouchIconButton>(lottery, "_rollButton").TransformToVisual(root).TransformBounds(new(0, 0,
                                Field<TouchIconButton>(lottery, "_rollButton").ActualWidth, Field<TouchIconButton>(lottery, "_rollButton").ActualHeight));
                            Check($"options fit the window below the top toolbar {theme} {width}", position.Left >= -1 && position.Right <= root.ActualWidth + 1
                                && position.Top >= 40 && position.Bottom <= root.ActualHeight + 1);
                            if (width < 720) Check($"options leave the start action visible {theme} {width}", position.Bottom <= rollBounds.Top + 1);
                            await BoardWindowRegression.SaveVisual(presenter, Path.Combine(directory, $"lottery-options-{theme}-{width}.png"));
                            optionsButton.Flyout.Hide();
                        }
                        if (count is 1 or 10 or 30) await Save(lottery,
                            Path.Combine(directory, $"lottery-{theme}-{count}-{width}.png"));
                    }
                }
            }
            controller.LoadNames(Enumerable.Range(1, 5000).Select(index => $"同学{index}"));
            lottery.ChangeCount(5000); await lottery.RollAsync();
            Check("large batches virtualize result cards", lottery.LastResult.Count == 5000 && (grid.ItemsSourceView?.Count ?? 0) == 5000
                && ResultCards(grid).Count() < 100);
            var resultScroll = Field<ScrollViewer>(lottery, "_resultScroll");
            resultScroll.ChangeView(null, resultScroll.ScrollableHeight, null, true); await Task.Delay(180);
            Check("large batch remains touch scrollable to the last name", grid.TryGetElement(4999) is FrameworkElement);
            controller.LoadNames(Enumerable.Range(1, 60).Select(index => $"同学{index}"));
            controller.NoRepeat = true; controller.Reset();
            lottery.ChangeCount(59); await lottery.RollAsync();
            lottery.ChangeCount(10);
            Check("last batch shows the actual remaining draw count", Field<TextBlock>(lottery, "_countLabel").Text == "1");
            await lottery.RollAsync();
            Check("last batch contains only remaining names", lottery.LastResult.Count == 1 && controller.RemainingCount == 0
                && !Field<TouchIconButton>(lottery, "_rollButton").IsEnabled);
            Invoke(Field<TouchIconButton>(lottery, "_resetRound"));
            Check("reset clears stale results and restores the round", lottery.LastResult.Count == 0 && controller.RemainingCount == 60 && (grid.ItemsSourceView?.Count ?? 0) == 0);
            controller.NoRepeat = false;
            controller.LoadNames(new[] { "欧阳同学（高一年级一班）", "Smith, John", "同名", "同名", "李四", "王五" });
            lottery.ChangeCount(6); await lottery.RollAsync();
            await Task.Delay(180);
            Check("long and repeated names retain separate result cards", lottery.LastResult.Count == 6
                && ResultItems().Count(item => item.Name == "同名") == 2);
            Check("long names fit their cards", Descendants(grid).OfType<TextBlock>().All(text => !text.IsTextTrimmed));
            await BoardWindowRegression.SaveVisual((FrameworkElement)lottery.Content, Path.Combine(directory, "lottery-long-names.png"));
            Check("secondary actions combine icons and readable text", new[] { "_resetRound", "_copy", "_importButton", "_optionsButton", "_rollButton" }
                .Select(name => Field<TouchIconButton>(lottery, name)).All(button => button.Content is StackPanel content
                    && content.Children.OfType<FontIcon>().Any() && content.Children.OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text))));
            var rosterButton = Field<TouchIconButton>(lottery, "_importButton");
            var rosterBounds = WindowChrome.GetScreenBounds(lottery, rosterButton);
            SetCursorPos(rosterBounds.X + rosterBounds.Width / 2, rosterBounds.Y + rosterBounds.Height / 2);
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(250);
            if (!shell.OpenWindows.OfType<SettingsWindow>().Any()) Invoke(rosterButton);
            for (int attempt = 0; attempt < 100 && !shell.OpenWindows.OfType<SettingsWindow>()
                .Any(window => Descendants(window.Content).OfType<ClassroomRosterEditor>().Any()); attempt++) await Task.Delay(20);
            var settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            Check("roster action navigates to settings", Descendants(settings.Content).OfType<WindowWorkspace>().Single().SelectedPage == "抽奖与点名"
                && Descendants(settings.Content).OfType<ClassroomRosterEditor>().Count() == 1);
            var rosterBox = Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single().Children.OfType<TextBox>().Single();
            var rosterRect = rosterBox.TransformToVisual((FrameworkElement)settings.Content).TransformBounds(
                new Windows.Foundation.Rect(0, 0, rosterBox.ActualWidth, rosterBox.ActualHeight));
            var settingsWorkspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            var settingsScroll = Field<ScrollViewer>(settingsWorkspace, "_scroll");
            await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "roster-jump.png"));
            checks.Add(new { check = "roster jump scrolls the editor into view", passed = InScrollViewport(rosterBox, settingsScroll),
                rosterRect.Top, rosterRect.Bottom, rosterRect.Height, rootHeight = ((FrameworkElement)settings.Content).ActualHeight,
                settingsScroll.VerticalOffset, settingsScroll.ScrollableHeight,
                pending = Field<bool>(settings, "_revealRoster") });
            var caption = WindowChrome.GetScreenBounds(settings, ((WindowFrame)settings.Content).Header);
            // Grant this diagnostic foreground input in the same way as a user click.
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(settings));
            SetCursorPos(caption.X + 180, caption.Y + 24); MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30); MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(1050);
            var settingsHit = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = caption.X + 30, Y = caption.Y + 24 }), 2);
            Check("roster settings remains non-topmost", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
            bool foregroundAvailable = NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(settings);
            checks.Add(new { check = "foreground roster settings receives native input", available = foregroundAvailable,
                passed = foregroundAvailable ? (bool?)(settingsHit == WindowChrome.GetHwnd(settings)) : null,
                hit = settingsHit.ToInt64(), expected = WindowChrome.GetHwnd(settings).ToInt64(),
                foreground = NativeMethods.GetForegroundWindow().ToInt64(), sourceVisible = lottery.AppWindow.IsVisible,
                caption = new { caption.X, caption.Y, caption.Width, caption.Height }, overlay = shell.Overlay.Window.Handle.ToInt64() });
            var editor = Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single();
            var settingDays = Descendants(settings.Content).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "冷却半衰期（天）");
            var settingStrength = Descendants(settings.Content).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "软冷却惩罚强度");
            settingDays.Value = 12; settingStrength.Value = 2;
            Check("settings updates live cooldown parameters", controller.CooldownHalfLifeDays == 12 && controller.CooldownPenaltyStrength == 2);
            Check("settings search includes the half-life", settings.SearchSettings("半衰期").Any(entry => entry.Title == "冷却半衰期（天）"));
            settingDays.Value = 10; settingStrength.Value = 1;
            editor.RosterText = "学号,姓名\n1,张三\n2,李四\n3,王五";
            settings.SelectCategory("外观选项"); settings.SelectCategory("抽奖与点名");
            ((FrameworkElement)settings.Content).UpdateLayout();
            await Task.Delay(100);
            Check("roster draft survives category navigation", ReferenceEquals(editor, Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single())
                && editor.RosterText.Contains("张三"));
            Check("settings search locates roster editor", settings.SearchSettings("名单").Any(entry => entry.Title == "名单管理"));
            Check("settings roster saves and updates active lottery", await editor.SaveAsync() && controller.Names.SequenceEqual(new[] { "张三", "李四", "王五" })
                && lottery.LastResult.Count == 0 && File.ReadAllLines(Path.Combine(shell.RosterDirectory, "Names.txt")).Length == 3);
            await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "settings-roster.png"));
            settings.Close();
            Check("closing roster settings returns to lottery", lottery.AppWindow.IsVisible);
            Check("returning from settings preserves icon button touch targets", rosterButton.IsEnabled);
            lottery.ChangeCount(3); await lottery.RollAsync();
            Check("saved roster is used by the next draw", lottery.LastResult.Order().SequenceEqual(new[] { "张三", "李四", "王五" }.Order()));
            lottery.ChangeCount(2);
            Task canceled = lottery.RollAsync();
            controller.LoadNames(new[] { "新名单甲", "新名单乙" });
            await canceled;
            Check("changing roster cancels old preview and history", lottery.LastResult.Count == 0 && controller.History.Count == 0 && (grid.ItemsSourceView?.Count ?? 0) == 0);
            await SetAnimations(false);
            Task reducedMotion = lottery.RollAsync();
            Check("reduced motion presents final results without flashing previews", reducedMotion.IsCompletedSuccessfully && lottery.LastResult.Count == 2);
            await SetAnimations(true);
            canceled = lottery.RollAsync(); lottery.Close(); await canceled;
            Check("closing lottery cancels preview safely", canceled.IsCompletedSuccessfully);
        }
        catch (Exception exception) { checks.Add(new { check = "lottery regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            await SetAnimations(systemAnimations);
            if (cursorAvailable) SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var window in shell.OpenWindows.Reverse().ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "lottery.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static async Task CheckResultIsolationAsync(AppServices services, ShellController shell, List<object> checks)
    {
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        shell.OpenLottery(); await Task.Delay(150);
        var lottery = shell.OpenWindows.OfType<LotteryWindow>().Single();
        var controller = services.Lottery;
        controller.LoadNames(new[] { "同学甲", "同学乙", "同学丙" });
        foreach (int count in new[] { 1, 3 })
        {
            lottery.ChangeCount(count); await lottery.RollAsync(); await Task.Delay(100);
            Check("normal draw displays results " + count, lottery.LastResult.Count == count
                && controller.History.Last().Names.SequenceEqual(lottery.LastResult));
            Check("normal draw leaves both canvases empty " + count, !shell.OpenWindows.OfType<BoardWindow>().Any()
                && services.Board.CurrentImages.Count == 0 && services.Board.Current.Document.Count == 0
                && services.ScreenImages.CurrentImages.Count == 0 && services.OverlayInk.Document.Count == 0
                && !services.Board.Current.History.CanUndo && !services.OverlayInk.History.CanUndo);
        }
        shell.ToggleBoard();
        var board = shell.OpenWindows.OfType<BoardWindow>().Single();
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(40, 40); stroke.AddPoint(120, 100); stroke.Complete();
        services.Board.Current.Document.Add(stroke);
        lottery.ChangeCount(2); await lottery.RollAsync(); await Task.Delay(100);
        Check("normal draw displays multiple names with whiteboard open", lottery.LastResult.Count == 2);
        Check("normal draw preserves existing whiteboard and undo", Unchanged());
        Check("results keep the copy action available", Field<TouchIconButton>(lottery, "_copy").IsEnabled);
        lottery.Close(); await Task.Delay(80);
        int history = controller.History.Count;
        shell.OpenLotteryQuick();
        for (int attempt = 0; attempt < 200 && controller.History.Count == history; attempt++) await Task.Delay(20);
        await Task.Delay(2200);
        Check("quick draw records its result", controller.History.Count == history + 1 && controller.History.Last().Names.Count == 1);
        Check("quick draw preserves existing whiteboard and undo", Unchanged());
        board.Close(); await Task.Delay(80);
        history = controller.History.Count;
        shell.OpenLotteryQuick();
        for (int attempt = 0; attempt < 200 && controller.History.Count == history; attempt++) await Task.Delay(20);
        await Task.Delay(2200);
        Check("quick draw completes while whiteboard is closed", controller.History.Count == history + 1);
        Check("quick draw keeps whiteboard closed and annotation empty", !shell.OpenWindows.OfType<BoardWindow>().Any()
            && services.ScreenImages.CurrentImages.Count == 0 && services.OverlayInk.Document.Count == 0);

        bool Unchanged() => services.Board.Current.Document.Count == 1
            && ReferenceEquals(services.Board.Current.Document.Strokes.Single(), stroke) && services.Board.CurrentImages.Count == 0
            && services.ScreenImages.CurrentImages.Count == 0 && !services.Board.Current.History.CanUndo;
    }

    private static IEnumerable<FrameworkElement> ResultCards(ItemsRepeater repeater)
        => Descendants(repeater).OfType<FrameworkElement>().Where(element => ReferenceEquals(VisualTreeHelper.GetParent(element), repeater)
            && repeater.GetElementIndex(element) >= 0);

    private static void Resize(Window window, int width, int height)
    {
        var work = WindowChrome.GetWorkArea(window); double dpi = WindowChrome.GetDpiScale(window);
        window.AppWindow.Move(new(work.X + 24, work.Y + 24));
        window.AppWindow.ResizeClient(new((int)(width * dpi), (int)(height * dpi)));
    }
    private static bool Visible(FrameworkElement element)
    {
        for (DependencyObject current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }
    private static bool IsDescendant(DependencyObject element, DependencyObject parent)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private static async Task Save(Window window, string path)
    {
        var frame = (WindowFrame)window.Content;
        var material = frame.Background;
        frame.Background = new SolidColorBrush(frame.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        try { await BoardWindowRegression.SaveVisual(frame, path); }
        finally { frame.Background = material; }
    }
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static bool FitsHorizontally(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1;
    }
    private static bool InScrollViewport(FrameworkElement element, ScrollViewer scroll)
    {
        // ScrollViewer applies its live offset in Composition. Compare content coordinates to
        // the logical viewport instead of using the outer XAML transform as a screen position.
        var bounds = element.TransformToVisual((UIElement)scroll.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Top >= scroll.VerticalOffset - 1 && bounds.Bottom <= scroll.VerticalOffset + scroll.ViewportHeight + 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static async Task SetAnimations(bool enabled)
    {
        SetSystemParameter(0x1043 /* SPI_SETCLIENTAREAANIMATION */, 0, new(enabled ? 1 : 0), 2);
        for (int attempt = 0; attempt < 15 && Motion.AnimationsEnabled != enabled; attempt++) await Task.Delay(20);
    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemParameter(uint action, uint parameter, IntPtr value, uint flags);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
