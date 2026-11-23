using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>Timestamp-based virtual pressure and a bounded, distance-based cubic nib tail.</summary>
internal sealed class SimulatedPressureTaper : TaperProfile
{
    private readonly TaperKind _kind;
    private readonly float[] _widths = new float[64];
    private readonly float[] _distances = new float[64];
    private float _distance, _headDistance, _speed, _ratio;
    private bool _hasSpeed;
    private int _stable;

    public SimulatedPressureTaper(TaperKind kind) => _kind = kind;
    public override TaperKind Kind => _kind;
    public override int StablePointCount(int count) => _kind is TaperKind.Natural or TaperKind.Calligraphy or TaperKind.InkWash ? _stable : count;

    public override void Apply(Span<InkPoint> points, int tipIndex, float baseWidth)
    {
        ref var current = ref points[tipIndex];
        float elapsed = 0, distance = 0;
        if (tipIndex > 0)
        {
            distance = Vector2.Distance(current.Position, points[tipIndex - 1].Position);
            elapsed = current.TimeMs - points[tipIndex - 1].TimeMs;
            _distance += distance;
        }

        if (elapsed > 0 && elapsed <= 200 && float.IsFinite(elapsed))
        {
            float speed = distance / elapsed;
            float weight = 1 - MathF.Exp(-elapsed / 35);
            _speed = _hasSpeed ? _speed + weight * (speed - _speed) : speed;
            _hasSpeed = true;
        }

        float speedUnit = _hasSpeed ? Math.Clamp((_speed - .03f) / (1.8f + baseWidth * .04f), 0, 1) : .35f;
        float pressure = 1 - SmoothStep(speedUnit);
        float ratio = _kind switch
        {
            TaperKind.SmoothPen => .82f + .18f * pressure,
            TaperKind.SoftPen => .42f + .66f * MathF.Pow(pressure, .85f),
            TaperKind.Calligraphy => .24f + 1.05f * MathF.Pow(pressure, 1.25f),
            TaperKind.InkWash => .45f + .75f * MathF.Pow(pressure, .85f),
            _ => .38f + .72f * pressure,
        };
        if (_kind is TaperKind.Calligraphy or TaperKind.InkWash && tipIndex > 1)
        {
            var a = points[tipIndex - 1].Position - points[tipIndex - 2].Position;
            var b = current.Position - points[tipIndex - 1].Position;
            if (a.LengthSquared() > .01f && b.LengthSquared() > .01f)
            {
                float turn = (1 - Vector2.Dot(Vector2.Normalize(a), Vector2.Normalize(b))) * .5f;
                ratio *= 1 + .18f * turn;
                // Water-ink direction and side-tip width are applied once by its nib geometry.
            }
        }
        float response = elapsed > 0 && elapsed <= 200 ? 1 - MathF.Exp(-elapsed / 22) : 1;
        _ratio = tipIndex == 0 ? ratio : _ratio + response * (ratio - _ratio);

        if (_kind is TaperKind.SmoothPen or TaperKind.SoftPen)
        {
            Settle(ref current.Width, PressureWidth(current, baseWidth) * _ratio);
            return;
        }

        float tipRatio = _kind is TaperKind.Calligraphy or TaperKind.InkWash ? .09f : .18f;
        float headLength = _kind == TaperKind.InkWash ? 6 : MathF.Max(5, baseWidth * 1.6f);
        // Pausing or jittering inside the starting contact must not inflate the head.
        // Grow it from the actual distance away from the first point.
        if (_headDistance < headLength)
            _headDistance = MathF.Max(_headDistance, Vector2.Distance(current.Position, points[0].Position));
        float head = tipRatio + (1 - tipRatio) * SmoothStep(Math.Clamp(_headDistance / headLength, 0, 1));
        int slot = tipIndex % _widths.Length;
        _widths[slot] = PressureWidth(current, baseWidth) * _ratio * head;
        _distances[slot] = _distance;
        // Keep water-ink transitions in writing distance. Scaling them with a wide brush
        // leaves an entire short character stroke inside the thin entry and exit regions.
        float tailLength = _kind == TaperKind.InkWash ? 12 : MathF.Max(10, baseWidth * (_kind == TaperKind.Calligraphy ? 4 : 2.5f));
        int first = Math.Max(_stable, tipIndex - _widths.Length + 1);
        for (int index = first; index <= tipIndex; index++)
        {
            float progress = Math.Clamp((_distance - _distances[index % _widths.Length]) / tailLength, 0, 1);
            float cubic = 1 - MathF.Pow(1 - progress, 3);
            Settle(ref points[index].Width, _widths[index % _widths.Length] * (tipRatio + (1 - tipRatio) * cubic));
            if (progress >= 1) _stable = index + 1;
        }
        _stable = Math.Max(_stable, tipIndex - _widths.Length + 1);
    }

    private static float SmoothStep(float value) => value * value * (3 - 2 * value);
}
