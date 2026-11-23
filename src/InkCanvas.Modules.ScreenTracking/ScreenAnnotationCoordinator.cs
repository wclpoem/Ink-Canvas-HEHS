using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.System;
using Microsoft.UI.Dispatching;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.ScreenTracking;

public sealed class ScreenAnnotationCoordinator : IDisposable, IScreenTrackingService
{
    private readonly IScreenTrackingHost _host;
    private readonly Action _configure, _reanchor;
    private readonly ScreenAnnotationMemory _memory;
    private readonly FixedScreenRegions _fixed = new();
    private readonly DispatcherQueueTimer _timer;
    private ScreenContentReader _reader;
    private ScreenViewSnapshot _previous;
    private bool _disposed;
    private int _reanchorEpoch;

    public ScreenAnnotationCoordinator(IScreenTrackingHost host, Func<Action, Action> protect = null)
    {
        _host = host;
        _configure = protect?.Invoke(Configure) ?? Configure;
        _reanchor = protect?.Invoke(Reanchor) ?? Reanchor;
        _memory = new ScreenAnnotationMemory(host.Controller, host.Images);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(25);
        var refresh = protect?.Invoke(Refresh) ?? Refresh;
        _timer.Tick += (_, _) => refresh();
        host.SettingsApplied += _configure;
        host.ResetRequested += _reanchor;
        try { Configure(); } catch { Dispose(); throw; }
    }

    private void Configure()
    {
        var settings = _host.Settings;
        if (settings.ScreenContentTracking)
        {
            _reader ??= new ScreenContentReader();
            _reader.Configure(Copy(settings)); _timer.Start();
            _host.Status = "正在识别 Word、PowerPoint 或 Edge 正文";
        }
        else
        {
            _reader?.Configure(new ExperimentalSettings());
            _timer.Stop(); _fixed.Clear(); _previous = null;
            _host.Presenter.SetScreenTrackingClip(null);
            _host.Status = "未启用屏幕内容跟随";
        }
    }

    public void Refresh()
    {
        if (_disposed || !_host.Settings.ScreenContentTracking || _reader is null || _host.BoardOpen) return;
        var snapshot = _reader.Latest;
        if (snapshot is null) return;
        if (_host.Controller.HasActiveInteraction || _host.Controller.Playback is not null) return;
        if (_reader.SampleAge > TimeSpan.FromSeconds(2))
        {
            _host.Presenter.SetScreenTrackingClip(null, suppressed: _memory.CurrentKey is not null);
            _host.Status = "应用暂未响应检测，已暂时隐藏该页批注";
            return;
        }
        if (ReferenceEquals(snapshot, _previous)) return;
        ApplySnapshot(snapshot);
    }

    public (IReadOnlyList<InkCanvas.Core.Ink.InkStroke> Strokes, Matrix3x2 WorldToContent)? CapturePowerPointInk(string fullName, int slideId)
        => _memory.CapturePreview(":" + ScreenContentReader.Hash(fullName) + ":" + slideId);

    public void ApplySnapshot(ScreenViewSnapshot snapshot)
    {
        var settings = _host.Settings;
        var presenter = _host.Presenter;
        presenter.BeginSceneUpdate();
        try
        {
            if (!snapshot.IsValid)
            {
                if (snapshot.Application == TrackedApplication.None)
                {
                    _memory.Leave(); presenter.SetScreenTrackingClip(null); _previous = null; _fixed.Clear();
                }
                else presenter.SetScreenTrackingClip(null, suppressed: true);
                _host.Status = snapshot.Status;
                return;
            }
            if (_reanchorEpoch > 0 && snapshot.ObservationEpoch >= _reanchorEpoch)
            {
                if (_previous?.ContextKey == snapshot.ContextKey) _memory.Reanchor(snapshot);
                _fixed.Clear(); _reanchorEpoch = 0;
            }
            if (!snapshot.AlignmentReliable)
            {
                presenter.SetScreenTrackingClip(null, suppressed: true);
                _previous = null;
                _host.Status = snapshot.Status;
                return;
            }
            var origin = new Vector2(_host.Window.X, _host.Window.Y);
            var previousView = _host.Controller.ViewTransform;
            if (!_memory.Apply(snapshot, settings, origin, _host.Window.DpiScale)) return;
            bool animate = _previous?.ContextKey == snapshot.ContextKey
                && _previous.WindowHandle == snapshot.WindowHandle && _previous.ObservationEpoch == snapshot.ObservationEpoch;
            Rect Local(Rect value) => new(value.Left - origin.X, value.Top - origin.Y, value.Right - origin.X, value.Bottom - origin.Y);
            var fixedRegions = _fixed.Update(_previous, snapshot);
            var masks = snapshot.Occlusions.Concat(settings.MaskFixedRegions ? fixedRegions : Array.Empty<Rect>()).Select(Local).ToArray();
            presenter.SetScreenTrackingClip(new ScreenInkClip(Local(snapshot.Viewport), masks));
            double duration = snapshot.Application == TrackedApplication.Word && _previous is not null
                && Math.Abs(snapshot.Scale - _previous.Scale) > .002f ? 32 : ScreenViewTransition.DurationMilliseconds;
            presenter.TransitionScreenTrackingView(previousView, animate, duration);
            _previous = snapshot;
            _host.Status = $"{snapshot.Status} · 已记忆 {_memory.RememberedPages} 个页面";
        }
        finally { presenter.EndSceneUpdate(); }
    }

    internal void Reanchor()
    {
        if (_reader?.Latest is not { IsValid: true } snapshot || _host.Controller.HasActiveInteraction) return;
        _reanchorEpoch = _reader.ResetAnchor();
        _host.Status = "正在重新绑定当前批注位置";
    }

    private static ExperimentalSettings Copy(ExperimentalSettings settings) => new()
    {
        ScreenContentTracking = settings.ScreenContentTracking, FollowScroll = settings.FollowScroll,
        FollowZoom = settings.FollowZoom, MaskFixedRegions = settings.MaskFixedRegions, RememberPages = settings.RememberPages,
        PreserveUnchangedRegions = settings.PreserveUnchangedRegions,
        Word = settings.Word, PowerPoint = settings.PowerPoint, Edge = settings.Edge,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _reader?.Dispose();
        _host.SettingsApplied -= _configure;
        _host.ResetRequested -= _reanchor;
        _host.Presenter.SetScreenTrackingClip(null);
    }
}
