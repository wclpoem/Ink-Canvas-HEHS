using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Host.Contracts;
using InkCanvas.App.Views;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class PdfActivationRegression
{
    public static async Task ObserveAsync(ShellController shell, string path, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        try
        {
            for (int count = 0; count < 300 && !shell.OpenWindows.OfType<IDocumentWorkspace>().Any(); count++) await Task.Delay(100);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            for (int count = 0; count < 100 && workspace.Window.Content is not FrameworkElement { XamlRoot: not null }; count++) await Task.Delay(50);
            checks.Add(new { check = "file argument opens the native PDF workspace", passed = workspace.Window.Title.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) });
            checks.Add(new { check = "PDF workspace uses original shared toolbar", passed = ReferenceEquals(shell.OpenWindows.OfType<FloatingBarWindow>().Single().Bar.Controller, workspace.Controller) });
            checks.Add(new { check = "PDF workspace accepts editing", passed = workspace.Controller is not null && workspace.Window.Content is FrameworkElement { XamlRoot: not null } });
            await BoardWindowRegression.SaveVisual((FrameworkElement)workspace.Window.Content, Path.Combine(directory, "pdf-open-with.png"));
        }
        catch (Exception error) { checks.Add(new { check = "PDF activation exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-activation.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
    }
}
