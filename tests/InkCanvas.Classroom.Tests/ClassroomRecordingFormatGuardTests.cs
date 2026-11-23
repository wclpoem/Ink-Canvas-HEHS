using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 新格式课堂录制文件（inkcanvas-classroom）守卫测试：
/// 探测只命中带 classroom.json 清单且 Format 标记匹配的 ZIP；纯 JSON 笔画 .icink、
/// 画板包 ZIP 与缺清单/格式不符的归档均不误判；未装插件时抛出冻结措辞的中文异常。
/// </summary>
public sealed class ClassroomRecordingFormatGuardTests
{
    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-guard-{Guid.NewGuid():N}-{name}");

    private static void WriteZip(string path, params (string Entry, string Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private static string ClassroomManifest(string format = ClassroomRecordingProbe.Format)
        => JsonSerializer.Serialize(new { Format = format, Version = 1, SessionId = "s-1" });

    [Fact]
    public void Probe_ClassroomRecordingPackage_Hits()
    {
        string path = TempFile("session.icink");
        WriteZip(path, ("classroom.json", ClassroomManifest()), ("events.json", "[]"));

        Assert.True(ClassroomRecordingProbe.IsClassroomRecording(path));
    }

    [Fact]
    public void Probe_BoardPackageZip_WithoutClassroomManifest_DoesNotHit()
    {
        string path = TempFile("board.icink");
        WriteZip(path, ("board.json", "{\"version\":1}"), ("strokes.json", "[]"));

        Assert.False(ClassroomRecordingProbe.IsClassroomRecording(path));
    }

    [Fact]
    public void Probe_PlainJsonStrokeFile_DoesNotHit()
    {
        string path = TempFile("strokes.icink");
        File.WriteAllText(path, "{\"version\":1,\"strokes\":[]}");

        Assert.False(ClassroomRecordingProbe.IsClassroomRecording(path));
    }

    [Fact]
    public void Probe_UnsupportedFormatManifest_DoesNotHit()
    {
        string path = TempFile("other.icink");
        WriteZip(path, ("classroom.json", ClassroomManifest("inkcanvas-board")));

        Assert.False(ClassroomRecordingProbe.IsClassroomRecording(path));
    }

    [Fact]
    public void Probe_MalformedManifest_DoesNotHit()
    {
        string path = TempFile("broken.icink");
        WriteZip(path, ("classroom.json", "{\"Format\":"));

        Assert.False(ClassroomRecordingProbe.IsClassroomRecording(path));
    }

    [Fact]
    public void Probe_MissingFile_DoesNotHit()
    {
        Assert.False(ClassroomRecordingProbe.IsClassroomRecording(TempFile("absent.icink")));
    }

    [Fact]
    public void Guard_Message_IsFrozenChineseWording()
    {
        Assert.Equal(
            "此文件是时光机课堂录制文件，需要安装并启用“时光机”插件才能读取。",
            ClassroomRecordingProbe.PluginRequiredMessage);

        var exception = new InvalidDataException(ClassroomRecordingProbe.PluginRequiredMessage);
        Assert.Equal(ClassroomRecordingProbe.PluginRequiredMessage, exception.Message);
    }

    [Fact]
    public async Task BoardService_ClassroomRecordingPackage_ThrowsPluginRequiredMessage()
    {
        string path = TempFile("session.icink");
        WriteZip(path, ("classroom.json", ClassroomManifest()));

        var board = new BoardService();
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => board.PrepareOpenAsync(path, entireBoard: false));

        Assert.Equal(ClassroomRecordingProbe.PluginRequiredMessage, exception.Message);
    }
}
