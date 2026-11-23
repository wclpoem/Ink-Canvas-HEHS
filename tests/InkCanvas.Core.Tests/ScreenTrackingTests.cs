using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Tools;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using InkCanvas.Platform.System;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ScreenTrackingTests
{
    private static ScreenViewSnapshot View(string key = "A", float scroll = 0, float zoom = 1) => new()
    {
        Application = TrackedApplication.Diagnostic, WindowHandle = 42, ContextKey = key,
        Viewport = new Rect(100, 200, 1100, 1000), Scale = zoom, ScrollPixels = new Vector2(0, scroll),
        HasScrollMetrics = true, HasZoomMetrics = true,
    };
    private static ExperimentalSettings Options() => new() { ScreenContentTracking = true, PreserveUnchangedRegions = false };
    private static InkStroke Stroke(float x = 200, float y = 300)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 10, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y); stroke.AddPoint(x + 20, y + 5); stroke.Complete(); return stroke;
    }
    private static void Record(InkController controller, InkStroke stroke)
    {
        controller.Document.Add(stroke);
        controller.History.Push(new DelegateAction("ink", () => controller.Document.Add(stroke), () => controller.Document.Remove(stroke)));
    }
    private static void Near(Vector2 expected, Vector2 actual) => Assert.True(Vector2.Distance(expected, actual) < .01f, $"Expected {expected}, actual {actual}");

    [Fact]
    public void LegacySettingsKeepExperimentDisabledAndRoundTripIndependentOptions()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"canvas\":{\"InkWidth\":4}}");
        Assert.False(old.Experimental.ScreenContentTracking);
        old.Experimental.ScreenContentTracking = true; old.Experimental.FollowZoom = false;
        var saved = old.Clone(); Assert.True(saved.Experimental.ScreenContentTracking); Assert.False(saved.Experimental.FollowZoom);
        saved.ResetToRecommendation(); Assert.False(saved.Experimental.ScreenContentTracking);
        Assert.True(saved.Experimental.RememberPages); Assert.True(saved.Experimental.PreserveUnchangedRegions);
    }

    [Fact]
    public void PowerPointPreviewsPreservePageIdentityAndMapInkIntoSlideCoordinates()
    {
        using var controller = new InkController();
        var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var origin = new Vector2(40, 60);
        var first = View("PowerPoint:42:deck:256", zoom: 2);
        memory.Apply(first, Options(), origin, 1.5f);
        var stroke = Stroke(); controller.Document.Add(stroke);
        Matrix3x2.Invert(first.ContentToScreen(), out var screenToContent);
        var expected = Vector2.Transform(stroke.Points[0].Position,
            controller.ViewTransform * Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(origin) * screenToContent);
        var current = memory.CapturePreview(":deck:256").Value;
        Near(expected, Vector2.Transform(current.Strokes[0].Points[0].Position, current.WorldToContent));
        Assert.NotSame(stroke, current.Strokes[0]);
        memory.Apply(View("PowerPoint:42:deck:257"), Options(), origin, 1.5f);
        var stored = memory.CapturePreview(":deck:256").Value;
        Near(expected, Vector2.Transform(stored.Strokes[0].Points[0].Position, stored.WorldToContent));
        Assert.Empty(controller.Document.Strokes);
        Assert.Null(memory.CapturePreview(":other-deck:256"));
    }

    [Fact]
    public void PagesRestoreObjectIdentityImagesAndUndoRedo()
    {
        using var controller = new InkController(); var images = new BoardService(controller);
        var memory = new ScreenAnnotationMemory(controller, images); var options = Options();
        var a = Stroke(); Record(controller, a);
        var image = new BoardImageAsset { Data = new byte[] { 1 }, Width = 40, Height = 30 }; images.AddImage(image);
        Assert.True(memory.Apply(View(), options, Vector2.Zero, 1));
        Assert.True(memory.Apply(View("B"), options, Vector2.Zero, 1)); Assert.Empty(controller.Document.Strokes); Assert.Empty(images.CurrentImages);
        var b = Stroke(350); Record(controller, b);
        memory.Apply(View(), options, Vector2.Zero, 1);
        Assert.Same(a, Assert.Single(controller.Document.Strokes)); Assert.Same(image, Assert.Single(images.CurrentImages));
        controller.Undo(); Assert.Empty(images.CurrentImages); controller.Undo(); Assert.Empty(controller.Document.Strokes);
        memory.Apply(View("B"), options, Vector2.Zero, 1); Assert.Same(b, Assert.Single(controller.Document.Strokes));
        controller.Undo(); Assert.Empty(controller.Document.Strokes);
        memory.Apply(View(), options, Vector2.Zero, 1); controller.Redo(); Assert.Same(a, Assert.Single(controller.Document.Strokes));
        controller.Redo(); Assert.Same(image, Assert.Single(images.CurrentImages)); Assert.Equal(2, memory.RememberedPages);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-1920, 80, 1.5f)]
    [InlineData(1920, -200, 2)]
    public void ScrollZoomAndWindowMoveFollowPhysicalPixelsWithoutChangingGeometry(float ox, float oy, float dpi)
    {
        using var controller = new InkController(); var images = new BoardService(controller);
        var memory = new ScreenAnnotationMemory(controller, images); var options = Options();
        var stroke = Stroke(); controller.Document.Add(stroke); var initial = stroke.Points.ToArray();
        var origin = new Vector2(ox, oy); var anchor = View(); memory.Apply(anchor, options, origin, dpi);
        var point = new Vector2(200, 300);
        var physical = point * dpi + origin;
        var content = physical - new Vector2(anchor.Viewport.Left, anchor.Viewport.Top);
        var current = View(scroll: 350, zoom: 1.5f) with { Viewport = new Rect(130, 240, 1130, 1040) };
        memory.Apply(current, options, origin, dpi);
        Near((Vector2.Transform(content, current.ContentToScreen()) - origin) / dpi, Vector2.Transform(point, controller.ViewTransform));
        Assert.Equal(initial, stroke.Points); Assert.Equal(10, stroke.Attributes.BaseWidth);
        memory.Apply(anchor, options, origin, dpi); Near(point, Vector2.Transform(point, controller.ViewTransform));
    }

    [Fact]
    public void ManualViewAdjustmentBecomesNewBaselineAndReanchorPreservesDisplayedPosition()
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller)); var options = Options();
        memory.Apply(View(), options, Vector2.Zero, 1); memory.Apply(View(scroll: 100), options, Vector2.Zero, 1);
        controller.ViewTransform *= Matrix3x2.CreateTranslation(60, 30);
        memory.Apply(View(scroll: 150), options, Vector2.Zero, 1); Near(new Vector2(60, -120), Vector2.Transform(Vector2.Zero, controller.ViewTransform));
        var before = controller.ViewTransform; memory.Reanchor(View(scroll: 700)); memory.Apply(View(scroll: 700), options, Vector2.Zero, 1);
        Assert.Equal(before, controller.ViewTransform);
        memory.Apply(View(scroll: 720), options, Vector2.Zero, 1); Near(new Vector2(60, -140), Vector2.Transform(Vector2.Zero, controller.ViewTransform));
    }

    [Theory]
    [InlineData(false, false, 1, 0)]
    [InlineData(true, false, 1, -200)]
    [InlineData(false, true, 2, -200)]
    public void FollowOptionsAreIndependent(bool scroll, bool zoom, float expectedScale, float expectedY)
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var options = Options(); options.FollowScroll = scroll; options.FollowZoom = zoom;
        memory.Apply(View(), options, Vector2.Zero, 1); memory.Apply(View(scroll: 200, zoom: 2), options, Vector2.Zero, 1);
        Assert.Equal(expectedScale, controller.ViewTransform.M11); Assert.Equal(expectedY, controller.ViewTransform.M32);
    }

    [Fact]
    public void ActivePointerDefersPageSwitchAndUnsupportedTargetRestoresIndependentDesktop()
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller)); var options = Options();
        memory.Apply(View(), options, Vector2.Zero, 1);
        var pointer = new OverlayPointer(4, 300, 300, .5f, 10, 0, OverlayPointerKind.Mouse, 1);
        controller.OnPointerDown(pointer); Assert.False(memory.Apply(View("B"), options, Vector2.Zero, 1)); Assert.False(memory.Leave()); Assert.Equal("A", memory.CurrentKey);
        controller.OnPointerUp(pointer); Assert.True(memory.Apply(View("B"), options, Vector2.Zero, 1)); Assert.Empty(controller.Document.Strokes);
        Assert.True(memory.Leave()); var desktop = Stroke(500); Record(controller, desktop);
        memory.Apply(View(), options, Vector2.Zero, 1); Assert.Single(controller.Document.Strokes); Assert.DoesNotContain(desktop, controller.Document.Strokes);
        memory.Leave(); Assert.Same(desktop, Assert.Single(controller.Document.Strokes));
    }

    [Fact]
    public void DisablingPageMemoryKeepsSingleWorkingSetAndInvalidSnapshotsDoNothing()
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller)); var options = Options();
        options.RememberPages = false; var stroke = Stroke(); controller.Document.Add(stroke);
        memory.Apply(View(), options, Vector2.Zero, 1); memory.Apply(View("B"), options, Vector2.Zero, 1);
        Assert.Same(stroke, Assert.Single(controller.Document.Strokes)); Assert.Equal(1, memory.RememberedPages);
        Assert.False(memory.Apply(View() with { Scale = float.NaN }, options, Vector2.Zero, 1)); Assert.Same(stroke, Assert.Single(controller.Document.Strokes));
    }

    [Theory]
    [InlineData(800, 25, 50, 1200)]
    [InlineData(800, 100, -1, 0)]
    [InlineData(800, 0, 20, 0)]
    [InlineData(800, 50, 100, 800)]
    public void ScrollPatternPercentagesMapToPhysicalContentOffset(float viewport, double view, double scroll, float expected) =>
        Assert.Equal(expected, ScreenViewSnapshot.ScrollOffset(viewport, view, scroll));

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-1920, 80, 1.5f)]
    [InlineData(1920, -200, 2)]
    public void WordZoomUsesTextPositionIncludingPaperRecentering(float ox, float oy, float dpi)
    {
        using var controller = new InkController();
        var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var alignment = new WordScrollAlignment();
        var origin = new Vector2(ox, oy);
        var viewport = new Rect(ox + 100, oy + 200, ox + 1100, oy + 1000);
        var initial = View() with { Application = TrackedApplication.Word, Viewport = viewport, Scale = dpi };
        var scroll = alignment.Update(Vector2.Zero, 1, null, new Vector2(0, 800), false).Scroll;
        initial = initial with { ScrollPixels = scroll };
        memory.Apply(initial, Options(), origin, dpi);
        var glyphBefore = new Vector2(ox + 450, oy + 350);
        var inkPoint = (glyphBefore - origin) / dpi;
        var glyphAfter = new Vector2(ox + 390, oy + 390);
        var viewportOrigin = new Vector2(viewport.Left, viewport.Top);
        var movement = glyphAfter - viewportOrigin - (glyphBefore - viewportOrigin) * 1.5f;
        var zoomed = alignment.Update(scroll, 1.5f, movement, new Vector2(0, 1300), true);
        Assert.True(zoomed.Reliable);
        memory.Apply(initial with { Scale = dpi * 1.5f, ScrollPixels = zoomed.Scroll }, Options(), origin, dpi);
        Near(glyphAfter, Vector2.Transform(inkPoint, controller.ViewTransform) * dpi + origin);

        var returnedMovement = glyphBefore - viewportOrigin - (glyphAfter - viewportOrigin) / 1.5f;
        var returned = alignment.Update(zoomed.Scroll, 1 / 1.5f, returnedMovement, new Vector2(0, 800), true);
        memory.Apply(initial with { ScrollPixels = returned.Scroll }, Options(), origin, dpi);
        Near(glyphBefore, Vector2.Transform(inkPoint, controller.ViewTransform) * dpi + origin);
    }

    [Fact]
    public void WordScrollbarFallbackPreservesCalibratedOffsetAcrossLongScrolls()
    {
        var alignment = new WordScrollAlignment();
        alignment.Update(Vector2.Zero, 1, null, new Vector2(0, 1000), false);
        var zoomed = alignment.Update(new Vector2(0, 1000), 1.5f, new Vector2(120, -60), new Vector2(0, 1700), true);
        Near(new Vector2(-120, 1560), zoomed.Scroll);
        var scrolled = alignment.Update(zoomed.Scroll, 1, null, new Vector2(0, 3200), true);
        Near(new Vector2(-120, 3060), scrolled.Scroll);
        Assert.True(scrolled.Reliable);
        var observed = alignment.Update(scrolled.Scroll, 1, new Vector2(0, -30), new Vector2(0, 3250), true);
        Near(new Vector2(-120, 3090), observed.Scroll);
        var fallback = alignment.Update(observed.Scroll, 1, null, new Vector2(0, 3300), true);
        Near(new Vector2(-120, 3140), fallback.Scroll);
    }

    [Fact]
    public void WordUnobservedZoomCannotResetCalibrationUsingScrollbarPercentages()
    {
        var alignment = new WordScrollAlignment();
        alignment.Update(Vector2.Zero, 1, null, new Vector2(0, 500), false);
        var lost = alignment.Update(new Vector2(0, 500), 2, null, new Vector2(0, 1200), true);
        Assert.False(lost.Reliable);
        Assert.False(alignment.Update(lost.Scroll, 1, Vector2.Zero, new Vector2(0, 1200), true).Reliable);
        alignment.Reset();
        Assert.True(alignment.Update(Vector2.Zero, 1, null, new Vector2(0, 1200), false).Reliable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5f)]
    [InlineData(2)]
    public void ContinuousWordPinchUsesAnAbsoluteReferenceWithoutAccumulatingDrift(float dpi)
    {
        var reference = Enumerable.Range(0, 5).Select(i => new Rect(-1100 + i * 10, 150 + i * 95,
            -1088 + i * 10, 170 + i * 95)).ToArray();
        var pivot = new Vector2(-800, 430);
        for (int frame = 0; frame <= 160; frame++)
        {
            float scale = 1 + .5f * MathF.Sin(frame * MathF.PI / 80);
            var expected = Matrix3x2.CreateScale(scale, pivot) * Matrix3x2.CreateTranslation(frame < 160 ? frame / 4f : 0, 0);
            var pairs = reference.Select(rect =>
            {
                var a = Vector2.Transform(new Vector2(rect.Left, rect.Top) * dpi, expected);
                var b = Vector2.Transform(new Vector2(rect.Right, rect.Bottom) * dpi, expected);
                return (new Rect(rect.Left * dpi, rect.Top * dpi, rect.Right * dpi, rect.Bottom * dpi),
                    new Rect(MathF.Round(a.X), MathF.Round(a.Y), MathF.Round(b.X), MathF.Round(b.Y)));
            }).ToArray();
            Assert.True(TextAnchorAlignment.TryFit(pairs, out var actual));
            foreach (var rect in reference)
            {
                var point = new Vector2(rect.Left, rect.Top) * dpi;
                Assert.InRange(Vector2.Distance(Vector2.Transform(point, expected), Vector2.Transform(point, actual)), 0, 1.1f);
            }
        }
    }

    [Fact]
    public void TextAlignmentToleratesAnOccludedOrReflowedCharacterAndRejectsIncoherentFrames()
    {
        var expected = Matrix3x2.CreateScale(1.2345f) * Matrix3x2.CreateTranslation(-140, 35);
        var reference = Enumerable.Range(0, 5).Select(i => new Rect(300, 100 + i * 90, 315, 120 + i * 90)).ToArray();
        var pairs = reference.Select(rect =>
        {
            var a = Vector2.Transform(new Vector2(rect.Left, rect.Top), expected);
            return (rect, new Rect(a.X, a.Y, a.X + rect.Width * expected.M11, a.Y + rect.Height * expected.M11));
        }).ToArray();
        pairs[1].Item2 = new Rect(100, 150, 120, 175);
        Assert.True(TextAnchorAlignment.TryFit(pairs, out var actual));
        Assert.InRange(Math.Abs(actual.M11 - expected.M11), 0, .0001f);
        Assert.True(TextAnchorAlignment.TryFit(new[] { pairs[0], pairs[2], pairs[3] }, out _));
        pairs[2].Item2 = new Rect(400, 220, 420, 245);
        pairs[3].Item2 = new Rect(750, 310, 770, 335);
        Assert.False(TextAnchorAlignment.TryFit(pairs, out _));
        Assert.False(TextAnchorAlignment.TryFit(new[] { pairs[0] }, out _));
    }

    [Fact]
    public void ARecoveredAbsoluteReferenceRestoresWordAlignmentAfterTemporaryZoomGaps()
    {
        var alignment = new WordScrollAlignment();
        alignment.Update(Vector2.Zero, 1, null, new Vector2(0, 500), false);
        var lost = alignment.Update(new Vector2(0, 500), 1.2f, null, null, true);
        Assert.False(lost.Reliable);
        var expected = new Vector2(-75, 680);
        var recovered = alignment.Update(lost.Scroll, 1, lost.Scroll - expected, new Vector2(0, 700), true, referenceAligned: true);
        Assert.True(recovered.Reliable);
        Near(expected, recovered.Scroll);
        var next = alignment.Update(recovered.Scroll, 1, null, new Vector2(0, 730), true);
        Near(new Vector2(-75, 710), next.Scroll);
    }

    [Fact]
    public void RepeatedCharacterRangesCannotProduceANonFiniteZoom()
    {
        var repeated = (new Rect(300, 100, 312, 120), new Rect(350, 120, 368, 150));
        var remote = (new Rect(300, 500, 312, 520), new Rect(350, 720, 368, 722));
        Assert.False(TextAnchorAlignment.TryFit(new[] { repeated, repeated, repeated, repeated, remote }, out _));
    }

    [Theory]
    [InlineData(.02f)]
    [InlineData(50)]
    public void WordZoomCanMoveBetweenTenAndFiveHundredPercent(float ratio)
    {
        var reference = new Rect(300, 100, 312, 120);
        var second = new Rect(300, 500, 312, 520);
        Rect Zoom(Rect rect) => new(rect.Left * ratio, rect.Top * ratio, rect.Right * ratio, rect.Bottom * ratio);
        Assert.True(TextAnchorAlignment.TryFit(new[] { (reference, Zoom(reference)), (second, Zoom(second)) }, out var view));
        Assert.InRange(Math.Abs(view.M11 - ratio), 0, .0001f);
    }

    [Fact]
    public void TouchZoomTransitionSettlesWithinOneObservationInterval()
    {
        var transition = new ScreenViewTransition();
        var view = Matrix3x2.Identity;
        for (int frame = 0; frame < 30; frame++)
        {
            var next = Matrix3x2.CreateScale(1 + frame * .01f, new Vector2(400, 300));
            transition.Retarget(view, next, frame * 25, true, 32);
            var current = transition.Sample(frame * 25 + 24);
            Assert.InRange(Math.Abs(current.M11 - next.M11), 0, .001f);
            view = next;
        }
        Assert.Equal(view, transition.Sample(30 * 25 + 32));
    }

    [Fact]
    public void ViewTransitionRetargetsContinuouslyAndRepeatedSamplesDoNotRestartIt()
    {
        var transition = new ScreenViewTransition();
        var first = Matrix3x2.CreateTranslation(0, -100);
        transition.Retarget(Matrix3x2.Identity, first, 0, true);
        float position = transition.Sample(30).M32;
        Assert.InRange(position, -99, -1);
        var next = Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(-40, -200);
        transition.Retarget(first, next, 30, true);
        Assert.Equal(position, transition.Current.M32);
        transition.Retarget(next, next, 80, true);
        var halfway = transition.Current;
        Assert.InRange(halfway.M11, 1.01f, 1.49f);
        Assert.InRange(halfway.M32, -199, position);
        Assert.Equal(next, transition.Sample(130));
        Assert.False(transition.IsActive);
    }

    [Fact]
    public void PageSwitchInputAndExternalViewChangesEndTheTransitionExactly()
    {
        var transition = new ScreenViewTransition();
        var target = Matrix3x2.CreateTranslation(0, -200);
        transition.Retarget(Matrix3x2.Identity, target, 0, true);
        transition.Sample(20);
        transition.Reset(target);
        Assert.Equal(target, transition.Current);
        Assert.False(transition.IsActive);
        transition.Retarget(target, Matrix3x2.Identity, 30, false);
        Assert.Equal(Matrix3x2.Identity, transition.Sample(40));
        transition.Retarget(Matrix3x2.Identity, target, 50, true);
        var manual = Matrix3x2.CreateTranslation(100, 50);
        transition.Retarget(manual, manual, 60, true);
        Assert.False(transition.IsActive);
        Assert.Equal(manual, transition.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PixelClipHandlesViewportMasksAndDirtyTileOrigin(byte background)
    {
        var clip = new ScreenInkClip(new Rect(11.2f, 21.2f, 16.9f, 26.9f), new[] { new Rect(13.2f, 22.1f, 14.2f, 23.1f) });
        byte[] pixels = Enumerable.Repeat((byte)200, 8 * 8 * 4).ToArray(); clip.Apply(pixels, 8, 8, 10, 20, background);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            bool visible = x >= 2 && x < 6 && y >= 2 && y < 6 && !(x >= 3 && x < 5 && y >= 2 && y < 4);
            Assert.All(pixels.Skip((y * 8 + x) * 4).Take(4), value => Assert.Equal(visible ? (byte)200 : background, value));
        }
        Assert.True(clip.Contains(new Vector2(15, 25))); Assert.False(clip.Contains(new Vector2(13.5f, 22.5f))); Assert.False(clip.Contains(Vector2.Zero));
        clip = new ScreenInkClip(new Rect(-10, -10, -2, -2), Array.Empty<Rect>()); clip.Apply(pixels, 8, 8, background: background);
        Assert.All(pixels, value => Assert.Equal(background, value));
    }

    [Fact]
    public void FixedRegionsRequireConfirmedScrollAndDisappearWhenRemoved()
    {
        var detector = new FixedScreenRegions(); var header = new ScreenRegionAnchor("header", new Rect(100, 200, 1100, 280));
        var a = View() with { Anchors = new[] { header } };
        Assert.Empty(detector.Update(null, a)); Assert.Empty(detector.Update(a, a));
        var b = a with { ScrollPixels = new Vector2(0, 100) }; Assert.Single(detector.Update(a, b));
        Assert.Empty(detector.Update(b, View("B"))); Assert.Empty(detector.Update(a, a with { Scale = 2 }));
        detector.Update(a, b); Assert.Empty(detector.Update(b, b with { Anchors = Array.Empty<ScreenRegionAnchor>() }));
    }

    [Fact]
    public void SameUrlVariantsIgnoreCountersAndScrollingAndRestoreEarlierContent()
    {
        var variants = new ContentPageVariants();
        string a = string.Concat(Enumerable.Range(1, 40).Select(i => $"语文课堂讨论章节{i}，学习古诗中的景物描写。"));
        string b = string.Concat(Enumerable.Range(1, 40).Select(i => $"数学应用题第{i}节，计算三角函数和立体几何的面积。"));
        string key = variants.Resolve("url", a, false); Assert.Equal(key, variants.Resolve("url", a + "访问人数100", false));
        for (int i = 0; i < 3; i++) Assert.Equal(key, variants.Resolve("url", b, true));
        Assert.Equal(key, variants.Resolve("url", b, false)); string other = variants.Resolve("url", b, false); Assert.NotEqual(key, other);
        variants.Resolve("url", a, false); Assert.Equal(key, variants.Resolve("url", a, false));
        Assert.Equal(key, variants.Resolve("url", "", false));
    }

    [Fact]
    public void MatchingParagraphRetainsInkButChangedRegionUsesSeparatePagesAndDeletionDoesNotResurrect()
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var options = Options(); options.PreserveUnchangedRegions = true;
        var paragraph = new ScreenRegionAnchor("same text", new Rect(100, 250, 600, 400));
        var a = View() with { StableContentRegions = new[] { paragraph } };
        var b = View("B") with { StableContentRegions = new[] { paragraph with { Bounds = new Rect(100, 450, 600, 600) } } };
        var shared = Stroke(); var specific = Stroke(700, 600); Record(controller, shared); Record(controller, specific);
        memory.Apply(a, options, Vector2.Zero, 1); memory.Apply(b, options, Vector2.Zero, 1);
        var copy = Assert.Single(controller.Document.Strokes); Assert.Equal(shared.Id, copy.Id); Assert.Equal(shared.Points[0].Y + 200, copy.Points[0].Y);
        Assert.False(controller.History.CanUndo);
        controller.Document.Remove(copy); memory.Apply(a, options, Vector2.Zero, 1);
        Assert.Same(specific, Assert.Single(controller.Document.Strokes));
        memory.Apply(b, options, Vector2.Zero, 1); Assert.Empty(controller.Document.Strokes);
    }

    [Fact]
    public void RepeatedOrDistortedParagraphsDoNotTransferInk()
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var options = Options(); options.PreserveUnchangedRegions = true;
        var paragraph = new ScreenRegionAnchor("duplicate", new Rect(100, 250, 600, 400));
        memory.Apply(View() with { StableContentRegions = new[] { paragraph, paragraph } }, options, Vector2.Zero, 1); controller.Document.Add(Stroke());
        memory.Apply(View("B") with { StableContentRegions = new[] { paragraph } }, options, Vector2.Zero, 1); Assert.Empty(controller.Document.Strokes);
        controller.Document.Add(Stroke()); memory.Apply(View("C") with { StableContentRegions = new[] { paragraph with { Bounds = new Rect(100, 250, 900, 400) } } }, options, Vector2.Zero, 1);
        Assert.Empty(controller.Document.Strokes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedDeletionSurvivesAnIntermediatePageWithoutThatParagraph(bool throughDesktop)
    {
        using var controller = new InkController(); var memory = new ScreenAnnotationMemory(controller, new BoardService(controller));
        var options = Options(); options.PreserveUnchangedRegions = true;
        var regions = new[] { new ScreenRegionAnchor("persistent paragraph", new Rect(100, 250, 600, 400)) };
        var a = View() with { StableContentRegions = regions }; var b = View("B") with { StableContentRegions = regions };
        memory.Apply(a, options, Vector2.Zero, 1); controller.Document.Add(Stroke()); memory.Apply(b, options, Vector2.Zero, 1);
        controller.Document.Clear();
        if (throughDesktop) memory.Leave(); else memory.Apply(View("C"), options, Vector2.Zero, 1);
        memory.Apply(a, options, Vector2.Zero, 1);
        Assert.Empty(controller.Document.Strokes);
    }
}
