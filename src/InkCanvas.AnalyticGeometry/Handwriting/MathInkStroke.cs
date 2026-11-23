namespace InkCanvas.AnalyticGeometry.Handwriting;

public readonly record struct InkSample(double X, double Y);

public readonly record struct InkBounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    public double CenterX => (Left + Right) * 0.5;
    public double CenterY => (Top + Bottom) * 0.5;
    public double Area => Width * Height;

    public static InkBounds Empty => new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => Left > Right || Top > Bottom;

    public InkBounds Union(InkBounds other) => IsEmpty
        ? other
        : other.IsEmpty
            ? this
            : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public InkBounds Inflate(double dx, double dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public bool Intersects(InkBounds other) => !IsEmpty && !other.IsEmpty
        && Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;

    public bool Contains(double x, double y) => !IsEmpty && x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>A single handwritten ink stroke with precomputed geometric summaries.</summary>
public sealed class MathInkStroke
{
    public int Id { get; }
    public IReadOnlyList<InkSample> Points { get; }
    public InkBounds Bounds { get; }
    public double PathLength { get; }
    public InkSample Start => Points[0];
    public InkSample End => Points[^1];
    public bool IsDot => Bounds.Width <= 10 && Bounds.Height <= 10 && PathLength <= 22;
    public bool IsHorizontalLine => !IsDot && Bounds.Width >= 14 && Bounds.Height <= Math.Max(14, Bounds.Width * 0.28)
        && Math.Abs(End.X - Start.X) >= Bounds.Width * 0.65;
    public bool IsVerticalLine => !IsDot && Bounds.Height >= 16 && Bounds.Width <= Math.Max(14, Bounds.Height * 0.32)
        && Math.Abs(End.Y - Start.Y) >= Bounds.Height * 0.65 && !HasHorizontalBow();

    private bool HasHorizontalBow()
    {
        if (Points.Count < 3 || Bounds.Width < 5) return false;
        double chordMidX = (Start.X + End.X) * 0.5;
        double maxDev = 0;
        for (int i = 1; i < Points.Count - 1; i++)
        {
            double dev = Math.Abs(Points[i].X - chordMidX);
            if (dev > maxDev) maxDev = dev;
        }
        return maxDev >= Bounds.Width * 0.42;
    }

    public MathInkStroke(int id, IReadOnlyList<InkSample> points)
    {
        if (points is null || points.Count == 0) throw new ArgumentException("Stroke must contain at least one point.", nameof(points));
        Id = id;
        Points = points;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        double length = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
            if (i > 0)
            {
                double dx = p.X - points[i - 1].X;
                double dy = p.Y - points[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        Bounds = new InkBounds(minX, minY, maxX, maxY);
        PathLength = length;
    }

    public static bool SegmentsIntersect(MathInkStroke a, MathInkStroke b, double tolerance = 4)
    {
        if (!a.Bounds.Inflate(tolerance, tolerance).Intersects(b.Bounds)) return false;
        double tolSq = tolerance * tolerance;
        int stepA = Math.Max(1, (a.Points.Count - 1) / 24);
        int stepB = Math.Max(1, (b.Points.Count - 1) / 24);
        if (a.Points.Count == 1 && b.Points.Count == 1)
            return DistSq(a.Points[0], b.Points[0]) <= tolSq;

        for (int i = 0; i < Math.Max(1, a.Points.Count - 1); i += stepA)
        {
            var a1 = a.Points[i];
            var a2 = a.Points[Math.Min(a.Points.Count - 1, i + stepA)];
            for (int j = 0; j < Math.Max(1, b.Points.Count - 1); j += stepB)
            {
                var b1 = b.Points[j];
                var b2 = b.Points[Math.Min(b.Points.Count - 1, j + stepB)];
                if (SegmentToSegmentDistanceSq(a1, a2, b1, b2) <= tolSq) return true;
            }
        }
        return false;
    }

    private static double SegmentToSegmentDistanceSq(InkSample p1, InkSample p2, InkSample q1, InkSample q2)
    {
        if (ProperIntersect(p1, p2, q1, q2)) return 0;
        double d1 = PointToSegmentDistSq(p1, q1, q2);
        double d2 = PointToSegmentDistSq(p2, q1, q2);
        double d3 = PointToSegmentDistSq(q1, p1, p2);
        double d4 = PointToSegmentDistSq(q2, p1, p2);
        return Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
    }

    private static bool ProperIntersect(InkSample a, InkSample b, InkSample c, InkSample d)
    {
        static double Cross(InkSample o, InkSample u, InkSample v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        double d1 = Cross(a, b, c), d2 = Cross(a, b, d);
        double d3 = Cross(c, d, a), d4 = Cross(c, d, b);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double PointToSegmentDistSq(InkSample p, InkSample a, InkSample b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double lenSq = vx * vx + vy * vy;
        if (lenSq < 1e-9) return DistSq(p, a);
        double t = Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / lenSq, 0, 1);
        double px = a.X + t * vx, py = a.Y + t * vy;
        double dx = p.X - px, dy = p.Y - py;
        return dx * dx + dy * dy;
    }

    private static double DistSq(InkSample a, InkSample b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

/// <summary>A recognized symbol group with bounding box and top-K alternatives for Select &amp; Correct.</summary>
public sealed record RecognizedSymbol(
    string Symbol,
    double Confidence,
    InkBounds Bounds,
    IReadOnlyList<int> StrokeIds,
    IReadOnlyList<string> Alternatives);

/// <summary>Full 2D mathematical handwriting recognition result.</summary>
public sealed record MathInkRecognitionResult(
    string Expression,
    string MathML,
    IReadOnlyList<RecognizedSymbol> Symbols,
    IReadOnlyList<string> Candidates);
