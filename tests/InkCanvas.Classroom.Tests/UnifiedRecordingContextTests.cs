using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 1B：统一录制上下文。覆盖 AI 步骤元数据（boardId + slideId/source/requestId）、
/// 跨板上下文过滤、子板事件合并以及 persistOnStop=false 不落盘。
/// </summary>
public sealed class UnifiedRecordingContextTests
{
    // ---- helpers ---------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-unified-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static BoardTextObject AiText(string content, float left = 10, float top = 10) => new()
    {
        Type = "text",
        Content = content,
        Title = "",
        FontFamily = "Microsoft YaHei UI",
        FontSize = 32,
        Color = "#FF202024",
        LineHeight = 1.4,
        Align = "left",
        Left = left,
        Top = top,
        Width = 320,
        Height = 140,
        Source = "ai",
        Instruction = "统一上下文测试",
        CreatedAt = 1_730_000_000_000,
    };

    private static List<TimelineEvent> AiSteps(InkRecorder recorder)
        => recorder.CapturedEvents
            .Where(item => item.Kind == TimelineEventKind.AiStep)
            .ToList();

    private static AiObjectsPayload Payload(TimelineEvent aiStep)
    {
        Assert.True(AiObjectPayloadCodec.TryDecode(aiStep.Payload, out AiObjectsPayload payload));
        return payload;
    }

    private static InkRecorder Recorder(BoardService board, int boardId, bool persistOnStop, string directory)
        => new(board, boardId, persistOnStop) { SessionDirectory = directory };

    // ---- 1) AiStep 元数据 -----------------------------------------------

    [Fact]
    public void AiStep_CarriesUnifiedContextMetadataForMainAndSideBoard()
    {
        string dir = TempDir();
        try
        {
            var mainBoard = new BoardService();
            using var main = Recorder(mainBoard, boardId: 0, persistOnStop: false, directory: dir);
            main.Start("主展板录制");
            main.SetAiContext(new ClassroomContext
            {
                Kind = BoardCanvasKind.MainBoard,
                BoardId = "main",
                PageId = mainBoard.CurrentPageId,
                PageIndex = mainBoard.CurrentIndex,
                SlideId = 7,
                Source = ContentSource.AiWrite,
                RequestId = "req-test-1",
            });
            mainBoard.AddAiObjectsAtomic(new[] { AiText("主展板文本") }, null, "test");

            TimelineEvent mainStep = Assert.Single(AiSteps(main));
            AiObjectsPayload mainPayload = Payload(mainStep);
            Assert.Equal(0, mainPayload.BoardId);
            Assert.Equal(7, mainPayload.SlideId);
            Assert.Equal("AiWrite", mainPayload.Source);
            Assert.Equal("req-test-1", mainPayload.RequestId);
            Assert.Equal(0, mainStep.BoardId);
            Assert.Equal(mainBoard.CurrentIndex, mainStep.PageIndex);

            var sideBoard = new BoardService();
            using var side = Recorder(sideBoard, boardId: 1, persistOnStop: false, directory: dir);
            side.Start("子板录制");
            side.SetAiContext(new ClassroomContext
            {
                Kind = BoardCanvasKind.SideBoard,
                BoardId = "side",
                PageId = sideBoard.CurrentPageId,
                PageIndex = sideBoard.CurrentIndex,
                SlideId = null,
                Source = ContentSource.AiShape,
                RequestId = "req-side",
            });
            sideBoard.AddAiObjectsAtomic(new[] { AiText("子板文本", 10, 200) }, null, "test");

            TimelineEvent sideStep = Assert.Single(AiSteps(side));
            AiObjectsPayload sidePayload = Payload(sideStep);
            Assert.Equal(1, sidePayload.BoardId);
            Assert.Null(sidePayload.SlideId);
            Assert.Equal("AiShape", sidePayload.Source);
            Assert.Equal("req-side", sidePayload.RequestId);
            Assert.Equal(1, sideStep.BoardId);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void AiStep_ContextIsFilteredByBoardOwnership()
    {
        string dir = TempDir();
        try
        {
            // 主展板录制器收到子板上下文：应按未绑定处理，元数据留空。
            var mainBoard = new BoardService();
            using var main = Recorder(mainBoard, boardId: 0, persistOnStop: false, directory: dir);
            main.Start("主展板过滤");
            main.SetAiContext(new ClassroomContext
            {
                Kind = BoardCanvasKind.SideBoard,
                BoardId = "side",
                SlideId = 3,
                Source = ContentSource.AiShape,
                RequestId = "req-side-on-main",
            });
            mainBoard.AddAiObjectsAtomic(new[] { AiText("主展板过滤文本") }, null, "test");

            TimelineEvent mainStep = Assert.Single(AiSteps(main));
            AiObjectsPayload mainPayload = Payload(mainStep);
            Assert.Equal(0, mainPayload.BoardId);
            Assert.Equal(0, mainStep.BoardId);
            Assert.Null(mainPayload.SlideId);
            Assert.Equal("", mainPayload.Source);
            Assert.Equal("", mainPayload.RequestId);

            // 子板录制器收到主展板上下文：同样不匹配，但事件归属仍是 boardId=1。
            var sideBoard = new BoardService();
            using var side = Recorder(sideBoard, boardId: 1, persistOnStop: false, directory: dir);
            side.Start("子板过滤");
            side.SetAiContext(new ClassroomContext
            {
                Kind = BoardCanvasKind.MainBoard,
                BoardId = "main",
                SlideId = 3,
                Source = ContentSource.AiWrite,
                RequestId = "req-main-on-side",
            });
            sideBoard.AddAiObjectsAtomic(new[] { AiText("子板过滤文本") }, null, "test");

            TimelineEvent sideStep = Assert.Single(AiSteps(side));
            AiObjectsPayload sidePayload = Payload(sideStep);
            Assert.Equal(1, sidePayload.BoardId);
            Assert.Equal(1, sideStep.BoardId);
            Assert.Null(sidePayload.SlideId);
            Assert.Equal("", sidePayload.Source);
            Assert.Equal("", sidePayload.RequestId);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void AiStep_ClearingContextLeavesMetadataEmpty()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = Recorder(board, boardId: 0, persistOnStop: false, directory: dir);
            recorder.Start("上下文清除录制");

            recorder.SetAiContext(new ClassroomContext
            {
                Kind = BoardCanvasKind.MainBoard,
                BoardId = "main",
                PageId = board.CurrentPageId,
                SlideId = 9,
                Source = ContentSource.AiWrite,
                RequestId = "req-before-clear",
            });
            board.AddAiObjectsAtomic(new[] { AiText("清除前文本") }, null, "test");

            recorder.SetAiContext(null);
            board.AddAiObjectsAtomic(new[] { AiText("清除后文本", 10, 200) }, null, "test");

            List<TimelineEvent> steps = AiSteps(recorder);
            Assert.Equal(2, steps.Count);

            AiObjectsPayload before = Payload(steps[0]);
            Assert.Equal(9, before.SlideId);
            Assert.Equal("AiWrite", before.Source);
            Assert.Equal("req-before-clear", before.RequestId);

            AiObjectsPayload after = Payload(steps[1]);
            Assert.Null(after.SlideId);
            Assert.Equal("", after.Source);
            Assert.Equal("", after.RequestId);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    // ---- 2) MergeEvents --------------------------------------------------

    [Fact]
    public async Task MergeEvents_MergesSideEventsIntoMainSessionInTimeOrder()
    {
        string dir = TempDir();
        try
        {
            var mainBoard = new BoardService();
            var main = Recorder(mainBoard, boardId: 0, persistOnStop: false, directory: dir);
            main.Start("主会话");
            await Task.Delay(20);
            mainBoard.AddAiObjectsAtomic(new[] { AiText("主板书文本") }, null, "test");
            main.Stop();

            var sideBoard = new BoardService();
            var side = Recorder(sideBoard, boardId: 1, persistOnStop: false, directory: dir);
            side.Start("子板会话");
            sideBoard.AddAiObjectsAtomic(new[] { AiText("子板文本", 10, 200) }, null, "test");
            side.Stop();

            int mainBefore = main.CapturedEvents.Count;
            int sideCount = side.CapturedEvents.Count;
            main.MergeEvents(side.CapturedEvents);

            IReadOnlyList<TimelineEvent> merged = main.CapturedEvents;

            // 实现只在主会话上追加，不做跨会话去重：总数等于两者之和。
            Assert.Equal(mainBefore + sideCount, merged.Count);

            List<long> times = merged.Select(item => item.TimeMs).ToList();
            Assert.Equal(times.OrderBy(time => time), times);

            Assert.Equal(1, merged.Count(item =>
                item.Kind == TimelineEventKind.AiStep && item.BoardId == 0));
            Assert.Equal(1, merged.Count(item =>
                item.Kind == TimelineEventKind.AiStep && item.BoardId == 1));

            Assert.All(merged, item => Assert.Equal(main.Session.Id, item.SessionId));
            Assert.Equal(Enumerable.Range(1, merged.Count), merged.Select(item => item.Seq));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    // ---- 3) persistOnStop=false 不落盘 -----------------------------------

    [Fact]
    public void Stop_WithPersistOnStopFalse_DoesNotWriteSessionFile()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            var recorder = Recorder(board, boardId: 1, persistOnStop: false, directory: dir);
            recorder.Start("子板不落盘");
            board.AddAiObjectsAtomic(new[] { AiText("子板不落盘文本") }, null, "test");
            string sessionId = recorder.Session.Id;
            recorder.Stop();

            // ClassroomSessionStore.Save 产生 "<id>.json"；false 分支只清理 journal 与快照。
            Assert.Empty(Directory.GetFiles(dir, "*.json"));
            Assert.False(File.Exists(ClassroomSessionStore.PathFor(sessionId, dir)));
            Assert.Empty(Directory.GetFiles(dir, "*.journal"));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void Stop_WithPersistOnStopTrue_WritesSingleSessionFile()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            var recorder = Recorder(board, boardId: 0, persistOnStop: true, directory: dir);
            recorder.Start("主会话落盘");
            board.AddAiObjectsAtomic(new[] { AiText("主会话落盘文本") }, null, "test");
            string sessionId = recorder.Session.Id;
            recorder.Stop();

            string expected = ClassroomSessionStore.PathFor(sessionId, dir);
            Assert.True(File.Exists(expected));
            Assert.Equal(expected, Assert.Single(Directory.GetFiles(dir, "*.json")));

            ClassroomSession saved = ClassroomSessionStore.Load(expected);
            Assert.Equal(sessionId, saved.Id);
            Assert.Contains(saved.Events, item => item.Kind == TimelineEventKind.AiStep);
        }
        finally
        {
            RemoveDir(dir);
        }
    }
}