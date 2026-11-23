using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 3B 纯逻辑层：练习页原生板书（文本而非图片、单步撤销）、练习结束快照与书签、
/// 知识点规范化、变式题不覆盖原页、离线练习闭环，以及练习关联 note 往返。
/// </summary>
public sealed class PracticeFlowTests
{
    // ---- helpers ---------------------------------------------------------

    private const string PracticePayload =
        """{"screenshot":"a.png","practice":true}""";

    private static VariantQuestionDraft Draft(string text, string answer, string knowledge)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Difficulty = ExerciseDifficulty.Basic,
            Text = text,
            Answer = answer,
            KnowledgePoint = knowledge,
            CreatedAtMs = 1_730_000_000_000,
        };

    private static TimelineEvent Timer(long timeMs, int page, string payload) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.TimerEnded,
        Payload = payload,
    };

    private static TimelineEvent PageEvent(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    // ---- 1. 原生练习页：只写文本、不写图片、单步撤销 ----------------------

    [Fact]
    public void NativePracticePage_WritesTextsNotImages_AndUndoesInOneStep()
    {
        var board = new BoardService();
        Assert.True(board.InsertPageAfterCurrent());
        int imagesBefore = board.CurrentImages.Count;

        VariantQuestionDraft draft = Draft(
            "小明有 6 组贴纸，每组 7 张，一共 42 张。请列出算式。",
            "6 × 7 = 42 张",
            "乘法，乘加");

        AssistantBoardPlan plan = ExerciseFlowPolicy.BuildPracticeBoardPlan(draft);
        Assert.Equal(3, plan.Blocks.Count);
        Assert.Equal("practice", plan.Intent);
        Assert.Equal("main", plan.TargetBoard);

        BoardPlanNativeResult created = BoardPlanWriter.Create(plan, "随堂练习", 1);
        Assert.True(created.Success, created.Error);
        Assert.Equal(0, created.Formulas.Count);
        Assert.Equal(3, created.Texts.Count);

        ExerciseFlowPolicy.LayoutPracticeObjects(created.Texts);
        Assert.All(created.Texts, text => Assert.Equal(48f, text.Left));
        Assert.True(created.Texts[1].Top > created.Texts[0].Top);

        int undoBefore = board.Current.History.UndoCount;
        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            created.Texts, created.Formulas, "随堂练习", board.CurrentPageId, "practice-group");

        Assert.NotEmpty(ids);
        Assert.NotEmpty(board.CurrentTextObjects);
        Assert.Empty(board.CurrentFormulaObjects);
        Assert.Equal(imagesBefore, board.CurrentImages.Count);
        Assert.Equal(undoBefore + 1, board.Current.History.UndoCount);

        BoardTextObject question = board.CurrentTextObjects.Single(text => text.Title == "题目");
        BoardTextObject knowledge = board.CurrentTextObjects.Single(text => text.Title == "知识点");
        BoardTextObject answer = board.CurrentTextObjects.Single(text => text.Title == "参考答案");
        Assert.Contains("42", question.Content);
        Assert.Equal("乘法，乘加", knowledge.Content);
        Assert.Contains("42", answer.Content);
        Assert.Equal("practice-group", question.GroupId);

        board.Current.Undo();
        Assert.Empty(board.CurrentTextObjects);
        Assert.Equal(imagesBefore, board.CurrentImages.Count);
    }

    // ---- 2. 快照记录、书签 kind 统一与结束记录 ----------------------------

    [Fact]
    public void SnapshotSaved_IsRecordedInTeachingSession_AndBookmarkKindIsUnified()
    {
        const string snapshotPath = @"C:\tmp\practice.png";
        VariantQuestionDraft draft = Draft("计算 7 × 6", "42", "乘法");
        Guid boardPageId = Guid.NewGuid();
        PracticePlan plan = ExerciseFlowPolicy.Plan(
            draft, 1, 3, "practice", "ai-1", boardPageId, "g-1");

        PracticeFinishRecord record = PracticeFinishPolicy.Plan(plan, true, true, snapshotPath, 123);
        Assert.True(record.SnapshotSaved);
        Assert.Equal(snapshotPath, record.SnapshotPath);
        Assert.Equal(123, record.FinishedAtMs);
        Assert.True(record.BookmarkCreated);
        Assert.Equal("练习结束", record.BookmarkName);

        TeachingSession session = TeachingSession.Create("练习", 1);
        Assert.True(session.RecordSnapshot(snapshotPath, 123));
        Assert.Contains(snapshotPath, session.SnapshotPaths);
        Assert.False(session.RecordSnapshot("", 124));

        string note = PracticeLinkNote.Build("ai-1", boardPageId, "g-1", snapshotPath);
        session.Link("practice", "ex-1", 0, 0, 2, note, 123);

        TeachingSession reloaded = TeachingSession.Deserialize(session.Serialize());
        Assert.NotNull(reloaded);
        Assert.Contains(snapshotPath, reloaded.SnapshotPaths);
        TeachingSessionLink link = reloaded.Links.Single(item => item.Id == "ex-1");
        Assert.Contains("snapshot=" + snapshotPath, link.Note);

        string updatedNote = PracticeLinkNote.Build("ai-2", boardPageId, "g-2", snapshotPath);
        TeachingSessionLink updated = reloaded.Link("practice", "ex-1", 0, 0, 2, updatedNote, 200);
        Assert.Equal(updatedNote, updated.Note);
        Assert.Equal(200, reloaded.UpdatedAtMs);

        var bookmarkSession = new ClassroomSession { Id = "s" };
        var bookmarkTimeline = new InkTimeline(bookmarkSession);
        bookmarkTimeline.AddBookmark(10, "练习结束", SessionBookmarkKind.Practice);
        Assert.Equal(SessionBookmarkKind.Practice, bookmarkSession.Bookmarks[0].Kind);

        var practiceSession = new ClassroomSession
        {
            Id = "practice-session",
            Events = new List<TimelineEvent> { Timer(10_000, 1, PracticePayload) },
        };
        Assert.True(InkTimeline.IsPracticeTimerEnded(PracticePayload));
        SessionBookmark practiceBookmark = new InkTimeline(practiceSession).AutoBookmarks().Last();
        Assert.Equal("练习结束", practiceBookmark.Name);
        Assert.Equal(SessionBookmarkKind.Practice, practiceBookmark.Kind);

        foreach (string payload in new[] { "180000", "倒计时结束" })
        {
            Assert.False(InkTimeline.IsPracticeTimerEnded(payload));
            var timerSession = new ClassroomSession
            {
                Id = "timer-session",
                Events = new List<TimelineEvent> { Timer(10_000, 1, payload) },
            };
            SessionBookmark timerBookmark = new InkTimeline(timerSession).AutoBookmarks().Last();
            Assert.Equal("倒计时结束", timerBookmark.Name);
            Assert.Equal(SessionBookmarkKind.Timer, timerBookmark.Kind);
        }

        Assert.False(InkTimeline.IsPracticeTimerEnded(""));
        Assert.False(InkTimeline.IsPracticeTimerEnded("not-json"));
        Assert.True(InkTimeline.IsPracticeTimerEnded("""{"scene":"Practice"}"""));
        Assert.True(InkTimeline.IsPracticeTimerEnded("练习结束 @ 12:30"));
    }

    // ---- 3. 知识点中英文分隔符往返 ---------------------------------------

    [Fact]
    public void KnowledgePoints_RoundTrip_WithChineseAndEnglishSeparators()
    {
        Assert.Equal(
            "函数，方程，几何，概率，统计",
            ExerciseFlowPolicy.NormalizeKnowledgePoints(" 函数，方程,几何、概率;统计 "));

        string normalized = ExerciseFlowPolicy.NormalizeKnowledgePoints(" 函数，方程,几何、概率;统计 ");
        Assert.Equal(normalized, ExerciseFlowPolicy.NormalizeKnowledgePoints(normalized));

        IReadOnlyList<string> parts = ExerciseFlowPolicy.SplitKnowledgePoints("函数，函数,方程；方程");
        Assert.Equal(new[] { "函数", "方程" }, parts);
        Assert.Equal("", ExerciseFlowPolicy.NormalizeKnowledgePoints("，,、；;"));
        Assert.Equal("", ExerciseFlowPolicy.NormalizeKnowledgePoints(null));

        VariantQuestionDraft draft = Draft("题干", "答案", " 函数，方程,几何、概率;统计 ");
        IReadOnlyList<string> lines = ExerciseFlowPolicy.BuildPasteLines(draft);
        Assert.Contains("知识点：函数，方程，几何，概率，统计", lines);
    }

    // ---- 4. 变式题写新页，不覆盖原页 -------------------------------------

    [Fact]
    public void NextVariant_DoesNotOverwriteOriginalPage()
    {
        var board = new BoardService();
        VariantQuestionDraft original = Draft("原题：1 + 1 = ?", "2", "加法");
        BoardPlanNativeResult originalCreated = BoardPlanWriter.Create(
            ExerciseFlowPolicy.BuildPracticeBoardPlan(original, "原题"), "原题", 1);
        board.AddAiObjectsAtomic(
            originalCreated.Texts, originalCreated.Formulas, "原题", board.CurrentPageId, "original-group");

        Guid[] originalIds = board.CurrentTextObjects.Select(text => text.Id).ToArray();
        string[] originalContents = board.CurrentTextObjects.Select(text => text.Content).ToArray();
        Assert.Equal(3, originalIds.Length);

        Assert.True(board.InsertPageAfterCurrent());
        VariantQuestionDraft variant = Draft("变式：2 + 3 = ?", "5", "加法，进位");
        BoardPlanNativeResult variantCreated = BoardPlanWriter.Create(
            ExerciseFlowPolicy.BuildPracticeBoardPlan(variant, "变式"), "变式", 2);
        IReadOnlyList<Guid> variantIds = board.AddAiObjectsAtomic(
            variantCreated.Texts, variantCreated.Formulas, "变式", board.CurrentPageId, "variant-group");

        Assert.NotEmpty(variantIds);
        Assert.NotEmpty(board.CurrentTextObjects);
        Assert.Equal(1, board.CurrentIndex);

        board.GoTo(0);
        Assert.Equal(originalIds, board.CurrentTextObjects.Select(text => text.Id));
        Assert.Equal(originalContents, board.CurrentTextObjects.Select(text => text.Content));
        Assert.DoesNotContain(board.CurrentTextObjects, text => text.Content.Contains("变式", StringComparison.Ordinal));
    }

    // ---- 5. 离线（无 AI 客户端）练习闭环 ---------------------------------

    [Fact]
    public void Offline_NoAiClient_PracticePipelineStillWorks()
    {
        VariantQuestionDraft draft = Draft("离线练习：3 × 4 = ?", "12", "乘法");
        PracticePlan plan = ExerciseFlowPolicy.Plan(
            draft, 1, 3, "practice", null, Guid.Empty, "offline-group");

        AssistantBoardPlan boardPlan = ExerciseFlowPolicy.BuildPracticeBoardPlan(draft);
        BoardPlanNativeResult created = BoardPlanWriter.Create(boardPlan, "随堂练习", 1);
        Assert.True(created.Success, created.Error);

        var board = new BoardService();
        Assert.True(board.InsertPageAfterCurrent());
        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            created.Texts, created.Formulas, "随堂练习", board.CurrentPageId, "offline-group");
        Assert.NotEmpty(ids);

        const string snapshotPath = @"C:\tmp\offline-practice.png";
        var session = new ClassroomSession
        {
            Id = "offline-session",
            Events = new List<TimelineEvent>
            {
                PageEvent(100, 0),
                new() { TimeMs = 200, PageIndex = 0, Kind = TimelineEventKind.TimerStarted, Payload = "180000" },
                Timer(180_200, 0, PracticePayload),
            },
        };

        PracticeFinishRecord record = PracticeFinishPolicy.Plan(plan, true, true, snapshotPath, 180_200);
        Assert.True(record.BookmarkCreated);
        Assert.True(record.SnapshotSaved);

        var teaching = TeachingSession.Create("离线练习", 180_200);
        Assert.True(teaching.RecordSnapshot(record.SnapshotPath, record.FinishedAtMs));

        var timeline = new InkTimeline(session);
        timeline.AddBookmark(record.FinishedAtMs, record.BookmarkName, SessionBookmarkKind.Practice);

        Assert.Empty(timeline.BuildFrame(record.FinishedAtMs));
        Assert.Empty(timeline.BuildObjectFrame(record.FinishedAtMs));
        Assert.Equal(0, timeline.PageAt(record.FinishedAtMs));
        SessionBookmark bookmark = timeline.AutoBookmarks().Last();
        Assert.Equal("练习结束", bookmark.Name);
        Assert.Equal(SessionBookmarkKind.Practice, bookmark.Kind);
        Assert.Contains(snapshotPath, teaching.SnapshotPaths);

        IReadOnlyList<PracticeFinishAction> actions =
            PracticeFinishActionPolicy.Build(aiAvailable: false, snapshotAvailable: true);
        Assert.Equal(3, actions.Count);
        Assert.Equal(
            new[]
            {
                PracticeFinishActionKind.NextVariant,
                PracticeFinishActionKind.Collect,
                PracticeFinishActionKind.Export,
            },
            actions.Select(action => action.Kind));
        Assert.True(actions[0].RequiresAi);
        Assert.False(actions[1].RequiresAi);
        Assert.False(actions[2].RequiresAi);
        Assert.Equal("再来一题（变式）", actions[0].Label);
    }

    // ---- 6. 练习关联 note 编解码往返 -------------------------------------

    [Fact]
    public void PracticeLinkNote_RoundTrips()
    {
        Guid boardPageId = Guid.NewGuid();
        const string snapshotPath = @"C:\课堂\练习;第1组.png";

        string note = PracticeLinkNote.Build("ai-9", boardPageId, "g-9", snapshotPath);
        PracticeLinkInfo info = PracticeLinkNote.Parse(note);

        Assert.Equal("ai-9", info.AiSessionId);
        Assert.Equal(boardPageId, info.BoardPageId);
        Assert.Equal("g-9", info.GroupId);
        Assert.Equal(snapshotPath, info.SnapshotPath);

        string emptyNote = PracticeLinkNote.Build(null, Guid.Empty, null);
        PracticeLinkInfo emptyInfo = PracticeLinkNote.Parse(emptyNote);
        Assert.Equal("", emptyInfo.AiSessionId);
        Assert.Equal(Guid.Empty, emptyInfo.BoardPageId);
        Assert.Equal("", emptyInfo.GroupId);
        Assert.Equal("", emptyInfo.SnapshotPath);

        PracticeLinkInfo nullInfo = PracticeLinkNote.Parse(null);
        Assert.Equal("", nullInfo.AiSessionId);
        Assert.Equal(Guid.Empty, nullInfo.BoardPageId);
        Assert.Equal("", PracticeLinkNote.Parse("   ").SnapshotPath);
    }
}