using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using InkCanvas.Platform.System;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ScreenArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("inkcanvas-screen-archive-").FullName;
    private readonly BoardService _source = new();
    private ArchiveSettings Settings() => new() { DefaultDirectory = _directory, UseDateFolders = false,
        ScreenCapture = new() { Enabled = true, IncludeOrdinaryAnnotation = true } };
    private static InkStroke Stroke(float x, float y, long? layer = null)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 8, TaperKind = TaperKind.None }) { LayerOrder = layer };
        stroke.AddPoint(x, y); stroke.AddPoint(x + 60, y + 30); stroke.Complete(); return stroke;
    }

    [Fact]
    public void OrdinaryAnnotationHasItsOwnOptInAndRequiresActualInk()
    {
        var settings = Settings(); settings.ScreenCapture.IncludeOrdinaryAnnotation = false;
        using var archives = new ScreenArchiveService(_source, () => settings, _directory);
        _source.AddImage(new() { Data = new byte[] { 1 }, Width = 10, Height = 10 });
        Assert.Null(Prepare(true));
        _source.Current.Document.Add(Stroke(10, 20));
        Assert.Null(Prepare(false)); Assert.NotNull(Prepare(true));
        settings.ScreenCapture.IncludeOrdinaryAnnotation = true;
        Assert.NotNull(Prepare(false));
        settings.ScreenCapture.Enabled = false;
        Assert.Null(Prepare(true)); Assert.Null(Prepare(false));

        ScreenArchiveCapture Prepare(bool presentation) => archives.Prepare(new(0, 0, 1920, 1080), Vector2.Zero, 1,
            Matrix3x2.Identity, presentation, "test");
    }

    [Theory]
    [InlineData(1f, 0, 0)]
    [InlineData(1.5f, -1920, 120)]
    [InlineData(2f, -3840, -1080)]
    public async Task ScreenshotAlignsWithZoomedRotatedInkAndPreservesOffscreenObjects(float dpi, int screenX, int screenY)
    {
        var settings = Settings(); using var archives = new ScreenArchiveService(_source, () => settings, _directory);
        var offscreen = Stroke(-900, -600, long.MinValue);
        _source.Current.Document.Add(offscreen);
        var image = new BoardImageAsset { Data = new byte[] { 1, 2 }, Left = 3000, Top = 1600, Width = 20, Height = 20, LayerOrder = long.MinValue };
        _source.SetImagePresent(_source.Current, image, true);
        _source.AddMedia(new() { Data = new byte[] { 3, 4 }, Left = -1400, Top = 100 });
        var view = Matrix3x2.CreateScale(1.4f) * Matrix3x2.CreateRotation(.2f) * Matrix3x2.CreateTranslation(150, 80);
        var origin = new Vector2(-4000, -1500);
        var capture = archives.Prepare(new(screenX, screenY, 1920, 1080), origin, dpi, view, false, "切换鼠标模式");
        var page = ScreenArchiveService.CreatePage(capture, new byte[] { 137, 80, 78, 71 });
        try
        {
            var background = page.CurrentImages.Single(item => item.Name == "屏幕截图背景");
            var position = Vector2.Transform(Vector2.Zero, BoardElementMath.RenderMatrix(background.Left, background.Top, background.Transform) * page.Current.ViewTransform);
            Assert.InRange(Vector2.Distance(position, (new Vector2(screenX, screenY) - origin) / dpi), 0, .002);
            Assert.Equal(1920 / dpi, background.Width); Assert.Equal(1080 / dpi, background.Height);
            Assert.Equal(0, background.LayerOrder); Assert.All(page.Current.DrawingObjects.Skip(1), item => Assert.True(item.LayerOrder > 0));
            Assert.Equal(offscreen.Points, page.Current.Document.Strokes.Single().Points);
            Assert.Equal(long.MinValue, offscreen.LayerOrder);
            Assert.Equal(3000, page.CurrentImages.Single(item => item.Id == image.Id).Left);
            Assert.Equal(new byte[] { 3, 4 }, page.CurrentMedia.Single().Data);
            string path = Path.Combine(_directory, "screen.icink"); await page.SaveBoardAsync(path);
            var restored = new BoardService();
            try
            {
                await restored.LoadBoardAsync(path);
                Assert.Equal(1, restored.PageCount); Assert.Equal(view, restored.Current.ViewTransform);
                Assert.Equal(offscreen.Points, restored.Current.Document.Strokes.Single().Points);
                Assert.Equal(2, restored.CurrentImages.Count); Assert.Single(restored.CurrentMedia);
            }
            finally { foreach (var controller in restored.Pages) controller.Dispose(); }
        }
        finally { foreach (var controller in page.Pages) controller.Dispose(); }
    }

    [Fact]
    public void CaptureKeepsItsOwnObjectsWhenTheSourceIsClearedOrReplaced()
    {
        var settings = Settings(); using var archives = new ScreenArchiveService(_source, () => settings, _directory);
        _source.Current.Document.Add(Stroke(10, 20));
        var prepared = archives.Prepare(new(0, 0, 800, 600), Vector2.Zero, 1, Matrix3x2.Identity, false, "清屏前存档");
        var points = prepared.Strokes.Single().Points.ToArray();
        _source.Current.Document.Strokes.Single().Transform(Matrix3x2.CreateTranslation(1000, 2000));
        _source.ClearCurrentPage();
        Assert.Equal(points, prepared.Strokes.Single().Points);
        Assert.Empty(_source.Current.Document.Strokes);
    }

    [Fact]
    public void PeriodicCaptureUsesItsIndependentIntervalAndDefersWhileDisabled()
    {
        var settings = Settings(); settings.ScreenCapture.IntervalMinutes = 3;
        settings.ScreenCapture.IncludeOrdinaryAnnotation = false;
        using var archives = new ScreenArchiveService(_source, () => settings, _directory);
        var due = archives.NextSaveUtc!.Value;
        Assert.False(archives.TakeScheduledSave(true, due.AddSeconds(-1)));
        Assert.False(archives.TakeScheduledSave(false, due.AddSeconds(1)));
        Assert.True(archives.TakeScheduledSave(true, due.AddSeconds(1)));
        Assert.Equal(due.AddSeconds(1).AddMinutes(3), archives.NextSaveUtc);
        Assert.Equal(Path.Combine(_directory, "ScreenArchives"), ScreenArchiveService.DirectoryFor(settings));
        settings.ScreenCapture.Directory = Path.Combine(_directory, "custom");
        Assert.Equal(settings.ScreenCapture.Directory, ScreenArchiveService.DirectoryFor(settings));
    }

    [Fact]
    public async Task BoardAndScreenRetentionAreIndependentEvenInTheSameDirectory()
    {
        var settings = Settings(); settings.AutomaticDirectory = Path.Combine(_directory, "shared"); settings.AutomaticCopiesToKeep = 1;
        _source.Current.Document.Add(Stroke(10, 10));
        using var archives = new BoardArchiveService(_source, () => settings, _directory);
        var first = await archives.SaveAutomaticAsync("board");
        var screenMetadata = new BoardArchiveMetadata { Kind = "screen", Automatic = true, SavedAtUtc = DateTimeOffset.UtcNow };
        string screenPath = Path.Combine(settings.AutomaticDirectory, "screen.icink");
        await _source.SaveBoardArchiveAsync(screenPath, screenMetadata);
        _source.Current.Document.Add(Stroke(50, 60));
        await archives.SaveAutomaticAsync("board");
        Assert.False(File.Exists(first.Path)); Assert.True(File.Exists(screenPath));
    }

    [Fact]
    public void SettingsRoundTripPreservesIndependentScreenArchiveChoices()
    {
        var store = new SettingsStore(_directory);
        var settings = new AppSettings();
        settings.Archive.ScreenCapture = new() { Enabled = true, IncludeOrdinaryAnnotation = true, SaveOnMouseMode = false,
            SaveOnClear = true, SaveOnExit = false, Directory = Path.Combine(_directory, "screens"), IntervalMinutes = 8, CopiesToKeep = 12 };
        store.Save(settings);
        var loaded = store.Load().Archive.ScreenCapture;
        Assert.True(loaded.Enabled); Assert.True(loaded.IncludeOrdinaryAnnotation); Assert.False(loaded.SaveOnMouseMode);
        Assert.False(loaded.SaveOnExit); Assert.Equal(8, loaded.IntervalMinutes); Assert.Equal(12, loaded.CopiesToKeep);
        Assert.Equal(settings.Archive.ScreenCapture.Directory, loaded.Directory);
        Assert.False(new AppSettings().Archive.ScreenCapture.Enabled);
        Assert.False(new AppSettings().Archive.ScreenCapture.IncludeOrdinaryAnnotation);
    }

    public void Dispose()
    {
        foreach (var page in _source.Pages) page.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
