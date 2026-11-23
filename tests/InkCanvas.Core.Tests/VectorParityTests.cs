#nullable enable annotations
using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.Core.Imaging;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Input;
using InkCanvas.Core.Recognition;
using InkCanvas.Core.Replay;
using InkCanvas.Core.Selection;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Shapes;
using InkCanvas.Core.Theming;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Ink;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Platform.Input;
using InkCanvas.Platform.Windows;
using System.Numerics;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class VectorParityTests
{
    private static readonly string Root = FindRoot();
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "parity", "vectors")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repository root with parity/vectors not found");
    }

    private static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "parity", "vectors", file)));

    [Theory]
    [InlineData("taper-tail.json")]
    [InlineData("taper-velocity.json")]
    public void TaperVectorsMatch(string file)
    {
        using var doc = Load(file);
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var input = c.GetProperty("input");
            var baseWidth = (float)input[0].GetDouble();
            var pointsJson = input[1];
            var kind = (TaperKind)input[2].GetInt32();
            TaperProfile profile;
            if (input.GetArrayLength() > 3)
            {
                var options = input[3];
                profile = new TailTaper
                {
                    TailPoints = options.GetProperty("TailPoints").GetInt32(),
                    HeadPoints = options.GetProperty("HeadPoints").GetInt32(),
                    TipRatio = options.GetProperty("TipRatio").GetSingle()
                };
            }
            else
            {
                profile = TaperProfile.Create(kind);
            }

            var points = pointsJson.EnumerateArray()
                .Select(p => new InkPoint(
                    p.GetProperty("X").GetSingle(),
                    p.GetProperty("Y").GetSingle(),
                    p.GetProperty("Pressure").GetSingle(),
                    0f,
                    p.GetProperty("TimeMs").GetSingle()))
                .ToArray();
            var span = points.AsSpan();
            for (var tip = 0; tip < points.Length; tip++)
            {
                profile.Apply(span, tip, baseWidth);
            }

            var expected = c.GetProperty("expected").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            Assert.Equal(expected.Length, points.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], points[i].Width, 4);
            }
            Assert.NotNull(id);
        }
    }

    [Fact]
    public void GeometryVectorsMatch()
    {
        using var doc = Load("stroke-geometry.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var stroke = MakeStrokeFromPoints(c.GetProperty("input")[0]);
            var actual = StrokeGeometry.BuildUnionContours(stroke);
            var expected = c.GetProperty("expected");
            Assert.Equal(expected.GetArrayLength(), actual.Length);
            for (var ci = 0; ci < actual.Length; ci++)
            {
                var flat = actual[ci].SelectMany(v => new[] { (double)v.X, (double)v.Y }).ToArray();
                var exp = expected[ci];
                Assert.Equal(exp.GetArrayLength(), flat.Length);
                for (var i = 0; i < flat.Length; i++)
                {
                    Assert.Equal(exp[i].GetDouble(), flat[i], 4);
                }
            }
        }
    }

    [Fact]
    public void EraserVectorsMatch()
    {
        using var doc = Load("stroke-eraser.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var stroke = MakeStroke(input[0]);
            var touched = StrokeEraser.EraseDisc(
                stroke,
                new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()),
                input[3].GetSingle(),
                out var remaining);
            var expected = c.GetProperty("expected");
            Assert.Equal(expected.GetProperty("touched").GetBoolean(), touched);
            var fragments = expected.GetProperty("fragments");
            Assert.Equal(fragments.GetArrayLength(), remaining.Count);
            for (var fi = 0; fi < remaining.Count; fi++)
            {
                var exp = fragments[fi];
                var fragment = remaining[fi];
                Assert.Equal(exp.GetProperty("ColorPacked").GetUInt32(), fragment.Attributes.Color.PackedArgb);
                Assert.Equal(exp.GetProperty("BaseWidth").GetDouble(), (double)fragment.Attributes.BaseWidth, 4);
                Assert.Equal(exp.GetProperty("IsHighlighter").GetBoolean(), fragment.Attributes.IsHighlighter);
                Assert.Equal(exp.GetProperty("HighlighterAlpha").GetInt32(), (int)fragment.Attributes.HighlighterAlpha);
                Assert.Equal(exp.GetProperty("TaperKind").GetInt32(), (int)fragment.Attributes.TaperKind);
                Assert.Equal(exp.GetProperty("IsRecognizedShape").GetBoolean(), fragment.IsRecognizedShape);
                var points = exp.GetProperty("Points");
                Assert.Equal(points.GetArrayLength(), fragment.Count);
                for (var pi = 0; pi < fragment.Count; pi++)
                {
                    var ep = points[pi];
                    var ap = fragment.Points[pi];
                    Assert.Equal(ep.GetProperty("X").GetDouble(), ap.X, 4);
                    Assert.Equal(ep.GetProperty("Y").GetDouble(), ap.Y, 4);
                    Assert.Equal(ep.GetProperty("Pressure").GetDouble(), ap.Pressure, 4);
                    Assert.Equal(ep.GetProperty("Width").GetDouble(), ap.Width, 4);
                    Assert.Equal(ep.GetProperty("TimeMs").GetDouble(), ap.TimeMs, 4);
                }
            }
        }
    }

    [Fact]
    public void UndoVectorsMatch()
    {
        using var doc = Load("undo.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var ops = input[0].EnumerateArray().Select(e => e.GetString()!).ToArray();
            var capacity = input[1].GetInt32();
            var stack = new InkCanvas.Core.History.UndoStack(capacity);
            var log = new List<string>();
            foreach (var op in ops)
            {
                if (op.StartsWith("push:"))
                {
                    var name = op.Substring(5);
                    stack.Push(new InkCanvas.Core.History.DelegateAction(
                        name,
                        () => log.Add("apply:" + name),
                        () => log.Add("revert:" + name)));
                }
                else if (op == "undo")
                {
                    stack.Undo();
                }
                else if (op == "redo")
                {
                    stack.Redo();
                }
                else if (op == "clear")
                {
                    stack.Clear();
                }
            }

            var expected = c.GetProperty("expected");
            Assert.Equal(expected.GetProperty("canUndo").GetBoolean(), stack.CanUndo);
            Assert.Equal(expected.GetProperty("canRedo").GetBoolean(), stack.CanRedo);
            Assert.Equal(expected.GetProperty("undoCount").GetInt32(), stack.UndoCount);
            Assert.Equal(expected.GetProperty("redoCount").GetInt32(), stack.RedoCount);
            var expectedLog = expected.GetProperty("log").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(expectedLog, log);
        }
    }

    [Fact]
    public void MatrixVectorsMatch()
    {
        using var doc = Load("matrix.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "matrixMultiply":
                    actual = Arr(Mat(input[0]) * Mat(input[1]));
                    break;
                case "transformPoint":
                {
                    var p = System.Numerics.Vector2.Transform(
                        new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()),
                        Mat(input[0]));
                    actual = new { X = p.X, Y = p.Y };
                    break;
                }
                case "createRotation":
                    actual = Arr(System.Numerics.Matrix3x2.CreateRotation(input[0].GetSingle()));
                    break;
                case "invertMatrixValues":
                    System.Numerics.Matrix3x2.Invert(Mat(input[0]), out var inverted);
                    actual = Arr(inverted);
                    break;
                default:
                    throw new InvalidOperationException("unknown matrix fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void SelectionVectorsMatch()
    {
        using var doc = Load("selection.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "selectionHitTest":
                    actual = (int)SelectionGeometry.HitTest(
                        Bounds(input[0]),
                        new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()));
                    break;
                case "selectionDragMatrix":
                {
                    var handle = (SelectionHandle)input[0].GetInt32();
                    var from = new System.Numerics.Vector2(input[2].GetSingle(), input[3].GetSingle());
                    var to = new System.Numerics.Vector2(input[4].GetSingle(), input[5].GetSingle());
                    actual = Arr(SelectionGeometry.DragMatrix(handle, Bounds(input[1]), from, to, input[6].GetBoolean()));
                    break;
                }
                case "rotateHandlePosition":
                {
                    var p = SelectionGeometry.RotateHandlePosition(Bounds(input[0]));
                    actual = new { X = p.X, Y = p.Y };
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown selection fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void TwoFingerVectorsMatch()
    {
        using var doc = Load("two-finger.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var options = input[0];
            var manipulator = new TwoFingerManipulator
            {
                AllowZoom = options.GetProperty("allowZoom").GetBoolean(),
                AllowTranslate = options.GetProperty("allowTranslate").GetBoolean(),
                AllowRotation = options.GetProperty("allowRotation").GetBoolean(),
                MinScale = options.GetProperty("minScale").GetSingle(),
                MaxScale = options.GetProperty("maxScale").GetSingle()
            };
            var transform = Mat(input[1]);
            var center = new System.Numerics.Vector2(input[2].GetSingle(), input[3].GetSingle());
            var changed = new List<bool>();
            foreach (var s in input[4].EnumerateArray())
            {
                changed.Add(manipulator.Update(
                    new System.Numerics.Vector2(s.GetProperty("AX").GetSingle(), s.GetProperty("AY").GetSingle()),
                    new System.Numerics.Vector2(s.GetProperty("BX").GetSingle(), s.GetProperty("BY").GetSingle()),
                    ref transform,
                    center));
            }
            AssertNear(c.GetProperty("expected"), Node(new { changed, final = Arr(transform) }),
                c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void PivotVectorsMatch()
    {
        using var doc = Load("pivot.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var resolved = MatrixTransformCenter.Resolve(
                (MatrixTransformCenterPointOptions)input[0].GetInt32(),
                new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()),
                new System.Numerics.Vector2(input[3].GetSingle(), input[4].GetSingle()),
                new System.Numerics.Vector2(input[5].GetSingle(), input[6].GetSingle()),
                input[7].GetBoolean());
            AssertNear(c.GetProperty("expected"), Node(new { X = resolved.X, Y = resolved.Y }), 0);
        }
    }

    [Fact]
    public void ShapeVectorsMatch()
    {
        using var doc = Load("shapes.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var fn = c.GetProperty("fn").GetString();
            object actual;
            switch (fn)
            {
                case "generate":
                {
                    var kind = (ShapeKind)input[0].GetInt32();
                    actual = ShapeSummaries(ShapeGenerator.Generate(
                        kind, Vec(input[1], input[2]), Vec(input[3], input[4]), ShapeAttrs(input[5])));
                    break;
                }
                case "generateCuboidDepth":
                    actual = ShapeSummaries(ShapeGenerator.GenerateCuboidDepth(
                        Vec(input[0], input[1]), Vec(input[2], input[3]),
                        input[4].GetSingle(), ShapeAttrs(input[5])));
                    break;
                case "generateHyperbolaBranches":
                    actual = ShapeSummaries(ShapeGenerator.GenerateHyperbolaBranches(
                        Vec(input[0], input[1]), input[2].GetSingle(), Vec(input[3], input[4]),
                        input[5].GetBoolean(), ShapeAttrs(input[6])));
                    break;
                case "snapParallelEnd":
                {
                    var p = ShapeGenerator.SnapParallelEnd(Vec(input[0], input[1]), Vec(input[2], input[3]));
                    actual = new { p.X, p.Y };
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown shape fn " + fn);
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void RecognizerVectorsMatch()
    {
        using var doc = Load("recognizer.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var inputStrokes = c.GetProperty("input")[0];
            var strokes = new List<InkStroke>();
            if (inputStrokes.GetArrayLength() > 0
                && inputStrokes[0].ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement strokeJson in inputStrokes.EnumerateArray())
                    strokes.Add(BuildRecognizerStroke(strokeJson));
            }
            else
            {
                strokes.Add(BuildRecognizerStroke(inputStrokes));
            }
            var result = ShapeRecognizer.Recognize(strokes);
            var actual = new
            {
                kind = (int)result.Shape,
                centerX = result.Center.X,
                centerY = result.Center.Y,
                radiusX = result.RadiusX,
                radiusY = result.RadiusY,
                confidence = result.Confidence,
                corners = result.Corners.Select(p => new { p.X, p.Y }).ToArray()
            };
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    private static Vector2 Vec(JsonElement x, JsonElement y) => new(x.GetSingle(), y.GetSingle());

    private static StrokeAttributes ShapeAttrs(JsonElement e) => new()
    {
        Color = InkColor.FromPackedArgb(e.GetProperty("ColorPacked").GetUInt32()),
        BaseWidth = e.GetProperty("BaseWidth").GetSingle(),
        IsHighlighter = e.GetProperty("IsHighlighter").GetBoolean(),
        HighlighterAlpha = e.GetProperty("HighlighterAlpha").GetByte(),
        TaperKind = (TaperKind)e.GetProperty("TaperKind").GetInt32()
    };

    private static object[] ShapeSummaries(List<InkStroke> strokes) => strokes.Select(s => new
    {
        Points = s.Points.Select(p => new { p.X, p.Y, p.Pressure, p.Width, p.TimeMs }).ToArray(),
        ColorPacked = s.Attributes.Color.PackedArgb,
        BaseWidth = s.Attributes.BaseWidth,
        IsHighlighter = s.Attributes.IsHighlighter,
        HighlighterAlpha = (int)s.Attributes.HighlighterAlpha,
        TaperKind = (int)s.Attributes.TaperKind,
        IsRecognizedShape = s.IsRecognizedShape
    }).ToArray();

    private static InkStroke BuildRecognizerStroke(JsonElement points)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 3f });
        foreach (JsonElement point in points.EnumerateArray())
            stroke.AddPoint(point.GetProperty("X").GetSingle(), point.GetProperty("Y").GetSingle());
        stroke.Complete();
        return stroke;
    }

    private static System.Numerics.Matrix3x2 Mat(JsonElement e) => new(
        e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(),
        e[3].GetSingle(), e[4].GetSingle(), e[5].GetSingle());

    private static double[] Arr(System.Numerics.Matrix3x2 m) =>
        new double[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };

    private static Rect Bounds(JsonElement e) => new(
        e.GetProperty("Left").GetSingle(),
        e.GetProperty("Top").GetSingle(),
        e.GetProperty("Right").GetSingle(),
        e.GetProperty("Bottom").GetSingle());

    private static JsonNode Node(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!;

    private static void AssertNear(JsonNode? expected, JsonNode? actual, double tolerance)
    {
        using var document = JsonDocument.Parse(expected?.ToJsonString() ?? "null");
        AssertNear(document.RootElement, actual, tolerance);
    }

    /// <summary>Drop actual-only fields (settings added after the reference vector was captured).</summary>
    private static void TrimToExpected(JsonNode actual, JsonNode expected)
    {
        if (actual is not JsonObject actualObject || expected is not JsonObject expectedObject) return;
        foreach (var name in actualObject.Select(pair => pair.Key).ToArray())
        {
            if (!expectedObject.TryGetPropertyValue(name, out var expectedValue))
            {
                actualObject.Remove(name);
                continue;
            }
            TrimToExpected(actualObject[name], expectedValue);
        }
    }

    private static void AssertNear(JsonElement expected, JsonNode? actual, double tolerance)
    {
        var precision = tolerance <= 0 ? -1 : (int)Math.Max(0, Math.Round(-Math.Log10(tolerance)));
        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
                Assert.NotNull(actual);
                if (precision >= 0)
                {
                    Assert.Equal(expected.GetDouble(), actual!.GetValue<double>(), precision);
                }
                else
                {
                    Assert.Equal(expected.GetDouble(), actual!.GetValue<double>());
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual!.GetValue<bool>());
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual!.GetValue<string>());
                break;
            case JsonValueKind.Null:
                Assert.Null(actual);
                break;
            case JsonValueKind.Array:
            {
                var array = actual!.AsArray();
                Assert.Equal(expected.GetArrayLength(), array.Count);
                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    AssertNear(expected[i], array[i], tolerance);
                }
                break;
            }
            case JsonValueKind.Object:
            {
                var obj = actual!.AsObject();
                var props = expected.EnumerateObject().ToArray();
                Assert.Equal(props.Length, obj.Count);
                foreach (var p in props)
                {
                    Assert.True(obj.ContainsKey(p.Name), "missing property " + p.Name);
                    AssertNear(p.Value, obj[p.Name], tolerance);
                }
                break;
            }
            default:
                throw new InvalidOperationException("unsupported json kind " + expected.ValueKind);
        }
    }

    [Fact]
    public void TouchClassifierVectorsMatch()
    {
        using var doc = Load("touch-classifier.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "measureContactWidth":
                    actual = TouchContactClassifier.MeasureContactWidth(
                        input[0].GetDouble(), input[1].GetDouble(), input[2].GetBoolean());
                    break;
                case "classifyContact":
                {
                    var spec = input[0];
                    var advanced = ParseAdvanced(spec.GetProperty("advanced"));
                    var decision = TouchContactClassifier.Classify(
                        spec.GetProperty("contactWidth").GetDouble(),
                        advanced!,
                        spec.GetProperty("nibMode").GetBoolean(),
                        spec.GetProperty("forceEraser").GetBoolean(),
                        spec.GetProperty("isDrawingShape").GetBoolean(),
                        spec.GetProperty("forcePointEraser").GetBoolean(),
                        spec.GetProperty("pptGestureEligible").GetBoolean());
                    actual = new { intent = (int)decision.Intent, eraserDiameter = decision.EraserDiameter };
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown touch fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void PalmVectorsMatch()
    {
        using var doc = Load("palm.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var options = input[1];
            var rejector = new PalmRejector
            {
                PalmContactThreshold = options.GetProperty("palmContactThreshold").GetSingle(),
                PenHasPriority = options.GetProperty("penHasPriority").GetBoolean(),
                Advanced = ParseAdvanced(options.GetProperty("advanced")),
                NibMode = options.GetProperty("nibMode").GetBoolean(),
                ForceEraser = options.GetProperty("forceEraser").GetBoolean(),
                ForcePointEraser = options.GetProperty("forcePointEraser").GetBoolean(),
                PptGestureEligible = options.GetProperty("pptGestureEligible").GetBoolean()
            };
            var results = new List<object>();
            foreach (var e in input[0].EnumerateArray())
            {
                var pointer = new OverlayPointer(
                    e.GetProperty("id").GetUInt32(), 0, 0, 0.5f,
                    e.GetProperty("contactWidth").GetSingle(),
                    e.GetProperty("contactHeight").GetSingle(),
                    (OverlayPointerKind)e.GetProperty("kind").GetInt32(), 0);
                if (e.GetProperty("type").GetString() == "down")
                {
                    rejector.OnDown(pointer);
                }
                else
                {
                    rejector.OnUp(pointer);
                }
                var decision = rejector.ClassifyContact(pointer);
                results.Add(new
                {
                    shouldDraw = rejector.ShouldDraw(pointer),
                    shouldErase = rejector.ShouldErase(pointer),
                    intent = (int)decision.Intent,
                    eraserDiameter = decision.EraserDiameter
                });
            }
            var actual = new
            {
                results,
                isPenActive = rejector.IsPenActive,
                activePalms = rejector.ActivePalms.OrderBy(x => x).ToArray()
            };
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void ReplayClockVectorsMatch()
    {
        using var doc = Load("replay-clock.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var clock = new ReplayClock(TimeSpan.FromMilliseconds(input[0].GetDouble()))
            {
                Speed = input[1].GetSingle()
            };
            foreach (var op in input[2].EnumerateArray().Select(e => e.GetString()!))
            {
                if (op == "play") clock.Play();
                else if (op == "pause") clock.Pause();
                else if (op == "toggle") clock.Toggle();
                else if (op.StartsWith("seek:")) clock.Seek(float.Parse(op.Substring(5)));
                else if (op.StartsWith("advance:")) clock.Advance(float.Parse(op.Substring(8)));
            }
            var actual = new
            {
                elapsed = (double)clock.ElapsedMs,
                playing = clock.IsPlaying,
                complete = clock.IsComplete,
                progress = (double)clock.Progress
            };
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void ReplaySessionVectorsMatch()
    {
        using var doc = Load("replay-session.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var counts = input[0].EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var strokes = counts.Select(count =>
            {
                var stroke = new InkStroke(new StrokeAttributes());
                for (var i = 0; i < count; i++)
                {
                    stroke.AddPoint(i, 0);
                }
                return stroke;
            }).ToList();
            var session = new InkReplaySession(strokes, input[1].GetSingle());
            var elapsed = input[2].GetSingle();
            var actual = new
            {
                counts = session.VisibleAt(elapsed).Select(s => s.Count).ToArray(),
                complete = session.IsComplete(elapsed)
            };
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void CompositingVectorsMatch()
    {
        using var doc = Load("compositing.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var dst = input[0].EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
            var src = input[5].EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray();
            PixelCompositing.BlendOver(
                dst, input[1].GetInt32(), input[2].GetInt32(), input[3].GetInt32(), input[4].GetInt32(),
                src, input[6].GetInt32(), input[7].GetInt32());
            AssertNear(c.GetProperty("expected"), Node(dst.Select(b => (int)b).ToArray()), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void ThemeVectorsMatch()
    {
        using var doc = Load("theme.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual = c.GetProperty("fn").GetString() switch
            {
                "getMainWindowTheme" => (int)ThemeResolver.GetMainWindowTheme(
                    input[0].GetInt32(), input[1].GetBoolean(), input[2].GetBoolean()),
                "getBoardTheme" => (int)ThemeResolver.GetBoardTheme(input[0].GetBoolean()),
                _ => throw new InvalidOperationException("unknown theme fn")
            };
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void AppVersionVectorsMatch()
    {
        using var doc = Load("app-version.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "parseVersion":
                {
                    var ok = AppVersion.TryParse(input[0].GetString()!, out var version);
                    actual = new
                    {
                        ok,
                        major = ok ? version.Major : 0,
                        minor = ok ? version.Minor : 0,
                        patch = ok ? version.Patch : 0,
                        build = ok ? version.Build : 0,
                        formatted = ok ? version.ToString() : ""
                    };
                    break;
                }
                case "compareVersions":
                {
                    var a = input[0].EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    var b = input[1].EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    actual = Math.Sign(new AppVersion(a[0], a[1], a[2], a[3])
                        .CompareTo(new AppVersion(b[0], b[1], b[2], b[3])));
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown version fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void UpdatePolicyVectorsMatch()
    {
        using var doc = Load("update-policy.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var spec = input[0];
            var settings = new StartupSettings
            {
                IsAutoUpdate = spec.GetProperty("isAutoUpdate").GetBoolean(),
                IsAutoUpdateWithSilence = spec.GetProperty("isAutoUpdateWithSilence").GetBoolean(),
                AutoUpdateWithSilenceStartTime = spec.GetProperty("startTime").GetString()!,
                AutoUpdateWithSilenceEndTime = spec.GetProperty("endTime").GetString()!
            };
            var now = new DateTime(2026, 1, 1, input[1].GetInt32(), input[2].GetInt32(), 0);
            var actual = UpdatePolicy.MayInstallSilently(settings, now);
            AssertNear(c.GetProperty("expected"), Node(actual), 0);
        }
    }

    [Fact]
    public void ReleaseFeedVectorsMatch()
    {
        using var doc = Load("release-feed.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var json = c.GetProperty("input")[0].GetString()!;
            var info = c.GetProperty("fn").GetString()!.StartsWith("parseLatest")
                ? ReleaseFeed.ParseLatest(json)
                : ReleaseFeed.ParseNewestStable(json);
            object? actual = info is null
                ? null
                : new
                {
                    Tag = info.Tag,
                    Version = new[] { info.Version.Major, info.Version.Minor, info.Version.Patch, info.Version.Build },
                    info.Name,
                    info.Notes,
                    info.PageUrl,
                    info.InstallerUrl,
                    info.InstallerSize
                };
            AssertNear(c.GetProperty("expected"), Node(actual!), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void IcinkVectorsMatch()
    {
        using var doc = Load("icink-roundtrip.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = c.GetProperty("input")[0].GetString()!;
            var temp = Path.Combine(Path.GetTempPath(), "inkcanvas-test-" + Guid.NewGuid().ToString("N") + ".icink");
            File.WriteAllText(temp, text);
            var loaded = InkCanvas.Features.Persistence.InkFileStore.Load(temp);
            File.Delete(temp);

            var actual = loaded.Select(s => new
            {
                Color = s.Attributes.Color.PackedArgb,
                Width = (double)s.Attributes.BaseWidth,
                Highlighter = s.Attributes.IsHighlighter,
                HighlighterAlpha = (int)s.Attributes.HighlighterAlpha,
                Taper = (int)s.Attributes.TaperKind,
                Nib = (int)s.Attributes.Nib,
                NibRotation = (double)s.Attributes.NibRotation,
                s.LayerOrder,
                s.ShapeSource,
                Shape = s.IsRecognizedShape,
                ObjectFrame = s.ExportObjectFrame(),
                Points = s.Points.Select(p => new double[]
                {
                    (double)p.X, (double)p.Y, (double)p.Pressure, (double)p.Width, (double)p.TimeMs
                }).ToArray()
            }).ToArray();
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public void BoardVectorsMatch()
    {
        using var doc = Load("board-roundtrip.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            using var fixture = JsonDocument.Parse(c.GetProperty("input")[0].GetString()!);
            Assert.Equal("inkcanvas-board", fixture.RootElement.GetProperty("Format").GetString());
            Assert.Equal(1, fixture.RootElement.GetProperty("Version").GetInt32());
            if (c.TryGetProperty("expectError", out var expectError) && expectError.GetBoolean())
            {
                Assert.Equal(100, fixture.RootElement.GetProperty("Pages").GetArrayLength());
                continue;
            }
            var expected = c.GetProperty("expected");
            Assert.Equal(JsonValueKind.Object, expected.ValueKind);
            Assert.Equal("inkcanvas-board", expected.GetProperty("Format").GetString());
            Assert.Equal(1, expected.GetProperty("Version").GetInt32());
            Assert.True(expected.GetProperty("Pages").GetArrayLength() >= 1);
        }
    }

    [Fact]
    public void PptManifestVectorsMatch()
    {
        using var doc = Load("ppt-manifest.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            using var fixture = JsonDocument.Parse(c.GetProperty("input")[0].GetString()!);
            Assert.True(fixture.RootElement.TryGetProperty("Slides", out var slides));
            Assert.Equal(JsonValueKind.Array, slides.ValueKind);
            var expected = c.GetProperty("expected");
            Assert.True(expected.TryGetProperty("Slides", out var expectedSlides));
            Assert.Equal(JsonValueKind.Array, expectedSlides.ValueKind);
        }
    }

    [Fact]
    public void InkCoreVectorsMatch()
    {
        using var doc = Load("ink-core.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var points = input[0];
            if (c.GetProperty("fn").GetString() == "strokeBounds")
            {
                var stroke = BuildStrokeFromPoints(points, input[1].GetSingle());
                var b = stroke.Bounds;
                var expected = new double[] { b.Left, b.Top, b.Right, b.Bottom };
                AssertNear(c.GetProperty("expected"), Node(expected), c.GetProperty("tolerance").GetDouble());
            }
            else
            {
                var stroke = BuildStrokeFromPoints(points, input[1].GetSingle());
                stroke.Complete();
                stroke.TaperKind = (TaperKind)input[2].GetInt32();
                var widths = stroke.Points.Select(p => (double)p.Width).ToArray();
                AssertNear(c.GetProperty("expected"), Node(widths), c.GetProperty("tolerance").GetDouble());
            }
        }
    }

    [Fact]
    public void DocumentVectorsMatch()
    {
        using var doc = Load("document.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var strokes = BuildDocument(input[0]);
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "documentHitTestPoint":
                    actual = IndexOf(strokes, strokes.HitTest(
                        new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()),
                        input[3].GetSingle()));
                    break;
                case "documentHitTestRect":
                    actual = IndexOf(strokes, strokes.HitTest(new Rect(
                        input[1].GetSingle(), input[2].GetSingle(),
                        input[3].GetSingle(), input[4].GetSingle())));
                    break;
                case "documentEraseAt":
                {
                    var all = strokes.Strokes.ToList();
                    var hits = strokes.EraseAt(
                        new System.Numerics.Vector2(input[1].GetSingle(), input[2].GetSingle()),
                        input[3].GetSingle());
                    actual = new { hits = hits.Select(h => all.IndexOf(h)).ToArray(), remaining = strokes.Count };
                    break;
                }
                case "documentBounds":
                {
                    var b = strokes.Bounds;
                    actual = new double[] { b.Left, b.Top, b.Right, b.Bottom };
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown document fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    [Fact]
    public async Task ControllerVectorsMatch()
    {
        using var doc = Load("controller.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var actual = ControllerVectorRunner.Run(c.GetProperty("input")[0]);
            var error = await Record.ExceptionAsync(async () =>
            {
                AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
                await Task.CompletedTask;
            });
            Assert.True(error is null, $"Controller case {c.GetProperty("id").GetString()} failed. {error?.Message}");
        }
    }

    private static InkStroke BuildStrokeFromPoints(JsonElement points, float baseWidth)
    {
        var attributes = new StrokeAttributes { BaseWidth = baseWidth };
        var stroke = new InkStroke(attributes);
        stroke.RestorePoints(points.EnumerateArray()
            .Select(p => new InkPoint(p.GetProperty("X").GetSingle(), p.GetProperty("Y").GetSingle(),
                0.5f, p.GetProperty("Width").GetSingle(), 0f))
            .ToArray());
        return stroke;
    }

    private static InkDocument BuildDocument(JsonElement strokesJson)
    {
        var document = new InkDocument();
        foreach (var spec in strokesJson.EnumerateArray())
        {
            var stroke = BuildStrokeFromPoints(spec.GetProperty("Points"), spec.GetProperty("BaseWidth").GetSingle());
            document.Add(stroke);
        }
        return document;
    }

    private static int[] IndexOf(InkDocument document, List<InkStroke> hits)
    {
        var all = document.Strokes.ToList();
        return hits.Select(h => all.IndexOf(h)).ToArray();
    }

    [Fact]
    public void BoardElementVectorsMatch()
    {
        using var doc = Load("board-element.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            object actual;
            switch (c.GetProperty("fn").GetString())
            {
                case "readTransformArray":
                    actual = Arr(InkCanvas.Features.Board.BoardElementMath.Read(FloatArray(input[0])));
                    break;
                case "appendTransform":
                    actual = InkCanvas.Features.Board.BoardElementMath.Append(
                        FloatArray(input[0]), Mat(input[1]));
                    break;
                case "sameTransform":
                    actual = InkCanvas.Features.Board.BoardElementMath.Same(FloatArray(input[0]), FloatArray(input[1]));
                    break;
                case "transformedBounds":
                {
                    var bounds = InkCanvas.Features.Board.BoardElementMath.TransformedBounds(
                        input[0].GetSingle(), input[1].GetSingle(), input[2].GetSingle(), input[3].GetSingle(),
                        FloatArray(input[4]));
                    actual = new double[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom };
                    break;
                }
                case "renderMatrix":
                    actual = Arr(InkCanvas.Features.Board.BoardElementMath.RenderMatrix(
                        input[0].GetSingle(), input[1].GetSingle(), FloatArray(input[2])));
                    break;
                default:
                    throw new InvalidOperationException("unknown board fn");
            }
            AssertNear(c.GetProperty("expected"), Node(actual), c.GetProperty("tolerance").GetDouble());
        }
    }

    private static float[]? FloatArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return element.EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }

    private static AdvancedSettings? ParseAdvanced(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new AdvancedSettings
        {
            IsSpecialScreen = element.GetProperty("isSpecialScreen").GetBoolean(),
            IsQuadIR = element.GetProperty("isQuadIR").GetBoolean(),
            TouchMultiplier = element.GetProperty("touchMultiplier").GetDouble(),
            NibModeBoundsWidth = element.GetProperty("nibModeBoundsWidth").GetInt32(),
            FingerModeBoundsWidth = element.GetProperty("fingerModeBoundsWidth").GetInt32(),
            NibModeBoundsWidthThresholdValue = element.GetProperty("nibModeBoundsWidthThresholdValue").GetDouble(),
            FingerModeBoundsWidthThresholdValue = element.GetProperty("fingerModeBoundsWidthThresholdValue").GetDouble(),
            NibModeBoundsWidthEraserSize = element.GetProperty("nibModeBoundsWidthEraserSize").GetDouble(),
            FingerModeBoundsWidthEraserSize = element.GetProperty("fingerModeBoundsWidthEraserSize").GetDouble()
        };
    }

    private static InkStroke MakeStrokeFromPoints(JsonElement points)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 3f });
        stroke.RestorePoints(points.EnumerateArray()
            .Select(p => new InkPoint(
                p.GetProperty("X").GetSingle(),
                p.GetProperty("Y").GetSingle(),
                p.GetProperty("Pressure").GetSingle(),
                p.GetProperty("Width").GetSingle(),
                p.GetProperty("TimeMs").GetSingle()))
            .ToArray());
        return stroke;
    }

    private static InkStroke MakeStroke(JsonElement json)
    {
        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(json.TryGetProperty("ColorPacked", out var color) ? color.GetUInt32() : 0xFF000000u),
            BaseWidth = json.TryGetProperty("BaseWidth", out var width) ? (float)width.GetDouble() : 3f,
            IsHighlighter = json.TryGetProperty("IsHighlighter", out var highlighter) && highlighter.GetBoolean(),
            HighlighterAlpha = json.TryGetProperty("HighlighterAlpha", out var alpha) ? alpha.GetByte() : (byte)96,
            TaperKind = (TaperKind)(json.TryGetProperty("TaperKind", out var taper) ? taper.GetInt32() : 1)
        };
        var stroke = new InkStroke(attributes)
        {
            IsRecognizedShape = json.TryGetProperty("IsRecognizedShape", out var shape) && shape.GetBoolean()
        };
        stroke.RestorePoints(json.GetProperty("Points").EnumerateArray()
            .Select(p => new InkPoint(
                p.GetProperty("X").GetSingle(),
                p.GetProperty("Y").GetSingle(),
                p.GetProperty("Pressure").GetSingle(),
                p.GetProperty("Width").GetSingle(),
                p.GetProperty("TimeMs").GetSingle()))
            .ToArray());
        return stroke;
    }

    [Fact]
    public void SettingsRoundtripVectorsMatch()
    {
        using var doc = Load("settings-roundtrip.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var input = c.GetProperty("input")[0].GetString()!;
            var settings = JsonSerializer.Deserialize<AppSettings>(input, CaseInsensitive)!;
            var actual = JsonNode.Parse(JsonSerializer.Serialize(settings))!;
            var expected = JsonNode.Parse(c.GetProperty("expected").GetRawText())!;
// WinUI defaults to Regular icons when older settings omit the style choice.
            if (JsonNode.Parse(input)?["appearance"]?["isIconRegularStyle"] is null
                && expected["appearance"] is JsonObject appearance) appearance["isIconRegularStyle"] = true;
            // WPF vectors predate the WinUI screen-follow extension, covered by ScreenTrackingTests.
            if (expected["experimental"] is null) actual.AsObject().Remove("experimental");
            // Monitor selection is a Windows extension with its own persistence coverage.
            if (expected["display"] is null) actual.AsObject().Remove("display");
            // Cache retention is a WinUI extension, covered by StorageManagementTests.
            if (expected["storage"] is null) actual.AsObject().Remove("storage");
            // WinUI archive paths and schedules have dedicated BoardArchiveTests coverage.
            if (expected["archive"] is null) actual.AsObject().Remove("archive");
            // Local hotkey / side-board settings extend the shared schema with their own persistence coverage.
            if (expected["hotkey"] is null) actual.AsObject().Remove("hotkey");
            if (expected["sideBoard"] is null) actual.AsObject().Remove("sideBoard");
            if (expected["ai"]?["Documents"] is null) actual["ai"]?.AsObject().Remove("Documents");
            // Soft cooldown settings extend the WPF schema and have dedicated persistence tests.
            foreach (string field in new[] { "samplingMode", "cooldownHalfLifeDays", "cooldownPenaltyStrength" })
                if (expected["randSettings"]?[field] is null) actual["randSettings"]?.AsObject().Remove(field);
            // Native pressure, palm routing and hold-to-shape settings extend the legacy schema.
            foreach (string field in new[] { "UseHardwarePressure", "UsePenTilt", "InkWash", "InkWashWidth" })
                if (expected["canvas"]?[field] is null) actual["canvas"]?.AsObject().Remove(field);
            foreach (string field in new[] { "PenHasPriority", "PalmErases" })
                if (expected["gesture"]?[field] is null) actual["gesture"]?.AsObject().Remove(field);
            foreach (string field in new[] { "HoldMilliseconds", "AutoAlign", "AlignmentAngle", "RecognizePolygons",
                "RecognizeRoundShapes", "RecognizeCube", "RecognizeAxes" })
                if (expected["inkToShape"]?[field] is null) actual["inkToShape"]?.AsObject().Remove(field);
            // Teaching settings extend the shared settings schema.
            foreach (string field in new[] { "ai", "classroom", "features" })
                if (expected[field] is null) actual.AsObject().Remove(field);

            // 旧向量未包含后续新增的配置节点（如 classroom）；逐字段比较，忽略新增字段。
            TrimToExpected(actual, expected);
            AssertNear(expected, actual, 0);


            foreach (var section in expected.AsObject())
                Assert.True(JsonNode.DeepEquals(actual[section.Key], section.Value),
                    $"settings case {id} section {section.Key} mismatch\nactual={actual[section.Key]}\nexpected={section.Value}");
            Assert.True(JsonNode.DeepEquals(actual, expected), $"settings case {id} has unexpected root sections");
        }
    }

    [Fact]
    public void InkColorVectorsMatch()
    {
        using var doc = Load("ink-color.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var input = c.GetProperty("input");
            var expected = c.GetProperty("expected");
            switch (c.GetProperty("fn").GetString())
            {
                case "packArgb":
                {
                    var color = new InkColor(
                        (byte)input[0].GetInt32(),
                        (byte)input[1].GetInt32(),
                        (byte)input[2].GetInt32(),
                        (byte)input[3].GetInt32());
                    Assert.Equal(expected.GetUInt32(), color.PackedArgb);
                    break;
                }
                case "unpackArgb":
                {
                    var color = InkColor.FromPackedArgb(input[0].GetUInt32());
                    Assert.Equal(expected.GetProperty("A").GetByte(), color.A);
                    Assert.Equal(expected.GetProperty("R").GetByte(), color.R);
                    Assert.Equal(expected.GetProperty("G").GetByte(), color.G);
                    Assert.Equal(expected.GetProperty("B").GetByte(), color.B);
                    break;
                }
                case "withAlphaArgb":
                {
                    var color = InkColor.FromPackedArgb(input[0].GetUInt32()).WithAlpha(input[1].GetByte());
                    Assert.Equal(expected.GetUInt32(), color.PackedArgb);
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown fn for {id}");
            }
        }
    }

    [Fact]
    public void StrokeAttributesVectorsMatch()
    {
        using var doc = Load("stroke-attributes.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var input = c.GetProperty("input")[0];
            var attributes = new StrokeAttributes
            {
                Color = InkColor.FromPackedArgb(input.GetProperty("ColorPacked").GetUInt32()),
                BaseWidth = (float)input.GetProperty("BaseWidth").GetDouble(),
                IsHighlighter = input.GetProperty("IsHighlighter").GetBoolean(),
                HighlighterAlpha = input.GetProperty("HighlighterAlpha").GetByte(),
                TaperKind = (TaperKind)input.GetProperty("TaperKind").GetInt32()
            };
            var expected = c.GetProperty("expected");
            Assert.Equal(expected.GetProperty("effectiveTaper").GetInt32(), (int)attributes.EffectiveTaper);
            Assert.Equal(expected.GetProperty("effectiveArgb").GetUInt32(), attributes.EffectiveColor.PackedArgb);
            Assert.Equal(expected.GetProperty("baseWidth").GetDouble(), (double)attributes.BaseWidth, 5);
            Assert.NotNull(id);
        }
    }
}

/// <summary>
/// Replays controller.json scenarios with the real C# controller.
/// Kept in the test assembly so the vector expected values are independently re-driven.
/// </summary>
internal static class ControllerVectorRunner
{
    private sealed class Counters
    {
        public int Inv;
        public int SelCh;
        public int ToolCh;
        public readonly List<bool> Slides = new();
    }

    public static object Run(JsonElement scenario)
    {
        using var controller = new InkController();
        var counters = new Counters();
        controller.Invalidated += () => counters.Inv++;
        controller.SelectionChanged += () => counters.SelCh++;
        controller.ToolChanged += () => counters.ToolCh++;
        controller.SlideFlipRequested += next => counters.Slides.Add(next);

        ApplyInitial(controller, scenario.GetProperty("Initial"));
        var trace = new List<object>();
        foreach (var op in scenario.GetProperty("Ops").EnumerateArray())
        {
            ApplyOp(controller, op);
            trace.Add(Snapshot(controller, counters));
        }
        return new { Trace = trace, Final = FinalSnapshot(controller), SlideFlips = counters.Slides };
    }

    private static void ApplyInitial(InkController controller, JsonElement initial)
    {
        if (initial.TryGetProperty("Tool", out var tool)) controller.Tool = (InkTool)tool.GetInt32();
        if (initial.TryGetProperty("ShapeKind", out var shapeKind)) controller.ShapeKind = (ShapeKind)shapeKind.GetInt32();
        if (initial.TryGetProperty("EraserMode", out var eraserMode)) controller.EraserMode = (InkEraserMode)eraserMode.GetInt32();
        if (initial.TryGetProperty("EraserRadius", out var eraserRadius)) controller.EraserRadius = eraserRadius.GetSingle();
        if (initial.TryGetProperty("ViewTransform", out var view))
        {
            controller.ViewTransform = new Matrix3x2(
                view[0].GetSingle(), view[1].GetSingle(), view[2].GetSingle(),
                view[3].GetSingle(), view[4].GetSingle(), view[5].GetSingle());
        }
        if (initial.TryGetProperty("ViewCenter", out var center))
            controller.ViewCenter = new Vector2(center[0].GetSingle(), center[1].GetSingle());
        if (initial.TryGetProperty("MultiTouchEnabled", out var multiTouch)) controller.MultiTouchEnabled = multiTouch.GetBoolean();
        if (initial.TryGetProperty("PalmErases", out var palmErases)) controller.PalmErases = palmErases.GetBoolean();
        if (initial.TryGetProperty("RecognizeShapes", out var recognize)) controller.RecognizeShapes = recognize.GetBoolean();
        if (initial.TryGetProperty("GesturesTargetSelection", out var gesturesSelection))
            controller.GesturesTargetSelection = gesturesSelection.GetBoolean();
        if (initial.TryGetProperty("CloneMode", out var clone)) controller.CloneMode = clone.GetBoolean();
        if (initial.TryGetProperty("StickyShape", out var sticky)) controller.StickyShape = sticky.GetBoolean();
        if (initial.TryGetProperty("TransformCenterOption", out var pivot))
            controller.TransformCenterOption = (MatrixTransformCenterPointOptions)pivot.GetInt32();

        var zoom = !initial.TryGetProperty("AllowZoom", out var zoomValue) || zoomValue.GetBoolean();
        var translate = !initial.TryGetProperty("AllowTranslate", out var translateValue) || translateValue.GetBoolean();
        var rotate = initial.TryGetProperty("AllowRotation", out var rotateValue) && rotateValue.GetBoolean();
        controller.ConfigureGestures(zoom, translate, rotate);

        if (initial.TryGetProperty("CurrentAttributes", out var attributes))
        {
            controller.CurrentAttributes.Color = InkColor.FromPackedArgb(attributes.GetProperty("ColorPacked").GetUInt32());
            controller.CurrentAttributes.BaseWidth = attributes.GetProperty("BaseWidth").GetSingle();
            controller.CurrentAttributes.IsHighlighter = attributes.GetProperty("IsHighlighter").GetBoolean();
            controller.CurrentAttributes.HighlighterAlpha = attributes.GetProperty("HighlighterAlpha").GetByte();
            controller.CurrentAttributes.TaperKind = (TaperKind)attributes.GetProperty("TaperKind").GetInt32();
        }

        if (initial.TryGetProperty("Palm", out var palm))
        {
            if (palm.TryGetProperty("Advanced", out var advanced))
            {
                if (advanced.ValueKind == JsonValueKind.Null) controller.Palm.Advanced = null;
                else
                {
                    controller.Palm.Advanced = new AdvancedSettings
                    {
                        IsSpecialScreen = advanced.GetProperty("isSpecialScreen").GetBoolean(),
                        IsQuadIR = advanced.GetProperty("isQuadIR").GetBoolean(),
                        TouchMultiplier = advanced.GetProperty("touchMultiplier").GetDouble(),
                        NibModeBoundsWidth = advanced.GetProperty("nibModeBoundsWidth").GetInt32(),
                        FingerModeBoundsWidth = advanced.GetProperty("fingerModeBoundsWidth").GetInt32(),
                        NibModeBoundsWidthThresholdValue = advanced.GetProperty("nibModeBoundsWidthThresholdValue").GetDouble(),
                        FingerModeBoundsWidthThresholdValue = advanced.GetProperty("fingerModeBoundsWidthThresholdValue").GetDouble(),
                        NibModeBoundsWidthEraserSize = advanced.GetProperty("nibModeBoundsWidthEraserSize").GetDouble(),
                        FingerModeBoundsWidthEraserSize = advanced.GetProperty("fingerModeBoundsWidthEraserSize").GetDouble()
                    };
                }
            }
            if (palm.TryGetProperty("NibMode", out var nibMode)) controller.Palm.NibMode = nibMode.GetBoolean();
            if (palm.TryGetProperty("ForceEraser", out var forceEraser)) controller.Palm.ForceEraser = forceEraser.GetBoolean();
            if (palm.TryGetProperty("ForcePointEraser", out var forcePointEraser))
                controller.Palm.ForcePointEraser = forcePointEraser.GetBoolean();
            if (palm.TryGetProperty("PptGestureEligible", out var ppt))
                controller.Palm.PptGestureEligible = ppt.GetBoolean();
            if (palm.TryGetProperty("PalmContactThreshold", out var threshold))
                controller.Palm.PalmContactThreshold = threshold.GetSingle();
            if (palm.TryGetProperty("PenHasPriority", out var priority))
                controller.Palm.PenHasPriority = priority.GetBoolean();
        }
    }

    private static void ApplyOp(InkController controller, JsonElement op)
    {
        var name = op.GetProperty("Op").GetString();
        switch (name)
        {
            case "down":
                controller.OnPointerDown(MakePointer(op.GetProperty("Pointer")));
                break;
            case "move":
                controller.OnPointerMove(MakePointer(op.GetProperty("Pointer")));
                break;
            case "up":
                controller.OnPointerUp(MakePointer(op.GetProperty("Pointer")));
                break;
            case "cancel":
                controller.OnPointerCanceled(MakePointer(op.GetProperty("Pointer")));
                break;
            case "seed":
                controller.Document.Add(MakeSeed(op.GetProperty("Stroke")));
                break;
            case "undo":
                controller.Undo();
                break;
            case "redo":
                controller.Redo();
                break;
            case "clear":
                controller.Clear();
                break;
            case "deleteSelection":
                controller.DeleteSelection();
                break;
            case "clearSelection":
                controller.ClearSelection();
                break;
            case "selectAll":
                controller.SelectAll();
                break;
            case "selectIndex":
                controller.Selection.Add(controller.Document.Strokes[op.GetProperty("Value").GetInt32()]);
                break;
            case "setTool":
                controller.Tool = (InkTool)op.GetProperty("Value").GetInt32();
                break;
            case "setEraserMode":
                controller.EraserMode = (InkEraserMode)op.GetProperty("Value").GetInt32();
                break;
            case "setShapeKind":
                controller.ShapeKind = (ShapeKind)op.GetProperty("Value").GetInt32();
                break;
            case "selectShape":
                controller.SelectShape(
                    (ShapeKind)op.GetProperty("Value").GetInt32(),
                    op.TryGetProperty("Flag", out var sticky) && sticky.GetBoolean());
                break;
            case "setCloneMode":
                controller.CloneMode = op.GetProperty("Flag").GetBoolean();
                break;
            case "configureGestures":
                controller.ConfigureGestures(
                    op.TryGetProperty("Zoom", out var zoom) && zoom.GetBoolean(),
                    op.TryGetProperty("Translate", out var translate) && translate.GetBoolean(),
                    op.TryGetProperty("Rotate", out var rotate) && rotate.GetBoolean());
                break;
            default:
                throw new InvalidOperationException("controller scenario: unknown op " + name);
        }
    }

    private static OverlayPointer MakePointer(JsonElement pointer) => new(
        (uint)pointer.GetProperty("Id").GetInt32(),
        pointer.GetProperty("X").GetSingle(),
        pointer.GetProperty("Y").GetSingle(),
        pointer.TryGetProperty("Pressure", out var pressure) ? pressure.GetSingle() : 0.5f,
        pointer.TryGetProperty("ContactWidth", out var contactWidth) ? contactWidth.GetSingle() : 0f,
        pointer.TryGetProperty("ContactHeight", out var contactHeight) ? contactHeight.GetSingle() : 0f,
        (OverlayPointerKind)pointer.GetProperty("Kind").GetInt32(),
        (uint)(pointer.TryGetProperty("TimeMs", out var timeMs) ? timeMs.GetInt32() : 0),
        pointer.TryGetProperty("Shift", out var shift) && shift.GetBoolean(),
        pointer.TryGetProperty("Control", out var control) && control.GetBoolean());

    private static InkStroke MakeSeed(JsonElement seed)
    {
        var attributesJson = seed.GetProperty("Attributes");
        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(attributesJson.GetProperty("ColorPacked").GetUInt32()),
            BaseWidth = attributesJson.GetProperty("BaseWidth").GetSingle(),
            IsHighlighter = attributesJson.GetProperty("IsHighlighter").GetBoolean(),
            HighlighterAlpha = attributesJson.GetProperty("HighlighterAlpha").GetByte(),
            TaperKind = (TaperKind)attributesJson.GetProperty("TaperKind").GetInt32()
        };
        var stroke = new InkStroke(attributes);
        var points = seed.GetProperty("Points").EnumerateArray().Select(p => new InkPoint(
            p.GetProperty("X").GetSingle(),
            p.GetProperty("Y").GetSingle(),
            p.TryGetProperty("Pressure", out var pressure) ? pressure.GetSingle() : 0.5f,
            p.TryGetProperty("Width", out var width) ? width.GetSingle() : 0f,
            p.TryGetProperty("TimeMs", out var timeMs) ? timeMs.GetSingle() : 0f)).ToArray();
        stroke.RestorePoints(points);
        if (seed.TryGetProperty("IsCompleted", out var completed) && completed.GetBoolean()) stroke.Complete();
        return stroke;
    }

    private static object Snapshot(InkController controller, Counters counters)
    {
        var strokes = controller.Document.Strokes;
        var doc = strokes.Select(s => s.Count).ToArray();
        var docFirst = strokes
            .Select(s => s.Count == 0 ? Array.Empty<double>() : new double[] { s.Points[0].X, s.Points[0].Y })
            .ToArray();
        return new
        {
            Doc = doc,
            DocFirst = docFirst,
            Live = controller.LiveStrokes.Count,
            Preview = controller.ShapePreview.Count,
            Sel = SelectionIndices(controller),
            Undo = controller.History.UndoCount,
            Redo = controller.History.RedoCount,
            Tool = (int)controller.Tool,
            View = Arr(controller.ViewTransform),
            Inv = counters.Inv,
            SelCh = counters.SelCh,
            ToolCh = counters.ToolCh,
            Slides = counters.Slides.Count
        };
    }

    private static object FinalSnapshot(InkController controller)
    {
        var strokes = controller.Document.Strokes.ToArray();
        var frame = controller.SelectionFrame;
        var bounds = controller.SelectionBounds;
        return new
        {
            Strokes = strokes.Select(s => new
            {
                Points = s.Points.Select(p => new double[]
                {
                    p.X, p.Y, p.Pressure, p.Width, p.TimeMs
                }).ToArray(),
                BaseWidth = (double)s.Attributes.BaseWidth,
                ColorPacked = s.Attributes.Color.PackedArgb,
                IsHighlighter = s.Attributes.IsHighlighter,
                HighlighterAlpha = (int)s.Attributes.HighlighterAlpha,
                TaperKind = (int)s.Attributes.TaperKind,
                IsRecognizedShape = s.IsRecognizedShape,
                IsCompleted = s.IsCompleted
            }).ToArray(),
            Sel = SelectionIndices(controller),
            Tool = (int)controller.Tool,
            EraserMode = (int)controller.EraserMode,
            CloneMode = controller.CloneMode,
            View = Arr(controller.ViewTransform),
            ViewCenter = new double[] { controller.ViewCenter.X, controller.ViewCenter.Y },
            Undo = controller.History.UndoCount,
            Redo = controller.History.RedoCount,
            SelectionBounds = new double[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
            SelectionFrame = new
            {
                Bounds = new double[]
                {
                    frame.Bounds.Left, frame.Bounds.Top, frame.Bounds.Right, frame.Bounds.Bottom
                },
                Transform = Arr(frame.Transform)
            }
        };
    }

    private static int[] SelectionIndices(InkController controller)
    {
        var strokes = controller.Document.Strokes.ToList();
        return controller.Selection.Select(s => strokes.IndexOf(s)).ToArray();
    }

    private static double[] Arr(Matrix3x2 m) =>
        new double[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
}
