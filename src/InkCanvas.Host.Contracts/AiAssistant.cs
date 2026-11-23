using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public sealed record AssistantSource(string Presentation, int SlideId, int SlideIndex, int AnimationStep);

/// <summary>Canvas and classroom capabilities supplied to the optional native AI plugin.</summary>
public interface IAiAssistantHost
{
    AppSettings Settings { get; }
    InkController ActiveController { get; }
    BoardService Board { get; }
    bool BoardOpen { get; }
    double DpiScale { get; }
    ScreenRect OverlayBounds { get; }
    AssistantSource Source { get; }
    bool IsClassroomPresentation { get; }
    IAssistantToolRegistry Tools => null;
    SlideContext GetSlideContext(int slideId, int slideIndex);
    void ConfigureWindow(Window window);
    void SaveSettings();
    void ShowBoard();
    void RecordExplanation(AssistantSession session, int slideId, int slideIndex);
    Task StartPracticeAsync(VariantQuestionDraft draft, bool startTimer);
    Task NotifyAsync(string title, string message);
}

public interface IAiAssistantService
{
    bool IsOpen { get; }
    void Open();
    Task OpenRegionAsync(ScreenRect rect);
    void Review(int slideId, int slideIndex, int animationStep, string text);
    void NotifySlideChanged(int slideId, int slideIndex, int animationStep);
    void NotifyBoardChanged();
}
