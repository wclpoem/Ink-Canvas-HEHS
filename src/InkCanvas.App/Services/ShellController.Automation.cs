using System.Diagnostics;
using InkCanvas.Features.Persistence;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private long _lastProcessMaintenance, _lastCleanup;
    private bool _maintenanceRunning, _cleanupRunning;
    private string _cleanupConfiguration;

    private void MaintainAutomation()
    {
        _ = Services.Archives.SaveIfDueAsync();
        if (_board is null && !DocumentActive && Services.ScreenArchives.TakeScheduledSave(_lastSlideShowState))
            _ = SaveScreenArchiveAsync("定时屏幕存档");
        var automation = Services.Settings.Automation;
        long now = Environment.TickCount64;
        if (!_maintenanceRunning && now - _lastProcessMaintenance >= 10_000
            && (automation.IsAutoKillPptService || automation.IsAutoKillEasiNote))
        {
            _lastProcessMaintenance = now;
            _maintenanceRunning = true;
            bool ppt = automation.IsAutoKillPptService, board = automation.IsAutoKillEasiNote;
            _ = CloseConfiguredProcessesAsync(ppt, board);
        }
        if (!automation.AutoDelSavedFiles) { _cleanupConfiguration = null; return; }
        string configuration = automation.AutoSavedStrokesLocation + "|" + automation.AutoDelSavedFilesDaysThreshold;
        if (_cleanupRunning || configuration == _cleanupConfiguration && now - _lastCleanup < 3_600_000) return;
        _cleanupConfiguration = configuration;
        _lastCleanup = now;
        _cleanupRunning = true;
        var snapshot = new Core.Settings.AutomationSettings { AutoDelSavedFiles = true,
            AutoDelSavedFilesDaysThreshold = automation.AutoDelSavedFilesDaysThreshold,
            AutoSavedStrokesLocation = automation.AutoSavedStrokesLocation };
        _ = CleanupAsync(snapshot);
    }

    private async Task CleanupAsync(Core.Settings.AutomationSettings settings)
    {
        try { await Task.Run(() => new AutoSaveService(settings).Prune()); }
        finally { _cleanupRunning = false; }
    }

    private async Task CloseConfiguredProcessesAsync(bool ppt, bool board)
    {
        try
        {
            await Task.Run(() =>
            {
                using var current = Process.GetCurrentProcess();
                var targets = new List<string>();
                if (ppt) targets.AddRange(new[] { "PPTService", "SeewoIwbAssistant" });
                if (board) targets.Add("EasiNote");
                foreach (string name in targets)
                foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (process.SessionId == current.SessionId && process.Id != current.Id) process.Kill();
                    }
                    catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("自动关闭 " + name + " 失败 " + ex.Message); }
                }
            });
        }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("自动关闭进程失败 " + ex.Message); }
        finally { _maintenanceRunning = false; }
    }
}
