using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;

namespace InkCanvas.Features.Tools;

/// <summary>Session-local page memory. Keeps real object identity and each page's undo/redo.</summary>
public sealed class ScreenAnnotationMemory
{
    private sealed class Page
    {
        public IReadOnlyList<InkStroke> Strokes = Array.Empty<InkStroke>();
        public IReadOnlyList<BoardImageAsset> Images = Array.Empty<BoardImageAsset>();
        public UndoHistoryState History;
        public Matrix3x2 View = Matrix3x2.Identity, BaseView = Matrix3x2.Identity;
        public ScreenViewSnapshot Anchor;
        public Matrix3x2 WorldToContent = Matrix3x2.Identity;
    }
    private readonly InkController _controller;
    private readonly BoardService _images;
    private readonly Dictionary<string, Page> _pages = new(StringComparer.Ordinal);
    private readonly Page _desktop = new();
    private Page _current;
    private string _key;
    private Matrix3x2 _lastDelta = Matrix3x2.Identity;
    private ScreenViewSnapshot _lastSnapshot;
    private readonly UnchangedRegionInk _shared = new();
    private Matrix3x2 _overlayToScreen = Matrix3x2.Identity;
    private bool _preserveRegions;
    public string CurrentKey => _key;
    public int RememberedPages => _pages.Count;

    public (IReadOnlyList<InkStroke> Strokes, Matrix3x2 WorldToContent)? CapturePreview(string contextSuffix)
    {
        var page = _lastSnapshot?.ContextKey.StartsWith("PowerPoint:", StringComparison.Ordinal) == true
            && _lastSnapshot.ContextKey.EndsWith(contextSuffix, StringComparison.Ordinal) ? _current
            : _pages.FirstOrDefault(pair => pair.Key.StartsWith("PowerPoint:", StringComparison.Ordinal)
                && pair.Key.EndsWith(contextSuffix, StringComparison.Ordinal)).Value;
        if (page is null) return null;
        var strokes = ReferenceEquals(page, _current) ? _controller.Document.Strokes : page.Strokes;
        var transform = page.WorldToContent;
        if (ReferenceEquals(page, _current) && _lastSnapshot is not null
            && Matrix3x2.Invert(_lastSnapshot.ContentToScreen(), out var inverse))
            transform = _controller.ViewTransform * _overlayToScreen * inverse;
        return (strokes.Select(stroke => stroke.Clone()).ToArray(), transform);
    }

    public ScreenAnnotationMemory(InkController controller, BoardService images)
    {
        _controller = controller; _images = images; _current = _desktop;
    }

    public bool Apply(ScreenViewSnapshot snapshot, ExperimentalSettings settings, Vector2 overlayOrigin, float overlayDpiScale)
    {
        if (!snapshot.IsValid || _controller.HasActiveInteraction || !float.IsFinite(overlayDpiScale) || overlayDpiScale <= 0) return false;
        string key = settings.RememberPages ? snapshot.ContextKey : "active-tracked-page";
        bool changed = _key != key;
        _preserveRegions = settings.PreserveUnchangedRegions;
        _overlayToScreen = Matrix3x2.CreateScale(overlayDpiScale) * Matrix3x2.CreateTranslation(overlayOrigin);
        var outgoing = changed && settings.PreserveUnchangedRegions ? _controller.Document.Strokes.ToArray() : Array.Empty<InkStroke>();
        var outgoingView = _controller.ViewTransform;
        if (changed)
        {
            Capture();
            if (!_pages.TryGetValue(key, out var page))
            {
                page = new Page { Anchor = snapshot };
                // Bind pre-existing desktop ink once, preserving all original undo actions.
                if (_key is null && _pages.Count == 0)
                {
                    page.Strokes = _desktop.Strokes; page.Images = _desktop.Images; page.History = _desktop.History;
                    page.View = page.BaseView = _desktop.View;
                    _desktop.Strokes = Array.Empty<InkStroke>(); _desktop.Images = Array.Empty<BoardImageAsset>(); _desktop.History = null;
                }
                _pages.Add(key, page);
            }
            _current = page; _key = key; _lastDelta = Matrix3x2.Identity;
        }
        if (!changed && _controller.ViewTransform != _current.View && Matrix3x2.Invert(_lastDelta, out var previousInverse))
            _current.BaseView = _controller.ViewTransform * previousInverse;
        _current.Anchor ??= snapshot;
        float scale = settings.FollowZoom && snapshot.HasZoomMetrics ? snapshot.Scale : _current.Anchor.Scale;
        Vector2 scroll = settings.FollowScroll && snapshot.HasScrollMetrics ? snapshot.ScrollPixels : _current.Anchor.ScrollPixels;
        var toOverlay = Matrix3x2.CreateTranslation(-overlayOrigin) * Matrix3x2.CreateScale(1 / overlayDpiScale);
        var anchor = _current.Anchor.ContentToScreen() * toOverlay;
        if (Matrix3x2.Invert(anchor, out var inverse))
            _lastDelta = inverse * snapshot.ContentToScreen(scale, scroll) * toOverlay;
        _current.View = _current.BaseView * _lastDelta;
        if (changed)
        {
            if (settings.PreserveUnchangedRegions)
            {
                var toScreen = Matrix3x2.CreateScale(overlayDpiScale) * Matrix3x2.CreateTranslation(overlayOrigin);
                _current.Strokes = _shared.Merge(_lastSnapshot, snapshot, outgoing, outgoingView * toScreen,
                    _current.Strokes, _current.View * toScreen, out bool synchronized);
                if (synchronized) _current.History = null;
            }
            _controller.RestoreScreenContext(_current.Strokes, _current.History, _current.View);
            _images.ReplaceCurrentImages(_current.Images);
        }
        if (_controller.ViewTransform != _current.View)
        {
            _controller.ViewTransform = _current.View;
            _controller.NotifyElementsChanged();
        }
        _lastSnapshot = snapshot;
        return true;
    }

    public bool Leave()
    {
        if (_key is null) return true;
        if (_controller.HasActiveInteraction) return false;
        if (_preserveRegions) _shared.Capture(_lastSnapshot, _controller.Document.Strokes, _controller.ViewTransform * _overlayToScreen);
        Capture(); _key = null; _current = _desktop; _lastDelta = Matrix3x2.Identity; _lastSnapshot = null;
        _controller.RestoreScreenContext(_desktop.Strokes, _desktop.History, _desktop.View);
        _images.ReplaceCurrentImages(_desktop.Images);
        return true;
    }

    public void Reanchor(ScreenViewSnapshot snapshot)
    {
        _current.Anchor = snapshot;
        _current.BaseView = _controller.ViewTransform;
        _current.View = _controller.ViewTransform;
        _lastDelta = Matrix3x2.Identity;
    }

    private void Capture()
    {
        if (_controller.ViewTransform != _current.View && Matrix3x2.Invert(_lastDelta, out var inverse))
            _current.BaseView = _controller.ViewTransform * inverse;
        _current.Strokes = _controller.Document.Strokes.ToArray();
        _current.Images = _images.CurrentImages.ToArray();
        _current.History = _controller.History.Capture();
        _current.View = _controller.ViewTransform;
        if (_lastSnapshot is not null && Matrix3x2.Invert(_lastSnapshot.ContentToScreen(), out var contentInverse))
            _current.WorldToContent = _controller.ViewTransform * _overlayToScreen * contentInverse;
    }
}
