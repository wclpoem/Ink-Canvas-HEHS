using System.Numerics;
using InkCanvas.Ink;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.DirectX;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly Grid _navigationPicture = new() { IsHitTestVisible = false };
    private CompositionGraphicsDevice _pictureDevice;
    private CompositionDrawingSurface _pictureSurface;
    private CompositionSurfaceBrush _pictureBrush;
    private SpriteVisual _pictureSprite;
    private ContainerVisual _pictureRoot;
    private Matrix3x2 _pictureView;
    private Core.Ink.Rect _pictureBounds;
    private bool _navigationPictureActive, _navigationPictureDirty = true, _viewportUpdateQueued;
    private bool _pictureMotionActive;
    private bool _navigationHandover, _handoverPaperReady, _handoverInkReady;
    internal int ViewportUpdateCount { get; private set; }
    internal int NavigationPictureDraws { get; private set; }
    internal bool UsesNavigationPicture => _navigationPictureActive;
    internal bool UsesCompositionScroll => _pictureMotionActive;
    internal bool NavigationHandoverPending => _navigationHandover;
    internal long NavigationPictureBytes => _pictureSurface is null ? 0 : (long)_pictureSurface.Size.Width * (long)_pictureSurface.Size.Height * 4;

    private void QueueViewportUpdate()
    {
        if (_viewportUpdateQueued || _closed) return;
        _viewportUpdateQueued = true;
        CompositionTarget.Rendering += UpdateViewportFrame;
    }

    private void UpdateViewportFrame(object sender, object args)
    {
        CompositionTarget.Rendering -= UpdateViewportFrame;
        _viewportUpdateQueued = false;
        if (_closed || !_idleResources.IsActive) return;
        ViewportUpdateCount++;
        UpdateNavigationPicture();
        if (!_navigationPictureActive) _paper.Invalidate();
        string zoom = (Math.Sqrt(Math.Abs(Controller.ViewTransform.GetDeterminant())) * 100).ToString("0") + "%";
        if (_zoom.Content?.ToString() != zoom) _zoom.Content = zoom;
        UpdateReadingControls();
        _detailCancellation?.Cancel();
        _detailTimer.Stop(); _detailTimer.Start();
        QueuePageRendering();
        UpdateScrollbar();
        if (!_scrollRendering) QueueActivePage();
        QueueSelection();
    }

    private void UpdateNavigationPicture()
    {
        bool navigating = Controller.IsViewManipulating || _scrollRendering;
        if (!navigating || Controller.Playback is not null)
        {
            EndNavigationPicture();
            return;
        }
        if (_navigationPictureDirty || !PictureCoversViewport()) DrawNavigationPicture();
        if (_pictureRoot is null) return;
        if (!_pictureMotionActive)
        {
            Matrix3x2.Invert(_pictureView, out var inverse);
            _pictureRoot.TransformMatrix = new Matrix4x4(inverse * DocumentView);
            StartPictureMotion();
        }
        _pictureRoot.IsVisible = true;
        _navigationHandover = false;
        _paper.Opacity = 0;
        _ink.PausePresentation(true);
        _selectionBorder.Visibility = Visibility.Collapsed;
        _navigationPictureActive = true;
    }

    private bool PictureCoversViewport()
    {
        if (_pictureSurface is null || !Matrix3x2.Invert(DocumentView, out var inverse)) return false;
        var transform = inverse * _pictureView;
        var travel = _wheelTarget is { } target ? target - DocumentView.Translation : _scrollInertia.Project(_scrollInertia.RemainingSeconds);
        var future = Matrix3x2.CreateTranslation(-Vector2.Clamp(travel, -ViewportSize, ViewportSize)) * transform;
        return _pictureBounds.Contains(Vector2.Transform(Vector2.Zero, transform))
            && _pictureBounds.Contains(Vector2.Transform(ViewportSize, transform))
            && _pictureBounds.Contains(Vector2.Transform(Vector2.Zero, future))
            && _pictureBounds.Contains(Vector2.Transform(ViewportSize, future));
    }

    private void DrawNavigationPicture()
    {
        if (_closed || !_idleResources.IsActive || !_viewHasLayout || ViewportSize.X < 1 || ViewportSize.Y < 1) return;
        var size = ViewportSize;
        var margin = new Vector2(size.X * .25f, size.Y * .65f);
        var travel = _wheelTarget is { } target ? target - DocumentView.Translation : _scrollInertia.Project(_scrollInertia.RemainingSeconds);
        margin = Vector2.Max(margin, Vector2.Abs(travel) + size * .1f);
        margin = Vector2.Min(margin, size * 1.5f);
        var bounds = new Core.Ink.Rect(-margin.X, -margin.Y, size.X + margin.X, size.Y + margin.Y);
        double scale = Math.Min(_root.XamlRoot?.RasterizationScale ?? 1, Math.Sqrt(6_000_000 / ((double)bounds.Width * bounds.Height)));
        scale = Math.Min(scale, 8192 / Math.Max(bounds.Width, bounds.Height));
        var pixels = new Windows.Foundation.Size(Math.Ceiling(bounds.Width * scale), Math.Ceiling(bounds.Height * scale));
        var compositor = ElementCompositionPreview.GetElementVisual(_navigationPicture).Compositor;
        _pictureDevice ??= CanvasComposition.CreateCompositionGraphicsDevice(compositor, _rasterizer.Device);
        _pictureRoot ??= compositor.CreateContainerVisual();
        if (_pictureSurface is null || _pictureSurface.Size != pixels)
        {
            _pictureBrush?.Dispose(); _pictureSprite?.Dispose(); _pictureSurface?.Dispose();
            _pictureSurface = _pictureDevice.CreateDrawingSurface(pixels, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            _pictureBrush = compositor.CreateSurfaceBrush(_pictureSurface);
            _pictureSprite = compositor.CreateSpriteVisual();
            _pictureSprite.Brush = _pictureBrush;
            _pictureRoot.Children.RemoveAll();
            _pictureRoot.Children.InsertAtTop(_pictureSprite);
            ElementCompositionPreview.SetElementChildVisual(_navigationPicture, _pictureRoot);
        }
        StopPictureMotion();
        _pictureView = DocumentView;
        using (var draw = CanvasComposition.CreateDrawingSession(_pictureSurface))
        {
            draw.Clear(Microsoft.UI.Colors.Transparent);
            var output = Matrix3x2.CreateTranslation(-bounds.Left, -bounds.Top) * Matrix3x2.CreateScale((float)scale);
            DrawDocument(draw, _pictureView, bounds, output, includeActiveInk: true);
        }
        _pictureBounds = bounds;
        _pictureSprite.Size = new(bounds.Width, bounds.Height);
        _pictureSprite.Offset = new(bounds.Left, bounds.Top, 0);
        _pictureRoot.TransformMatrix = Matrix4x4.Identity;
        _pictureRoot.IsVisible = _navigationPictureActive;
        _navigationPicture.Clip = new RectangleGeometry { Rect = new(0, 0, size.X, size.Y) };
        _navigationPictureDirty = false;
        NavigationPictureDraws++;
    }

    private void EndNavigationPicture()
    {
        if (!_navigationPictureActive && !_navigationHandover) return;
        StopPictureMotion();
        if (_pictureRoot is not null)
        {
            Matrix3x2.Invert(_pictureView, out var inverse);
            _pictureRoot.TransformMatrix = new Matrix4x4(inverse * DocumentView);
        }
        if (!_navigationPictureActive) return;
        _navigationPictureActive = false;
        _navigationHandover = true;
        _handoverPaperReady = _handoverInkReady = false;
        _paper.Opacity = 1;
        _paper.Invalidate();
        _ink.PausePresentation(false);
        QueueSelection();
    }

    private void NavigationInkDrawn()
    {
        _handoverInkReady = true;
        CompleteNavigationHandover();
    }

    private void CompleteNavigationHandover()
    {
        if (!_navigationHandover || !_handoverPaperReady || !_handoverInkReady) return;
        // Keep the last cached frame visible until both editable layers are ready,
        // so ending a gesture cannot expose their previous viewport for one frame.
        _navigationHandover = false;
        if (_pictureRoot is not null) _pictureRoot.IsVisible = false;
    }

    private void StartPictureMotion()
    {
        if (!_scrollRendering || _pictureMotionActive) return;
        var origin = DocumentView.Translation;
        double duration = _wheelTarget is not null ? .36 : _scrollInertia.RemainingSeconds;
        if (duration < .02) return;
        var animation = _pictureRoot.Compositor.CreateVector3KeyFrameAnimation();
        using var linear = _pictureRoot.Compositor.CreateLinearEasingFunction();
        animation.Duration = TimeSpan.FromSeconds(duration);
        animation.InsertKeyFrame(0, Vector3.Zero);
        for (int sample = 1; sample <= 48; sample++)
        {
            double seconds = duration * sample / 48;
            var travel = _wheelTarget is { } target ? (target - origin) * (float)(1 - Math.Exp(-seconds / .055)) : _scrollInertia.Project(seconds);
            animation.InsertKeyFrame(sample / 48f, new(ScrollBounds.Clamp(origin + travel) - origin, 0), linear);
        }
        // The compositor moves the cached paper and ink while the UI thread handles
        // prefetch and scroll bar bookkeeping. Both paths use the same decay model.
        _pictureRoot.StartAnimation("Offset", animation);
        animation.Dispose();
        _pictureMotionActive = true;
    }

    private void StopPictureMotion()
    {
        if (!_pictureMotionActive) return;
        _pictureRoot.StopAnimation("Offset");
        _pictureRoot.Offset = Vector3.Zero;
        Matrix3x2.Invert(_pictureView, out var inverse);
        _pictureRoot.TransformMatrix = new Matrix4x4(inverse * DocumentView);
        _pictureMotionActive = false;
    }

    private void ReleaseNavigationPicture()
    {
        CompositionTarget.Rendering -= UpdateViewportFrame;
        _viewportUpdateQueued = false;
        EndNavigationPicture();
        _navigationHandover = false;
        ElementCompositionPreview.SetElementChildVisual(_navigationPicture, null);
        _pictureBrush?.Dispose(); _pictureSprite?.Dispose(); _pictureSurface?.Dispose();
        _pictureRoot?.Dispose(); _pictureDevice?.Dispose();
        _pictureBrush = null; _pictureSprite = null; _pictureSurface = null;
        _pictureRoot = null; _pictureDevice = null;
        _navigationPictureDirty = true;
    }
}
