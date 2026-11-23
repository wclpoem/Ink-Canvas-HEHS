using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace InkCanvas.App.Diagnostics;

internal static class ImageImportRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start();
        bool cursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        CancellationTokenSource pickerCancellation = null;
        try
        {
            string sample = Path.Combine(directory, "sample.png");
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 320, 180, 96))
            {
                using (var draw = target.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Orange); draw.FillCircle(90, 90, 60, Microsoft.UI.Colors.RoyalBlue); }
                await target.SaveAsync(sample, CanvasBitmapFileFormat.Png);
            }
            await Task.Delay(150);
            var controller = services.OverlayInk;
            controller.RecognizeShapes = false; controller.Tool = InkTool.Pen;
            var toolbar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            Check("screen toolbar media entries are usable", Descendants(toolbar.Bar).OfType<TouchIconButton>()
                .Where(button => button.Glyph == Glyphs.InsertMedia).All(button => button.IsEnabled && button.Opacity == 1));
            foreach (bool mouseMode in new[] { true, false })
            {
                if (shell.Overlay.ClickThrough != mouseMode) shell.ToggleClickThrough();
                Invoke(Descendants(toolbar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "工具"));
                await Task.Delay(100);
                var insertion = await MoreMenuRegression.OpenInsertionMenu(toolbar.GetOpenPanel(BarPanelKind.More).Content);
                var image = Descendants(insertion).OfType<TouchIconButton>()
                    .Single(button => button.Label == "插入图片");
                Check("image insertion menu is enabled in mouse mode " + mouseMode,
                    image.IsEnabled && image.Opacity == 1 && new ButtonAutomationPeer(image).IsEnabled());
                if (mouseMode) { toolbar.ClosePanels(); continue; }
                Invoke(image);
                var menuDialog = await FindPickerAsync();
                Check("tools menu opens image picker and dismisses popup", !toolbar.Bar.IsExpanded);
                await ChooseFileAsync(menuDialog, sample);
                for (int i = 0; i < 500 && services.ScreenImages.CurrentImages.Count == 0; i++) await Task.Delay(20);
                Check("tools menu inserts and selects an image on the screen", services.ScreenImages.CurrentImages.Count == 1
                    && controller.SelectedElements.Count == 1 && controller.Tool == InkTool.Select && !shell.Overlay.ClickThrough);
                controller.Undo();
                Check("screen image menu insertion can be undone", services.ScreenImages.CurrentImages.Count == 0);
                controller.Redo();
                Check("screen image menu insertion can be restored", services.ScreenImages.CurrentImages.Count == 1);
                controller.Undo(); controller.History.Clear(); controller.ClearSelection(); controller.Tool = InkTool.Pen;
            }
            var watch = Stopwatch.StartNew();
            var pending = shell.InsertImageAsync();
            Check("opening image picker returns immediately to the dispatcher", watch.ElapsedMilliseconds < 150);
            Check("image picker loading preserves writing mode", !shell.Overlay.ClickThrough && controller.Tool == InkTool.Pen);
            var dialog = await FindPickerAsync();
            Check("desktop overlay remains enabled while picker is open", IsWindowEnabled(shell.Overlay.Window.Handle) && !shell.Overlay.ClickThrough);
            int before = controller.Document.Count;
            bool native = await NativeStrokeAsync(shell.Overlay.Window.X + 45, shell.Overlay.Window.Y + 150, shell.Overlay.Window.Handle, checks);
            checks.Add(new { check = "native screen input commits while image picker is open", available = native,
                passed = native ? (bool?)(controller.Document.Count > before) : null, shell.Overlay.Window.PointerMessageCount, shell.Overlay.Window.MouseMessageCount });
            DrawStroke(controller);
            Check("screen controller accepts ink during file selection", controller.Document.Count > before);
            shell.ToggleClickThrough();
            PostMessageW(dialog, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check("cancel picker preserves the user's latest mouse mode", shell.Overlay.ClickThrough && services.ScreenImages.CurrentImages.Count == 0);

            shell.ToggleBoard(); await Task.Delay(150);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            controller = board.CurrentController;
            controller.RecognizeShapes = false; controller.Tool = InkTool.Pen;
            pending = board.InsertImageAsync();
            await board.InsertImageAsync();
            dialog = await FindPickerAsync();
            Check("whiteboard remains enabled while file picker is open", IsWindowEnabled(WindowChrome.GetHwnd(board)) && controller.Tool == InkTool.Pen);
            before = controller.Document.Count;
            var work = WindowChrome.GetWorkArea(board);
            native = await NativeStrokeAsync(work.X + 45, work.Y + 150, WindowChrome.GetHwnd(board), checks);
            checks.Add(new { check = "native whiteboard input commits while image picker is open", available = native,
                passed = native ? (bool?)(controller.Document.Count > before) : null });
            DrawStroke(controller);
            Check("board controller accepts ink during file selection", controller.Document.Count > before);
            controller.ViewTransform = Matrix3x2.CreateScale(1.2f) * Matrix3x2.CreateTranslation(60, -20);
            controller.NotifyElementsChanged();
            var pointer = new OverlayPointer(998, 100, 110, .5f, 0, 0, OverlayPointerKind.Pen, 500);
            controller.OnPointerDown(pointer); controller.OnPointerMove(pointer with { X = 160, Y = 140, TimeMs = 530 });
            await ChooseFileAsync(dialog, sample);
            for (int i = 0; i < 500 && FindPicker() != IntPtr.Zero; i++) await Task.Delay(20);
            Check("native picker accepts selected file", FindPicker() == IntPtr.Zero);
            Check("completed image load waits for the current stroke", !pending.IsCompleted && controller.Tool == InkTool.Pen
                && controller.HasActiveInteraction && services.Board.CurrentImages.Count == 0);
            controller.OnPointerUp(pointer with { X = 180, Y = 150, TimeMs = 550 });
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check("image inserts after lift without losing ink", services.Board.CurrentImages.Count == 1 && controller.Document.Count > before
                && controller.Tool == InkTool.Select && controller.SelectedElements.Count == 1);
            var surface = Descendants(board.Content).OfType<Controls.Controls.InkSurface>().Single();
            Check("image uses the current viewport after panning during selection", Vector2.Distance(controller.SelectionFrame.Center(controller.ViewTransform),
                new((float)surface.ActualWidth / 2, (float)surface.ActualHeight / 2)) < .01f);

            pending = board.InsertImageAsync();
            await FindPickerAsync();
            services.Board.InsertPageAfterCurrent();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check("changing board page cancels pending image without inserting on another page", services.Board.CurrentImages.Count == 0);
            pending = board.InsertImageAsync();
            await FindPickerAsync();
            board.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check("closing board closes its pending image picker", FindPicker() == IntPtr.Zero);

            shell.Overlay.SetInteractionMode(false, true);
            pending = shell.InsertImageAsync(); await FindPickerAsync();
            shell.ToggleBoard(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check("changing canvas mode cancels the old picker", FindPicker() == IntPtr.Zero && services.ScreenImages.CurrentImages.Count == 0);
            shell.ToggleBoard();

            pickerCancellation = new CancellationTokenSource();
            var picker = ImageFilePicker.PickAsync(new(0, 0, 1920, 1032), pickerCancellation.Token);
            await FindPickerAsync(); pickerCancellation.Cancel();
            bool cancelled = false;
            try { await picker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { cancelled = true; }
            Check("picker cancellation releases native modal thread", cancelled && FindPicker() == IntPtr.Zero);

            string large = Path.Combine(directory, "large-photo.jpg");
            await Task.Run(async () =>
            {
                var pixels = new byte[6000 * 4000 * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)(i / 7); pixels[i + 1] = (byte)(i / 19); pixels[i + 2] = (byte)(i / 43); pixels[i + 3] = 255; }
                using var output = File.Create(large).AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 6000, 4000, 96, 96, pixels);
                await encoder.FlushAsync();
            });
            int ticks = 0; double gap = 0;
            var clock = Stopwatch.StartNew(); double last = 0;
            var heartbeat = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            heartbeat.Interval = TimeSpan.FromMilliseconds(16);
            heartbeat.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; gap = Math.Max(gap, now - last); last = now; ticks++; };
            heartbeat.Start();
            var loadTimer = Stopwatch.StartNew();
            var asset = await ImageImport.LoadAsync(await StorageFile.GetFileFromPathAsync(large), true, new(1000, 700), Matrix3x2.Identity);
            checks.Add(new { check = "large photo import remains responsive", passed = ticks > 0 && gap < 500,
                milliseconds = loadTimer.ElapsedMilliseconds, uiTicks = ticks, longestGapMilliseconds = gap, outputBytes = asset.Data.Length });
            using var imageStream = new MemoryStream(asset.Data, false).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(imageStream);
            Check("compressed photos retain JPEG and fit the configured size", asset.ContentType == "image/jpeg" && decoder.PixelWidth <= 1920 && decoder.PixelHeight <= 1080);
            using var scene = new ImageSceneRenderer();
            using var testController = new InkController();
            var images = new BoardService(testController); images.AddImage(asset);
            int uiThread = Environment.CurrentManagedThreadId, changedThread = 0;
            scene.Changed += () => changedThread = Environment.CurrentManagedThreadId;
            await scene.PrepareAsync(CanvasDevice.GetSharedDevice(), testController);
            Check("background decode publishes on renderer's UI thread", scene.CachedBitmapCount == 1 && changedThread == uiThread);
            heartbeat.Stop();
            using var canceledLoad = new CancellationTokenSource(); canceledLoad.Cancel();
            bool loadCancelled = false;
            try { await ImageImport.LoadAsync(await StorageFile.GetFileFromPathAsync(large), true, new(1000, 700), Matrix3x2.Identity, canceledLoad.Token); }
            catch (OperationCanceledException) { loadCancelled = true; }
            Check("cancelled image decoding does not commit an asset", loadCancelled);
        }
        catch (Exception ex) { checks.Add(new { check = "image import regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            pickerCancellation?.Cancel(); pickerCancellation?.Dispose();
            var dialog = FindPicker();
            if (dialog != IntPtr.Zero) PostMessageW(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            if (cursorAvailable) SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var window in shell.OpenWindows.Reverse().ToArray()) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "image-import.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    internal static async Task<IntPtr> FindPickerAsync(string title = "插入图片")
    {
        for (int i = 0; i < 500; i++)
        {
            var window = FindPicker(title);
            if (window != IntPtr.Zero) return window;
            await Task.Delay(20);
        }
        throw new TimeoutException("Image picker did not open.");
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static void DrawStroke(InkController controller)
    {
        var pointer = new OverlayPointer(997, 55, 150, .5f, 0, 0, OverlayPointerKind.Pen, 100);
        controller.OnPointerDown(pointer);
        controller.OnPointerMove(pointer with { X = 110, Y = 175, TimeMs = 120 });
        controller.OnPointerUp(pointer with { X = 130, Y = 180, TimeMs = 140 });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static IntPtr FindPicker(string expectedTitle = "插入图片")
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            if (process != Environment.ProcessId || !NativeMethods.IsWindowVisible(window)) return true;
            var title = new StringBuilder(100); GetWindowTextW(window, title, title.Capacity);
            if (title.ToString() != expectedTitle) return true;
            found = window; return false;
        }, IntPtr.Zero);
        return found;
    }

    internal static async Task ChooseFileAsync(IntPtr dialog, string path)
    {
        IntPtr edit = IntPtr.Zero;
        var controls = new List<string>();
        EnumChildWindows(dialog, (child, _) =>
        {
            var name = new StringBuilder(100); GetClassNameW(child, name, name.Capacity);
            var text = new StringBuilder(200); GetWindowTextW(child, text, text.Capacity);
            int parentId = GetDlgCtrlID(GetParent(child));
            controls.Add(name + " " + GetDlgCtrlID(child) + " parent=" + parentId + " " + text);
            if (name.ToString() == "Edit" && (parentId == 1148 || GetDlgCtrlID(child) == 1148)) edit = child;
            return true;
        }, IntPtr.Zero);
        await File.WriteAllLinesAsync(Path.Combine(Path.GetDirectoryName(path), "picker-controls.txt"), controls);
        if (edit == IntPtr.Zero) throw new InvalidOperationException("File name editor missing. " + string.Join(", ", controls));
        await Task.Run(() => SetWindowTextW(edit, path));
        PostMessageW(dialog, 0x0111 /* WM_COMMAND */, new IntPtr(1 /* IDOK */), IntPtr.Zero);
    }

    private static async Task<bool> NativeStrokeAsync(int x, int y, IntPtr expected, List<object> checks)
    {
        if (!SetCursorPos(x, y)) { checks.Add(new { check = "native cursor injection unavailable", reason = "SetCursorPos returned false" }); return false; }
        await Task.Delay(30);
        var actual = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = x, Y = y }), 2);
        if (actual != expected)
        {
            var title = new StringBuilder(100); GetWindowTextW(actual, title, title.Capacity);
            checks.Add(new { check = "native hit test unavailable", expected = expected.ToInt64(), actual = actual.ToInt64(), title = title.ToString(), x, y });
            return false;
        }
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(30);
        SetCursorPos(x + 70, y + 25); await Task.Delay(30);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
        return true;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr window, string text);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
