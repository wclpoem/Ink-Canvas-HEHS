using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using InkCanvas.Host.Contracts;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using PdfSharp.Drawing;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

/// <summary>Exercises native rendering on UI/background threads and releases it before host exit.</summary>
public static class PdfRasterizerDiagnostics
{
    public static async Task RunAsync(IDocumentWorkspaceHost host, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        try
        {
            string path = Path.Combine(directory, "lifetime.pdf");
            using (var fixture = new PdfSharp.Pdf.PdfDocument())
            {
                for (int index = 0; index < 4; index++)
                {
                    var page = fixture.AddPage();
                    page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800); page.Rotate = index * 90;
                    using var drawing = XGraphics.FromPdfPage(page);
                    drawing.DrawRectangle(XBrushes.White, 0, 0, 600, 800);
                    drawing.DrawRectangle(XBrushes.Red, 90, 120, 120, 160);
                    drawing.DrawEllipse(XBrushes.Blue, 300, 400, 150, 190);
                }
                fixture.Save(path);
            }
            using var file = await PdfAnnotationFile.OpenAsync(path);
            using var device = new CanvasDevice();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                await using var renderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
                var page = file.Pages[index];
                var source = new Rect(0, 0, page.DipWidth, page.DipHeight);
                byte[] png = await renderer.RenderAsync(index, page, source, 1, CancellationToken.None);
                Check("page dimensions and rotation " + index, Width(png) == (int)Math.Ceiling(page.DipWidth)
                    && Height(png) == (int)Math.Ceiling(page.DipHeight));
                using var full = await BitmapAsync(png, device);
                using var direct = await renderer.RenderBitmapAsync(index, page, source, 1, CancellationToken.None);
                byte[] fullPixels = full.GetPixelBytes(), directPixels = direct.GetPixelBytes();
                Check("direct GPU preview matches PNG export " + index, directPixels.SequenceEqual(fullPixels));
                var crop = new Rect(Math.Floor(page.DipWidth * .125), Math.Floor(page.DipHeight * .125),
                    Math.Floor(page.DipWidth * .625), Math.Floor(page.DipHeight * .625));
                using var detail = await BitmapAsync(await renderer.RenderAsync(index, page, crop, 1, CancellationToken.None), device);
                using var reference = new CanvasRenderTarget(device, (float)crop.Width, (float)crop.Height, 96);
                using (var draw = reference.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White);
                    draw.DrawImage(full, Vector2.Zero, crop);
                }
                byte[] actual = detail.GetPixelBytes(), expected = reference.GetPixelBytes();
                long error = 0;
                for (int channel = 0; channel < actual.Length; channel++) error += Math.Abs(actual[channel] - expected[channel]);
                double meanError = error / (double)actual.Length;
                Check("partial page keeps pixel origin " + index, meanError < 1, new { meanError });
            }

            for (int cycle = 0; cycle < 3; cycle++)
            {
                // AI exports call the same rasterizer from background work after the PDF window closes.
                await Task.Run(async () =>
                {
                    await using var renderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
                    await renderer.PreviewAsync(0, file.Pages[0], CancellationToken.None);
                });
            }
            Check("background export repeatedly opens renders and disposes", true);

            using (var input = File.OpenRead(path))
            using (var stream = input.AsRandomAccessStream())
            {
                var document = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(stream);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    using var software = new PdfNativeRenderer(forceSoftwareRenderer: true);
                    using var page = document.GetPage(0);
                    await page.PreparePageAsync();
                    var png = await software.RenderAsync(page, new(0, 0, (float)page.Size.Width, (float)page.Size.Height), 600, 800, CancellationToken.None);
                    Check("WARP renders and releases before process exit " + cycle, Width(png) == 600 && Height(png) == 800);
                }
            }

            var closing = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            var geometry = file.Pages[0];
            var rendering = closing.RenderAsync(0, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 2, CancellationToken.None);
            var disposing = closing.DisposeAsync().AsTask();
            Check("dispose waits for active rendering", (await rendering).Length > 0);
            await disposing;
            await closing.DisposeAsync();
            bool disposedRejected = false;
            try { await closing.PreviewAsync(0, geometry, CancellationToken.None); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check("disposed renderer rejects work and repeated disposal is safe", disposedRejected);

            await using (var canceled = await PdfRasterizer.OpenAsync(file, CancellationToken.None))
            {
                using var cancellation = new CancellationTokenSource();
                var pending = canceled.RenderAsync(0, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 2, cancellation.Token);
                cancellation.Cancel();
                bool canceledObserved = false;
                try { await pending; }
                catch (OperationCanceledException) { canceledObserved = true; }
                Check("canceled render drains before device release", canceledObserved);
                Check("renderer remains usable after cancellation", (await canceled.PreviewAsync(0, geometry, CancellationToken.None)).Length > 0);
            }

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool canceledObserved = false;
                try { await using var canceled = await PdfRasterizer.OpenAsync(file, cancellation.Token); }
                catch (OperationCanceledException) { canceledObserved = true; }
                Check("canceled open releases input file", canceledObserved);
            }
            var windowRenderer = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            var windowFile = await PdfAnnotationFile.OpenAsync(path);
            var window = new PdfWindow(host, windowFile, windowRenderer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate();
            await Task.Delay(80);
            Check("window closes with pending page rendering", await window.CloseAsync());
        }
        catch (Exception exception) { Check("native PDF lifetime exception", false, exception.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf-lifetime.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static int Width(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
    private static int Height(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
    private static async Task<CanvasBitmap> BitmapAsync(byte[] png, CanvasDevice device)
    {
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0);
        return await CanvasBitmap.LoadAsync(device, stream, 96);
    }
}
