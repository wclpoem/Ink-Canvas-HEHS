using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private IReadOnlyList<ExtensionCommand> _displayedExtensionCommands = Array.Empty<ExtensionCommand>();
    private ITimeMachineService _timeMachineService;

    public IReadOnlyList<ExtensionCommand> ExtensionCommands
    {
        get
        {
            var commands = Services.Plugins.Commands.ToList();
            if (Services.Modules.HasExport<Host.Contracts.IBoardLibraryService>()) commands.Add(new ExtensionCommand("inkcanvas.library", "open", "文稿库", "浏览文件夹、预览与打开白板文稿"));
            if (Services.Modules.HasExport<Host.Contracts.IPdfWorkspaceService>()) commands.AddRange(new[] {
            new ExtensionCommand("inkcanvas.pdf", "open", "打开 PDF", "查看 PDF 并将墨迹批注保存到文件"),
            new ExtensionCommand("inkcanvas.pdf", "import", "PDF 导入画板", "选择 PDF 页码并导入新画板页") });
            if (Services.Modules.HasExport<Host.Contracts.IAnalyticGeometryService>()) commands.Add(new ExtensionCommand("inkcanvas.analytic-geometry", "open", "解析几何", "数学工作区、代数计算、二维与三维联动作图"));
            if (AiAssistantAvailable) commands.Add(new ExtensionCommand("inkcanvas.ai-assistant", "open", "AI 助手", "面向教师的通用 AI 助手与课堂工具调用"));
            var timeMachine = Services.Modules.GetExport<ITimeMachineService>();
            if (timeMachine is not null)
            {
                commands.Add(new ExtensionCommand("inkcanvas.time-machine", "record",
                    timeMachine.IsRecording ? "停止录制" : "开始时光机录制", "录制板书/白板/PPT 批注"));
                commands.Add(new ExtensionCommand("inkcanvas.time-machine", "open",
                    "时光机录制", "查看课堂录制、章节书签并导出视频"));
            }
            return commands;
        }
    }

    private async Task StartPluginsAsync()
    {
        try { await Services.Plugins.StartEnabledAsync(new HashSet<string>(Services.ExtensionPreferences.EnabledPlugins)); }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("Plugin startup: " + ex.Message, Core.Diagnostics.LogType.Error); }
    }
    private void RefreshTimeMachineService()
    {
        var service = Services.Modules.GetExport<ITimeMachineService>();
        if (ReferenceEquals(service, _timeMachineService)) return;
        if (_timeMachineService is not null) _timeMachineService.RecordingStateChanged -= OnTimeMachineRecordingChanged;
        _timeMachineService = service;
        if (_timeMachineService is not null) _timeMachineService.RecordingStateChanged += OnTimeMachineRecordingChanged;
    }

    private void OnTimeMachineRecordingChanged() => OnExtensionCommandsChanged();

    private void OnExtensionCommandsChanged()
    {
        RefreshTimeMachineService();
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            var commands = ExtensionCommands.ToArray();
            if (_displayedExtensionCommands.SequenceEqual(commands)) return;
            _displayedExtensionCommands = commands;
            _toolkit?.SetAiAvailable(AiAssistantAvailable);
            _spotlight?.SetAiAvailable(AiAssistantAvailable);
            _magnifier?.SetAiAvailable(AiAssistantAvailable);
            _bar?.ClosePanels(); _bar?.Bar.RefreshExtensionCommands();
            _board?.RefreshExtensionCommands();
        });
    }
    public async Task ExecuteExtensionCommandAsync(ExtensionCommand command)
    {
        // A menu or detached panel may outlive a module that stopped after it opened.
        if (!ExtensionCommands.Any(available => available.PluginId == command.PluginId && available.CommandId == command.CommandId))
        { OnExtensionCommandsChanged(); return; }
        if (command.PluginId == "inkcanvas.library")
        {
            Services.Modules.Invoke<Host.Contracts.IBoardLibraryService>(library => library.Open());
            return;
        }
        if (command.PluginId == "inkcanvas.ai-assistant")
        {
            OpenAiAssistant();
            return;
        }
        if (command.PluginId == "inkcanvas.analytic-geometry")
        {
            try { Services.Modules.GetExport<Host.Contracts.IAnalyticGeometryService>()?.Open(); }
            catch (Exception ex) { await NotifyAsync("解析几何", ex.GetBaseException().Message); }
            return;
        }
        if (command.PluginId == "inkcanvas.pdf")
        {
            if (command.CommandId == "open") await OpenPdfAsync();
            else if (command.CommandId == "import")
            {
                try { var pdf = Services.Modules.GetExport<Host.Contracts.IPdfWorkspaceService>(); if (pdf is not null) await pdf.ImportToBoardAsync(); }
                catch (Exception ex) { await NotifyAsync("PDF 导入失败", ex.GetBaseException().Message); }
            }
            return;
        }
        if (command.PluginId == "inkcanvas.time-machine")
        {
            var timeMachine = Services.Modules.GetExport<ITimeMachineService>();
            if (timeMachine is null) { OnExtensionCommandsChanged(); return; }
            try
            {
                if (command.CommandId == "record")
                {
                    if (timeMachine.IsRecording) timeMachine.StopRecording();
                    else
                    {
                        if (_board is null && _pptSession?.Snapshot?.IsAttached != true)
                        {
                            await NotifyAsync("板书时光机", "请先打开画板或启动课件放映后再开始录制。");
                            return;
                        }
                        timeMachine.StartRecording();
                    }
                }
                else if (command.CommandId == "open")
                {
                    await timeMachine.OpenRecordingsAsync();
                }
            }
            catch (Exception ex)
            {
                Core.Diagnostics.FileLog.WriteLogToFile("TimeMachine command " + command.CommandId + ": " + ex.Message, Core.Diagnostics.LogType.Error);
                if (!_disposed) await NotifyAsync("板书时光机", ex.GetBaseException().Message);
            }
            return;
        }
        var controller = ActiveController;
        if (controller.HasActiveInteraction || controller.Playback is not null) return;
        if (!Matrix3x2.Invert(controller.ViewTransform, out var inverse)) return;
        var originalView = controller.ViewTransform;
        bool canvasChanged = false;
        void InvalidateCanvas() => canvasChanged = true;
        controller.Document.Changed += InvalidateCanvas;
        controller.ElementsChanged += InvalidateCanvas;
        controller.PlaybackChanged += InvalidateCanvas;
        var context = new PluginCommandContext(controller.ViewCenter.X * 2, controller.ViewCenter.Y * 2, _board is null ? "screen" : "board");
        try
        {
            var result = await Services.Plugins.ExecuteAsync(command.PluginId, command.CommandId, context);
            if (_disposed || ActiveController != controller || canvasChanged || controller.ViewTransform != originalView
                || controller.HasActiveInteraction || controller.Playback is not null) return;
            var strokes = new List<InkStroke>();
            long layer = controller.NextLayerOrder();
            foreach (var item in result.Strokes)
            {
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = item.Width, Color = InkColor.FromPackedArgb(item.Argb), TaperKind = TaperKind.None }) { LayerOrder = layer++ };
                foreach (var point in item.Points) stroke.AddPoint(point.X, point.Y);
                stroke.Complete(); stroke.Transform(inverse); strokes.Add(stroke);
            }
            if (strokes.Count > 0)
            {
                controller.Document.AddRange(strokes);
                controller.History.Push(new DelegateAction(command.Title, () => controller.Document.AddRange(strokes), () => controller.Document.RemoveAll(strokes)));
            }
            if (!string.IsNullOrWhiteSpace(result.Notification)) await NotifyAsync(command.Title, result.Notification);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile("Plugin command " + command.PluginId + "/" + command.CommandId + ": " + ex.Message, Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("插件命令执行失败", ex.GetBaseException().Message);
        }
        finally
        {
            controller.Document.Changed -= InvalidateCanvas;
            controller.ElementsChanged -= InvalidateCanvas;
            controller.PlaybackChanged -= InvalidateCanvas;
        }
    }
}
