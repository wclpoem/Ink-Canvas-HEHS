using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using InkCanvas.Features.Ppt;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

internal static class PptPreviewWorker
{
    public static async Task Run(string pipeName, int parentId, string directory)
    {
        _ = Task.Run(async () =>
        {
            try { using var parent = Process.GetProcessById(parentId); await parent.WaitForExitAsync(); }
            catch (ArgumentException) { }
            Environment.Exit(0);
        });
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var interop = new PptInteropService();
            while (true)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string line;
                try { line = await reader.ReadLineAsync(idle.Token); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                PptPreviewResponse response;
                string path = Path.Combine(directory, "slide.png");
                try
                {
                    var request = JsonSerializer.Deserialize<PptPreviewRequest>(line) ?? throw new InvalidDataException();
                    interop.ProgIds = new[] { request.Presentation.ApplicationProgId };
                    if (!interop.TryAttach()) throw new InvalidOperationException("尚未连接到当前课件。");
                    var slide = interop.GetPreviewSlide(request.Presentation, request.Index);
                    if (!interop.ExportSlidePreview(request.Presentation, slide.SlideId, path))
                        throw new InvalidOperationException("此页暂时无法生成预览。");
                    byte[] image = await File.ReadAllBytesAsync(path);
                    response = new(new(slide, image));
                }
                catch (Exception ex) { response = new(null, ex.GetBaseException().Message); }
                finally
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { Application.Current.Exit(); }
    }
}
