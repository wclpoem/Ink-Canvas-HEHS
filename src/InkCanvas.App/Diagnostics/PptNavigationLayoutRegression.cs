using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class PptNavigationLayoutRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        var windows = new List<PptNavigationWindow>();
        string settingsPath = Path.Combine(directory, "settings");
        using var services = new AppServices(settingsPath);
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        FloatingBarWindow bar = null;
        try
        {
            shell.Start(); shell.StartInClickThrough();
            await Task.Delay(300);
            foreach (var notice in shell.OpenWindows.OfType<NoticeWindow>().ToArray()) notice.Close();
            var office = Process.GetProcessesByName("POWERPNT")
                .FirstOrDefault(process => !process.HasExited && process.Responding && process.MainWindowHandle != 0);
            if (office is null) throw new InvalidOperationException("No visible PowerPoint window is available on this computer.");

            bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            int themeBefore = services.Settings.Appearance.Theme;
            double scaleBefore = services.Settings.Appearance.FloatingBarScale;
            bool enabledBottomBefore = services.Settings.PowerPoint.IsShowBottomPPTNavigationPanel;
            bool enabledSideBefore = services.Settings.PowerPoint.IsShowSidePPTNavigationPanel;
            bool counterBottomBefore = services.Settings.PowerPoint.IsShowPPTNavigationBottom;
            bool counterSideBefore = services.Settings.PowerPoint.IsShowPPTNavigationSides;
            services.Settings.PowerPoint.IsShowBottomPPTNavigationPanel = true;
            services.Settings.PowerPoint.IsShowSidePPTNavigationPanel = true;
            services.Settings.PowerPoint.IsShowPPTNavigationBottom = true;
            services.Settings.PowerPoint.IsShowPPTNavigationSides = true;
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                services.ApplySettings();

                foreach (bool readingMode in new[] { true, false })
                {
                    bar.SetPresentationMode(true, office.MainWindowHandle, windowed: readingMode);
                    await Task.Delay(150);
                    var navs = new Dictionary<PptNavigationPlacement, PptNavigationWindow>();
                    foreach (var placement in Enum.GetValues<PptNavigationPlacement>())
                    {
                        var navigation = new PptNavigationWindow(placement, shell, theme, bar);
                        windows.Add(navigation); navs.Add(placement, navigation);
                        navigation.Update(new PptSnapshot(), true, true);
                        Check("visible at " + theme + " " + Mode(readingMode) + " " + placement,
                            navigation.AppWindow.IsVisible && navigation.WindowOpacity == 1);
                    }

                    await Task.Delay(180);
                    var area = bar.GetPlacementArea();
                    var toolbar = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(bar));
                    foreach (var pair in navs)
                    {
                        var bounds = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(pair.Value));
                        bool overlap = bounds.X < toolbar.X + toolbar.Width && bounds.X + bounds.Width > toolbar.X
                            && bounds.Y < toolbar.Y + toolbar.Height && bounds.Y + bounds.Height > toolbar.Y;
                        bool bottom = pair.Key is PptNavigationPlacement.BottomLeft or PptNavigationPlacement.BottomRight;
                        double edgeGap = ScreenService.FromWindow(WindowChrome.GetHwnd(pair.Value)).DpiScale
                            * (pair.Key == PptNavigationPlacement.BottomLeft ? 2
                                : pair.Key is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight ? 6 : 0);
                        Check("in bounds and avoids toolbar " + theme + " " + Mode(readingMode) + " " + pair.Key,
                            !overlap && bounds.X >= area.X && bounds.Y >= area.Y
                            && bounds.X + bounds.Width <= area.X + area.Width
                            && bounds.Y + bounds.Height <= area.Y + area.Height
                            && (!bottom || Math.Abs(bounds.Y + bounds.Height - toolbar.Y - toolbar.Height + edgeGap) <= 3),
                            new { area, toolbar, bounds, overlap, bottom, edgeGap });
                        var card = pair.Value.NavigationCard;
                        var fill = (card.Background as SolidColorBrush)?.Color;
                        var stroke = (card.BorderBrush as SolidColorBrush)?.Color;
                        var expectedFill = PopupBrushes.FloatBarBackground(theme).Color;
                        var expectedStroke = PopupBrushes.FloatBarBorder(theme).Color;
                        Check("matches toolbar theme " + theme + " " + Mode(readingMode) + " " + pair.Key,
                            fill == expectedFill && stroke == expectedStroke,
                            new { fill = fill?.ToString(), expectedFill = expectedFill.ToString(),
                                stroke = stroke?.ToString(), expectedStroke = expectedStroke.ToString(), barTheme = bar.Bar.Theme });
                        Check("matches toolbar height corner radius and border " + pair.Key,
                            Math.Abs((bottom ? card.ActualHeight : card.ActualWidth) - InkCanvas.Controls.Controls.FloatingBar.ChromeHeight) < 1
                            && Math.Abs(card.CornerRadius.TopLeft - InkCanvas.Controls.Controls.FloatingBar.ChromeCornerRadius) < .01
                            && Math.Abs(card.BorderThickness.Left - InkCanvas.Controls.Controls.FloatingBar.ChromeBorderThickness) < .01);
                    }

                    foreach (var navigation in navs.Values) navigation.RecordInteraction();
                    await Task.Delay(TimeSpan.FromSeconds(5.25));
                    Check("75 percent idle window opacity " + theme + " " + Mode(readingMode), navs.Values.All(navigation =>
                        Math.Abs(navigation.TargetOpacity - .75) < .001 && Math.Abs(navigation.WindowOpacity - .75) < .04));
                    foreach (var navigation in navs.Values) navigation.RecordInteraction();
                    await Task.Delay(300);
                    Check("click feedback fades to normal window opacity " + theme + " " + Mode(readingMode), navs.Values.All(navigation =>
                        navigation.TargetOpacity == 1 && Math.Abs(navigation.WindowOpacity - 1) < .02));
                    foreach (var navigation in navs.Values) navigation.Close();
                    windows.RemoveAll(navs.Values.Contains);
                }
            }
            services.Settings.Appearance.Theme = themeBefore;
            services.Settings.Appearance.FloatingBarScale = scaleBefore;
            services.Settings.PowerPoint.IsShowBottomPPTNavigationPanel = enabledBottomBefore;
            services.Settings.PowerPoint.IsShowSidePPTNavigationPanel = enabledSideBefore;
            services.Settings.PowerPoint.IsShowPPTNavigationBottom = counterBottomBefore;
            services.Settings.PowerPoint.IsShowPPTNavigationSides = counterSideBefore;
            services.ApplySettings();
        }
        catch (Exception exception) { results.Add(new { check = "PPT navigation local regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            foreach (var window in windows.ToArray()) window.Close();
            if (bar is not null) bar.SetPresentationMode(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "ppt-navigation.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        void Check(string name, bool passed, object detail = null)
        {
            results.Add(new { check = name, passed, detail });
            File.AppendAllText(Path.Combine(directory, "progress.log"), name + " " + passed + Environment.NewLine);
            File.WriteAllText(Path.Combine(directory, "ppt-navigation.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }

        static string Mode(bool readingMode) => readingMode ? "reading mode" : "full-screen show";
    }
}
