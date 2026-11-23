using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rect = Windows.Foundation.Rect;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private sealed record PagePreview(CanvasBitmap Bitmap, double Scale);
    private readonly Dictionary<int, PagePreview> _pagePreviews = new();
    private readonly Dictionary<int, InkSceneRenderer> _pageScenes = new();
    private readonly Dictionary<int, Core.Ink.Rect> _pageInkBounds = new();
    private readonly ScrollBar _scrollbar = new() { Orientation = Orientation.Vertical, Width = 20, Minimum = 0, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private readonly ScrollBar _horizontalScrollbar = new() { Orientation = Orientation.Horizontal, Height = 20, Minimum = 0, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private bool _scrollUpdating, _previewQueued, _activePageQueued;
    private int _previewDirection;
    private long _previewBytes;
    internal int BitmapRenderCount { get; private set; }
    internal bool PreviewWorkerRunning => _renderPending || _previewQueued;
    internal IReadOnlyCollection<int> CachedPages => _pagePreviews.Keys;
    public long VisiblePreviewBytes => _previewBytes;
    public int VisiblePageCount => VisiblePages().Length;
    internal Vector2 ViewportSize => new((float)_viewport.ActualWidth, (float)_viewport.ActualHeight);
    internal InkCanvas.Controls.Controls.InkSurface AnnotationSurface => _ink;
    private Matrix3x2 DocumentView => _layout.DocumentView(_pageIndex, Controller.ViewTransform);

    private IReadOnlyList<InkStroke> PageInk(int index)
        => _controllers.TryGetValue(index, out var controller) ? controller.Document.Strokes : _file.GetInk(index);

    private void UpdateInkBounds(int index)
    {
        var bounds = Core.Ink.Rect.Empty;
        foreach (var stroke in PageInk(index)) bounds = bounds.Union(stroke.Bounds);
        if (bounds.IsEmpty) _pageInkBounds.Remove(index);
        else _pageInkBounds[index] = bounds;
    }

    private IEnumerable<int> VisibleInkPages()
        => VisibleInkPages(DocumentView, new(0, 0, ViewportSize.X, ViewportSize.Y));

    private IEnumerable<int> VisibleInkPages(Matrix3x2 document, Core.Ink.Rect screen)
    {
        if (!Matrix3x2.Invert(document, out var inverse)) yield break;
        var first = Vector2.Transform(new(screen.Left, screen.Top), inverse);
        var last = Vector2.Transform(new(screen.Right, screen.Bottom), inverse);
        var viewport = new Core.Ink.Rect(first.X, first.Y, last.X, last.Y);
        foreach (var pair in _pageInkBounds)
        {
            var origin = new Vector2(_layout[pair.Key].Left, _layout[pair.Key].Top);
            var bounds = pair.Value;
            if (viewport.Intersects(new(bounds.Left + origin.X, bounds.Top + origin.Y, bounds.Right + origin.X, bounds.Bottom + origin.Y)))
                yield return pair.Key;
        }
    }

    private int[] VisiblePages()
    {
        if (Controller is null || !Matrix3x2.Invert(DocumentView, out var inverse)) return Array.Empty<int>();
        float top = Vector2.Transform(Vector2.Zero, inverse).Y;
        float bottom = Vector2.Transform(new(0, (float)_viewport.ActualHeight), inverse).Y;
        return _layout.VisiblePages(top, bottom).ToArray();
    }

    internal void ActivateTouchedPage(OverlayPointer sample)
    {
        if (Controller is null || Controller.HasActiveInteraction || !Matrix3x2.Invert(DocumentView, out var inverse)) return;
        var point = Vector2.Transform(new(sample.X, sample.Y), inverse);
        int index = _layout.NearestPage(point.Y);
        ActivatePage(index, preserveView: true);
    }

    private void QueueActivePage()
    {
        if (_activePageQueued) return;
        _activePageQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { _activePageQueued = false; UpdateActivePage(); });
    }

    private void UpdateActivePage()
    {
        if (_closed || _pageChanging || Controller is null || Controller.HasActiveInteraction || _scrollRendering) return;
        var before = Controller.ViewTransform;
        ClampDocumentView();
        if (Controller.ViewTransform != before) Controller.NotifyElementsChanged();
        if (!Matrix3x2.Invert(DocumentView, out var inverse)) return;
        float y = Vector2.Transform(new(0, (float)_viewport.ActualHeight / 2), inverse).Y;
        ActivatePage(_layout.NearestPage(y), preserveView: true);
    }

    public void ScrollBy(float delta)
    {
        if (Controller is null || Controller.HasActiveInteraction) return;
        StopScrollMotion();
        _fitMode = FitMode.Free;
        var view = Controller.ViewTransform;
        view.Translation += new Vector2(0, delta);
        Controller.ViewTransform = view;
        ClampDocumentView();
        Controller.NotifyElementsChanged();
        UpdateActivePage();
    }

    private void ClampDocumentView()
    {
        if (!_viewHasLayout) return;
        var document = DocumentView;
        var offset = ScrollBounds.Clamp(document.Translation) - document.Translation;
        if (offset.LengthSquared() > .000001f)
        {
            var view = Controller.ViewTransform;
            view.Translation += offset;
            Controller.ViewTransform = view;
        }
    }

    private void UpdateScrollbar()
    {
        if (_scrollUpdating || !_viewHasLayout) return;
        _scrollUpdating = true;
        try
        {
            var document = DocumentView;
            var bounds = ScrollBounds;
            var offset = bounds.Offset(document.Translation);
            _scrollbar.ViewportSize = _viewport.ActualHeight;
            _scrollbar.Maximum = bounds.Extent.Y;
            _scrollbar.LargeChange = Math.Max(32, _viewport.ActualHeight * .85);
            _scrollbar.SmallChange = 70;
            _scrollbar.IsEnabled = bounds.Extent.Y > .01f;
            _scrollbar.Value = offset.Y;
            _horizontalScrollbar.ViewportSize = _viewport.ActualWidth;
            _horizontalScrollbar.Maximum = bounds.Extent.X;
            _horizontalScrollbar.LargeChange = Math.Max(32, _viewport.ActualWidth * .85);
            _horizontalScrollbar.SmallChange = 70;
            _horizontalScrollbar.IsEnabled = bounds.Extent.X > .01f;
            _horizontalScrollbar.Value = offset.X;
        }
        finally { _scrollUpdating = false; }
    }

    private void ScrollbarChanged()
    {
        if (_scrollUpdating || Controller is null) return;
        if (Controller.HasActiveInteraction) { UpdateScrollbar(); return; }
        StopScrollMotion();
        _fitMode = FitMode.Free;
        var view = Controller.ViewTransform;
        view.Translation += ScrollBounds.Translation(new((float)_horizontalScrollbar.Value, (float)_scrollbar.Value)) - DocumentView.Translation;
        Controller.ViewTransform = view;
        Controller.NotifyElementsChanged();
        UpdateActivePage();
    }

    private void QueuePageRendering()
    {
        if (_closed || !_idleResources.IsActive || !_viewHasLayout || _previewQueued || _renderPending) return;
        _previewQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _previewQueued = false;
            if (!_closed) _ = RenderPageAsync();
        });
    }

    private async Task RenderPageAsync()
    {
        if (_closed || !_idleResources.IsActive || !_viewHasLayout || _renderPending) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _renderCancellation = cancellation;
        _renderPending = true;
        bool failed = false;
        try
        {
            // One worker always re-evaluates the latest viewport. Moving a few pixels must
            // not cancel an almost finished page and restart its rendering from scratch.
            while (!_closed && _idleResources.IsActive)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var request = PreviewRequests().FirstOrDefault(item => !PreviewReady(item));
                if (request.Scale == 0) break;
                int index = request.Index;
                var page = _file.Pages[index];
                var bitmap = await _rasterizer.RenderBitmapAsync(index, page, new Rect(0, 0, page.DipWidth, page.DipHeight), request.Scale, cancellation.Token);
                if (_closed || cancellation.IsCancellationRequested) { bitmap.Dispose(); return; }
                BitmapRenderCount++;
                if (!PreviewRequests().Any(item => item.Index == index)) { bitmap.Dispose(); continue; }
                if (_pagePreviews.Remove(index, out var previous)) { _previewBytes -= BitmapBytes(previous.Bitmap); previous.Bitmap.Dispose(); }
                _pagePreviews[index] = new(bitmap, request.Scale);
                _previewBytes += BitmapBytes(bitmap);
                TrimPagePreviews();
                _navigationPictureDirty = true;
                if (_navigationPictureActive) QueueViewportUpdate();
                else _paper.Invalidate();
            }
            _detailTimer.Stop(); _detailTimer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed = true; if (!_closed && !cancellation.IsCancellationRequested) SetStatus("页面渲染失败 · " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_renderCancellation, cancellation)) { _renderPending = false; _renderCancellation = null; }
            cancellation.Dispose();
            if (!failed && !_closed && _idleResources.IsActive && PreviewRequests().Any(item => !PreviewReady(item))) QueuePageRendering();
        }
    }

    private bool PreviewReady((int Index, double Scale) request)
        => _pagePreviews.TryGetValue(request.Index, out var cached) && cached.Scale >= request.Scale * .99
            && ((Controller.IsViewManipulating || _scrollRendering) && VisiblePreviewBytes <= 104 * 1024 * 1024
                || cached.Scale <= request.Scale * 1.01);

    private static long BitmapBytes(CanvasBitmap bitmap) => (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4;

    private IEnumerable<(int Index, double Scale)> PreviewRequests()
    {
        if (!_viewHasLayout || !Matrix3x2.Invert(DocumentView, out var inverse)) yield break;
        float top = Vector2.Transform(Vector2.Zero, inverse).Y;
        float bottom = Vector2.Transform(ViewportSize, inverse).Y;
        var visible = _layout.VisiblePages(top, bottom).ToHashSet();
        double area = visible.Sum(index => (double)_file.Pages[index].DipWidth * _file.Pages[index].DipHeight);
        double zoom = Math.Sqrt(Math.Abs(DocumentView.GetDeterminant())) * (_root.XamlRoot?.RasterizationScale ?? 1);
        double maximum = Controller.IsViewManipulating || _scrollRendering ? .7 : 1.4;
        double visibleScale = Math.Min(maximum, Math.Min(zoom, Math.Sqrt(16_000_000 / Math.Max(1, area))));
        foreach (int index in _layout.PreviewPages(top, bottom, _previewDirection))
        {
            var page = _file.Pages[index];
            double scale = visible.Contains(index) ? visibleScale : Math.Min(visibleScale, Math.Sqrt(1_000_000 / ((double)page.DipWidth * page.DipHeight)));
            scale = 1.4 / Math.Pow(2, Math.Max(0, Math.Ceiling(Math.Log2(1.4 / Math.Max(.01, scale)))));
            yield return (index, scale);
        }
    }

    private void TrimPagePreviews()
    {
        var requested = PreviewRequests().Select(item => item.Index).ToHashSet();
        foreach (int index in _pagePreviews.Keys.Where(index => !requested.Contains(index)).OrderByDescending(index => Math.Abs(index - _pageIndex)).ToArray())
        {
            if (_pagePreviews.Count <= Math.Max(6, requested.Count) && VisiblePreviewBytes <= 104 * 1024 * 1024) break;
            _previewBytes -= BitmapBytes(_pagePreviews[index].Bitmap);
            _pagePreviews[index].Bitmap.Dispose(); _pagePreviews.Remove(index);
        }
        var inkPages = VisibleInkPages().ToHashSet();
        foreach (int index in _pageScenes.Keys.Where(index => !inkPages.Contains(index)).ToArray())
        { _pageScenes[index].Dispose(); _pageScenes.Remove(index); }
    }

    private void ReleasePagePreviews()
    {
        foreach (var preview in _pagePreviews.Values) preview.Bitmap.Dispose();
        _pagePreviews.Clear();
        _previewBytes = 0;
        foreach (var scene in _pageScenes.Values) scene.Dispose();
        _pageScenes.Clear();
    }

    private void DrawPage(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_closed || _idleResources.IsReleased || Controller is null) return;
        DrawDocument(args.DrawingSession, DocumentView, new(0, 0, ViewportSize.X, ViewportSize.Y), Matrix3x2.Identity, includeActiveInk: false);
        _handoverPaperReady = true;
        CompleteNavigationHandover();
    }

    private void DrawDocument(CanvasDrawingSession draw, Matrix3x2 document, Core.Ink.Rect screen, Matrix3x2 output, bool includeActiveInk)
    {
        if (!Matrix3x2.Invert(document, out var inverse)) return;
        float top = Vector2.Transform(new(screen.Left, screen.Top), inverse).Y;
        float bottom = Vector2.Transform(new(screen.Right, screen.Bottom), inverse).Y;
        foreach (int index in _layout.VisiblePages(top, bottom))
        {
            var page = _file.Pages[index];
            var view = _layout.PageView(index, document);
            var bounds = new Rect(0, 0, page.DipWidth, page.DipHeight);
            draw.Transform = view * output;
            float border = 1 / MathF.Sqrt(MathF.Abs(view.GetDeterminant()));
            draw.DrawRectangle(new Rect(-border, -border, bounds.Width + 2 * border, bounds.Height + 2 * border), _pageEdgeColor, border);
            draw.FillRectangle(bounds, Microsoft.UI.Colors.White);
            if (_pagePreviews.TryGetValue(index, out var preview)) draw.DrawImage(preview.Bitmap, bounds, preview.Bitmap.Bounds);
            if (index == _pageIndex && _detail is not null) draw.DrawImage(_detail, _detailRect, _detail.Bounds);
            DrawSearchHighlights(draw, index);
        }
        draw.Transform = Matrix3x2.Identity;
        // All page backgrounds precede ink so margin strokes can cross page gaps.
        foreach (int index in VisibleInkPages(document, screen))
        {
            if (index == _pageIndex && !includeActiveInk) continue;
            var view = _layout.PageView(index, document) * output;
            var strokes = PageInk(index);
            if (strokes.Count == 0) continue;
            if (!_pageScenes.TryGetValue(index, out var scene)) _pageScenes[index] = scene = new InkSceneRenderer { ShowSelectionOutline = false };
            scene.Strokes.Retain(strokes);
            foreach (var stroke in strokes) scene.Strokes.Draw(draw, stroke, view);
        }
        draw.Transform = Matrix3x2.Identity;
    }
}
