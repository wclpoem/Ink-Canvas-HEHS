using System.Numerics;
using System.Text.Json;
using System.IO.Compression;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class InkWashBrushTests
{
    [Fact]
    public void DefaultsSurviveSettingsRoundTripAndReset()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"canvas\":{}}");
        Assert.Equal(30, settings.Canvas.InkWashWidth);
        Assert.Equal(new InkWashBrushOptions(.42f, 44, .44f), settings.Canvas.InkWash);
        settings.Canvas.InkWash = new(.8f, 70, .7f);
        settings.Canvas.InkWashWidth = 56;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal(settings.Canvas.InkWash, restored.Canvas.InkWash);
        Assert.Equal(56, restored.Canvas.InkWashWidth);
        restored.ResetToRecommendation();
        Assert.Equal(InkWashBrushOptions.Default, restored.Canvas.InkWash);
        Assert.Equal(30, restored.Canvas.InkWashWidth);
    }

    [Fact]
    public void InvalidParametersHaveFiniteBoundedFallbacks()
    {
        Assert.Equal(InkWashBrushOptions.Default, new InkWashBrushOptions(float.NaN, float.PositiveInfinity, float.NegativeInfinity).Normalize());
        Assert.Equal(new InkWashBrushOptions(1, -180, 0), new InkWashBrushOptions(10, -900, -10).Normalize());
    }

    [Fact]
    public void DrynessControlsTransparentFeibaiAndGreyDrag()
    {
        var stroke = Sample();
        stroke.Attributes.InkWash = new(0, 44, .44f);
        var wet = Build(stroke);
        Assert.Empty(wet.FeibaiCutouts);
        Assert.Empty(wet.GreyDryDrag);
        stroke.Attributes.InkWash = new(1, 44, .44f);
        var dry = Build(stroke);
        Assert.NotEmpty(dry.FeibaiCutouts);
        Assert.NotEmpty(dry.GreyDryDrag);
        Assert.NotEmpty(dry.CoreClusters);
        Assert.All(AllContours(dry), contour =>
        {
            Assert.True(StrokeGeometry.SignedArea(contour) >= 0);
            Assert.All(contour, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y)));
        });
    }

    [Fact]
    public void CompletingStrokeDoesNotChangeGeometry()
    {
        var stroke = Sample();
        var live = Flatten(Build(stroke));
        stroke.Complete();
        Assert.Equal(live, Flatten(Build(stroke)));
    }

    [Theory]
    [InlineData(0, .1f)]
    [InlineData(90, .8f)]
    public void AngleAndFlatnessChangeActualContours(float angle, float flatness)
    {
        var stroke = Sample();
        var before = Flatten(Build(stroke));
        stroke.Attributes.InkWash = stroke.Attributes.InkWash with { Angle = angle, Flatness = flatness };
        Assert.NotEqual(before, Flatten(Build(stroke)));
    }

    [Fact]
    public void CloneReplayAndBothFileFormatsPreserveParametersAndTexture()
    {
        var stroke = Sample(); stroke.Complete();
        stroke.Attributes.InkWash = new(.83f, 62, .72f);
        var clone = stroke.Clone(false);
        Assert.Equal(Flatten(Build(stroke)), Flatten(Build(clone)));
        var replay = new InkReplaySession(new[] { stroke }).VisibleAt(120).Single();
        Assert.Equal(stroke.Attributes.InkWash, replay.Attributes.InkWash);
        Assert.Equal(stroke.Attributes.InkWashSeed, replay.Attributes.InkWashSeed);
        var pdf = PdfInkCodec.Decode(PdfInkCodec.Encode(new[] { stroke })).Single();
        Assert.Equal(Flatten(Build(stroke)), Flatten(Build(pdf)));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icink");
        try
        {
            InkFileStore.Save(path, new[] { stroke });
            var restored = InkFileStore.Load(path).Single();
            Assert.Equal(stroke.Id, restored.Id);
            Assert.Equal(Flatten(Build(stroke)), Flatten(Build(restored)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TranslationAndRotationKeepTheBrushAttachedToItsStroke()
    {
        var stroke = Sample();
        var before = Flatten(Build(stroke));
        var transform = Matrix3x2.CreateRotation(.6f) * Matrix3x2.CreateTranslation(160, -40);
        stroke.Transform(transform);
        var after = Flatten(Build(stroke));
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
            Assert.InRange(Vector2.Distance(Vector2.Transform(before[i], transform), after[i]), 0, .02f);
    }

    [Fact]
    public void ArcLengthCacheHandlesAppendRestoreAndTransform()
    {
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
        stroke.AddPoint(0, 0); stroke.AddPoint(3, 4);
        Assert.Equal(5, stroke.ArcLength());
        stroke.AddPoint(6, 8); Assert.Equal(10, stroke.ArcLength());
        Assert.Equal(5, stroke.ArcLengthAt(1));
        stroke.Transform(Matrix3x2.CreateScale(2)); Assert.Equal(20, stroke.ArcLength());
        var points = stroke.Points.ToArray();
        stroke.RestorePrefix(points, 2); Assert.Equal(10, stroke.ArcLength());
        stroke.RestorePoints(points); Assert.Equal(20, stroke.ArcLength());
    }

    [Fact]
    public void PreviousPdfPressureMetadataRemainsReadable()
    {
        using var memory = new MemoryStream();
        using (var compressed = new ZLibStream(memory, CompressionLevel.Optimal, true))
        using (var writer = new BinaryWriter(compressed))
        {
            writer.Write(0x334B4349); writer.Write(1); writer.Write(Guid.NewGuid().ToByteArray()); writer.Write(false);
            writer.Write(false); writer.Write(InkColor.Black.PackedArgb); writer.Write(30f);
            writer.Write(false); writer.Write((byte)96); writer.Write((int)TaperKind.InkWash);
            writer.Write((int)PenNib.Round); writer.Write(0f); writer.Write(false); writer.Write(false); writer.Write(1);
            foreach (float value in new[] { 10f, 20f, .6f, 18f, 5f }) writer.Write(value);
            writer.Write(true); writer.Write(25f); writer.Write(-35f);
        }
        var restored = Assert.Single(PdfInkCodec.Decode(memory.ToArray()));
        Assert.True(restored.Points[0].HasPressure);
        Assert.Equal(25, restored.Points[0].TiltX);
        Assert.Equal(InkWashBrushOptions.Default, restored.Attributes.InkWash);
    }

    [Fact]
    public void ControllerUsesIndependentWidthAndConfirmsShakyTouchDotAtConfiguredDiameter()
    {
        using var controller = new InkController { RecognizeShapes = false, PalmErases = false, InkWashWidth = 30 };
        controller.ConfigurePenWidths(3, 18);
        controller.CurrentAttributes.TaperKind = TaperKind.InkWash;
        var down = new OverlayPointer(1, 100, 100, .5f, 0, 0, OverlayPointerKind.Touch, 0);
        controller.OnPointerDown(down);
        Assert.Equal(30, controller.LiveStrokes.Single().Attributes.BaseWidth);
        controller.OnPointerMove(down with { X = 102, Y = 101, TimeMs = 400 });
        controller.OnPointerUp(down with { X = 101, Y = 100, TimeMs = 430 });
        var dot = Assert.Single(controller.Document.Strokes);
        Assert.Equal(30, Assert.Single(dot.Points).Width);
        Assert.False(InkBrush.UsesInkWash(dot));
        Assert.Equal(3, controller.CurrentAttributes.BaseWidth);
        Assert.Equal(18, controller.GetDrawingAttributes(true).BaseWidth);
        controller.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
        Assert.Equal(3, controller.GetDrawingAttributes(false).BaseWidth);
    }

    [Fact]
    public void LightHardwarePressureIsNotReplacedByThickRadiusFloor()
    {
        var stroke = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
        var points = Enumerable.Range(0, 100).Select(i => new InkPoint(i * 4, 50, .01f, .1f, i * 8, true)).ToArray();
        stroke.RestorePoints(points);
        var contours = Build(stroke);
        Assert.All(AllContours(contours).SelectMany(c => c), p => Assert.InRange(p.Y, 49.8f, 50.2f));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(40)]
    public void OrdinaryWritingKeepsAFullBodyAndAThinnerFastStroke(float milliseconds)
    {
        var normal = Straight(30, 600, milliseconds);
        var fast = Straight(30, 600, .5f);
        float normalBody = normal.Points.Skip(160).Take(300).Average(point => point.Width);
        float fastBody = fast.Points.Skip(160).Take(300).Average(point => point.Width);
        Assert.InRange(normalBody, 20, 40);
        Assert.True(normalBody > fastBody * 1.25f);
        Assert.True(normal.Points[^1].Width < normalBody * .2f);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    public void ShortCharacterStrokesGrowWithTheConfiguredBrushWidth(int length)
    {
        var narrow = Straight(15, length, 8);
        var medium = Straight(30, length, 8);
        var wide = Straight(90, length, 8);
        float Body(InkStroke stroke) => stroke.Points.Max(point => point.Width);
        Assert.InRange(Body(medium), 22, 40);
        Assert.True(Body(medium) > Body(narrow) * 1.7f);
        Assert.True(Body(wide) > Body(medium) * 2.7f);
        float VisibleBody(InkStroke stroke) => Build(stroke).WetBody.SelectMany(contour => contour).Max(point => point.Y)
            - Build(stroke).WetBody.SelectMany(contour => contour).Min(point => point.Y);
        Assert.True(VisibleBody(wide) > VisibleBody(medium) * 2.1f);
    }

    [Fact]
    public void WidthEditAppliesToTheNextStrokeAcrossToolChanges()
    {
        using var controller = new InkController { RecognizeShapes = false, PalmErases = false, InkWashWidth = 30 };
        controller.ConfigurePenWidths(3, 18);
        controller.CurrentAttributes.TaperKind = TaperKind.InkWash;
        var down = new OverlayPointer(1, 100, 100, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(down);
        controller.InkWashWidth = 72;
        controller.OnPointerMove(down with { X = 145, TimeMs = 20 });
        controller.OnPointerUp(down with { X = 145, TimeMs = 30 });
        Assert.Equal(30, controller.Document.Strokes.Single().Attributes.BaseWidth);
        controller.Tool = InkTool.Highlighter;
        Assert.Equal(18, controller.GetDrawingAttributes(true).BaseWidth);
        controller.Tool = InkTool.Pen;
        controller.OnPointerDown(down with { Id = 2, TimeMs = 100 });
        Assert.Equal(72, controller.LiveStrokes.Single().Attributes.BaseWidth);
        Assert.Equal(3, controller.CurrentAttributes.BaseWidth);
        controller.OnPointerUp(down with { Id = 2, TimeMs = 115 });
        controller.CurrentAttributes.TaperKind = TaperKind.None;
        Assert.Equal(3, controller.GetDrawingAttributes(false).BaseWidth);
    }

    [Fact]
    public void PausingAtTheHeadDoesNotInflateWaterInkOrAlterCompletedGeometry()
    {
        var stroke = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash });
        for (int sample = 0; sample < 60; sample++)
            stroke.AddPoint(100 + (sample % 3) * .1f, 100, .5f, sample * 20);
        Assert.All(stroke.Points, point => Assert.InRange(point.Width, 0, 1.5f));
        var before = Flatten(Build(stroke));
        stroke.Complete();
        Assert.Equal(before, Flatten(Build(stroke)));
    }

    [Fact]
    public void LongStrokeRetainsContinuousCoreAndStaysWithinDamageBounds()
    {
        var stroke = Sample(3000);
        stroke.Attributes.InkWash = new(1, 44, .9f);
        for (int start = 0; start < stroke.Count - 1; start += CalligraphyBrushGeometry.ChunkPointCount)
        {
            int end = Math.Min(stroke.Count - 1, start + CalligraphyBrushGeometry.ChunkPointCount);
            var contours = CalligraphyBrushGeometry.BuildChunk(stroke, start, end, start == 0, end == stroke.Count - 1, false);
            Assert.NotEmpty(contours.CoreClusters);
            Assert.All(AllContours(contours).SelectMany(c => c), p => Assert.True(stroke.Bounds.Contains(p)));
        }
    }

    private static InkStroke Sample(int count = 200)
    {
        var stroke = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
        for (int i = 0; i < count; i++) stroke.AddPoint(i * 4, 100 + MathF.Sin(i * .04f) * 30, .5f, i * 3);
        return stroke;
    }
    private static InkStroke Straight(float width, int length, float milliseconds)
    {
        var stroke = new InkStroke(new() { BaseWidth = width, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
        for (int point = 0; point <= length; point++) stroke.AddPoint(100 + point, 100, .5f, point * milliseconds);
        return stroke;
    }
    private static CalligraphyChunkContours Build(InkStroke stroke) => CalligraphyBrushGeometry.BuildChunk(stroke, 0, stroke.Count - 1, true, true, stroke.IsCompleted);
    private static IEnumerable<Vector2[]> AllContours(CalligraphyChunkContours contours) => contours.WetBody.Concat(contours.GreyDryDrag)
        .Concat(contours.OuterClusters).Concat(contours.CoreClusters).Concat(contours.FeibaiCutouts);
    private static Vector2[] Flatten(CalligraphyChunkContours contours) => AllContours(contours).SelectMany(c => c).ToArray();
}
