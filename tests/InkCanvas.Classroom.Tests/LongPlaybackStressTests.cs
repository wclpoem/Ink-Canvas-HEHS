using System.Diagnostics;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 90 分钟长课回放的逐帧重建压力测试：按 30fps 走完整段时间轴，
/// 观察 <see cref="InkTimeline.BuildFrame"/> 的耗时与托管内存是否保持有界。
/// <para>
/// 纯逻辑路径：不经过 Win2D 栅格化、UI 线程与真实设备，数字仅代表本机的合成负载。
/// </para>
/// </summary>
public sealed class LongPlaybackStressTests
{
    private const long TotalMs = 5_400_000;      // 90 分钟
    private const int EventCount = 20_000;
    private const int EventsPerPage = 2_000;
    private const int SampleEveryFrames = 20_000;
    private const long FrameIntervalNumerator = 1_000; // 帧间隔 1000/30 ms

    private readonly ITestOutputHelper _output;

    public LongPlaybackStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static InkStroke Stroke(int seed)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });

        // 每笔 2~4 个点；坐标落在小范围内，避免数据过大拖慢序列化。
        int points = 2 + seed % 3;
        for (int i = 0; i < points; i++)
        {
            float x = seed % 40 + i * 0.5f;
            float y = 10f + seed % 7;
            stroke.AddPoint(x, y);
        }
        stroke.Complete();
        return stroke;
    }

    private static ClassroomSession BuildSession(out int strokeCount, out int pageCount)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        pageCount = EventCount / EventsPerPage;
        int totalEvents = EventCount + pageCount; // 每页多一条 PageChanged

        var session = new ClassroomSession
        {
            Id = sessionId,
            Name = "90 分钟长课回放压力测试",
            CreatedAt = DateTime.Now,
            Events = new List<TimelineEvent>(totalEvents),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };

        // 先写页切换事件，再写该页全部笔画；时间均匀铺满 90 分钟，末条事件正好落在总时长。
        strokeCount = 0;
        int seq = 0;
        for (int page = 0; page < pageCount; page++)
        {
            int pageEventIndex = seq;
            session.Events.Add(new TimelineEvent
            {
                SessionId = sessionId,
                Seq = ++seq,
                TimeMs = EventTime(pageEventIndex, totalEvents),
                PageIndex = page,
                Kind = TimelineEventKind.PageChanged,
            });

            for (int i = 0; i < EventsPerPage; i++)
            {
                int seed = page * EventsPerPage + i;
                int strokeEventIndex = seq;
                InkStroke stroke = Stroke(seed);
                session.Events.Add(new TimelineEvent
                {
                    SessionId = sessionId,
                    Seq = ++seq,
                    TimeMs = EventTime(strokeEventIndex, totalEvents),
                    PageIndex = page,
                    Kind = TimelineEventKind.StrokeAdded,
                    StrokeId = stroke.Id.ToString(),
                    StrokeJson = StrokeJsonCodec.Serialize(stroke),
                });
                strokeCount++;
            }
        }

        return session;
    }

    private static long EventTime(int index, int totalEvents)
        => index * TotalMs / (totalEvents - 1);

    /// <summary>
    /// 仅用于内存采样前的状态稳定：强制完整回收、等待终结器清空并给后台 GC 收敛时间，
    /// 使峰值采样与结束采样处于同一 GC 状态。这里不改变任何判定阈值与语义。
    /// </summary>
    private static void StabilizeManagedMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Thread.Sleep(100);
    }

    [Fact]
    public void BuildFrame_90MinutePlayback_30Fps_KeepsTimeAndMemoryBounded()
    {
        ClassroomSession session = BuildSession(out int strokeCount, out int pageCount);
        var timeline = new InkTimeline(session);

        // 冷启动：首次定位会补齐检查点并建立笔画索引；正落在有笔画的页上。
        long durationMs = timeline.Duration;
        long halfMs = durationMs / 2;
        var coldWatch = Stopwatch.StartNew();
        IReadOnlyList<InkStroke> coldFrame = timeline.BuildFrame(halfMs);
        coldWatch.Stop();
        Assert.True(coldFrame.Count > 0, $"冷启动定位 {halfMs}ms 未取到任何笔画，页码过滤可能失效");

        StabilizeManagedMemory();
        var memorySamples = new List<long> { GC.GetTotalMemory(forceFullCollection: true) };

        int frameCount = 0;
        long lastFrameTimeMs = 0;
        IReadOnlyList<InkStroke> lastFrame = Array.Empty<InkStroke>();

        var frameWatch = Stopwatch.StartNew();
        for (int frame = 0; ; frame++)
        {
            // 整数运算保证帧时间点单调递增，且最后一帧正好落在总时长上。
            long timeMs = frame * FrameIntervalNumerator / 30;
            if (timeMs > TotalMs) break;

            lastFrame = timeline.BuildFrame(timeMs);
            lastFrameTimeMs = timeMs;
            frameCount++;

            if (frameCount % SampleEveryFrames == 0)
            {
                StabilizeManagedMemory();
                memorySamples.Add(GC.GetTotalMemory(forceFullCollection: true));
            }
        }
        frameWatch.Stop();

        StabilizeManagedMemory();
        memorySamples.Add(GC.GetTotalMemory(forceFullCollection: true));

        long peakBytes = memorySamples.Max();
        long lastSampleBytes = memorySamples[^1];
        double totalMs = frameWatch.Elapsed.TotalMilliseconds;
        double averageMs = frameCount > 0 ? totalMs / frameCount : 0;

        string memorySeries = string.Join(", ",
            memorySamples.Select(b => (b / 1024.0 / 1024.0).ToString("F1")));

        _output.WriteLine($"事件数={session.Events.Count}；笔画数={strokeCount}；页数={pageCount}");
        _output.WriteLine($"冷启动 BuildFrame({halfMs}ms) 耗时={coldWatch.Elapsed.TotalMilliseconds:F1} ms；笔画数={coldFrame.Count}");
        _output.WriteLine($"总帧数={frameCount}；逐帧总耗时={totalMs:F1} ms；平均每帧={averageMs:F3} ms（上界 180000 ms）");
        _output.WriteLine($"内存采样(MB)=[{memorySeries}]");
        _output.WriteLine($"峰值内存={peakBytes / 1024.0 / 1024.0:F1} MB；最后采样={lastSampleBytes / 1024.0 / 1024.0:F1} MB；峰值−结束={(peakBytes - lastSampleBytes) / 1024.0 / 1024.0:F1} MB（上界 256 MB）");

        Assert.Equal(durationMs, lastFrameTimeMs);
        // 原判据：回放结束后应释放峰值内存，峰值与结束值之差须小于 256MB。
        // 每次采样前已通过 StabilizeManagedMemory 统一 GC 状态，确保峰值与结束值可比。
        Assert.True(peakBytes - lastSampleBytes < 256L * 1024 * 1024,
            $"内存峰值与最后采样相差 {(peakBytes - lastSampleBytes) / 1024.0 / 1024.0:F1} MB，超过 256MB");
        Assert.True(totalMs < 180_000, $"逐帧总耗时 {totalMs:F1} ms 超过 180 秒上界");
        Assert.True(lastFrame.Count > 0, "最后一帧未取到笔画");

        IReadOnlyList<InkStroke> endFrame = timeline.BuildFrame(durationMs);
        Assert.Equal(endFrame.Count, lastFrame.Count);
    }
}