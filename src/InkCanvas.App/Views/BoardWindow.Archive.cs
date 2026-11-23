using Microsoft.UI.Windowing;

namespace InkCanvas.App.Views;

public sealed partial class BoardWindow
{
    private bool _archiveCloseApproved, _archiveClosing;

    private void OnArchiveClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_archiveCloseApproved && !_services.Archives.HasUnsavedChanges || !_services.Settings.Archive.SaveOnExit) return;
        args.Cancel = true;
        if (_archiveClosing) return;
        DispatcherQueue.TryEnqueue(() => _ = CloseWithArchiveAsync());
    }

    public new void Close() => _ = CloseWithArchiveAsync();

    internal async Task<bool> CloseWithArchiveAsync()
    {
        if (_closed) return true;
        if (_archiveClosing) return false;
        _archiveClosing = true;
        try
        {
            await SaveArchiveBeforeCloseAsync("退出白板");
            base.Close();
            return true;
        }
        catch (Exception ex)
        {
            if (!_closed) await ShowMessageAsync("白板存档失败，内容已保留。\n" + ex.GetBaseException().Message);
            return false;
        }
        finally { _archiveClosing = false; }
    }

    internal async Task SaveArchiveBeforeCloseAsync(string reason)
    {
        if (!_services.Settings.Archive.SaveOnExit) return;
        if (CurrentController.HasActiveInteraction) throw new InvalidOperationException("请先完成当前笔画或拖动，再退出白板。");
        CurrentController.EndPlayback();
        CurrentController.FinishDotAnimations();
        bool interactive = _contentRoot.IsHitTestVisible;
        _contentRoot.IsHitTestVisible = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _services.Archives.SaveAutomaticAsync(reason);
                if (!_services.Archives.HasUnsavedChanges) { _archiveCloseApproved = true; return; }
            }
            throw new InvalidOperationException("保存期间白板仍在变化，请完成编辑后再退出。");
        }
        finally { _contentRoot.IsHitTestVisible = interactive; }
    }
}
