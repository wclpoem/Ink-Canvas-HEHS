using InkCanvas.Features.Tools;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StopwatchSessionTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void PauseAndResumeExcludePausedTimeWithoutUiTicks()
    {
        var clock = new Clock(); var timer = new StopwatchSession(clock);
        timer.StartPause(); clock.Advance(12.345);
        Assert.Equal(TimeSpan.FromSeconds(12.345), timer.Elapsed);
        timer.StartPause(); clock.Advance(500);
        Assert.Equal(TimeSpan.FromSeconds(12.345), timer.Elapsed);
        timer.StartPause(); clock.Advance(70.001);
        Assert.Equal(TimeSpan.FromSeconds(82.346), timer.Elapsed);
    }

    [Fact]
    public void LapsSnapshotBothSplitAndCumulativeElapsedTime()
    {
        var clock = new Clock(); var timer = new StopwatchSession(clock);
        Assert.Null(timer.Lap());
        timer.StartPause(); clock.Advance(3.25); var first = timer.Lap();
        clock.Advance(7.5); var second = timer.Lap();
        Assert.Equal(new StopwatchLap(1, TimeSpan.FromSeconds(3.25), TimeSpan.FromSeconds(3.25)), first);
        Assert.Equal(new StopwatchLap(2, TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(10.75)), second);
        timer.StartPause(); clock.Advance(300); Assert.Null(timer.Lap());
        timer.StartPause(); clock.Advance(2); Assert.Equal(TimeSpan.FromSeconds(2), timer.Lap().Split);
        Assert.Equal(TimeSpan.FromSeconds(3.25), first.Elapsed);
    }

    [Fact]
    public void ResetStopsClockClearsLapsAndRestartsFromZero()
    {
        var clock = new Clock(); var timer = new StopwatchSession(clock);
        timer.StartPause(); clock.Advance(25); timer.Lap(); timer.Reset(); clock.Advance(10);
        Assert.False(timer.IsRunning); Assert.Empty(timer.Laps); Assert.Equal(TimeSpan.Zero, timer.Elapsed);
        timer.StartPause(); clock.Advance(.01); Assert.Equal(TimeSpan.FromSeconds(.01), timer.Elapsed);
        Assert.Equal(1, timer.Lap().Number);
    }
}
