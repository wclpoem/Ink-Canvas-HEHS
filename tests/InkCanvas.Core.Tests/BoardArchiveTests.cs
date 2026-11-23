using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class BoardArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-archive-test-").FullName;
    private readonly List<BoardService> _boards = new();
    private BoardService Board() { var board = new BoardService(); _boards.Add(board); return board; }
    private ArchiveSettings Settings() => new() { DefaultDirectory = _directory, UseDateFolders = false, Title = "函数复习", ClassName = "初三一班", Teacher = "王老师" };
    private BoardArchiveService Service(BoardService board, ArchiveSettings settings) => new(board, () => settings, _directory);

    private static void Draw(BoardService board, float x = 20)
    {
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(x, 30); stroke.AddPoint(x + 10, 50); stroke.Complete(); board.Current.Document.Add(stroke);
    }

    [Fact]
    public void FilenameSupportsTeachingFieldsAndRejectsInvalidTemplates()
    {
        var settings = Settings(); settings.FileNameTemplate = "{date}_{class}_{teacher}_{title}_{pages}_{reason}_{time}";
        string name = ArchivePolicy.FileName(settings, new(2026, 11, 22, 10, 30, 45, TimeSpan.FromHours(8)), 3, "退出白板");
        Assert.Equal("2026-11-22_初三一班_王老师_函数复习_3_退出白板_10-30-45.icink", name);
        settings.Title = "../CON:<lesson>?"; settings.FileNameTemplate = "{title}";
        Assert.DoesNotContain('/', ArchivePolicy.FileName(settings, DateTimeOffset.Now, 1, "test"));
        Assert.DoesNotContain(':', ArchivePolicy.FileName(settings, DateTimeOffset.Now, 1, "test"));
        settings.FileNameTemplate = "{unknown}";
        Assert.Throws<ArgumentException>(() => ArchivePolicy.FileName(settings, DateTimeOffset.Now, 1, "test"));
        Assert.Throws<ArgumentException>(() => ArchivePolicy.ResolveDirectory("relative"));
        settings.FileNameTemplate = "CON.icink";
        Assert.Equal("_CON.icink", ArchivePolicy.FileName(settings, DateTimeOffset.Now, 1, "test"));
    }

    [Fact]
    public async Task AutomaticArchiveRestoresAllPagesMediaAndTeachingMetadata()
    {
        var board = Board(); Draw(board);
        board.Background = BoardBackground.Grid;
        board.AddImage(new() { Data = new byte[] { 1, 2, 3 }, Width = 40, Height = 30 });
        board.InsertPageAfterCurrent(); board.Background = BoardBackground.White;
        board.AddMedia(new() { Data = new byte[] { 4, 5 }, PosterData = new byte[] { 6 } }); Draw(board, 100);
        var settings = Settings();
        using var archives = Service(board, settings);
        var result = await archives.SaveAutomaticAsync("退出白板");
        Assert.Equal(2, result.PageCount); Assert.True(result.Bytes > 0);
        var metadata = await BoardService.ReadArchiveMetadataAsync(result.Path);
        Assert.Equal(settings.ClassName, metadata.ClassName); Assert.Equal(settings.Teacher, metadata.Teacher);
        Assert.Equal("退出白板", metadata.Reason); Assert.True(metadata.Automatic);
        var restored = Board(); await restored.LoadBoardAsync(result.Path);
        Assert.Equal(2, restored.PageCount); Assert.Equal(BoardBackground.Grid, restored.Background);
        Assert.Single(restored.CurrentImages); Assert.Single(restored.Current.Document.Strokes);
        restored.NextPage(); Assert.Equal(BoardBackground.White, restored.Background);
        Assert.Equal(new byte[] { 4, 5 }, restored.CurrentMedia.Single().Data);
        Assert.Equal(metadata, restored.ArchiveMetadata);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.NotNull(zip.GetEntry("archive.json"));
        using var reader = new StreamReader(zip.GetEntry("board.json")!.Open());
        Assert.DoesNotContain("\"Data\"", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task OnlyChangesTriggerSavesIncludingInactivePageAndPaperChanges()
    {
        var board = Board(); Draw(board); var first = board.Current;
        board.InsertPageAfterCurrent(); Draw(board, 80);
        var settings = Settings(); using var archives = Service(board, settings);
        var initial = await archives.SaveAutomaticAsync("定时保存");
        Assert.Null(await archives.SaveAutomaticAsync("退出白板"));
        board.PreviousPage();
        Assert.Null(await archives.SaveAutomaticAsync("定时保存"));
        board.Background = BoardBackground.Coordinate;
        Assert.NotNull(await archives.SaveAutomaticAsync("定时保存"));
        board.NextPage();
        first.Document.Clear();
        Assert.NotNull(await archives.SaveAutomaticAsync("定时保存"));
        Assert.False(archives.HasUnsavedChanges);
        settings.ClassName = "初三二班";
        Assert.True(archives.HasUnsavedChanges);
        Assert.NotNull(await archives.SaveAutomaticAsync("退出白板"));
    }

    [Fact]
    public async Task SameNameArchivesNeverOverwriteAndRetentionKeepsManualFiles()
    {
        var board = Board(); Draw(board);
        var settings = Settings(); settings.FileNameTemplate = "固定文件名"; settings.AutomaticCopiesToKeep = 2;
        using var archives = Service(board, settings);
        string manualPath = Path.Combine(ArchivePolicy.AutomaticDirectory(settings), "手动文件.icink");
        await archives.SaveManualAsync(board, manualPath, true);
        string unrelated = Path.Combine(Path.GetDirectoryName(manualPath)!, "旧格式.icink");
        InkFileStore.Save(unrelated, board.Current.Document.Strokes);
        var first = await archives.SaveAutomaticAsync("定时保存", force: true);
        Draw(board, 50); var second = await archives.SaveAutomaticAsync("定时保存");
        Draw(board, 70); var third = await archives.SaveAutomaticAsync("退出白板");
        Assert.NotEqual(first.Path, second.Path); Assert.NotEqual(second.Path, third.Path);
        Assert.False(File.Exists(first.Path)); Assert.True(File.Exists(second.Path)); Assert.True(File.Exists(third.Path));
        Assert.True(File.Exists(manualPath)); Assert.True(File.Exists(unrelated));
        using var reopened = Service(board, settings);
        Assert.Equal(third.Path, reopened.LastAutomaticSave.Path);
    }

    [Fact]
    public async Task FailedAndCancelledSavesKeepExistingArchiveAndRemainRetryable()
    {
        var board = Board(); Draw(board); var settings = Settings();
        using var archives = Service(board, settings);
        var first = await archives.SaveAutomaticAsync("定时保存");
        Draw(board, 60);
        string blocker = Path.Combine(_directory, "blocked"); await File.WriteAllTextAsync(blocker, "unchanged");
        settings.AutomaticDirectory = Path.Combine(blocker, "child");
        await Assert.ThrowsAnyAsync<IOException>(() => archives.SaveAutomaticAsync("退出白板"));
        Assert.NotNull(archives.LastError); Assert.True(archives.HasUnsavedChanges);
        Assert.Equal(first.Path, archives.LastAutomaticSave.Path); Assert.True(File.Exists(first.Path));
        settings.AutomaticDirectory = "";
        var retried = await archives.SaveAutomaticAsync("退出白板");
        Assert.NotNull(retried); Assert.Null(archives.LastError);
        var previous = await File.ReadAllBytesAsync(retried.Path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => board.SaveBoardAsync(retried.Path, cancellation.Token));
        Assert.Equal(previous, await File.ReadAllBytesAsync(retried.Path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ScheduledSaveSkipsEmptyBoardsAndUsesConfiguredInterval()
    {
        var board = Board(); var settings = Settings(); settings.AutoSaveEnabled = true; settings.AutoSaveMinutes = 2;
        using var archives = Service(board, settings);
        var due = archives.NextSaveUtc!.Value;
        await archives.SaveIfDueAsync(due.AddSeconds(-1)); Assert.Null(archives.LastSave);
        await archives.SaveIfDueAsync(due.AddSeconds(1)); Assert.Null(archives.LastSave);
        Draw(board); await archives.SaveIfDueAsync(archives.NextSaveUtc!.Value.AddSeconds(1));
        Assert.NotNull(archives.LastAutomaticSave);
        settings.AutoSaveEnabled = false; archives.Configure();
        Assert.Null(archives.NextSaveUtc);
    }

    [Fact]
    public async Task SnapshotServiceCapturesBeforeEditingAndCreatesUniqueNames()
    {
        var board = Board(); Draw(board);
        var saves = new AutoSaveService(new() { AutoSavedStrokesLocation = _directory });
        var pending = saves.SaveSnapshotAsync(board.Current.Document.Strokes, "clear");
        board.Current.Document.Clear();
        string path = await pending;
        Assert.Single(InkFileStore.Load(path));
        var another = await saves.SaveSnapshotAsync(InkFileStore.Load(path), "clear");
        Assert.NotEqual(path, another); Assert.Null(saves.LastError);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task EditsDuringAnArchiveRemainPendingAndASinglePageSaveDoesNotMarkTheBoardSaved()
    {
        var board = Board(); Draw(board);
        board.AddMedia(new() { Data = new byte[4 * 1024 * 1024] });
        var settings = Settings(); using var archives = Service(board, settings);
        var pending = archives.SaveAutomaticAsync("定时保存");
        Draw(board, 80);
        var snapshot = await pending;
        Assert.True(archives.HasUnsavedChanges);
        await archives.SaveManualAsync(board, Path.Combine(_directory, "page.icink"), entireBoard: false);
        Assert.True(archives.HasUnsavedChanges);
        var final = await archives.SaveAutomaticAsync("退出白板");
        Assert.NotNull(final); Assert.False(archives.HasUnsavedChanges);
        var old = Board(); await old.LoadBoardAsync(snapshot.Path);
        Assert.Single(old.Current.Document.Strokes);
        var restored = Board(); await restored.LoadBoardAsync(final.Path);
        Assert.Equal(2, restored.Current.Document.Count);
    }

    public void Dispose()
    {
        foreach (var board in _boards) foreach (var page in board.Pages) page.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
