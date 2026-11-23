using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ShapeToolsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        NativeMethods.GetCursorPos(out var originalCursor);
        try
        {
            foreach (bool boardMode in new[] { false, true })
            {
                if (boardMode) { shell.ToggleBoard(); await Task.Delay(180); }
                shell.OpenShapePicker(); await Task.Delay(200);
                var window = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
                window.AutoHide = false;
                await Task.Delay(350);
                var preview = Descendants(window.Content).OfType<ShapePreview>().Single();
                var controller = boardMode ? services.Board.Current : services.OverlayInk;
                var catalog = ShapeMenuCatalog.Rows.SelectMany(row => row).ToArray();
                Check("shape categories cover every choice " + boardMode, catalog.All(item => item.LongPress)
                    && preview.ShapeCount == Enum.GetValues<ShapeKind>().Length && ShapeMenuCatalog.Categories.Length == 4
                    && catalog.Select(item => item.Kind).Distinct().Count() == catalog.Length
                    && ShapeMenuCatalog.Categories.All(category => category.Rows.SelectMany(row => row).Any()));
                Check("line and coordinate tools share one category " + boardMode,
                    ShapeMenuCatalog.Categories[0].Name == "线条与坐标" && ShapeMenuCatalog.Categories[0].Rows.Length == 3
                    && ShapeMenuCatalog.Categories[0].Rows.SelectMany(row => row).Count() == 11);
                preview.SelectCategoryFor(ShapeKind.CoordinateTwoPoint);
                Check("coordinate selection opens the merged category " + boardMode,
                    preview.VisibleCells.Count == 11 && preview.VisibleCells.Any(cell => ((ShapeMenuItem)cell.Tag).Kind == ShapeKind.Line));
                Check("only initial category is visible " + boardMode,
                    VisibleShapeCells(preview).SequenceEqual(preview.VisibleCells));
                Check("shape picker stays nonactivating " + boardMode, NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
                int rectangle = Array.FindIndex(catalog, item => item.Kind == ShapeKind.Rectangle);
                foreach (var kind in new[] { ShapeKind.Rectangle, ShapeKind.CoordinateTwoPoint, ShapeKind.HexagonalPrism })
                {
                    preview.SelectCategoryFor(kind);
                    await Task.Delay(180);
                    controller.Tool = InkTool.Pen;
                    var input = await Hold(window, preview.GetCell(kind), jitter: true, () =>
                        controller.Tool == InkTool.Shape && controller.ShapeKind == kind && controller.StickyShape
                        && AutomationProperties.GetItemStatus(preview.GetCell(kind)) == "连续绘制");
                    checks.Add(new { check = "native hold selects before release with contact wobble " + boardMode + " " + kind,
                        available = input.Passed.HasValue, input.Status, passed = input.Passed.HasValue ? (bool?)(input.Passed.Value
                            && controller.Tool == InkTool.Shape && controller.ShapeKind == kind && controller.StickyShape) : null });
                }
                foreach (var item in catalog)
                {
                    int index = Array.IndexOf(catalog, item);
                    preview.SelectItem(index, sticky: true);
                    Check("hold selection entry " + boardMode + " " + item.Kind, controller.ShapeKind == item.Kind && controller.StickyShape && controller.Tool == InkTool.Shape);
                    controller.Document.Clear(); controller.History.Clear();
                    for (int count = 0; count < 2; count++)
                    {
                        Drag(controller, new(100 + count * 300, 180), new(260 + count * 300, 60));
                        if (ShapeGenerator.IsTwoStepSolid(item.Kind)) Drag(controller, new(200, 300), new(250, 215));
                        else if (ShapeGenerator.IsHyperbola(item.Kind)) Drag(controller, new(200 + count * 300, 120), new(240 + count * 300, 120));
                    }
                    Check("continuous drawing keeps selected shape " + boardMode + " " + item.Kind,
                        controller.Tool == InkTool.Shape && controller.StickyShape && controller.History.UndoCount == 2
                        && controller.ShapePreview.Count == 0 && AutomationProperties.GetItemStatus(preview.GetCell(item.Kind)) == "连续绘制");
                }
                int coordinateIndex = Array.FindIndex(catalog, item => item.Kind == ShapeKind.CoordinateTwoPoint);
                preview.SelectItem(coordinateIndex, sticky: true);
                int coordinateStrokeCount = controller.Document.Count;
                Drag(controller, new(120, 180), new(290, 60));
                Check("two-point coordinates use pointer-down as origin " + boardMode,
                    controller.Document.Count == coordinateStrokeCount + 2
                    && controller.Document.Strokes[^2].Points[0].Position == new Vector2(120, 180)
                    && controller.Document.Strokes[^2].Points[1].Position == new Vector2(290, 180)
                    && controller.Document.Strokes[^1].Points[0].Position == new Vector2(120, 180)
                    && controller.Document.Strokes[^1].Points[1].Position == new Vector2(120, 60)
                    && controller.StickyShape && controller.Tool == InkTool.Shape);
                foreach (var kind in Enum.GetValues<ShapeKind>().Where(kind => kind is >= ShapeKind.Cube and <= ShapeKind.ConeFrustum))
                {
                    int index = Array.FindIndex(catalog, item => item.Kind == kind);
                    preview.SelectItem(index, sticky: true);
                    controller.Document.Clear(); controller.History.Clear();
                    for (int count = 0; count < 2; count++)
                    {
                        Drag(controller, new(100 + count * 300, 100), new(260 + count * 300, 230));
                        if (ShapeGenerator.IsTwoStepSolid(kind)) Drag(controller, new(200, 300), new(250, 215));
                    }
                    Check("continuous solid drawing " + boardMode + " " + kind, controller.Tool == InkTool.Shape
                        && controller.StickyShape && controller.History.UndoCount == 2 && controller.ShapePreview.Count == 0);
                    string path = Path.Combine(directory, $"{boardMode}-{kind}.icink");
                    InkFileStore.Save(path, controller.Document.Strokes);
                    var loaded = InkFileStore.Load(path);
                    Check("solid persistence " + boardMode + " " + kind, loaded.Count == controller.Document.Count
                        && loaded.Sum(stroke => stroke.Count) == controller.Document.Strokes.Sum(stroke => stroke.Count));
                }
                preview.SelectItem(rectangle);
                Check("tap clears continuous selection " + boardMode, !controller.StickyShape);
                Drag(controller, new(100, 100), new(260, 230));
                Check("tap draws once then returns to pen " + boardMode, controller.Tool == InkTool.Pen);
                foreach (double scale in new[] { 50d, 100d, 150d })
                {
                    window.ApplyScale(scale);
                    for (int category = 0; category < ShapeMenuCatalog.Categories.Length; category++)
                    {
                        preview.SelectCategory(category); await Task.Delay(50);
                        Check("only chosen category is visible " + boardMode + " " + scale + " " + category,
                            VisibleShapeCells(preview).SequenceEqual(preview.VisibleCells));
                        var root = (FrameworkElement)window.Content;
                        var cells = preview.VisibleCells;
                        Check("shape cells fit menu " + boardMode + " " + scale + " " + ShapeMenuCatalog.Categories[category].Name,
                            cells.Count > 0 && cells.All(cell =>
                            {
                                var bounds = cell.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
                                return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                            }));
                        if (scale == 100) await BoardWindowRegression.SaveVisual(preview,
                            Path.Combine(directory, $"shape-menu-{boardMode}-{category}.png"));
                    }
                }
                var categories = Descendants(preview).OfType<SegmentedSelector>().Single();
                var categoryButtons = Descendants(categories).OfType<ToggleButton>().ToArray();
                var categoryInput = await Click(window, categoryButtons[0]);
                checks.Add(new { check = "native category switch " + boardMode, available = categoryInput.Passed.HasValue, categoryInput.Status,
                    passed = categoryInput.Passed.HasValue ? (bool?)(categoryInput.Passed.Value && categoryButtons[0].IsChecked == true
                        && preview.VisibleCells.Count == ShapeMenuCatalog.Categories[0].Rows.Sum(row => row.Length)
                        && VisibleShapeCells(preview).SequenceEqual(preview.VisibleCells)) : null });
                await BoardWindowRegression.SaveVisual(preview, Path.Combine(directory, $"shape-menu-{boardMode}.png"));
                window.Close();
                if (boardMode) shell.ToggleBoard();
            }
            await SaveSolids(directory);
        }
        catch (Exception ex) { checks.Add(new { check = "shape tools exception", passed = false, error = ex.ToString() }); }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "shape-tools.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static async Task SaveSolids(string directory)
    {
        var kinds = Enum.GetValues<ShapeKind>().Where(kind => kind is >= ShapeKind.Cube and <= ShapeKind.ConeFrustum).ToArray();
        using var controller = new InkController();
        for (int i = 0; i < kinds.Length; i++)
        {
            var origin = new Vector2(50 + i % 4 * 300, 90 + i / 4 * 300);
            var attributes = new StrokeAttributes { BaseWidth = 2.5f, TaperKind = TaperKind.None };
            var strokes = ShapeGenerator.IsTwoStepSolid(kinds[i])
                ? ShapeGenerator.GenerateSolid(kinds[i], origin, origin + new Vector2(150, 125), kinds[i] == ShapeKind.SquarePyramid ? new(0, -145) : new(60, -50), attributes)
                : ShapeGenerator.Generate(kinds[i], origin, origin + new Vector2(200, 180), attributes);
            controller.Document.AddRange(strokes);
        }
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1250, 650, 96);
        using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.White);
            renderer.DrawCommitted(drawing, controller);
            for (int i = 0; i < kinds.Length; i++) drawing.DrawText(ShapeMenuCatalog.Rows.SelectMany(row => row).Single(item => item.Kind == kinds[i]).Label,
                50 + i % 4 * 300, 250 + i / 4 * 300, Microsoft.UI.Colors.Black);
        }
        await target.SaveAsync(Path.Combine(directory, "solid-shapes.png"), CanvasBitmapFileFormat.Png);
    }

    private static async Task<(bool? Passed, string Status)> Hold(Window window, FrameworkElement cell, bool jitter, Func<bool> selected)
    {
        var bounds = WindowChrome.GetScreenBounds(window, cell);
        int x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        if (!SetCursorPos(x, y)) return (null, $"cursor unavailable at {x},{y}, error {Marshal.GetLastWin32Error()}");
        await Task.Delay(120);
        var target = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = x, Y = y }), 2);
        if (target != WindowChrome.GetHwnd(window)) return (null,
            $"target {target} expected {WindowChrome.GetHwnd(window)}, cell {bounds}, window {window.AppWindow.Position} {window.AppWindow.Size}");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        bool result;
        try
        {
            await Task.Delay(250);
            if (jitter) SetCursorPos(x + 2, y + 1);
            await Task.Delay(350);
            result = selected();
        }
        finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(50);
        return (result, "input delivered");
    }

    private static async Task<(bool? Passed, string Status)> Click(Window window, FrameworkElement cell)
    {
        var bounds = WindowChrome.GetScreenBounds(window, cell);
        int x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        if (!SetCursorPos(x, y)) return (null, $"cursor unavailable at {x},{y}, error {Marshal.GetLastWin32Error()}");
        await Task.Delay(80);
        var target = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = x, Y = y }), 2);
        if (target != WindowChrome.GetHwnd(window)) return (null,
            $"target {target} expected {WindowChrome.GetHwnd(window)}, cell {bounds}, window {window.AppWindow.Position} {window.AppWindow.Size}");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(180);
        return (true, "input delivered");
    }

    private static IEnumerable<Grid> VisibleShapeCells(ShapePreview preview) => Descendants(preview).OfType<Grid>()
        .Where(cell => cell.Tag is ShapeMenuItem && IsVisible(cell));

    private static bool IsVisible(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility == Visibility.Collapsed) return false;
        return true;
    }

    private static OverlayPointer Pointer(Vector2 point) => new(812, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
    private static void Drag(InkController controller, Vector2 from, Vector2 to)
    {
        controller.OnPointerDown(Pointer(from)); controller.OnPointerMove(Pointer(to)); controller.OnPointerUp(Pointer(to));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
