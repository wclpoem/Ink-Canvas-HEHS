using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using System.Numerics;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>Multi-page board behaviour: deep copy, ordering, per-page state, undo and persistence.</summary>
public sealed class BoardPageTests
{
    private static InkStroke NewStroke(float x1, float y1, float x2, float y2)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Opaque(10, 20, 30),
            BaseWidth = 2.5f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x1, y1, 0.5f, 0f);
        stroke.AddPoint(x2, y2, 0.5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static BoardImageAsset NewImage(byte fill = 1) => new()
    {
        Name = "img",
        Data = new[] { fill, (byte)2, (byte)3 },
        ContentType = "image/png",
        Width = 20,
        Height = 10,
        Left = 5,
        Top = 6,
    };

    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-classroom-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void CopyPage_DeepCopiesStrokes()
    {
        var board = new BoardService();
        board.Current.Document.Add(NewStroke(10, 10, 20, 20));

        Assert.True(board.CopyCurrentPage());
        Assert.Equal(2, board.PageCount);

        var original = board.Pages[0].Document.Strokes[0];
        var copy = board.Pages[1].Document.Strokes[0];
        Assert.NotSame(original, copy);
        Assert.Equal(10f, copy.Points[0].X, 3);

        copy.Transform(Matrix3x2.CreateTranslation(100f, 0f));

        Assert.Equal(10f, board.Pages[0].Document.Strokes[0].Points[0].X, 3);
        Assert.Equal(110f, board.Pages[1].Document.Strokes[0].Points[0].X, 3);
    }

    [Fact]
    public void CopyPage_CopiesImagesIndependently()
    {
        var board = new BoardService();
        Assert.True(board.AddImage(NewImage(1)));

        Assert.True(board.CopyCurrentPage());

        var copy = board.CurrentImages[0];
        copy.Left = 999f;
        copy.Data[0] = (byte)200;

        board.GoTo(0);
        var original = board.CurrentImages[0];

        Assert.NotSame(original, copy);
        Assert.Equal(5f, original.Left, 3);
        Assert.Equal((byte)1, original.Data[0]);
    }

    [Fact]
    public void CopyPage_InsertsAfterCurrentAndNamesCopy()
    {
        var board = new BoardService();
        board.RenamePage(0, "第一页");

        Assert.True(board.CopyCurrentPage());

        Assert.Equal(2, board.PageCount);
        Assert.Equal(1, board.CurrentIndex);
        Assert.Equal("第一页", board.PageName(0));
        Assert.Equal("第一页 副本", board.PageName(1));
    }

    [Fact]
    public void MovePage_ReordersPages()
    {
        var board = new BoardService();
        board.InsertPageAfterCurrent();
        board.InsertPageAfterCurrent();
        board.RenamePage(0, "A");
        board.RenamePage(1, "B");
        board.RenamePage(2, "C");

        Assert.True(board.MovePage(0, 2));

        Assert.Equal("B", board.PageName(0));
        Assert.Equal("C", board.PageName(1));
        Assert.Equal("A", board.PageName(2));
    }

    [Fact]
    public void MovePage_UndoRestoresOrder()
    {
        var board = new BoardService();
        board.InsertPageAfterCurrent();
        board.InsertPageAfterCurrent();
        board.RenamePage(0, "A");
        board.RenamePage(1, "B");
        board.RenamePage(2, "C");

        board.MovePage(0, 2);
        Assert.Equal("B", board.PageName(0));

        Assert.True(board.UndoPage());

        Assert.Equal("A", board.PageName(0));
        Assert.Equal("B", board.PageName(1));
        Assert.Equal("C", board.PageName(2));
    }

    [Fact]
    public void RenamePage_UpdatesName()
    {
        var board = new BoardService();

        Assert.True(board.RenamePage(0, "  课堂例题  "));
        Assert.Equal("课堂例题", board.PageName(0));

        Assert.False(board.RenamePage(0, "   "));
        Assert.Equal("课堂例题", board.PageName(0));
    }

    [Fact]
    public void PerPageBackground_IsIndependentAcrossPages()
    {
        var board = new BoardService();
        board.SetBackground(BoardBackground.Cream);
        board.InsertPageAfterCurrent();
        board.SetBackground(BoardBackground.Green);

        board.GoTo(0);
        Assert.Equal(BoardBackground.Cream, board.Background);

        board.GoTo(1);
        Assert.Equal(BoardBackground.Green, board.Background);
    }

    [Fact]
    public void PerPageGridSpacing_IsIndependentAcrossPages()
    {
        var board = new BoardService();
        board.GridSpacing = 56f;
        board.InsertPageAfterCurrent();
        board.GridSpacing = 24f;

        board.GoTo(0);
        Assert.Equal(56f, board.GridSpacing, 3);

        board.GoTo(1);
        Assert.Equal(24f, board.GridSpacing, 3);

        board.GridSpacing = 9999f;
        Assert.Equal(160f, board.GridSpacing, 3);
    }

    [Fact]
    public void RemoveCurrentPage_CanUndo()
    {
        var board = new BoardService();
        board.Current.Document.Add(NewStroke(0, 0, 5, 5));
        board.InsertPageAfterCurrent();
        board.GoTo(0);

        board.RemoveCurrentPage();
        Assert.Equal(1, board.PageCount);
        Assert.True(board.CanUndoPage);

        Assert.True(board.UndoPage());
        Assert.Equal(2, board.PageCount);
        Assert.Equal(0, board.CurrentIndex);
        Assert.Single(board.Pages[0].Document.Strokes);
    }

    [Fact]
    public void SaveLoad_RoundTripsPerPageMetadata()
    {
        var path = TempFile("board.icink");
        try
        {
            var board = new BoardService();
            board.RenamePage(0, "第一页");
            board.SetBackground(BoardBackground.Cream);
            board.GridSpacing = 56f;
            board.InsertPageAfterCurrent();
            board.RenamePage(1, "第二页");
            board.SetBackground(BoardBackground.Coordinate);
            board.GridSpacing = 24f;

            board.SaveBoard(path);

            var loaded = new BoardService();
            loaded.LoadBoard(path);

            Assert.Equal(2, loaded.PageCount);
            Assert.Equal("第一页", loaded.PageName(0));
            Assert.Equal(BoardBackground.Cream, loaded.PageBackground(0));
            Assert.Equal(56f, loaded.PageGridSpacing(0), 3);
            Assert.Equal("第二页", loaded.PageName(1));
            Assert.Equal(BoardBackground.Coordinate, loaded.PageBackground(1));
            Assert.Equal(24f, loaded.PageGridSpacing(1), 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadLegacyBoardFile_UsesDefaultsForMissingPageMetadata()
    {
        var path = TempFile("legacy.icink");
        const string json =
            "{\"Format\":\"inkcanvas-board\",\"Version\":1,\"Background\":\"Grid\",\"GridSpacing\":32," +
            "\"Pages\":[{\"Strokes\":[],\"Images\":[]}]}";
        try
        {
            File.WriteAllText(path, json);

            var board = new BoardService();
            board.LoadBoard(path);

            Assert.Equal(1, board.PageCount);
            Assert.Equal(BoardBackground.Grid, board.Background);
            Assert.Equal(32f, board.GridSpacing, 3);
            Assert.Equal("页面 1", board.PageName(0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SavePage_WithoutImages_RoundTripsThroughBoardPackage()
    {
        var path = TempFile("page.icink");
        try
        {
            var board = new BoardService();
            board.Current.Document.Add(NewStroke(1, 1, 2, 2));

            board.SavePage(path);

            using (var package = System.IO.Compression.ZipFile.OpenRead(path))
                Assert.NotNull(package.GetEntry("board.json"));

            var loaded = new BoardService();
            loaded.LoadPage(path);
            Assert.Single(loaded.Current.Document.Strokes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GetPageThumbnail_ReportsCountsAndName()
    {
        var board = new BoardService();
        board.RenamePage(0, "示例");
        board.Current.Document.Add(NewStroke(0, 0, 4, 4));
        board.AddImage(NewImage());

        var info = board.GetPageThumbnail(0);

        Assert.Equal(0, info.Index);
        Assert.Equal("示例", info.Name);
        Assert.Equal(1, info.StrokeCount);
        Assert.Equal(1, info.ImageCount);
    }
}