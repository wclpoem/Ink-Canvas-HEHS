using InkCanvas.Core.Settings;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Host.Contracts;

/// <summary>Explicit, host-owned services for the trusted screen-follow module.</summary>
public interface IScreenTrackingHost
{
    InkController Controller { get; }
    BoardService Images { get; }
    LayeredInkWindow Window { get; }
    OverlayInkPresenter Presenter { get; }
    ExperimentalSettings Settings { get; }
    bool BoardOpen { get; }
    string Status { get; set; }
    event Action SettingsApplied;
    event Action ResetRequested;
}

public interface IScreenTrackingService
{
    void Refresh();
    void ApplySnapshot(ScreenViewSnapshot snapshot);
    (IReadOnlyList<InkCanvas.Core.Ink.InkStroke> Strokes, System.Numerics.Matrix3x2 WorldToContent)?
        CapturePowerPointInk(string fullName, int slideId) => null;
}
