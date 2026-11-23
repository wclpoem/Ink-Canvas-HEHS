using System.Reflection;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Modules.TimeMachine;
using Microsoft.UI.Xaml;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>录制器事件桥接、自动书签、种子笔画、画板关闭停止与停止后状态测试。</summary>
public sealed class RecorderTests
{
    private sealed class FakeHost : ITimeMachineHost
    {
        public BoardService Board { get; } = new();
        public InkController ActiveController => Board.Current;
        public bool BoardOpen { get; set; } = true;
        public PptInkSession Ppt => null;
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"inkcanvas-tm-host-{Guid.NewGuid():N}");
        public Window Owner => null;
        public int Theme => 0;

        public event Action ActiveControllerChanged;
        public event Action BoardOpenChanged;
        public event Action<ClassroomSignal> ClassroomSignal;

        public string ScreenshotPath { get; set; } = "";

        public Task<string> CaptureScreenshotAsync() => Task.FromResult(ScreenshotPath);
        public Task NotifyAsync(string title, string message) => Task.CompletedTask;
        public void ConfigureWindow(Window window, int width = 1120, int height = 820) { }

        public void RaiseSignal(ClassroomSignal signal) => ClassroomSignal?.Invoke(signal);
        public void RaiseBoardOpenChanged() => BoardOpenChanged?.Invoke();
        public void RaiseActiveControllerChanged() => ActiveControllerChanged?.Invoke();
    }

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

    [Fact]
    public void Start_RecordsInitialPageAndBackground()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);

        recorder.Start();
        try
        {
            Assert.True(recorder.IsRecording);
            Assert.Contains(recorder.Session.Events, e => e.Kind == TimelineEventKind.PageChanged);
            Assert.Contains(recorder.Session.Events, e => e.Kind == TimelineEventKind.BackgroundChanged);
        }
        finally
        {
            var session = recorder.Stop();
            Assert.False(recorder.IsRecording);
            Assert.NotNull(session);
        }
    }

    [Fact]
    public void SeedExistingStrokes_AreRecordedAtStart()
    {
        var host = new FakeHost();
        host.Board.Current.Document.Add(Stroke(17));

        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            Assert.Contains(recorder.Session.Events,
                e => e.Kind == TimelineEventKind.StrokeAdded && e.StrokeJson is not null);
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void RosterSignal_AppendsEventAndBookmark()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(ClassroomSignalKind.RosterDrawn, "王五"));

            Assert.Contains(recorder.Session.Events, e =>
                e.Kind == TimelineEventKind.RosterDrawn && e.Payload == "王五");
            Assert.Contains(recorder.Session.Bookmarks, b => b.Kind == "roster" && b.Name.Contains("王五"));
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Theory]
    [InlineData(ClassroomSignalKind.TimerStarted)]
    [InlineData(ClassroomSignalKind.TimerStopped)]
    public void TimerSignal_AppendsMatchingEvent(ClassroomSignalKind kind)
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(kind, "60000"));
            Assert.Contains(recorder.Session.Events, e => e.Kind == ToEventKind(kind));
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void TimerEnded_Signal_AppendsBookmark()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(ClassroomSignalKind.TimerEnded));
            Assert.Contains(recorder.Session.Bookmarks, b => b.Kind == "timer");
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Theory]
    [InlineData(ClassroomSignalKind.TimerEnded)]
    [InlineData(ClassroomSignalKind.PracticeFinished)]
    public async Task EndedSignals_CaptureClassroomScreenshot(ClassroomSignalKind kind)
    {
        var host = new FakeHost();
        string pngPath = Path.Combine(Path.GetTempPath(), $"tm-shot-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3, 4 });
        host.ScreenshotPath = pngPath;

        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(kind, "练习结束"));
            await recorder.PendingCaptures;

            var ended = Assert.Single(recorder.Session.Events, e => e.Kind == TimelineEventKind.TimerEnded);
            Assert.Equal(pngPath, ended.Screenshot);
        }
        finally
        {
            recorder.Stop();
            File.Delete(pngPath);
        }
    }

    [Fact]
    public async Task RosterDrawn_DoesNotCaptureScreenshot()
    {
        var host = new FakeHost();
        host.ScreenshotPath = Path.Combine(Path.GetTempPath(), $"tm-shot-{Guid.NewGuid():N}.png");

        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(ClassroomSignalKind.RosterDrawn, "赵六"));
            await recorder.PendingCaptures;

            var roster = Assert.Single(recorder.Session.Events, e => e.Kind == TimelineEventKind.RosterDrawn);
            Assert.Null(roster.Screenshot);
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void PracticeFinished_MapsToTimerEndedBookmark()    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.RaiseSignal(new ClassroomSignal(ClassroomSignalKind.PracticeFinished, "练习结束"));
            Assert.Contains(recorder.Session.Events, e => e.Kind == TimelineEventKind.TimerEnded);
            Assert.Contains(recorder.Session.Bookmarks, b => b.Kind == "practice");
            Assert.Contains(recorder.Session.Bookmarks, b => b.Name == "练习结束");
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void BackgroundChange_IsRecordedWithEnumName()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            host.Board.SetBackground(BoardBackground.Grid);
            Assert.Contains(recorder.Session.Events, e =>
                e.Kind == TimelineEventKind.BackgroundChanged && e.Payload == "Grid");
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void PageChange_AppendsPageEventAndBookmark()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            Assert.True(host.Board.InsertPageAfterCurrent());
            host.Board.GoTo(1);

            Assert.Contains(recorder.Session.Events, e => e.Kind == TimelineEventKind.PageChanged);
            Assert.Contains(recorder.Session.Bookmarks, b => b.Kind == "page");
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void BoardClosed_TriggersBoardClosedCallback()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        bool closed = false;
        recorder.BoardClosed += () => closed = true;
        recorder.Start();

        host.BoardOpen = false;
        host.RaiseBoardOpenChanged();

        Assert.True(closed);
        recorder.Stop();
    }

    [Fact]
    public void DuplicateEvent_IsDeduplicated()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        try
        {
            var ev = new TimelineEvent
            {
                TimeMs = 100,
                PageIndex = 0,
                Kind = TimelineEventKind.RosterDrawn,
                Payload = "张三",
            };
            recorder.Append(ev);
            recorder.Append(ev);

            Assert.Equal(1, recorder.Session.Events.Count(e =>
                e.Kind == TimelineEventKind.RosterDrawn && e.Payload == "张三"));
        }
        finally
        {
            recorder.Stop();
        }
    }

    [Fact]
    public void Stop_IsIdempotentAndLeavesSessionIntact()
    {
        var host = new FakeHost();
        var recorder = new TimeMachineRecorder(host);
        recorder.Start();
        host.RaiseSignal(new ClassroomSignal(ClassroomSignalKind.RosterDrawn, "李四"));

        var first = recorder.Stop();
        var second = recorder.Stop();

        Assert.Same(first, second);
        Assert.False(recorder.IsRecording);
        Assert.Contains(first.Events, e => e.Kind == TimelineEventKind.RosterDrawn);
    }

    [Fact]
    public void Recorder_DoesNotExposePauseJournalOrAudio()
    {
        var type = typeof(TimeMachineRecorder);
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(members, m => m.Name.Contains("Pause", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("Continue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("Journal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, m => m.Name.Contains("Backup", StringComparison.OrdinalIgnoreCase));
    }

    private static TimelineEventKind ToEventKind(ClassroomSignalKind kind) => kind switch
    {
        ClassroomSignalKind.TimerStarted => TimelineEventKind.TimerStarted,
        ClassroomSignalKind.TimerStopped => TimelineEventKind.TimerStopped,
        _ => TimelineEventKind.TimerEnded,
    };
}
