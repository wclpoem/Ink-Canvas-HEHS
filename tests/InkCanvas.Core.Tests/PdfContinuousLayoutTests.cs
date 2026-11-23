using System.Numerics;
using InkCanvas.Pdf;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfContinuousLayoutTests
{
    private static PdfPageGeometry Page(int rotation = 0) => new(20, 30, 620, 830, rotation);

    [Fact]
    public void RotatedAndPortraitPagesAreCenteredWithGaps()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page(90), Page(180) });
        Assert.True(layout[0].Left > 0);
        Assert.Equal(0, layout[1].Left);
        Assert.Equal(24, layout[1].Top - layout[0].Bottom);
        Assert.Equal(24, layout[2].Top - layout[1].Bottom);
        Assert.Equal(layout.Height, layout[2].Bottom);
        Assert.Equal(layout.Width / 2, layout[0].Center.X);
    }

    [Fact]
    public void ContinuousPageSwitchPreservesExactDocumentViewAndInkCoordinates()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page(90), Page() });
        var document = Matrix3x2.CreateScale(1.25f) * Matrix3x2.CreateTranslation(-150, -1000);
        for (int index = 0; index < layout.Count; index++)
        {
            var page = layout.PageView(index, document);
            Assert.Equal(document, layout.DocumentView(index, page));
            var local = new Vector2(123, 456);
            var expected = Vector2.Transform(local + new Vector2(layout[index].Left, layout[index].Top), document);
            Assert.True(Vector2.Distance(expected, Vector2.Transform(local, page)) < .001f);
        }
    }

    [Fact]
    public void VisibilityCrossesPageBoundaryAndRejectsGaps()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page() });
        Assert.Equal(new[] { 0, 1 }, layout.VisiblePages(layout[0].Bottom - 100, layout[1].Top + 100));
        Assert.Equal(-1, layout.HitTest(new(layout.Width / 2, layout[0].Bottom + 12)));
        Assert.Equal(1, layout.HitTest(layout[1].Center));
        Assert.Equal(-1, layout.HitTest(new(-5, layout[0].Center.Y)));
    }

    [Fact]
    public void LongDocumentOnlyEnumeratesVisiblePages()
    {
        var layout = new PdfContinuousLayout(Enumerable.Repeat(Page(), 10000).ToArray());
        Assert.Equal(9999, layout.NearestPage(float.MaxValue));
        Assert.Equal(0, layout.NearestPage(-500));
        Assert.Equal(new[] { 8000, 8001 }, layout.VisiblePages(layout[8000].Top, layout[8001].Bottom));
    }

    [Fact]
    public void PrefetchPrioritizesVisiblePagesAndTravelDirectionWithFixedLookAhead()
    {
        var layout = new PdfContinuousLayout(Enumerable.Repeat(Page(), 10000).ToArray());
        float top = layout[8000].Top, bottom = layout[8001].Bottom;
        Assert.Equal(new[] { 8000, 8001, 8002, 7999, 8003, 8004 }, layout.PreviewPages(top, bottom, 1));
        Assert.Equal(new[] { 8000, 8001, 7999, 8002, 7998, 7997 }, layout.PreviewPages(top, bottom, -1));
        Assert.Equal(new[] { 8000, 8001, 8002, 7999 }, layout.PreviewPages(top, bottom, 0));
        var edge = layout.PreviewPages(layout[9999].Top, float.MaxValue, 1).ToArray();
        Assert.Equal(new[] { 9999, 9998 }, edge);
    }
}
