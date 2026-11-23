using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SettingsFeatureRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(directory, "saved");
        Directory.CreateDirectory(services.Settings.Automation.AutoSavedStrokesLocation);
        using var shell = new ShellController(services);
        shell.Start();
        try
        {
            shell.OpenSettings(); await Task.Delay(200);
            var settingsWindow = shell.OpenWindows.OfType<SettingsWindow>().Single();
            var formerlyDeferred = new[]
            {
                ("书写与擦除", "显示画笔光标"), ("触控与手势", "自动开关两指缩放功能"), ("演示与课件", "允许幻灯片模式下的双指手势"),
                ("触控与手势", "触摸大小倍数校准"), ("外观与工具栏", "在调色盘窗口中显示 启用笔尖模式 切换按钮"), ("常规", "关闭软件时二次弹窗确认"),
                ("外观与工具栏", "自动收纳忽略桌面EN批注窗口"), ("外观与工具栏", "进入“希沃轻白板”时自动收纳"), ("外观与工具栏", "进入“鸿合视频展台”时自动收纳"),
                ("外观与工具栏", "进入“文香白板”时自动收纳"), ("外观与工具栏", "进入“中原旧白板”时自动收纳"), ("常规", "自动查杀希沃“PPT 小工具”"),
                ("常规", "自动查杀 希沃白板5"), ("画布与识别", "切换到鼠标模式后自动清屏"), ("保存与备份", "定期自动删除超过保存时间的墨迹、截图文件"),
                ("保存与备份", "保存时间设置（Days）"), ("触控与手势", "特殊屏幕触摸大小倍数"),
            };
            foreach (var (category, title) in formerlyDeferred)
            {
                settingsWindow.SelectCategory(category); ((FrameworkElement)settingsWindow.Content).UpdateLayout();
                var editor = Descendants(settingsWindow.Content).OfType<Control>().FirstOrDefault(control => AutomationProperties.GetName(control) == title);
                Check("classroom setting has an editor or a dependency reason " + title, editor is not null
                    && (editor.IsEnabled || !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(editor))));
            }
            settingsWindow.Close(); await Task.Delay(100);
            services.Settings.Canvas.IsShowCursor = true;
            services.ApplySettings();
            var ink = services.OverlayInk;
            ink.Tool = InkTool.Pen;
            ink.OnPointerHover(new(8761, 240, 240, .5f, 1, 1, OverlayPointerKind.Mouse, 0));
            Check("pen cursor setting displays without creating ink", ink.IsPenCursorVisible && ink.Document.Count == 0);
            services.Settings.Canvas.IsShowCursor = false; services.ApplySettings();
            Check("disabling cursor clears its preview", !ink.IsPenCursorVisible);

            foreach (bool visible in new[] { false, true })
            {
                services.Settings.Appearance.IsEnableDisPlayNibModeToggler = visible;
                shell.OpenPenPalette(false); await Task.Delay(100);
                var palette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                var toggle = Descendants(palette.Content).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetName(item) == "启用笔尖模式");
                Check("palette nib mode visibility " + visible, toggle.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed));
                if (visible)
                {
                    toggle.IsOn = true;
                    Check("palette nib mode applies and persists", services.Settings.Startup.IsEnableNibMode && ink.Palm.NibMode
                        && services.Store.Load().Startup.IsEnableNibMode);
                    palette.Detach();
                    services.Settings.Appearance.IsEnableDisPlayNibModeToggler = false; services.ApplySettings();
                    Check("detached palette immediately follows nib switch visibility", toggle.Visibility == Visibility.Collapsed);
                    services.Settings.Appearance.IsEnableDisPlayNibModeToggler = true;
                    services.Settings.Startup.IsEnableNibMode = false; services.ApplySettings();
                    Check("live nib synchronization keeps the existing control without recursive edits", toggle.Visibility == Visibility.Visible
                        && !toggle.IsOn && ReferenceEquals(toggle, Descendants(palette.Content).OfType<ToggleSwitch>()
                            .Single(item => AutomationProperties.GetName(item) == "启用笔尖模式")) && !ink.Palm.NibMode);
                }
                palette.Close();
            }
            var stroke = new InkStroke(new StrokeAttributes()); stroke.AddPoint(100, 100); stroke.AddPoint(200, 140); stroke.Complete();
            ink.Document.Add(stroke);
            var points = stroke.Points.ToArray();
            services.Settings.Automation.IsAutoClearWhenExitingWritingMode = true;
            shell.ToggleClickThrough();
            Check("mouse mode clears the current annotation", ink.Document.Count == 0 && ink.History.CanUndo);
            shell.ToggleClickThrough(); ink.Undo();
            Check("mouse mode clear restores through undo", ink.Document.Strokes.Single().Points.SequenceEqual(points));
            services.Settings.Automation.MinimumAutomationStrokeNumber = 0;
            services.Settings.Automation.IsAutoSaveStrokesAtClear = true;
            shell.ClearCanvas();
            await services.AutoSave.WaitForPendingAsync();
            Check("clear workflow saves configured ink before clearing", ink.Document.Count == 0
                && Directory.GetFiles(services.Settings.Automation.AutoSavedStrokesLocation, "*.icink", SearchOption.AllDirectories).Length > 0);
            ink.Undo();
            Check("clear workflow preserves undo", ink.Document.Strokes.Single().Points.SequenceEqual(points));
            services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;

            services.Settings.Gesture.IsEnableMultiTouchMode = true;
            services.Settings.Gesture.IsEnableTwoFingerTranslate = false;
            services.Settings.Gesture.AutoSwitchTwoFingerGesture = true;
            shell.ToggleBoard(); await Task.Delay(120);
            Check("board automatically enables gesture mode", !services.Board.Current.MultiTouchEnabled && services.Settings.Gesture.IsEnableMultiTouchMode);
            var gestures = new BoardGesturePanel(services.Settings.Gesture, 1, true);
            var gestureWindow = new Window { Content = gestures };
            WindowChrome.MakeBorderless(gestureWindow); WindowChrome.ShowWithoutActivation(gestureWindow);
            await Task.Delay(80);
            var multi = Descendants(gestures).OfType<ToggleSwitch>().Single(toggle => AutomationProperties.GetName(toggle) == "多指书写");
            var translate = Descendants(gestures).OfType<ToggleSwitch>().Single(toggle => AutomationProperties.GetName(toggle) == "双指移动");
            Check("automatic board gesture controls show effective state and reason", !multi.IsOn && !multi.IsEnabled
                && translate.IsOn && !translate.IsEnabled && !string.IsNullOrEmpty(AutomationProperties.GetHelpText(multi)));
            gestureWindow.Close();
            shell.ToggleBoard(); await Task.Delay(100);
            Check("leaving board restores configured gesture mode", services.OverlayInk.MultiTouchEnabled && services.Settings.Gesture.IsEnableMultiTouchMode);

            services.Settings.Gesture.IsEnableMultiTouchMode = false;
            services.Settings.Gesture.IsEnableTwoFingerTranslate = true;
            services.PresentationActive = true;
            services.Settings.PowerPoint.IsEnableTwoFingerGestureInPresentationMode = false;
            services.ApplySettings();
            var originalView = ink.ViewTransform;
            Gesture(ink);
            Check("presentation gesture switch blocks translation", ink.ViewTransform == originalView);
            services.Settings.PowerPoint.IsEnableTwoFingerGestureInPresentationMode = true;
            services.ApplySettings(); Gesture(ink);
            Check("presentation gesture switch allows translation", ink.ViewTransform != originalView);
            services.PresentationActive = false;

            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            services.Settings.Appearance.IsShowLRSwitchButton = true; bar.Bar.RefreshSettingsVisuals();
            var position = bar.AppWindow.Position;
            var side = Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "左右切换");
            Invoke(side); await Task.Delay(50);
            Check("toolbar side switch moves in current monitor", bar.AppWindow.Position.X != position.X);

            var oldBoard = new Window { Title = "InstantAnnotationWindow", Content = new TextBlock { Text = "旧白板收纳检测" } };
            WindowChrome.MakeStandard(oldBoard, 420, 240, topmost: false);
            oldBoard.Activate();
            services.Settings.Automation.IsAutoFoldInOldZyBoard = true;
            for (int index = 0; index < 25 && !bar.IsFolded; index++) await Task.Delay(100);
            Check("old whiteboard visible window triggers normal automatic folding", bar.IsFolded);
            oldBoard.Close(); services.Settings.Automation.IsAutoFoldInOldZyBoard = false;
            for (int index = 0; index < 25 && bar.IsFolded; index++) await Task.Delay(100);
            Check("leaving old whiteboard restores automatically folded toolbar", !bar.IsFolded);

            string old = Path.Combine(services.Settings.Automation.AutoSavedStrokesLocation, "old.icink");
            File.WriteAllText(old, "fixture"); File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
            services.Settings.Automation.AutoDelSavedFiles = true;
            for (int i = 0; i < 30 && File.Exists(old); i++) await Task.Delay(100);
            Check("periodic cleanup runs from the normal shell loop", !File.Exists(old));
            services.Settings.Automation.AutoDelSavedFiles = false;

            services.Settings.Advanced.IsSecondConfimeWhenShutdownApp = true;
            shell.Exit(); await Task.Delay(100);
            // The confirmation uses an independent window so it also works with a folded toolbar.
            var confirmation = FindWindowW(null, "退出 Ink Canvas");
            InkCanvas.Platform.Interop.NativeMethods.GetWindowThreadProcessId(confirmation, out uint process);
            bool ownedPrompt = confirmation != IntPtr.Zero && process == Environment.ProcessId;
            Check("exit confirmation opens before teardown", ownedPrompt);
            if (ownedPrompt)
            {
                SendMessageW(confirmation, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(80);
                Check("canceling exit keeps annotation services alive", shell.Overlay.Window.IsVisible);
            }
            shell.Restart(); await Task.Delay(100);
            var restart = FindWindowW(null, "重启 Ink Canvas");
            InkCanvas.Platform.Interop.NativeMethods.GetWindowThreadProcessId(restart, out uint restartProcess);
            Check("restart confirms before launching another process", restart != IntPtr.Zero && restartProcess == Environment.ProcessId);
            if (restart != IntPtr.Zero && restartProcess == Environment.ProcessId)
                SendMessageW(restart, 0x0010, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(70);
        }
        catch (Exception exception) { checks.Add(new { check = "settings feature exception", passed = false, error = exception.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "settings-features.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string title);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static void Gesture(InkController ink)
    {
        var first = new OverlayPointer(7201, 300, 300, .5f, 1, 1, OverlayPointerKind.Touch, 100);
        var second = first with { Id = 7202, X = 400 };
        ink.OnPointerDown(first); ink.OnPointerDown(second);
        ink.OnPointerMove(first with { X = 350, Y = 320, TimeMs = 130 });
        ink.OnPointerMove(second with { X = 450, Y = 320, TimeMs = 130 });
        ink.OnPointerUp(first with { X = 350, Y = 320, TimeMs = 150 });
        ink.OnPointerUp(second with { X = 450, Y = 320, TimeMs = 150 });
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
