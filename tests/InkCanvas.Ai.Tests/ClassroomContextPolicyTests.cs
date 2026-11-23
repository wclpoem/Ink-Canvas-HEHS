using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// 统一课堂上下文比较策略：会话/画板/页面身份、课件页、选区、显示与写回判定，
/// 以及面向教师的过期提示与日志描述。重点回归「同索引换页 ≠ 同页」。
/// </summary>
public sealed class ClassroomContextPolicyTests
{
    private static readonly Guid PageA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PageB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ClassroomContext Baseline() => new()
    {
        SessionId = "session-1",
        RequestId = "req-1",
        Generation = 7,
        Kind = BoardCanvasKind.MainBoard,
        BoardId = "main",
        PageId = PageA,
        PageIndex = 2,
        PresentationKey = "lesson.pptx",
        SlideId = 3,
        SlideIndex = 4,
        AnimationStep = 0,
        SelectionId = "sel-1",
        StrokeIds = new[] { "stroke-1", "stroke-2" },
        GroupId = "group-1",
        ObjectIds = new[] { "object-1" },
        Source = ContentSource.AiWrite,
        CreatedAtMs = 1730000000000,
    };

    [Fact]
    public void SameSession_ComparesSessionIdWithNullNormalization()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.SameSession(baseline, baseline));
        Assert.False(ClassroomContextPolicy.SameSession(baseline, baseline with { SessionId = "session-2" }));
        Assert.False(ClassroomContextPolicy.SameSession(baseline, null));
        Assert.False(ClassroomContextPolicy.SameSession(null, baseline));

        var empty = new ClassroomContext();
        Assert.True(ClassroomContextPolicy.SameSession(empty, empty with { SessionId = null! }));
    }

    [Fact]
    public void SameBoard_RequiresSameKindAndBoardId()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.SameBoard(baseline, baseline));
        Assert.False(ClassroomContextPolicy.SameBoard(baseline, baseline with { Kind = BoardCanvasKind.SideBoard }));
        Assert.False(ClassroomContextPolicy.SameBoard(baseline, baseline with { BoardId = "side" }));

        var empty = new ClassroomContext();
        Assert.True(ClassroomContextPolicy.SameBoard(empty, empty with { BoardId = null! }));
    }

    [Fact]
    public void SamePage_DifferentPageIdWithSameIndex_IsNotSamePage()
    {
        var baseline = Baseline();
        var replaced = baseline with { PageId = PageB };

        Assert.Equal(baseline.PageIndex, replaced.PageIndex);
        Assert.False(ClassroomContextPolicy.SamePage(baseline, replaced));
    }

    [Fact]
    public void SamePage_FallsBackToIndexWhenBothPageIdsAreEmpty()
    {
        var first = new ClassroomContext
        {
            Kind = BoardCanvasKind.MainBoard,
            BoardId = "main",
            PageIndex = 2,
        };
        var sameIndex = first with { PageIndex = 2 };
        var otherIndex = first with { PageIndex = 3 };

        Assert.True(ClassroomContextPolicy.SamePage(first, sameIndex));
        Assert.False(ClassroomContextPolicy.SamePage(first, otherIndex));
    }

    [Fact]
    public void SamePage_SamePageIdWithDifferentIndex_IsSamePage()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.SamePage(baseline, baseline with { PageIndex = 9 }));
    }

    [Fact]
    public void SameSlide_ComparesPresentationSlideAndAnimationStep()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.SameSlide(baseline, baseline));

        var noSlide = new ClassroomContext();
        Assert.True(ClassroomContextPolicy.SameSlide(noSlide, new ClassroomContext()));

        Assert.False(ClassroomContextPolicy.SameSlide(baseline, baseline with { PresentationKey = "other.pptx" }));
        Assert.False(ClassroomContextPolicy.SameSlide(baseline, baseline with { SlideId = 4 }));
        Assert.False(ClassroomContextPolicy.SameSlide(baseline, baseline with { AnimationStep = 1 }));
    }

    [Fact]
    public void SameSelection_IgnoresStrokeOrderButDetectsSetChanges()
    {
        var baseline = Baseline();
        var reordered = baseline with { StrokeIds = new[] { "stroke-2", "stroke-1" } };

        Assert.True(ClassroomContextPolicy.SameSelection(baseline, reordered));
        Assert.False(ClassroomContextPolicy.SameSelection(baseline,
            baseline with { StrokeIds = new[] { "stroke-1", "stroke-2", "stroke-3" } }));
        Assert.False(ClassroomContextPolicy.SameSelection(baseline, baseline with { SelectionId = "sel-2" }));

        var empty = new ClassroomContext();
        Assert.True(ClassroomContextPolicy.SameSelection(empty, new ClassroomContext()));
    }

    [Fact]
    public void IsCurrentForDisplay_RequiresSameBoardAndPage()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.IsCurrentForDisplay(baseline, baseline));
        Assert.False(ClassroomContextPolicy.IsCurrentForDisplay(baseline, baseline with { BoardId = "side" }));
        Assert.False(ClassroomContextPolicy.IsCurrentForDisplay(baseline, baseline with { PageId = PageB }));
    }

    [Fact]
    public void CanWriteBack_AllIdentical_IsTrue()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.CanWriteBack(baseline, baseline));
    }

    [Fact]
    public void CanWriteBack_RejectsSlideSelectionOrGenerationChanges()
    {
        var baseline = Baseline();

        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { SlideId = 4 }));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { SelectionId = "sel-2" }));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { Generation = 8 }));
    }

    [Fact]
    public void CanWriteBack_RejectsMissingPageIdentityAndDifferentBoard()
    {
        var baseline = Baseline();
        var withoutPageId = baseline with { PageId = Guid.Empty };

        Assert.False(ClassroomContextPolicy.CanWriteBack(withoutPageId, baseline));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, withoutPageId));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { BoardId = "side" }));
    }

    [Fact]
    public void StaleNotice_ReportsFirstStaleDimension()
    {
        var baseline = Baseline();

        Assert.Contains("画板", ClassroomContextPolicy.StaleNotice(baseline, baseline with { Kind = BoardCanvasKind.SideBoard }));
        Assert.Contains("页面", ClassroomContextPolicy.StaleNotice(baseline, baseline with { PageId = PageB }));
        Assert.Contains("幻灯片", ClassroomContextPolicy.StaleNotice(baseline, baseline with { SlideId = 4 }));
        Assert.Contains("选区", ClassroomContextPolicy.StaleNotice(baseline, baseline with { SelectionId = "sel-2" }));
        Assert.Contains("过期", ClassroomContextPolicy.StaleNotice(baseline, baseline with { Generation = 8 }));
    }

    [Fact]
    public void Describe_IncludesIdentityAndAiPrefix()
    {
        var baseline = Baseline();
        string description = ClassroomContextPolicy.Describe(baseline);

        Assert.StartsWith("[AI] ", description);
        Assert.Contains("kind=MainBoard", description);
        Assert.Contains("board=main", description);
        Assert.Contains($"pageId={PageA}", description);
        Assert.Contains("request=req-1", description);
        Assert.Equal("[AI] context=none", ClassroomContextPolicy.Describe(null));
    }
}