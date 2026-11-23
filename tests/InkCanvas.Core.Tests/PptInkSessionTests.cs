using System.Reflection;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PptInkSessionTests
{
    [Fact]
    public void RecreatedOfficeProxyDoesNotReloadUnchangedPage()
    {
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop);
        int applications = 0;
        session.ApplyCurrentInk = _ => applications++;
        var first = Snapshot("proxy-one", 1);
        Process(session, first);
        for (int index = 0; index < 20; index++) Process(session, first with
        { Presentation = first.Presentation with { Identity = "proxy-" + index } });
        Assert.Equal(1, applications);
    }

    [Fact]
    public void RealSlideChangeStillRestoresItsOwnInkAfterProxyChanges()
    {
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop);
        IReadOnlyList<InkStroke> current = Array.Empty<InkStroke>();
        session.ApplyCurrentInk = strokes => current = strokes;
        session.CaptureCurrentInk = () => current;
        Process(session, Snapshot("proxy-one", 1));
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(20, 30); stroke.Complete();
        current = new[] { stroke };
        Process(session, Snapshot("proxy-two", 2));
        Assert.Empty(current);
        Process(session, Snapshot("proxy-three", 1));
        Assert.Equal(stroke.Id, Assert.Single(current).Id);
        Assert.NotSame(stroke, current[0]);
    }

    [Fact]
    public void PreviewReadsCurrentAndStoredSlidesWithoutChangingPageOrInk()
    {
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop);
        IReadOnlyList<InkStroke> current = Array.Empty<InkStroke>();
        session.CaptureCurrentInk = () => current;
        session.ApplyCurrentInk = ink => current = ink;
        var first = Snapshot("preview", 1);
        Process(session, first);
        var original = new InkStroke(new StrokeAttributes());
        original.AddPoint(12, 34); original.Complete();
        current = new[] { original };
        var preview = session.CaptureSlideInk(first.Slide.SlideId);
        Assert.Equal(original.Id, Assert.Single(preview).Id);
        Assert.NotSame(original, preview[0]);
        preview[0].Transform(System.Numerics.Matrix3x2.CreateTranslation(500, 500));
        Assert.Equal(12, original.Points[0].X);
        Process(session, Snapshot("preview", 2));
        Assert.Empty(current);
        var stored = session.CaptureSlideInk(first.Slide.SlideId);
        Assert.Equal(original.Id, Assert.Single(stored).Id);
        Assert.Equal(12, stored[0].Points[0].X);
        Assert.Equal(2, session.Snapshot.Slide.Index);
        Assert.Empty(session.CaptureSlideInk(9999));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(801)]
    public void LargeModernArchivesLoadInkOnDemandAndKeepUnreadPagesWhenSaving(int count)
    {
        string directory = Directory.CreateTempSubdirectory("inkcanvas-large-ppt-test-").FullName;
        try
        {
            var presentation = Snapshot("large", 1).Presentation with { SlideCount = count };
            var store = new PptInkFileStore(directory);
            var stroke = new InkStroke(new StrokeAttributes());
            stroke.AddPoint(12, 34); stroke.Complete();
            store.Save(new(presentation, 1, 1, Enumerable.Range(1, count)
                .Select(index => new PptStoredSlide(index + 255, index, new[] { stroke })).ToArray()));
            var metadata = store.LoadForSession(presentation);
            Assert.Equal(count, metadata.Slides.Count);
            Assert.All(metadata.Slides, slide => { Assert.True(slide.IsDeferred); Assert.Empty(slide.Strokes); });
            Assert.All(metadata.Slides, slide => Assert.Equal(1, slide.StoredStrokeCount));
            using var interop = new PptInteropService();
            using var session = new PptInkSession(interop, store);
            IReadOnlyList<InkStroke> current = Array.Empty<InkStroke>();
            session.ApplyCurrentInk = ink => current = ink;
            session.CaptureCurrentInk = () => current;
            var snapshot = Snapshot("large", 1) with { Presentation = presentation };
            Process(session, snapshot);
            Assert.Single(current);
            var indexArchive = session.CreateIndexArchive();
            Assert.Equal(count - 1, indexArchive.Slides.Count(slide => slide.IsDeferred));
            var details = new PptPresentationDetails(presentation,
                Enumerable.Range(1, count).Select(index => new PptSlideInfo(index + 255, index, "", null, null, null)).ToArray(), null);
            Assert.All(SlideContentIndex.Build(details, indexArchive).Slides,
                slide => { Assert.True(slide.HasInk); Assert.Equal(1, slide.InkStrokeCount); });
            current = Array.Empty<InkStroke>();
            session.Save();
            var last = store.TryLoadSlide(presentation, count + 255);
            Assert.Single(last);
            Assert.Empty(store.TryLoadSlide(presentation, 256));
            Assert.Single(session.CaptureSlideInk(count + 255));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DeferredInkUsesManifestFileNameAndClearRemovesUnreadInk()
    {
        string directory = Directory.CreateTempSubdirectory("inkcanvas-deferred-ppt-test-").FullName;
        try
        {
            var presentation = Snapshot("custom-files", 1).Presentation;
            var store = new PptInkFileStore(directory);
            var stroke = new InkStroke(new StrokeAttributes());
            stroke.AddPoint(12, 34); stroke.Complete();
            store.Save(new(presentation, 1, 1, new[] { new PptStoredSlide(257, 2, new[] { stroke }) }));
            string archiveDirectory = store.GetPresentationDirectory(presentation);
            string manifest = Path.Combine(archiveDirectory, "InkCanvas-Ppt.json");
            File.Move(Path.Combine(archiveDirectory, "slide-257.icink"), Path.Combine(archiveDirectory, "custom.icink"));
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("slide-257.icink", "custom.icink"));
            Assert.Single(store.TryLoadSlide(presentation, 257));
            using var interop = new PptInteropService();
            using var session = new PptInkSession(interop, store);
            Process(session, Snapshot("custom-files", 1));
            Assert.Single(session.CaptureSlideInk(257));
            session.ClearPresentationInk();
            Assert.Empty(store.TryLoadSlide(presentation, 257));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static PptSnapshot Snapshot(string identity, int index) => new()
    {
        State = PptConnectionState.SlideShow,
        Presentation = new(identity, "Lesson", "C:\\Lessons\\Lesson.pptx", true, 2, 960, 540, "PowerPoint.Application"),
        Slide = new(index + 255, index, "Slide " + index, false, false, 0),
        WindowHandle = 1,
        ShowPosition = index,
    };

    private static void Process(PptInkSession session, PptSnapshot snapshot)
        => typeof(PptInkSession).GetMethod("ProcessTransition", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(session, new object[] { snapshot });
}
