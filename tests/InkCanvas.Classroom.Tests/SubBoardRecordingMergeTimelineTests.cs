using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 子板录制合并后的时间轴回归：主/子 recorder（boardId 0/1）各自录制后合并到主会话，
/// 断言 BuildFrame / BuildObjectFrame 按板过滤、关键时间点指纹在「顺序 / 冷启动 / 倒退」
/// 三条回放路径上完全一致，且回放只读、不依赖网络或 AI 客户端程序集。
/// </summary>
public sealed class SubBoardRecordingMergeTimelineTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("inkcanvas-subboard-merge-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论。 */ }
    }

    private static BoardTextObject AiText(string content, float left, float top) => new()
    {
        Type = "text",
        Content = content,
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
        Instruction = "子板合并时间轴测试",
        CreatedAt = 1_730_000_000_000,
    };

    private static InkStroke NewStroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private InkRecorder Recorder(BoardService board, int boardId)
        => new(board, boardId, persistOnStop: false) { SessionDirectory = _directory };

    private static ClassroomSession CloneSession(ClassroomSession source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        CreatedAt = source.CreatedAt,
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
        Events = source.Events.Select(ev => new TimelineEvent
        {
            SessionId = ev.SessionId,
            Seq = ev.Seq,
            TimeMs = ev.TimeMs,
            PageIndex = ev.PageIndex,
            PptSlideId = ev.PptSlideId,
            BoardId = ev.BoardId,
            Kind = ev.Kind,
            StrokeId = ev.StrokeId,
            StrokeJson = ev.StrokeJson,
            StrokeIds = ev.StrokeIds,
            Payload = ev.Payload,
        }).ToList(),
    };

    private static string FrameFingerprint(InkTimeline timeline, long timeMs)
        => timeline.PageAt(timeMs)
            + "|" + string.Join(",", timeline.BuildFrame(timeMs).Select(StrokeSignature))
            + "|" + string.Join(";", timeline.BuildObjectFrame(timeMs, 0).Select(ObjectSignature))
            + "|" + string.Join(";", timeline.BuildObjectFrame(timeMs, 1).Select(ObjectSignature));

    private static string StrokeSignature(InkStroke stroke)
    {
        IReadOnlyList<InkPoint> points = stroke.Points;
        if (points.Count == 0) return "empty";
        return points.Count
            + ":" + points[0].X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + points[0].Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + points[^1].X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + points[^1].Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ObjectSignature(TimelineAiObject item)
        => string.Join("|",
            item.Id, item.Kind, item.PageIndex, item.BoardId, item.Content,
            item.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Transform is null ? "null" : string.Join(",", item.Transform.Select(value =>
                value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));

    [Fact]
    public async Task MergedMainAndSideRecording_FiltersFramesAndReplaysIdenticallyOffline()
    {
        var mainBoard = new BoardService();
        using var main = Recorder(mainBoard, boardId: 0);
        main.Start("主展板录制");

        // 主展板笔画通过录制器的外部事件入口写入（与 PPT / 点名事件同一路径）。
        InkStroke mainStroke = NewStroke(20f);
        main.Append(new TimelineEvent
        {
            TimeMs = main.ElapsedMs,
            PageIndex = 0,
            BoardId = 0,
            Kind = TimelineEventKind.StrokeAdded,
            StrokeId = mainStroke.Id.ToString(),
            StrokeJson = StrokeJsonCodec.Serialize(mainStroke),
        });
        mainBoard.AddAiObjectsAtomic(new[] { AiText("主板书", 10, 20) }, null, "主板书");
        await Task.Delay(25);

        var sideBoard = new BoardService();
        using var side = Recorder(sideBoard, boardId: 1);
        side.Start("子板录制");
        sideBoard.AddAiObjectsAtomic(new[] { AiText("子板书", 10, 220) }, null, "子板书");
        IReadOnlyList<TimelineEvent> sideEvents = side.CapturedEvents;
        side.Stop();

        // 子板事件并入主会话：同一份 session 中同时存在 boardId 0/1 的对象。
        main.MergeEvents(sideEvents);
        ClassroomSession session = main.Session;
        Assert.NotNull(session);

        // 合并后不得留下子会话 id，事件序号连续。
        Assert.All(session.Events, ev => Assert.Equal(session.Id, ev.SessionId));
        Assert.Equal(Enumerable.Range(1, session.Events.Count), session.Events.Select(ev => ev.Seq));
        Assert.Contains(session.Events, ev => ev.Kind == TimelineEventKind.AiStep && ev.BoardId == 0);
        Assert.Contains(session.Events, ev => ev.Kind == TimelineEventKind.AiStep && ev.BoardId == 1);

        long end = session.Events.Max(ev => ev.TimeMs);
        long[] keyTimes = { 0, Math.Max(0, end / 2), end };

        var timeline = new InkTimeline(session, eventsPerCheckpoint: 2, checkpointIntervalMs: 20);

        // 笔画只在主展板；对象按板过滤，不串板。
        IReadOnlyList<InkStroke> strokes = timeline.BuildFrame(end);
        Assert.Single(strokes);
        Assert.Contains(timeline.BuildObjectFrame(end, 0), item => item.Content == "主板书");
        Assert.DoesNotContain(timeline.BuildObjectFrame(end, 0), item => item.Content == "子板书");
        Assert.Contains(timeline.BuildObjectFrame(end, 1), item => item.Content == "子板书");
        Assert.DoesNotContain(timeline.BuildObjectFrame(end, 1), item => item.Content == "主板书");

        // 三条回放路径指纹一致：顺序访问 / 冷启动直接定位 / 倒退访问。
        Dictionary<long, string> fromStart = Walk(timeline, keyTimes);
        Dictionary<long, string> directSeek = keyTimes
            .ToDictionary(time => time,
                time => FrameFingerprint(new InkTimeline(CloneSession(session), 2, 20), time));
        Dictionary<long, string> backwards = Walk(timeline, keyTimes.Reverse());

        foreach (long time in keyTimes)
        {
            Assert.True(fromStart[time] == directSeek[time],
                $"{time}ms：顺序回放与冷启动定位的合并会话指纹必须一致。");
            Assert.True(fromStart[time] == backwards[time],
                $"{time}ms：顺序回放与倒退回放的合并会话指纹必须一致。");
        }

        // 回放只读：多次取帧不修改事件载荷。
        string before = string.Join("|", session.Events.Select(ev => ev.Payload));
        _ = timeline.BuildFrame(end);
        _ = timeline.BuildObjectFrame(end, 0);
        _ = timeline.BuildObjectFrame(end, 1);
        _ = timeline.SceneAt(end);
        Assert.Equal(before, string.Join("|", session.Events.Select(ev => ev.Payload)));

        // 回放不依赖网络：Core 时间轴程序集不引用 HTTP 或 AI 客户端。
        var assembly = typeof(InkTimeline).Assembly;
        string[] references = assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain("InkCanvas.Platform", references);
        Assert.DoesNotContain("InkCanvas.Modules.AiAssistant", references);
    }

    private static Dictionary<long, string> Walk(InkTimeline timeline, IEnumerable<long> order)
    {
        var result = new Dictionary<long, string>();
        foreach (long time in order) result[time] = FrameFingerprint(timeline, time);
        return result;
    }
}