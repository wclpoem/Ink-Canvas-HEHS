using InkCanvas.Core.Ink;
using InkCanvas.Modules.TimeMachine;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>长会话（数千笔画）下检查点增量回放与缓存边界的测试。</summary>
public sealed class LongPlaybackStressTests
{
    private static InkStroke Stroke(float index)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(index, 10f);
        stroke.AddPoint(index + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static ClassroomSession BuildSession(int strokeCount, int msPerStroke = 10)
    {
        var events = new List<TimelineEvent>
        {
            new() { TimeMs = 0, PageIndex = 0, Kind = TimelineEventKind.PageChanged },
        };
        for (int i = 1; i <= strokeCount; i++)
        {
            var stroke = Stroke(i);
            events.Add(new TimelineEvent
            {
                TimeMs = i * msPerStroke,
                PageIndex = 0,
                Kind = TimelineEventKind.StrokeAdded,
                StrokeId = stroke.Id.ToString(),
                StrokeJson = StrokeJsonCodec.Serialize(stroke),
            });
        }
        return new ClassroomSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "stress",
            CreatedAt = DateTime.Now,
            Events = events,
            Checkpoints = new List<SessionCheckpoint>(),
        };
    }

    [Fact]
    public void ThousandsOfStrokes_FramesMatchExpectedCount()
    {
        const int count = 3000;
        var timeline = new InkTimeline(BuildSession(count));

        long probe1 = 5_000;
        long probe2 = 15_000;
        long probe3 = count * 10L;

        int expected1 = Math.Min(count, (int)(probe1 / 10));
        int expected2 = Math.Min(count, (int)(probe2 / 10));
        int expected3 = count;

        Assert.Equal(expected1, timeline.BuildFrame(probe1).Count);
        Assert.Equal(expected2, timeline.BuildFrame(probe2).Count);
        Assert.Equal(expected3, timeline.BuildFrame(probe3).Count);
    }

    [Fact]
    public void Checkpoints_AreGeneratedAndNearestStaysBeforeTarget()
    {
        const int count = 2500;
        var timeline = new InkTimeline(BuildSession(count));
        timeline.BuildFrame(24_900);

        Assert.NotEmpty(timeline.Session.Checkpoints);
        foreach (long probe in new[] { 10_000L, 20_000L, 24_900L })
        {
            var nearest = timeline.NearestCheckpoint(probe);
            Assert.NotNull(nearest);
            Assert.True(nearest.TimeMs <= probe);
            Assert.True(nearest.EventIndex <= timeline.IndexAt(probe));
        }
    }

    [Fact]
    public void RepeatedSeeks_KeepConsistentFrame()
    {
        const int count = 1200;
        var timeline = new InkTimeline(BuildSession(count));

        var first = timeline.BuildFrame(8_000).Count;
        var again = timeline.BuildFrame(8_000).Count;
        Assert.Equal(first, again);
        Assert.Equal(800, first);
    }
}
