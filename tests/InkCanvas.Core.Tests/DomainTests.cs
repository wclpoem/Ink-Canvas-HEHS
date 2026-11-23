using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using System.Numerics;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Core.Tests;

public class ShapeRecognizerTests
{
    private static InkStroke FromPoints(IEnumerable<Vector2> pts)
    {
        var s = new InkStroke(new StrokeAttributes { BaseWidth = 3 });
        foreach (var p in pts) s.AddPoint(p.X, p.Y);
        s.Complete();
        return s;
    }

    [Fact]
    public void RecognizesCircle()
    {
        var pts = ShapeGenerator.EllipsePoints(new Vector2(200, 200), 90, 90, 0, 2 * MathF.PI, 80);
        var r = ShapeRecognizer.Recognize(new[] { FromPoints(pts) });
        Assert.Equal(RecognizedShape.Circle, r.Shape);
        Assert.InRange(r.RadiusX, 85f, 95f);
        Assert.InRange(r.Center.X, 195f, 205f);
    }

    [Fact]
    public void RecognizesEllipse()
    {
        var pts = ShapeGenerator.EllipsePoints(new Vector2(300, 200), 140, 70, 0, 2 * MathF.PI, 90);
        var r = ShapeRecognizer.Recognize(new[] { FromPoints(pts) });
        Assert.Equal(RecognizedShape.Ellipse, r.Shape);
        Assert.InRange(r.RadiusX, 130f, 150f);
        Assert.InRange(r.RadiusY, 62f, 78f);
    }

    [Fact]
    public void RecognizesStraightLine()
    {
        var pts = Enumerable.Range(0, 60).Select(i => new Vector2(50 + i * 4f, 100 + i * 1.2f));
        var r = ShapeRecognizer.Recognize(new[] { FromPoints(pts) });
        Assert.Equal(RecognizedShape.Line, r.Shape);
        Assert.Equal(2, r.Corners.Length);
    }

    [Fact]
    public void RejectsTinyMarks()
    {
        var pts = ShapeGenerator.EllipsePoints(new Vector2(20, 20), 6, 6, 0, 2 * MathF.PI, 30);
        Assert.Equal(RecognizedShape.None, ShapeRecognizer.Recognize(new[] { FromPoints(pts) }).Shape);
    }

    [Fact]
    public void RejectsOpenScribble()
    {
        var pts = Enumerable.Range(0, 70)
            .Select(i => new Vector2(100 + i * 3f, 200 + MathF.Sin(i * 0.9f) * 60f));
        var r = ShapeRecognizer.Recognize(new[] { FromPoints(pts) });
        Assert.True(r.Shape is RecognizedShape.None or RecognizedShape.Line);
    }
}

public class ShapeGeneratorTests
{
    private static readonly StrokeAttributes Attrs = new() { BaseWidth = 3, Color = InkColor.Black };
    private static readonly Vector2 A = new(100, 100);
    private static readonly Vector2 B = new(300, 260);

    [Fact]
    public void EveryShapeKindProducesGeometry()
    {
        foreach (ShapeKind kind in Enum.GetValues<ShapeKind>())
        {
            var strokes = ShapeGenerator.Generate(kind, A, B, Attrs);
            Assert.True(strokes.Count > 0, $"{kind} produced no strokes");
            // Focus dots and dot-line dots are single-point strokes (WPF pressure dots).
            Assert.All(strokes, s => Assert.True(s.Count >= 1, $"{kind} produced an empty stroke"));
            Assert.All(strokes, s => Assert.True(s.IsCompleted));
        }
    }

    [Fact]
    public void Coordinate3DHasThreeAxes()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.Coordinate3D, A, B, Attrs);
        Assert.Equal(3, strokes.Count);
    }

    [Fact]
    public void DashedLineIsBrokenIntoSegments()
    {
        var solid = ShapeGenerator.Generate(ShapeKind.Line, A, B, Attrs);
        var dashed = ShapeGenerator.Generate(ShapeKind.DashedLine, A, B, Attrs);
        Assert.Single(solid);
        Assert.True(dashed.Count > 3);
    }

    [Fact]
    public void CircleIsRoundRegardlessOfDragAspect()
    {
        var s = ShapeGenerator.Generate(ShapeKind.Circle, new Vector2(0, 0), new Vector2(200, 80), Attrs).Single();
        var b = s.Bounds;
        Assert.InRange(b.Width / b.Height, 0.9f, 1.1f);
    }
}

public class UndoStackTests
{
    [Fact]
    public void UndoAndRedoRoundTrip()
    {
        var doc = new InkDocument();
        var stack = new UndoStack();
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(1, 1);

        doc.Add(stroke);
        stack.Push(new DelegateAction("add", () => doc.Add(stroke), () => doc.Remove(stroke)));

        Assert.Equal(1, doc.Count);
        Assert.True(stack.Undo());
        Assert.Equal(0, doc.Count);
        Assert.True(stack.Redo());
        Assert.Equal(1, doc.Count);
    }

    [Fact]
    public void PushClearsRedoBranch()
    {
        var stack = new UndoStack();
        int x = 0;
        stack.Push(new DelegateAction("a", () => x = 1, () => x = 0));
        stack.Undo();
        Assert.Equal(0, x);
        Assert.True(stack.CanRedo);
        stack.Push(new DelegateAction("b", () => x = 2, () => x = 0));
        Assert.False(stack.CanRedo);
        stack.Undo();
        Assert.Equal(0, x);
    }

    [Fact]
    public void CapacityDropsOldest()
    {
        var stack = new UndoStack(3);
        for (int i = 0; i < 10; i++)
            stack.Push(new DelegateAction("x", () => { }, () => { }));
        Assert.Equal(3, stack.UndoCount);
    }
}

public class InkDocumentTests
{
    [Fact]
    public void EraseRemovesOnlyTouchedStrokes()
    {
        var doc = new InkDocument();
        var near = new InkStroke(new StrokeAttributes());
        near.AddPoint(100, 100); near.AddPoint(140, 100);
        var far = new InkStroke(new StrokeAttributes());
        far.AddPoint(600, 600); far.AddPoint(660, 600);
        doc.AddRange(new[] { near, far });

        var erased = doc.EraseAt(new Vector2(120, 100), 12f);
        Assert.Single(erased);
        Assert.Same(near, erased[0]);
        Assert.Equal(1, doc.Count);
    }

    [Fact]
    public void TransformMovesStrokesAndScalesWidth()
    {
        var s = new InkStroke(new StrokeAttributes { BaseWidth = 4 });
        s.AddPoint(10, 10); s.AddPoint(20, 10);
        s.Complete();
        float before = s.Points[0].Width;
        s.Transform(Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(5, 7));
        Assert.Equal(25f, s.Points[0].X);
        Assert.Equal(27f, s.Points[0].Y);
        Assert.Equal(before * 2f, s.Points[0].Width, 3);
    }
}

public class SettingsTests
{
    [Fact]
    public void RoundTripsThroughJson()
    {
        var s = new AppSettings();
        s.Canvas.InkWidth = 7.5;
        s.Appearance.Theme = 2;
        s.Automation.AutoSavedStrokesLocation = @"E:\somewhere";
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.Equal(7.5, back.Canvas.InkWidth);
        Assert.Equal(2, back.Appearance.Theme);
        Assert.Equal(@"E:\somewhere", back.Automation.AutoSavedStrokesLocation);
    }

    [Fact]
    public void ReadsLegacyWpfSettingsJson()
    {
        // A snippet in the original Newtonsoft-era shape, including the former public fields.
        const string legacy = """
        {
          "canvas": { "inkWidth": 4.5, "inkStyle": 1, "eraserType": 2 },
          "appearance": { "theme": 1, "floatingBarBottomMargin": 42.0 },
          "automation": { "autoDelSavedFiles": true, "autoDelSavedFilesDaysThreshold": 30 },
          "randSettings": { "peopleCount": 45 }
        }
        """;
        var s = JsonSerializer.Deserialize<AppSettings>(legacy);
        Assert.Equal(4.5, s.Canvas.InkWidth);
        Assert.Equal(1, s.Canvas.InkStyle);
        Assert.Equal(2, s.Canvas.EraserType);
        Assert.Equal(1, s.Appearance.Theme);
        Assert.Equal(42.0, s.Appearance.FloatingBarBottomMargin);
        Assert.True(s.Automation.AutoDelSavedFiles);
        Assert.Equal(30, s.Automation.AutoDelSavedFilesDaysThreshold);
        Assert.Equal(45, s.Rand.PeopleCount);
        // Untouched sections keep their defaults.
        Assert.True(s.PowerPoint.PowerPointSupport);
        Assert.False(s.PowerPoint.HideStrokesAfterLeavingAnnotation);
    }
}
