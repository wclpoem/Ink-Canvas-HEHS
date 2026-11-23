using System.Diagnostics;
using System.Numerics;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly PdfScrollInertia _scrollInertia = new();
    private Vector2? _wheelTarget;
    private bool _scrollRendering, _touchNavigating, _touchZoomed;
    private float _touchStartScale;
    private double _scrollFrame;
    private static double NavigationTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private const float DocumentBottomPadding = 84;
    private PdfScrollBounds ScrollBounds
    {
        get
        {
            float scale = MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
            return PdfScrollBounds.Create(new(_layout.Width, _layout.Height + DocumentBottomPadding / scale), ViewportSize, scale);
        }
    }

    private void ConfigureNavigation()
    {
        _ink.InputCanceled += () => { _touchNavigating = false; StopScrollMotion(); };
        _ink.InputEnded += NavigationInputEnded;
        AppWindow.Changed += (_, args) => { if (args.DidVisibilityChange && !AppWindow.IsVisible) StopScrollMotion(); };
    }

    internal void NavigationInputStarting(OverlayPointer sample)
    {
        StopScrollMotion();
        _touchNavigating = false;
        _touchZoomed = false;
        _touchStartScale = MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
        _scrollInertia.Reset(DocumentView.Translation, NavigationTime);
        ActivateTouchedPage(sample);
    }

    private void ObserveNavigation()
    {
        if (Controller.IsViewManipulating && Controller.Tool != InkTool.Pan)
        {
            float scale = MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
            if (Math.Abs(scale / _touchStartScale - 1) > .02f) _touchZoomed = true;
            _touchNavigating = true;
            _scrollInertia.Observe(DocumentView.Translation, NavigationTime);
        }
    }

    internal void NavigationInputEnded()
    {
        UpdateActivePage();
        if (_touchNavigating && !_touchZoomed && Controller.Tool != InkTool.Pan && !Controller.HasActiveInteraction && _scrollInertia.Start(NavigationTime))
            StartScrollRendering();
        _touchNavigating = false;
        QueueViewportUpdate();
    }

    internal void SmoothScrollBy(Vector2 delta)
    {
        if (Controller.HasActiveInteraction) return;
        StopPictureMotion();
        _scrollInertia.Stop();
        _fitMode = FitMode.Free;
        _wheelTarget = ScrollBounds.Clamp((_wheelTarget ?? DocumentView.Translation) + delta);
        StartScrollRendering();
    }

    private void StartScrollRendering()
    {
        if (_scrollRendering) return;
        _scrollFrame = NavigationTime;
        _scrollRendering = true;
        CompositionTarget.Rendering += RenderScrollMotion;
    }

    private void StopScrollMotion()
    {
        if (_scrollRendering && !_closed && !Controller.HasActiveInteraction)
        {
            // Catch up to the visible compositor position before a new finger takes over.
            AdvanceScrollMotion(NavigationTime);
        }
        _scrollInertia.Stop();
        _wheelTarget = null;
        EndNavigationPicture();
        if (!_scrollRendering) return;
        CompositionTarget.Rendering -= RenderScrollMotion;
        _scrollRendering = false;
    }

    private void RenderScrollMotion(object sender, object args)
    {
        if (_closed || Controller.HasActiveInteraction || !_idleResources.IsActive) { StopScrollMotion(); return; }
        AdvanceScrollMotion(NavigationTime);
        if (_wheelTarget is null && !_scrollInertia.IsRunning) { StopScrollMotion(); UpdateActivePage(); }
    }

    private void AdvanceScrollMotion(double now)
    {
        double elapsed = now - _scrollFrame;
        _scrollFrame = now;
        var before = DocumentView.Translation;
        Vector2 delta;
        if (_wheelTarget is { } target)
        {
            target = ScrollBounds.Clamp(target);
            delta = (target - before) * (float)(1 - Math.Exp(-elapsed / .055));
            if (Vector2.DistanceSquared(target, before + delta) < .16f) { delta = target - before; _wheelTarget = null; }
        }
        else delta = _scrollInertia.Advance(elapsed);
        var after = ScrollBounds.Clamp(before + delta);
        _scrollInertia.Constrain(delta, after - before);
        var view = Controller.ViewTransform;
        view.Translation += after - before;
        Controller.ViewTransform = view;
        if (after != before) Controller.NotifyElementsChanged();
    }
}
