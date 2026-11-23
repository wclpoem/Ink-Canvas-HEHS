using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public sealed record CanvasToolTarget(InkController Controller, string Kind, bool CanEdit);

public interface ICanvasToolsHost
{
    IAiDocumentBridge DocumentAi => null;
    CanvasToolTarget CaptureTarget();
    bool IsCurrent(CanvasToolTarget target);
    void ConfigureWindow(Window window, int width = 1180, int height = 880);
    void EnsureVisible(CanvasToolTarget target);
    bool CommitShape(CanvasToolTarget target, IReadOnlyList<InkStroke> strokes, IReadOnlyList<InkStroke> replaced = null);
    event Action TargetChanged;
}

public interface IAnalyticGeometryService
{
    void Open();
}
