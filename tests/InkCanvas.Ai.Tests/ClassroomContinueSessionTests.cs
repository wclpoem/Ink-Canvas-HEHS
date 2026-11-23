using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// 「会话可继续」与「可写回」的语义边界：变式预览/再讲追问只要求会话、画板、页、
/// 课件页与请求代数一致，不约束选区；写回仍严格比较选区与笔画/对象集合。
/// 重点回归「仅选区变化导致变式预览被 CanWriteBack 误丢弃」。
/// </summary>
public sealed class ClassroomContinueSessionTests
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
    public void CanContinueSession_IdenticalContext_IsTrue()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.CanContinueSession(baseline, baseline));
    }

    [Fact]
    public void CanContinueSession_ToleratesSelectionChangesButWriteBackStillRejectsThem()
    {
        var baseline = Baseline();
        var otherSelectionId = baseline with { SelectionId = "sel-2" };
        var otherStrokeIds = baseline with { StrokeIds = new[] { "stroke-1", "stroke-2", "stroke-3" } };
        var otherObjectIds = baseline with { ObjectIds = new[] { "object-1", "object-2" } };

        Assert.True(ClassroomContextPolicy.CanContinueSession(baseline, otherSelectionId));
        Assert.True(ClassroomContextPolicy.CanContinueSession(baseline, otherStrokeIds));
        Assert.True(ClassroomContextPolicy.CanContinueSession(baseline, otherObjectIds));

        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, otherSelectionId));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, otherStrokeIds));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, otherObjectIds));
    }

    [Fact]
    public void CanContinueSession_RejectsPageBoardGenerationAndSessionChanges()
    {
        var baseline = Baseline();

        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { PageId = PageB }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { Kind = BoardCanvasKind.SideBoard }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { BoardId = "side" }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { Generation = 8 }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { SessionId = "session-2" }));
    }

    [Fact]
    public void CanContinueSession_RejectsContextsWithoutStablePageIdentity()
    {
        var baseline = Baseline();
        var withoutPageId = baseline with { PageId = Guid.Empty };

        Assert.True(ClassroomContextPolicy.SamePage(withoutPageId, withoutPageId));
        Assert.False(ClassroomContextPolicy.CanContinueSession(withoutPageId, withoutPageId));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, withoutPageId));
        Assert.False(ClassroomContextPolicy.CanContinueSession(withoutPageId, baseline));
    }

    [Fact]
    public void CanContinueSession_RejectsSlideChangesAndAllowsBothWithoutSlide()
    {
        var baseline = Baseline();

        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { PresentationKey = "other.pptx" }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { SlideId = 4 }));
        Assert.False(ClassroomContextPolicy.CanContinueSession(baseline, baseline with { AnimationStep = 1 }));

        var noSlide = baseline with { PresentationKey = "", SlideId = null, AnimationStep = null };
        Assert.True(ClassroomContextPolicy.CanContinueSession(noSlide, noSlide));
        Assert.True(ClassroomContextPolicy.CanContinueSession(noSlide, noSlide with { SelectionId = "sel-2" }));
    }

    [Fact]
    public void ContinueSessionNotice_ReportsNonSelectionReasons()
    {
        var baseline = Baseline();

        string boardNotice = ClassroomContextPolicy.ContinueSessionNotice(
            baseline, baseline with { Kind = BoardCanvasKind.SideBoard });
        string pageNotice = ClassroomContextPolicy.ContinueSessionNotice(
            baseline, baseline with { PageId = PageB });
        string slideNotice = ClassroomContextPolicy.ContinueSessionNotice(
            baseline, baseline with { SlideId = 4 });
        string expiredNotice = ClassroomContextPolicy.ContinueSessionNotice(
            baseline, baseline with { Generation = 8 });

        Assert.Contains("画板", boardNotice);
        Assert.Contains("页面", pageNotice);
        Assert.Contains("幻灯片", slideNotice);
        Assert.Contains("过期", expiredNotice);

        foreach (string notice in new[] { boardNotice, pageNotice, slideNotice, expiredNotice })
            Assert.DoesNotContain("选区", notice);

        Assert.Contains("会话已结束", ClassroomContextPolicy.ContinueSessionNotice(null, baseline));
        Assert.Contains("会话已结束", ClassroomContextPolicy.ContinueSessionNotice(baseline, null));
    }

    [Fact]
    public void CanWriteBack_RemainsStrictOnSelectionAndGeneration()
    {
        var baseline = Baseline();

        Assert.True(ClassroomContextPolicy.CanWriteBack(baseline, baseline));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { SelectionId = "sel-2" }));
        Assert.False(ClassroomContextPolicy.CanWriteBack(baseline, baseline with { Generation = 8 }));
    }
}