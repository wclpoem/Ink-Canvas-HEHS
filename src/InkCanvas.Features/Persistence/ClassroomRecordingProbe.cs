using System.IO.Compression;
using System.Text.Json;

namespace InkCanvas.Features.Persistence;

/// <summary>
/// 新格式课堂录制文件（inkcanvas-classroom）的静态探测。本体不读取插件私有格式：
/// 仅在 ZIP 容器中查找 <see cref="ManifestEntry"/> 清单并校验 Format 标记。
/// 纯 JSON 笔画 .icink、画板包 ZIP 与 .pdf 都不属于命中范围；任何读取/解析失败都按未命中处理。
/// </summary>
/// <remarks>
/// Features 不反向引用 Host.Contracts（避免循环依赖），下列常量与
/// <c>InkCanvas.Host.Contracts.ClassroomRecordingFormat</c> 的冻结值保持一致。
/// </remarks>
public static class ClassroomRecordingProbe
{
    /// <summary>与冻结契约 ClassroomRecordingFormat.Format 一致。</summary>
    public const string Format = "inkcanvas-classroom";
    /// <summary>与冻结契约 ClassroomRecordingFormat.ManifestEntry 一致。</summary>
    public const string ManifestEntry = "classroom.json";

    /// <summary>未安装/未启用插件时给出的中文提示（冻结措辞，宿主与测试共同引用）。</summary>
    public const string PluginRequiredMessage =
        "此文件是时光机课堂录制文件，需要安装并启用“时光机”插件才能读取。";

    /// <summary>探测指定文件是否为时光机课堂录制文件。</summary>
    public static bool IsClassroomRecording(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (!LooksLikeZip(stream)) return false;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return IsClassroomRecording(archive);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在已打开的 ZIP 归档内探测课堂录制清单（避免重复打开文件句柄）。</summary>
    public static bool IsClassroomRecording(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestEntry);
        if (entry is null) return false;
        try
        {
            using var reader = new StreamReader(entry.Open());
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            return document.RootElement.TryGetProperty("Format", out var format)
                && format.ValueKind == JsonValueKind.String
                && format.GetString() == Format;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeZip(Stream stream)
    {
        Span<byte> header = stackalloc byte[2];
        int read = stream.Read(header);
        stream.Position = 0;
        return read == 2 && header[0] == 0x50 && header[1] == 0x4B;
    }
}
