using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private DocumentAiHost _documentAi;
    private sealed class DocumentAiHost : IAiDocumentHost, IAiDocumentBridge, IDisposable
    {
        private readonly ShellController _shell;
        public DocumentAiHost(ShellController shell)
        {
            _shell = shell;
            shell.Services.Archives.Saved += ArchiveSaved;
            shell.Services.ScreenArchives.Saved += ArchiveSaved;
        }
        public string DataDirectory => Path.Combine(_shell.Services.Store.Directory, "PluginData", "inkcanvas.ai-assistant", "Documents");
        public event Action<AiDocumentRequest> DocumentSaved;
        public bool Available => _shell.Services.Modules.HasExport<IAiDocumentService>();
        public bool SupportsSuggestions => _shell.Services.Modules.GetExport<IAiDocumentService>()?.SupportsSuggestions == true;
        private void ArchiveSaved(ArchiveSaveResult saved) => Saved(new(saved.Path));
        public void Saved(AiDocumentRequest request)
        {
            if (!_shell._dispatcher.HasThreadAccess) { _shell._dispatcher.TryEnqueue(() => Saved(request)); return; }
            foreach (Action<AiDocumentRequest> callback in DocumentSaved?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { callback(request); } catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("AI 文稿适配 " + ex.Message); }
        }
        public Task<AiDocumentResult> DescribeAsync(AiDocumentRequest request, CancellationToken token = default) =>
            _shell.Services.Modules.GetExport<IAiDocumentService>()?.DescribeAsync(request, token)
            ?? Task.FromException<AiDocumentResult>(new InvalidOperationException("请安装并启用 AI 助手插件。"));
        public void Completed(AiDocumentResult result) => _ = CompleteAsync(result);
        public void Dispose()
        {
            _shell.Services.Archives.Saved -= ArchiveSaved;
            _shell.Services.ScreenArchives.Saved -= ArchiveSaved;
        }
        private async Task CompleteAsync(AiDocumentResult result)
        {
            try
            {
                await _shell.Services.Archives.RelocateAsync(result.OriginalPath, result.Path);
                await _shell.Services.ScreenArchives.RelocateAsync(result.OriginalPath, result.Path);
                _shell.Services.Modules.GetExport<IBoardLibraryService>()?.Relocate(result.OriginalPath, result.Path);
            }
            catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("同步 AI 文稿名称 " + ex.Message); }
        }
    }
}
