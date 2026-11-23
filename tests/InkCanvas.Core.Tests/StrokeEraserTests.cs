using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.History;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StrokeEraserTests
{
    [Fact]
    public void ErasingMiddleKeepsBothOutsideFragmentsAndTheirWidths()
    {
        var stroke = Line();
        Assert.True(StrokeEraser.EraseDisc(stroke, new Vector2(100, 50), 10, out var fragments));
        Assert.Equal(2, fragments.Count);
        Assert.Equal(0f, fragments[0].Points[0].X);
        Assert.Equal(88f, fragments[0].Points[^1].X, 3);
        Assert.Equal(112f, fragments[1].Points[0].X, 3);
        Assert.Equal(200f, fragments[1].Points[^1].X);
        Assert.All(fragments, f =>
        {
            Assert.True(f.IsCompleted);
            Assert.All(f.Points, p => Assert.Equal(4f, p.Width));
        });
        Assert.Equal(200f, stroke.Points[^1].X);
        Assert.Equal(2, stroke.Count);
    }

    [Fact]
    public void ErasingEndpointKeepsRestOfStroke()
    {
        Assert.True(StrokeEraser.EraseDisc(Line(), new Vector2(0, 50), 10, out var fragments));
        var fragment = Assert.Single(fragments);
        Assert.Equal(12f, fragment.Points[0].X, 3);
        Assert.Equal(200f, fragment.Points[^1].X);
    }

    [Fact]
    public void MissLeavesOriginalStrokeUntouched()
    {
        var stroke = Line();
        Assert.False(StrokeEraser.EraseDisc(stroke, new Vector2(100, 100), 10, out var fragments));
        Assert.Same(stroke, Assert.Single(fragments));
    }

    [Fact]
    public void CoveringWholeStrokeRemovesIt()
    {
        Assert.True(StrokeEraser.EraseDisc(Line(), new Vector2(100, 50), 110, out var fragments));
        Assert.Empty(fragments);
    }

    [Fact]
    public void EraserAlsoRemovesSinglePointInk()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        stroke.AddPoint(100, 50);
        stroke.Complete();
        Assert.True(StrokeEraser.EraseDisc(stroke, new Vector2(100, 50), 10, out var fragments));
        Assert.Empty(fragments);
    }

    [Fact]
    public void FragmentReplacementPreservesLayerOrderAndNotifiesOnce()
    {
        var document = new InkDocument();
        var original = Line();
        var overlay = Line();
        document.AddRange(new[] { original, overlay });
        StrokeEraser.EraseDisc(original, new Vector2(100, 50), 10, out var fragments);
        int notifications = 0;
        document.Changed += () =>
        {
            notifications++;
            Assert.Equal(3, document.Count);
            Assert.Same(overlay, document.Strokes[^1]);
        };
        Assert.True(document.Replace(original, fragments));
        Assert.Equal(1, notifications);
        Assert.Same(fragments[0], document.Strokes[0]);
        Assert.Same(fragments[1], document.Strokes[1]);
    }

    private static InkStroke Line()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
        stroke.AddPoint(0, 50);
        stroke.AddPoint(200, 50);
        stroke.Complete();
        return stroke;
    }

    [Fact]
    public void AreaErasePreservesStrokeIdentityAcrossEarlierUndoAndRedo()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 10 };
        var original = Line();
        var other = Line();
        other.Transform(Matrix3x2.CreateTranslation(0, 400));
        controller.Document.Add(other);
        controller.Document.Add(original);
        controller.History.Push(new DelegateAction("add", () => controller.Document.Add(original), () => controller.Document.Remove(original)));
        var pointer = new OverlayPointer(1, 100, 50, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        var fragments = controller.Document.Strokes.Skip(1).ToArray();
        Assert.Equal(2, fragments.Length);
        Assert.Same(other, controller.Document.Strokes[0]);
        controller.Undo();
        Assert.Same(original, controller.Document.Strokes[1]);
        controller.Undo();
        Assert.Same(other, Assert.Single(controller.Document.Strokes));
        controller.Redo();
        controller.Redo();
        Assert.Equal(new[] { other }.Concat(fragments), controller.Document.Strokes);
        controller.SelectAll();
        controller.TransformSelectionUndoable(Matrix3x2.CreateTranslation(20, 30), "move");
        controller.Undo();
        controller.Undo();
        Assert.Same(original, controller.Document.Strokes[1]);
        Assert.Equal(0, original.Points[0].X);
    }

    [Fact]
    public void FastAreaEraseNotifiesOncePerPointerSample()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 20 };
        controller.Document.AddRange(Enumerable.Range(0, 20).Select(index =>
        {
            var stroke = Line();
            stroke.Transform(Matrix3x2.CreateTranslation(0, index));
            return stroke;
        }));
        var pointer = new OverlayPointer(1, -100, 50, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        int changes = 0;
        controller.Document.Changed += () => changes++;
        controller.OnPointerMove(pointer with { X = 300 });
        Assert.Equal(1, changes);
        controller.OnPointerUp(pointer with { X = 300 });
        Assert.Empty(controller.Document.Strokes);
        controller.Undo();
        Assert.Equal(20, controller.Document.Count);
    }

    [Fact]
    public void EmptyAreaEraseDoesNotCopyDensePageIntoHistory()
    {
        using var controller = new InkController { Tool = InkTool.Eraser, EraserMode = InkEraserMode.Area, EraserRadius = 10 };
        controller.Document.AddRange(Enumerable.Range(0, 250).Select(index =>
        {
            var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
            for (int point = 0; point < 400; point++) stroke.AddPoint(point, index);
            stroke.Complete();
            return stroke;
        }));
        var pointer = new OverlayPointer(1, 1000, 1000, .5f, 1, 1, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        controller.OnPointerDown(pointer);
        controller.OnPointerUp(pointer);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 512 * 1024);
        Assert.False(controller.History.CanUndo);
    }
}
