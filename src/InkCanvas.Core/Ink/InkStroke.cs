using System.Numerics;
using System.Runtime.InteropServices;
using InkCanvas.Core.Selection;

namespace InkCanvas.Core.Ink;

/// <summary>
/// A single stroke. Widths are assigned on arrival by a <see cref="TaperProfile"/> and
/// stored in each <see cref="InkPoint"/>; <see cref="Complete"/> deliberately does not
/// recompute them. That is what makes the committed stroke render identically to the
/// last live frame.
/// </summary>
public sealed class InkStroke
{
    private readonly List<InkPoint> _points = new();
    private readonly List<float> _arcLengths = new();
    private TaperProfile _taper;
    private OneEuroFilter _filter;
    private HardwarePressureProfile _hardwarePressure;
    private bool _boundsDirty = true;
    private Rect _bounds;
    private float _hardwareRadius;
    private Matrix3x2 _boundsNib;
    private SelectionFrame? _objectFrame;

    public InkStroke(StrokeAttributes attributes)
    {
        Attributes = attributes ?? throw new ArgumentNullException(nameof(attributes));
        if (attributes.EffectiveTaper == TaperKind.InkWash && attributes.InkWashSeed == 0)
            attributes.InkWashSeed = Random.Shared.Next(1, 10000);
        if (attributes.EffectiveTaper is >= TaperKind.SmoothPen and <= TaperKind.InkWash) _filter = new OneEuroFilter();
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public ShapeSource ShapeSource { get; set; }

    public StrokeAttributes Attributes { get; }

    public IReadOnlyList<InkPoint> Points => _points;

    public int Count => _points.Count;

    public bool IsCompleted { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesHardwarePressure => _hardwarePressure is not null;

    /// <summary>Prefix whose widths can no longer change when another sample arrives.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int StablePointCount => IsCompleted || _hardwarePressure is not null ? Count : _taper?.StablePointCount(Count) ?? Count;

    public void RestorePrefix(IReadOnlyList<InkPoint> points, int count)
    {
        _points.Clear();
        _arcLengths.Clear();
        for (int i = 0; i < Math.Min(count, points.Count); i++) _points.Add(points[i]);
        _boundsDirty = true; _objectFrame = null; GeometryVersion++;
    }

    /// <summary>Changes only when point positions or widths change; render caches can retain untouched ink.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long GeometryVersion { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SelectionFrame ObjectFrame
    {
        get => _objectFrame ?? new SelectionFrame(Bounds, Matrix3x2.Identity);
        set => _objectFrame = value;
    }

    public float[] ExportObjectFrame() => _objectFrame is { } frame ? new[]
    {
        frame.Bounds.Left, frame.Bounds.Top, frame.Bounds.Right, frame.Bounds.Bottom,
        frame.Transform.M11, frame.Transform.M12, frame.Transform.M21, frame.Transform.M22, frame.Transform.M31, frame.Transform.M32,
    } : null;

    public void ImportObjectFrame(float[] data)
    {
        if (data is not { Length: 10 } || data.Any(v => !float.IsFinite(v))) return;
        var bounds = new Rect(data[0], data[1], data[2], data[3]);
        var transform = new Matrix3x2(data[4], data[5], data[6], data[7], data[8], data[9]);
        if (!bounds.IsEmpty && MathF.Abs(transform.GetDeterminant()) > 1e-6f) _objectFrame = new SelectionFrame(bounds, transform);
    }

    /// <summary>Set when the stroke was produced by shape recognition rather than freehand.</summary>
    public bool IsRecognizedShape { get; set; }


    public TaperKind TaperKind
    {
        get => _taper?.Kind ?? Attributes.EffectiveTaper;
        set
        {
            if (TaperKind == value) { Attributes.TaperKind = value; return; }
            _taper = null;
            _hardwarePressure = null;
            Attributes.TaperKind = value;
            _filter = value is >= TaperKind.SmoothPen and <= TaperKind.InkWash ? new OneEuroFilter() : null;
            // Re-evaluate from scratch only for an already-finished stroke (e.g. a settings
            // change applied to history). Live strokes never take this path.
            if (IsCompleted) RebuildWidths();
        }
    }

    public void AddPoint(float x, float y, float pressure = 0.5f, float timeMs = 0f,
        bool hasPressure = false, float tiltX = 0, float tiltY = 0)
    {
        if (_filter is not null)
        {
            var filtered = _filter.Update(new Vector2(x, y), timeMs);
            x = filtered.X;
            y = filtered.Y;
        }
        // Width starts at 0 so the profile's monotonic "settle" rule can raise it.
        bool validPressure = hasPressure && float.IsFinite(pressure);
        pressure = float.IsFinite(pressure) ? Math.Clamp(pressure, 0, 1) : .5f;
        _points.Add(new InkPoint(x, y, pressure, 0f, timeMs, validPressure,
            float.IsFinite(tiltX) ? Math.Clamp(tiltX, -89, 89) : 0,
            float.IsFinite(tiltY) ? Math.Clamp(tiltY, -89, 89) : 0));
        ApplyWidth(CollectionsMarshal.AsSpan(_points), _points.Count - 1);
        _boundsDirty = true;
        GeometryVersion++;
        _objectFrame = null;
    }

    /// <summary>
    /// Marks the stroke finished. Intentionally a no-op with respect to geometry.
    /// </summary>
    public void Complete() => IsCompleted = true;

    private void ApplyWidth(Span<InkPoint> points, int index)
    {
        if (points[index].HasPressure)
            _hardwarePressure ??= new HardwarePressureProfile(Attributes.EffectiveTaper, Attributes.IsHighlighter);
        if (_hardwarePressure is not null) _hardwarePressure.Apply(points, index, Attributes.BaseWidth);
        else (_taper ??= TaperProfile.Create(Attributes.EffectiveTaper)).Apply(points, index, Attributes.BaseWidth);
    }

    private void RebuildWidths()
    {
        var span = CollectionsMarshal.AsSpan(_points);
        for (int i = 0; i < span.Length; i++) span[i].Width = 0f;
        for (int i = 0; i < span.Length; i++) ApplyWidth(span, i);
        _boundsDirty = true;
        GeometryVersion++;
    }

    public Rect Bounds
    {
        get
        {
            if (_boundsDirty || _boundsNib != Attributes.NibTransform)
            {
                _boundsNib = Attributes.NibTransform;
                _bounds = ComputeBounds();
                _boundsDirty = false;
            }
            return _bounds;
        }
    }

    /// <summary>Includes wide hardware-pressure strokes in object selection.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float HitRadius
    {
        get { _ = Bounds; return MathF.Max(Attributes.BaseWidth * .5f, _hardwareRadius); }
    }

    /// <summary>Damage bounds including the segment joining the stable prefix to the changing tail.</summary>
    public Rect BoundsFrom(int firstPoint) => ComputeBounds(Math.Max(0, firstPoint));

    private Rect ComputeBounds(int firstPoint = 0)
    {
        if (firstPoint >= _points.Count) return Rect.Empty;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        float maxHalf = 0f;
        bool tilted = false;
        bool hardware = false;
        for (int index = firstPoint; index < _points.Count; index++)
        {
            var p = _points[index];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Width > maxHalf) maxHalf = p.Width;
            tilted |= p.TiltX != 0 || p.TiltY != 0;
            hardware |= p.HasPressure;
        }
        maxHalf = MathF.Max(maxHalf, .5f) * 0.5f;
        var nib = Attributes.NibTransform;
        float rx = maxHalf * MathF.Sqrt(nib.M11 * nib.M11 + nib.M21 * nib.M21);
        float ry = maxHalf * MathF.Sqrt(nib.M12 * nib.M12 + nib.M22 * nib.M22);
        // Tilt spreads the brush and offsets its dark core. Include both vector layers.
        bool waterInk = InkBrush.UsesInkWash(this);
        float spread = waterInk ? 1.9f : tilted ? 1.8f : 1;
        if (firstPoint == 0) _hardwareRadius = hardware ? maxHalf * spread : 0;
        return Rect.FromLTRB(minX - rx * spread, minY - ry * spread, maxX + rx * spread, maxY + ry * spread);
    }

    /// <summary>Total centreline length, used by replay and by the recogniser.</summary>
    public float ArcLength() => Count == 0 ? 0 : ArcLengthAt(Count - 1);

    /// <summary>Lazily extends an append-only length cache, keeping streamed brush chunks linear overall.</summary>
    public float ArcLengthAt(int pointIndex)
    {
        if (pointIndex < 0 || pointIndex >= Count) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        if (_arcLengths.Count == 0) _arcLengths.Add(0);
        for (int i = _arcLengths.Count; i <= pointIndex; i++)
            _arcLengths.Add(_arcLengths[^1] + Vector2.Distance(_points[i - 1].Position, _points[i].Position));
        return _arcLengths[pointIndex];
    }

    public void Transform(Matrix3x2 m, bool scaleWidth = true)
    {
        var frame = ObjectFrame;
        var span = CollectionsMarshal.AsSpan(_points);
        float widthScale = 1f;
        if (scaleWidth)
        {
            widthScale = MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));
            if (widthScale <= 0.0001f) widthScale = 1f;
        }
        for (int i = 0; i < span.Length; i++)
        {
            var v = Vector2.Transform(new Vector2(span[i].X, span[i].Y), m);
            span[i].X = v.X;
            span[i].Y = v.Y;
            span[i].Width *= widthScale;
            var tilt = Vector2.TransformNormal(new Vector2(span[i].TiltX, span[i].TiltY), m);
            float tiltLength = new Vector2(span[i].TiltX, span[i].TiltY).Length();
            if (tilt.LengthSquared() > .0001f) tilt = Vector2.Normalize(tilt) * tiltLength;
            span[i].TiltX = tilt.X;
            span[i].TiltY = tilt.Y;
        }
        Attributes.BaseWidth *= widthScale;
        _arcLengths.Clear();
        if (Attributes.EffectiveTaper == TaperKind.InkWash)
        {
            float angle = -Attributes.InkWash.Angle * MathF.PI / 180;
            var direction = Vector2.TransformNormal(new Vector2(MathF.Cos(angle), MathF.Sin(angle)), m);
            Attributes.InkWash = Attributes.InkWash with { Angle = -MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI };
        }
        if (Attributes.Nib == PenNib.Ellipse45)
        {
            float angle = -MathF.PI / 4 + Attributes.NibRotation;
            var direction = Vector2.TransformNormal(new Vector2(MathF.Cos(angle), MathF.Sin(angle)), m);
            Attributes.NibRotation = MathF.Atan2(direction.Y, direction.X) + MathF.PI / 4;
        }
        _boundsDirty = true;
        GeometryVersion++;
        _objectFrame = frame with { Transform = frame.Transform * m };
        if (ShapeSource?.Geometry is { } geometry)
            ShapeSource = ShapeSource.FromGeometry(ShapeSource.GroupId, geometry.Transformed(m));
    }

    /// <summary>Shortest distance from a point to this stroke's centreline.</summary>
    public float DistanceTo(Vector2 p)
    {
        if (ShapeSource?.Geometry?.IsFill == true && ContainsPolygon(p)) return 0;
        if (_points.Count == 0) return float.MaxValue;
        if (_points.Count == 1) return Vector2.Distance(p, _points[0].Position);
        float best = float.MaxValue;
        for (int i = 1; i < _points.Count; i++)
        {
            float d = DistanceToSegment(p, _points[i - 1].Position, _points[i].Position);
            if (d < best) best = d;
        }
        return best;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public InkStroke Clone(bool preserveId = true)
    {
        var copy = new InkStroke(Attributes.Clone()) { Id = preserveId ? Id : Guid.NewGuid(), IsRecognizedShape = IsRecognizedShape, LayerOrder = LayerOrder, ShapeSource = ShapeSource };
        copy._points.AddRange(_points);
        copy.IsCompleted = IsCompleted;
        copy._boundsDirty = true;
        copy._objectFrame = _objectFrame;
        return copy;
    }

    public static List<InkStroke> CloneObjects(IEnumerable<InkStroke> strokes)
    {
        var originals = strokes.ToArray();
        var groups = originals.Where(stroke => stroke.ShapeSource is not null).Select(stroke => stroke.ShapeSource.GroupId)
            .Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        return originals.Select(stroke =>
        {
            var copy = stroke.Clone(preserveId: false);
            if (stroke.ShapeSource is { } source)
            {
                if (!groups.TryGetValue(source.GroupId, out var id)) groups[source.GroupId] = id = Guid.NewGuid();
                copy.ShapeSource = source with { GroupId = id };
                if (source.Geometry is { } geometry)
                    copy.ShapeSource = ShapeSource.FromGeometry(id, geometry with
                    {
                        StartNode = geometry.StartNode is { } start && groups.TryGetValue(start, out var startCopy) ? startCopy : geometry.StartNode,
                        EndNode = geometry.EndNode is { } end && groups.TryGetValue(end, out var endCopy) ? endCopy : geometry.EndNode,
                    });
            }
            return copy;
        }).ToList();
    }

    private bool ContainsPolygon(Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = _points.Count - 1; i < _points.Count; j = i++)
        {
            var a = _points[i]; var b = _points[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Replace all points, preserving already-computed widths (used by persistence).</summary>
    public void RestorePoints(IEnumerable<InkPoint> points)
    {
        _points.Clear();
        _arcLengths.Clear();
        _points.AddRange(points);
        _boundsDirty = true;
        GeometryVersion++;
        _objectFrame = null;
    }
}


/// <summary>UI-independent rectangle.</summary>
public readonly record struct Rect(float Left, float Top, float Right, float Bottom)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);

    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public Vector2 Center => new((Left + Right) * 0.5f, (Top + Bottom) * 0.5f);

    public static Rect FromLTRB(float l, float t, float r, float b) => new(l, t, r, b);

    public bool Contains(Vector2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Intersects(Rect o) => !(o.Left > Right || o.Right < Left || o.Top > Bottom || o.Bottom < Top);

    public Rect Union(Rect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return new Rect(MathF.Min(Left, o.Left), MathF.Min(Top, o.Top),
                        MathF.Max(Right, o.Right), MathF.Max(Bottom, o.Bottom));
    }

    public Rect Inflate(float d) => new(Left - d, Top - d, Right + d, Bottom + d);
}
