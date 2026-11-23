using System.Runtime.CompilerServices;
using InkCanvas.Host.Contracts;
using InkCanvas.Pdf;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private void NotifyDocumentAi(string path)
    {
        if (_host.DocumentAi?.Available != true) return;
        var pages = _file.Pages.ToArray(); string password = _file.Password;
        _host.DocumentAi.Saved(new(path, "inkcanvas.pdf") { ExportImages = (directory, token) => ExportAiPagesAsync(path, password, pages, directory, token) });
    }

    private static async IAsyncEnumerable<AiDocumentImage> ExportAiPagesAsync(string path, string password, IReadOnlyList<PdfPageGeometry> pages,
        string directory, [EnumeratorCancellation] CancellationToken token)
    {
        await using var renderer = await PdfRasterizer.OpenAsync(path, password, token);
        for (int index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            double scale = Math.Min(1, Math.Min(3840 / (double)Math.Max(page.DipWidth, page.DipHeight), Math.Sqrt(3840d * 2160 / (page.DipWidth * page.DipHeight))));
            byte[] png = await renderer.RenderAsync(index, page, new Windows.Foundation.Rect(0, 0, page.DipWidth, page.DipHeight), scale, token);
            string output = Path.Combine(directory, $"pdf-{index + 1:D4}.png");
            await File.WriteAllBytesAsync(output, png, token);
            yield return new(output, index + 1, "page", (int)Math.Ceiling(page.DipWidth * scale), (int)Math.Ceiling(page.DipHeight * scale));
        }
    }
}
