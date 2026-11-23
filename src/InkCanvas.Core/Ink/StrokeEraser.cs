using System.Numerics;

namespace InkCanvas.Core.Ink;

/// <summary>
/// WPF-style EraseByPoint: cuts only the portion of a centerline touched by the eraser disc,
/// returning the remaining stroke fragments. This is intentionally separate from the existing
/// whole-stroke <see cref="InkDocument.EraseAt"/> operation.
/// </summary>
public static class StrokeEraser
{
    public static bool EraseDisc(InkStroke stroke, Vector2 center, float radius, out List<InkStroke> remaining)
    {
        remaining = new List<InkStroke>();
        if (stroke is null || stroke.Count == 0 || radius <= 0) return false;
        if (!stroke.Bounds.Inflate(radius).Contains(center))
        {
            remaining.Add(stroke);
            return false;
        }

        var points = stroke.Points;
        if (points.Count == 1)
        {
            if (Vector2.Distance(points[0].Position, center) <= radius + points[0].Width * 0.5f)
                return true;
            remaining.Add(stroke);
            return false;
        }

        float maxHalfWidth = points.Max(p => p.Width) * 0.5f;
        float cutRadius = radius + maxHalfWidth;
        float cutRadiusSquared = cutRadius * cutRadius;
        bool touched = false;
        var fragments = new List<List<InkPoint>>();
        var current = new List<InkPoint>();
        Span<float> cuts = stackalloc float[4];

        for (int segment = 1; segment < points.Count; segment++)
        {
            InkPoint a = points[segment - 1], b = points[segment];
            Vector2 p0 = a.Position, d = b.Position - p0, f = p0 - center;
            float aa = Vector2.Dot(d, d);
            cuts[0] = 0f;
            cuts[1] = 1f;
            int cutCount = 2;

            if (aa > 1e-8f)
            {
                float bb = 2f * Vector2.Dot(f, d);
                float cc = Vector2.Dot(f, f) - cutRadiusSquared;
                float discriminant = bb * bb - 4f * aa * cc;
                if (discriminant >= 0)
                {
                    float root = MathF.Sqrt(discriminant);
                    float t1 = (-bb - root) / (2f * aa);
                    float t2 = (-bb + root) / (2f * aa);
                    if (t1 > 0f && t1 < 1f) cuts[cutCount++] = t1;
                    if (t2 > 0f && t2 < 1f) cuts[cutCount++] = t2;
                }
            }
            cuts[..cutCount].Sort();

            for (int i = 1; i < cutCount; i++)
            {
                float t0 = cuts[i - 1], t1 = cuts[i];
                float tm = (t0 + t1) * 0.5f;
                Vector2 mid = Vector2.Lerp(p0, b.Position, tm);
                if (Vector2.DistanceSquared(mid, center) <= cutRadiusSquared)
                {
                    touched = true;
                    Flush(ref current, fragments);
                    continue;
                }

                AppendDistinct(current, Interpolate(a, b, t0));
                AppendDistinct(current, Interpolate(a, b, t1));
            }
        }
        Flush(ref current, fragments);

        if (!touched)
        {
            remaining.Add(stroke);
            return false;
        }

        foreach (var fragment in fragments)
        {
            if (fragment.Count == 0) continue;
            var copy = new InkStroke(stroke.Attributes.Clone())
            {
                LayerOrder = stroke.LayerOrder,
                IsRecognizedShape = stroke.IsRecognizedShape,
                ShapeSource = stroke.ShapeSource,
            };
            copy.RestorePoints(fragment);
            copy.Complete();
            if (stroke.ShapeSource is not null) copy.ObjectFrame = stroke.ObjectFrame;
            remaining.Add(copy);
        }
        return true;
    }

    private static InkPoint Interpolate(in InkPoint a, in InkPoint b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Pressure + (b.Pressure - a.Pressure) * t,
        a.Width + (b.Width - a.Width) * t,
        a.TimeMs + (b.TimeMs - a.TimeMs) * t,
        a.HasPressure || b.HasPressure,
        a.TiltX + (b.TiltX - a.TiltX) * t,
        a.TiltY + (b.TiltY - a.TiltY) * t);

    private static void AppendDistinct(List<InkPoint> points, InkPoint point)
    {
        if (points.Count == 0 || Vector2.DistanceSquared(points[^1].Position, point.Position) > 1e-4f)
            points.Add(point);
    }

    private static void Flush(ref List<InkPoint> current, List<List<InkPoint>> fragments)
    {
        if (current.Count > 0)
        {
            fragments.Add(current);
            // Transfer ownership: clearing this same list also emptied the saved fragment.
            current = new List<InkPoint>();
        }
    }
}
