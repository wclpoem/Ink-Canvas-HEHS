using System.Diagnostics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace InkCanvas.App.Diagnostics;

/// <summary>Measures real popup construction/loading and checks attached/detached placement.</summary>
internal static class PopupPerformanceRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var timings = new List<object>();
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            await Task.Delay(250);
            {
                var probeTimer = Stopwatch.StartNew();
                var probe = new Window { Content = new Microsoft.UI.Xaml.Controls.Border { Width = 248, Height = 400 } };
                timings.Add(new { kind = "profile-window-create", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
                probeTimer.Restart();
                WindowChrome.MakeBorderless(probe, noActivate: true);
                timings.Add(new { kind = "profile-borderless", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
                probeTimer.Restart();
                FloatingBarWindow.EnableTransparency(probe);
                timings.Add(new { kind = "profile-backdrop", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
                probeTimer.Restart();
                WindowChrome.ClipToSurfaces(probe, () => new[] { (Microsoft.UI.Xaml.Controls.Border)probe.Content });
                timings.Add(new { kind = "profile-clipping", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
                probeTimer.Restart();
                WindowChrome.PlaceDip(probe, 100, 100, 248, 400);
                timings.Add(new { kind = "profile-place", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
                probeTimer.Restart();
                probe.Close();
                timings.Add(new { kind = "profile-close", synchronousMs = probeTimer.Elapsed.TotalMilliseconds });
            }
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            for (int round = 0; round < 3; round++)
            {
                foreach (bool highlighter in new[] { false, true })
                    await Open(highlighter ? "highlighter" : "pen", round, () =>
                    {
                        shell.OpenPenPalette(highlighter);
                        return shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                    });
                await Open("shapes", round, () =>
                {
                    shell.OpenShapePicker();
                    return shell.OpenWindows.OfType<ShapePickerWindow>().Single();
                });
                foreach (var kind in new[] { BarPanelKind.More, BarPanelKind.Gesture, BarPanelKind.Eraser })
                    await Open(kind.ToString(), round, () =>
                    {
                        var panel = new BarPanelWindow(bar, bar.Bar, kind);
                        var position = bar.AppWindow.Position;
                        panel.ShowAboveBar(position.X, position.Y, bar.AppWindow.Size.Width);
                        return panel;
                    });
            }

            var preview = new PenStrokePreview();
            var attributes = new StrokeAttributes { BaseWidth = 8, TaperKind = TaperKind.Natural };
            preview.Update(attributes, false);
            var stroke = preview.Stroke;
            var previewTimer = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) preview.Update(attributes, false);
            timings.Add(new { kind = "unchanged-preview-30-updates", synchronousMs = previewTimer.Elapsed.TotalMilliseconds });
            checks.Add(new { check = "unchanged preview reuses its stroke geometry", passed = ReferenceEquals(stroke, preview.Stroke) });
            attributes.Color = InkColor.Opaque(220, 38, 38);
            preview.Update(attributes, false);
            checks.Add(new { check = "preview color changes reuse geometry", passed = ReferenceEquals(stroke, preview.Stroke)
                && preview.Stroke.Attributes.Color.PackedArgb == attributes.Color.PackedArgb });
            attributes.BaseWidth = 12;
            preview.Update(attributes, false);
            checks.Add(new { check = "preview width changes rebuild the stroke", passed = !ReferenceEquals(stroke, preview.Stroke)
                && preview.Stroke.Attributes.BaseWidth == 12 });

            foreach (bool highlighter in new[] { false, true })
            {
                shell.OpenPenPalette(highlighter);
                var palette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                await Follow(palette, () => palette.Detach(), () => palette.IsDetached, "palette " + highlighter);
            }
            shell.OpenShapePicker();
            var shapes = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
            await Follow(shapes, () => shapes.Detach(), () => shapes.IsDetached, "shapes");

            foreach (var kind in new[] { BarPanelKind.More, BarPanelKind.Gesture, BarPanelKind.Eraser })
            {
                var ownerPosition = bar.AppWindow.Position;
                var panel = new BarPanelWindow(bar, bar.Bar, kind);
                bool closed = false; panel.Closed += (_, _) => closed = true;
                panel.ShowAboveBar(ownerPosition.X, ownerPosition.Y, bar.AppWindow.Size.Width);
                await Task.Delay(100);
                var previous = panel.AppWindow.Position;
                bar.AppWindow.Move(new PointInt32(ownerPosition.X + 40, ownerPosition.Y - 25));
                await Task.Delay(100);
                checks.Add(new { check = "attached panel follows toolbar movement " + kind,
                    passed = !closed && previous != panel.AppWindow.Position });
                if (!closed)
                {
                    panel.Detach();
                    var detached = panel.AppWindow.Position;
                    bar.AppWindow.Move(ownerPosition);
                    await Task.Delay(100);
                    checks.Add(new { check = "detached panel keeps its position " + kind,
                        passed = !closed && detached == panel.AppWindow.Position });
                    panel.Close();
                }
                bar.AppWindow.Move(ownerPosition);
            }

            shell.ToggleBoard();
            await Task.Delay(200);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            board.ShowPenPalette();
            await Task.Delay(100);
            var boardPanel = board.ToolPanels.Single();
            var old = boardPanel.AppWindow.Position;
            var bounds = board.AppWindow.Position;
            var size = board.AppWindow.Size;
            board.AppWindow.MoveAndResize(new RectInt32(bounds.X + 80, bounds.Y + 80, size.Width - 160, size.Height - 160));
            await Task.Delay(160);
            checks.Add(new { check = "board pane follows owner resize before detaching", passed = old != boardPanel.AppWindow.Position });
            boardPanel.Detach();
            var detachedBoard = boardPanel.AppWindow.Position;
            board.AppWindow.Move(new PointInt32(bounds.X + 100, bounds.Y + 100));
            await Task.Delay(120);
            checks.Add(new { check = "detached board pane retains its position", passed = detachedBoard == boardPanel.AppWindow.Position });
            board.Close();
        }
        catch (Exception error) { checks.Add(new { check = "popup performance exception", passed = false, error = error.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "popup-performance.json"), JsonSerializer.Serialize(new { timings, checks },
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        async Task Open(string kind, int round, Func<Window> create)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var window = create();
            double synchronousMs = timer.Elapsed.TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var root = (FrameworkElement)window.Content;
            var loaded = new TaskCompletionSource();
            void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
            root.Loaded += OnLoaded;
            if (!root.IsLoaded) await Task.WhenAny(loaded.Task, Task.Delay(5000));
            root.Loaded -= OnLoaded;
            double loadedMs = timer.Elapsed.TotalMilliseconds;
            await Task.Delay(60);
            timings.Add(new { kind, round, synchronousMs, loadedMs, allocatedBytes = allocated });
            checks.Add(new { check = $"popup loads {kind} round {round}", passed = root.IsLoaded && root.ActualWidth > 0 && root.ActualHeight > 0 });
            window.Close();
            await Task.Delay(50);
        }

        async Task Follow(Window popup, Action detach, Func<bool> isDetached, string kind)
        {
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var origin = bar.AppWindow.Position;
            await Task.Delay(100);
            var previous = popup.AppWindow.Position;
            bar.AppWindow.Move(new PointInt32(origin.X + 40, origin.Y - 25));
            await Task.Delay(100);
            checks.Add(new { check = "attached pane follows toolbar " + kind, passed = !isDetached() && previous != popup.AppWindow.Position });
            detach();
            var detached = popup.AppWindow.Position;
            bar.AppWindow.Move(origin);
            await Task.Delay(100);
            checks.Add(new { check = "detached pane ignores toolbar movement " + kind, passed = isDetached() && detached == popup.AppWindow.Position });
            popup.Close();
        }
    }
}
