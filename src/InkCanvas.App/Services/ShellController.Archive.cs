namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private readonly HashSet<Task> _screenArchiveTasks = new();
    internal Func<Platform.System.ScreenRect, (byte[] Pixels, int Width, int Height)> ScreenArchiveSource { get; set; } = ScreenArchiveCapture.Capture;
    internal Task<Features.Persistence.ArchiveSaveResult> SaveScreenArchiveAsync(string reason, bool force = false)
    {
        var task = CaptureScreenArchiveAsync(reason, force);
        _screenArchiveTasks.Add(task);
        _ = TrackScreenArchiveAsync(task);
        return task;
    }

    private async Task TrackScreenArchiveAsync(Task task)
    {
        try { await task; }
        finally { _screenArchiveTasks.Remove(task); }
    }

    private async Task<Features.Persistence.ArchiveSaveResult> CaptureScreenArchiveAsync(string reason, bool force)
    {
        if (_disposed || _board is not null || DocumentActive) return null;
        try
        {
            var screen = new Platform.System.ScreenRect(Overlay.Window.X, Overlay.Window.Y, Overlay.Window.Width, Overlay.Window.Height);
            var prepared = Services.ScreenArchives.Prepare(screen, new(Overlay.Window.X, Overlay.Window.Y), Overlay.Window.DpiScale,
                Overlay.Presenter.DisplayedView, _lastSlideShowState, reason, force);
            if (prepared is null) return null;
            // Capture pixels before the caller can clear ink, switch pages, or hide annotation mode.
            var captured = ScreenArchiveSource(screen);
            var saving = Services.ScreenArchives.SaveAsync(prepared, captured.Pixels, captured.Width, captured.Height);
            captured = default;
            return await saving;
        }
        catch (Exception ex)
        {
            Services.ScreenArchives.ReportFailure(ex);
            Core.Diagnostics.FileLog.WriteLogToFile("截图白板存档失败 " + ex, Core.Diagnostics.LogType.Error);
            return null;
        }
    }

    private async Task WaitForScreenArchivesAsync()
    {
        while (_screenArchiveTasks.Count > 0) await Task.WhenAll(_screenArchiveTasks.ToArray());
    }

    private void ApplyArchivePaths()
    {
        try { _pptStore.SetAutoSaveRoot(Services.Settings.Automation.AutoSavedStrokesLocation); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { Core.Diagnostics.FileLog.WriteLogToFile("PPT 存档目录尚未更新 " + ex.Message); }
    }
    private async Task CloseBoardToMouseModeAsync()
    {
        var board = _board;
        if (board is null || !await board.CloseWithArchiveAsync() || _disposed) return;
        if (!Overlay.ClickThrough && _board is null) ToggleClickThrough();
    }
}
