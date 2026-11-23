using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

[CollectionDefinition("Archive memory accounting", DisableParallelization = true)]
public sealed class ArchiveMemoryAccountingCollection { }

[Collection("Archive memory accounting")]
public sealed class BoardStorageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-board-storage-").FullName;
    private readonly List<BoardService> _boards = new();
    private string FilePath(string name = "board.icink") => Path.Combine(_directory, name);
    private BoardService Board()
    {
        var board = new BoardService(); _boards.Add(board); return board;
    }

    [Fact]
    public async Task PackageStoresBinaryMediaOnceAndPreservesAllPagesAndStrokeAppearance()
    {
        var board = Board();
        board.Background = BoardBackground.Grid; board.GridSpacing = 56;
        var image = new BoardImageAsset { Data = new byte[] { 1, 2, 3 }, Width = 320, Height = 180, Opacity = .45f,
            LayerOrder = 7, Transform = BoardElementMath.Write(Matrix3x2.CreateRotation(.3f)) };
        var media = new BoardMediaAsset { Data = new byte[] { 8, 7, 6, 5 }, PosterData = new byte[] { 4, 3 }, NaturalWidth = 1920, NaturalHeight = 1080, LayerOrder = 9 };
        board.AddImage(image); board.AddMedia(media);
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.InkWash, BaseWidth = 30, InkWash = new(.2f, 35, .3f), InkWashSeed = 123 });
        stroke.AddPoint(20, 30); stroke.AddPoint(100, 110); stroke.Complete(); board.Current.Document.Add(stroke);
        board.InsertPageAfterCurrent();
        board.AddImage(image.Copy(false, false)); board.AddMedia(media.Copy(false, false));
        string path = FilePath();
        await board.SaveBoardAsync(path);
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(3, archive.Entries.Count(entry => entry.FullName.StartsWith("media/")));
            using var reader = new StreamReader(archive.GetEntry("board.json")!.Open());
            string manifest = await reader.ReadToEndAsync();
            Assert.DoesNotContain("\"Data\"", manifest); Assert.DoesNotContain("\"PosterData\"", manifest);
            Assert.Contains("\"Version\":4", manifest);
        }
        var loaded = Board(); await loaded.LoadBoardAsync(path);
        Assert.Equal(2, loaded.PageCount); Assert.Equal(BoardBackground.Grid, loaded.Background); Assert.Equal(56, loaded.GridSpacing);
        var restored = Assert.Single(loaded.CurrentImages);
        Assert.Equal(image.Data, restored.Data); Assert.Equal(image.Opacity, restored.Opacity); Assert.Equal(image.Transform, restored.Transform);
        Assert.Equal(image.LayerOrder, restored.LayerOrder);
        var video = Assert.Single(loaded.CurrentMedia);
        Assert.Equal(media.Data, video.Data); Assert.Equal(media.PosterData, video.PosterData); Assert.Equal(1920u, video.NaturalWidth);
        var ink = Assert.Single(loaded.Current.Document.Strokes);
        Assert.Equal(stroke.Id, ink.Id); Assert.Equal(stroke.Attributes.InkWash, ink.Attributes.InkWash); Assert.Equal(123, ink.Attributes.InkWashSeed);
        Assert.Equal(stroke.Points, ink.Points);
        loaded.NextPage();
        Assert.Same(restored.Data, loaded.CurrentImages[0].Data); Assert.Same(video.Data, loaded.CurrentMedia[0].Data);
    }

    [Fact]
    public async Task LargeVideoSaveAvoidsBase64AllocationAndLoadAllocatesOnlyTheBinaryPayload()
    {
        var board = Board();
        var data = new byte[32 * 1024 * 1024]; Random.Shared.NextBytes(data);
        board.AddMedia(new() { Data = data });
        string path = FilePath();
        long before = GC.GetTotalAllocatedBytes(true);
        await board.SaveBoardAsync(path);
        long saveAllocation = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(saveAllocation < data.Length / 2, $"Save allocated {saveAllocation:N0} bytes for {data.Length:N0} binary bytes.");
        Assert.InRange(new FileInfo(path).Length, data.Length, data.Length + 65536L);
        var loaded = Board(); before = GC.GetTotalAllocatedBytes(true);
        await loaded.LoadBoardAsync(path);
        long loadAllocation = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(loadAllocation < data.Length * 2L, $"Load allocated {loadAllocation:N0} bytes for {data.Length:N0} binary bytes.");
        Assert.True(data.AsSpan().SequenceEqual(loaded.CurrentMedia.Single().Data));
    }

    [Fact]
    public async Task SaveSnapshotIsStableWhileTheUserContinuesEditing()
    {
        var board = Board();
        var image = new BoardImageAsset { Name = "before", Data = new byte[2 * 1024 * 1024], Width = 400, Height = 300, Left = 10 };
        board.AddImage(image);
        Task save = board.SavePageAsync(FilePath());
        image.Name = "after"; image.Left = 500; image.Data = new byte[] { 99 };
        board.InsertPageAfterCurrent();
        await save;
        var loaded = Board(); await loaded.LoadBoardAsync(FilePath());
        var restored = Assert.Single(loaded.CurrentImages);
        Assert.Equal("before", restored.Name); Assert.Equal(10, restored.Left); Assert.Equal(2 * 1024 * 1024, restored.Data.Length);
    }

    [Fact]
    public async Task ASlowerEarlierSaveCannotOverwriteTheLatestRequestedSnapshot()
    {
        var board = Board(); var media = new BoardMediaAsset { Name = "old", Data = new byte[32 * 1024 * 1024] };
        board.AddMedia(media);
        var earlier = board.SaveBoardAsync(FilePath());
        media.Name = "latest"; media.Data = new byte[] { 42 };
        var latest = board.SaveBoardAsync(FilePath());
        await Task.WhenAll(earlier, latest);
        var loaded = Board(); await loaded.LoadBoardAsync(FilePath());
        Assert.Equal("latest", loaded.CurrentMedia.Single().Name); Assert.Equal(new byte[] { 42 }, loaded.CurrentMedia.Single().Data);
    }

    [Fact]
    public async Task FailedSerializationKeepsThePreviousFileAndDoesNotBlockTheNextSave()
    {
        var board = Board(); var image = new BoardImageAsset { Data = new byte[2 * 1024 * 1024], Width = 100, Height = 100, Left = 25 };
        board.AddImage(image); await board.SaveBoardAsync(FilePath());
        image.Left = float.NaN;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => board.SaveBoardAsync(FilePath()));
        var loaded = Board(); await loaded.LoadBoardAsync(FilePath()); Assert.Equal(25, loaded.CurrentImages.Single().Left);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        image.Left = 40; await board.SaveBoardAsync(FilePath());
        await loaded.LoadBoardAsync(FilePath()); Assert.Equal(40, loaded.CurrentImages.Single().Left);
    }

    [Fact]
    public async Task CancellationAndConcurrentSavesLeaveACompleteFileAndNoTemporaryFiles()
    {
        var board = Board(); board.AddMedia(new() { Data = new byte[2 * 1024 * 1024] });
        string path = FilePath();
        await board.SaveBoardAsync(path);
        long length = new FileInfo(path).Length;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => board.SaveBoardAsync(path, canceled.Token));
        Assert.Equal(length, new FileInfo(path).Length);
        await Task.WhenAll(board.SaveBoardAsync(path), board.SaveBoardAsync(path));
        var loaded = Board(); await loaded.LoadBoardAsync(path);
        Assert.Equal(2 * 1024 * 1024, loaded.CurrentMedia.Single().Data.Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        await File.WriteAllTextAsync(FilePath("blocked"), "original");
        await Assert.ThrowsAnyAsync<IOException>(() => board.SaveBoardAsync(Path.Combine(FilePath("blocked"), "board.icink")));
        Assert.Equal("original", await File.ReadAllTextAsync(FilePath("blocked")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyJsonAndStrokeOnlyFilesStillOpen(bool strokeOnly)
    {
        var stroke = new { Width = 5, Points = new[] { new[] { 12f, 34f, .5f, 5f, 0f } } };
        object model = strokeOnly ? new { Version = 1, Strokes = new[] { stroke } } : new
        {
            Format = "inkcanvas-board", Version = 1, Background = "White", GridSpacing = 48,
            Pages = new[] { new { Strokes = new[] { stroke }, Images = new[] { new BoardImageAsset { Data = new byte[] { 1, 2 }, Width = 100, Height = 80 } },
                Media = new[] { new BoardMediaAsset { Data = new byte[] { 3, 4 }, PosterData = new byte[] { 5 } } } } },
        };
        await File.WriteAllTextAsync(FilePath(), JsonSerializer.Serialize(model));
        var board = Board(); await board.LoadBoardAsync(FilePath());
        Assert.Equal(5, Assert.Single(board.Current.Document.Strokes).Points.Single().Width);
        if (!strokeOnly)
        {
            Assert.Equal(BoardBackground.White, board.Background); Assert.Equal(new byte[] { 1, 2 }, board.CurrentImages.Single().Data);
            Assert.Equal(new byte[] { 5 }, board.CurrentMedia.Single().PosterData);
        }
        await board.SaveBoardAsync(FilePath());
        using var zip = ZipFile.OpenRead(FilePath()); Assert.NotNull(zip.GetEntry("board.json"));
    }

    [Fact]
    public async Task MissingMediaFailsBeforeReplacingTheCurrentBoard()
    {
        var source = Board(); source.AddImage(new() { Data = new byte[] { 1, 2 }, Width = 100, Height = 100 });
        await source.SaveBoardAsync(FilePath());
        using (var zip = ZipFile.Open(FilePath(), ZipArchiveMode.Update)) zip.GetEntry("media/000000.bin")!.Delete();
        var target = Board(); var existing = target.Current;
        target.AddMedia(new() { Data = new byte[] { 9 } });
        await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadBoardAsync(FilePath()));
        Assert.Same(existing, target.Current); Assert.Equal(new byte[] { 9 }, target.CurrentMedia.Single().Data);
    }

    [Fact]
    public async Task PreparedPageRetainsItsOriginalTargetAndCanOutliveTheInputFile()
    {
        var source = Board(); source.AddImage(new() { Data = new byte[] { 1, 2 }, Width = 100, Height = 100 });
        await source.SavePageAsync(FilePath());
        var target = Board();
        var apply = await target.PrepareOpenAsync(FilePath(), false);
        File.Delete(FilePath()); target.InsertPageAfterCurrent(); apply();
        Assert.Empty(target.CurrentImages); target.PreviousPage(); Assert.Single(target.CurrentImages);
    }

    [Fact]
    public async Task OpeningOnePageDoesNotAllocateOtherPagesVideoPayloads()
    {
        var source = Board(); source.InsertPageAfterCurrent(); source.AddMedia(new() { Data = new byte[32 * 1024 * 1024] });
        await source.SaveBoardAsync(FilePath());
        var target = Board(); long before = GC.GetTotalAllocatedBytes(true);
        await target.LoadPageAsync(FilePath());
        long allocation = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(allocation < 8 * 1024 * 1024, $"Opening the empty first page allocated {allocation:N0} bytes.");
        Assert.Empty(target.CurrentMedia); Assert.Single(target.Pages);
    }

    [Fact]
    public async Task ImportedPageMustBeSingleAndFullyPreparedBeforeTheTemporaryFileIsDeleted()
    {
        var source = Board(); source.AddMedia(new() { Data = new byte[] { 3, 4 }, PosterData = new byte[] { 5 } });
        await source.SavePageAsync(FilePath());
        var target = Board(); var apply = await target.PrepareImportedPageAsync(FilePath(), true);
        File.Delete(FilePath()); apply(); Assert.Equal(new byte[] { 3, 4 }, target.CurrentMedia.Single().Data);
        await File.WriteAllTextAsync(FilePath(), "{\"Format\":\"inkcanvas-board\",\"Version\":1,\"Pages\":[]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => target.PrepareImportedPageAsync(FilePath(), true));
    }

    public void Dispose()
    {
        foreach (var board in _boards) foreach (var page in board.Pages) page.Dispose();
        Directory.Delete(_directory, true);
    }
}
