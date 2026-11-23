using System.Numerics;
using InkCanvas.Core.Ink;
using Microsoft.Graphics.Canvas;

namespace InkCanvas.Ink.Rendering;

/// <summary>Visible history tiles. Translation reuses rasterized ink, including ink entering from outside the viewport.</summary>
public sealed class CommittedInkLayer : IDisposable
{
    private sealed class Run
    {
        public bool Moving;
        public IImageSelectionElement Image;
        public ISelectionElement Element;
        public readonly List<InkStroke> Strokes = new();
        public readonly List<InkCanvas.Core.Ink.Rect> Bounds = new();
        public readonly Dictionary<(int X, int Y), List<int>> TileStrokes = new();
        public readonly List<int> SpanningStrokes = new();
        public InkCanvas.Core.Ink.Rect Coverage = InkCanvas.Core.Ink.Rect.Empty;
        public CanvasRenderTarget SmallTarget;
        public Vector2 SmallOrigin;
    }
    private sealed class Tile
    {
        public CanvasRenderTarget Target;
        public int DrawnCount;
        public long LastUsed;
    }
    private readonly List<Run> _runs = new();
    private readonly Dictionary<(int Run, int X, int Y), Tile> _tiles = new();
    private readonly List<(InkStroke Stroke, long Version, uint Color, Matrix3x2 Nib, InkCanvas.Core.Ink.Rect Bounds)> _snapshot = new();
    private Matrix3x2 _linear;
    private Vector2 _rasterOffset;
    private InkPlayback _playback;
    private long _dotAnimationVersion;
    private long _recognitionVersion;
    private IReadOnlyList<InkStroke> _preview;
    private InkController _owner;
    private float _dpi;
    private bool _dirty = true;
    private bool _selectionGestureActive;
    private long _frame;
    private IReadOnlyList<DrawingObject> _objects = Array.Empty<DrawingObject>();
    private long _layerVersion;
    private const int MaxTiles = 512;
    private const long MaxTileRasterBytes = 48L * 516 * 516 * 4;
    private long _tileRasterBytes;
    private const long MaxSmallRasterBytes = 12 * 1024 * 1024;
    private long _smallRasterBytes;
    public long CommittedStrokeDraws { get; private set; }
    public long RebuildCount { get; private set; }
    public long RasterBytes => _tileRasterBytes + _smallRasterBytes;
    public long RasterBudgetBytes { get; set; } = MaxTileRasterBytes + MaxSmallRasterBytes;
    public void Invalidate() => _dirty = true;

    public void Draw(CanvasDrawingSession session, InkSceneRenderer scene, InkController controller,
        float width, float height, float dpi = 96, Matrix3x2? transformOverride = null,
        IReadOnlyList<DrawingObject> objectsOverride = null, bool reuseRaster = false)
    {
        if (width <= 0 || height <= 0) return;
        var objects = objectsOverride ?? controller.DrawingObjects;
        if (objects.Count == 0) { Dispose(); return; }
        var view = transformOverride ?? controller.ViewTransform;
        if (RasterBytes > RasterBudgetBytes) Dispose();
        long minimumRaster = 516L * 516 * 4 * (controller.IsSelectionGestureActive ? 4 : 1);
        if (RasterBudgetBytes < minimumRaster)
        {
            Dispose();
            foreach (var item in objects) scene.DrawObject(session, controller, item, view);
            return;
        }
        var viewport = new InkCanvas.Core.Ink.Rect(0, 0, width, height);
        scene.Images.TrimOutsideViewport(controller, view, viewport);
        var linear = view;
        linear.Translation = Vector2.Zero;
        float pixel = 96 / dpi;
        var rasterOffset = new Vector2(view.M31 - MathF.Floor(view.M31 / pixel) * pixel,
            view.M32 - MathF.Floor(view.M32 / pixel) * pixel);
        bool transforming = controller.IsViewManipulating || reuseRaster;
        bool reuseGestureRaster = transforming && _owner == controller && _dpi == dpi
            && Matrix3x2.Invert(_linear, out var previousInverse) && CanReuseRaster(previousInverse * linear);
        var preview = controller.PreviewStrokes;
        bool changed = !ReferenceEquals(_owner, controller) || linear != _linear && !reuseGestureRaster || dpi != _dpi
            || !ReferenceEquals(_playback, controller.Playback) || !ReferenceEquals(_preview, preview)
            || _selectionGestureActive != controller.IsSelectionGestureActive
            || _dotAnimationVersion != controller.DotAnimationVersion || _layerVersion != controller.LayerVersion
            || _recognitionVersion != controller.RecognitionSourcesVersion;
        bool orderChanged = !ReferenceEquals(_objects, objects) && !_objects.SequenceEqual(objects);
        if (_dirty || changed || orderChanged)
        {
            var strokes = objects.Where(item => item.Stroke is not null).Select(item => item.Stroke).ToArray();
            bool append = !changed && preview is null && _runs.Count == 1 && _runs[0].Image is null
                && objects.All(item => item.Stroke is not null) && strokes.Length > _snapshot.Count;
            for (int i = 0; append && i < _snapshot.Count; i++)
                append = ReferenceEquals(strokes[i], _snapshot[i].Stroke) && strokes[i].GeometryVersion == _snapshot[i].Version
                    && strokes[i].Attributes.EffectiveColor.PackedArgb == _snapshot[i].Color && strokes[i].Attributes.NibTransform == _snapshot[i].Nib;
            if (!append)
            {
                bool partial = !changed && rasterOffset == _rasterOffset && preview is null && _runs.Count == 1 && !_runs[0].Moving
                    && _runs[0].Image is null && objects.All(item => item.Stroke is not null);
                if (partial) RetainUnchangedTiles(strokes, pixel);
                else ClearTiles();
                ClearRuns(); _linear = linear; _rasterOffset = rasterOffset; RebuildCount++;
            }
            for (int i = append ? _snapshot.Count : 0; i < objects.Count; i++)
            {
                var item = objects[i];
                if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
                {
                    _runs.Add(new Run { Image = image });
                    continue;
                }
                // Native text/formula objects are vector elements: keep each as its own ordered run so
                // the tiled ink/image path never drops them (they used to be skipped here entirely).
                if (item.Element is IBoardTextElement or IBoardFormulaElement)
                {
                    _runs.Add(new Run { Element = item.Element });
                    continue;
                }
                if (item.Stroke is not { } stroke) continue;
                if (controller.Playback?.Includes(stroke) == true || controller.IsAnimatingDot(stroke)
                    || controller.IsRecognitionPreviewSource(stroke)) continue;
                bool moving = controller.IsPreviewStroke(stroke);
                if (_runs.Count == 0 || _runs[^1].Image is not null || _runs[^1].Element is not null || _runs[^1].Moving != moving)
                    _runs.Add(new Run { Moving = moving });
                _runs[^1].Strokes.Add(stroke);
                var bounds = TransformBounds(stroke.Bounds, _linear * Matrix3x2.CreateTranslation(_rasterOffset));
                _runs[^1].Bounds.Add(bounds);
                _runs[^1].Coverage = _runs[^1].Coverage.Union(bounds);
                IndexStroke(_runs[^1], bounds, pixel * 512, pixel * 2);
            }
            _snapshot.Clear();
            foreach (var stroke in strokes) _snapshot.Add((stroke, stroke.GeometryVersion, stroke.Attributes.EffectiveColor.PackedArgb, stroke.Attributes.NibTransform, stroke.Bounds));
            _owner = controller; _preview = preview; _playback = controller.Playback; _dpi = dpi;
            _dotAnimationVersion = controller.DotAnimationVersion; _dirty = false;
            _recognitionVersion = controller.RecognitionSourcesVersion;
            _objects = objects; _layerVersion = controller.LayerVersion;
            _selectionGestureActive = controller.IsSelectionGestureActive;
        }
        _frame++;
        float side = 512 * pixel;
        for (int runIndex = 0; runIndex < _runs.Count; runIndex++)
        {
            var run = _runs[runIndex];
            if (run.Image is not null)
            {
                scene.Images.Draw(session, run.Image, view, viewport);
                continue;
            }
            if (run.Element is not null)
            {
                scene.DrawElement(session, controller, run.Element, view);
                continue;
            }
            var runView = run.Moving ? controller.SelectionPreviewTransform * view : view;
            var previewTransform = controller.SelectionPreviewTransform;
            bool translating = previewTransform.M11 == 1 && previewTransform.M22 == 1 && previewTransform.M12 == 0 && previewTransform.M21 == 0;
            // Drawing the runs in document order preserves translucent intersections and stacking.
            if (run.Moving && !translating && !controller.IsSelectionGestureActive)
            {
                foreach (var stroke in run.Strokes) scene.Strokes.Draw(session, stroke, runView);
                continue;
            }
            Matrix3x2.Invert(_linear * Matrix3x2.CreateTranslation(_rasterOffset), out var inverseRasterView);
            var runLinear = runView;
            runLinear.Translation = Vector2.Zero;
            var rasterToView = runLinear == _linear ? Matrix3x2.CreateTranslation(runView.Translation - _rasterOffset) : inverseRasterView * runView;
            if (!Matrix3x2.Invert(rasterToView, out var inverseRaster)) continue;
            var visible = TransformBounds(new InkCanvas.Core.Ink.Rect(0, 0, width, height), inverseRaster);
            if (!run.Coverage.Intersects(visible)) continue;
            if (run.Strokes.Count <= 2 && preview is not null)
            {
                if (DrawSmallRun(session, scene, run, rasterToView, pixel, dpi, run.Moving && _selectionGestureActive ? 2 : 1)) continue;
                foreach (var stroke in run.Strokes) scene.Strokes.Draw(session, stroke, runView);
                continue;
            }
            int firstX = (int)MathF.Floor(visible.Left / side), firstY = (int)MathF.Floor(visible.Top / side);
            int lastX = (int)MathF.Ceiling(visible.Right / side), lastY = (int)MathF.Ceiling(visible.Bottom / side);
            var previousTransform = session.Transform;
            session.Transform = rasterToView * previousTransform;
            try
            {
                for (int y = firstY; y < lastY; y++)
                for (int x = firstX; x < lastX; x++)
                {
                    var bounds = new InkCanvas.Core.Ink.Rect(x * side - 2 * pixel, y * side - 2 * pixel,
                        (x + 1) * side + 2 * pixel, (y + 1) * side + 2 * pixel);
                    if (!run.Coverage.Intersects(bounds)) continue;
                    if (!run.TileStrokes.ContainsKey((x, y)) && run.SpanningStrokes.Count == 0) continue;
                    var key = (runIndex, x, y);
                    if (!_tiles.TryGetValue(key, out var tile))
                    {
                        float rasterScale = run.Moving && _selectionGestureActive ? 2
                            : Math.Clamp(MathF.Sqrt(MathF.Abs(rasterToView.GetDeterminant())), .125f, 1);
                        long pixels = (long)MathF.Ceiling(516 * rasterScale);
                        long bytes = pixels * pixels * 4;
                        while (_tiles.Count > 0 && (_tiles.Count >= MaxTiles || _tileRasterBytes + bytes > Math.Min(MaxTileRasterBytes, RasterBudgetBytes * 4 / 5)))
                        {
                            var oldest = _tiles.MinBy(pair => pair.Value.LastUsed);
                            _tileRasterBytes -= (long)oldest.Value.Target.SizeInPixels.Width * oldest.Value.Target.SizeInPixels.Height * 4;
                            oldest.Value.Target.Dispose(); _tiles.Remove(oldest.Key);
                        }
                        tile = new Tile { Target = new CanvasRenderTarget(session, side + 4 * pixel, side + 4 * pixel,
                            dpi * rasterScale) };
                        _tiles.Add(key, tile);
                        _tileRasterBytes += (long)tile.Target.SizeInPixels.Width * tile.Target.SizeInPixels.Height * 4;
                    }
                    tile.LastUsed = _frame;
                    if (tile.DrawnCount < run.Strokes.Count)
                    {
                        using var draw = tile.Target.CreateDrawingSession();
                        if (tile.DrawnCount == 0) draw.Clear(Microsoft.UI.Colors.Transparent);
                        var transform = _linear * Matrix3x2.CreateTranslation(_rasterOffset + new Vector2(-x * side + 2 * pixel, -y * side + 2 * pixel));
                        if (run.SpanningStrokes.Count == 0 && run.TileStrokes.TryGetValue((x, y), out var candidates))
                        {
                            foreach (int i in candidates)
                            {
                                if (i < tile.DrawnCount) continue;
                                scene.Strokes.Draw(draw, run.Strokes[i], transform); CommittedStrokeDraws++;
                            }
                        }
                        else if (run.SpanningStrokes.Count > 0)
                        {
                            for (int i = tile.DrawnCount; i < run.Strokes.Count; i++)
                            {
                                if (!run.Bounds[i].Intersects(bounds)) continue;
                                scene.Strokes.Draw(draw, run.Strokes[i], transform); CommittedStrokeDraws++;
                            }
                        }
                        tile.DrawnCount = run.Strokes.Count;
                    }
                    session.DrawImage(tile.Target, new Windows.Foundation.Rect(x * side, y * side, side, side),
                        new Windows.Foundation.Rect(2 * pixel, 2 * pixel, side, side));
                }
            }
            finally { session.Transform = previousTransform; }
        }
    }

    private static void IndexStroke(Run run, InkCanvas.Core.Ink.Rect bounds, float side, float padding)
    {
        int index = run.Strokes.Count - 1;
        int left = (int)MathF.Floor((bounds.Left - padding) / side);
        int top = (int)MathF.Floor((bounds.Top - padding) / side);
        int right = (int)MathF.Floor((bounds.Right + padding) / side);
        int bottom = (int)MathF.Floor((bounds.Bottom + padding) / side);
        if ((long)(right - left + 1) * (bottom - top + 1) > 256)
        { run.SpanningStrokes.Add(index); return; }
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            if (!run.TileStrokes.TryGetValue((x, y), out var strokes))
                run.TileStrokes[(x, y)] = strokes = new();
            strokes.Add(index);
        }
    }

    // Alternating selected and stationary handwriting produces many one-stroke runs.
    // Tight rasters keep those runs in document order without rebuilding their paths per frame
    // or allocating a full 512-pixel tile for each small character.
    private bool DrawSmallRun(CanvasDrawingSession session, InkSceneRenderer scene, Run run,
        Matrix3x2 rasterToView, float pixel, float dpi, int rasterScale)
    {
        if (run.SmallTarget is null)
        {
            var origin = new Vector2(MathF.Floor(run.Coverage.Left / pixel) - 2,
                MathF.Floor(run.Coverage.Top / pixel) - 2) * pixel;
            int width = (int)MathF.Ceiling((run.Coverage.Right - origin.X) / pixel) + 2;
            int height = (int)MathF.Ceiling((run.Coverage.Bottom - origin.Y) / pixel) + 2;
            long bytes = (long)width * height * 4 * rasterScale * rasterScale;
            if (width <= 0 || height <= 0 || width > 512 || height > 512
                || _smallRasterBytes + bytes > Math.Min(MaxSmallRasterBytes, RasterBudgetBytes / 5)) return false;
            run.SmallTarget = new CanvasRenderTarget(session, width * pixel, height * pixel, dpi * rasterScale);
            run.SmallOrigin = origin;
            _smallRasterBytes += bytes;
            using var draw = run.SmallTarget.CreateDrawingSession();
            draw.Clear(Microsoft.UI.Colors.Transparent);
            var transform = _linear * Matrix3x2.CreateTranslation(_rasterOffset - origin);
            foreach (var stroke in run.Strokes)
            {
                scene.Strokes.Draw(draw, stroke, transform);
                CommittedStrokeDraws++;
            }
        }
        var previous = session.Transform;
        try
        {
            session.Transform = rasterToView * previous;
            session.DrawImage(run.SmallTarget, run.SmallOrigin);
        }
        finally { session.Transform = previous; }
        return true;
    }

    private void RetainUnchangedTiles(InkStroke[] strokes, float pixel)
    {
        var retained = _snapshot.Select(item => item.Stroke).ToHashSet();
        var incoming = strokes.ToHashSet();
        if (retained.Count != _snapshot.Count || incoming.Count != strokes.Length
            || !_snapshot.Where(item => incoming.Contains(item.Stroke)).Select(item => item.Stroke)
            .SequenceEqual(strokes.Where(retained.Contains)))
        {
            ClearTiles();
            return;
        }
        var previous = _snapshot.ToDictionary(item => item.Stroke);
        var dirtyBounds = new List<InkCanvas.Core.Ink.Rect>();
        foreach (var old in _snapshot)
            if (!incoming.Contains(old.Stroke)) dirtyBounds.Add(old.Bounds);
        foreach (var stroke in strokes)
        {
            if (previous.TryGetValue(stroke, out var old) && old.Version == stroke.GeometryVersion
                && old.Color == stroke.Attributes.EffectiveColor.PackedArgb && old.Nib == stroke.Attributes.NibTransform) continue;
            dirtyBounds.Add(stroke.Bounds);
            if (previous.ContainsKey(stroke)) dirtyBounds.Add(old.Bounds);
        }
        var transform = _linear * Matrix3x2.CreateTranslation(_rasterOffset);
        var changed = dirtyBounds.Select(bounds => TransformBounds(bounds, transform).Inflate(2 * pixel)).ToArray();
        float side = 512 * pixel;
        foreach (var pair in _tiles.ToArray())
        {
            var bounds = new InkCanvas.Core.Ink.Rect(pair.Key.X * side - 2 * pixel, pair.Key.Y * side - 2 * pixel,
                (pair.Key.X + 1) * side + 2 * pixel, (pair.Key.Y + 1) * side + 2 * pixel);
            if (changed.Any(area => area.Intersects(bounds)))
            {
                _tileRasterBytes -= (long)pair.Value.Target.SizeInPixels.Width * pair.Value.Target.SizeInPixels.Height * 4;
                pair.Value.Target.Dispose();
                _tiles.Remove(pair.Key);
            }
            else pair.Value.DrawnCount = strokes.Length;
        }
    }

    private static bool CanReuseRaster(Matrix3x2 delta)
    {
        // Rebuild at gesture end, so a zoom quality threshold cannot stall active fingers.
        return float.IsFinite(delta.M11) && float.IsFinite(delta.M12)
            && float.IsFinite(delta.M21) && float.IsFinite(delta.M22)
            && MathF.Abs(delta.GetDeterminant()) > 1e-6f;
    }

    public static InkCanvas.Core.Ink.Rect TransformBounds(InkCanvas.Core.Ink.Rect bounds, Matrix3x2 transform)
    {
        if (bounds.IsEmpty) return bounds;
        var center = Vector2.Transform(bounds.Center, transform);
        float x = (MathF.Abs(transform.M11) * bounds.Width + MathF.Abs(transform.M21) * bounds.Height) / 2;
        float y = (MathF.Abs(transform.M12) * bounds.Width + MathF.Abs(transform.M22) * bounds.Height) / 2;
        return InkCanvas.Core.Ink.Rect.FromLTRB(center.X - x, center.Y - y, center.X + x, center.Y + y);
    }
    private void ClearTiles()
    {
        foreach (var tile in _tiles.Values) tile.Target.Dispose();
        _tiles.Clear();
        _tileRasterBytes = 0;
    }
    private void ClearRuns()
    {
        foreach (var run in _runs) run.SmallTarget?.Dispose();
        _runs.Clear(); _smallRasterBytes = 0;
    }
    public void Dispose()
    {
        ClearTiles(); ClearRuns(); _snapshot.Clear(); _objects = Array.Empty<DrawingObject>();
        _owner = null; _preview = null; _playback = null; _dirty = true;
    }
}
