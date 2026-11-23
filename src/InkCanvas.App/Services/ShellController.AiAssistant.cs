using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private IAiAssistantService Assistant => Services.Modules.GetExport<IAiAssistantService>();
    public bool AiAssistantAvailable => Assistant is not null;

    private SlideContext BuildSlideContext(int slideId, int slideIndex)
    {
        PptSnapshot snapshot = _pptSession.Snapshot;
        if (snapshot?.Presentation is null) return null;

        try
        {
            string currentText = SafeSlideText(slideId);
            string previousText = "";
            string nextText = "";
            PptPresentationDetails details = Services.Ppt.GetPresentationDetails();
            if (details?.Slides is { Count: > 0 })
            {
                int position = -1;
                for (int i = 0; i < details.Slides.Count; i++)
                {
                    if (details.Slides[i].SlideId != slideId) continue;
                    position = i;
                    break;
                }

                if (position > 0)
                    previousText = SafeSlideText(details.Slides[position - 1].SlideId);
                if (position >= 0 && position + 1 < details.Slides.Count)
                    nextText = SafeSlideText(details.Slides[position + 1].SlideId);
            }

            return SlideContextPolicy.Build(
                snapshot.Presentation.Name, slideId, slideIndex,
                currentText, previousText, nextText,
                _teachingSession?.CurrentAnimationStep ?? 0);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"课件上下文读取失败：{ex.Message}", Core.Diagnostics.LogType.Info);
            return null;
        }
    }


    private void RecordAiExplanation(AssistantSession session, int slideId, int slideIndex)
    {
        if (session is null || slideId <= 0) return;
        if (string.IsNullOrWhiteSpace(session.Text)) return;

        try
        {
            if (_slideIndex is not null)
            {
                _slideIndex.SetText(slideId, session.Text);
                _slideIndex.MarkAi(slideId);
                SaveSlideIndex();
                _slideNav?.Update(_slideIndex, _pptSession.Snapshot?.Slide?.SlideId ?? 0);
            }

            _teachingSession?.Link(
                "ai", session.SessionId, slideId, slideIndex,
                _board is null ? -1 : Services.Board.CurrentIndex, "", Environment.TickCount64);
            SaveTeachingSession();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 讲解归档失败：{ex.Message}", Core.Diagnostics.LogType.Error);
        }
    }


    private sealed class AssistantHost(ShellController shell) : IAiAssistantHost
    {
        public AppSettings Settings => shell.Services.Settings;
        public InkController ActiveController => shell.ActiveController;
        public BoardService Board => shell.Services.Board;
        public bool BoardOpen => shell._board is not null;
        public double DpiScale => shell.CurrentDpi();
        public ScreenRect OverlayBounds => new(shell.Overlay.Window.X, shell.Overlay.Window.Y,
            shell.Overlay.Window.Width, shell.Overlay.Window.Height);
        public AssistantSource Source => new(shell.CurrentPresentationName(),
            shell._pptSession?.Snapshot?.Slide?.SlideId ?? 0, shell._pptSession?.Snapshot?.Slide?.Index ?? 0,
            shell._pptSession?.Snapshot?.IsSlideShowRunning == true ? shell.Services.Ppt.GetAnimationStep() : 0);
        public bool IsClassroomPresentation => shell._pptSession?.Snapshot?.IsSlideShowRunning == true || shell._preset?.Id == "teach";
        public IAssistantToolRegistry Tools => shell.Services.AssistantTools;
        public SlideContext GetSlideContext(int slideId, int slideIndex) => shell.BuildSlideContext(slideId, slideIndex);
        public void ConfigureWindow(Window window) => new CanvasToolsHost(shell).ConfigureWindow(window, 380, 760);
        public void SaveSettings() => shell.Services.SaveSettings();
        public void ShowBoard() { if (shell._board is null) shell.ToggleBoard(); }
        public void RecordExplanation(AssistantSession session, int slideId, int slideIndex) => shell.RecordAiExplanation(session, slideId, slideIndex);
        public Task StartPracticeAsync(VariantQuestionDraft draft, bool startTimer) => shell.StartPracticeAsync(draft, startTimer);
        public Task NotifyAsync(string title, string message) => shell.NotifyAsync(title, message);
    }
}
