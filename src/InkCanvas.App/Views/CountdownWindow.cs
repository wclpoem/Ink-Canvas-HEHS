using System.Runtime.InteropServices;
using InkCanvas.Features.Tools;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Views;

/// <summary>Touch-first classroom timer sharing one session across normal, full-screen and compact views.</summary>
public sealed partial class CountdownWindow : Window, IDisposable
{
    private readonly CountdownSession _session = new();
    private readonly StopwatchSession _stopwatch = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _disposed, _warning;
    private int _displaySeconds = -1;
    internal bool SoundStarted { get; private set; }
    internal bool IsRunning => _session.State is CountdownState.Running or CountdownState.Paused;
    internal bool IsPaused => _session.State == CountdownState.Paused;
    internal int DisplaySeconds => _displaySeconds;
    internal int Round => _session.Round;
    internal int ConfiguredSeconds => _session.ConfiguredSeconds;
    internal bool RefreshTimerRunning => _timer.IsRunning;
    internal bool IsStopwatch => _stopwatchMode;
    internal TimeSpan StopwatchElapsed => _stopwatch.Elapsed;
    internal IReadOnlyList<StopwatchLap> StopwatchLaps => _stopwatch.Laps;
    public event Action<TimeSpan> TimerStarted;
    public event Action<TimeSpan> TimerStopped;
    public event Action TimerEnded;

    public CountdownWindow(int theme = 0, int defaultMinutes = 0)
    {
        Title = "课堂计时器";
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += TimerTick;
        if (defaultMinutes > 0) _session.SetDuration(Math.Clamp(defaultMinutes, 1, 5999) * 60);
        BuildLayout(theme);
        Closed += (_, _) => Dispose();
        RefreshState();
    }

    internal void SetDuration(int seconds)
    {
        if (_disposed || !_session.SetDuration(seconds)) return;
        StopSound();
        _durationFlyout.Hide();
        RefreshState();
    }

    internal void StartPause()
    {
        if (_disposed) return;
        _durationFlyout.Hide(); StopSound();
        if (_stopwatchMode)
        {
            _stopwatch.StartPause(); SyncRefreshTimer(); RefreshState();
            return;
        }
        _session.StartPause();
        SyncRefreshTimer();
        RefreshState();
        var remaining = TimeSpan.FromSeconds(_session.RemainingSeconds);
        if (IsPaused) TimerStopped?.Invoke(remaining); else TimerStarted?.Invoke(remaining);
    }

    public void StartCountdown(TimeSpan duration)
    {
        if (_disposed || duration <= TimeSpan.Zero) return;
        SwitchTimerMode(false);
        Reset();
        SetDuration((int)Math.Clamp(Math.Ceiling(duration.TotalSeconds), 1, CountdownSession.MaximumSeconds));
        StartPause();
    }

    public void StopCountdown()
    {
        if (!IsRunning) return;
        _session.Reset(); StopSound(); SyncRefreshTimer(); RefreshState(); TimerStopped?.Invoke(TimeSpan.Zero);
    }

    internal void Reset()
    {
        if (_stopwatchMode)
        {
            _stopwatch.Reset(); _lapItems.Clear(); SyncRefreshTimer(); RefreshState();
            return;
        }
        bool active = IsRunning;
        StopSound(); _durationFlyout.Hide();
        _session.Reset(); SyncRefreshTimer(); RefreshState();
        if (active) TimerStopped?.Invoke(TimeSpan.Zero);
    }

    internal void AddTime(int seconds)
    {
        if (_disposed) return;
        _session.AddTime(seconds); RefreshState();
    }

    private void TimerTick(DispatcherQueueTimer sender, object args) => OnTick();

    internal void OnTick()
    {
        if (_disposed) return;
        bool ended = _session.Tick(_repeat.IsChecked == true);
        if (ended)
        {
            SyncRefreshTimer();
            RefreshState();
            TimerEnded?.Invoke();
            PlayFinishSound();
            return;
        }
        if (AppWindow.IsVisible && AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized })
            RefreshTime();
    }

    private void RefreshTime()
    {
        if (_stopwatchMode) { RefreshStopwatchTime(); return; }
        double remaining = _session.RemainingSeconds;
        int seconds = (int)Math.Ceiling(remaining);
        if (seconds != _displaySeconds)
        {
            _displaySeconds = seconds;
            string text = $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
            _time.Text = _compactTime.Text = text;
        }
        _ring.Value = remaining / _session.TotalSeconds;
        _ring.IsPaused = IsPaused;
        bool warning = remaining is > 0 and <= 10 && IsRunning;
        if (_warning != warning) { _warning = warning; ApplyPalette(); }
    }

    private void SyncRefreshTimer()
    {
        bool visible = AppWindow.IsVisible && AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        bool stopwatchVisible = _stopwatchMode && _stopwatch.IsRunning && visible;
        var interval = TimeSpan.FromMilliseconds(stopwatchVisible ? 50 : 100);
        if (_timer.Interval != interval) _timer.Interval = interval;
        bool refresh = _session.State == CountdownState.Running || stopwatchVisible;
        if (refresh && !_timer.IsRunning) _timer.Start();
        if (!refresh && _timer.IsRunning) _timer.Stop();
    }

    private void PlayFinishSound()
    {
        if (_sound.IsChecked != true || _disposed) return;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "TimerDownNotice.wav");
        SoundStarted = PlaySoundW(path, 0, 0x00020000 | 0x00000001 | 0x00000002);
    }

    private void StopSound()
    {
        if (!SoundStarted) return;
        PlaySoundW(null, 0, 0); SoundStarted = false;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string sound, nint module, uint flags);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= TimerTick; StopSound();
        _minSize?.Dispose();
        AppWindow.Changed -= PresenterChanged;
        _uiSettings.ColorValuesChanged -= SystemColorsChanged;
        _durationFlyout.Hide();
    }
}
