using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class InkWashRegression
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
        var window = new SettingsWindow(services, shell: shell);
        window.Activate();
        try
        {
            window.SelectCategory("画板选项");
            await Task.Delay(250);
            var sliders = Descendants(window.Content).OfType<Slider>().Where(slider => AutomationProperties.GetName(slider).StartsWith("水墨")).ToArray();
            Check("four water-ink editors expose requested defaults", sliders.Length == 4
                && sliders.Select(slider => slider.Value).SequenceEqual(new double[] { 30, 42, 44, 44 }));
            double[] changed = { 38, 65, 72, 60 };
            for (int i = 0; i < sliders.Length; i++) sliders[i].Value = changed[i];
            var saved = services.Store.Load().Canvas;
            Check("water-ink edits save and reach the drawing controller", saved.InkWashWidth == 38
                && saved.InkWash == new InkWashBrushOptions(.65f, 72, .60f)
                && services.OverlayInk.InkWashWidth == 38 && services.OverlayInk.CurrentAttributes.InkWash == saved.InkWash);
            var hits = window.SearchSettings("水墨");
            Check("water-ink options are searchable", sliders.All(slider => hits.Any(hit => hit.Title == AutomationProperties.GetName(slider))));
            window.NavigateToSetting(hits.First(hit => hit.Title == "水墨笔锋粗细（px）"));
            await Task.Delay(150);
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "settings.png"));

            var palette = new PenPalette { Controller = services.OverlayInk };
            var boardPalette = new BoardPenPalette(services.Board.Current, services.Settings, 1);
            Check("toolbar palettes contain no water-ink parameter editors", new DependencyObject[] { palette, boardPalette }
                .All(root => !Descendants(root).OfType<Slider>().Any(slider => AutomationProperties.GetName(slider).StartsWith("水墨"))));

            services.Settings.Canvas.InkStyle = OriginalMenuCatalog.ToPenStyle(TaperKind.InkWash);
            services.ApplySettings(); services.SaveSettings();
            double ordinaryWidth = services.Settings.Canvas.InkWidth;
            shell.OpenPenPalette(false);
            await Task.Delay(180);
            var desktopPalette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            var desktopWidth = Descendants(desktopPalette.Content).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "画笔粗细");
            Check("water-ink width slider uses independent pixel units", desktopWidth.Maximum == 120 && desktopWidth.Value == 38);
            SetWidth(desktopWidth, 64);
            Check("desktop water-ink width persists without overwriting ordinary pen width", services.Store.Load().Canvas.InkWashWidth == 64
                && services.Settings.Canvas.InkWidth == ordinaryWidth && services.OverlayInk.GetDrawingAttributes(false).BaseWidth == 64);
            DrawController(services.OverlayInk, 64, "desktop width reaches actual ink");
            sliders.Single(slider => AutomationProperties.GetName(slider) == "水墨笔锋粗细（px）").Value = 82;
            Check("settings width synchronizes an already-open desktop palette", desktopWidth.Value == 82 && services.OverlayInk.InkWashWidth == 82);
            await BoardWindowRegression.SaveVisual((FrameworkElement)desktopPalette.Content, Path.Combine(directory, "desktop-width.png"));
            var style = Descendants(desktopPalette.Content).OfType<PenStylePicker>().Single();
            ChooseStyle(style, TaperKind.None);
            Check("switching to ordinary pen restores its width scale", desktopWidth.Maximum == 30 && desktopWidth.Value == ordinaryWidth * 2);
            SetWidth(desktopWidth, 18);
            ChooseStyle(style, TaperKind.InkWash);
            Check("switching back retains independent water-ink width", desktopWidth.Maximum == 120 && desktopWidth.Value == 82
                && services.Settings.Canvas.InkWidth == 9 && services.OverlayInk.GetDrawingAttributes(false).BaseWidth == 82);
            desktopPalette.Close();
            shell.OpenPenPalette(false);
            await Task.Delay(180);
            desktopPalette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            desktopWidth = Descendants(desktopPalette.Content).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "画笔粗细");
            Check("reopening palette preserves the water-ink readout", desktopWidth.Value == 82);
            desktopPalette.Close();

            shell.ToggleBoard();
            await Task.Delay(180);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            board.ShowPenPalette();
            await Task.Delay(150);
            var activeBoardPalette = board.ToolPanels.SelectMany(panel => Descendants(panel.Content)).OfType<BoardPenPalette>().Single();
            var boardWidth = Descendants(activeBoardPalette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "画笔粗细");
            Check("whiteboard opens with the saved water-ink width", boardWidth.Maximum == 120 && boardWidth.Value == 82);
            SetWidth(boardWidth, 96);
            Check("whiteboard water-ink width persists and reaches its controller", services.Store.Load().Canvas.InkWashWidth == 96
                && board.CurrentController.GetDrawingAttributes(false).BaseWidth == 96 && services.Settings.Canvas.InkWidth == 9);
            await BoardWindowRegression.SaveVisual(activeBoardPalette, Path.Combine(directory, "board-width.png"));
            DrawController(board.CurrentController, 96, "whiteboard width reaches actual ink");
            services.Settings.Canvas.InkWashWidth = 54; services.ApplySettings(); services.SaveSettings();
            Check("external settings synchronize an open whiteboard palette", boardWidth.Value == 54);
            ChooseStyle(Descendants(activeBoardPalette).OfType<PenStylePicker>().Single(), TaperKind.None);
            Check("whiteboard ordinary pen restores its separate width", boardWidth.Maximum == 30 && boardWidth.Value == 18);
            ChooseStyle(Descendants(activeBoardPalette).OfType<PenStylePicker>().Single(), TaperKind.InkWash);
            Check("whiteboard switching styles restores water-ink pixels", boardWidth.Maximum == 120 && boardWidth.Value == 54);
            board.Close();
            services.Reload();
            Check("reloading configuration retains the water-ink width", services.OverlayInk.InkWashWidth == 54 && services.Settings.Canvas.InkWidth == 9);

            using (var samples = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 310, 96))
            using (var renderer = new StrokeRenderer())
            {
                using (var drawing = samples.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.White);
                    float[] widths = { 15, 30, 60, 90 };
                    for (int row = 0; row < widths.Length; row++)
                    {
                        for (int segment = 0; segment < 4; segment++)
                        {
                            var stroke = new InkStroke(new() { BaseWidth = widths[row], TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
                            for (int p = 0; p <= 60; p++) stroke.AddPoint(100 + segment * 185 + p, 40 + row * 72, .5f, p * 8);
                            stroke.Complete(); renderer.Draw(drawing, stroke, Matrix3x2.Identity);
                        }
                    }
                }
                await samples.SaveAsync(Path.Combine(directory, "short-writing-widths.png"), CanvasBitmapFileFormat.Png);
            }

            using var liveTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 360, 96);
            using var coldTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 360, 96);
            foreach (bool hardware in new[] { false, true })
            foreach (float width in new[] { 3f, 30, 90 })
            {
                var stroke = new InkStroke(new() { BaseWidth = width, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
                using var live = new StrokeRenderer();
                for (int i = 0; i < 240; i++)
                {
                    stroke.AddPoint(80 + i * 3, 170 + MathF.Sin(i * .055f) * 50, .35f + .3f * MathF.Sin(i * .03f), i * 4,
                        hardware, hardware ? 25 : 0, hardware ? -35 : 0);
                    using var drawing = liveTarget.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    live.Draw(drawing, stroke, Matrix3x2.Identity);
                }
                var before = liveTarget.GetPixelBytes();
                stroke.Complete();
                Draw(live, stroke, liveTarget);
                Check($"completion preserves every live pixel {hardware} {width}", before.SequenceEqual(liveTarget.GetPixelBytes()));
                using var cold = new StrokeRenderer();
                Draw(cold, stroke, coldTarget);
                var after = coldTarget.GetPixelBytes();
                Check($"water-ink renders visible pixels {hardware} {width}", Enumerable.Range(0, after.Length / 4).Any(p => after[p * 4 + 3] > 0));
                int maxError = before.Zip(after).Max(pair => Math.Abs(pair.First - pair.Second));
                Check($"cold geometry matches frozen chunks {hardware} {width}", maxError <= 1, new { maxError });
                long builds = live.GeometryBuildCount;
                for (int frame = 0; frame < 10; frame++)
                {
                    using var drawing = liveTarget.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    live.Draw(drawing, stroke, Matrix3x2.CreateTranslation(frame, 0));
                }
                Check($"panning reuses cached geometry {hardware} {width}", live.GeometryBuildCount == builds);
                stroke.Attributes.InkWash = new(.9f, 80, .8f);
                Draw(live, stroke, liveTarget);
                Check($"brush parameter changes invalidate cached geometry {hardware} {width}", live.GeometryBuildCount > builds);
                live.Retain(Array.Empty<InkStroke>());
                Check($"removed ink releases native geometry {hardware} {width}", live.CachedGeometryCount == 0);
                if (!hardware && width == 30) await coldTarget.SaveAsync(Path.Combine(directory, "water-ink.png"), CanvasBitmapFileFormat.Png);
            }

            using (var renderer = new StrokeRenderer())
            {
                var stroke = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
                var times = new List<double>();
                for (int i = 0; i < 2000; i++)
                {
                    stroke.AddPoint(60 + i * 2.5f, 160 + MathF.Sin(i * .025f) * 50, .5f, i * 4);
                    if (i < 2 || i % 8 != 0) continue;
                    var timer = Stopwatch.StartNew();
                    Draw(renderer, stroke, liveTarget);
                    times.Add(timer.Elapsed.TotalMilliseconds);
                }
                var ordered = times.Skip(4).Order().ToArray();
                Check("long strokes rebuild a bounded moving tail", renderer.SegmentBuildCount < stroke.Count * 20,
                    new { points = stroke.Count, renderer.SegmentBuildCount, frames = times.Count,
                        medianMilliseconds = ordered[ordered.Length / 2], p95Milliseconds = ordered[(int)(ordered.Length * .95)] });
                Draw(renderer, stroke, liveTarget);
                long builds = renderer.GeometryBuildCount;
                Draw(renderer, stroke, liveTarget);
                Check("unchanged live frame reuses native geometry", builds == renderer.GeometryBuildCount);
            }
        }
        catch (Exception ex) { checks.Add(new { check = "water-ink regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "ink-wash.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            shell.Dispose();
            Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
        void DrawController(InkController controller, float width, string name)
        {
            controller.Tool = InkTool.Pen; controller.RecognizeShapes = false;
            var first = new OverlayPointer(981, 100, 100, .5f, 0, 0, OverlayPointerKind.Mouse, 1000);
            controller.OnPointerDown(first);
            for (int sample = 1; sample <= 40; sample++) controller.OnPointerMove(first with { X = 100 + sample, TimeMs = (uint)(1000 + sample * 8) });
            controller.OnPointerUp(first with { X = 140, TimeMs = 1330 });
            var stroke = controller.Document.Strokes.Last();
            Check(name, stroke.Attributes.BaseWidth == width && stroke.Points.Max(point => point.Width) > width * .8f);
            controller.Undo();
        }
        static void SetWidth(Slider slider, double width) => ((IRangeValueProvider)new SliderAutomationPeer(slider).GetPattern(PatternInterface.RangeValue)).SetValue(width);
        static void ChooseStyle(PenStylePicker picker, TaperKind kind)
            => ((IToggleProvider)new ToggleButtonAutomationPeer(Descendants(picker).OfType<ToggleButton>()
                .ElementAt(OriginalMenuCatalog.ToPenStyle(kind))).GetPattern(PatternInterface.Toggle)).Toggle();
    }

    private static void Draw(StrokeRenderer renderer, InkStroke stroke, CanvasRenderTarget target)
    {
        using var drawing = target.CreateDrawingSession();
        drawing.Clear(Microsoft.UI.Colors.Transparent);
        renderer.Draw(drawing, stroke, Matrix3x2.Identity);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
