using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AiAssistant.Providers;

internal static class ProviderIcon
{
    public static FrameworkElement Label(string icon, string name)
    {
        var image = new Image { Width = 26, Height = 26, Stretch = Stretch.Uniform };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Padding = new Thickness(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White), Child = image });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        image.Loaded += async (_, _) =>
        {
            using var input = typeof(ProviderIcon).Assembly.GetManifestResourceStream("InkCanvas.AiIcons." + icon + ".svg");
            if (input is null) return;
            using var memory = new InMemoryRandomAccessStream();
            using var output = memory.AsStreamForWrite();
            await input.CopyToAsync(output); await output.FlushAsync(); memory.Seek(0);
            // The SVG source retains its decoded image after the input stream is closed.
            var source = new SvgImageSource();
            try { await source.SetSourceAsync(memory); image.Source = source; }
            catch (Exception) { image.Source = null; }
        };
        return panel;
    }
}
