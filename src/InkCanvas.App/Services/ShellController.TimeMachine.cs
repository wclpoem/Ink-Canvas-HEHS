using InkCanvas.Features.Board;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    /// <summary>Local fallback window used only when the time-machine module is unavailable.</summary>
    private InkCanvas.App.Views.TimeMachineWindow _timeMachine;

    internal void RaiseClassroomSignal(ClassroomSignalKind kind, string label = null)
    {
        var handler = ClassroomSignal;
        if (handler is null) return;
        try { handler(new ClassroomSignal(kind, label)); }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"ClassroomSignal/{kind}: {ex.Message}", Core.Diagnostics.LogType.Info);
        }
    }

    private sealed class TimeMachineHost(ShellController shell) : ITimeMachineHost
    {
        public Window Owner => shell.DocumentActive ? shell._documentWorkspace.Window : (Window)shell._board ?? shell._bar;
        public int Theme => shell.EffectiveTheme;
        public BoardService Board => shell.Services.Board;
        public InkController ActiveController => shell.ActiveController;
        public bool BoardOpen => shell._board is not null;
        public PptInkSession Ppt => shell._pptSession;
        public string DataDirectory => Path.Combine(shell.Services.Store.Directory, "PluginData", "inkcanvas.time-machine");

        public event Action ActiveControllerChanged
        {
            add => shell.ActiveControllerChanged += value;
            remove => shell.ActiveControllerChanged -= value;
        }
        public event Action BoardOpenChanged
        {
            add => shell.BoardOpenChanged += value;
            remove => shell.BoardOpenChanged -= value;
        }
        public event Action<ClassroomSignal> ClassroomSignal
        {
            add => shell.ClassroomSignal += value;
            remove => shell.ClassroomSignal -= value;
        }

        public Task<string> CaptureScreenshotAsync() => shell.CaptureScreenshotToFileAsync();
        public Task NotifyAsync(string title, string message) => shell.NotifyAsync(title, message);
        public void ConfigureWindow(Window window, int width = 1120, int height = 820)
            => new DocumentHost(shell).ConfigureWindow(window, width, height);
    }
}
