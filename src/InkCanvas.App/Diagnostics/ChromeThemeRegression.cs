using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ChromeThemeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var windows = new List<Window>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Appearance.Theme = 1;
        try
        {
            var keeper = new Window { Title = "窗口主题回归检查", Content = new Grid { Background = PopupBrushes.WindowSurface(1) } };
            windows.Add(keeper);
            WindowChrome.MakeStandard(keeper, 400, 300);
            WindowChrome.ShowWithoutActivation(keeper);
            keeper.AppWindow.Hide();
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                var bar = new FloatingBarWindow();
                windows.Add(bar);
                bar.Bar.Settings = services.Settings;
                bar.Bar.Controller = services.OverlayInk;
                bar.Bar.Build();
                WindowChrome.ApplyTheme(bar, theme);
                WindowChrome.ShowWithoutActivation(bar);
                await Task.Delay(120);
                var barBackdrop = (ChromeBackdrop)bar.SystemBackdrop;
                Check("main toolbar material follows its selected theme", Field<SystemBackdropConfiguration>(barBackdrop, "_configuration")?.Theme
                    == (theme == 2 ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light));
                var material = Field<DesktopAcrylicController>(barBackdrop, "_controller");
                Check("active material has a theme foundation instead of clear black", material is null
                    || material.TintColor.A == 255 && material.TintOpacity >= .5f && material.LuminosityOpacity >= .9f);
                Check("floating toolbar keeps its XAML swap chain unlayered", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(bar), NativeMethods.WS_EX_LAYERED));
                Check("toolbar fallback paints its authored theme directly", barBackdrop.State == "Active"
                    || bar.Bar.ChromeSurfaces.All(surface => surface.Background is SolidColorBrush brush && brush.Color.A == 255
                        && brush.Color.R == (theme == 1 ? 255 : 0)));
                await BoardWindowRegression.SaveVisual((FrameworkElement)bar.Content, Path.Combine(directory, $"toolbar-{theme}.png"));
                var more = new BarPanelWindow(bar, bar.Bar, BarPanelKind.More);
                windows.Add(more);
                WindowChrome.ShowWithoutActivation(more);
                await Task.Delay(120);
                Check("toolbar panel material follows its selected theme", Field<SystemBackdropConfiguration>((ChromeBackdrop)more.SystemBackdrop, "_configuration")?.Theme
                    == (theme == 2 ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light));
                more.Close(); windows.Remove(more);
                var shapes = new ShapePickerWindow(services.OverlayInk, () => { }, theme);
                windows.Add(shapes);
                WindowChrome.ShowWithoutActivation(shapes);
                await Task.Delay(120);
                Check("shape panel material follows its selected theme", Field<SystemBackdropConfiguration>((ChromeBackdrop)shapes.SystemBackdrop, "_configuration")?.Theme
                    == (theme == 2 ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light));
                shapes.Close(); windows.Remove(shapes);
                foreach (bool highlighter in new[] { false, true })
                {
                    var palette = new PenPaletteWindow(services.OverlayInk, highlighter, theme);
                    windows.Add(palette);
                    WindowChrome.ShowWithoutActivation(palette);
                    await Task.Delay(160);
                    var backdrop = (ChromeBackdrop)palette.SystemBackdrop;
                    backdrop.SetTheme(theme == 1 ? 2 : 1);
                    WindowChrome.ApplyTheme(palette, theme);
                    await Task.Delay(80);
                    var configuration = Field<SystemBackdropConfiguration>(backdrop, "_configuration");
                    var controller = Field<DesktopAcrylicController>(backdrop, "_controller");
                    var expected = theme == 2 ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
                    Check("popup content and material use the same theme", configuration?.Theme == expected,
                        new { palette.Title, theme, actualTheme = configuration?.Theme.ToString(), backdrop.State,
                            requestedTheme = typeof(ChromeBackdrop).GetField("_theme", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(backdrop),
                            defaults = Field<SystemBackdropConfiguration>(backdrop, "_defaults")?.Theme.ToString() });
                    if (controller is not null)
                    {
                        var tint = controller.TintColor;
                        var fallback = controller.FallbackColor;
                        Check("acrylic uses an opaque theme color", tint.A == 255 && (theme == 1 ? tint.R >= 240 : tint.R <= 40),
                            new { theme, tint.A, tint.R, tint.G, tint.B });
                        Check("acrylic fallback uses the window theme", fallback.A == 255
                            && (theme == 1 ? fallback.R >= 240 : fallback.R <= 40), new { theme, fallback.A, fallback.R });
                    }
                    await BoardWindowRegression.SaveVisual((FrameworkElement)palette.Content,
                        Path.Combine(directory, $"{(highlighter ? "highlighter" : "pen")}-{theme}.png"));
                    palette.Close(); windows.Remove(palette);
                }
                bar.Close(); windows.Remove(bar);
            }
            foreach (int theme in new[] { 1, 2 })
            {
                var surface = new Border { Background = PopupBrushes.FloatBarBackground(theme), Width = 200, Height = 100 };
                var window = new Window { Title = "不支持亚克力材质的备用背景", Content = surface };
                windows.Add(window);
                WindowChrome.MakeBorderless(window, noActivate: true);
                var backdrop = new ChromeBackdrop(enableAcrylic: false);
                backdrop.SetTheme(theme);
                window.SystemBackdrop = backdrop;
                WindowChrome.ClipToSurfaces(window, () => new[] { surface });
                WindowChrome.ApplyTheme(window, theme);
                WindowChrome.PlaceDip(window, 100, 100, 200, 100);
                WindowChrome.ShowWithoutActivation(window);
                await Task.Delay(120);
                var fallback = Field<Windows.UI.Composition.CompositionColorBrush>(backdrop, "_fallback");
                Check("unsupported acrylic paints an opaque theme foundation", fallback is not null && fallback.Color.A == 255
                    && (theme == 1 ? fallback.Color.R == 255 : fallback.Color.R == 31),
                    new { theme, backdrop.State, color = fallback?.Color.ToString() });
                Check("unsupported acrylic paints its XAML card without a black underlay", surface.Background is SolidColorBrush card
                    && card.Color.A == 255 && card.Color.R == (theme == 1 ? 255 : 0)
                    && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_LAYERED));
                await BoardWindowRegression.SaveVisual(surface, Path.Combine(directory, $"fallback-{theme}.png"));
                backdrop.SetTheme(theme == 1 ? 2 : 1);
                WindowChrome.ApplyTheme(window, theme);
                Check("fallback follows explicit window theme changes", fallback?.Color.R == (theme == 1 ? 255 : 31));
                var configuration = Field<SystemBackdropConfiguration>(backdrop, "_configuration");
                configuration.IsHighContrast = true;
                configuration.HighContrastBackgroundColor = Windows.UI.Color.FromArgb(255, 12, 34, 56);
                typeof(ChromeBackdrop).GetMethod("ApplyColors", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(backdrop, null);
                backdrop.UpdateSurfaceFallback();
                Check("high contrast keeps its exact background and full opacity", fallback?.Color == configuration.HighContrastBackgroundColor
                    && surface.Background is SolidColorBrush contrast && contrast.Color == configuration.HighContrastBackgroundColor
                    && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_LAYERED));
                backdrop.SetTheme(theme);
                window.AppWindow.Hide();
                window.SystemBackdrop = null;
                Check("releasing hidden material detaches its brush and alpha", Field<Windows.UI.Composition.CompositionColorBrush>(backdrop, "_fallback") is null
                    && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_LAYERED));
                Check("releasing fallback restores the authored card brush", surface.Background is SolidColorBrush authored
                    && authored.Color.A == (theme == 1 ? 191 : 153));
                FloatingBarWindow.EnableTransparency(window);
                WindowChrome.ShowWithoutActivation(window);
                await Task.Delay(120);
                var restored = (ChromeBackdrop)window.SystemBackdrop;
                Check("restoring a hidden window restores its theme", Field<SystemBackdropConfiguration>(restored, "_configuration")?.Theme
                    == (theme == 2 ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light));
                window.Close(); windows.Remove(window);
            }
            services.Settings.Appearance.Theme = 1;
            foreach (var window in new Window[] { new SettingsWindow(services), new CountdownWindow(1),
                new LotteryWindow(services.Lottery, theme: 1), new InfoWindow("浅色窗口检查", "# Ink Canvas\n正文内容", 1) })
            {
                windows.Add(window);
                WindowChrome.ShowWithoutActivation(window);
                await Task.Delay(120);
                Check("standard window keeps a light foundation or native Mica", window.SystemBackdrop is MicaBackdrop
                    || window.Content is Panel { Background: SolidColorBrush brush }
                    && brush.Color.A == 255 && brush.Color.R >= 235 && brush.Color.G >= 235 && brush.Color.B >= 235,
                    new { window.Title, actualTheme = ((FrameworkElement)window.Content).ActualTheme.ToString() });
                window.Close(); windows.Remove(window);
            }
        }
        catch (Exception ex) { Check("theme diagnostic exception", false, ex.ToString()); }
        finally
        {
            foreach (var window in windows.AsEnumerable().Reverse()) window.Close();
            File.WriteAllText(Path.Combine(directory, "themes.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }

    private static T Field<T>(object target, string name) where T : class =>
        typeof(ChromeBackdrop).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as T;

}
