using System.Reflection;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.P0.Tests;

public sealed class ReviewClearedInkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ink-pr5-cleared-" + Guid.NewGuid().ToString("N"));
    private readonly PptPresentationInfo _presentation = new("review", "review.pptx", "", false, 2, 960, 540, "PowerPoint.Application");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void SavingAnExplicitlyEmptySlideDoesNotRestoreItsOldInk()
    {
        var store = new PptInkFileStore(_directory);
        SaveInitial(store);
        store.Save(new PptInkArchive(_presentation, 1, 1, new[] { new PptStoredSlide(256, 1, Array.Empty<InkStroke>()) }));
        var reopened = store.Load(_presentation);
        Assert.Empty(reopened.Slides.Single(item => item.SlideId == 256).Strokes);
        Assert.Single(reopened.Slides.Single(item => item.SlideId == 257).Strokes);
    }

    [Fact]
    public void ClearingThePresentationSurvivesReopening()
    {
        var store = new PptInkFileStore(_directory);
        SaveInitial(store);
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop, store);
        // Exercise the real archive/session transition without opening or changing a user's deck.
        var snapshot = new PptSnapshot { State = PptConnectionState.Editing, Presentation = _presentation };
        typeof(PptInkSession).GetMethod("ProcessTransition", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, new object[] { snapshot });
        session.ClearPresentationInk();
        Assert.All(store.Load(_presentation).Slides, item => Assert.Empty(item.Strokes));
    }

    private void SaveInitial(PptInkFileStore store)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
        stroke.AddPoint(0, 0); stroke.AddPoint(20, 20); stroke.Complete();
        store.Save(new PptInkArchive(_presentation, 1, 1, new[] {
            new PptStoredSlide(256, 1, new[] { stroke }), new PptStoredSlide(257, 2, new[] { stroke.Clone(false) }) }));
    }
}
