using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 页身份（PageId）回归：越界解析、增删页保持身份、保存/加载持久化，
/// 以及 AI 写回时的页令牌校验（同索引换页不得写入）。
/// </summary>
public sealed class BoardPageIdentityTests
{
    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-page-identity-{Guid.NewGuid():N}-{name}");

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

    private static BoardTextObject NewText(string content) => new()
    {
        Type = "text",
        Content = content,
        FontFamily = "Microsoft YaHei UI",
        FontSize = 32,
        Color = "#FF202024",
        LineHeight = 1.4,
        Align = "left",
        Left = 10,
        Top = 20,
        Width = 320,
        Height = 140,
        Source = "ai",
        Instruction = "测试页令牌",
        CreatedAt = 1730000000000,
    };

    [Fact]
    public void ResolvePageId_OutOfRange_ReturnsEmpty()
    {
        var board = new BoardService();

        Assert.NotEqual(Guid.Empty, board.ResolvePageId(0));
        Assert.Equal(Guid.Empty, board.ResolvePageId(-1));
        Assert.Equal(Guid.Empty, board.ResolvePageId(999));
        Assert.Equal(board.ResolvePageId(0), board.CurrentPageId);
    }

    [Fact]
    public void InsertPageAfterCurrent_KeepsOriginalIdentityAndAssignsNewOne()
    {
        var board = new BoardService();
        Guid originalId = board.ResolvePageId(0);

        Assert.True(board.InsertPageAfterCurrent());

        Assert.Equal(2, board.PageCount);
        Assert.Equal(originalId, board.ResolvePageId(0));
        Assert.NotEqual(Guid.Empty, board.ResolvePageId(1));
        Assert.NotEqual(originalId, board.ResolvePageId(1));
        Assert.Equal(board.ResolvePageId(1), board.CurrentPageId);
    }

    [Fact]
    public void RemovePage_KeepsIdentitiesOfRemainingPages()
    {
        var board = new BoardService();
        board.InsertPageAfterCurrent();
        board.InsertPageAfterCurrent();

        Guid firstId = board.ResolvePageId(0);
        Guid thirdId = board.ResolvePageId(2);

        Assert.True(board.RemovePage(1));

        Assert.Equal(2, board.PageCount);
        Assert.Equal(firstId, board.ResolvePageId(0));
        Assert.Equal(thirdId, board.ResolvePageId(1));
    }

    [Fact]
    public void SaveLoad_PreservesPageIdentities()
    {
        string path = TempFile("identity.icink");
        try
        {
            var board = new BoardService();
            board.InsertPageAfterCurrent();
            Guid firstId = board.ResolvePageId(0);
            Guid secondId = board.ResolvePageId(1);

            board.SaveBoard(path);

            var loaded = new BoardService();
            loaded.LoadBoard(path);

            Assert.Equal(2, loaded.PageCount);
            Assert.Equal(firstId, loaded.ResolvePageId(0));
            Assert.Equal(secondId, loaded.ResolvePageId(1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AddAiObjectsAtomic_RejectsMismatchedPageToken()
    {
        var board = new BoardService();
        board.InsertPageAfterCurrent();
        Guid otherPageId = board.CurrentPageId;
        board.GoTo(0);

        IReadOnlyList<Guid> rejected = board.AddAiObjectsAtomic(
            new[] { NewText("不应写入") }, null, "test", otherPageId);

        Assert.Empty(rejected);
        Assert.Empty(board.CurrentTextObjects);

        IReadOnlyList<Guid> accepted = board.AddAiObjectsAtomic(
            new[] { NewText("应写入") }, null, "test", board.CurrentPageId);

        Assert.Single(accepted);
        Assert.Single(board.CurrentTextObjects);
        Assert.Equal("应写入", board.CurrentTextObjects[0].Content);
    }

    [Fact]
    public void AddImagesAtomic_RejectsMismatchedPageToken()
    {
        var board = new BoardService();
        board.InsertPageAfterCurrent();
        Guid otherPageId = board.CurrentPageId;
        board.GoTo(0);

        IReadOnlyList<int> rejected = board.AddImagesAtomic(new[] { NewImage() }, "test", otherPageId);

        Assert.Empty(rejected);
        Assert.Empty(board.CurrentImages);

        Assert.False(board.AddImage(NewImage(), otherPageId));
        Assert.Empty(board.CurrentImages);

        IReadOnlyList<int> accepted = board.AddImagesAtomic(new[] { NewImage() }, "test", board.CurrentPageId);
        Assert.Single(accepted);
        Assert.Single(board.CurrentImages);

        Assert.True(board.AddImage(NewImage(), board.CurrentPageId));
        Assert.Equal(2, board.CurrentImages.Count);
    }
}