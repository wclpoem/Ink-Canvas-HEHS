using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

/// <summary>Cached ink/image runs interleaved with native video views in the same XAML canvas.</summary>
internal sealed class BoardDrawingLayers : Canvas, IDisposable
{
    private sealed class Run
    {
        public CanvasControl Canvas;
        public DrawingObject[] Objects = Array.Empty<DrawingObject>();
        public readonly CommittedInkLayer Cache = new();
    }

    private readonly List<Run> _runs = new();
    private readonly Dictionary<Guid, int> _indices = new();
    private InkController _controller;
    private InkSceneRenderer _scene = new(4096) { ShowSelectionOutline = false };
    private bool _suspended;

    public BoardDrawingLayers()
    {
        SizeChanged += (_, _) =>
        {
            foreach (var run in _runs)
            {
                run.Canvas.Width = ActualWidth;
                run.Canvas.Height = ActualHeight;
            }
        };
        _scene.Images.Changed += Invalidate;
    }

    public int LayerIndex(Guid id) => _indices.GetValueOrDefault(id);
    public int CachedImageCount => _scene.Images.CachedBitmapCount;
    internal long RasterBytes => _runs.Sum(run => run.Cache.RasterBytes);
    internal int SurfaceCount => _runs.Count;

    internal async Task SaveSceneAsync(string path)
    {
        await _scene.Images.PrepareAsync(CanvasDevice.GetSharedDevice(), _controller);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)ActualWidth, (float)ActualHeight, 96);
        using (var draw = target.CreateDrawingSession())
        {
            draw.Clear(Microsoft.UI.Colors.Transparent);
            foreach (var run in _runs.OrderBy(run => GetZIndex(run.Canvas)))
                run.Cache.Draw(draw, _scene, _controller, (float)ActualWidth, (float)ActualHeight, objectsOverride: run.Objects);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }

    public void UpdateOrder(InkController controller)
    {
        if (!ReferenceEquals(_controller, controller))
        {
            if (_controller is not null) _controller.DotAnimationChanged -= Invalidate;
            foreach (var run in _runs) run.Cache.Dispose();
            _scene.Dispose();
            _scene = new InkSceneRenderer(4096) { ShowSelectionOutline = false };
            _scene.Images.Changed += Invalidate;
            _controller = controller;
            _controller.DotAnimationChanged += Invalidate;
        }
        var groups = new List<(int Index, DrawingObject[] Objects)>();
        var pending = new List<DrawingObject>();
        _indices.Clear();
        int first = 0;
        for (int index = 0; index < controller.DrawingObjects.Count; index++)
        {
            var item = controller.DrawingObjects[index];
            _indices[item.Id] = index;
            if (item.Element is BoardSelectionElement { Media: not null })
            {
                if (pending.Count > 0)
                {
                    groups.Add((first, pending.ToArray()));
                    pending.Clear();
                }
            }
            else
            {
                if (pending.Count == 0) first = index;
                pending.Add(item);
            }
        }
        if (pending.Count > 0) groups.Add((first, pending.ToArray()));
        while (_runs.Count > groups.Count)
        {
            var last = _runs[^1];
            last.Cache.Dispose();
            Children.Remove(last.Canvas);
            last.Canvas.RemoveFromVisualTree();
            _runs.RemoveAt(_runs.Count - 1);
        }
        for (int index = 0; index < groups.Count; index++)
        {
            if (index == _runs.Count)
            {
                var run = new Run { Canvas = new CanvasControl { IsHitTestVisible = false,
                    ClearColor = Microsoft.UI.Colors.Transparent, Width = ActualWidth, Height = ActualHeight } };
                run.Canvas.Draw += (sender, args) =>
                {
                    if (!_suspended) run.Cache.Draw(args.DrawingSession, _scene, _controller,
                        (float)ActualWidth, (float)ActualHeight, sender.Dpi, objectsOverride: run.Objects);
                };
                _runs.Add(run);
                Children.Add(run.Canvas);
            }
            var current = _runs[index];
            current.Cache.RasterBudgetBytes = 64L * 1024 * 1024 / groups.Count;
            if (!current.Objects.SequenceEqual(groups[index].Objects)) current.Objects = groups[index].Objects;
            SetZIndex(current.Canvas, groups[index].Index);
            current.Cache.Invalidate();
            current.Canvas.Invalidate();
        }
        _scene.Strokes.Retain(controller.Document.Strokes);
        _scene.Images.Retain(controller);
    }

    public void Invalidate()
    {
        if (_suspended) return;
        foreach (var run in _runs) run.Canvas.Invalidate();
    }

    public void SuspendRendering()
    {
        _suspended = true;
        foreach (var run in _runs) run.Cache.Dispose();
        _scene.ReleaseResources();
    }

    public void ResumeRendering()
    {
        _suspended = false;
        Invalidate();
    }

    private void ClearRuns()
    {
        foreach (var run in _runs)
        {
            run.Cache.Dispose();
            Children.Remove(run.Canvas);
            run.Canvas.RemoveFromVisualTree();
        }
        _runs.Clear();
    }

    public void Dispose()
    {
        if (_controller is not null) _controller.DotAnimationChanged -= Invalidate;
        ClearRuns();
        _scene.Dispose();
    }
}
