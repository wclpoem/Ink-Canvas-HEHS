using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ArchiveDescriptionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("InkCanvas-caption-").FullName;
    private string FilePath(string name) => Path.Combine(_directory, name + ".icink");

    [Fact]
    public async Task NamingPreservesEditableInkAndDoesNotOverwriteAnotherDocument()
    {
        var board = new BoardService();
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 12, TaperKind = TaperKind.InkWash }); stroke.AddPoint(-50, 20); stroke.AddPoint(100, 200); stroke.Complete(); board.Current.Document.Add(stroke);
        board.AddMedia(new() { Data = new byte[1024 * 1024], Name = "video.mp4" });
        string original = FilePath("原名"), existing = FilePath("新标题"); await board.SaveBoardAsync(original);
        await File.WriteAllTextAsync(existing, "keep");
        var stamp = new FileInfo(original);
        string renamed = await BoardService.DescribeArchiveAsync(original, "新标题", "水墨笔迹与课堂批注", stamp.Length, stamp.LastWriteTimeUtc);
        Assert.NotEqual(existing, renamed); Assert.False(File.Exists(original)); Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        var metadata = await BoardService.ReadArchiveMetadataAsync(renamed);
        Assert.Equal("新标题", metadata.Title); Assert.Equal("水墨笔迹与课堂批注", metadata.Description);
        var restored = new BoardService(); await restored.LoadBoardAsync(renamed);
        Assert.Equal(stroke.Points, Assert.Single(restored.Current.Document.Strokes).Points);
        Assert.Equal(1024 * 1024, Assert.Single(restored.CurrentMedia).Data.Length);
        foreach (var page in restored.Pages.Concat(board.Pages)) page.Dispose();
    }

    [Fact]
    public async Task ConcurrentChangeMakesTheModelResultStale()
    {
        string original = FilePath("原名"); var board = new BoardService(); await board.SaveBoardAsync(original);
        var old = new FileInfo(original); long length = old.Length; DateTime modified = old.LastWriteTimeUtc;
        await File.AppendAllTextAsync(original, "changed");
        await Assert.ThrowsAsync<IOException>(() => BoardService.DescribeArchiveAsync(original, "标题", "描述", length, modified));
        Assert.True(File.Exists(original)); Assert.False(File.Exists(FilePath("标题")));
        foreach (var page in board.Pages) page.Dispose();
    }

    [Fact]
    public async Task VisualPageReaderLeavesVideoPayloadUnloaded()
    {
        var board = new BoardService(); board.AddMedia(new() { Data = new byte[16 * 1024 * 1024] }); board.InsertPageAfterCurrent();
        string original = FilePath("多页"); await board.SaveBoardAsync(original);
        int pages = 0;
        await foreach (var visual in BoardService.ReadVisualPagesAsync(original))
        {
            pages++; Assert.All(visual.CurrentMedia, media => Assert.Empty(media.Data));
        }
        Assert.Equal(2, pages); foreach (var page in board.Pages) page.Dispose();
    }

    [Fact]
    public async Task LegacyJsonCanBeNamedAndMigratedWithoutLosingPoints()
    {
        string original = FilePath("旧版");
        await File.WriteAllTextAsync(original, """{"Strokes":[{"Color":4278190080,"Width":4,"Points":[[10,20,0.5,4,0],[30,40,0.5,4,1]]}]}""");
        var stamp = new FileInfo(original);
        string renamed = await BoardService.DescribeArchiveAsync(original, "历史板书", "旧版板书内容", stamp.Length, stamp.LastWriteTimeUtc);
        using var zip = ZipFile.OpenRead(renamed); Assert.NotNull(zip.GetEntry("board.json"));
        var board = new BoardService(); await board.LoadBoardAsync(renamed);
        Assert.Equal(2, Assert.Single(board.Current.Document.Strokes).Count);
        foreach (var page in board.Pages) page.Dispose();
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
