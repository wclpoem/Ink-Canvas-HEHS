using System.Numerics;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.System;

namespace InkCanvas.App.Services;

internal sealed record PptSlidePreviewInk(IReadOnlyList<InkStroke> Strokes, Matrix3x2 WorldToSlide);

internal sealed record PptPreviewContext(
    Func<PptSlidePreviewSource> Images,
    Func<PptPresentationInfo, int, CancellationToken, Task<PptSlidePreviewInk>> CaptureInk,
    Func<PptPresentationInfo, PptSlideInfo, bool> Navigate,
    Action<PptNavigationWindow> Opening,
    InkCanvas.Ink.InkController Controller);

public sealed partial class ShellController
{
    private PptSlidePreviewSource _pptPreviewSource;

    private PptPreviewContext CreatePptPreviewContext() => new(
        () => _pptPreviewSource ??= new PptSlidePreviewSource(), CapturePptPreviewInk, NavigatePptPreview,
        current =>
        {
            foreach (var window in _pptNavigations.Values)
                if (!ReferenceEquals(window, current)) window.SetPreviewOpen(false);
        }, Services.OverlayInk);

    private async Task<PptSlidePreviewInk> CapturePptPreviewInk(PptPresentationInfo presentation, int slideId, CancellationToken token)
    {
        var snapshot = _pptSession.Snapshot;
        if (snapshot.Presentation?.Identity != presentation.Identity)
            return new(Array.Empty<InkStroke>(), Matrix3x2.Identity);
        if (_pptSession.InkManagedExternally())
        {
            var tracked = Services.Modules.GetExport<IScreenTrackingService>()?.CapturePowerPointInk(presentation.FullName, slideId);
            if (tracked is { } ink) return new(ink.Strokes, ink.WorldToContent);
        }
        var area = snapshot.IsWindowedShow ? WindowChrome.GetClientBounds(snapshot.WindowHandle)
            : ScreenService.FromWindow(snapshot.WindowHandle).Bounds;
        double scale = Math.Min(area.Width / Math.Max(1, presentation.SlideWidth), area.Height / Math.Max(1, presentation.SlideHeight));
        if (scale <= 0) return new(Array.Empty<InkStroke>(), Matrix3x2.Identity);
        float left = (float)(area.X + (area.Width - presentation.SlideWidth * scale) / 2);
        float top = (float)(area.Y + (area.Height - presentation.SlideHeight * scale) / 2);
        var worldToSlide = Services.OverlayInk.ViewTransform * Matrix3x2.CreateScale((float)Overlay.Window.DpiScale)
            * Matrix3x2.CreateTranslation(Overlay.Window.X - left, Overlay.Window.Y - top) * Matrix3x2.CreateScale((float)(1 / scale));
        return new(await _pptSession.CaptureSlideInkAsync(slideId, token), worldToSlide);
    }

    private bool NavigatePptPreview(PptPresentationInfo presentation, PptSlideInfo slide)
    {
        if (!_lastSlideShowState || Services.OverlayInk.HasActiveInteraction
            || _pptSession.Snapshot.Presentation?.Identity != presentation.Identity) return false;
        long now = Environment.TickCount64;
        if (!_pptGuard.TryEnter(now)) return false;
        try
        {
            _pptCaptureGeneration = _pptGate.NextGeneration();
            _pptCaptureSession = _pptGate.Session;
            _ = SaveScreenArchiveAsync("PPT 翻页前存档");
            bool moved = slide.SlideId > 0 ? _pptSession.GoToSlideId(slide.SlideId) : _pptSession.GoToSlide(slide.Index);
            if (moved) _pptGuard.SuppressExternal(now);
            QueuePresentationRefresh();
            return moved;
        }
        finally { _pptGuard.Exit(now); }
    }
}
