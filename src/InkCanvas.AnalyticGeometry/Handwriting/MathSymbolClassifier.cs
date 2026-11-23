namespace InkCanvas.AnalyticGeometry.Handwriting;

/// <summary>
/// Built-in offline mathematical and English letter recognizer combining direction-aware
/// normalized point-cloud matching ($P+ style), stroke-order invariance, and geometric/topological
/// invariants (aspect ratio, closure, stroke geometry) across digits, all 52 English letters
/// (a-z, A-Z with print &amp; cursive variants), Greek letters, and math operators.
/// </summary>
public static class MathSymbolClassifier
{
    private const int SampleCount = 24;

    private readonly record struct FeaturePoint(double X, double Y, double DirX, double DirY);

    private sealed record SymbolTemplate(
        string Symbol,
        int StrokeCount,
        FeaturePoint[] Points,
        double PreferredAspect,
        double AspectWeight,
        double ClosureRatio,
        bool CheckClosure);

    private static readonly SymbolTemplate[] Templates = BuildTemplates();

    public static IReadOnlyList<(string Symbol, double Score)> Classify(IReadOnlyList<MathInkStroke> strokes)
    {
        if (strokes.Count == 0) return Array.Empty<(string, double)>();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        void Vote(string symbol, double score)
        {
            if (!scores.TryGetValue(symbol, out double existing) || score > existing) scores[symbol] = score;
        }

        var bounds = InkBounds.Empty;
        foreach (var s in strokes) bounds = bounds.Union(s.Bounds);
        double aspect = bounds.Width / Math.Max(1, bounds.Height);

        if (strokes.Count == 1)
        {
            var s = strokes[0];
            if (s.IsDot)
            {
                Vote(".", 0.96); Vote(",", 0.62); Vote("·", 0.58);
                return Rank(scores);
            }
            if (s.IsHorizontalLine)
            {
                Vote("-", 0.96); Vote("1", 0.32);
                return Rank(scores);
            }
            if (s.IsVerticalLine)
            {
                Vote("1", 0.88); Vote("l", 0.84); Vote("I", 0.80); Vote("|", 0.65); Vote("i", 0.52);
                return Rank(scores);
            }
            if (IsParenthesisShape(s, out string paren))
            {
                Vote(paren, 0.94); Vote("c", 0.45);
            }
            if (IsRadicalShape(s))
            {
                Vote("√", 0.95);
            }
        }
        else if (strokes.Count == 2)
        {
            var a = strokes[0];
            var b = strokes[1];
            if (a.IsHorizontalLine && b.IsHorizontalLine)
            {
                double verticalGap = Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY);
                double width = Math.Max(a.Bounds.Width, b.Bounds.Width);
                if (verticalGap <= Math.Max(26, width * 0.9))
                {
                    Vote("=", 0.96); Vote("≠", 0.40);
                    return Rank(scores);
                }
            }
            if ((a.IsDot && b.Bounds.Height >= 14 && a.Bounds.CenterY < b.Bounds.Top + 8)
                || (b.IsDot && a.Bounds.Height >= 14 && b.Bounds.CenterY < a.Bounds.Top + 8))
            {
                var stem = a.IsDot ? b : a;
                if (stem.IsVerticalLine || stem.Bounds.Width < stem.Bounds.Height * 0.42)
                {
                    Vote("i", 0.95); Vote("j", 0.68); Vote("1", 0.40);
                    return Rank(scores);
                }
                if (stem.End.X < stem.Start.X - stem.Bounds.Width * 0.25 && stem.End.Y > stem.Bounds.CenterY)
                {
                    Vote("j", 0.95); Vote("i", 0.68);
                    return Rank(scores);
                }
            }
            if ((a.IsHorizontalLine && b.IsVerticalLine) || (b.IsHorizontalLine && a.IsVerticalLine))
            {
                var h = a.IsHorizontalLine ? a : b;
                var v = a.IsVerticalLine ? a : b;
                double relY = (h.Bounds.CenterY - v.Bounds.Top) / Math.Max(1, v.Bounds.Height);
                double relX = (v.Bounds.CenterX - h.Bounds.Left) / Math.Max(1, h.Bounds.Width);
                if (relY <= 0.20 && relX is >= 0.25 and <= 0.75)
                {
                    Vote("T", 0.95); Vote("t", 0.76); Vote("+", 0.58);
                }
                else if (relY >= 0.80 && relX <= 0.35)
                {
                    Vote("L", 0.94); Vote("t", 0.52);
                }
                else if (relY is >= 0.35 and <= 0.68 && relX is >= 0.28 and <= 0.72)
                {
                    Vote("+", 0.94); Vote("t", 0.80); Vote("T", 0.62); Vote("x", 0.52);
                }
                else if (relY < 0.38)
                {
                    Vote("t", 0.91); Vote("T", 0.82); Vote("7", 0.70); Vote("+", 0.66);
                }
                else
                {
                    Vote("+", 0.84); Vote("t", 0.68);
                }
            }
            if (IsYTwoStroke(a, b))
            {
                Vote("y", 0.95); Vote("Y", 0.82); Vote("x", 0.58); Vote("4", 0.45);
            }
            else if (IsCrossShape(a, b))
            {
                Vote("x", 0.93); Vote("X", 0.86); Vote("×", 0.84); Vote("+", 0.62);
            }
            if (IsFourTwoStroke(a, b))
            {
                Vote("4", 0.91); Vote("+", 0.55); Vote("t", 0.45);
            }
            if (IsFOrTWithHook(a, b, out string hookLetter))
            {
                Vote(hookLetter, 0.92);
                Vote(hookLetter == "f" ? "t" : "f", 0.72);
            }
        }
        else if (strokes.Count == 3)
        {
            if (IsDivisionShape(strokes)) { Vote("÷", 0.95); Vote("=", 0.42); return Rank(scores); }
            if (IsNotEqualShape(strokes)) { Vote("≠", 0.93); Vote("=", 0.50); return Rank(scores); }
            if (IsHShape(strokes)) { Vote("H", 0.95); Vote("A", 0.52); Vote("π", 0.48); }
            if (IsAShape(strokes)) { Vote("A", 0.95); Vote("H", 0.52); }
            if (IsFThreeStroke(strokes)) { Vote("F", 0.94); Vote("E", 0.65); Vote("T", 0.52); }
            if (IsISerifShape(strokes)) { Vote("I", 0.93); Vote("H", 0.55); }
            if (IsPiShape(strokes)) { Vote("π", 0.93); Vote("H", 0.58); Vote("n", 0.45); }
        }
        else if (strokes.Count == 4)
        {
            if (IsEFourStroke(strokes)) { Vote("E", 0.95); Vote("F", 0.65); return Rank(scores); }
        }

        // General direction-aware point-cloud template matching with stroke-order invariance.
        var variants = BuildStrokeOrderVariants(strokes);
        double closure = strokes.Count == 1 ? ComputeClosureRatio(strokes[0]) : 1.0;

        foreach (var template in Templates)
        {
            if (template.StrokeCount != strokes.Count) continue;
            double bestDistance = double.PositiveInfinity;
            foreach (var variant in variants)
            {
                double d = CloudDistance(variant, template.Points);
                if (d < bestDistance) bestDistance = d;
            }
            double aspectPenalty = template.AspectWeight * Math.Abs(Math.Log((aspect + 0.15) / (template.PreferredAspect + 0.15)));
            double closurePenalty = template.CheckClosure && strokes.Count == 1
                ? 0.45 * Math.Abs(closure - template.ClosureRatio)
                : 0.0;
            double score = Math.Exp(-1.45 * bestDistance - 0.32 * aspectPenalty - closurePenalty);
            Vote(template.Symbol, Math.Clamp(score, 0.05, 0.96));
        }

        if (scores.Count == 0) Vote("x", 0.3);
        return Rank(scores);
    }

    private static List<FeaturePoint[]> BuildStrokeOrderVariants(IReadOnlyList<MathInkStroke> strokes)
    {
        var result = new List<FeaturePoint[]>(4) { NormalizeCloud(strokes) };
        if (strokes.Count == 1)
        {
            var rev = new MathInkStroke(strokes[0].Id, strokes[0].Points.Reverse().ToArray());
            result.Add(NormalizeCloud(new[] { rev }));
        }
        else if (strokes.Count == 2)
        {
            result.Add(NormalizeCloud(new[] { strokes[1], strokes[0] }));
        }
        else if (strokes.Count == 3)
        {
            result.Add(NormalizeCloud(new[] { strokes[0], strokes[2], strokes[1] }));
            result.Add(NormalizeCloud(new[] { strokes[1], strokes[0], strokes[2] }));
            result.Add(NormalizeCloud(new[] { strokes[2], strokes[1], strokes[0] }));
        }
        return result;
    }

    private static double ComputeClosureRatio(MathInkStroke s)
    {
        double diag = Math.Sqrt(s.Bounds.Width * s.Bounds.Width + s.Bounds.Height * s.Bounds.Height);
        if (diag < 1e-4) return 0;
        double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
        return Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / diag, 0, 1.2);
    }

    private static IReadOnlyList<(string Symbol, double Score)> Rank(Dictionary<string, double> scores)
        => scores.OrderByDescending(pair => pair.Value).Take(8).Select(pair => (pair.Key, pair.Value)).ToArray();

    private static bool IsParenthesisShape(MathInkStroke s, out string paren)
    {
        paren = null;
        if (s.Points.Count < 3 || s.Bounds.Height < 18 || s.Bounds.Width < 5 || s.Bounds.Width > s.Bounds.Height * 0.52)
            return false;
        if (Math.Abs(s.End.Y - s.Start.Y) < s.Bounds.Height * 0.72) return false;
        double chordMidX = (s.Start.X + s.End.X) * 0.5;
        double signedDev = 0;
        for (int i = 1; i < s.Points.Count - 1; i++)
        {
            double d = s.Points[i].X - chordMidX;
            if (Math.Abs(d) > Math.Abs(signedDev)) signedDev = d;
        }
        if (Math.Abs(signedDev) < s.Bounds.Width * 0.45) return false;
        paren = signedDev < 0 ? "(" : ")";
        return true;
    }

    private static bool IsRadicalShape(MathInkStroke s)
    {
        if (s.Points.Count < 4 || s.Bounds.Width < 18 || s.Bounds.Height < 14) return false;
        int valleyIndex = 0;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < s.Points.Count; i++)
        {
            if (s.Points[i].Y > maxY) { maxY = s.Points[i].Y; valleyIndex = i; }
        }
        if (valleyIndex == 0 || valleyIndex >= s.Points.Count - 2) return false;
        int peakIndex = valleyIndex;
        double minY = double.PositiveInfinity;
        for (int i = valleyIndex; i < s.Points.Count; i++)
        {
            if (s.Points[i].Y < minY) { minY = s.Points[i].Y; peakIndex = i; }
        }
        if (peakIndex <= valleyIndex) return false;
        double startRelX = (s.Start.X - s.Bounds.Left) / Math.Max(1, s.Bounds.Width);
        double startRelY = (s.Start.Y - s.Bounds.Top) / Math.Max(1, s.Bounds.Height);
        double valleyRelX = (s.Points[valleyIndex].X - s.Bounds.Left) / Math.Max(1, s.Bounds.Width);
        double endRelX = (s.End.X - s.Bounds.Left) / Math.Max(1, s.Bounds.Width);
        double endRelY = (s.End.Y - s.Bounds.Top) / Math.Max(1, s.Bounds.Height);
        double roofLength = s.End.X - s.Points[peakIndex].X;
        // '√' starts mid-left (unlike 'r' which starts near the top-left) and has a wide top roof.
        return startRelX < 0.35 && startRelY >= 0.32 && valleyRelX < 0.55 && endRelX > 0.78 && endRelY < 0.32
            && roofLength > s.Bounds.Width * 0.40 && s.Bounds.Width >= s.Bounds.Height * 0.85;
    }

    private static bool IsCrossShape(MathInkStroke a, MathInkStroke b)
    {
        if (!MathInkStroke.SegmentsIntersect(a, b, 10)) return false;
        double diagA = DiagonalRatio(a);
        double diagB = DiagonalRatio(b);
        if (diagA < 0.35 || diagB < 0.35) return false;
        double slopeA = (a.End.Y - a.Start.Y) * (a.End.X - a.Start.X);
        double slopeB = (b.End.Y - b.Start.Y) * (b.End.X - b.Start.X);
        return slopeA * slopeB < 0;
    }

    private static bool IsYTwoStroke(MathInkStroke a, MathInkStroke b)
    {
        var longer = a.Bounds.Height >= b.Bounds.Height ? a : b;
        var shorter = ReferenceEquals(longer, a) ? b : a;
        if (longer.Bounds.Height < shorter.Bounds.Height * 1.18) return false;
        if (shorter.Bounds.Bottom > longer.Bounds.Bottom - longer.Bounds.Height * 0.15) return false;
        double slopeLong = (longer.End.X - longer.Start.X) * (longer.End.Y - longer.Start.Y);
        double slopeShort = (shorter.End.X - shorter.Start.X) * (shorter.End.Y - shorter.Start.Y);
        return slopeLong < 0 && slopeShort > 0 && MathInkStroke.SegmentsIntersect(a, b, 12);
    }

    private static bool IsFourTwoStroke(MathInkStroke a, MathInkStroke b)
    {
        var vertical = a.IsVerticalLine ? a : b.IsVerticalLine ? b : null;
        if (vertical is null) return false;
        var other = ReferenceEquals(vertical, a) ? b : a;
        if (other.IsHorizontalLine || !MathInkStroke.SegmentsIntersect(a, b, 12)) return false;
        // '4': L-stroke starts in upper region and its horizontal foot sits above the bottom of the vertical stem.
        // 'd': belly sits in the lower half and reaches the bottom of the stem.
        bool startsUpper = other.Bounds.Top <= vertical.Bounds.Top + vertical.Bounds.Height * 0.30;
        bool footAboveBottom = other.Bounds.Bottom <= vertical.Bounds.Bottom - vertical.Bounds.Height * 0.14;
        bool startsHigh = other.Start.Y < other.Bounds.CenterY;
        bool endsRight = other.End.X > other.Bounds.CenterX;
        return startsUpper && footAboveBottom && startsHigh && endsRight;
    }

    private static bool IsFOrTWithHook(MathInkStroke a, MathInkStroke b, out string letter)
    {
        letter = null;
        var bar = a.IsHorizontalLine ? a : b.IsHorizontalLine ? b : null;
        if (bar is null) return false;
        var stem = ReferenceEquals(bar, a) ? b : a;
        if (stem.IsHorizontalLine || stem.IsVerticalLine || stem.Bounds.Height < stem.Bounds.Width * 1.1) return false;
        if (!MathInkStroke.SegmentsIntersect(bar, stem, 12)) return false;
        bool topHookRight = stem.Start.X > stem.Bounds.CenterX && stem.Start.Y < stem.Bounds.Top + stem.Bounds.Height * 0.30;
        bool bottomHookRight = stem.End.X > stem.Bounds.CenterX && stem.End.Y > stem.Bounds.Bottom - stem.Bounds.Height * 0.30;
        if (topHookRight && !bottomHookRight) { letter = "f"; return true; }
        if (bottomHookRight && !topHookRight) { letter = "t"; return true; }
        return false;
    }

    private static bool IsHShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bar = strokes.FirstOrDefault(s => s.IsHorizontalLine);
        if (bar is null) return false;
        var legs = strokes.Where(s => !ReferenceEquals(s, bar)).ToArray();
        if (legs.Length != 2 || !legs.All(l => l.IsVerticalLine)) return false;
        double topDist = Math.Abs(legs[0].Start.X - legs[1].Start.X);
        double botDist = Math.Abs(legs[0].End.X - legs[1].End.X);
        if (botDist > topDist * 1.45 + 6) return false; // converging top legs belong to 'A'
        double top = Math.Min(legs[0].Bounds.Top, legs[1].Bounds.Top);
        double bottom = Math.Max(legs[0].Bounds.Bottom, legs[1].Bounds.Bottom);
        double relY = (bar.Bounds.CenterY - top) / Math.Max(1, bottom - top);
        return relY is >= 0.28 and <= 0.72;
    }

    private static bool IsAShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bar = strokes.FirstOrDefault(s => s.IsHorizontalLine);
        if (bar is null) return false;
        var legs = strokes.Where(s => !ReferenceEquals(s, bar)).ToArray();
        if (legs.Length != 2) return false;
        double topDist = Math.Abs(legs[0].Start.X - legs[1].Start.X);
        double botDist = Math.Abs(legs[0].End.X - legs[1].End.X);
        double relY = (bar.Bounds.CenterY - Math.Min(legs[0].Bounds.Top, legs[1].Bounds.Top))
            / Math.Max(1, Math.Max(legs[0].Bounds.Bottom, legs[1].Bounds.Bottom) - Math.Min(legs[0].Bounds.Top, legs[1].Bounds.Top));
        return botDist > topDist * 1.4 + 6 && relY is >= 0.35 and <= 0.80;
    }

    private static bool IsFThreeStroke(IReadOnlyList<MathInkStroke> strokes)
    {
        var bars = strokes.Where(s => s.IsHorizontalLine).OrderBy(s => s.Bounds.CenterY).ToArray();
        if (bars.Length != 2) return false;
        var stem = strokes.First(s => !bars.Contains(s));
        if (!stem.IsVerticalLine && stem.Bounds.Height < stem.Bounds.Width * 1.5) return false;
        double relTop = (bars[0].Bounds.CenterY - stem.Bounds.Top) / Math.Max(1, stem.Bounds.Height);
        double relMid = (bars[1].Bounds.CenterY - stem.Bounds.Top) / Math.Max(1, stem.Bounds.Height);
        return relTop < 0.25 && relMid is >= 0.30 and <= 0.72 && bars[0].Bounds.Right > stem.Bounds.CenterX + 4;
    }

    private static bool IsISerifShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bars = strokes.Where(s => s.IsHorizontalLine).OrderBy(s => s.Bounds.CenterY).ToArray();
        if (bars.Length != 2) return false;
        var stem = strokes.First(s => !bars.Contains(s));
        if (!stem.IsVerticalLine) return false;
        double relTop = (bars[0].Bounds.CenterY - stem.Bounds.Top) / Math.Max(1, stem.Bounds.Height);
        double relBot = (bars[1].Bounds.CenterY - stem.Bounds.Top) / Math.Max(1, stem.Bounds.Height);
        return relTop < 0.22 && relBot > 0.78;
    }

    private static bool IsEFourStroke(IReadOnlyList<MathInkStroke> strokes)
    {
        var bars = strokes.Where(s => s.IsHorizontalLine).ToArray();
        if (bars.Length != 3) return false;
        var stem = strokes.First(s => !bars.Contains(s));
        return stem.IsVerticalLine || stem.Bounds.Height > stem.Bounds.Width * 1.5;
    }

    private static bool IsDivisionShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bar = strokes.FirstOrDefault(s => s.IsHorizontalLine);
        if (bar is null) return false;
        var dots = strokes.Where(s => !ReferenceEquals(s, bar)).ToArray();
        return dots.Length == 2 && dots.All(d => d.IsDot || (d.Bounds.Width < bar.Bounds.Width * 0.45 && d.Bounds.Height < bar.Bounds.Width * 0.45))
            && ((dots[0].Bounds.CenterY < bar.Bounds.Top && dots[1].Bounds.CenterY > bar.Bounds.Bottom)
                || (dots[1].Bounds.CenterY < bar.Bounds.Top && dots[0].Bounds.CenterY > bar.Bounds.Bottom));
    }

    private static bool IsPiShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bar = strokes.FirstOrDefault(s => s.IsHorizontalLine);
        if (bar is null) return false;
        var legs = strokes.Where(s => !ReferenceEquals(s, bar)).ToArray();
        return legs.Length == 2 && legs.All(l => l.Bounds.Height > l.Bounds.Width * 0.9 && l.Bounds.CenterY > bar.Bounds.CenterY)
            && bar.Bounds.CenterY < Math.Min(legs[0].Bounds.Top, legs[1].Bounds.Top) + Math.Max(10, bar.Bounds.Width * 0.25);
    }

    private static bool IsNotEqualShape(IReadOnlyList<MathInkStroke> strokes)
    {
        var bars = strokes.Where(s => s.IsHorizontalLine).ToArray();
        if (bars.Length != 2) return false;
        var slash = strokes.First(s => !bars.Contains(s));
        return !slash.IsVerticalLine && (MathInkStroke.SegmentsIntersect(slash, bars[0], 10) || MathInkStroke.SegmentsIntersect(slash, bars[1], 10));
    }

    private static double DiagonalRatio(MathInkStroke s)
    {
        double dx = Math.Abs(s.End.X - s.Start.X);
        double dy = Math.Abs(s.End.Y - s.Start.Y);
        double max = Math.Max(dx, dy);
        return max < 1 ? 0 : Math.Min(dx, dy) / max;
    }

    private static FeaturePoint[] NormalizeCloud(IReadOnlyList<MathInkStroke> strokes)
    {
        var raw = new List<(InkSample Pos, double Dx, double Dy)>(SampleCount);
        double totalLength = strokes.Sum(s => Math.Max(1e-3, s.PathLength));
        int remaining = SampleCount;
        for (int i = 0; i < strokes.Count; i++)
        {
            int count = i == strokes.Count - 1 ? remaining : Math.Max(2, (int)Math.Round(SampleCount * Math.Max(1e-3, strokes[i].PathLength) / totalLength));
            count = Math.Min(count, remaining);
            remaining -= count;
            var pts = Resample(strokes[i].Points, count);
            for (int k = 0; k < pts.Length; k++)
            {
                var prev = pts[Math.Max(0, k - 1)];
                var next = pts[Math.Min(pts.Length - 1, k + 1)];
                double dx = next.X - prev.X, dy = next.Y - prev.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                raw.Add((pts[k], len > 1e-6 ? dx / len : 0, len > 1e-6 ? dy / len : 0));
            }
        }
        return NormalizePoints(raw);
    }

    private static InkSample[] Resample(IReadOnlyList<InkSample> points, int n)
    {
        if (n <= 1 || points.Count == 1) return Enumerable.Repeat(points[0], Math.Max(1, n)).ToArray();
        double total = 0;
        for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        if (total < 1e-6) return Enumerable.Repeat(points[0], n).ToArray();
        double interval = total / (n - 1);
        var result = new List<InkSample>(n) { points[0] };
        double accumulated = 0;
        var prev = points[0];
        for (int i = 1; i < points.Count && result.Count < n; i++)
        {
            var curr = points[i];
            double d = Distance(prev, curr);
            if (accumulated + d >= interval)
            {
                double t = (interval - accumulated) / Math.Max(1e-9, d);
                var q = new InkSample(prev.X + t * (curr.X - prev.X), prev.Y + t * (curr.Y - prev.Y));
                result.Add(q);
                prev = q;
                accumulated = 0;
                i--;
            }
            else
            {
                accumulated += d;
                prev = curr;
            }
        }
        while (result.Count < n) result.Add(points[^1]);
        return result.ToArray();
    }

    private static FeaturePoint[] NormalizePoints(List<(InkSample Pos, double Dx, double Dy)> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (p, _, _) in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double scale = Math.Max(1e-4, Math.Max(maxX - minX, maxY - minY));
        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        var output = new FeaturePoint[points.Count];
        for (int i = 0; i < points.Count; i++)
            output[i] = new FeaturePoint((points[i].Pos.X - cx) / scale, (points[i].Pos.Y - cy) / scale, points[i].Dx, points[i].Dy);
        return output;
    }

    private static double CloudDistance(FeaturePoint[] a, FeaturePoint[] b)
    {
        double d1 = GreedyMatch(a, b);
        double d2 = GreedyMatch(b, a);
        double ordered = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) ordered += FeatureDistance(a[i], b[i]);
        ordered /= Math.Max(1, n);
        return Math.Min((d1 + d2) * 0.5, ordered) * 0.65 + ordered * 0.35;
    }

    private static double GreedyMatch(FeaturePoint[] a, FeaturePoint[] b)
    {
        bool[] matched = new bool[b.Length];
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double best = double.PositiveInfinity;
            int bestIndex = -1;
            for (int j = 0; j < b.Length; j++)
            {
                if (matched[j]) continue;
                double d = FeatureDistance(a[i], b[j]);
                if (d < best) { best = d; bestIndex = j; }
            }
            if (bestIndex >= 0) matched[bestIndex] = true;
            sum += best;
        }
        return sum / Math.Max(1, a.Length);
    }

    private static double FeatureDistance(FeaturePoint a, FeaturePoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        double pos = Math.Sqrt(dx * dx + dy * dy);
        double dot = a.DirX * b.DirX + a.DirY * b.DirY;
        double dirPenalty = 0.14 * (1.0 - Math.Clamp(dot, -1.0, 1.0));
        return pos + dirPenalty;
    }

    private static double Distance(InkSample a, InkSample b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static SymbolTemplate[] BuildTemplates()
    {
        var list = new List<SymbolTemplate>();
        void Add(string symbol, double aspect, double aspectWeight, params (double X, double Y)[][] strokes)
            => AddWithClosure(symbol, aspect, aspectWeight, false, 0, strokes);

        void AddClosed(string symbol, double aspect, double aspectWeight, params (double X, double Y)[][] strokes)
            => AddWithClosure(symbol, aspect, aspectWeight, true, 0.12, strokes);

        void AddOpen(string symbol, double aspect, double aspectWeight, params (double X, double Y)[][] strokes)
            => AddWithClosure(symbol, aspect, aspectWeight, true, 0.72, strokes);

        void AddWithClosure(string symbol, double aspect, double aspectWeight, bool checkClosure, double closureRatio, params (double X, double Y)[][] strokes)
        {
            var inkStrokes = strokes.Select((pts, idx) => new MathInkStroke(idx, pts.Select(p => new InkSample(p.X, p.Y)).ToArray())).ToArray();
            list.Add(new SymbolTemplate(symbol, strokes.Length, NormalizeCloud(inkStrokes), aspect, aspectWeight, closureRatio, checkClosure));
        }

        // === Digits 0-9 ===
        AddClosed("0", 0.70, 0.45, new[] { (0.5, 0.0), (0.15, 0.18), (0.08, 0.5), (0.18, 0.85), (0.5, 1.0), (0.82, 0.85), (0.92, 0.5), (0.82, 0.18), (0.5, 0.0) });
        Add("1", 0.25, 0.65, new[] { (0.5, 0.0), (0.5, 1.0) });
        Add("1", 0.35, 0.55, new[] { (0.28, 0.22), (0.55, 0.0), (0.55, 1.0) });
        Add("2", 0.75, 0.38, new[] { (0.15, 0.25), (0.35, 0.05), (0.70, 0.05), (0.85, 0.25), (0.65, 0.55), (0.10, 0.95), (0.90, 0.95) });
        Add("3", 0.70, 0.38, new[] { (0.15, 0.15), (0.55, 0.02), (0.85, 0.22), (0.45, 0.48), (0.88, 0.72), (0.55, 0.98), (0.15, 0.85) });
        Add("4", 0.75, 0.35, new[] { (0.65, 0.0), (0.10, 0.65), (0.95, 0.65) }, new[] { (0.68, 0.0), (0.68, 1.0) });
        Add("4", 0.75, 0.35, new[] { (0.35, 0.0), (0.15, 0.60), (0.95, 0.60) }, new[] { (0.70, 0.15), (0.70, 1.0) });
        Add("5", 0.75, 0.38, new[] { (0.85, 0.05), (0.20, 0.05), (0.15, 0.45), (0.60, 0.40), (0.88, 0.65), (0.60, 0.95), (0.15, 0.88) });
        Add("5", 0.75, 0.38, new[] { (0.20, 0.05), (0.15, 0.45), (0.60, 0.42), (0.88, 0.68), (0.55, 0.96), (0.15, 0.88) }, new[] { (0.20, 0.05), (0.88, 0.05) });
        Add("6", 0.70, 0.38, new[] { (0.75, 0.05), (0.35, 0.25), (0.12, 0.65), (0.30, 0.95), (0.75, 0.92), (0.85, 0.65), (0.55, 0.48), (0.15, 0.62) });
        Add("7", 0.72, 0.38, new[] { (0.10, 0.08), (0.90, 0.08), (0.35, 0.98) });
        Add("8", 0.68, 0.38, new[] { (0.50, 0.05), (0.18, 0.22), (0.50, 0.48), (0.82, 0.75), (0.50, 0.96), (0.18, 0.75), (0.50, 0.48), (0.82, 0.22), (0.50, 0.05) });
        Add("9", 0.70, 0.38, new[] { (0.82, 0.28), (0.55, 0.05), (0.18, 0.22), (0.22, 0.48), (0.62, 0.50), (0.82, 0.28), (0.75, 0.98) });

        // === Lowercase English Letters a-z ===
        Add("a", 0.85, 0.30, new[] { (0.78, 0.25), (0.45, 0.10), (0.12, 0.45), (0.25, 0.88), (0.70, 0.75), (0.82, 0.25), (0.85, 0.92) });
        Add("a", 0.85, 0.30, new[] { (0.75, 0.30), (0.45, 0.12), (0.14, 0.50), (0.42, 0.88), (0.75, 0.60) }, new[] { (0.78, 0.18), (0.80, 0.92) });
        Add("b", 0.62, 0.32, new[] { (0.20, 0.02), (0.20, 0.95), (0.65, 0.92), (0.88, 0.68), (0.62, 0.44), (0.20, 0.55) });
        Add("b", 0.62, 0.32, new[] { (0.20, 0.02), (0.20, 0.95) }, new[] { (0.20, 0.48), (0.68, 0.45), (0.88, 0.70), (0.62, 0.95), (0.20, 0.92) });
        AddOpen("c", 0.78, 0.35, new[] { (0.85, 0.20), (0.45, 0.08), (0.12, 0.50), (0.45, 0.92), (0.85, 0.82) });
        Add("d", 0.62, 0.32, new[] { (0.78, 0.48), (0.42, 0.42), (0.14, 0.68), (0.38, 0.95), (0.78, 0.80), (0.80, 0.02), (0.80, 0.96) });
        Add("d", 0.62, 0.32, new[] { (0.78, 0.02), (0.78, 0.96) }, new[] { (0.78, 0.46), (0.36, 0.44), (0.12, 0.70), (0.38, 0.95), (0.78, 0.90) });
        Add("e", 0.82, 0.30, new[] { (0.15, 0.52), (0.85, 0.48), (0.72, 0.15), (0.30, 0.15), (0.12, 0.52), (0.32, 0.88), (0.85, 0.82) });
        Add("f", 0.55, 0.35, new[] { (0.82, 0.12), (0.55, 0.04), (0.45, 0.35), (0.42, 0.96) }, new[] { (0.15, 0.45), (0.82, 0.45) });
        Add("g", 0.62, 0.32, new[] { (0.80, 0.22), (0.48, 0.08), (0.15, 0.28), (0.42, 0.52), (0.80, 0.35), (0.78, 0.85), (0.50, 0.98), (0.18, 0.88) });
        Add("h", 0.65, 0.32, new[] { (0.18, 0.04), (0.18, 0.96), (0.22, 0.52), (0.55, 0.38), (0.82, 0.55), (0.84, 0.96) });
        Add("h", 0.65, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.52), (0.55, 0.40), (0.82, 0.56), (0.84, 0.96) });
        Add("j", 0.45, 0.38, new[] { (0.62, 0.24), (0.60, 0.84), (0.36, 0.98), (0.14, 0.86) });
        Add("k", 0.65, 0.32, new[] { (0.20, 0.04), (0.20, 0.96) }, new[] { (0.84, 0.28), (0.22, 0.60), (0.86, 0.96) });
        Add("k", 0.65, 0.32, new[] { (0.20, 0.04), (0.20, 0.96) }, new[] { (0.82, 0.28), (0.24, 0.60) }, new[] { (0.34, 0.54), (0.86, 0.96) });
        Add("l", 0.26, 0.55, new[] { (0.45, 0.04), (0.45, 0.90), (0.72, 0.96) });
        Add("m", 1.25, 0.32, new[] { (0.08, 0.24), (0.08, 0.92), (0.12, 0.36), (0.32, 0.18), (0.50, 0.40), (0.50, 0.92), (0.54, 0.36), (0.74, 0.18), (0.92, 0.40), (0.92, 0.92) });
        Add("n", 0.85, 0.30, new[] { (0.15, 0.20), (0.15, 0.92), (0.20, 0.32), (0.52, 0.15), (0.82, 0.35), (0.85, 0.92) });
        Add("n", 0.85, 0.30, new[] { (0.15, 0.92), (0.18, 0.25), (0.52, 0.15), (0.82, 0.35), (0.85, 0.92) });
        AddClosed("o", 0.92, 0.38, new[] { (0.50, 0.08), (0.15, 0.24), (0.08, 0.52), (0.18, 0.84), (0.50, 0.94), (0.84, 0.82), (0.92, 0.52), (0.82, 0.22), (0.50, 0.08) });
        Add("p", 0.62, 0.32, new[] { (0.20, 0.08), (0.20, 0.98), (0.22, 0.22), (0.62, 0.10), (0.86, 0.32), (0.62, 0.56), (0.20, 0.52) });
        Add("p", 0.62, 0.32, new[] { (0.20, 0.08), (0.20, 0.98) }, new[] { (0.20, 0.16), (0.64, 0.10), (0.86, 0.34), (0.62, 0.56), (0.20, 0.52) });
        Add("q", 0.62, 0.32, new[] { (0.78, 0.22), (0.44, 0.08), (0.14, 0.32), (0.40, 0.56), (0.78, 0.42), (0.78, 0.98), (0.92, 0.84) });
        Add("q", 0.62, 0.32, new[] { (0.78, 0.20), (0.42, 0.08), (0.14, 0.34), (0.42, 0.56), (0.78, 0.42) }, new[] { (0.78, 0.12), (0.78, 0.98) });
        Add("r", 0.68, 0.35, new[] { (0.22, 0.18), (0.22, 0.94), (0.26, 0.42), (0.52, 0.16), (0.86, 0.22) });
        Add("s", 0.68, 0.32, new[] { (0.82, 0.15), (0.45, 0.05), (0.18, 0.25), (0.50, 0.50), (0.82, 0.72), (0.48, 0.95), (0.15, 0.85) });
        Add("t", 0.60, 0.35, new[] { (0.45, 0.05), (0.45, 0.86), (0.78, 0.92) }, new[] { (0.15, 0.35), (0.85, 0.35) });
        Add("u", 0.88, 0.32, new[] { (0.14, 0.12), (0.18, 0.72), (0.48, 0.92), (0.80, 0.72), (0.84, 0.12), (0.86, 0.92) });
        Add("u", 0.88, 0.32, new[] { (0.14, 0.12), (0.18, 0.75), (0.50, 0.94), (0.82, 0.75), (0.86, 0.12) });
        AddOpen("v", 0.85, 0.35, new[] { (0.12, 0.10), (0.50, 0.94), (0.88, 0.10) });
        AddOpen("w", 1.25, 0.32, new[] { (0.06, 0.12), (0.28, 0.92), (0.50, 0.32), (0.72, 0.92), (0.94, 0.12) });
        Add("x", 0.90, 0.30, new[] { (0.10, 0.10), (0.90, 0.90) }, new[] { (0.90, 0.10), (0.10, 0.90) });
        Add("x", 0.90, 0.30, new[] { (0.12, 0.16), (0.42, 0.50), (0.12, 0.86), (0.48, 0.52), (0.88, 0.16), (0.58, 0.50), (0.88, 0.86) });
        Add("y", 0.75, 0.30, new[] { (0.10, 0.10), (0.50, 0.55) }, new[] { (0.90, 0.10), (0.25, 1.00) });
        Add("y", 0.72, 0.30, new[] { (0.15, 0.10), (0.20, 0.45), (0.55, 0.50), (0.85, 0.10), (0.80, 0.95), (0.45, 0.98) });
        Add("z", 0.85, 0.32, new[] { (0.10, 0.10), (0.90, 0.10), (0.10, 0.90), (0.90, 0.90) });
        Add("z", 0.85, 0.32, new[] { (0.10, 0.10), (0.90, 0.10), (0.10, 0.90), (0.90, 0.90) }, new[] { (0.25, 0.50), (0.75, 0.50) });

        // === Uppercase English Letters A-Z ===
        Add("A", 0.80, 0.32, new[] { (0.50, 0.04), (0.10, 0.96) }, new[] { (0.50, 0.04), (0.90, 0.96) }, new[] { (0.22, 0.64), (0.78, 0.64) });
        Add("A", 0.80, 0.32, new[] { (0.10, 0.96), (0.50, 0.04), (0.90, 0.96) }, new[] { (0.22, 0.64), (0.78, 0.64) });
        Add("B", 0.68, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.72, 0.10), (0.78, 0.30), (0.22, 0.48), (0.82, 0.66), (0.74, 0.94), (0.18, 0.96) });
        Add("B", 0.68, 0.32, new[] { (0.18, 0.04), (0.18, 0.96), (0.20, 0.06), (0.72, 0.12), (0.76, 0.32), (0.22, 0.48), (0.82, 0.68), (0.72, 0.94), (0.18, 0.96) });
        AddOpen("C", 0.78, 0.35, new[] { (0.86, 0.18), (0.50, 0.04), (0.12, 0.50), (0.50, 0.96), (0.86, 0.82) });
        Add("D", 0.75, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.72, 0.16), (0.88, 0.50), (0.72, 0.84), (0.18, 0.96) });
        AddClosed("D", 0.75, 0.32, new[] { (0.18, 0.04), (0.18, 0.96), (0.72, 0.86), (0.88, 0.50), (0.72, 0.14), (0.18, 0.04) });
        Add("E", 0.68, 0.32, new[] { (0.82, 0.06), (0.18, 0.06), (0.18, 0.94), (0.84, 0.94) }, new[] { (0.18, 0.50), (0.74, 0.50) });
        Add("F", 0.66, 0.32, new[] { (0.84, 0.06), (0.18, 0.06), (0.18, 0.96) }, new[] { (0.18, 0.48), (0.74, 0.48) });
        Add("G", 0.82, 0.32, new[] { (0.84, 0.20), (0.48, 0.04), (0.12, 0.50), (0.48, 0.96), (0.86, 0.78), (0.86, 0.52), (0.52, 0.52) });
        Add("H", 0.76, 0.32, new[] { (0.16, 0.04), (0.16, 0.96) }, new[] { (0.84, 0.04), (0.84, 0.96) }, new[] { (0.16, 0.50), (0.84, 0.50) });
        Add("J", 0.58, 0.35, new[] { (0.74, 0.06), (0.74, 0.78), (0.48, 0.96), (0.16, 0.82) });
        Add("K", 0.72, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.84, 0.06), (0.20, 0.52), (0.86, 0.96) });
        Add("L", 0.68, 0.35, new[] { (0.18, 0.04), (0.18, 0.94), (0.86, 0.94) });
        Add("M", 0.95, 0.32, new[] { (0.10, 0.96), (0.14, 0.06), (0.50, 0.62), (0.86, 0.06), (0.90, 0.96) });
        Add("M", 0.95, 0.32, new[] { (0.12, 0.06), (0.12, 0.96) }, new[] { (0.12, 0.06), (0.50, 0.62), (0.88, 0.06), (0.88, 0.96) });
        Add("N", 0.78, 0.32, new[] { (0.14, 0.96), (0.14, 0.06), (0.86, 0.96), (0.86, 0.06) });
        Add("N", 0.78, 0.32, new[] { (0.14, 0.06), (0.14, 0.96) }, new[] { (0.14, 0.06), (0.86, 0.96), (0.86, 0.06) });
        AddClosed("O", 0.88, 0.35, new[] { (0.50, 0.04), (0.14, 0.22), (0.06, 0.50), (0.14, 0.80), (0.50, 0.96), (0.86, 0.80), (0.94, 0.50), (0.86, 0.22), (0.50, 0.04) });
        Add("P", 0.66, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.74, 0.10), (0.84, 0.32), (0.70, 0.52), (0.18, 0.54) });
        Add("Q", 0.88, 0.32, new[] { (0.50, 0.04), (0.12, 0.45), (0.50, 0.90), (0.88, 0.45), (0.50, 0.04) }, new[] { (0.58, 0.64), (0.92, 0.98) });
        Add("R", 0.72, 0.32, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.74, 0.12), (0.80, 0.34), (0.22, 0.52), (0.86, 0.96) });
        Add("S", 0.68, 0.32, new[] { (0.82, 0.14), (0.46, 0.04), (0.16, 0.24), (0.50, 0.50), (0.84, 0.74), (0.48, 0.96), (0.14, 0.84) });
        Add("T", 0.78, 0.35, new[] { (0.10, 0.08), (0.90, 0.08) }, new[] { (0.50, 0.08), (0.50, 0.96) });
        AddOpen("U", 0.80, 0.32, new[] { (0.14, 0.06), (0.16, 0.74), (0.50, 0.96), (0.84, 0.74), (0.86, 0.06) });
        AddOpen("V", 0.82, 0.35, new[] { (0.10, 0.06), (0.50, 0.96), (0.90, 0.06) });
        AddOpen("W", 1.15, 0.32, new[] { (0.06, 0.06), (0.28, 0.94), (0.50, 0.36), (0.72, 0.94), (0.94, 0.06) });
        Add("X", 0.85, 0.30, new[] { (0.12, 0.06), (0.88, 0.94) }, new[] { (0.88, 0.06), (0.12, 0.94) });
        Add("Y", 0.75, 0.32, new[] { (0.12, 0.06), (0.50, 0.50), (0.88, 0.06) }, new[] { (0.50, 0.50), (0.50, 0.96) });
        Add("Z", 0.82, 0.32, new[] { (0.10, 0.08), (0.90, 0.08), (0.10, 0.92), (0.90, 0.92) });

        // === Parentheses, inequalities, Greek, and math structures ===
        Add("(", 0.36, 0.45, new[] { (0.75, 0.05), (0.35, 0.30), (0.25, 0.50), (0.35, 0.70), (0.75, 0.95) });
        Add(")", 0.36, 0.45, new[] { (0.25, 0.05), (0.65, 0.30), (0.75, 0.50), (0.65, 0.70), (0.25, 0.95) });
        AddOpen("<", 0.65, 0.38, new[] { (0.85, 0.15), (0.15, 0.50), (0.85, 0.85) });
        AddOpen(">", 0.65, 0.38, new[] { (0.15, 0.15), (0.85, 0.50), (0.15, 0.85) });
        Add("+", 0.95, 0.30, new[] { (0.50, 0.10), (0.50, 0.90) }, new[] { (0.10, 0.50), (0.90, 0.50) });
        Add("=", 1.40, 0.38, new[] { (0.10, 0.30), (0.90, 0.30) }, new[] { (0.10, 0.70), (0.90, 0.70) });
        Add("√", 1.20, 0.30, new[] { (0.05, 0.55), (0.18, 0.55), (0.32, 0.95), (0.48, 0.08), (0.95, 0.08) });
        Add("∫", 0.36, 0.58, new[] { (0.82, 0.08), (0.55, 0.04), (0.48, 0.50), (0.42, 0.95), (0.18, 0.92) });
        Add("∞", 1.70, 0.48, new[] { (0.50, 0.50), (0.25, 0.20), (0.08, 0.50), (0.25, 0.80), (0.50, 0.50), (0.75, 0.20), (0.92, 0.50), (0.75, 0.80), (0.50, 0.50) });
        Add("π", 1.10, 0.30, new[] { (0.08, 0.18), (0.92, 0.18) }, new[] { (0.32, 0.20), (0.28, 0.92) }, new[] { (0.70, 0.20), (0.72, 0.92) });
        Add("α", 1.15, 0.32, new[] { (0.92, 0.22), (0.56, 0.72), (0.24, 0.86), (0.08, 0.52), (0.26, 0.18), (0.58, 0.36), (0.92, 0.82) });
        Add("θ", 0.72, 0.35, new[] { (0.50, 0.04), (0.14, 0.50), (0.50, 0.96), (0.86, 0.50), (0.50, 0.04) }, new[] { (0.14, 0.50), (0.86, 0.50) });

        return list.ToArray();
    }
}
