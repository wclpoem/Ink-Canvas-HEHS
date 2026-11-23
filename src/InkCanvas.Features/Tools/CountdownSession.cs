namespace InkCanvas.Features.Tools;

public enum CountdownState { Ready, Running, Paused, Finished }

/// <summary>Monotonic classroom timekeeping, independent of UI refresh and window modes.</summary>
public sealed class CountdownSession(TimeProvider clock = null)
{
    public const int MaximumSeconds = 359999;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _startedAt;
    private double _elapsed;
    public int ConfiguredSeconds { get; private set; } = 180;
    public int TotalSeconds { get; private set; } = 180;
    public int Round { get; private set; } = 1;
    public CountdownState State { get; private set; }
    private double Elapsed => _elapsed + (State == CountdownState.Running ? _clock.GetElapsedTime(_startedAt).TotalSeconds : 0);
    public double RemainingSeconds => State == CountdownState.Finished ? 0 : Math.Max(0, TotalSeconds - Elapsed);

    public bool SetDuration(int seconds)
    {
        if (State == CountdownState.Running) return false;
        ConfiguredSeconds = Math.Clamp(seconds, 1, MaximumSeconds);
        Reset();
        return true;
    }

    public void StartPause()
    {
        if (State == CountdownState.Running)
        {
            _elapsed = Elapsed;
            State = CountdownState.Paused;
            return;
        }
        if (State != CountdownState.Paused)
        {
            TotalSeconds = ConfiguredSeconds;
            _elapsed = 0;
            Round = 1;
        }
        _startedAt = _clock.GetTimestamp();
        State = CountdownState.Running;
    }

    public void Reset()
    {
        _elapsed = 0;
        TotalSeconds = ConfiguredSeconds;
        Round = 1;
        State = CountdownState.Ready;
    }

    public void AddTime(int seconds)
    {
        if (seconds <= 0) return;
        if (State is CountdownState.Ready or CountdownState.Finished)
            SetDuration((int)Math.Min(MaximumSeconds, ConfiguredSeconds + (long)seconds));
        else TotalSeconds = (int)Math.Min(MaximumSeconds, TotalSeconds + (long)seconds);
    }

    /// <summary>Completes elapsed rounds even after a delayed or suspended UI tick.</summary>
    public bool Tick(bool repeat)
    {
        if (State != CountdownState.Running || RemainingSeconds > 0) return false;
        double elapsed = Elapsed;
        if (repeat)
        {
            double overrun = elapsed - TotalSeconds;
            int completed = (int)Math.Min(int.MaxValue - (long)Round, 1 + Math.Floor(overrun / ConfiguredSeconds));
            Round += completed;
            TotalSeconds = ConfiguredSeconds;
            _elapsed = overrun % ConfiguredSeconds;
            _startedAt = _clock.GetTimestamp();
        }
        else
        {
            _elapsed = TotalSeconds;
            State = CountdownState.Finished;
        }
        return true;
    }
}
