using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    internal async Task OpenLibraryDocumentAsync(string path)
    {
        try { await new LibraryHost(this).OpenBoardAsync(path); }
        catch (Exception ex) { if (!_disposed) await NotifyAsync("文稿打开失败", ex.GetBaseException().Message); }
    }
    private sealed class LibraryHost(ShellController shell) : IBoardLibraryHost
    {
        public IAiDocumentBridge DocumentAi => shell._documentAi;
        public string DefaultDirectory => ArchivePolicy.ResolveDirectory(shell.Services.Settings.Archive.DefaultDirectory);
        public string DataDirectory => Path.Combine(shell.Services.Store.Directory, "PluginData", "inkcanvas.library");
        public void ConfigureWindow(Window window)
        {
            new DocumentHost(shell).ConfigureWindow(window, 1240, 850);
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        }
        public async Task<bool> OpenBoardAsync(string path)
        {
            // 新格式课堂录制文件：插件在位则路由给时光机模块，否则抛出守卫提示。
            if (ClassroomRecordingProbe.IsClassroomRecording(path))
            {
                var service = shell.Services.Modules.GetExport<ITimeMachineService>();
                if (service is null) throw new InvalidDataException(ClassroomRecordingProbe.PluginRequiredMessage);
                await service.OpenFileAsync(path);
                return true;
            }
            var board = shell.Services.Board;
            var current = board.Current;
            var apply = await board.PrepareOpenAsync(path, true);
            if (shell._disposed || board.Current != current) return false;
            if (board.HasContent)
            {
                var result = await Views.YesOrNoWindow.AskAsync((Window)shell._board ?? shell._bar, "打开文稿会替换当前白板。是否继续？请先保存需要保留的内容。");
                if (!result || shell._disposed || board.Current != current) return false;
            }
            apply();
            if (shell._board is null) shell.ToggleBoard();
            shell._board?.Activate();
            return true;
        }
    }
}
