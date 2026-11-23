using System.Text;
using InkCanvas.AnalyticGeometry.Algebra;

namespace InkCanvas.AnalyticGeometry.Handwriting;

/// <summary>
/// Built-in 2D mathematical handwriting recognition engine (similar to Windows Math Input Panel).
/// Groups multi-stroke symbols and English letters, classifies them offline, resolves letter/function
/// context (sin, cos, tan, log, ln, GeoGebra commands, case &amp; digit disambiguation), parses 2D
/// spatial structures (fractions, radicals, superscripts, subscripts), and outputs expression + MathML 3.0.
/// </summary>
public static class MathInkRecognizer
{
    private static readonly string[] KnownFunctions =
    {
        "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh",
        "ln", "log", "abs", "exp", "lim", "sqrt", "pi",
    };

    private static readonly string[] KnownCommands =
    {
        "Point", "Line", "Segment", "Circle", "Polygon", "Midpoint", "Vector",
        "Solve", "Factor", "Expand", "Simplify", "Integral", "Derivative", "Limit",
    };

    private static readonly HashSet<char> BicameralLookAlikes = new("copsxzuvwCOPSXZUVW");

    public static MathInkRecognitionResult Recognize(
        IReadOnlyList<MathInkStroke> strokes,
        IReadOnlyDictionary<int, string> symbolOverrides = null)
    {
        if (strokes is null || strokes.Count == 0)
            return new MathInkRecognitionResult("", null, Array.Empty<RecognizedSymbol>(), Array.Empty<string>());

        var groups = GroupStrokes(strokes);
        var recognized = new List<RecognizedSymbol>(groups.Count);
        foreach (var group in groups)
        {
            var ranked = MathSymbolClassifier.Classify(group);
            var bounds = InkBounds.Empty;
            var ids = new int[group.Count];
            for (int i = 0; i < group.Count; i++) { bounds = bounds.Union(group[i].Bounds); ids[i] = group[i].Id; }
            string best = ranked.Count > 0 ? ranked[0].Symbol : "?";
            double confidence = ranked.Count > 0 ? ranked[0].Score : 0.2;
            var alternatives = ranked.Select(item => item.Symbol).Distinct().ToList();

            if (symbolOverrides is not null)
            {
                foreach (int id in ids)
                {
                    if (symbolOverrides.TryGetValue(id, out string chosen) && !string.IsNullOrWhiteSpace(chosen))
                    {
                        best = chosen;
                        confidence = 1.0;
                        if (!alternatives.Contains(chosen)) alternatives.Insert(0, chosen);
                        else { alternatives.Remove(chosen); alternatives.Insert(0, chosen); }
                        break;
                    }
                }
            }
            recognized.Add(new RecognizedSymbol(best, confidence, bounds, ids, alternatives));
        }

        // Context-aware English letter & function disambiguation using top-K alternatives and relative height.
        recognized = ResolveLetterContext(recognized, symbolOverrides);

        string primary = ParseSpatial(recognized);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) candidates.Add(primary);

        // Build alternative expression candidates by swapping the most ambiguous symbols.
        foreach (var entry in recognized
            .Select((sym, idx) => (Symbol: sym, Index: idx))
            .Where(item => item.Symbol.Alternatives.Count > 1 && item.Symbol.Confidence < 0.94)
            .OrderBy(item => item.Symbol.Confidence)
            .Take(3))
        {
            foreach (string alt in entry.Symbol.Alternatives.Skip(1).Take(2))
            {
                var mutated = recognized.ToList();
                mutated[entry.Index] = entry.Symbol with { Symbol = alt };
                string expr = ParseSpatial(mutated);
                if (!string.IsNullOrWhiteSpace(expr) && !candidates.Contains(expr))
                    candidates.Add(expr);
                if (candidates.Count >= 6) break;
            }
            if (candidates.Count >= 6) break;
        }

        string mathMl = null;
        if (!string.IsNullOrWhiteSpace(primary))
        {
            try { mathMl = MathMarkup.ToMathML(primary); }
            catch (FormatException) { mathMl = null; }
        }

        return new MathInkRecognitionResult(primary, mathMl, recognized, candidates);
    }

    private static List<RecognizedSymbol> ResolveLetterContext(
        List<RecognizedSymbol> symbols,
        IReadOnlyDictionary<int, string> overrides)
    {
        if (symbols.Count == 0) return symbols;
        var ordered = symbols.OrderBy(s => s.Bounds.Left).ToList();
        var locked = new HashSet<int>();
        if (overrides is not null)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].StrokeIds.Any(overrides.ContainsKey)) locked.Add(i);
        }

        double medianHeight = Median(ordered
            .Where(s => s.Symbol is not ("-" or "=" or "." or "," or "·"))
            .Select(s => s.Bounds.Height)
            .DefaultIfEmpty(36));

        // 1. Disambiguate bicameral letters (C/c, O/o, S/s, P/p, U/u, V/v, W/w, X/x, Z/z) using relative size & context.
        for (int i = 0; i < ordered.Count; i++)
        {
            if (locked.Contains(i)) continue;
            var sym = ordered[i];
            if (sym.Symbol.Length != 1) continue;
            char ch = sym.Symbol[0];
            bool followedByPointDef = i + 2 < ordered.Count && ordered[i + 1].Symbol == "=" && ordered[i + 2].Symbol == "(";
            bool followedByFuncCall = i + 1 < ordered.Count && ordered[i + 1].Symbol == "(";

            if (followedByPointDef && char.IsLetter(ch))
            {
                string upper = char.ToUpperInvariant(ch).ToString();
                if (sym.Alternatives.Contains(upper) || BicameralLookAlikes.Contains(ch))
                    ordered[i] = sym with { Symbol = upper };
            }
            else if (followedByFuncCall && char.IsLetter(ch) && i == 0 && sym.Bounds.Height <= medianHeight * 1.25)
            {
                string lower = char.ToLowerInvariant(ch).ToString();
                if (sym.Alternatives.Contains(lower))
                    ordered[i] = sym with { Symbol = lower };
            }
            else if (BicameralLookAlikes.Contains(ch) && ordered.Count >= 2)
            {
                bool clearlyTall = sym.Bounds.Height > medianHeight * 1.28;
                string preferred = clearlyTall ? char.ToUpperInvariant(ch).ToString() : char.ToLowerInvariant(ch).ToString();
                if (sym.Alternatives.Contains(preferred) && sym.Symbol != preferred)
                    ordered[i] = sym with { Symbol = preferred };
            }
        }

        // 2. Match multi-letter math functions and GeoGebra commands using each symbol's top-K alternatives.
        var vocabulary = KnownFunctions.Concat(KnownCommands).OrderByDescending(w => w.Length).ToArray();
        for (int i = 0; i < ordered.Count; i++)
        {
            foreach (string word in vocabulary)
            {
                if (i + word.Length > ordered.Count) continue;
                bool aligned = true;
                for (int k = 0; k < word.Length; k++)
                {
                    var current = ordered[i + k];
                    if (k > 0)
                    {
                        var prev = ordered[i + k - 1];
                        double gap = current.Bounds.Left - prev.Bounds.Right;
                        double dy = Math.Abs(current.Bounds.CenterY - prev.Bounds.CenterY);
                        if (gap > Math.Max(medianHeight * 1.35, 52) || dy > Math.Max(medianHeight * 0.75, 28))
                        { aligned = false; break; }
                    }
                    if (!CanMatchLetter(current, word[k], locked.Contains(i + k)))
                    { aligned = false; break; }
                }
                if (!aligned) continue;

                // Promote the matched word letters so downstream spatial parser cleanly recognizes the function/command.
                for (int k = 0; k < word.Length; k++)
                {
                    if (locked.Contains(i + k)) continue;
                    string letter = word[k].ToString();
                    var alts = ordered[i + k].Alternatives.ToList();
                    if (!alts.Contains(letter)) alts.Insert(0, letter);
                    ordered[i + k] = ordered[i + k] with { Symbol = letter, Confidence = Math.Max(ordered[i + k].Confidence, 0.90), Alternatives = alts };
                }
                i += word.Length - 1;
                break;
            }
        }

        // 3. Local digit vs letter disambiguation ('0' vs 'o'/'O', '1' vs 'l'/'I').
        for (int i = 0; i < ordered.Count; i++)
        {
            if (locked.Contains(i)) continue;
            var sym = ordered[i];
            bool prevDigit = i > 0 && ordered[i - 1].Symbol.Length == 1 && char.IsAsciiDigit(ordered[i - 1].Symbol[0]);
            bool nextDigit = i + 1 < ordered.Count && ordered[i + 1].Symbol.Length == 1 && char.IsAsciiDigit(ordered[i + 1].Symbol[0]);
            bool nextDot = i + 1 < ordered.Count && ordered[i + 1].Symbol == ".";
            bool prevDot = i > 0 && ordered[i - 1].Symbol == ".";
            if (prevDigit || nextDigit || prevDot || nextDot)
            {
                if (sym.Symbol is "o" or "O" && sym.Alternatives.Contains("0"))
                    ordered[i] = sym with { Symbol = "0" };
                else if (sym.Symbol is "l" or "I" && sym.Alternatives.Contains("1"))
                    ordered[i] = sym with { Symbol = "1" };
            }
        }

        return ordered;
    }

    private static bool CanMatchLetter(RecognizedSymbol sym, char expected, bool isLocked)
    {
        if (sym.Symbol.Length == 1 && char.ToLowerInvariant(sym.Symbol[0]) == char.ToLowerInvariant(expected)) return true;
        if (isLocked) return false;
        char lower = char.ToLowerInvariant(expected);
        foreach (string alt in sym.Alternatives)
        {
            if (alt.Length == 1 && char.ToLowerInvariant(alt[0]) == lower) return true;
        }
        // Common handwritten confusions inside known function words (e.g. '0' for 'o', '1' for 'l'/'i', '5' for 's', '2' for 'z').
        return (lower, sym.Symbol) switch
        {
            ('o', "0") => true,
            ('l', "1") or ('l', "I") or ('l', "|") => true,
            ('i', "1") or ('i', "l") => true,
            ('s', "5") => true,
            ('z', "2") => true,
            ('g', "9") => true,
            _ => false,
        };
    }

    private static List<List<MathInkStroke>> GroupStrokes(IReadOnlyList<MathInkStroke> strokes)
    {
        int n = strokes.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[rb] = ra; }

        double medianHeight = Median(strokes.Where(s => !s.IsDot && !s.IsHorizontalLine).Select(s => s.Bounds.Height).DefaultIfEmpty(36));
        double medianWidth = Median(strokes.Where(s => !s.IsDot && !s.IsHorizontalLine).Select(s => s.Bounds.Width).DefaultIfEmpty(26));
        double touchTolerance = Math.Clamp(medianWidth * 0.22, 6, 12);

        // Detect candidate long fraction bars first so they are NOT merged with numerator/denominator strokes.
        var fractionBars = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            var bar = strokes[i];
            if (!bar.IsHorizontalLine || bar.Bounds.Width < Math.Max(34, medianWidth * 1.15)) continue;
            bool hasAbove = false, hasBelow = false;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                var other = strokes[j];
                if (other.Bounds.CenterX < bar.Bounds.Left - 8 || other.Bounds.CenterX > bar.Bounds.Right + 8) continue;
                if (other.Bounds.Bottom <= bar.Bounds.CenterY + 6 && bar.Bounds.CenterY - other.Bounds.Bottom < medianHeight * 2.2) hasAbove = true;
                if (other.Bounds.Top >= bar.Bounds.CenterY - 6 && other.Bounds.Top - bar.Bounds.CenterY < medianHeight * 2.2) hasBelow = true;
            }
            if (hasAbove && hasBelow) fractionBars.Add(i);
        }

        for (int i = 0; i < n; i++)
        {
            if (fractionBars.Contains(i)) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (fractionBars.Contains(j)) continue;
                var a = strokes[i];
                var b = strokes[j];

                // 1. Direct intersection or near-touching strokes forming multi-stroke letters (A, B, D, E, F, H, K, P, R, T, X, Y, a, b, d, f, k, p, q, t, x, y).
                if (MathInkStroke.SegmentsIntersect(a, b, touchTolerance))
                {
                    if (LooksLikeRadicalTouchingContent(a, b) || LooksLikeRadicalTouchingContent(b, a)) continue;
                    // Only merge near-touching (non-crossing) strokes if their X-spans overlap meaningfully or one is a bar/stem.
                    double overlapX = Math.Min(a.Bounds.Right, b.Bounds.Right) - Math.Max(a.Bounds.Left, b.Bounds.Left);
                    double minW = Math.Max(1, Math.Min(a.Bounds.Width, b.Bounds.Width));
                    bool trueCross = MathInkStroke.SegmentsIntersect(a, b, 3.5);
                    if (trueCross || overlapX >= minW * 0.25 || a.IsHorizontalLine || b.IsHorizontalLine || a.IsVerticalLine || b.IsVerticalLine)
                    {
                        Union(i, j);
                        continue;
                    }
                }

                // 2. Parallel horizontal bars forming '=' (or top/bottom serifs / E / F).
                if (a.IsHorizontalLine && b.IsHorizontalLine)
                {
                    double overlapX = Math.Min(a.Bounds.Right, b.Bounds.Right) - Math.Max(a.Bounds.Left, b.Bounds.Left);
                    double minW = Math.Min(a.Bounds.Width, b.Bounds.Width);
                    double gapY = Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY);
                    if (overlapX >= minW * 0.5 && gapY <= Math.Max(26, Math.Max(a.Bounds.Width, b.Bounds.Width) * 0.85))
                    {
                        Union(i, j);
                        continue;
                    }
                }

                // 3. Dot above stem ('i', 'j') or dots above/below a short horizontal bar ('÷').
                if (a.IsDot ^ b.IsDot)
                {
                    var dot = a.IsDot ? a : b;
                    var stem = a.IsDot ? b : a;
                    if (!stem.IsHorizontalLine && stem.Bounds.Height >= 14
                        && dot.Bounds.CenterX >= stem.Bounds.Left - 14 && dot.Bounds.CenterX <= stem.Bounds.Right + 14
                        && dot.Bounds.Bottom <= stem.Bounds.Top + 8 && stem.Bounds.Top - dot.Bounds.Bottom <= Math.Max(24, stem.Bounds.Height * 0.65))
                    {
                        Union(i, j);
                        continue;
                    }
                    if (stem.IsHorizontalLine && dot.Bounds.CenterX >= stem.Bounds.Left && dot.Bounds.CenterX <= stem.Bounds.Right
                        && Math.Abs(dot.Bounds.CenterY - stem.Bounds.CenterY) <= Math.Max(24, stem.Bounds.Width * 0.7))
                    {
                        Union(i, j);
                        continue;
                    }
                }

                // 4. 'π' / 'T' top bar with vertical leg(s) underneath.
                if (a.IsHorizontalLine ^ b.IsHorizontalLine)
                {
                    var bar = a.IsHorizontalLine ? a : b;
                    var leg = a.IsHorizontalLine ? b : a;
                    if (leg.IsVerticalLine && leg.Bounds.CenterX >= bar.Bounds.Left - 4 && leg.Bounds.CenterX <= bar.Bounds.Right + 4
                        && leg.Bounds.Top >= bar.Bounds.Top - 8 && leg.Bounds.Top <= bar.Bounds.Bottom + 14)
                    {
                        Union(i, j);
                    }
                }
            }
        }

        var map = new Dictionary<int, List<MathInkStroke>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!map.TryGetValue(root, out var list)) map[root] = list = new List<MathInkStroke>();
            list.Add(strokes[i]);
        }
        return map.Values.ToList();
    }

    private static bool LooksLikeRadicalTouchingContent(MathInkStroke radical, MathInkStroke content)
    {
        if (radical.Bounds.Width < 24 || radical.Bounds.Height < 18) return false;
        return content.Bounds.Left > radical.Bounds.Left + radical.Bounds.Width * 0.25
            && content.Bounds.Right <= radical.Bounds.Right + 10
            && content.Bounds.Top >= radical.Bounds.Top - 4
            && content.Bounds.Bottom <= radical.Bounds.Bottom + 8;
    }

    private static string ParseSpatial(IReadOnlyList<RecognizedSymbol> items)
    {
        if (items.Count == 0) return "";
        var remaining = items.ToList();
        var tokens = new List<(InkBounds Bounds, string Text, bool IsOperator, bool IsAtom)>();

        double typicalHeight = Median(remaining
            .Where(s => s.Symbol is not ("-" or "=" or "." or "," or "·"))
            .Select(s => s.Bounds.Height)
            .DefaultIfEmpty(36));

        // Step 1: Detect fractions (horizontal '-' bar with symbols above and below).
        var bars = remaining
            .Where(s => s.Symbol == "-" && s.Bounds.Width >= Math.Max(26, s.Bounds.Height * 2.2))
            .OrderByDescending(s => s.Bounds.Width)
            .ToArray();

        foreach (var bar in bars)
        {
            if (!remaining.Contains(bar)) continue;
            double padX = Math.Max(8, bar.Bounds.Width * 0.1);
            var above = remaining.Where(s => !ReferenceEquals(s, bar)
                && s.Bounds.CenterX >= bar.Bounds.Left - padX && s.Bounds.CenterX <= bar.Bounds.Right + padX
                && s.Bounds.Bottom <= bar.Bounds.CenterY + 6
                && bar.Bounds.CenterY - s.Bounds.Bottom <= Math.Max(typicalHeight * 2.6, bar.Bounds.Width * 1.4)).ToList();
            var below = remaining.Where(s => !ReferenceEquals(s, bar)
                && s.Bounds.CenterX >= bar.Bounds.Left - padX && s.Bounds.CenterX <= bar.Bounds.Right + padX
                && s.Bounds.Top >= bar.Bounds.CenterY - 6
                && s.Bounds.Top - bar.Bounds.CenterY <= Math.Max(typicalHeight * 2.6, bar.Bounds.Width * 1.4)).ToList();

            if (above.Count > 0 && below.Count > 0)
            {
                remaining.Remove(bar);
                foreach (var a in above) remaining.Remove(a);
                foreach (var b in below) remaining.Remove(b);
                var box = bar.Bounds;
                foreach (var a in above) box = box.Union(a.Bounds);
                foreach (var b in below) box = box.Union(b.Bounds);
                string num = ParseSpatial(above);
                string den = ParseSpatial(below);
                tokens.Add((box, $"({num})/({den})", false, true));
            }
        }

        // Step 2: Detect radicals ('√' enclosing symbols underneath its roof).
        var radicals = remaining.Where(s => s.Symbol == "√").OrderByDescending(s => s.Bounds.Width).ToArray();
        foreach (var rad in radicals)
        {
            if (!remaining.Contains(rad)) continue;
            var inside = remaining.Where(s => !ReferenceEquals(s, rad)
                && s.Bounds.CenterX >= rad.Bounds.Left + rad.Bounds.Width * 0.22
                && s.Bounds.CenterX <= rad.Bounds.Right + 10
                && s.Bounds.CenterY >= rad.Bounds.Top - 6
                && s.Bounds.CenterY <= rad.Bounds.Bottom + 12).ToList();
            if (inside.Count > 0)
            {
                remaining.Remove(rad);
                foreach (var item in inside) remaining.Remove(item);
                var box = rad.Bounds;
                foreach (var item in inside) box = box.Union(item.Bounds);
                string body = ParseSpatial(inside);
                tokens.Add((box, $"sqrt({body})", false, true));
            }
        }

        foreach (var sym in remaining)
        {
            string mapped = NormalizeSymbol(sym.Symbol);
            bool isOp = mapped is "+" or "-" or "*" or "/" or "=" or "," or "<" or ">" or "<=" or ">=" or "!=" or "(" or ")" or "[" or "]";
            tokens.Add((sym.Bounds, mapped, isOp, !isOp || mapped == ")"));
        }

        tokens.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));

        // Step 3: Left-to-right assembly with superscript / subscript grouping.
        var output = new List<(string Text, bool IsOperator, bool IsAtom)>();
        int idx = 0;
        while (idx < tokens.Count)
        {
            var current = tokens[idx];
            idx++;

            if (current.IsAtom && !current.IsOperator && idx < tokens.Count)
            {
                var supTokens = new List<RecognizedSymbol>();
                var subTokens = new List<RecognizedSymbol>();
                double baseTop = current.Bounds.Top;
                double baseBottom = current.Bounds.Bottom;
                double baseHeight = Math.Max(14, current.Bounds.Height);
                double baseMidY = current.Bounds.CenterY;
                double cursorRight = current.Bounds.Right;

                while (idx < tokens.Count)
                {
                    var next = tokens[idx];
                    if (next.Bounds.Left - cursorRight > Math.Max(baseHeight * 1.4, 48)) break;
                    bool isSup = !next.IsOperator || next.Text is "-" or "+";
                    bool looksSup = isSup
                        && next.Bounds.CenterY < baseTop + baseHeight * 0.36
                        && next.Bounds.Bottom < baseMidY + baseHeight * 0.18
                        && next.Bounds.Height < baseHeight * 0.92;
                    bool looksSub = !next.IsOperator
                        && next.Bounds.CenterY > baseBottom - baseHeight * 0.34
                        && next.Bounds.Top > baseMidY - baseHeight * 0.12
                        && next.Bounds.Height < baseHeight * 0.85;

                    if (looksSup && subTokens.Count == 0)
                    {
                        supTokens.Add(new RecognizedSymbol(next.Text, 1, next.Bounds, Array.Empty<int>(), Array.Empty<string>()));
                        cursorRight = Math.Max(cursorRight, next.Bounds.Right);
                        idx++;
                    }
                    else if (looksSub && supTokens.Count == 0)
                    {
                        subTokens.Add(new RecognizedSymbol(next.Text, 1, next.Bounds, Array.Empty<int>(), Array.Empty<string>()));
                        cursorRight = Math.Max(cursorRight, next.Bounds.Right);
                        idx++;
                    }
                    else break;
                }

                string text = current.Text;
                if (supTokens.Count > 0)
                {
                    string sup = ParseSpatial(supTokens);
                    text = sup.Length == 1 ? $"{text}^{sup}" : $"{text}^({sup})";
                }
                else if (subTokens.Count > 0)
                {
                    string sub = ParseSpatial(subTokens);
                    text = $"{text}_{sub}";
                }
                output.Add((text, false, true));
            }
            else
            {
                output.Add((current.Text, current.IsOperator, current.IsAtom));
            }
        }

        return AssembleExpression(output);
    }

    private static string AssembleExpression(List<(string Text, bool IsOperator, bool IsAtom)> tokens)
    {
        if (tokens.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            string matchedFunc = TryMatchWord(tokens, i, KnownFunctions, out int consumed);
            if (matchedFunc is not null)
            {
                if (sb.Length > 0 && NeedsImplicitMultiply(sb[^1], matchedFunc[0])) sb.Append('*');
                sb.Append(matchedFunc);
                i += consumed - 1;
                if (matchedFunc != "pi" && i + 1 < tokens.Count && tokens[i + 1].Text != "(")
                {
                    sb.Append('(');
                    sb.Append(tokens[++i].Text);
                    sb.Append(')');
                }
                continue;
            }

            string matchedCmd = TryMatchWord(tokens, i, KnownCommands, out int cmdConsumed);
            if (matchedCmd is not null)
            {
                sb.Append(matchedCmd);
                i += cmdConsumed - 1;
                continue;
            }

            string part = tokens[i].Text;
            if (sb.Length > 0 && part.Length > 0 && NeedsImplicitMultiply(sb[^1], part[0]))
                sb.Append('*');
            sb.Append(part);
        }
        return sb.ToString();
    }

    private static string TryMatchWord(List<(string Text, bool IsOperator, bool IsAtom)> tokens, int start, string[] words, out int consumed)
    {
        consumed = 0;
        foreach (string word in words)
        {
            if (start + word.Length > tokens.Count) continue;
            bool match = true;
            for (int k = 0; k < word.Length; k++)
            {
                if (tokens[start + k].Text.Length != 1 || char.ToLowerInvariant(tokens[start + k].Text[0]) != char.ToLowerInvariant(word[k]))
                { match = false; break; }
            }
            if (match) { consumed = word.Length; return word; }
        }
        return null;
    }

    private static bool NeedsImplicitMultiply(char prev, char next)
    {
        bool prevEndsAtom = char.IsAsciiDigit(prev) || prev == ')';
        bool nextStartsAtom = char.IsLetter(next) || next == '(';
        return prevEndsAtom && nextStartsAtom;
    }

    private static string NormalizeSymbol(string symbol) => symbol switch
    {
        "×" => "*",
        "÷" => "/",
        "−" => "-",
        "π" => "pi",
        "·" => "*",
        "≠" => "!=",
        "≤" => "<=",
        "≥" => ">=",
        _ => symbol,
    };

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 36 : sorted[sorted.Length / 2];
    }
}
