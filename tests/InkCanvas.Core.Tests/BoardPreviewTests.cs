using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

[Collection("Archive memory accounting")]
public sealed class BoardPreviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "InkCanvas-preview-tests-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) { Directory.CreateDirectory(_directory); return Path.Combine(_directory, name); }

    [Fact]
    public async Task CoverReaderDoesNotReadTheBoardOrLargeVideoPayload()
    {
        string path = PathFor("large.icink");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5GQAAAAASUVORK5CYII=");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var cover = zip.CreateEntry(BoardPreview.CoverEntry, CompressionLevel.NoCompression).Open()) await cover.WriteAsync(png);
            using (var metadata = zip.CreateEntry("preview/info.json").Open()) await JsonSerializer.SerializeAsync(metadata, new BoardPreviewInfo(1, 7, "课程"));
            using (var invalidManifest = new StreamWriter(zip.CreateEntry("board.json").Open())) invalidManifest.Write("invalid manifest intentionally");
            using var video = zip.CreateEntry("media/video.bin", CompressionLevel.NoCompression).Open();
            var chunk = new byte[65536]; for (int i = 0; i < 512; i++) await video.WriteAsync(chunk);
        }
        long before = GC.GetTotalAllocatedBytes(true);
        var result = await BoardPreview.ReadAsync(path);
        Assert.Equal(7, result.Info.PageCount); Assert.Equal(png, result.Cover);
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 4 * 1024 * 1024, "Reading the cover should avoid allocating the embedded video or parsing board.json.");
    }

    [Fact]
    public async Task OversizedDeclaredCoverIsIgnoredBeforeDecompression()
    {
        string path = PathFor("oversized.icink");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var cover = zip.CreateEntry(BoardPreview.CoverEntry).Open()) await cover.WriteAsync(new byte[BoardPreview.MaximumCoverBytes + 1]);
        var result = await BoardPreview.ReadAsync(path); Assert.Null(result.Cover);
    }

    [Fact]
    public async Task HugePngDimensionsAreRejectedBeforeImageDecode()
    {
        string path = PathFor("dimensions.icink");
        var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 4000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 4000);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var cover = zip.CreateEntry(BoardPreview.CoverEntry).Open()) await cover.WriteAsync(png);
        await Assert.ThrowsAsync<InvalidDataException>(() => BoardPreview.ReadAsync(path));
    }

    [Fact]
    public async Task OldFilesRemainEligibleForOnDemandRendering()
    {
        string path = PathFor("old.icink"); await File.WriteAllTextAsync(path, "{}");
        var result = await BoardPreview.ReadAsync(path); Assert.Null(result.Info); Assert.Null(result.Cover);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
