using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class ScreenArchiveRegression
{
    public static void Fixture(string readyPath)
    {
        var screen = ScreenService.Virtual;
        var window = new Window { Title = "Screen archive fixture", Content = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 110, 188)) } };
        WindowChrome.MakeBorderless(window, topmost: false);
        WindowChrome.Place(window, screen.X, screen.Y, screen.Width, screen.Height);
        window.Closed += (_, _) => Application.Current.Exit();
        window.Activate();
        File.WriteAllText(readyPath, "ready");
    }

    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--screenarchivefixture=" + Path.Combine(directory, "fixture.ready"))
        { UseShellExecute = false, CreateNoWindow = true });
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "archives");
        services.Settings.Archive.ScreenCapture = new() { Enabled = true, IncludeOrdinaryAnnotation = false };
        services.ApplySettings();
        using var shell = new ShellController(services);
        SettingsWindow settings = null;
        try
        {
            await Until(() => File.Exists(Path.Combine(directory, "fixture.ready")));
            shell.Start(); shell.StartInClickThrough();
            shell.ToggleClickThrough();
            await Task.Delay(250);
            var ink = services.OverlayInk;
            ink.ViewTransform = Matrix3x2.Identity;
            var visible = Stroke(120, 120);
            var outside = Stroke(-200, -160);
            ink.Document.Add(visible); ink.Document.Add(outside);
            Check("ordinary capture requires its separate switch", await shell.SaveScreenArchiveAsync("disabled") is null && services.ScreenArchives.LastSave is null);
            services.Settings.Archive.ScreenCapture.IncludeOrdinaryAnnotation = true;
            services.ApplySettings();
            var result = await shell.SaveScreenArchiveAsync("屏幕存档验收");
            bool nativeCapture = result is not null;
            if (result is null)
            {
                checks.Add(new { check = "native desktop capture", passed = (bool?)null, detail = services.ScreenArchives.LastError });
                shell.ScreenArchiveSource = screen =>
                {
                    var bytes = new byte[screen.Width * screen.Height * 4];
                    for (int index = 0; index < bytes.Length; index += 4)
                    { bytes[index] = 188; bytes[index + 1] = 110; bytes[index + 2] = 22; bytes[index + 3] = 255; }
                    return (bytes, screen.Width, screen.Height);
                };
                result = await shell.SaveScreenArchiveAsync("固定截图存档验收");
            }
            else Check("native desktop capture", true);
            Check("desktop snapshot produces a single-page archive", result is { PageCount: 1 } && File.Exists(result.Path));
            var loaded = new BoardService();
            try
            {
                await loaded.LoadBoardAsync(result.Path);
                var background = loaded.CurrentImages.Single(item => item.Name == "屏幕截图背景");
                Check("archive retains editable and offscreen strokes", loaded.Current.Document.Count == 2
                    && loaded.Current.Document.Strokes.Any(item => item.Points.Any(point => point.X < 0 && point.Y < 0)));
                Check("screenshot stays behind ink and records its type", background.LayerOrder == 0 && loaded.ArchiveMetadata.Kind == "screen"
                    && loaded.Current.DrawingObjects.Skip(1).All(item => item.Stroke is not null && item.LayerOrder > 0));
                var screen = ScreenService.Virtual;
                var pixels = await Pixels(background.Data);
                int x = Math.Clamp((int)(120 * shell.Overlay.Window.DpiScale), 0, pixels.Width - 1);
                int y = Math.Clamp((int)(120 * shell.Overlay.Window.DpiScale), 0, pixels.Height - 1);
                int offset = (y * pixels.Width + x) * 4;
                Check(nativeCapture ? "background contains the underlying desktop without duplicate ink" : "fixture background pixels encode without additional ink", Math.Abs(pixels.Bytes[offset] - 188) < 3
                    && Math.Abs(pixels.Bytes[offset + 1] - 110) < 3 && Math.Abs(pixels.Bytes[offset + 2] - 22) < 3);
                var toolbar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
                var bar = toolbar.AppWindow.Position; var size = toolbar.AppWindow.Size;
                int barX = Math.Clamp(bar.X + size.Width / 2 - screen.X, 0, pixels.Width - 1);
                int barY = Math.Clamp(bar.Y + size.Height / 2 - screen.Y, 0, pixels.Height - 1);
                offset = (barY * pixels.Width + barX) * 4;
                if (nativeCapture) Check("background excludes the floating toolbar", Math.Abs(pixels.Bytes[offset] - 188) < 3 && Math.Abs(pixels.Bytes[offset + 2] - 22) < 3);
                await File.WriteAllBytesAsync(Path.Combine(directory, "captured-background.png"), background.Data);
            }
            finally { foreach (var page in loaded.Pages) page.Dispose(); }
            ink.Document.Add(Stroke(250, 200));
            shell.ToggleClickThrough();
            await Until(() => services.ScreenArchives.LastSave?.Reason == "切换鼠标模式" && !services.ScreenArchives.IsSaving);
            Check("mouse mode uses the new archive option", services.ScreenArchives.LastSave.Reason == "切换鼠标模式");
            shell.ToggleClickThrough();
            ink.Document.Add(Stroke(300, 260));
            shell.ClearCanvas();
            Check("clear happens after a stable ink snapshot is taken", ink.Document.Count == 0);
            await Until(() => services.ScreenArchives.LastSave?.Reason == "清屏前存档" && !services.ScreenArchives.IsSaving);
            var cleared = new BoardService();
            try { await cleared.LoadBoardAsync(services.ScreenArchives.LastSave.Path); Check("clear backup contains all prior strokes", cleared.Current.Document.Count == 4); }
            finally { foreach (var page in cleared.Pages) page.Dispose(); }
            Check("no-ink automatic capture creates no additional file", await shell.SaveScreenArchiveAsync("empty", true) is null);
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("保存与备份"); await Task.Delay(250);
            Check("screen archive settings participate in search", settings.SearchSettings("普通批注模式").Any(item => item.Category == "保存与备份"));
            var root = (FrameworkElement)settings.Content;
            var scroll = Descendants(root).OfType<ScrollViewer>().Single(item => item.Content is Border { Child: StackPanel panel }
                && panel.Children.OfType<TextBlock>().Any(text => text.Text == "截图与墨迹存档"));
            var section = Descendants(root).OfType<TextBlock>().Single(item => item.Text == "截图与墨迹存档");
            float top = (float)section.TransformToVisual((UIElement)scroll.Content).TransformPoint(default).Y;
            scroll.ChangeView(null, top - 10, null, true); await Task.Delay(80);
            var frame = (Controls.Controls.WindowFrame)root;
            frame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 243, 243));
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "screen-archive-settings.png"));
            settings.Close(); settings = null;
        }
        catch (Exception ex) { checks.Add(new { check = "screen archive regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            settings?.Close();
            var start = Environment.TickCount64;
            while (services.ScreenArchives.IsSaving && Environment.TickCount64 - start < 5000) await Task.Delay(40);
            await File.WriteAllTextAsync(Path.Combine(directory, "screen-archive.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            if (!fixture.HasExited) fixture.CloseMainWindow();
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static InkStroke Stroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 20, Color = InkColor.Black, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y); stroke.AddPoint(x + 80, y); stroke.Complete(); return stroke;
    }
    private static async Task<(byte[] Bytes, int Width, int Height)> Pixels(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }
    private static async Task Until(Func<bool> predicate)
    {
        long start = Environment.TickCount64;
        while (!predicate()) { if (Environment.TickCount64 - start > 8000) throw new TimeoutException("屏幕存档验收超时。"); await Task.Delay(40); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
