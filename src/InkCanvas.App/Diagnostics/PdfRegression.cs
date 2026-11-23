using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class PdfRegression
{
    public static async Task Run(string directory, bool lifetimeOnly = false)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services); shell.Start();
        var checks = new List<object>();
        try
        {
            await Task.Delay(300);
            var module = services.Modules.GetExport<IPdfWorkspaceService>() ?? throw new InvalidOperationException("PDF module did not start. " + string.Join(" / ", services.Modules.Status.Select(status => status.Id + " " + status.State + " " + status.Error)));
            string diagnostic = lifetimeOnly ? "PdfRasterizerDiagnostics" : "PdfDiagnostics";
            var method = module.GetType().Assembly.GetType("InkCanvas.Modules.Pdf." + diagnostic, true)!.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!;
            await (Task)method.Invoke(null, new object[] { shell.WorkspaceHost, directory });
            if (lifetimeOnly) return;
            string source = Path.Combine(directory, "classroom.pdf");
            await module.OpenAsync(source); await Task.Delay(250);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            bool suppliedPdfForeground = NativeMethods.GetForegroundWindow() != WindowChrome.GetHwnd(workspace.Window);
            if (suppliedPdfForeground) shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            Check("main toolbar binds PDF controller", ReferenceEquals(bar.Bar.Controller, workspace.Controller) && !shell.Overlay.Window.IsVisible,
                new { foregroundContextSuppliedByTest = suppliedPdfForeground });
            Check("PDF gestures use independent toolbar settings", ReferenceEquals(bar.Bar.GestureSettingsOverride, workspace.Gestures)
                && !workspace.Controller.MultiTouchEnabled && services.Settings.Gesture.IsEnableMultiTouchMode);
            workspace.Gestures.IsEnableMultiTouchMode = true; services.ApplySettings();
            Check("PDF gesture choices apply to its controllers", workspace.Controller.MultiTouchEnabled);
            workspace.Gestures.IsEnableMultiTouchMode = false; services.ApplySettings();
            workspace.Window.GetType().GetMethod("GoToPage")!.Invoke(workspace.Window, new object[] { 1 });
            Check("page changes rebind main toolbar", ReferenceEquals(bar.Bar.Controller, workspace.Controller));
            shell.ToggleClickThrough();
            Check("mouse mode pans PDF without enabling overlay", workspace.Controller.Tool == InkTool.Pan && !shell.Overlay.Window.IsVisible);
            shell.OpenPenPalette(false);
            Check("shared pen popup targets PDF", workspace.Controller.Tool == InkTool.Pen && shell.OpenWindows.OfType<PenPaletteWindow>().Any());
            await Task.Delay(120);
            foreach (var palette in shell.OpenWindows.OfType<PenPaletteWindow>().ToArray()) palette.Close();
            await CheckDesktopRouting(workspace, bar);
            shell.ToggleBoard(); await Task.Delay(150);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            Check("board suspends PDF without replacing its ink", ReferenceEquals(bar.Bar.Controller, services.Board.Current));
            board.Close(); await Task.Delay(100);
            bool boardReturnContextSupplied = NativeMethods.GetForegroundWindow() != WindowChrome.GetHwnd(workspace.Window);
            if (boardReturnContextSupplied) shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            Check("closing board restores PDF toolbar and viewport", ReferenceEquals(bar.Bar.Controller, workspace.Controller) && !shell.Overlay.Window.IsVisible,
                new { foregroundContextSuppliedByTest = boardReturnContextSupplied });
            var previousPage = services.Board.Current;
            var import = module.ImportToBoardAsync(source);
            var dialog = await FindDialogAsync(shell, "导入 PDF 到新画板页");
            var range = Descendants(dialog.Content as DependencyObject).OfType<TextBox>().Single(box => box.PlaceholderText.Contains("1-5"));
            range.Text = "0-9999"; Invoke(dialog, "导入"); await Task.Delay(80);
            Check("invalid import range keeps dialog open", !import.IsCompleted && services.Board.PageCount == 1);
            range.Text = "1-2"; Invoke(dialog, "导入"); await import.WaitAsync(TimeSpan.FromSeconds(30));
            Check("PDF import appends pages without replacing existing board", services.Board.PageCount == 3 && ReferenceEquals(services.Board.Pages[0], previousPage));
            services.Board.GoTo(1);
            Check("PDF import restores editable native ink above page image", services.Board.CurrentImages.Count == 1 && services.Board.Current.Document.Count == 1
                && services.Board.Current.Document.Strokes[0].LayerOrder > services.Board.CurrentImages[0].LayerOrder);
            var canceled = module.ImportToBoardAsync(source);
            dialog = await FindDialogAsync(shell, "导入 PDF 到新画板页"); Invoke(dialog, "取消"); await canceled;
            Check("canceling PDF import preserves board pages", services.Board.PageCount == 3);
            shell.OpenWindows.OfType<BoardWindow>().Single().Close();
            await workspace.CloseAsync();
            Check("closing PDF restores desktop controller", ReferenceEquals(bar.Bar.Controller, services.OverlayInk));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "pdf-error.txt"), ex.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-integration.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });

        async Task CheckDesktopRouting(IDocumentWorkspace workspace, FloatingBarWindow bar)
        {
            string statePath = Path.Combine(directory, "pdf-foreground-host.json");
            File.Delete(statePath);
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--activationhost=" + statePath);
            using var peer = Process.Start(start);
            try
            {
                nint foreground = 0;
                for (int attempt = 0; attempt < 60 && foreground == 0; attempt++)
                {
                    await Task.Delay(100);
                    try
                    {
                        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
                        foreground = (nint)state.RootElement.GetProperty("hwnd").GetInt64();
                    }
                    catch (Exception ex) when (ex is IOException or JsonException) { }
                }
                if (foreground == 0) throw new TimeoutException("PDF foreground test host did not start.");
                var pdfController = workspace.Controller;
                int pdfCount = pdfController.Document.Count;
                NativeMethods.SetForegroundWindow(foreground); await Task.Delay(250);
                bool liveForeground = NativeMethods.GetForegroundWindow() == foreground;
                if (!liveForeground) shell.RefreshDocumentTarget(foreground);
                Check("switching away from PDF restores screen annotation", ReferenceEquals(bar.Bar.Controller, services.OverlayInk) && shell.Overlay.Window.IsVisible,
                    new { bound = ReferenceEquals(bar.Bar.Controller, services.OverlayInk), visible = shell.Overlay.Window.IsVisible,
                        liveForeground, foreground = NativeMethods.GetForegroundWindow().ToInt64(), expectedForeground = foreground.ToInt64() });
                if (shell.Overlay.ClickThrough) shell.ToggleClickThrough();
                services.OverlayInk.Tool = InkTool.Pen;
                int screenCount = services.OverlayInk.Document.Count;
                var input = new InkCanvas.Platform.Windows.OverlayPointer(884, 100, 100, .5f, 0, 0, InkCanvas.Platform.Windows.OverlayPointerKind.Mouse, 1000);
                services.OverlayInk.OnPointerDown(input);
                services.OverlayInk.OnPointerMove(input with { X = 160, Y = 120, TimeMs = 1030 });
                services.OverlayInk.OnPointerUp(input with { X = 180, Y = 130, TimeMs = 1050 });
                services.OverlayInk.FinishDotAnimations();
                Check("desktop annotation uses independent PDF and screen histories", services.OverlayInk.Document.Count == screenCount + 1 && pdfController.Document.Count == pdfCount);
                services.OverlayInk.Undo();
                shell.ToggleClickThrough();
                workspace.Window.Activate(); NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(workspace.Window)); await Task.Delay(250);
                bool pdfForegroundSupplied = NativeMethods.GetForegroundWindow() != WindowChrome.GetHwnd(workspace.Window);
                if (pdfForegroundSupplied) shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
                Check("returning to PDF restores page toolbar and input", ReferenceEquals(bar.Bar.Controller, pdfController) && !shell.Overlay.Window.IsVisible,
                    new { foregroundContextSuppliedByTest = pdfForegroundSupplied });
                shell.OpenPenPalette(false);
                Check("PDF palette retains the document target", ReferenceEquals(bar.Bar.Controller, pdfController) && !shell.Overlay.Window.IsVisible);
                await Task.Delay(120);
                foreach (var palette in shell.OpenWindows.OfType<PenPaletteWindow>().ToArray()) palette.Close();
                workspace.Window.AppWindow.Hide();
                shell.RefreshDocumentTarget(0);
                Check("hidden PDF restores screen annotation", ReferenceEquals(bar.Bar.Controller, services.OverlayInk) && shell.Overlay.Window.IsVisible);
                workspace.Window.Activate();
                shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
                Check("showing PDF restores its unchanged ink history", ReferenceEquals(bar.Bar.Controller, pdfController) && pdfController.Document.Count == pdfCount);
            }
            finally
            {
                if (!peer.HasExited) { peer.CloseMainWindow(); await Task.Delay(200); }
            }
        }
    }
    private static async Task<ContentDialog> FindDialogAsync(ShellController shell, string title)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            foreach (var root in shell.OpenWindows.Select(window => (window.Content as FrameworkElement)?.XamlRoot).Where(root => root is not null).Distinct())
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            {
                var dialog = Descendants(popup.Child).OfType<ContentDialog>().FirstOrDefault(item => item.Title?.ToString() == title);
                if (dialog is not null && Descendants(dialog).OfType<Button>().Any(button => button.Content?.ToString() == "取消")) return dialog;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("PDF dialog did not appear " + title);
    }
    private static void Invoke(ContentDialog dialog, string label)
    {
        var button = Descendants(dialog).OfType<Button>().First(button => button.Content?.ToString() == label);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}
