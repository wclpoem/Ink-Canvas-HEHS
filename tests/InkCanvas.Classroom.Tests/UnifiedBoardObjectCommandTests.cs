using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 2A：统一板书对象视图与命令入口。覆盖一次撤销删除（笔画 + 图片 + 原生对象）、
/// 删除 AI 对象进入时间轴（remove/add）、图片统一分组后整体变换与取消分组、
/// 以及 Core 笔画编解码的全字段往返。
/// </summary>
public sealed class UnifiedBoardObjectCommandTests
{
    // ---- helpers ---------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-unified-object-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static BoardImageAsset Image(byte fill, float left = 10) => new()
    {
        Name = $"图片 {fill}",
        Data = new byte[] { fill, 2, 3, 4 },
        ContentType = "image/png",
        Left = left,
        Top = 20,
        Width = 160,
        Height = 90,
    };

    private static BoardTextObject Text(string content, float left = 10) => new()
    {
        Type = "text",
        Content = content,
        FontSize = 28,
        Left = left,
        Top = 30,
        Width = 300,
        Height = 120,
        Source = "ai",
        Instruction = "统一命令测试",
        CreatedAt = 1_730_000_000_000,
    };

    private static BoardFormulaObject Formula(string latex, float left = 10) => new()
    {
        Latex = latex,
        FontSize = 30,
        Left = left,
        Top = 200,
        Width = 200,
        Height = 80,
        Source = "ai",
        Instruction = "统一命令测试",
        CreatedAt = 1_730_000_000_000,
    };

    private static InkStroke Stroke(float x = 400)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 10f, 16f);
        stroke.AddPoint(x + 20f, 24f);
        stroke.Complete();
        return stroke;
    }

    private static AiObjectsPayload Decode(TimelineEvent item)
    {
        Assert.True(AiObjectPayloadCodec.TryDecode(item.Payload, out AiObjectsPayload payload));
        return payload;
    }

    private static List<TimelineEvent> AiSteps(InkRecorder recorder)
        => recorder.CapturedEvents.Where(item => item.Kind == TimelineEventKind.AiStep).ToList();

    // ---- 一次撤销删除 ----------------------------------------------------

    [Fact]
    public void DeleteObjects_RemovesStrokesImagesAndNativeObjects_InOneUndoStep()
    {
        var board = new BoardService();
        BoardImageAsset first = Image(1, 10);
        BoardImageAsset second = Image(2, 200);
        Assert.Equal(new[] { 0, 1 }, board.AddImagesAtomic(new[] { first, second }, "AI 板书"));

        BoardTextObject text = Text("一元二次方程");
        BoardFormulaObject formula = Formula("x^2");
        Assert.Equal(2, board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书").Count);

        InkStroke stroke = Stroke();
        board.Current.Document.Add(stroke);

        var ids = new[] { first.Id, second.Id, text.Id, formula.Id, stroke.Id };
        int undoBefore = board.Current.History.UndoCount;

        int deleted = board.DeleteObjects(Guid.Empty, ids, "删除对象");

        Assert.Equal(5, deleted);
        Assert.Equal(undoBefore + 1, board.Current.History.UndoCount);
        Assert.Empty(board.CurrentImages);
        Assert.Empty(board.CurrentTextObjects);
        Assert.Empty(board.CurrentFormulaObjects);
        Assert.Empty(board.Current.Document.Strokes);

        Assert.True(board.Current.History.Undo());

        Assert.Equal(2, board.CurrentImages.Count);
        Assert.Single(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Single(board.Current.Document.Strokes);
        Assert.Equal(ids, new[]
        {
            board.CurrentImages[0].Id, board.CurrentImages[1].Id,
            board.CurrentTextObjects[0].Id, board.CurrentFormulaObjects[0].Id,
            board.Current.Document.Strokes[0].Id,
        });
    }

    // ---- 删除入时间轴 ----------------------------------------------------

    [Fact]
    public void DeleteAiObjects_WritesRemoveAiStep_AndUndoWritesAdd()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board, boardId: 1, persistOnStop: false) { SessionDirectory = dir };
            recorder.Start("统一对象录制");

            BoardImageAsset image = Image(7);
            board.AddImagesAtomic(new[] { image }, "AI 板书");
            BoardTextObject text = Text("删除入时间轴");
            board.AddAiObjectsAtomic(new[] { text }, Array.Empty<BoardFormulaObject>(), "AI 板书", "native-group");
            Assert.Equal(2, AiSteps(recorder).Count);

            int deleted = board.DeleteObjects(Guid.Empty, new[] { image.Id, text.Id }, "删除 AI 对象");
            Assert.Equal(2, deleted);

            List<TimelineEvent> afterDelete = AiSteps(recorder);
            Assert.Equal(3, afterDelete.Count);
            AiObjectsPayload remove = Decode(afterDelete[^1]);
            Assert.Equal("remove", remove.Action);
            Assert.Single(remove.Objects);
            Assert.Single(remove.NativeObjects);
            Assert.Equal(text.Content, remove.NativeObjects[0].Content);

            Assert.True(board.Current.History.Undo());
            List<TimelineEvent> afterUndo = AiSteps(recorder);
            Assert.Equal(4, afterUndo.Count);
            AiObjectsPayload add = Decode(afterUndo[^1]);
            Assert.Equal("add", add.Action);
            Assert.Single(add.Objects);
            Assert.Single(add.NativeObjects);
            Assert.Equal(text.Content, add.NativeObjects[0].Content);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    // ---- 图片统一分组 / 整体变换 / 取消分组 -------------------------------

    [Fact]
    public void InkControllerDeleteSelection_OnAiObject_WritesRemoveAndUndoAdds()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board, boardId: 0, persistOnStop: false) { SessionDirectory = dir };
            recorder.Start("选区删除录制");

            BoardTextObject text = Text("选区删除");
            board.AddAiObjectsAtomic(new[] { text }, Array.Empty<BoardFormulaObject>(), "AI 板书", "delete-group");
            Assert.Single(AiSteps(recorder));

            board.Current.SelectElement(text);
            Assert.Contains(text, board.Current.SelectedElements);

            board.Current.DeleteSelection();
            Assert.Empty(board.CurrentTextObjects);

            List<TimelineEvent> afterDelete = AiSteps(recorder);
            Assert.Equal(2, afterDelete.Count);
            Assert.Equal("remove", Decode(afterDelete[^1]).Action);
            Assert.Equal(text.Content, Decode(afterDelete[^1]).NativeObjects[0].Content);

            board.Current.Undo();
            Assert.Single(board.CurrentTextObjects);

            List<TimelineEvent> afterUndo = AiSteps(recorder);
            Assert.Equal(3, afterUndo.Count);
            Assert.Equal("add", Decode(afterUndo[^1]).Action);
            Assert.Equal(text.Content, Decode(afterUndo[^1]).NativeObjects[0].Content);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void GroupImages_TransformAsWhole_ThenUngroup_RestoresOnUndo()
    {
        var board = new BoardService();
        BoardImageAsset first = Image(3, 10);
        BoardImageAsset second = Image(4, 220);
        board.AddImagesAtomic(new[] { first, second }, "AI 板书");
        var ids = new[] { first.Id, second.Id };

        Assert.Equal(2, board.GroupObjects(Guid.Empty, ids, "image-group", "图片分组"));
        BoardSelectionElement[] elements = board.GetSelectionElements().OfType<BoardSelectionElement>().ToArray();
        Assert.Equal(2, elements.Length);
        Assert.All(elements, element => Assert.Equal("image-group", element.SelectionGroupId));

        Assert.True(board.TransformObjects(Guid.Empty, ids, Matrix3x2.CreateTranslation(30, 15), "整体变换"));
        Assert.NotNull(first.Transform);
        Assert.NotNull(second.Transform);

        Assert.Equal(2, board.UngroupObjects(Guid.Empty, "image-group", "取消分组"));
        Assert.Equal("", first.GroupId);
        Assert.Equal("", second.GroupId);

        Assert.True(board.Current.History.Undo());
        Assert.Equal("image-group", first.GroupId);
        Assert.Equal("image-group", second.GroupId);

        Assert.True(board.Current.History.Undo());
        Assert.Null(first.Transform);
        Assert.Null(second.Transform);
    }

    // ---- Core 笔画编解码全字段往返 ---------------------------------------

    [Fact]
    public void CoreStrokeJsonCodec_RoundTripsShapeSourceObjectFrameAndTilt()
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(0xFF203040),
            BaseWidth = 3.5f,
            IsHighlighter = true,
            HighlighterAlpha = 80,
            TaperKind = TaperKind.InkWash,
            Nib = PenNib.Ellipse45,
            NibRotation = 12.5f,
            InkWash = new InkWashBrushOptions(0.6f, 30f, 0.35f),
            InkWashSeed = 42,
        })
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            LayerOrder = 17,
            IsRecognizedShape = true,
            ShapeSource = new ShapeSource(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "test", "{\"kind\":\"rect\"}"),
        };
        stroke.AddPoint(1f, 2f, 0.7f, 10f, hasPressure: true, tiltX: 0.8f, tiltY: -0.4f);
        stroke.AddPoint(3f, 4f, 0.5f, 20f);
        stroke.Complete();
        stroke.ObjectFrame = new SelectionFrame(
            new Rect(5, 6, 25, 36), Matrix3x2.CreateRotation(0.25f));

        string json = StrokeJsonCodec.Serialize(stroke);
        InkStroke restored = StrokeJsonCodec.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(stroke.Id, restored!.Id);
        Assert.Equal(stroke.LayerOrder, restored.LayerOrder);
        Assert.Equal(stroke.IsRecognizedShape, restored.IsRecognizedShape);
        Assert.Equal(stroke.ShapeSource!.GroupId, restored.ShapeSource!.GroupId);
        Assert.Equal(stroke.ShapeSource.Provider, restored.ShapeSource.Provider);
        Assert.Equal(stroke.ShapeSource.Definition, restored.ShapeSource.Definition);
        Assert.Equal(stroke.ExportObjectFrame(), restored.ExportObjectFrame());
        Assert.Equal(stroke.Attributes.Nib, restored.Attributes.Nib);
        Assert.Equal(stroke.Attributes.NibRotation, restored.Attributes.NibRotation);
        Assert.Equal(stroke.Attributes.InkWash, restored.Attributes.InkWash);
        Assert.Equal(stroke.Attributes.InkWashSeed, restored.Attributes.InkWashSeed);
        Assert.Equal(stroke.Attributes.Color.PackedArgb, restored.Attributes.Color.PackedArgb);
        Assert.Equal(stroke.Attributes.BaseWidth, restored.Attributes.BaseWidth);
        Assert.Equal(stroke.Attributes.HighlighterAlpha, restored.Attributes.HighlighterAlpha);
        Assert.Equal(stroke.TaperKind, restored.TaperKind);
        Assert.Equal(stroke.Count, restored.Count);

        InkPoint firstPoint = restored.Points[0];
        Assert.True(firstPoint.HasPressure);
        Assert.Equal(0.8f, firstPoint.TiltX);
        Assert.Equal(-0.4f, firstPoint.TiltY);
        Assert.False(restored.Points[1].HasPressure);
    }

    // ---- 旧 JSON 缺字段兼容 ----------------------------------------------

    [Fact]
    public void CoreStrokeJsonCodec_ReadsLegacyJsonWithoutNewFields()
    {
        const string legacy = "{\"Color\":4278190080,\"Width\":2.0,\"Highlighter\":false,"
            + "\"HighlighterAlpha\":96,\"Taper\":0,\"Shape\":false,"
            + "\"Points\":[[1.0,2.0,0.5,2.0,10.0]]}";

        InkStroke restored = StrokeJsonCodec.Deserialize(legacy);

        Assert.NotNull(restored);
        Assert.NotEqual(Guid.Empty, restored!.Id);
        Assert.Null(restored.LayerOrder);
        Assert.Null(restored.ShapeSource);
        Assert.Null(restored.ExportObjectFrame());
        Assert.Single(restored.Points);
    }
}