using InkCanvas.AnalyticGeometry.Handwriting;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class MathInkRecognizerTests
{
    private static MathInkStroke Stroke(int id, params (double X, double Y)[] points)
        => new(id, points.Select(p => new InkSample(p.X, p.Y)).ToArray());

    private static MathInkStroke[] Boxed(double x, double y, double w, double h, ref int id, params (double X, double Y)[][] strokes)
    {
        var list = new MathInkStroke[strokes.Length];
        for (int i = 0; i < strokes.Length; i++)
            list[i] = new MathInkStroke(id++, strokes[i].Select(p => new InkSample(x + p.X * w, y + p.Y * h)).ToArray());
        return list;
    }

    [Fact]
    public void RecognizesBasicEquationWithEqualsAndVariables()
    {
        var strokes = new[]
        {
            Stroke(0, (20, 40), (35, 70)),
            Stroke(1, (50, 40), (22, 100)),
            Stroke(2, (70, 58), (105, 58)),
            Stroke(3, (70, 74), (105, 74)),
            Stroke(4, (125, 45), (160, 88)),
            Stroke(5, (160, 45), (125, 88)),
        };

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("y=x", result.Expression);
        Assert.NotNull(result.MathML);
        Assert.Contains("<mo>=</mo>", result.MathML);
        Assert.Equal(3, result.Symbols.Count);
    }

    [Fact]
    public void RecognizesTwoDimensionalFractionAndGeneratesMfracMathMl()
    {
        var strokes = new[]
        {
            Stroke(0, (80, 20), (110, 55)),
            Stroke(1, (110, 20), (80, 55)),
            Stroke(2, (50, 75), (145, 75)),
            Stroke(3, (78, 95), (92, 85), (110, 85), (118, 95), (108, 110), (76, 130), (120, 130)),
        };

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("(x)/(2)", result.Expression);
        Assert.NotNull(result.MathML);
        Assert.Contains("<mfrac>", result.MathML);
    }

    [Fact]
    public void RecognizesSuperscriptExponentAndGeneratesMsupMathMl()
    {
        var strokes = new[]
        {
            Stroke(0, (30, 60), (80, 120)),
            Stroke(1, (80, 60), (30, 120)),
            Stroke(2, (92, 42), (102, 34), (116, 34), (122, 42), (112, 54), (90, 68), (124, 68)),
        };

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("x^2", result.Expression);
        Assert.NotNull(result.MathML);
        Assert.Contains("<msup>", result.MathML);
    }

    [Fact]
    public void RecognizesRadicalWithEnclosedRadicand()
    {
        var strokes = new[]
        {
            Stroke(0, (20, 75), (32, 75), (46, 115), (65, 35), (155, 35)),
            Stroke(1, (82, 58), (122, 102)),
            Stroke(2, (122, 58), (82, 102)),
        };

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("sqrt(x)", result.Expression);
        Assert.NotNull(result.MathML);
        Assert.Contains("<msqrt>", result.MathML);
    }

    [Fact]
    public void SymbolOverrideCorrectsRecognizedExpressionImmediately()
    {
        var strokes = new[]
        {
            Stroke(0, (20, 45), (55, 85)),
            Stroke(1, (55, 45), (20, 85)),
            Stroke(2, (80, 45), (80, 85)),
            Stroke(3, (65, 65), (95, 65)),
            Stroke(4, (120, 45), (120, 85)),
        };

        var initial = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("x+1", initial.Expression);

        var corrected = MathInkRecognizer.Recognize(strokes, new Dictionary<int, string> { [0] = "a" });
        Assert.Equal("a+1", corrected.Expression);
    }

    [Fact]
    public void RecognizesLowercaseEnglishLettersWithPrintAndCursiveVariants()
    {
        var samples = new (string Expected, (double X, double Y)[][] Strokes)[]
        {
            ("a", new[] { new[] { (0.78, 0.25), (0.45, 0.10), (0.12, 0.45), (0.25, 0.88), (0.70, 0.75), (0.82, 0.25), (0.85, 0.92) } }),
            ("b", new[] { new[] { (0.20, 0.02), (0.20, 0.95) }, new[] { (0.20, 0.48), (0.68, 0.45), (0.88, 0.70), (0.62, 0.95), (0.20, 0.92) } }),
            ("d", new[] { new[] { (0.78, 0.02), (0.78, 0.96) }, new[] { (0.78, 0.46), (0.36, 0.44), (0.12, 0.70), (0.38, 0.95), (0.78, 0.90) } }),
            ("e", new[] { new[] { (0.15, 0.52), (0.85, 0.48), (0.72, 0.15), (0.30, 0.15), (0.12, 0.52), (0.32, 0.88), (0.85, 0.82) } }),
            ("f", new[] { new[] { (0.82, 0.12), (0.55, 0.04), (0.45, 0.35), (0.42, 0.96) }, new[] { (0.15, 0.45), (0.82, 0.45) } }),
            ("g", new[] { new[] { (0.80, 0.22), (0.48, 0.08), (0.15, 0.28), (0.42, 0.52), (0.80, 0.35), (0.78, 0.85), (0.50, 0.98), (0.18, 0.88) } }),
            ("h", new[] { new[] { (0.18, 0.04), (0.18, 0.96), (0.22, 0.52), (0.55, 0.38), (0.82, 0.55), (0.84, 0.96) } }),
            ("i", new[] { new[] { (0.50, 0.30), (0.50, 0.96) }, new[] { (0.50, 0.10), (0.51, 0.11) } }),
            ("k", new[] { new[] { (0.20, 0.04), (0.20, 0.96) }, new[] { (0.84, 0.28), (0.22, 0.60), (0.86, 0.96) } }),
            ("m", new[] { new[] { (0.08, 0.24), (0.08, 0.92), (0.12, 0.36), (0.32, 0.18), (0.50, 0.40), (0.50, 0.92), (0.54, 0.36), (0.74, 0.18), (0.92, 0.40), (0.92, 0.92) } }),
            ("n", new[] { new[] { (0.15, 0.92), (0.18, 0.25), (0.52, 0.15), (0.82, 0.35), (0.85, 0.92) } }),
            ("r", new[] { new[] { (0.22, 0.18), (0.22, 0.94), (0.26, 0.42), (0.52, 0.16), (0.86, 0.22) } }),
            ("t", new[] { new[] { (0.15, 0.35), (0.85, 0.35) }, new[] { (0.45, 0.05), (0.45, 0.86), (0.78, 0.92) } }), // reversed stroke order!
            ("u", new[] { new[] { (0.14, 0.12), (0.18, 0.72), (0.48, 0.92), (0.80, 0.72), (0.84, 0.12), (0.86, 0.92) } }),
            ("w", new[] { new[] { (0.06, 0.12), (0.28, 0.92), (0.50, 0.32), (0.72, 0.92), (0.94, 0.12) } }),
        };

        foreach (var (expected, raw) in samples)
        {
            int id = 0;
            var strokes = Boxed(20, 20, 48, 64, ref id, raw);
            var ranked = MathSymbolClassifier.Classify(strokes);
            Assert.True(ranked.Count > 0 && ranked[0].Symbol.Equals(expected, StringComparison.OrdinalIgnoreCase),
                $"Expected '{expected}', got '{(ranked.Count > 0 ? ranked[0].Symbol : "<none>")}' (top3: {string.Join(", ", ranked.Take(3).Select(r => $"{r.Symbol}:{r.Score:F2}"))})");
        }
    }

    [Fact]
    public void RecognizesUppercaseGeometryPointLettersAndStrokeOrderInvariance()
    {
        var samples = new (string Expected, (double X, double Y)[][] Strokes)[]
        {
            ("A", new[] { new[] { (0.22, 0.64), (0.78, 0.64) }, new[] { (0.50, 0.04), (0.10, 0.96) }, new[] { (0.50, 0.04), (0.90, 0.96) } }),
            ("B", new[] { new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.72, 0.10), (0.78, 0.30), (0.22, 0.48), (0.82, 0.66), (0.74, 0.94), (0.18, 0.96) } }),
            ("D", new[] { new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.72, 0.16), (0.88, 0.50), (0.72, 0.84), (0.18, 0.96) } }),
            ("E", new[] { new[] { (0.82, 0.06), (0.18, 0.06), (0.18, 0.94), (0.84, 0.94) }, new[] { (0.18, 0.50), (0.74, 0.50) } }),
            ("F", new[] { new[] { (0.18, 0.06), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.84, 0.06) }, new[] { (0.18, 0.48), (0.74, 0.48) } }),
            ("H", new[] { new[] { (0.16, 0.04), (0.16, 0.96) }, new[] { (0.84, 0.04), (0.84, 0.96) }, new[] { (0.16, 0.50), (0.84, 0.50) } }),
            ("L", new[] { new[] { (0.18, 0.04), (0.18, 0.94), (0.86, 0.94) } }),
            ("M", new[] { new[] { (0.10, 0.96), (0.14, 0.06), (0.50, 0.62), (0.86, 0.06), (0.90, 0.96) } }),
            ("N", new[] { new[] { (0.14, 0.96), (0.14, 0.06), (0.86, 0.96), (0.86, 0.06) } }),
            ("P", new[] { new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.74, 0.10), (0.84, 0.32), (0.70, 0.52), (0.18, 0.54) } }),
            ("R", new[] { new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.74, 0.12), (0.80, 0.34), (0.22, 0.52), (0.86, 0.96) } }),
            ("T", new[] { new[] { (0.50, 0.08), (0.50, 0.96) }, new[] { (0.10, 0.08), (0.90, 0.08) } }), // reversed stroke order!
        };

        foreach (var (expected, raw) in samples)
        {
            int id = 0;
            var strokes = Boxed(20, 20, 50, 68, ref id, raw);
            var ranked = MathSymbolClassifier.Classify(strokes);
            Assert.True(ranked.Count > 0 && ranked[0].Symbol.Equals(expected, StringComparison.OrdinalIgnoreCase),
                $"Expected '{expected}', got '{(ranked.Count > 0 ? ranked[0].Symbol : "<none>")}' (top3: {string.Join(", ", ranked.Take(3).Select(r => $"{r.Symbol}:{r.Score:F2}"))})");
        }
    }

    [Fact]
    public void RecognizesPointDefinitionWithUppercaseContext()
    {
        // Write "P=(1,2)" — bicameral P/p should resolve to uppercase 'P' before '=('!
        int id = 0;
        var strokes = new List<MathInkStroke>();
        strokes.AddRange(Boxed(10, 30, 40, 62, ref id, new[] { (0.18, 0.04), (0.18, 0.96) }, new[] { (0.18, 0.06), (0.74, 0.10), (0.84, 0.32), (0.70, 0.52), (0.18, 0.54) })); // P
        strokes.AddRange(Boxed(66, 48, 34, 24, ref id, new[] { (0.10, 0.25), (0.90, 0.25) }, new[] { (0.10, 0.75), (0.90, 0.75) })); // =
        strokes.AddRange(Boxed(114, 30, 18, 62, ref id, new[] { (0.75, 0.05), (0.35, 0.30), (0.25, 0.50), (0.35, 0.70), (0.75, 0.95) })); // (
        strokes.AddRange(Boxed(142, 34, 10, 54, ref id, new[] { (0.5, 0.0), (0.5, 1.0) })); // 1

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.StartsWith("P=(1", result.Expression);
    }

    [Fact]
    public void RecognizesMultiLetterTrigonometricFunctionWithContextDisambiguation()
    {
        // Write "cosx" where 'o' is drawn like a '0' circle — context resolver should still output "cos(x)".
        int id = 0;
        var strokes = new List<MathInkStroke>();
        strokes.AddRange(Boxed(10, 40, 36, 46, ref id, new[] { (0.85, 0.20), (0.45, 0.08), (0.12, 0.50), (0.45, 0.92), (0.85, 0.82) })); // c
        strokes.AddRange(Boxed(60, 40, 36, 46, ref id, new[] { (0.50, 0.05), (0.15, 0.20), (0.08, 0.50), (0.18, 0.85), (0.50, 0.96), (0.82, 0.85), (0.92, 0.50), (0.82, 0.20), (0.50, 0.05) })); // o / 0
        strokes.AddRange(Boxed(110, 40, 34, 46, ref id, new[] { (0.82, 0.15), (0.45, 0.05), (0.18, 0.25), (0.50, 0.50), (0.82, 0.72), (0.48, 0.95), (0.15, 0.85) })); // s
        strokes.AddRange(Boxed(162, 40, 36, 46, ref id, new[] { (0.10, 0.10), (0.90, 0.90) }, new[] { (0.90, 0.10), (0.10, 0.90) })); // x

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("cos(x)", result.Expression);
        Assert.NotNull(result.MathML);
    }

    [Fact]
    public void RecognizesLogFunctionWithVerticalBarDisambiguation()
    {
        // Write "lnx" where 'l' is a single vertical line (looks like '1') — context resolver should output "ln(x)".
        int id = 0;
        var strokes = new List<MathInkStroke>();
        strokes.AddRange(Boxed(10, 30, 12, 60, ref id, new[] { (0.5, 0.0), (0.5, 1.0) })); // l / 1
        strokes.AddRange(Boxed(40, 46, 36, 44, ref id, new[] { (0.15, 0.92), (0.18, 0.25), (0.52, 0.15), (0.82, 0.35), (0.85, 0.92) })); // n
        strokes.AddRange(Boxed(96, 46, 36, 44, ref id, new[] { (0.10, 0.10), (0.90, 0.90) }, new[] { (0.90, 0.10), (0.10, 0.90) })); // x

        var result = MathInkRecognizer.Recognize(strokes);
        Assert.Equal("ln(x)", result.Expression);
    }
}
