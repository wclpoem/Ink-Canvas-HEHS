using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkCanvas.Controls.Controls;

/// <summary>
/// A WinUI 3 ink surface for opaque contexts (the blackboard/whiteboard window).
/// <para>
/// Uses the same <see cref="InkController"/> and <see cref="InkSceneRenderer"/> as the
/// transparent overlay, so board ink and screen ink behave identically — including the
/// live taper. The transparent screen-pen overlay cannot use this control because a WinUI 3
/// window is always opaque; it uses <see cref="OverlayInkPresenter"/> instead.
/// </para>
/// </summary>
public sealed class InkSurface : UserControl, IDisposable
{
    private readonly CanvasControl _canvas;
    private InkController _controller;
    private InkSceneRenderer _scene;
    private readonly CommittedInkLayer _committed = new();
    private DotAnimationClock _dotClock;
    private readonly Dictionary<uint, OverlayPointer> _activePointers = new();
    private bool _disposed;
    public bool IsRenderingSuspended { get; private set; }
    private bool _presentationPaused;
    /// <summary>Lets a document move a cached composition surface while retaining input and ink caches.</summary>
    public void PausePresentation(bool paused)
    {
        if (_presentationPaused == paused) return;
        _presentationPaused = paused;
        _canvas.Opacity = paused ? 0 : 1;
        if (!paused && !IsRenderingSuspended) _canvas.Invalidate();
    }
    public event Action InputBegan;
    /// <summary>Lets a continuous document bind the touched page before its first sample.</summary>
    public event Action<OverlayPointer> InputStarting;
    public event Action InputEnded;
    public event Action InputCanceled;
    /// <summary>Signals that the current controller view has been drawn for a host presentation handover.</summary>
    public event Action FrameDrawn;
    public bool DrawCommitted { get; set; } = true;
    /// <summary>Optional page extent in document coordinates. Null keeps an infinite canvas.</summary>
    public InkCanvas.Core.Ink.Rect? ContentBounds { get; set; }

    public InkSurface()
    {
        _canvas = new CanvasControl
        {
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
        };
        _canvas.Draw += OnDraw;
        Content = _canvas;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerEntered += OnPointerEntered;
        PointerExited += (_, args) => _controller?.HideEraserPreview(args.Pointer.PointerId);
        Unloaded += (_, _) => _controller?.HideEraserPreview();
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCanceled;

        ManipulationMode = ManipulationModes.None; // gestures are handled by InkController
    }

    public InkController Controller
    {
        get => _controller;
        set
        {
            // Detach the OLD controller first (both Invalidated and Document.Changed), so a
            // board rebind never leaves the previous document driving redraws of this surface.
            if (_controller is not null)
            {
                _dotClock?.Dispose();
                _dotClock = null;
                foreach (var sample in _activePointers.Values) _controller.OnPointerCanceled(sample);
                _activePointers.Clear();
                _controller.HideEraserPreview();
                _controller.Invalidated -= OnInvalidated;
                _controller.EraserPreviewChanged -= OnInvalidated;
                _controller.Document.Changed -= OnDocumentChanged;
                _controller.PlaybackChanged -= OnPlaybackChanged;
            }
            _controller = value;
            _scene?.Dispose();
            _committed.Dispose();
            _scene = new InkSceneRenderer();
            if (_controller is not null)
            {
                _dotClock = new DotAnimationClock(_controller, OnInvalidated);
                _controller.Invalidated += OnInvalidated;
                _controller.EraserPreviewChanged += OnInvalidated;
                _controller.Document.Changed += OnDocumentChanged;
                _controller.PlaybackChanged += OnPlaybackChanged;
            }
            _canvas.Invalidate();
        }
    }

    /// <summary>Board background colour; transparent when the host paints it.</summary>
    public Windows.UI.Color ClearColor
    {
        get => _canvas.ClearColor;
        set => _canvas.ClearColor = value;
    }

    private void OnDocumentChanged()
    {
        _committed.Invalidate();
        _scene?.Strokes.Retain(_controller.Document.Strokes);
        _canvas.Invalidate();
    }
    private void OnPlaybackChanged() { _committed.Invalidate(); _canvas.Invalidate(); }

    // Latency contract (board): XAML delivers pointer events on the UI thread and the handlers
    // below run the controller inline, exactly like the overlay's WndProc path. Invalidate()
    // schedules the Win2D draw on the very next composition render tick — the earliest a XAML
    // surface can present — and Win2D coalesces repeated invalidations into one draw. There is
    // no dispatcher hop, no timer, and no redraw of an unchanged scene: CanvasControl only draws
    // when invalidated, never on a free-running loop.
    private void OnInvalidated() { if (!IsRenderingSuspended && !_presentationPaused) _canvas.Invalidate(); }

    public void SuspendRendering()
    {
        IsRenderingSuspended = true;
        _scene?.ReleaseResources();
        _committed.Dispose();
    }

    public void ResumeRendering()
    {
        if (_disposed) return;
        IsRenderingSuspended = false;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_controller is null || _scene is null || IsRenderingSuspended || _presentationPaused) return;
        _controller.ViewCenter = new System.Numerics.Vector2(
            (float)ActualWidth / 2, (float)ActualHeight / 2);
        Microsoft.Graphics.Canvas.CanvasActiveLayer clip = null;
        if (ContentBounds is { } bounds)
        {
            var first = System.Numerics.Vector2.Transform(new(bounds.Left, bounds.Top), _controller.ViewTransform);
            var last = System.Numerics.Vector2.Transform(new(bounds.Right, bounds.Bottom), _controller.ViewTransform);
            clip = args.DrawingSession.CreateLayer(1, new Windows.Foundation.Rect(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y)));
        }
        using (clip)
        {
        _scene.ShowSelectionOutline = ContentBounds is null;
        if (DrawCommitted) _committed.Draw(args.DrawingSession, _scene, _controller, (float)ActualWidth, (float)ActualHeight, sender.Dpi);
        _scene.DrawLive(args.DrawingSession, _controller);
        }
        if (ContentBounds is not null) _scene.DrawSelectionOverlay(args.DrawingSession, _controller);
        FrameDrawn?.Invoke();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var sample = Translate(e);
        InputStarting?.Invoke(sample);
        if (_controller is null) return;
        InputBegan?.Invoke();
        CapturePointer(e.Pointer);
        bool gestureContact = sample.Kind == OverlayPointerKind.Touch && !_controller.MultiTouchEnabled
            && _activePointers.Values.Any(pointer => pointer.Kind == OverlayPointerKind.Touch);
        if (ContentBounds is { } bounds && _controller.Tool is not (InkTool.Pan or InkTool.Select)
            && !gestureContact
            && System.Numerics.Matrix3x2.Invert(_controller.ViewTransform, out var inverse)
            && !bounds.Contains(System.Numerics.Vector2.Transform(new(sample.X, sample.Y), inverse)))
        { ReleasePointerCapture(e.Pointer); return; }
        _activePointers[sample.Id] = sample;
        _controller.OnPointerDown(sample);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_controller is null) return;
        var sample = Translate(e);
        if (!_activePointers.ContainsKey(sample.Id))
        {
            _controller.OnPointerHover(sample);
            return;
        }
        // WinUI may coalesce several digitizer packets into one routed event.
        // Intermediate points are newest first. Preserve their original timestamps.
        var points = e.GetIntermediatePoints(this);
        // Erase every sample, then update board layers once for the coalesced input packet.
        using var eraseUpdate = _controller.Tool == InkTool.Eraser ? _controller.Document.DeferNotifications() : null;
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var intermediate = Translate(e, points[i]);
            _activePointers[intermediate.Id] = intermediate;
            _controller.OnPointerMove(intermediate);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (_controller is not null) _controller.OnPointerHover(Translate(args));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_controller is null) return;
        if (!_activePointers.Remove(e.Pointer.PointerId)) return;
        _controller.OnPointerUp(Translate(e));
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (_activePointers.Count == 0) InputEnded?.Invoke();
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_controller is null) return;
        // Capture-lost may no longer carry a current point, and follows normal up too.
        if (_activePointers.Remove(e.Pointer.PointerId, out var last))
        {
            InputCanceled?.Invoke();
            _controller.OnPointerCanceled(last);
            if (_activePointers.Count == 0) InputEnded?.Invoke();
        }
    }

    private OverlayPointer Translate(PointerRoutedEventArgs e)
        => Translate(e, e.GetCurrentPoint(this));

    private OverlayPointer Translate(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint point)
    {
        var props = point.Properties;
        var kind = point.PointerDeviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Pen => OverlayPointerKind.Pen,
            Microsoft.UI.Input.PointerDeviceType.Touch => OverlayPointerKind.Touch,
            _ => OverlayPointerKind.Mouse,
        };
        var contact = props.ContactRect;
        bool hasPressure = false;
        if (kind == OverlayPointerKind.Pen)
        {
            if (_activePointers.TryGetValue(e.Pointer.PointerId, out var previous) && previous.HasPressure)
                hasPressure = true;
            else if (InkCanvas.Platform.Interop.NativeMethods.GetPointerPenInfo(e.Pointer.PointerId, out var pen))
                hasPressure = (pen.penMask & 1) != 0;
            else
                hasPressure = float.IsFinite(props.Pressure) && props.Pressure != .5f;
        }
        return new OverlayPointer(
            e.Pointer.PointerId,
            (float)point.Position.X,
            (float)point.Position.Y,
            kind == OverlayPointerKind.Pen ? Math.Clamp(props.Pressure, 0, 1) : .5f,
            (float)contact.Width,
            (float)contact.Height,
            kind,
            (uint)(point.Timestamp / 1000),
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0,
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            HasPressure = hasPressure,
            TiltX = kind == OverlayPointerKind.Pen ? props.XTilt : 0,
            TiltY = kind == OverlayPointerKind.Pen ? props.YTilt : 0,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dotClock?.Dispose();
        _dotClock = null;
        if (_controller is not null)
        {
            foreach (var sample in _activePointers.Values) _controller.OnPointerCanceled(sample);
            _controller.HideEraserPreview();
            _controller.Invalidated -= OnInvalidated;
            _controller.EraserPreviewChanged -= OnInvalidated;
            _controller.Document.Changed -= OnDocumentChanged;
            _controller.PlaybackChanged -= OnPlaybackChanged;
        }
        _activePointers.Clear();
        _controller = null;
        _canvas.Draw -= OnDraw;
        _scene?.Dispose();
        _committed.Dispose();
        _scene = null;
        _canvas.RemoveFromVisualTree();
        Content = null;
    }
}
