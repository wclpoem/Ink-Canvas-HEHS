using InkCanvas.Core.Ink;
using InkCanvas.Modules.TimeMachine;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>时间轴重建、多页/背景、检查点、视图/PPT 载荷与章节书签的纯逻辑测试（适配插件命名空间）。</summary>
public sealed class TimelineCoreTests
{
    private static InkStroke Stroke(float x)
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

    private static TimelineEvent Add(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static TimelineEvent Page(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static TimelineEvent Background(long timeMs, int page, string name) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.BackgroundChanged,
        Payload = name,
    };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
    };

    private static List<float> FrameXs(IReadOnlyList<InkStroke> frame)
        => frame.Select(s => s.Points[0].X).OrderBy(x => x).ToList();

    [Fact]
    public void BuildFrame_AddsStrokesUpToTime()
    {
        var s1 = Stroke(1);
        var s2 = Stroke(2);
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "White"),
            Add(100, 0, s1), Add(200, 0, s2)));

        Assert.Empty(timeline.BuildFrame(50));
        Assert.Equal(new List<float> { 1 }, FrameXs(timeline.BuildFrame(150)));
        Assert.Equal(new List<float> { 1, 2 }, FrameXs(timeline.BuildFrame(250)));
    }

    [Fact]
    public void BuildFrame_RemovesStrokes()
    {
        var s1 = Stroke(1);
        var s2 = Stroke(2);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, s1), Add(200, 0, s2),
            new TimelineEvent
            {
                TimeMs = 300,
                PageIndex = 0,
                Kind = TimelineEventKind.StrokesRemoved,
                StrokeIds = new[] { s1.Id.ToString() },
            }));

        Assert.Equal(2, timeline.BuildFrame(250).Count);
        Assert.Equal(new List<float> { 2 }, FrameXs(timeline.BuildFrame(350)));
    }

    [Fact]
    public void BuildFrame_ClearEmptiesPage()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, Stroke(1)), Add(200, 0, Stroke(2)),
            new TimelineEvent { TimeMs = 300, PageIndex = 0, Kind = TimelineEventKind.Cleared }));

        Assert.Equal(2, timeline.BuildFrame(250).Count);
        Assert.Empty(timeline.BuildFrame(350));
    }

    [Fact]
    public void BuildFrame_UndoRedoAppliesDiff()
    {
        var s1 = Stroke(1);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, s1),
            new TimelineEvent
            {
                TimeMs = 200,
                PageIndex = 0,
                Kind = TimelineEventKind.Undo,
                StrokeIds = new[] { s1.Id.ToString() },
            },
            new TimelineEvent
            {
                TimeMs = 300,
                PageIndex = 0,
                Kind = TimelineEventKind.Redo,
                Payload = InkTimeline.EncodeAddedIds(new[] { s1.Id.ToString() }),
            }));

        Assert.Single(timeline.BuildFrame(150));
        Assert.Empty(timeline.BuildFrame(250));
        Assert.Single(timeline.BuildFrame(350));
    }

    [Fact]
    public void BuildFrame_UndoRedo_ReAddsStrokeWithEmbeddedJson()
    {
        var shape = Stroke(7);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            new TimelineEvent
            {
                TimeMs = 100,
                PageIndex = 0,
                Kind = TimelineEventKind.Redo,
                Payload = InkTimeline.EncodeAddedStrokes(new[] { shape }),
            }));

        Assert.Equal(new List<float> { 7 }, FrameXs(timeline.BuildFrame(150)));
    }

    [Fact]
    public void BuildFrame_MultiPageFiltersByCurrentPage()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "Black"),
            Add(100, 0, Stroke(10)),
            Page(200, 1),
            Add(300, 1, Stroke(20))));

        Assert.Equal(new List<float> { 10 }, FrameXs(timeline.BuildFrame(150)));
        Assert.Empty(timeline.BuildFrame(250));
        Assert.Equal(new List<float> { 20 }, FrameXs(timeline.BuildFrame(350)));
    }

    [Fact]
    public void BackgroundAt_ChangesOverTime()
    {
        var timeline = new InkTimeline(Session(
            Page(0, 0), Background(0, 0, "Black"), Background(100, 0, "White")));

        Assert.Equal("Black", timeline.BackgroundAt(50));
        Assert.Equal("White", timeline.BackgroundAt(150));
    }

    [Fact]
    public void PageAt_FollowsPageChanged()
    {
        var timeline = new InkTimeline(Session(Page(0, 0), Page(100, 2)));

        Assert.Equal(0, timeline.PageAt(50));
        Assert.Equal(2, timeline.PageAt(150));
    }

    [Fact]
    public void IndexAt_And_TimeAtEvent()
    {
        var timeline = new InkTimeline(Session(
            new TimelineEvent { TimeMs = 0, Kind = TimelineEventKind.PageChanged },
            new TimelineEvent { TimeMs = 100, Kind = TimelineEventKind.PageChanged },
            new TimelineEvent { TimeMs = 200, Kind = TimelineEventKind.PageChanged }));

        Assert.Equal(-1, timeline.IndexAt(-1));
        Assert.Equal(0, timeline.IndexAt(0));
        Assert.Equal(1, timeline.IndexAt(150));
        Assert.Equal(2, timeline.IndexAt(999));
        Assert.Equal(200, timeline.TimeAtEvent(2));
        Assert.Equal(0, timeline.TimeAtEvent(99));
    }

    [Fact]
    public void Checkpoint_GeneratedByCount_AndNearestIsCorrect()
    {
        var events = new List<TimelineEvent> { Page(0, 0) };
        for (int i = 1; i <= 260; i++) events.Add(Add(i, 0, Stroke(i)));
        var timeline = new InkTimeline(Session(events.ToArray()));

        Assert.Equal(260, timeline.BuildFrame(260).Count);
        Assert.NotEmpty(timeline.Session.Checkpoints);

        var nearest = timeline.NearestCheckpoint(240);
        Assert.NotNull(nearest);
        Assert.True(nearest.EventIndex <= timeline.IndexAt(240));
    }

    [Fact]
    public void CursorPointAt_ReturnsLastStrokePointOnCurrentPage()
    {
        var s1 = Stroke(30);
        var s2 = Stroke(40);
        var timeline = new InkTimeline(Session(
            Page(0, 0),
            Add(100, 0, s1),
            Add(200, 0, s2)));

        var cursor = timeline.CursorPointAt(150);
        Assert.NotNull(cursor);
        Assert.Equal(s1.Points[^1].X, cursor.Value.X, 3);

        var latest = timeline.CursorPointAt(250);
        Assert.Equal(s2.Points[^1].X, latest.Value.X, 3);
    }

    [Fact]
    public void ViewChangedPayload_RoundTrips()
    {
        string payload = InkTimeline.EncodeViewChanged(1.5, 120, 80);
        var view = InkTimeline.DecodeViewChanged(payload);

        Assert.True(view.IsValid);
        Assert.Equal(1.5, view.Scale, 6);
        Assert.Equal(120, view.Tx, 6);
        Assert.Equal(80, view.Ty, 6);
    }

    [Fact]
    public void ViewChangedPayload_InvalidWhenUnparseable()
    {
        var view = InkTimeline.DecodeViewChanged("not-json");
        Assert.False(view.IsValid);
    }

    [Fact]
    public void PptSlideChangedPayload_RoundTrips_WithScreenshot()
    {
        string payload = InkTimeline.EncodePptSlideChanged(12, 3, "screenshots/a.png");
        var change = InkTimeline.DecodePptSlideChanged(payload);

        Assert.True(change.IsValid);
        Assert.Equal(12, change.SlideId);
        Assert.Equal(3, change.Index);
        Assert.Equal("screenshots/a.png", change.Screenshot);
    }

    [Fact]
    public void PptSlideChangedPayload_LegacyPlainNumberStillParses()
    {
        var change = InkTimeline.DecodePptSlideChanged("5");
        Assert.True(change.IsValid);
        Assert.Equal(5, change.Index);
    }

    [Fact]
    public void ApplyAutoBookmarks_GeneratesPageClearPptRosterTimer()
    {
        var session = Session(
            Page(0, 0),
            Add(100, 0, Stroke(1)),
            new TimelineEvent { TimeMs = 200, PageIndex = 0, Kind = TimelineEventKind.Cleared },
            new TimelineEvent { TimeMs = 3_000, PageIndex = 0, Kind = TimelineEventKind.PptSlideChanged,
                Payload = InkTimeline.EncodePptSlideChanged(7, 2, "") },
            new TimelineEvent { TimeMs = 5_000, PageIndex = 0, Kind = TimelineEventKind.RosterDrawn, Payload = "李四" },
            new TimelineEvent { TimeMs = 7_000, PageIndex = 0, Kind = TimelineEventKind.TimerEnded });

        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();

        Assert.Contains(session.Bookmarks, b => b.Kind == "cleared");
        Assert.Contains(session.Bookmarks, b => b.Kind == "ppt");
        Assert.Contains(session.Bookmarks, b => b.Kind == "roster" && b.Name.Contains("李四"));
        Assert.Contains(session.Bookmarks, b => b.Kind == "timer");
        Assert.All(session.Bookmarks, b => Assert.True(b.Auto));
    }

    [Fact]
    public void RenameBookmark_TurnsIntoManual()
    {
        var session = Session(
            Page(0, 0),
            new TimelineEvent { TimeMs = 100, PageIndex = 0, Kind = TimelineEventKind.Cleared });
        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();

        Assert.True(timeline.RenameBookmark(0, "第一节开始"));
        Assert.False(session.Bookmarks[0].Auto);
        Assert.Equal("manual", session.Bookmarks[0].Kind);
        Assert.Equal("第一节开始", session.Bookmarks[0].Name);
    }

    [Fact]
    public void RemoveBookmark_SuppressesItFromFutureAutoRegeneration()
    {
        var session = Session(
            Page(0, 0),
            new TimelineEvent { TimeMs = 3_000, PageIndex = 0, Kind = TimelineEventKind.Cleared });
        var timeline = new InkTimeline(session);
        timeline.ApplyAutoBookmarks();
        Assert.Equal(2, session.Bookmarks.Count);

        // 第二条为「清空」自动书签；删除后该时刻不再被自动规则重新生成。
        Assert.True(timeline.RemoveBookmark(1));
        Assert.Single(session.Bookmarks);
        Assert.Equal("page", session.Bookmarks[0].Kind);

        timeline.ApplyAutoBookmarks();
        Assert.Single(session.Bookmarks);
        Assert.DoesNotContain(session.Bookmarks, b => b.Kind == "cleared");
    }
}
