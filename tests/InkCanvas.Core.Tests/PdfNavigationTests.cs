using System.Numerics;
using InkCanvas.Pdf;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfNavigationTests
{
    [Fact]
    public void SmallDocumentIsCenteredWithoutPhantomScrollRange()
    {
        var bounds = PdfScrollBounds.Create(new(600, 800), new(1000, 1000), .5f);
        Assert.Equal(Vector2.Zero, bounds.Extent);
        Assert.Equal(new Vector2(350, 300), bounds.Clamp(new(-10000, 10000)));
        Assert.Equal(Vector2.Zero, bounds.Offset(new(350, 300)));
    }

    [Fact]
    public void BothScrollBarsReachDocumentEdgesAndRoundTripAtAnyZoom()
    {
        foreach (float scale in new[] { .02f, .5f, 1f, 3f, 8f })
        {
            var bounds = PdfScrollBounds.Create(new(800, 120000), new(600, 900), scale);
            var offset = bounds.Extent * .37f;
            Assert.True(Vector2.Distance(offset, bounds.Offset(bounds.Translation(offset))) < .02f);
            Assert.Equal(bounds.Minimum, bounds.Translation(bounds.Extent));
            Assert.Equal(bounds.Maximum, bounds.Translation(Vector2.Zero));
            Assert.Equal(bounds.Minimum, bounds.Clamp(new(-1e9f)));
            Assert.Equal(bounds.Maximum, bounds.Clamp(new(1e9f)));
        }
    }

    [Fact]
    public void InertiaUsesElapsedTimeAcrossSixtyAndHighRefreshDisplays()
    {
        Vector2 Travel(int rate)
        {
            var motion = MovingTouch();
            Assert.True(motion.Start(.08));
            var distance = Vector2.Zero;
            for (int frame = 0; frame < rate / 2; frame++) distance += motion.Advance(1d / rate);
            return distance;
        }
        Assert.True(Vector2.Distance(Travel(60), Travel(144)) < .002f);
        Assert.True(Travel(120).Y < -60);
    }

    [Fact]
    public void StationaryFingerAndStaleReleaseDoNotFling()
    {
        var motion = new PdfScrollInertia();
        motion.Reset(Vector2.Zero, 0);
        for (int sample = 1; sample <= 10; sample++) motion.Observe(Vector2.Zero, sample * .01);
        Assert.False(motion.Start(.1));
        motion = MovingTouch();
        Assert.False(motion.Start(.3));
    }

    [Fact]
    public void EdgeStopsOnlyBlockedAxisAndNewContactDiscardsMomentum()
    {
        var motion = MovingTouch();
        Assert.True(motion.Start(.08));
        var requested = motion.Advance(.016);
        motion.Constrain(requested, new(0, requested.Y));
        var next = motion.Advance(.016);
        Assert.Equal(0, next.X);
        Assert.True(next.Y < 0);
        motion.Reset(new(20, -100), 1);
        Assert.False(motion.IsRunning);
        Assert.False(motion.Start(1));
        Assert.Equal(Vector2.Zero, motion.Advance(.016));
    }

    [Fact]
    public void InertiaFinishesWithoutAnIdleRenderLoop()
    {
        var motion = MovingTouch();
        Assert.True(motion.Start(.08));
        for (int frame = 0; frame < 240 && motion.IsRunning; frame++) motion.Advance(1d / 120);
        Assert.False(motion.IsRunning);
        Assert.Equal(Vector2.Zero, motion.Advance(1d / 120));
    }

    [Fact]
    public void CompositionProjectionMatchesFrameBasedScrollAndCannotExtendMomentum()
    {
        var motion = MovingTouch();
        Assert.True(motion.Start(.08));
        double remaining = motion.RemainingSeconds;
        var expected = motion.Project(.25);
        var distance = Vector2.Zero;
        for (int frame = 0; frame < 30; frame++) distance += motion.Advance(1d / 120);
        Assert.True(Vector2.Distance(expected, distance) < .002f);
        Assert.True(Math.Abs(motion.RemainingSeconds - (remaining - .25)) < .002);
        motion.Stop();
        Assert.Equal(0, motion.RemainingSeconds);
        Assert.Equal(Vector2.Zero, motion.Project(1));
    }

    private static PdfScrollInertia MovingTouch()
    {
        var motion = new PdfScrollInertia();
        motion.Reset(Vector2.Zero, 0);
        for (int sample = 1; sample <= 8; sample++) motion.Observe(new(5 * sample, -10 * sample), sample * .01);
        return motion;
    }
}
