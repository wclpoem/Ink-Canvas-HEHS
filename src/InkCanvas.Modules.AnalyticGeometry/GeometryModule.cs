using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed class GeometryModule : IApplicationModule
{
    private GeometryService _service;
    public void Start(IModuleContext context)
    {
        _service = new GeometryService(context.GetService<ICanvasToolsHost>());
        context.RegisterCleanup(_service.Dispose); context.Export<IAnalyticGeometryService>(_service);
        _service.RegisterAssistant(context);
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}

internal sealed partial class GeometryService(ICanvasToolsHost host) : IAnalyticGeometryService, IDisposable
{
    private GeometryWorkbenchWindow _window;
    private bool _disposed;
    public void Open()
    {
        if (_disposed) return;
        if (_window is not null) { _window.Activate(); return; }
        var window = new GeometryWorkbenchWindow(host); _window = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        window.Activate();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _window?.Close(); _window = null; }
}
