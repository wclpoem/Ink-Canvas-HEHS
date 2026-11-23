using System.Security.Cryptography;
using System.Text;
using InkCanvas.Core.Ink;
using InkCanvas.Modules.TimeMachine;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>合成脚本跨页/背景/擦除/清空/撤销重做的端到端回放、重复帧一致性、导出采样与书签测试。</summary>
public sealed class EndToEndReplayTests
{
    private const long T1 = 250;
    private const long T2 = 450;
    private const long T3 = 850;
    private const long T4 = 950;
    private const long T5 = 1_050;
    private const long T6 = 1_250;
    private const long T7 = 1_350;
    private const long T8 = 1_750;

    private static InkStroke Stroke(float x, float y = 10f)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 5f, y);
        stroke.AddPoint(x + 10f, y + 2f);
        stroke.Complete();
        return stroke;
    }

    private static TimelineEvent Added(long timeMs, int page, InkStroke stroke) => new()
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

    private static TimelineEvent Ppt(long timeMs, int page, int slideId, int index) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PptSlideChanged,
        Payload = InkTimeline.EncodePptSlideChanged(slideId, index, ""),
    };

    private static TimelineEvent View(long timeMs, int page, double scale, double tx, double ty) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.ViewChanged,
        Payload = InkTimeline.EncodeViewChanged(scale, tx, ty),
    };

    private static TimelineEvent Removed(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokesRemoved,
        StrokeIds = strokes.Select(s => s.Id.ToString()).ToArray(),
    };

    private static TimelineEvent Undo(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Undo,
        StrokeIds = strokes.Select(s => s.Id.ToString()).ToArray(),
    };

    private static TimelineEvent Redo(long timeMs, int page, params InkStroke[] strokes) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Redo,
        Payload = InkTimeline.EncodeAddedIds(strokes.Select(s => s.Id.ToString())),
    };

    private static TimelineEvent Cleared(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.Cleared,
    };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "end-to-end-replay",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private sealed class Script
    {
        public ClassroomSession Session { get; init; }
        public float P0A { get; init; }
        public float P0B { get; init; }
        public float P1A { get; init; }
        public float P1B { get; init; }
        public float P1C { get; init; }
        public float P2A { get; init; }
    }

    private static Script CreateScript()
    {
        InkStroke p0a = Stroke(10);
        InkStroke p0b = Stroke(20, 15);
        InkStroke p1a = Stroke(30);
        InkStroke p1b = Stroke(40);
        InkStroke p1c = Stroke(50);
        InkStroke p2a = Stroke(60);

        ClassroomSession session = Session(
            Page(0, 0),
            Background(0, 0, "Black"),
            Ppt(0, 0, 101, 1),
            Added(100, 0, p0a),
            Added(200, 0, p0b),
            View(300, 0, 1.5, 120, 80),
            Page(400, 1),
            Background(400, 1, "Grid"),
            Added(500, 1, p1a),
            Added(600, 1, p1b),
            Removed(700, 1, p1a),
            Added(800, 1, p1c),
            Undo(900, 1, p1c),
            Redo(1_000, 1, p1c),
            Page(1_100, 2),
            Background(1_100, 2, "Coordinate"),
            View(1_150, 2, 2.0, -40, 12),
            Ppt(1_200, 2, 202, 2),
            Added(1_300, 2, p2a),
            Cleared(1_400, 1),
            Page(1_700, 1));

        return new Script
        {
            Session = session,
            P0A = 10f,
            P0B = 20f,
            P1A = 30f,
            P1B = 40f,
            P1C = 50f,
            P2A = 60f,
        };
    }

    private static IReadOnlyCollection<float> FrameXs(IReadOnlyList<InkStroke> frame)
        => frame.Select(s => s.Points[0].X).OrderBy(x => x).ToArray();

    private static string FrameHash(IReadOnlyList<InkStroke> frame)
    {
        var builder = new StringBuilder();
        foreach (InkStroke stroke in frame)
            builder.Append(StrokeJsonCodec.Serialize(stroke)).Append('\n');
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string FrameSignature(InkTimeline timeline, long timeMs)
        => FrameHash(timeline.BuildFrame(timeMs));

    [Fact]
    public void SyntheticSession_CrossPageBackgroundEraseClearMatchesEventOrder()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        AssertSceneAndFrame(timeline, T1, 0, "Black", 1, script.P0A, script.P0B);
        AssertSceneAndFrame(timeline, T2, 1, "Grid", 1);
        AssertSceneAndFrame(timeline, T3, 1, "Grid", 1, script.P1B, script.P1C);
        AssertSceneAndFrame(timeline, T4, 1, "Grid", 1, script.P1B);
        AssertSceneAndFrame(timeline, T5, 1, "Grid", 1, script.P1B, script.P1C);
        AssertSceneAndFrame(timeline, T6, 2, "Coordinate", 2);
        AssertSceneAndFrame(timeline, T7, 2, "Coordinate", 2, script.P2A);
        AssertSceneAndFrame(timeline, T8, 1, "Coordinate", 2);
    }

    private static void AssertSceneAndFrame(InkTimeline timeline, long timeMs, int page,
        string background, int pptSlide, params float[] expectedXs)
    {
        ClassroomScene scene = timeline.SceneAt(timeMs);
        Assert.Equal(page, scene.PageIndex);
        Assert.Equal(background, scene.Background);
        Assert.Equal(pptSlide, scene.PptSlideIndex);
        Assert.Equal("", scene.PptScreenshotPath);

        IReadOnlyList<InkStroke> frame = timeline.BuildFrame(timeMs);
        Assert.Equal(expectedXs.OrderBy(x => x).ToArray(), FrameXs(frame).ToArray());
        Assert.Equal(page, timeline.PageAt(timeMs));
        Assert.Equal(background, timeline.BackgroundAt(timeMs));
    }

    [Fact]
    public void RepeatedBuildFrame_IsBitIdenticalAcrossFivePasses()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        long[] points = { T1, T2, T3, T4, T5, T6, T7, T8, 0, 100, 1_700 };

        foreach (long timeMs in points)
        {
            string expected = FrameSignature(timeline, timeMs);
            for (int pass = 0; pass < 5; pass++)
            {
                Assert.Equal(expected, FrameSignature(timeline, timeMs));
                Assert.Equal(expected, FrameHash(new InkTimeline(script.Session).BuildFrame(timeMs)));
            }
            Assert.Equal(64, expected.Length);
        }
    }

    [Fact]
    public void ExportSampling_At1x_MatchesBuildFrameFingerprint()
    {
        AssertExportSampling(1.0, 33.333);
    }

    [Fact]
    public void ExportSampling_At2x_MatchesBuildFrameFingerprint()
    {
        AssertExportSampling(2.0, 66.667);
    }

    private static void AssertExportSampling(double speed, double millisecondsPerFrame)
    {
        var script = CreateScript();
        var options = new ExportOptions("sample.mp4", 0, 1_700, 30, 4_000_000, 640, 480, speed, false, false, false);
        IReadOnlyList<long> samples = TimeMachineExporter.SampleFrameTimes(options);
        int expectedCount = TimeMachineExporter.EstimateFrameCount(options);

        Assert.Equal(expectedCount, samples.Count);
        Assert.NotEmpty(samples);
        Assert.Equal(0, samples[0]);
        Assert.True(samples[^1] <= options.EndMs);

        var timeline = new InkTimeline(script.Session);
        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i] > samples[i - 1]);

        long frameSpan = samples.Count > 1 ? samples[1] - samples[0] : 0;
        Assert.InRange(frameSpan, (long)(millisecondsPerFrame - 1), (long)(millisecondsPerFrame + 1));

        var exportTimeline = new InkTimeline(script.Session);
        foreach (long timeMs in samples)
            Assert.Equal(FrameSignature(timeline, timeMs), FrameHash(exportTimeline.BuildFrame(timeMs)));
    }

    [Fact]
    public void BoardOnlyExport_UsesSameStrokeSetAsFullClassroom()
    {
        var script = CreateScript();
        var full = new ExportOptions("full.mp4", 0, 1_700, 30, 4_000_000, 640, 480, 1, false, false, false);
        var boardOnly = new ExportOptions("board.mp4", 0, 1_700, 30, 4_000_000, 640, 480, 1, true, false, false);

        Assert.True(TimeMachineExporter.ShouldDrawPptBackground(full));
        Assert.False(TimeMachineExporter.ShouldDrawPptBackground(boardOnly));

        IReadOnlyList<long> fullTimes = TimeMachineExporter.SampleFrameTimes(full);
        IReadOnlyList<long> boardTimes = TimeMachineExporter.SampleFrameTimes(boardOnly);
        Assert.Equal(fullTimes, boardTimes);

        var timeline = new InkTimeline(script.Session);
        foreach (long timeMs in fullTimes)
        {
            Assert.Equal(FrameXs(timeline.BuildFrame(timeMs)), FrameXs(timeline.BuildFrame(timeMs)));
            Assert.Equal(FrameHash(timeline.BuildFrame(timeMs)), FrameHash(timeline.BuildFrame(timeMs)));
        }

        ClassroomScene scene = timeline.SceneAt(1_250);
        Assert.Equal(2, scene.PptSlideIndex);
        Assert.Equal("", scene.PptScreenshotPath);
    }

    [Fact]
    public void Bookmarks_AddRenameRemoveSummaryAndDescribe()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        int index = timeline.AddBookmark(850, "重点讲解");
        Assert.InRange(index, 0, script.Session.Bookmarks.Count - 1);
        SessionBookmark bookmark = script.Session.Bookmarks[index];
        Assert.Equal(850, bookmark.TimeMs);
        Assert.Equal(1, bookmark.PageIndex);
        Assert.Equal("重点讲解", bookmark.Name);
        Assert.False(bookmark.Auto);
        Assert.Equal("manual", bookmark.Kind);

        Assert.True(timeline.RenameBookmark(index, "重点讲解（改）"));
        Assert.Equal("重点讲解（改）", script.Session.Bookmarks[index].Name);
        Assert.False(timeline.RenameBookmark(index, "   "));
        Assert.False(timeline.RenameBookmark(-1, "x"));
        Assert.False(timeline.RenameBookmark(99, "x"));

        string summary = timeline.BookmarkSummary(bookmark, 20);
        string description = timeline.DescribeBookmark(bookmark, 20);
        Assert.Equal("撤销", summary);
        Assert.StartsWith("00:00 · 第 2 页", description);
        Assert.Contains(summary, description);

        int second = timeline.AddBookmark(1_500, "清空后");
        Assert.True(second > index);
        Assert.True(timeline.RemoveBookmark(second));
        Assert.Single(script.Session.Bookmarks);
        Assert.False(timeline.RemoveBookmark(second));
        Assert.False(timeline.RemoveBookmark(-1));

        Assert.Equal("", timeline.BookmarkSummary(null));
        Assert.Equal("", timeline.DescribeBookmark(null));
    }

    [Fact]
    public void PptSlidePayload_EncodesSlideIdIndexAndEmptyScreenshot()
    {
        PptSlideChange change = InkTimeline.DecodePptSlideChanged(
            InkTimeline.EncodePptSlideChanged(202, 2, ""));

        Assert.True(change.IsValid);
        Assert.Equal(202, change.SlideId);
        Assert.Equal(2, change.Index);
        Assert.Equal("", change.Screenshot);

        PptSlideChange empty = InkTimeline.DecodePptSlideChanged("");
        Assert.False(empty.IsValid);

        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);
        Assert.Equal(1, timeline.SceneAt(1_000).PptSlideIndex);
        Assert.Equal(2, timeline.SceneAt(1_200).PptSlideIndex);
        Assert.Equal("", timeline.SceneAt(1_200).PptScreenshotPath);
    }

    [Fact]
    public void UndoRedo_RestoresStrokeInFrameOrder()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Contains(script.P1C, FrameXs(timeline.BuildFrame(850)));
        Assert.DoesNotContain(script.P1C, FrameXs(timeline.BuildFrame(950)));
        Assert.Contains(script.P1C, FrameXs(timeline.BuildFrame(1_050)));
        Assert.Equal(
            new[] { script.P1B, script.P1C }.OrderBy(x => x),
            FrameXs(timeline.BuildFrame(1_050)).OrderBy(x => x));
    }

    [Fact]
    public void ClearedSecondPage_RemovesOnlyItsStrokesWhenReturning()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Contains(script.P2A, FrameXs(timeline.BuildFrame(1_400)));
        Assert.Equal(2, timeline.SceneAt(1_400).PageIndex);
        Assert.Empty(timeline.BuildFrame(1_750));
        Assert.Equal(1, timeline.PageAt(1_750));
        Assert.Equal(2, timeline.SceneAt(1_650).PptSlideIndex);
        Assert.Contains(script.P2A, FrameXs(timeline.BuildFrame(1_650)));
    }

    [Fact]
    public void TimelineIndexAt_IsChronologicalAndMonotonic()
    {
        var script = CreateScript();
        var timeline = new InkTimeline(script.Session);

        Assert.Equal(-1, timeline.IndexAt(-1));
        long previousTime = long.MinValue;
        for (int i = 0; i < script.Session.Events.Count; i++)
        {
            TimelineEvent e = script.Session.Events[i];
            Assert.True(e.TimeMs >= previousTime);
            previousTime = e.TimeMs;
            Assert.True(timeline.IndexAt(e.TimeMs) >= i);
        }

        Assert.Equal(script.Session.Events.Count - 1,
            timeline.IndexAt(script.Session.Events[^1].TimeMs));
        Assert.Equal(script.Session.Events.Count - 1, timeline.IndexAt(99_999));
        Assert.Equal(script.Session.Events[^1].TimeMs, timeline.Duration);
    }
}
