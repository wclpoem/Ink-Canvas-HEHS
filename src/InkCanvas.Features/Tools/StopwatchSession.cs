namespace InkCanvas.Features.Tools;

public sealed record StopwatchLap(int Number, TimeSpan Split, TimeSpan Elapsed);

/// <summary>Elapsed time and lap snapshots driven by a monotonic clock.</summary>
public sealed class StopwatchSession(TimeProvider clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<StopwatchLap> _laps = new();
    private long _startedAt;
    private TimeSpan _elapsed;
    public bool IsRunning { get; private set; }
    public TimeSpan Elapsed => _elapsed + (IsRunning ? _clock.GetElapsedTime(_startedAt) : TimeSpan.Zero);
    public IReadOnlyList<StopwatchLap> Laps => _laps;

    public void StartPause()
    {
        if (IsRunning) _elapsed = Elapsed;
        else _startedAt = _clock.GetTimestamp();
        IsRunning = !IsRunning;
    }

    public StopwatchLap Lap()
    {
        if (!IsRunning) return null;
        var elapsed = Elapsed;
        var previous = _laps.Count == 0 ? TimeSpan.Zero : _laps[^1].Elapsed;
        var lap = new StopwatchLap(_laps.Count + 1, elapsed - previous, elapsed);
        _laps.Add(lap);
        return lap;
    }

    public void Reset()
    {
        IsRunning = false; _elapsed = TimeSpan.Zero; _laps.Clear();
    }
}
