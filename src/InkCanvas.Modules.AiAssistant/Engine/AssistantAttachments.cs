using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AiAssistant;

public static class AssistantAttachments
{
    public static bool IsImagePath(string path) => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" }
        .Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static async Task<AssistantAttachment> FromPathAsync(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!IsImagePath(path)) return new() { Path = path };
        var file = new FileInfo(path);
        if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("图片超过 32 MB，请缩小后添加。");
        return await FromImageAsync(await File.ReadAllBytesAsync(path), file.Name);
    }

    public static async Task<AssistantAttachment> FromImageAsync(byte[] bytes, string name)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bytes.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        double scale = Math.Min(1, 2048d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        using var image = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)) },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(.8f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties);
        encoder.SetSoftwareBitmap(image);
        await encoder.FlushAsync();
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        var compressed = new byte[checked((int)output.Size)];
        reader.ReadBytes(compressed);
        return new() { Label = name, Image = "data:image/jpeg;base64," + Convert.ToBase64String(compressed) };
    }

    public static async Task<string> ReadTextAsync(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("附件已移动或删除，请重新添加。", path);
        if (file.Length > 20 * 1024 * 1024) throw new InvalidDataException("文件超过 20 MB，请提取相关部分后添加。");
        string extension = file.Extension.ToLowerInvariant();
        if (extension is ".docx" or ".pptx" or ".xlsx" or ".odt")
        {
            return await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(path);
                var result = new StringBuilder();
                var entries = archive.Entries.Where(entry => extension switch
                {
                    ".docx" => entry.FullName == "word/document.xml",
                    ".pptx" => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal),
                    ".xlsx" => entry.FullName == "xl/sharedStrings.xml" || entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal),
                    _ => entry.FullName == "content.xml",
                }).OrderBy(entry => entry.FullName, StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("附件文本过大，请提取相关内容。");
                    using var stream = entry.Open();
                    var document = XDocument.Load(stream);
                    result.AppendLine(entry.FullName);
                    foreach (var paragraph in document.Descendants().Where(node => node.Name.LocalName is "p" or "row"))
                        result.AppendLine(string.Join(" ", paragraph.Descendants().Where(node => node.Name.LocalName is "t" or "v").Select(node => node.Value)));
                    if (entry.FullName == "xl/sharedStrings.xml")
                        result.AppendLine(string.Join("\n", document.Descendants().Where(node => node.Name.LocalName == "t").Select(node => node.Value)));
                    if (result.Length > 100000) throw new InvalidDataException("附件文本较长，请拆分相关内容后添加。");
                }
                return result.ToString();
            }, token);
        }
        if (!new[] { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".cs", ".py", ".js", ".ts", ".yaml", ".yml", ".tex" }.Contains(extension)) return "";
        if (file.Length > 400000) throw new InvalidDataException("文本文件较长，请提取相关部分后添加。");
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token);
    }
}
