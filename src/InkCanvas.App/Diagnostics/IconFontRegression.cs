using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class IconFontRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        var window = new Window { Title = "字体图标检查" };
        var panel = new Grid { Width = 840 };
        var checks = new List<object>();
        var glyphs = typeof(Glyphs).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()))
            .Where(item => item.Value.Length == 1 && item.Value[0] >= '\uE000').ToArray();
        var icons = new List<FontIcon>();
        for (int col = 0; col < 10; col++) panel.ColumnDefinitions.Add(new() { Width = new GridLength(84) });
        for (int row = 0; row < (glyphs.Length + 9) / 10; row++) panel.RowDefinitions.Add(new() { Height = new GridLength(72) });
        for (int index = 0; index < glyphs.Length; index++)
        {
            var item = glyphs[index];
            var icon = new FontIcon { FontFamily = IconFontStyle.Font, Glyph = item.Value, FontSize = 28,
                IsTextScaleFactorEnabled = false, HorizontalAlignment = HorizontalAlignment.Center, Height = 40 };
            icons.Add(icon);
            var tile = new StackPanel { Spacing = 4, Children = { icon, new TextBlock { Text = item.Name, FontSize = 9,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } };
            Grid.SetColumn(tile, index % 10); Grid.SetRow(tile, index / 10); panel.Children.Add(tile);
        }
        window.Content = panel;
        WindowChrome.MakeStandard(window, 900, 760);
        window.Activate();
        try
        {
            shell.ToggleClickThrough();
            await Task.Delay(100);
            Check("new installations use Regular icons", services.Settings.Appearance.IsIconRegularStyle);
            foreach (bool regular in new[] { true, false, true })
            {
                services.Settings.Appearance.IsIconRegularStyle = regular;
                services.ApplySettings();
                services.SaveSettings();
                IconFontStyle.Refresh(panel);
                string file = regular ? "FluentSystemIcons-Regular.ttf" : "FluentSystemIcons.ttf";
                Check("both font assets are present " + regular, File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", file)));
                Check("same controls follow style changes " + regular, icons.All(icon => icon.FontFamily.Source == IconFontStyle.Family));
                Check("icon preference persists " + regular, services.Store.Load().Appearance.IsIconRegularStyle == regular);
                var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
                var boardEntry = Descendants(bar.Content).OfType<TouchIconButton>().Single(button => button.Label == "画板");
                var selectionEntry = Descendants(bar.Content).OfType<TouchIconButton>().Single(button => button.Label == "选择");
                Check("whiteboard entry uses the whiteboard icon " + regular,
                    Descendants(boardEntry).OfType<FontIcon>().Single().Glyph == Glyphs.Board);
                Check("selection uses a single selection-frame icon " + regular,
                    Descendants(selectionEntry).OfType<FontIcon>().Single().Glyph == Glyphs.Select);
                var handle = Descendants(bar.Content).OfType<Border>().Single(item => AutomationProperties.GetName(item) == "拖动移动工具栏");
                var smile = Descendants(handle).OfType<FontIcon>().Single();
                Check("toolbar drag handle keeps smile in both styles " + regular,
                    smile.Glyph == Glyphs.Emoji && smile.FontFamily.Source == IconFontStyle.Family);
                foreach (int theme in new[] { 1, 2 })
                {
                    panel.RequestedTheme = theme == 1 ? ElementTheme.Light : ElementTheme.Dark;
                    panel.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
                    foreach (var icon in icons) icon.Foreground = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
                    WindowChrome.ApplyTheme(bar, theme);
                    await Task.Delay(80);
                    Check("glyph sheet renders " + regular + " " + theme,
                        await BoardWindowRegression.SaveVisual(panel, Path.Combine(directory, $"icons-{regular}-{theme}.png")));
                    Check("toolbar updated icons render " + regular + " " + theme,
                        await BoardWindowRegression.SaveVisual((FrameworkElement)bar.Content, Path.Combine(directory, $"toolbar-{regular}-{theme}.png")));
                }
            }
            Check("toolbar icon screenshot", await BoardWindowRegression.SaveVisual((FrameworkElement)shell.OpenWindows.OfType<FloatingBarWindow>().Single().Content,
                Path.Combine(directory, "toolbar-regular.png")));
        }
        catch (Exception exception) { Check("font regression exception", false, exception.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "icons.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
