using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Library;

public sealed class LibraryModule : IApplicationModule
{
    private LibraryService _service;
    public void Start(IModuleContext context)
    {
        _service = new LibraryService(context.GetService<IBoardLibraryHost>());
        context.RegisterCleanup(_service.Dispose);
        context.Export<IBoardLibraryService>(_service);
        _service.RegisterAssistant(context);
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}

internal sealed partial class LibraryService(IBoardLibraryHost host) : IBoardLibraryService, IDisposable
{
    private readonly LibraryStore _store = new(host.DataDirectory);
    public void Relocate(string previous, string path) => _ = RelocateAsync(previous, path);
    private async Task RelocateAsync(string previous, string path)
    {
        try
        {
            if (!previous.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(previous)) return;
            await _store.LoadAsync(); _store.RelocatePaths(previous, path); await _store.SaveAsync();
            _window?.Relocate(previous, path);
        }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("更新文稿库名称 " + ex.Message); }
    }
    private LibraryWindow _window;
    private bool _disposed;
    public void Open() => Show(LibraryMode.Browse);
    private LibraryWindow Show(LibraryMode mode, string name = null, IReadOnlyList<string> extensions = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LibraryService));
        if (_window is not null)
        {
            _window.Activate();
            if (_window.Mode == mode) return _window;
            _window.Close();
        }
        var window = new LibraryWindow(host, mode, name, extensions, _store);
        _window = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        window.Activate();
        return window;
    }
    public Task<string> PickOpenAsync(IReadOnlyList<string> extensions) => Show(LibraryMode.Open, extensions: extensions).Result;
    public Task<string> PickSaveAsync(string suggestedName) => Show(LibraryMode.Save, suggestedName).Result;
    public void Dispose() { _disposed = true; _window?.Close(); _window = null; }
}
