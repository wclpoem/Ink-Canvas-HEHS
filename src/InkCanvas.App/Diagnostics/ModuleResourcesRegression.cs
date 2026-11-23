using System.Text.Json;
using System.Xml.Linq;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ModuleResourcesRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var window = new Window { Title = "模块共享资源检查" };
        try
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var light = CreateSample(ElementTheme.Light);
            var dark = CreateSample(ElementTheme.Dark);
            root.Children.Add(light);
            root.Children.Add(dark);
            Grid.SetColumn(dark, 1);
            window.Content = root;
            WindowChrome.MakeStandard(window, 880, 860);
            window.Activate();
            await Task.Delay(180);

            foreach (var sample in new[] { light, dark })
            {
                string theme = sample.ActualTheme.ToString();
                Check(theme + " title bars", Find<TitleBar>(sample, "Title").ActualHeight == 48
                    && Find<TitleBar>(sample, "CompactTitle").ActualHeight == 40);
                Check(theme + " card radius", Find<Border>(sample, "Card").CornerRadius == new CornerRadius(14));
                Check(theme + " button target", Find<Button>(sample, "Action").MinHeight == 44);
                Check(theme + " primary template", Find<Button>(sample, "Primary").Template is not null);
                Check(theme + " toggle template", Find<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(sample, "Pill").Template is not null);
                var icon = Find<TouchIconButton>(sample, "Icon");
                Check(theme + " icon style", icon.MinWidth == 44 && icon.MinHeight == 44
                    && icon.IconSize == 18 && icon.Padding == new Thickness(6));
                Check(theme + " icon template", icon.Template is not null && Descendants(icon).OfType<FontIcon>().Any());
                var compact = Find<TouchIconButton>(sample, "CompactIcon");
                Check(theme + " compact icon", compact.ActualWidth == 40 && compact.ActualHeight == 40);
                var unstyled = Find<TouchIconButton>(sample, "DefaultIcon");
                Check(theme + " original component defaults", unstyled.MinWidth == 40 && unstyled.MinHeight == 40
                    && unstyled.IconSize == 20 && unstyled.Padding == new Thickness(8) && unstyled.Template is not null);
                Check(theme + " original explicit overrides", Find<TouchIconButton>(sample, "OverrideIcon").MinHeight == 52);
                Check(theme + " regular font", Find<FontIcon>(sample, "Font").FontFamily.Source.Contains("FluentSystemIcons-Regular.ttf"));
                Check(theme + " system font", Find<FontIcon>(sample, "SystemFont").FontFamily.Source == "Segoe Fluent Icons");
                Check(theme + " text styles", Find<TextBlock>(sample, "Caption").FontSize == 12
                    && Find<TextBlock>(sample, "Body").FontSize == 14 && Find<TextBlock>(sample, "Subtitle").FontSize == 16);
                CheckTheme(sample, theme);

                icon.IconSize = 24;
                Check(theme + " local icon size", Descendants(icon).OfType<FontIcon>().Single().FontSize == 24
                    && Descendants(icon).OfType<Viewbox>().Single().Width == 28);
                icon.ClearValue(TouchIconButton.IconSizeProperty);
                Check(theme + " cleared icon size restores style", icon.IconSize == 18
                    && Descendants(icon).OfType<FontIcon>().Single().FontSize == 18);
            }

            light.RequestedTheme = ElementTheme.Dark;
            dark.RequestedTheme = ElementTheme.Light;
            await Task.Delay(100);
            CheckTheme(light, "light changed to dark");
            CheckTheme(dark, "dark changed to light");
            Check("independent roots use different theme colors",
                ColorOf(Find<TextBlock>(light, "Body").Foreground) != ColorOf(Find<TextBlock>(dark, "Body").Foreground));

            var flyout = new Flyout
            {
                Content = new TextBlock { Text = "共享弹窗样式" },
                FlyoutPresenterStyle = (Style)Application.Current.Resources["IcModuleFlyoutPresenterStyle"],
            };
            flyout.ShowAt(Find<Button>(light, "Action"));
            await Task.Delay(100);
            var presenter = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<FlyoutPresenter>().Single();
            Check("flyout loads style and native template", presenter.Template is not null
                && presenter.CornerRadius == new CornerRadius(10) && presenter.Padding == new Thickness(16));
            flyout.Hide();

            var dictionary = Application.Current.Resources.MergedDictionaries.Single(item =>
                item.Source?.OriginalString.EndsWith("/ModuleResources.xaml", StringComparison.Ordinal) == true);
            var themes = new[] { "Light", "Dark", "HighContrast" }
                .Select(key => (ResourceDictionary)dictionary.ThemeDictionaries[key]).ToArray();
            // Read declared keys from XAML. Realizing ThemeResource values during WinRT
            // ResourceDictionary enumeration can change its indexed view.
            var palette = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "InkCanvas.Controls", "Themes", "ModuleResources.xaml"));
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var declaredThemes = palette.Root.Elements().Single(item => item.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements().ToArray();
            string[] keys = declaredThemes[0].Elements().Select(item => (string)item.Attribute(xaml + "Key")).OrderBy(key => key).ToArray();
            Check("all themes expose the same palette", keys.Length > 0 && declaredThemes.All(theme =>
                keys.SequenceEqual(theme.Elements().Select(item => (string)item.Attribute(xaml + "Key")).OrderBy(key => key))));
            foreach (string key in keys)
                Check("high contrast color resolves " + key, themes[2][key] is SolidColorBrush);
            Check("existing tokens remain available", Application.Current.Resources["IcTouchTargetMin"] is double size && size == 40);
        }
        catch (Exception exception)
        {
            Check("resource regression exception", false, exception.ToString());
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "module-resources.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            Application.Current.Exit();
        }

        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });

        void CheckTheme(FrameworkElement sample, string theme)
        {
            Check(theme + " semantic text color", ColorOf(Find<TextBlock>(sample, "Body").Foreground)
                == ColorOf(Find<TextBlock>(sample, "NativeText").Foreground));
            Check(theme + " semantic card color", ColorOf(Find<Border>(sample, "Card").Background)
                == ColorOf(Find<Border>(sample, "NativeCard").Background));
        }
    }

    private static StackPanel CreateSample(ElementTheme theme)
    {
        var sample = (StackPanel)XamlReader.Load("""
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:controls="using:InkCanvas.Controls.Controls"
                        Spacing="8" Padding="16" Background="{ThemeResource IcModuleWindowBackgroundBrush}">
                <TitleBar x:Name="Title" Title="标准标题栏" Style="{StaticResource IcModuleTitleBarStyle}" />
                <TitleBar x:Name="CompactTitle" Title="紧凑标题栏" Style="{StaticResource IcModuleCompactTitleBarStyle}" />
                <Border x:Name="Card" Style="{StaticResource IcModuleCardStyle}">
                    <StackPanel Spacing="8">
                        <TextBlock x:Name="Subtitle" Text="模块与插件" Style="{StaticResource IcModuleSubtitleStyle}" />
                        <TextBlock x:Name="Body" Text="正文" Style="{StaticResource IcModuleBodyStyle}" />
                        <TextBlock x:Name="Caption" Text="辅助信息" Style="{StaticResource IcModuleCaptionStyle}" />
                    </StackPanel>
                </Border>
                <TextBlock x:Name="NativeText" Text="原生主题文字" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                <Border x:Name="NativeCard" Height="4" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
                <Button x:Name="Action" Content="常规操作" Style="{StaticResource IcModuleButtonStyle}" />
                <Button x:Name="Primary" Content="主要操作" Style="{StaticResource IcModulePrimaryButtonStyle}" />
                <ToggleButton x:Name="Pill" Content="选项" IsChecked="True" Style="{StaticResource IcModulePillStyle}" />
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <controls:TouchIconButton x:Name="Icon" Glyph="&#xF6B3;" Style="{StaticResource IcModuleIconButtonStyle}" />
                    <controls:TouchIconButton x:Name="CompactIcon" Glyph="&#xF6B3;" Style="{StaticResource IcModuleCompactIconButtonStyle}" />
                    <controls:TouchIconButton x:Name="DefaultIcon" Glyph="&#xF6B3;" />
                    <controls:TouchIconButton x:Name="OverrideIcon" Glyph="&#xF6B3;" MinHeight="52" />
                    <FontIcon x:Name="Font" Glyph="&#xF6B3;" Style="{StaticResource IcModuleFontIconStyle}" />
                    <FontIcon x:Name="SystemFont" Glyph="&#xE8C8;" Style="{StaticResource IcModuleSystemFontIconStyle}" />
                </StackPanel>
            </StackPanel>
            """);
        sample.RequestedTheme = theme;
        return sample;
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement => (T)root.FindName(name);
    private static Windows.UI.Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
