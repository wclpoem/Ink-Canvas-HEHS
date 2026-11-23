using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class BoardChromeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "archives");
        services.Settings.Archive.AutomaticDirectory = Path.Combine(directory, "automatic");
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        try
        {
            CheckCursor();
            shell.ToggleBoard();
            await Task.Delay(150);
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            var root = (FrameworkElement)window.Content;
            foreach (bool regular in new[] { true, false })
            foreach (var background in new[] { BoardBackground.White, BoardBackground.Black })
            {
                services.Settings.Appearance.IsIconRegularStyle = regular;
                services.ApplySettings();
                services.Board.Background = background;
                await Task.Delay(70);
                var add = Buttons(root, "加页");
                Check("all last-page add buttons use document-add icon", add.Length == 3
                    && add.All(button => Icons(button).Single().Glyph == Glyphs.AddPage));
                var exit = Buttons(root, "退出");
                Check("board selection uses a single selection-frame icon", Buttons(root, "选择") is { Length: 1 } select
                    && Icons(select[0]).Single().Glyph == Glyphs.Select);
                Check("both exit buttons use sign-out icon", exit.Length == 2
                    && exit.All(button => Icons(button).Single().Glyph == Glyphs.ExitBoard));
                Check("both font preferences reach board buttons", add.Concat(exit).All(button =>
                    Icons(button).Single().FontFamily.Source == IconFontStyle.Family));
                Check("icons fit their board button cells", add.Concat(exit).All(button =>
                {
                    var host = Descendants(button).OfType<Viewbox>().Single();
                    var bounds = host.TransformToVisual(button).TransformBounds(new(0, 0, host.ActualWidth, host.ActualHeight));
                    return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= button.ActualWidth + 1 && bounds.Bottom <= button.ActualHeight + 1;
                }));
                await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"board-{regular}-{background}.png"));
            }

            var allAdd = Buttons(root, "加页");
            foreach (var button in allAdd)
            {
                int before = services.Board.PageCount;
                Invoke(button);
                await Task.Delay(40);
                Check("each last-page add entry inserts one page", services.Board.PageCount == before + 1
                    && services.Board.CurrentIndex == before && ReferenceEquals(window.CurrentController, services.Board.Current));
            }
            services.Board.GoTo(0);
            await Task.Delay(60);
            var next = Buttons(root, "下一页");
            Check("earlier pages restore both next-page icons and labels", next.Length == 2
                && next.All(button => Icons(button).Single().Glyph == "\uF16B") && Buttons(root, "加页").Length == 1);
            int pages = services.Board.PageCount;
            Invoke(next[0]);
            await Task.Delay(40);
            Check("next-page entry navigates without adding", services.Board.CurrentIndex == 1 && services.Board.PageCount == pages);
            Invoke(Buttons(root, "加页").Single());
            await Task.Delay(40);
            Check("dedicated add inserts after a middle page", services.Board.PageCount == pages + 1 && services.Board.CurrentIndex == 2);

            foreach (bool saveOnExit in new[] { false, true })
            foreach (int entry in new[] { 0, 1 })
            {
                services.Settings.Archive.SaveOnExit = saveOnExit;
                services.ApplySettings();
                window = shell.OpenWindows.OfType<BoardWindow>().Single();
                root = (FrameworkElement)window.Content;
                Draw(window.CurrentController);
                int strokes = services.Board.Current.Document.Count;
                var exit = Buttons(root, "退出")[entry];
                Invoke(exit);
                for (int poll = 0; poll < 100 && shell.OpenWindows.OfType<BoardWindow>().Any(); poll++) await Task.Delay(25);
                Check("exit entry closes board and restores desktop", !shell.OpenWindows.OfType<BoardWindow>().Any()
                    && !services.BoardModeActive && shell.Overlay.Window.IsVisible);
                Check("exit keeps current page contents", services.Board.Current.Document.Count == strokes);
                if (saveOnExit)
                    Check("exit entry saves before closing", services.Archives.LastAutomaticSave is { Reason: "退出白板" } archive
                        && File.Exists(archive.Path) && !services.Archives.HasUnsavedChanges);
                CheckCursor();
                shell.ToggleBoard();
                await Task.Delay(80);
                Check("board reopens after toolbar exit", shell.OpenWindows.OfType<BoardWindow>().Count() == 1
                    && services.Board.Current.Document.Count == strokes);
            }
        }
        catch (Exception error) { Check("board chrome regression exception", false, error.ToString()); }
        finally
        {
            services.Settings.Archive.SaveOnExit = false;
            foreach (var board in shell.OpenWindows.OfType<BoardWindow>().ToArray()) await board.CloseWithArchiveAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "board-chrome.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            Application.Current.Exit();
        }

        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });

        void CheckCursor()
        {
            var overlay = shell.Overlay.Window;
            bool clickThrough = overlay.ClickThrough;
            IntPtr original = GetCursor();
            try
            {
                overlay.ClickThrough = false;
                IntPtr arrow = NativeMethods.LoadCursorW(IntPtr.Zero, new(NativeMethods.IDC_ARROW));
                foreach (int inherited in new[] { 32514, 32650 }) // IDC_WAIT / IDC_APPSTARTING
                {
                    NativeMethods.SetCursor(NativeMethods.LoadCursorW(IntPtr.Zero, new(inherited)));
                    var foreground = NativeMethods.GetForegroundWindow();
                    IntPtr handled = NativeMethods.SendMessageW(overlay.Handle, NativeMethods.WM_SETCURSOR, overlay.Handle,
                        new((NativeMethods.WM_MOUSEMOVE << 16) | NativeMethods.HTCLIENT));
                    Check("annotation replaces inherited busy cursor", arrow != IntPtr.Zero && handled == new IntPtr(1) && GetCursor() == arrow, inherited);
                    Check("cursor update keeps annotation no-activate", NativeMethods.GetForegroundWindow() == foreground
                        && NativeMethods.HasExStyle(overlay.Handle, NativeMethods.WS_EX_NOACTIVATE));
                }
                overlay.ClickThrough = true;
                Check("mouse mode retains input pass-through", overlay.ClickThrough);
            }
            finally
            {
                overlay.ClickThrough = clickThrough;
                NativeMethods.SetCursor(original);
            }
        }
    }

    private static void Draw(InkController controller)
    {
        controller.Tool = InkTool.Pen;
        var input = new OverlayPointer(27011, 200, 160, .5f, 0, 0, OverlayPointerKind.Mouse, 1000);
        controller.OnPointerDown(input);
        controller.OnPointerMove(input with { X = 300, Y = 200, TimeMs = 1050 });
        controller.OnPointerUp(input with { X = 330, Y = 200, TimeMs = 1070 });
    }

    private static Button[] Buttons(DependencyObject root, string label) => Descendants(root).OfType<Button>()
        .Where(button => button.Content is Border { Child: Grid }
            && AutomationProperties.GetName(button) == label).ToArray();
    private static IEnumerable<FontIcon> Icons(Button button) => Descendants(button).OfType<FontIcon>();
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetCursor();
}
