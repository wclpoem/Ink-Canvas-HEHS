using InkCanvas.Features.Tools;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class CountdownSessionTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void PauseResumeAndDelayedTicksKeepExactElapsedTime()
    {
        var clock = new Clock(); var timer = new CountdownSession(clock);
        timer.StartPause(); clock.Advance(17.25);
        Assert.Equal(162.75, timer.RemainingSeconds, 6);
        timer.StartPause(); clock.Advance(500);
        Assert.Equal(CountdownState.Paused, timer.State);
        Assert.Equal(162.75, timer.RemainingSeconds, 6);
        timer.StartPause(); clock.Advance(162.8);
        Assert.True(timer.Tick(false));
        Assert.Equal(CountdownState.Finished, timer.State);
        Assert.Equal(0, timer.RemainingSeconds);
        Assert.False(timer.Tick(false));
        timer.StartPause(); Assert.Equal(180, timer.RemainingSeconds);
    }

    [Fact]
    public void ExtendingPausedSessionDoesNotReplaceConfiguredResetDuration()
    {
        var clock = new Clock(); var timer = new CountdownSession(clock);
        timer.SetDuration(180); timer.StartPause(); clock.Advance(30); timer.StartPause();
        timer.AddTime(60);
        Assert.Equal(210, timer.RemainingSeconds);
        Assert.Equal(240, timer.TotalSeconds);
        Assert.Equal(180, timer.ConfiguredSeconds);
        timer.Reset(); Assert.Equal(180, timer.RemainingSeconds);
        timer.AddTime(60); Assert.Equal(240, timer.ConfiguredSeconds);
    }

    [Fact]
    public void RepeatPreservesOvershootInsteadOfDriftingWithEachUiTick()
    {
        var clock = new Clock(); var timer = new CountdownSession(clock);
        timer.SetDuration(3); timer.StartPause(); clock.Advance(9.25);
        Assert.True(timer.Tick(true));
        Assert.Equal(4, timer.Round);
        Assert.Equal(2.75, timer.RemainingSeconds, 6);
        Assert.False(timer.Tick(true));
        clock.Advance(2.8); Assert.True(timer.Tick(false));
        Assert.Equal(CountdownState.Finished, timer.State);
    }

    [Fact]
    public void DurationBoundsAndEditingCannotOverwriteActiveSession()
    {
        var timer = new CountdownSession();
        timer.SetDuration(0); Assert.Equal(1, timer.ConfiguredSeconds);
        timer.SetDuration(int.MaxValue); Assert.Equal(CountdownSession.MaximumSeconds, timer.ConfiguredSeconds);
        timer.StartPause(); Assert.False(timer.SetDuration(30));
        timer.AddTime(int.MaxValue); Assert.Equal(CountdownSession.MaximumSeconds, timer.TotalSeconds);
        timer.StartPause(); Assert.True(timer.SetDuration(60));
        Assert.Equal(CountdownState.Ready, timer.State);
    }

    [Fact]
    public void ExtraTimeOnlyExtendsTheCurrentRound()
    {
        var clock = new Clock(); var timer = new CountdownSession(clock);
        timer.SetDuration(3); timer.StartPause(); timer.AddTime(2); clock.Advance(5.5);
        Assert.True(timer.Tick(true));
        Assert.Equal(2, timer.Round);
        Assert.Equal(3, timer.TotalSeconds);
        Assert.Equal(2.5, timer.RemainingSeconds);
    }
}
