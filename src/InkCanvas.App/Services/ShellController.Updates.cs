using System.Runtime.InteropServices;
using InkCanvas.App.Views;
using InkCanvas.Core.Update;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private bool _installingUpdate;
    private UpdateWindow _updateWindow;
    private bool CanInstallUpdateSilently()
    {
        if (_exiting || _disposed || _board is not null || _documentWorkspace is not null || _settings is not null
            || _countdown is not null || _lottery is not null || _replay is not null || _imagePicking || _videoPicking || _lastSlideShowState || _updateWindow is not null) return false;
        if (Services.OverlayInk.HasActiveInteraction || Services.OverlayInk.Playback is not null
            || Services.OverlayInk.Document.Count > 0 || Services.ScreenImages.CurrentImages.Count > 0 || Services.ScreenImages.CurrentMedia.Count > 0) return false;
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) >= 60000;
    }

    private async Task<bool> PrepareForUpdateAsync(bool silent)
    {
        if (silent) return CanInstallUpdateSilently();
        if (ActiveController.HasActiveInteraction) { await ShowUpdateNoticeAsync("更新", "请先完成当前笔画或拖动，再安装更新。"); return false; }
        if (Services.Settings.Archive.ScreenCapture.SaveOnExit)
        {
            await SaveScreenArchiveAsync("更新前屏幕存档");
            await WaitForScreenArchivesAsync();
            if (Services.ScreenArchives.LastError is not null && Services.ScreenArchives.IsEnabled(_lastSlideShowState)
                && Services.OverlayInk.Document.Count > 0 && _board is null) return false;
        }
        if (_documentWorkspace is not null && !await _documentWorkspace.CloseAsync()) return false;
        var boardWindow = _board;
        var stores = new[] { Services.ScreenImages, Services.Board };
        var pages = stores.SelectMany(store => store.Pages).ToArray();
        var spacing = stores.Select(store => store.GridSpacing).ToArray();
        bool changed = false;
        void MarkChanged() => changed = true;
        foreach (var page in pages) { page.Document.Changed += MarkChanged; page.History.Changed += MarkChanged; }
        foreach (var store in stores) { store.PageChanged += MarkChanged; store.ImagesChanged += MarkChanged; store.BackgroundChanged += MarkChanged; }
        try
        {
            string directory = Path.Combine(Services.Store.Directory, "BeforeUpdate", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            if (Services.OverlayInk.Document.Count > 0 || Services.ScreenImages.CurrentImages.Count > 0 || Services.ScreenImages.CurrentMedia.Count > 0)
            {
                Directory.CreateDirectory(directory);
                await Services.ScreenImages.SavePageAsync(Path.Combine(directory, "桌面批注.icink"));
            }
            if (boardWindow is not null)
            {
                Directory.CreateDirectory(directory);
                await Services.Board.SaveBoardAsync(Path.Combine(directory, "画板.icink"));
            }
            if (changed || ActiveController.HasActiveInteraction || !ReferenceEquals(boardWindow, _board)
                || !spacing.SequenceEqual(stores.Select(store => store.GridSpacing)))
            {
                await ShowUpdateNoticeAsync("更新", "备份期间画板内容发生了变化。请完成编辑后重新安装更新。");
                return false;
            }
            if (boardWindow is not null) { await boardWindow.SaveArchiveBeforeCloseAsync("安装更新"); boardWindow.Close(); }
            Services.SaveSettings();
            return true;
        }
        finally
        {
            foreach (var page in pages) { page.Document.Changed -= MarkChanged; page.History.Changed -= MarkChanged; }
            foreach (var store in stores) { store.PageChanged -= MarkChanged; store.ImagesChanged -= MarkChanged; store.BackgroundChanged -= MarkChanged; }
        }
    }

    private Task<ReleaseInfo> ChooseUpdateReleaseAsync(IReadOnlyList<ReleaseInfo> releases)
        => ShowUpdateWindowAsync(UpdateWindow.Versions(releases, Updater.CurrentVersion, EffectiveTheme));

    private async Task<bool> ConfirmUpdateReleaseAsync(ReleaseInfo release)
        => await ShowUpdateWindowAsync(UpdateWindow.Confirm(release, Updater.CurrentVersion, EffectiveTheme)) is not null;

    private async Task ShowUpdateNoticeAsync(string title, string message)
        => await ShowUpdateWindowAsync(UpdateWindow.Notice(title, message, Updater.CurrentVersion, EffectiveTheme));

    private async Task<ReleaseInfo> ShowUpdateWindowAsync(UpdateWindow window)
    {
        if (_disposed || _exiting) { window.Close(); return null; }
        _bar?.ClosePanels();
        _updateWindow?.Close();
        _updateWindow = window;
        var owner = _settings as Window ?? (DocumentActive ? _documentWorkspace.Window : (Window)_board ?? _bar);
        if (owner is not null) WindowChrome.CenterOnOwner(window, owner);
        window.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, window)) _updateWindow = null; };
        NotifyWindowOpened(window);
        window.Activate();
        WindowChrome.EnsureAbove(window, Overlay.Window.Handle);
        return await window.Result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
}
