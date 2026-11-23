using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Tools;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class BoardPerformanceRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false;
        services.Settings.Automation.IsAutoSaveStrokesAtClear = false;
        using var shell = new ShellController(services);
        try
        {
            MeasureEraser();
            await VerifyImageCancellationAsync();
            var board = services.Board;
            for (int page = 0; page < 160; page++)
            {
                if (page > 0) board.NextPage();
                board.Current.Document.AddRange(Enumerable.Range(0, 40).Select(index => Stroke(index, page)));
            }
            board.GoTo(0);
            shell.Start();
            shell.StartInClickThrough();
            var watch = Stopwatch.StartNew();
            shell.ToggleBoard();
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            double openingMs = watch.Elapsed.TotalMilliseconds;
            await Task.Delay(400);
            var timings = new List<double>();
            long largestRaster = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int page = 0; page < 160; page += 4)
                {
                    watch.Restart();
                    board.GoTo(page);
                    timings.Add(watch.Elapsed.TotalMilliseconds);
                    await Task.Delay(16);
                    largestRaster = Math.Max(largestRaster, window.CachedBoardRasterBytes);
                    if (window.DrawingSurfaceCount != 1) throw new InvalidOperationException("Unexpected handwriting surface count.");
                }
            }
            timings.Sort();
            checks.Add(new { check = "160 populated pages reuse drawing surfaces and bound raster memory", passed = largestRaster <= 64L * 1024 * 1024,
                openingMs, medianSwitchMs = timings[timings.Count / 2], p95SwitchMs = timings[(int)(timings.Count * .95)], largestRaster });
            var controller = window.CurrentController;
            var original = controller.Document.Strokes.ToArray();
            var restorationTimes = new List<double>();
            for (int round = 0; round < 8; round++)
            {
                shell.ToggleBoard();
                await Task.Delay(80);
                watch.Restart();
                shell.ToggleBoard();
                restorationTimes.Add(watch.Elapsed.TotalMilliseconds);
                await Task.Delay(80);
                if (!ReferenceEquals(window, shell.OpenWindows.OfType<BoardWindow>().Single()))
                    throw new InvalidOperationException("Board restoration allocated another window.");
            }
            checks.Add(new { check = "annotation round trips reuse the board and preserve all ink", passed = ReferenceEquals(controller, board.Current)
                && original.SequenceEqual(controller.Document.Strokes), restorationTimes });
            shell.ToggleBoard();
            await Task.Delay(16000);
            checks.Add(new { check = "hidden board releases idle rasters and media players", passed = window.IdleResourcesReleased
                && window.CachedBoardRasterBytes == 0 && window.MediaLayer.PlayerCount == 0,
                rasterBytes = window.CachedBoardRasterBytes, mediaPlayers = window.MediaLayer.PlayerCount });
            shell.ToggleBoard();
            await Task.Delay(250);
            checks.Add(new { check = "cold resources restore without replacing the document", passed = !window.IdleResourcesReleased
                && ReferenceEquals(window.CurrentController, controller) && original.SequenceEqual(controller.Document.Strokes) });
            using var process = Process.GetCurrentProcess();
            checks.Add(new { check = "memory after repeated navigation", privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false),
                pageCount = board.PageCount, totalStrokes = board.Pages.Sum(page => page.Document.Count) });
        }
        catch (Exception ex) { checks.Add(new { check = "board performance exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "board-performance.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            Application.Current.Exit();
        }

        void MeasureEraser()
        {
            using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area,
                EraserRadius = 14, RecognizeShapes = false };
            controller.Document.AddRange(Enumerable.Range(0, 1200).Select(index => Stroke(index, 0)));
            using var scene = new InkSceneRenderer();
            using var cache = new CommittedInkLayer();
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1920, 1080, 96);
            controller.Document.Changed += () =>
            {
                cache.Invalidate();
                scene.Strokes.Retain(controller.Document.Strokes);
            };
            Draw(cache);
            long initialDraws = cache.CommittedStrokeDraws;
            var frames = new List<double>();
            var pointer = new OverlayPointer(9001, 200, 200, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
            controller.OnPointerDown(pointer);
            for (int frame = 0; frame < 120; frame++)
            {
                var watch = Stopwatch.StartNew();
                controller.OnPointerMove(pointer with { X = 200 + frame * 5, TimeMs = (uint)(frame * 8) });
                Draw(cache);
                frames.Add(watch.Elapsed.TotalMilliseconds);
            }
            controller.OnPointerUp(pointer with { X = 795, TimeMs = 1000 });
            Draw(cache);
            byte[] cached = target.GetPixelBytes();
            using var fresh = new CommittedInkLayer();
            Draw(fresh);
            bool matches = cached.SequenceEqual(target.GetPixelBytes());
            controller.Undo();
            bool undo = controller.Document.Count == 1200 && frames.Count > 0 && cache.CommittedStrokeDraws > initialDraws;
            controller.Redo();
            Draw(cache);
            bool redo = cached.SequenceEqual(target.GetPixelBytes());
            frames.Sort();
            checks.Add(new { check = "dense-page area erasing matches fresh rendering and supports undo", passed = matches && undo && redo,
                matches, undo, redo, medianMs = frames[60], p95Ms = frames[114], strokeDraws = cache.CommittedStrokeDraws - initialDraws,
                rasterBytes = cache.RasterBytes });

            void Draw(CommittedInkLayer layer)
            {
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    layer.Draw(drawing, scene, controller, 1920, 1080);
                }
                target.GetPixelBytes(0, 0, 1, 1);
            }
        }

        async Task VerifyImageCancellationAsync()
        {
            var imageBoard = new BoardService();
            try
            {
                byte[] data = await ScreenshotService.EncodePngAsync(new byte[4096 * 16 * 4], 4096, 16);
                for (int index = 0; index < 24; index++)
                    imageBoard.AddImage(new BoardImageAsset { Data = data.ToArray(), Width = 4096, Height = 16 });
                using var images = new ImageSceneRenderer { MaximumDecodeDimension = 128 };
                var loading = images.PrepareAsync(CanvasDevice.GetSharedDevice(), imageBoard.Current);
                images.Clear();
                await loading;
                bool canceled = images.CachedBitmapCount == 0;
                await images.PrepareAsync(CanvasDevice.GetSharedDevice(), imageBoard.Current);
                checks.Add(new { check = "leaving a page cancels queued image decoding and subsequent loads still work",
                    passed = canceled && images.CachedBitmapCount == 24 && images.CachedBitmapBytes <= 24L * 128 * 128 * 4,
                    canceled, count = images.CachedBitmapCount, bytes = images.CachedBitmapBytes });
            }
            finally { foreach (var page in imageBoard.Pages) page.Dispose(); }
        }
    }

    private static InkStroke Stroke(int index, int page)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None, BaseWidth = 3,
            Color = InkColor.Opaque(30, (byte)(90 + page % 100), 190) });
        for (int point = 0; point < 80; point++)
            stroke.AddPoint(30 + index % 12 * 145 + point * 1.5f, 30 + index / 12 % 36 * 26 + MathF.Sin(point * .12f) * 8);
        stroke.Complete();
        return stroke;
    }
}
