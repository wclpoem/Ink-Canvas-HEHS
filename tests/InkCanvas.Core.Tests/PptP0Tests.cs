using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using System;
using System.IO;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PptP0Tests : IDisposable
{
    private readonly string _tempDir;

    public PptP0Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "inkcanvas-ppt-p0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private string NewFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static PptPresentationInfo Info(string path, int slideCount = 3) => new(
        "com-identity", "deck.pptx", path, path is { Length: > 0 }, slideCount,
        960, 540, "PowerPoint.Application");

    private static PptPresentationInfo NoPath(string name, int slideCount = 3) => new(
        "com-identity", name, string.Empty, false, slideCount, 960, 540, "PowerPoint.Application");

    private static PptSlideInfo Slide(int id, int index) => new(id, index, "slide", null, null, null);

    private static PptStoredSlide Stored(int id, int index) =>
        new(id, index, Array.Empty<InkStroke>());

    private static PptInkArchive Archive(int slideCount, params PptStoredSlide[] slides) => new(
        new PptPresentationInfo("com", "deck.pptx", string.Empty, false, slideCount,
            960, 540, "PowerPoint.Application"),
        null, null, slides);

    [Fact]
    public void Hash_Is32LowercaseHex()
    {
        string hash = PresentationIdentity.Hash("hello");
        Assert.Equal(32, hash.Length);
        Assert.Matches("^[0-9a-f]{32}$", hash);
        Assert.NotEqual(hash, PresentationIdentity.Hash("hello!"));
    }

    [Fact]
    public void NormalizePath_IsCaseInsensitive()
    {
        Assert.Equal(
            PresentationIdentity.NormalizePath(@"C:\Temp\Deck.PPTX"),
            PresentationIdentity.NormalizePath(@"c:\temp\deck.pptx"));
    }

    [Fact]
    public void BuildKey_NullOrEmptyPresentation_IsUnknown()
    {
        Assert.Equal("unknown", PresentationIdentity.BuildKey(null));
        Assert.Equal("unknown", PresentationIdentity.BuildKey(
            new PptPresentationInfo(string.Empty, string.Empty, string.Empty, false, 0, 0, 0, string.Empty)));
    }

    [Fact]
    public void BuildKey_ForExistingFile_IsStable()
    {
        string path = NewFile("stable.pptx", "payload");
        string first = PresentationIdentity.BuildKey(Info(path));
        string second = PresentationIdentity.BuildKey(Info(path));
        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void BuildKey_ChangesWhenFileSizeChanges()
    {
        string path = NewFile("size.pptx", "a");
        string before = PresentationIdentity.BuildKey(Info(path));
        File.WriteAllText(path, "abcdef");
        string after = PresentationIdentity.BuildKey(Info(path));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void BuildKey_ChangesWhenLastWriteTimeChanges()
    {
        string path = NewFile("mtime.pptx", "same");
        string before = PresentationIdentity.BuildKey(Info(path));
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        string after = PresentationIdentity.BuildKey(Info(path));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void BuildKey_WithoutPath_DependsOnNameAndSlideCount()
    {
        var baseDeck = NoPath("deck", 5);
        Assert.NotEqual(PresentationIdentity.BuildKey(baseDeck),
            PresentationIdentity.BuildKey(NoPath("other", 5)));
        Assert.NotEqual(PresentationIdentity.BuildKey(baseDeck),
            PresentationIdentity.BuildKey(NoPath("deck", 6)));
        Assert.Equal(PresentationIdentity.BuildKey(baseDeck),
            PresentationIdentity.BuildKey(NoPath("deck", 5)));
    }

    [Fact]
    public void BuildKey_MissingFile_UsesNormalizedPath()
    {
        string path = Path.Combine(_tempDir, "does-not-exist.pptx");
        Assert.Equal(
            PresentationIdentity.BuildKey(Info(path)),
            PresentationIdentity.BuildKey(Info(path.ToUpperInvariant())));
        Assert.Equal(32, PresentationIdentity.BuildKey(Info(path)).Length);
    }

    [Fact]
    public void Inspect_NullArchive_HasNoWarning()
    {
        var issue = SlideMappingInspector.Inspect(null, new[] { Slide(1, 1) });
        Assert.False(issue.NeedsWarning);
        Assert.False(issue.CountChanged);
        Assert.False(issue.PageOrderChanged);
        Assert.Empty(issue.MissingSlideIds);
    }

    [Fact]
    public void Inspect_MissingSlideId_IsReported()
    {
        var issue = SlideMappingInspector.Inspect(
            Archive(2, Stored(1, 1), Stored(2, 2)),
            new[] { Slide(1, 1), Slide(3, 2) });

        Assert.Contains(2, issue.MissingSlideIds);
        Assert.True(issue.NeedsWarning);
        Assert.False(issue.PageOrderChanged);
        Assert.False(issue.CountChanged);
    }

    [Fact]
    public void Inspect_ReorderedSlides_IsReported()
    {
        var issue = SlideMappingInspector.Inspect(
            Archive(2, Stored(1, 1), Stored(2, 2)),
            new[] { Slide(2, 1), Slide(1, 2) });

        Assert.True(issue.PageOrderChanged);
        Assert.False(issue.CountChanged);
        Assert.Empty(issue.MissingSlideIds);
    }

    [Fact]
    public void Inspect_ChangedSlideCount_IsReported()
    {
        var issue = SlideMappingInspector.Inspect(
            Archive(1, Stored(1, 1)),
            new[] { Slide(1, 1), Slide(2, 2) });

        Assert.True(issue.CountChanged);
        Assert.False(issue.PageOrderChanged);
        Assert.True(issue.NeedsWarning);
    }

    [Fact]
    public void Inspect_NullInputs_AreSafe()
    {
        Assert.False(SlideMappingInspector.Inspect(null, null).NeedsWarning);
        Assert.True(SlideMappingInspector.Inspect(Archive(2, Stored(1, 1)), null).NeedsWarning);
        var byDefault = default(SlideMappingIssue);
        Assert.False(byDefault.NeedsWarning);
    }

    [Fact]
    public void Guard_FirstEnterSucceeds_AndRejectsWhileHeld()
    {
        var guard = new NavigationGuard();
        Assert.True(guard.TryEnter(1_000));
        Assert.False(guard.TryEnter(5_000));
    }

    [Fact]
    public void Guard_RejectsWithinInterval_AllowsAfter()
    {
        var guard = new NavigationGuard(180, 350);
        Assert.True(guard.TryEnter(1_000));
        guard.Exit(1_000);

        Assert.False(guard.TryEnter(1_100));
        Assert.True(guard.TryEnter(1_180));
        Assert.Equal(1_000, guard.LastExitMs);
    }

    [Fact]
    public void Guard_ReportsRetryDelayInsideInterval()
    {
        var guard = new NavigationGuard(180, 350);
        Assert.True(guard.TryEnter(1_000, out long acceptedRetry));
        Assert.Equal(0, acceptedRetry);
        guard.Exit(1_000);

        Assert.False(guard.TryEnter(1_060, out long retryAfterMs));
        Assert.Equal(120, retryAfterMs);
    }

    [Fact]
    public void Guard_HeldRequestHasNoRetryDelay()
    {
        var guard = new NavigationGuard(180, 350);
        Assert.True(guard.TryEnter(1_000, out _));
        Assert.False(guard.TryEnter(5_000, out long retryAfterMs));
        Assert.Equal(0, retryAfterMs);
    }

    [Fact]
    public void Guard_SuppressExternal_BlocksUntilExpiry()
    {
        var guard = new NavigationGuard(180, 350);
        guard.SuppressExternal(10_000);

        Assert.True(guard.IsSuppressed(10_000));
        Assert.True(guard.IsSuppressed(10_350));
        Assert.False(guard.IsSuppressed(10_351));
    }

    [Fact]
    public void Gate_OldGeneration_IsNotCurrent()
    {
        var gate = new SlideSwitchGate();
        gate.BeginSession();
        int first = gate.NextGeneration();
        Assert.True(gate.IsCurrent(gate.Session, first));

        int second = gate.NextGeneration();
        Assert.NotEqual(first, second);
        Assert.False(gate.IsCurrent(gate.Session, first));
        Assert.True(gate.IsCurrent(gate.Session, second));
    }

    [Fact]
    public void Gate_BeginSession_InvalidatesOldSession()
    {
        var gate = new SlideSwitchGate();
        gate.BeginSession();
        int oldSession = gate.Session;
        int generation = gate.NextGeneration();

        gate.BeginSession();
        Assert.NotEqual(oldSession, gate.Session);
        Assert.Equal(0, gate.Generation);
        Assert.False(gate.IsCurrent(oldSession, generation));
        Assert.True(gate.IsCurrent(gate.Session, 0));
    }

    [Fact]
    public void Layout_BottomLeft_PlacesInsideTopLeft()
    {
        var result = NavigationLayout.Place(
            NavigationCorner.BottomLeft, new ScreenBox(0, 0, 1920, 1080), 310, 50, 8,
            new ScreenBox(650, 1022, 620, 50));

        Assert.Equal(8, result.X);
        Assert.Equal(1022, result.Y);
        Assert.Equal(310, result.Width);
        Assert.Equal(50, result.Height);
        Assert.Equal(NavigationCorner.BottomLeft, result.Corner);
    }

    [Fact]
    public void Layout_SideRight_PlacesRightCentered()
    {
        var result = NavigationLayout.Place(
            NavigationCorner.SideRight, new ScreenBox(0, 0, 1920, 1080), 50, 310, 8);

        Assert.Equal(1920 - 50 - 8, result.X);
        Assert.Equal((1080 - 310) / 2, result.Y);
        Assert.Equal(50, result.Width);
        Assert.Equal(310, result.Height);
    }

    [Fact]
    public void Layout_NextCorner_CyclesAllFour()
    {
        var corner = NavigationCorner.BottomLeft;
        corner = NavigationLayout.NextCorner(corner);
        Assert.Equal(NavigationCorner.BottomRight, corner);
        corner = NavigationLayout.NextCorner(corner);
        Assert.Equal(NavigationCorner.SideRight, corner);
        corner = NavigationLayout.NextCorner(corner);
        Assert.Equal(NavigationCorner.SideLeft, corner);
        Assert.Equal(NavigationCorner.BottomLeft, NavigationLayout.NextCorner(corner));
    }

    [Fact]
    public void Layout_ClampsToSmallShowBounds()
    {
        var bounds = new ScreenBox(100, 200, 100, 100);
        var result = NavigationLayout.Place(NavigationCorner.BottomRight, bounds, 620, 100, 16);

        Assert.True(result.Width <= bounds.Width);
        Assert.True(result.Height <= bounds.Height);
        Assert.InRange(result.X, bounds.X, bounds.X + bounds.Width - result.Width);
        Assert.InRange(result.Y, bounds.Y, bounds.Y + bounds.Height - result.Height);
    }

    [Theory]
    [InlineData(NavigationCorner.BottomLeft)]
    [InlineData(NavigationCorner.BottomRight)]
    public void Layout_NarrowReadingView_MovesAboveToolbar(NavigationCorner corner)
    {
        var result = NavigationLayout.Place(corner, new ScreenBox(-900, 120, 900, 600),
            310, 50, 8, new ScreenBox(-750, 662, 600, 50));

        Assert.Equal(604, result.Y);
        Assert.InRange(result.X, -900, -310);
    }

    [Fact]
    public void Layout_AlignsToMovedToolbarOnSecondaryDisplay()
    {
        var result = NavigationLayout.Place(NavigationCorner.BottomRight,
            new ScreenBox(-2560, -180, 2560, 1440), 620, 100, 16,
            new ScreenBox(-1800, 900, 1000, 100));

        Assert.Equal(-636, result.X);
        Assert.Equal(900, result.Y);
    }
}
