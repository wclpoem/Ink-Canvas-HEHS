using InkCanvas.Core.Ink;
using System.Numerics;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// Regression tests for the stroke-taper flicker.
/// <para>
/// The WPF original applied the taper in <c>StrokeCollected</c> (after pen-up) and replaced
/// the point collection, so the rendered width jumped on lift. These tests pin the two
/// properties that make that impossible: the committed geometry equals the last live frame,
/// and no already-drawn point ever gets narrower.
/// </para>
/// </summary>
public class TaperTests
{
    private static InkStroke NewStroke(TaperKind kind, float width = 8f)
    {
        var attrs = new StrokeAttributes { BaseWidth = width, TaperKind = kind, Color = InkColor.Black };
        return new InkStroke(attrs);
    }

    /// <summary>Draw a diagonal stroke one sample at a time, as real input would.</summary>
    private static void DrawSamples(InkStroke s, int count, float step = 3f)
    {
        for (int i = 0; i < count; i++)
            s.AddPoint(100 + i * step, 100 + i * step * 0.6f, 0.5f, i * 8f);
    }

    [Theory]
    [InlineData(TaperKind.None)]
    [InlineData(TaperKind.Tail)]
    [InlineData(TaperKind.Velocity)]
    [InlineData(TaperKind.Natural)]
    public void LastLiveFrameEqualsCommittedGeometry(TaperKind kind)
    {
        var s = NewStroke(kind);
        DrawSamples(s, 60);

        // Geometry exactly as rendered on the final live frame, before pen-up.
        float[] widthsBefore = s.Points.Select(p => p.Width).ToArray();
        Vector2[] outlineBefore = StrokeGeometry.BuildOutline(s);

        s.Complete();

        float[] widthsAfter = s.Points.Select(p => p.Width).ToArray();
        Vector2[] outlineAfter = StrokeGeometry.BuildOutline(s);

        Assert.Equal(widthsBefore, widthsAfter);
        Assert.Equal(outlineBefore, outlineAfter);
    }

    [Theory]
    [InlineData(TaperKind.None)]
    [InlineData(TaperKind.Tail)]
    [InlineData(TaperKind.Velocity)]
    [InlineData(TaperKind.Natural)]
    [InlineData(TaperKind.InkWash)]
    public void NoPointEverGetsNarrower(TaperKind kind)
    {
        var s = NewStroke(kind);
        var highWater = new List<float>();
        for (int i = 0; i < 80; i++)
        {
            s.AddPoint(100 + i * 2.5f, 120 + MathF.Sin(i * 0.3f) * 20f, 0.5f, i * 8f);
            if (highWater.Count < s.Count) highWater.Add(0f);
            for (int j = 0; j < s.Count; j++)
            {
                float w = s.Points[j].Width;
                Assert.True(w >= highWater[j] - 1e-4f,
                    $"point {j} shrank from {highWater[j]} to {w} when sample {i} arrived");
                highWater[j] = MathF.Max(highWater[j], w);
            }
        }
    }

    [Fact]
    public void TailIsAlreadyTaperedWhileStillDrawing()
    {
        var s = NewStroke(TaperKind.Tail, 10f);
        DrawSamples(s, 40);

        // The taper must exist BEFORE pen-up — that is the whole point of the fix.
        float tip = s.Points[^1].Width;
        float middle = s.Points[10].Width;
        Assert.True(tip < middle, $"tip {tip} should be thinner than settled middle {middle}");
        Assert.True(middle > 9.9f, $"settled region should be at full width, was {middle}");
    }

    [Fact]
    public void VelocityTaperDoesNotRewriteEarlierPoints()
    {
        var s = NewStroke(TaperKind.Velocity);
        DrawSamples(s, 30);
        float[] snapshot = s.Points.Select(p => p.Width).ToArray();

        // Continue with a much faster run; earlier widths must be untouched.
        for (int i = 30; i < 60; i++)
            s.AddPoint(100 + i * 14f, 100 + i * 9f, 0.5f, i * 8f);

        for (int j = 0; j < snapshot.Length; j++)
            Assert.Equal(snapshot[j], s.Points[j].Width);
    }

    [Fact]
    public void NoneTaperIsUniform()
    {
        var s = NewStroke(TaperKind.None, 6f);
        DrawSamples(s, 25);
        Assert.All(s.Points, p => Assert.Equal(6f, p.Width));
    }

    [Fact]
    public void HighlighterNeverTapers()
    {
        var attrs = new StrokeAttributes { BaseWidth = 24f, IsHighlighter = true, TaperKind = TaperKind.Tail };
        var s = new InkStroke(attrs);
        DrawSamples(s, 30);
        Assert.Equal(TaperKind.None, s.TaperKind);
        Assert.All(s.Points, p => Assert.Equal(24f, p.Width));
    }

    [Fact]
    public void OutlineIsDeterministic()
    {
        var a = NewStroke(TaperKind.Tail);
        var b = NewStroke(TaperKind.Tail);
        DrawSamples(a, 45);
        DrawSamples(b, 45);
        Assert.Equal(StrokeGeometry.BuildOutline(a), StrokeGeometry.BuildOutline(b));
    }

    [Fact]
    public void SinglePointProducesAClosedDot()
    {
        var s = NewStroke(TaperKind.Tail, 8f);
        s.AddPoint(50, 50);
        var outline = StrokeGeometry.BuildOutline(s);
        Assert.True(outline.Length >= 8);
        Assert.All(outline, v => Assert.InRange(Vector2.Distance(v, new Vector2(50, 50)), 0.1f, 8f));
    }
}
