using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.App.Diagnostics;

internal static class MicaWindowRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Appearance.Theme = 1;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        var windows = new List<Window>();
        try
        {
            var settings = new SettingsWindow(services, shell: shell); windows.Add(settings);
            settings.Activate(); settings.SelectCategory("外观选项"); await Task.Delay(200);
            var material = settings.SystemBackdrop;
            for (int index = 0; index < 3; index++)
            {
                var combo = Descendants(settings.Content).OfType<ComboBox>().Single(item => AutomationProperties.GetName(item) == "颜色主题");
                combo.SelectedIndex = index;
                await Task.Delay(180);
                await CheckWindow(settings, "settings-" + index);
                Check("settings rebuild reuses its window material " + index, ReferenceEquals(material, settings.SystemBackdrop));
                var workspaceView = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
                Check("settings card layout exposes Mica between cards " + index,
                    IsTransparent((Brush)workspaceView.Resources["NavigationViewContentBackground"]));
            }
            settings.Close(); windows.Remove(settings);

            services.Settings.Appearance.Theme = 1;
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>() ?? throw new InvalidOperationException("PDF module missing");
            string fixture = Path.Combine(directory, "material.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics", true)!
                .GetMethod("CreateFixture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { fixture, 2 });
            await pdf.OpenAsync(fixture); await Task.Delay(250);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single(); windows.Add(workspace.Window);
            await CheckWindow(workspace.Window, "pdf-light");
            services.Settings.Appearance.Theme = 2; workspace.ApplySettings(); await Task.Delay(100);
            await CheckWindow(workspace.Window, "pdf-dark");
            Check("PDF viewport keeps a content layer over Mica", Descendants(workspace.Window.Content).OfType<InkSurface>().Single().Parent
                is Grid { Background: SolidColorBrush { Color.A: > 0 and < 255 } });
            await workspace.CloseAsync(); windows.Remove(workspace.Window);

            var geometry = services.Modules.GetExport<IAnalyticGeometryService>() ?? throw new InvalidOperationException("Geometry module missing");
            geometry.Open(); await Task.Delay(350);
            var workbench = shell.OpenWindows.Single(item => item.GetType().Name == "GeometryWorkbenchWindow"); windows.Add(workbench);
            await CheckWindow(workbench, "geometry-dark");
            WindowChrome.ApplyTheme(workbench, 1); await Task.Delay(100);
            await CheckWindow(workbench, "geometry-light");
            workbench.Close(); windows.Remove(workbench);
            var legacyType = geometry.GetType().Assembly.GetType("InkCanvas.Modules.AnalyticGeometry.GeometryWindow", true)!;
            var editor = (Window)Activator.CreateInstance(legacyType, shell.CanvasToolsTestHost)!; windows.Add(editor);
            editor.Activate(); await Task.Delay(180);
            await CheckWindow(editor, "geometry-editor-dark");
            editor.Close(); windows.Remove(editor);
        }
        catch (Exception ex) { checks.Add(new { check = "Mica window regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            foreach (var window in windows.ToArray()) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "mica.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        async Task CheckWindow(Window window, string name)
        {
            var frame = (WindowFrame)window.Content;
            Check("native base Mica is installed " + name, window.SystemBackdrop is MicaBackdrop { Kind: MicaKind.Base });
            Check("title and frame reveal the material " + name, IsTransparent(frame.Background) && IsTransparent(frame.Header.Background));
            var body = frame.Children.OfType<Grid>().Single();
            Check("window body does not cover the material " + name, IsTransparent(body.Background));
            Check("material and title follow the content theme " + name, frame.ActualTheme == body.ActualTheme && frame.Header.ActualTheme == body.ActualTheme);
            // RenderTargetBitmap omits the native backdrop. Paint its neutral fallback for a layout-only image.
            var original = frame.Background;
            frame.Background = new SolidColorBrush(frame.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
            try { await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, name + "-fallback.png")); }
            finally { frame.Background = original; }
            Check("fallback layout capture restores the material surface " + name, IsTransparent(frame.Background));
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static bool IsTransparent(Brush brush) => brush is null or SolidColorBrush { Color.A: 0 };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var element in Descendants(VisualTreeHelper.GetChild(root, index))) yield return element;
    }
}
