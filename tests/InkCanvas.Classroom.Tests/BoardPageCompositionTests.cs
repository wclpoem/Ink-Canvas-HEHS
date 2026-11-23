using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 2B：讲义/封面/子板贴回共用的板书页合成边界必须包含原生文本、公式、媒体与旋转图片，
/// 避免旧实现只统计笔画/图片导致裁剪。
/// </summary>
public sealed class BoardPageCompositionTests
{
    private static InkStroke Stroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 2, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 8, y + 8);
        stroke.Complete();
        return stroke;
    }

    [Fact]
    public void ContentBounds_IncludesNativeTextAndFormulaBeyondStrokes()
    {
        var board = new BoardService();
        board.Current.Document.Add(Stroke(0, 0));

        var text = new BoardTextObject
        {
            Type = "text",
            Content = "远端的文本对象",
            Left = 500,
            Top = 600,
            Width = 120,
            Height = 40,
        };
        var formula = new BoardFormulaObject
        {
            Latex = "x^2 + y^2 = 1",
            Left = 50,
            Top = 900,
            Width = 200,
            Height = 80,
        };
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "bounds-group");

        Rect bounds = BoardPageComposer.ContentBounds(board, 0);

        Assert.False(bounds.IsEmpty);
        Assert.True(bounds.Right >= 620, $"右边被裁剪：{bounds.Right}");
        Assert.True(bounds.Bottom >= 980, $"底边被裁剪：{bounds.Bottom}");
        Assert.True(bounds.Left <= 0, $"左边被裁剪：{bounds.Left}");
        Assert.True(bounds.Top <= 0, $"顶边被裁剪：{bounds.Top}");
    }

    [Fact]
    public void ContentBounds_UsesRotatedImageHull()
    {
        var board = new BoardService();
        var image = new BoardImageAsset
        {
            Name = "旋转图片",
            Data = new byte[] { 1, 2, 3 },
            ContentType = "image/png",
            Left = 100,
            Top = 100,
            Width = 40,
            Height = 20,
            Transform = new[] { 0f, 1f, -1f, 0f, 0f, 0f },
        };
        board.AddImage(image);

        Rect bounds = BoardPageComposer.ContentBounds(board, 0);

        // 90° 旋转后 x ∈ [-120,-100]，y ∈ [100,140]。
        Assert.True(bounds.Left <= -120, $"旋转图片左边未计入：{bounds.Left}");
        Assert.True(bounds.Bottom >= 140, $"旋转图片底边未计入：{bounds.Bottom}");
    }

    [Fact]
    public void ContentBounds_IncludesMediaPosterPlacement()
    {
        var board = new BoardService();
        var media = new BoardMediaAsset
        {
            Name = "视频",
            Data = new byte[] { 1, 2, 3 },
            PosterData = new byte[] { 4, 5, 6 },
            Left = 700,
            Top = 50,
            Width = 320,
            Height = 180,
        };
        Assert.True(board.AddMedia(media));

        Rect bounds = BoardPageComposer.ContentBounds(board, 0);

        Assert.True(bounds.Right >= 1020, $"媒体右边未计入：{bounds.Right}");
        Assert.True(bounds.Bottom >= 230, $"媒体底边未计入：{bounds.Bottom}");
    }

    [Fact]
    public void ContentBounds_OutOfRangePageIsEmpty()
    {
        var board = new BoardService();
        Assert.True(BoardPageComposer.ContentBounds(board, -1).IsEmpty);
        Assert.True(BoardPageComposer.ContentBounds(board, 99).IsEmpty);
    }
}