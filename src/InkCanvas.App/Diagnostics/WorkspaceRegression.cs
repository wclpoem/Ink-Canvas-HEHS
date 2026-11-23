using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class WorkspaceRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var services = new AppServices(Path.Combine(directory, "settings-" + Guid.NewGuid().ToString("N")));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        bool cursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        try
        {
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            for (int i = 0; i < 10; i++)
            {
                Invoke(FindButton(bar.Content, "工具"));
                await Task.Delay(35);
                var panel = bar.GetOpenPanel(BarPanelKind.More);
                var watch = Stopwatch.StartNew();
                Invoke(FindButton(panel.Content, "设置"));
                shell.OpenSettings(); shell.OpenSettings();
                await Task.Delay(160);
                var settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
                watch.Stop();
                Check("settings opens as a normal window " + i, settings.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { IsAlwaysOnTop: false }
                    && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
                if (i == 0)
                {
                    var caption = WindowChrome.GetScreenBounds(settings, ((WindowFrame)settings.Content).Header);
                    SetCursorPos(caption.X + 180, caption.Y + 24);
                    MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(30);
                    MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(80);
                    NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(settings));
                    shell.ToggleClickThrough();
                    await Task.Delay(1100);
                    caption = WindowChrome.GetScreenBounds(settings, ((WindowFrame)settings.Content).Header);
                    var hit = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = caption.X + 30, Y = caption.Y + 24 }), 2);
                    checks.Add(new { check = "normal settings title bar receives input above annotation", passed = hit == WindowChrome.GetHwnd(settings),
                        hit = hit.ToInt64(), expected = WindowChrome.GetHwnd(settings).ToInt64(), foreground = NativeMethods.GetForegroundWindow().ToInt64(),
                        overlay = shell.Overlay.Window.Handle.ToInt64(), overlayTopmost = shell.Overlay.Window.Topmost, caption });
                    Check("maintenance keeps settings non-topmost", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
                    var other = new Window { Title = "Normal window order check", Content = new Grid() };
                    WindowChrome.MakeStandard(other, 640, 400, topmost: false);
                    other.Activate();
                    NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(other));
                    await Task.Delay(1100);
                    Check("switching to another normal window keeps settings non-topmost", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST)
                        && shell.Overlay.Window.Topmost && NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(other));
                    settings.Activate();
                    NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(settings));
                    await Task.Delay(80);
                    Check("reactivated settings receives input without becoming topmost", GetAncestor(WindowFromPoint(new NativeMethods.POINT
                        { X = caption.X + 30, Y = caption.Y + 24 }), 2) == WindowChrome.GetHwnd(settings)
                        && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
                    other.Close();
                    shell.ToggleClickThrough();
                }
                Check("settings opens once and dispatcher remains responsive " + i, watch.ElapsedMilliseconds < 2000
                    && Descendants(settings.Content).OfType<NavigationView>().Count() == 1
                    && ClassroomWindowRegression.CheckNativeFrame(settings, extended: true));
                foreach (string category in new[] { "画板选项", "手势选项", "墨迹识别选项", "外观选项", "自动选项" })
                {
                    settings.SelectCategory(category);
                    ((FrameworkElement)settings.Content).UpdateLayout();
                    await Task.Delay(8);
                }
                settings.SelectCategory("画板选项");
                ((FrameworkElement)settings.Content).UpdateLayout();
                var width = Descendants(settings.Content).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "荧光笔粗细");
                width.Value = 120;
                Check("settings retain independent highlighter width " + i, services.Settings.Canvas.HighlighterWidth == 60);
                if (i == 9)
                {
                    await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "settings-canvas.png"));
                    settings.SelectCategory("外观选项");
                    await Task.Delay(30);
                    await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "settings-appearance.png"));
                }
                settings.Close();
                await Task.Delay(20);
                Check("closing settings restores annotation surface ordering " + i, shell.Overlay.Window.Topmost);
            }

            shell.OpenGuide(); await Task.Delay(160);
            var guide = shell.OpenWindows.OfType<SettingsWindow>().Single();
            var guideWorkspace = Descendants(guide.Content).OfType<WindowWorkspace>().Single();
            Check("guide opens inside settings navigation", ClassroomWindowRegression.CheckNativeFrame(guide, extended: true)
                && guideWorkspace.SelectedPage == "操作指南");
            guide.SelectCategory("快捷键");
            Check("guide supports nested navigation and back", guideWorkspace.CanGoBack && guideWorkspace.GoBack()
                && guideWorkspace.SelectedPage == "操作指南");
            await BoardWindowRegression.SaveVisual((FrameworkElement)guide.Content, Path.Combine(directory, "guide.png"));
            guide.Close();
            await ClassroomWindowRegression.Run(directory);

            var controller = services.OverlayInk;
            shell.ToggleClickThrough();
            controller.Tool = InkTool.Select;
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4 });
            stroke.AddPoint(150, 160); stroke.AddPoint(300, 220); stroke.Complete();
            controller.Document.Add(stroke);
            controller.SelectAll();
            await Task.Delay(180);
            var toolbar = shell.OpenWindows.OfType<SelectionToolbarWindow>().Single();
            Check("desktop selection exposes a compact nonactivating menu", toolbar.IsShown
                && NativeMethods.HasExStyle(WindowChrome.GetHwnd(toolbar), NativeMethods.WS_EX_NOACTIVATE)
                && toolbar.AppWindow.ClientSize.Width < 450);
            CheckPlacement("desktop menu initial placement");
            Invoke(FindButton(toolbar.Content, "克隆"));
            Check("desktop clone mode toggles", controller.CloneMode);
            Invoke(FindButton(toolbar.Content, "样式")); await Task.Delay(50);
            Invoke(FindButton(toolbar.Content, "加粗"));
            Check("selection style changes with undo", stroke.Attributes.BaseWidth == 5);
            controller.Undo();
            Check("selection width undo restores original", stroke.Attributes.BaseWidth == 4);
            await BoardWindowRegression.SaveVisual((FrameworkElement)toolbar.Content, Path.Combine(directory, "selection-style.png"));
            CheckPlacement("expanded menu avoids main bar");
            Invoke(FindButton(toolbar.Content, "变换")); await Task.Delay(50);
            Invoke(FindButton(toolbar.Content, "45°"));
            Check("selection transform applies", Math.Abs(controller.SelectionRotationDegrees - 45) < .1);
            controller.Undo();
            Invoke(FindButton(toolbar.Content, "收起")); await Task.Delay(50);
            checks.Add(new { check = "selection toolbar folds", passed = toolbar.Toolbar.IsCollapsed && toolbar.AppWindow.ClientSize.Width < 100 * WindowChrome.GetDpiScale(toolbar),
                collapsed = toolbar.Toolbar.IsCollapsed, width = toolbar.AppWindow.ClientSize.Width, dpiScale = WindowChrome.GetDpiScale(toolbar) });
            Invoke(FindButton(toolbar.Content, "展开")); await Task.Delay(50);
            Check("selection toolbar unfolds", !toolbar.Toolbar.IsCollapsed && toolbar.AppWindow.ClientSize.Width > 200);
            var work = WindowChrome.GetWorkArea(toolbar);
            var overlay = shell.Overlay.Window;
            foreach (var point in new[] { new Vector2(work.X + 5, work.Y + 5), new Vector2(work.X + work.Width - 5, work.Y + work.Height - 5),
                new Vector2(bar.AppWindow.Position.X + bar.AppWindow.Size.Width / 2, bar.AppWindow.Position.Y) })
            {
                var center = controller.SelectionBounds.Center;
                var target = (point - new Vector2(overlay.X, overlay.Y)) / overlay.DpiScale;
                controller.TransformSelectionUndoable(Matrix3x2.CreateTranslation(target - center), "move selection", false);
                await Task.Delay(50);
                CheckPlacement("selection menu stays on screen at " + point);
            }
            await BoardWindowRegression.SaveVisual((FrameworkElement)toolbar.Content, Path.Combine(directory, "selection.png"));
            var beforePosition = toolbar.AppWindow.Position;
            for (int i = 0; i < 100; i++) toolbar.QueueUpdate();
            await Task.Delay(50);
            Check("unchanged selection keeps stable window position", toolbar.AppWindow.Position == beforePosition);
            shell.ToggleClickThrough(); await Task.Delay(50);
            Check("selection menu hides in mouse mode", !toolbar.IsShown);
            shell.ToggleClickThrough(); await Task.Delay(50);
            Check("selection menu returns with annotation", toolbar.IsShown);
            shell.ToggleBoard(); await Task.Delay(160);
            Check("desktop selection menu hides in board", !toolbar.IsShown);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            var boardStroke = stroke.Clone(preserveId: false);
            boardStroke.Transform(Matrix3x2.CreateTranslation(new Vector2(350, 250) - boardStroke.Bounds.Center), false);
            board.CurrentController.Document.Add(boardStroke);
            board.CurrentController.Tool = InkTool.Select; board.CurrentController.SelectAll();
            await Task.Delay(80);
            var boardMenu = Descendants(board.Content).OfType<SelectionToolbar>().Single();
            Check("board shares compact selection toolbar", boardMenu.ActualWidth < 400);
            Invoke(FindButton(boardMenu, "收起")); await Task.Delay(40);
            Check("board selection toolbar folds", boardMenu.IsCollapsed);
            Invoke(FindButton(boardMenu, "展开"));
            await Task.Delay(80);
            var boardRoot = (FrameworkElement)board.Content;
            var menuBounds = boardMenu.TransformToVisual(boardRoot).TransformBounds(new Windows.Foundation.Rect(0, 0, boardMenu.ActualWidth, boardMenu.ActualHeight));
            Check("board menu remains near selected objects after unfolding", Math.Abs(menuBounds.X + menuBounds.Width / 2 - board.CurrentController.SelectionVisualBounds.Center.X) < 180
                && menuBounds.Bottom <= boardRoot.ActualHeight && menuBounds.Right <= boardRoot.ActualWidth);
            await BoardWindowRegression.SaveVisual((FrameworkElement)board.Content, Path.Combine(directory, "board-selection.png"));
            shell.OpenSettings();
            await Task.Delay(1100);
            var boardSettings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(boardSettings));
            await Task.Delay(100);
            var boardCaption = WindowChrome.GetScreenBounds(boardSettings, ((WindowFrame)boardSettings.Content).Header);
            var boardHit = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = boardCaption.X + 30, Y = boardCaption.Y + 24 }), 2);
            checks.Add(new { check = "normal settings receives input above whiteboard", passed = boardHit == WindowChrome.GetHwnd(boardSettings)
                && !NativeMethods.HasExStyle(WindowChrome.GetHwnd(boardSettings), NativeMethods.WS_EX_TOPMOST), hit = boardHit.ToInt64(),
                expected = WindowChrome.GetHwnd(boardSettings).ToInt64(), foreground = NativeMethods.GetForegroundWindow().ToInt64(),
                board = WindowChrome.GetHwnd(board).ToInt64(), boardTopmost = NativeMethods.HasExStyle(WindowChrome.GetHwnd(board), NativeMethods.WS_EX_TOPMOST) });
            boardSettings.Close();
            Check("closing settings restores whiteboard ordering", NativeMethods.HasExStyle(WindowChrome.GetHwnd(board), NativeMethods.WS_EX_TOPMOST));
            board.Close();

            void CheckPlacement(string name)
            {
                var position = toolbar.AppWindow.Position; var size = toolbar.AppWindow.Size;
                var area = WindowChrome.GetWorkArea(toolbar);
                var rect = new InkCanvas.Core.Ink.Rect(position.X, position.Y, position.X + size.Width, position.Y + size.Height);
                var primary = shell.DesktopToolbarBounds;
                Check(name, rect.Left >= area.X && rect.Top >= area.Y && rect.Right <= area.X + area.Width
                    && rect.Bottom <= area.Y + area.Height && !rect.Intersects(primary));
            }
        }
        catch (Exception ex) { checks.Add(new { check = "workspace regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            if (cursorAvailable) SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "workspace.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static Button FindButton(DependencyObject root, string name) => Descendants(root).OfType<Button>()
        .First(button => AutomationProperties.GetName(button) == name || button is TouchIconButton touch && touch.Label == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
