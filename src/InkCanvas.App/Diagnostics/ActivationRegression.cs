using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal sealed class ActivationHost
{
    private readonly Window _window;
    private readonly DispatcherQueueTimer _timer;
    private int _deactivations, _frames;
    private bool _active;
    private bool _foregroundGranted;
    public ActivationHost(string statusFile)
    {
        uint.TryParse(Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--foreground-client="))?["--foreground-client=".Length..], out uint foregroundClient);
        _window = new Window { Title = "InkCanvas focus regression host", Content = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateBlue),
            Children = { new TextBlock { Text = "前台播放状态检查", Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(24) } },
        } };
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(60, 60, 420, 220));
        _window.Activated += (_, args) =>
        {
            bool active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (_active && !active) _deactivations++;
            _active = active;
            if (active && foregroundClient != 0) _foregroundGranted = AllowSetForegroundWindow(foregroundClient);
        };
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) =>
        {
            if (_active) _frames++;
            File.WriteAllText(statusFile, JsonSerializer.Serialize(new { hwnd = WindowChrome.GetHwnd(_window).ToInt64(), deactivations = _deactivations, frames = _frames, active = _active, foregroundGranted = _foregroundGranted }));
        };
        _window.Closed += (_, _) => { _timer.Stop(); Application.Current.Exit(); };
        _window.Activate(); _timer.Start();
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}

internal static class ActivationRegression
{
    private sealed record HostState(long Hwnd, int Deactivations, int Frames, bool Active);
    public static async Task Run(ShellController shell, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        string statusFile = Path.Combine(directory, "foreground-host.json");
        File.Delete(statusFile);
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--activationhost=" + statusFile);
        using var host = Process.Start(start);
        HostState baseline = null;
        var created = new List<Window>();
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
        bool originalClickThrough = shell.Overlay.ClickThrough;
        double originalWidth = shell.Services.Settings.Canvas.InkWidth;
        var originalNib = shell.Services.Settings.Canvas.PenNib;
        try
        {
            for (int i = 0; i < 50 && baseline is null; i++)
            { await Task.Delay(100); baseline = Read(statusFile); }
            if (baseline is null) throw new InvalidOperationException("Foreground host did not start.");
            var hostHwnd = new IntPtr(baseline.Hwnd);
            NativeMethods.SetForegroundWindow(hostHwnd); await Task.Delay(180);
            baseline = Read(statusFile);
            bool available = NativeMethods.GetForegroundWindow() == hostHwnd;
            checks.Add(new { check = "independent foreground host available", available });
            CheckMessages(shell.Overlay.Window.Handle, "ink overlay", checks);
            CheckMessages(WindowChrome.GetHwnd(bar), "floating toolbar", checks);
            await Check("show toolbar", () => WindowChrome.ShowWithoutActivation(bar));
            shell.StartInClickThrough();
            var pen = Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "批注");
            await Check("pen button and palette", () => Invoke(pen));
            var palette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            CheckMessages(WindowChrome.GetHwnd(palette), "pen palette", checks);
            var width = Descendants(palette.Content).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider).StartsWith("画笔粗细"));
            await Check("change pen width", () => width.Value = width.Value < 20 ? 20 : 10);
            var nib = Descendants(palette.Content).OfType<SegmentedSelector>().Single(combo => AutomationProperties.GetName(combo) == "笔尖");
            var nibButton = Descendants(nib).OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Last();
            await Check("choose nib", () => ((IToggleProvider)new ToggleButtonAutomationPeer(nibButton).GetPattern(PatternInterface.Toggle)).Toggle());
            await Task.Delay(650);
            bool paletteStays = palette.AppWindow?.IsVisible == true;
            checks.Add(new { check = "inactive palette remains open over foreground host", passed = paletteStays });
            if (paletteStays) palette.Close();
            await Check("eraser submenu", () => Invoke(Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "橡皮")));
            var menu = bar.GetOpenPanel(BarPanelKind.Eraser);
            CheckMessages(WindowChrome.GetHwnd(menu), "eraser submenu", checks);
            await Task.Delay(650);
            bool menuStays = bar.GetOpenPanel(BarPanelKind.Eraser) == menu;
            checks.Add(new { check = "inactive eraser submenu remains open", passed = menuStays });
            if (menuStays) await Check("pick lasso eraser", () => Invoke(Descendants(menu.Content).OfType<TouchIconButton>().Single(button => button.Label == "套索擦")));
            await Check("shape picker", shell.OpenShapePicker);
            var shapes = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
            CheckMessages(WindowChrome.GetHwnd(shapes), "shape picker", checks);
            await Check("shape pick", () => Descendants(shapes.Content).OfType<ShapePreview>().Single().SelectItem(0));
            if (shell.OpenWindows.Contains(shapes)) shapes.Close();
            await Check("fold toolbar", () => bar.SetFolded(true));
            foreach (var handle in bar.FoldHandles) CheckMessages(WindowChrome.GetHwnd(handle), "folded handle", checks);
            await Check("restore toolbar", () => bar.SetFolded(false));
            foreach (var placement in Enum.GetValues<PptNavigationPlacement>())
            {
                var navigation = new PptNavigationWindow(placement, shell, 1); created.Add(navigation);
                await Check("show PPT navigation " + placement, () => navigation.Update(new PptSnapshot(), true, true));
                checks.Add(new { check = "PPT navigation begins at full window opacity " + placement, passed = navigation.WindowOpacity == 1 });
                navigation.RecordInteraction();
                checks.Add(new { check = "PPT navigation returns to full opacity on interaction " + placement, passed = navigation.TargetOpacity == 1 });
                CheckMessages(WindowChrome.GetHwnd(navigation), "PPT navigation " + placement, checks);
            }
            if (shell.Overlay.ClickThrough) shell.ToggleClickThrough();
            shell.Services.OverlayInk.Tool = InkTool.Pen;
            await TryRealInk(shell, hostHwnd, statusFile, checks, available);
            var source = new InkStroke(new StrokeAttributes { BaseWidth = 3, TaperKind = TaperKind.None });
            source.AddPoint(30, 30); source.AddPoint(100, 100); source.Complete(); shell.Services.OverlayInk.Document.Add(source);
            await Check("floating replay controls", shell.OpenReplay);
            var replay = shell.OpenWindows.OfType<ReplayWindow>().Single();
            CheckMessages(WindowChrome.GetHwnd(replay), "replay controls", checks);
            await Check("pause replay", () => Invoke(Descendants(replay.Content).OfType<TouchIconButton>().Single(button => AutomationProperties.GetName(button) == "播放 / 暂停")));
            replay.Close();
            // A genuine text-entry window must still be able to take focus.
            var entry = new NamesInputWindow(1); created.Add(entry); entry.Activate(); await Task.Delay(150);
            bool entryActivates = NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(entry);
            bool ordinaryStyle = !NativeMethods.HasExStyle(WindowChrome.GetHwnd(entry), NativeMethods.WS_EX_NOACTIVATE);
            checks.Add(new { check = "text entry windows retain ordinary activation", available, ordinaryStyle, passed = ordinaryStyle && (!available || entryActivates), entryActivates });
            entry.Close(); created.Remove(entry);

            async Task Check(string action, Action invoke)
            {
                var before = Read(statusFile); invoke(); await Task.Delay(150);
                var after = Read(statusFile);
                bool foregroundUnchanged = NativeMethods.GetForegroundWindow() == hostHwnd;
                bool noDeactivation = before?.Deactivations == after?.Deactivations;
                bool stillAdvances = after?.Frames > before?.Frames;
                checks.Add(new { check = "annotation controls retain foreground playback", action, available,
                    foregroundUnchanged, noDeactivation, stillAdvances, passed = available ? (bool?)(foregroundUnchanged && noDeactivation && stillAdvances) : null });
            }
        }
        catch (Exception exception) { checks.Add(new { check = "activation regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            foreach (var window in created) window.Close();
            bar.ClosePanels();
            foreach (var popup in shell.OpenWindows.Where(window => window is PenPaletteWindow or ShapePickerWindow or ReplayWindow).ToArray()) popup.Close();
            if (shell.Overlay.ClickThrough != originalClickThrough) shell.ToggleClickThrough();
            shell.Services.Settings.Canvas.InkWidth = originalWidth;
            shell.Services.Settings.Canvas.PenNib = originalNib;
            shell.Services.ApplySettings(); shell.Services.SaveSettings();
            if (!host.HasExited) { host.CloseMainWindow(); await Task.Delay(150); }
            File.WriteAllText(Path.Combine(directory, "activation.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void CheckMessages(IntPtr window, string name, List<object> checks)
    {
        var handles = new List<IntPtr> { window };
        EnumChildWindows(window, (child, _) => { handles.Add(child); return true; }, IntPtr.Zero);
        var responses = handles.Select(handle => new
        {
            mouse = NativeMethods.SendMessageW(handle, NativeMethods.WM_MOUSEACTIVATE, window, new IntPtr(1 | NativeMethods.WM_LBUTTONDOWN << 16)).ToInt64(),
            pointer = NativeMethods.SendMessageW(handle, NativeMethods.WM_POINTERACTIVATE, new IntPtr(1 | 1 << 16), window).ToInt64(),
        }).ToArray();
        bool style = NativeMethods.HasExStyle(window, NativeMethods.WS_EX_NOACTIVATE);
        checks.Add(new { check = "mouse rejects window activation without swallowing input", window = name, style, responses,
            passed = style && responses.All(response => response.mouse == NativeMethods.MA_NOACTIVATE) });
        // WM_POINTERACTIVATE needs a valid pointer ID. Windows may discard a synthetic
        // message before calling the window procedure when that pointer does not exist.
        bool pointerAvailable = NativeMethods.GetPointerInfo(1, out var pointer)
            && pointer.pointerType is NativeMethods.POINTER_INPUT_TYPE_PEN or NativeMethods.POINTER_INPUT_TYPE_TOUCH;
        checks.Add(new { check = "pen and touch reject window activation", window = name, available = pointerAvailable,
            passed = pointerAvailable ? (bool?)(style && responses.All(response => response.pointer == NativeMethods.PA_NOACTIVATE)) : null });
    }
    private static async Task TryRealInk(ShellController shell, IntPtr foreground, string statusFile, List<object> checks, bool foregroundAvailable)
    {
        var point = new NativeMethods.POINT { X = 700, Y = 300 };
        bool available = SetCursorPos(point.X, point.Y) && GetAncestor(WindowFromPoint(point), 2) == shell.Overlay.Window.Handle;
        if (!available) { checks.Add(new { check = "native pointer writing keeps foreground", available = false }); return; }
        int count = shell.Services.OverlayInk.Document.Count;
        var before = Read(statusFile);
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        try
        {
            for (int i = 1; i <= 8; i++) { SetCursorPos(point.X + i * 10, point.Y + i * 3); await Task.Delay(15); }
        }
        finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(150);
        var after = Read(statusFile);
        bool stroke = shell.Services.OverlayInk.Document.Count == count + 1;
        bool retained = NativeMethods.GetForegroundWindow() == foreground && before.Deactivations == after.Deactivations;
        checks.Add(new { check = "native pointer writing keeps foreground", available = foregroundAvailable, inputAvailable = available,
            stroke, retained, passed = foregroundAvailable ? (bool?)(stroke && retained) : null });
    }
    private static HostState Read(string path)
    { try { return JsonSerializer.Deserialize<HostState>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { return null; } }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private delegate bool EnumWindowProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindowProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
