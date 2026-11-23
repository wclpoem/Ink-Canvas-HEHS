using InkCanvas.Controls.Theming;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace InkCanvas.Controls.Controls;

/// <summary>One cell of the WPF shape popup: kind, label, resource key, slot metrics.</summary>
public sealed record ShapeMenuItem(
    ShapeKind Kind,
    string Label,
    string IconKey,
    Thickness Margin,
    double SlotWidth = 0,
    bool LongPress = false,
    double Rotation = 0);

public sealed record ShapeMenuCategory(string Name, ShapeMenuItem[][] Rows);

/// <summary>Shape choices shared by the desktop and board menus.</summary>
public static class ShapeMenuCatalog
{
    public const string Title = "图形（长按可连续绘制）";
    private static ShapeMenuItem Item(ShapeKind kind, string label, bool sticky = true, double rotation = 0)
        => new(kind, label, kind.ToString(), new Thickness(8, 10, 8, 10), SlotWidth: 30, LongPress: sticky, Rotation: rotation);
    public static readonly ShapeMenuCategory[] Categories =
    {
        new ShapeMenuCategory("线条与坐标", new[]
        {
            new[] { Item(ShapeKind.Line, "直线"), Item(ShapeKind.DashedLine, "虚线"),
                Item(ShapeKind.DotLine, "点线"), Item(ShapeKind.Arrow, "箭头"), Item(ShapeKind.ParallelLine, "平行线") },
            new[] { Item(ShapeKind.CoordinateBoth, "坐标系（双向，中心原点）"),
                Item(ShapeKind.CoordinateXOneWay, "坐标系（x 单向）"), Item(ShapeKind.CoordinateYOneWay, "坐标系（y 单向）") },
            new[] { Item(ShapeKind.CoordinateFirstQuadrant, "坐标系（第一象限）"), Item(ShapeKind.Coordinate3D, "空间坐标系"),
                Item(ShapeKind.CoordinateTwoPoint, "坐标系（两点）") },
        }),
        new ShapeMenuCategory("平面", new[]
        {
            new[] { Item(ShapeKind.Rectangle, "矩形（两点）"), Item(ShapeKind.Square, "正方形"), Item(ShapeKind.Circle, "圆（两点）"),
                Item(ShapeKind.Ellipse, "椭圆（两点）"), Item(ShapeKind.Triangle, "三角形") },
            new[] { Item(ShapeKind.RectangleFromCenter, "中心矩形"), Item(ShapeKind.CircleFromCenter, "中心圆"),
                Item(ShapeKind.EllipseFromCenter, "中心椭圆"), Item(ShapeKind.EllipseWithFoci, "椭圆（两点，含焦点）"), Item(ShapeKind.EllipseWithFociFromCenter, "中心椭圆（含焦点）") },
            new[] { Item(ShapeKind.DashedCircle, "虚线圆（两点）"), Item(ShapeKind.DashedCircleFromCenter, "中心虚线圆"),
                Item(ShapeKind.RightTriangle, "直角三角形"), Item(ShapeKind.Diamond, "菱形"), Item(ShapeKind.Parallelogram, "平行四边形") },
            new[] { Item(ShapeKind.Trapezoid, "梯形") },
        }),
        new ShapeMenuCategory("曲线", new[]
        {
            new[] { Item(ShapeKind.Hyperbola, "双曲线"), Item(ShapeKind.HyperbolaWithFoci, "双曲线（含焦点）"),
                Item(ShapeKind.HyperbolaWithAsymptotes, "双曲线（含渐近线）"), Item(ShapeKind.HyperbolaWithFociAndAsymptotes, "双曲线（含焦点和渐近线）") },
            new[] { Item(ShapeKind.ParabolaUp, "抛物线（纵向）"), Item(ShapeKind.ParabolaRight, "抛物线（横向）", rotation: 90),
                Item(ShapeKind.ParabolaUpWithFoci, "抛物线（纵向，含焦点）"), Item(ShapeKind.ParabolaWithFoci, "抛物线（横向，含焦点）", rotation: 90) },
        }),
        new ShapeMenuCategory("立体", new[]
        {
            new[] { Item(ShapeKind.Cylinder, "圆柱"), Item(ShapeKind.Cone, "圆锥"), Item(ShapeKind.ConeFrustum, "圆台"),
                Item(ShapeKind.Sphere, "球体"), Item(ShapeKind.Hemisphere, "半球") },
            new[] { Item(ShapeKind.Cuboid, "长方体（两笔）"), Item(ShapeKind.Cube, "正方体（两笔）"),
                Item(ShapeKind.TriangularPrism, "三棱柱（两笔）"), Item(ShapeKind.PentagonalPrism, "五棱柱（两笔）"), Item(ShapeKind.HexagonalPrism, "六棱柱（两笔）") },
            new[] { Item(ShapeKind.Tetrahedron, "四面体"), Item(ShapeKind.SquarePyramid, "四棱锥（两笔）") },
        }),
    };
    public static readonly ShapeMenuItem[][] Rows = Categories.SelectMany(category => category.Rows).ToArray();
    public static readonly double[] RowSpacings = new double[Rows.Length];
}

/// <summary>
/// Vector previews ported 1:1 from upstream Resources/DrawShapeImageDictionary.xaml
/// (the board dictionary is byte-identical after brush substitution). Each icon keeps its
/// original ~600-870-unit canvas; pens use round caps and thickness-multiple dashes, which
/// WinUI StrokeDashArray shares, so dash values port verbatim. All strokes reference one
/// shared foreground brush (WPF DynamicResource FloatBarForeground/BoardBarForeground).
/// </summary>
internal static class ShapeIcons
{
    public static Canvas Create(ShapeKind kind, Brush foreground)
    {
        if (kind is >= ShapeKind.Cube and <= ShapeKind.ConeFrustum)
        {
            var strokes = ShapeGenerator.IsTwoStepSolid(kind)
                ? ShapeGenerator.GenerateSolid(kind, new(35, 100), new(205, 245), kind == ShapeKind.SquarePyramid ? new(-20, -135) : new(65, -65), new() { TaperKind = InkCanvas.Core.Ink.TaperKind.None })
                : ShapeGenerator.Generate(kind, new(35, 35), new(265, 265), new() { TaperKind = InkCanvas.Core.Ink.TaperKind.None });
            var canvas = New(300, 300);
            foreach (var stroke in strokes)
            {
                var points = new PointCollection();
                foreach (var point in stroke.Points) points.Add(new Point(point.X, point.Y));
                // The legacy 800-DIP icon canvases use a 50-DIP line and scale to a
                // 30-DIP cell. Match that ~1.9-DIP rendered weight on this 300-DIP canvas.
                canvas.Children.Add(new Polyline { Points = points, Stroke = foreground, StrokeThickness = 18.75,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Miter });
            }
            return canvas;
        }
        if (kind is ShapeKind.Square or ShapeKind.Triangle or ShapeKind.RightTriangle or ShapeKind.Diamond
            or ShapeKind.Parallelogram or ShapeKind.Trapezoid or ShapeKind.Tetrahedron)
        {
            var canvas = New(800, 800);
            var strokes = ShapeGenerator.Generate(kind, new System.Numerics.Vector2(70, 70), new System.Numerics.Vector2(730, 730),
                new InkCanvas.Core.Ink.StrokeAttributes { TaperKind = InkCanvas.Core.Ink.TaperKind.None });
            foreach (var stroke in strokes)
                for (int i = 1; i < stroke.Count; i++)
                    L(canvas, foreground, stroke.Points[i - 1].X, stroke.Points[i - 1].Y, stroke.Points[i].X, stroke.Points[i].Y, pen: 40);
            return canvas;
        }
        switch (kind)
        {
            case ShapeKind.Line:
                {
                    var c = New(750.75, 750.75);
                    L(c, foreground, 25, 725.75, 725.75, 25);
                    return c;
                }
            case ShapeKind.DashedLine:
                {
                    var c = New(750.75, 750.75);
                    L(c, foreground, 25, 725.75, 60.36, 690.39);
                    L(c, foreground, 130.36, 620.39, 655.39, 95.36, dash: new[] { 1.98, 1.98 });
                    L(c, foreground, 690.39, 60.36, 725.75, 25);
                    return c;
                }
            case ShapeKind.DotLine:
                {
                    var c = New(749.84, 749.84);
                    L(c, foreground, 30, 719.84, 719.84, 30, pen: 60, dash: new[] { 0.0166666666666667, 2 });
                    return c;
                }
            case ShapeKind.Arrow:
                {
                    var c = New(753.64, 751.14);
                    L(c, foreground, 25, 726.14, 725.75, 25.39);
                    L(c, foreground, 725.62, 25, 375.38, 132.77);
                    L(c, foreground, 615.38, 375.77, 728.64, 25.47);
                    return c;
                }
            case ShapeKind.ParallelLine:
                {
                    var c = New(755.38, 698.08);
                    L(c, foreground, 25.42, 25, 726.79, 25);
                    L(c, foreground, 25, 241.08, 726.38, 241.08);
                    L(c, foreground, 29, 673.08, 730.38, 673.08);
                    L(c, foreground, 25, 457.08, 726.38, 457.08);
                    return c;
                }
            case ShapeKind.CoordinateBoth:
                {
                    var c = New(813.58, 813.58);
                    CoordX(c, foreground, 457.61);
                    CoordY(c, foreground, 357.61);
                    return c;
                }
            case ShapeKind.CoordinateXOneWay:
                {
                    var c = New(813.58, 813.58);
                    CoordX(c, foreground, 457.61);
                    CoordY(c, foreground, 157.61);
                    return c;
                }
            case ShapeKind.CoordinateYOneWay:
                {
                    var c = New(813.58, 813.58);
                    CoordX(c, foreground, 657.61);
                    CoordY(c, foreground, 357.61);
                    return c;
                }
            case ShapeKind.CoordinateFirstQuadrant:
                {
                    var c = New(813.58, 813.58);
                    CoordX(c, foreground, 657.61);
                    CoordY(c, foreground, 157.61);
                    return c;
                }
            case ShapeKind.Coordinate3D:
                {
                    var c = New(796.54, 798.42);
                    L(c, foreground, 290.48, 508.88, 770.01, 508.88);
                    L(c, foreground, 770.6, 508.42, 601.29, 418.79);
                    L(c, foreground, 600.18, 597.33, 771.54, 509.71);
                    L(c, foreground, 290.3, 509.06, 290.3, 26.53);
                    L(c, foreground, 289.84, 25.94, 200.21, 195.25);
                    L(c, foreground, 378.75, 196.36, 291.13, 25);
                    L(c, foreground, 290.39, 508.97, 26.67, 772.68);
                    L(c, foreground, 26.58, 773.42, 209.68, 717.08);
                    L(c, foreground, 84.21, 590.05, 25, 773.18);
                    return c;
                }
            case ShapeKind.CoordinateTwoPoint:
                {
                    var c = New(825, 825);
                    L(c, foreground, 125, 670, 775, 670);
                    L(c, foreground, 775, 670, 600, 580);
                    L(c, foreground, 775, 670, 600, 760);
                    L(c, foreground, 125, 670, 125, 50);
                    L(c, foreground, 125, 50, 35, 225);
                    L(c, foreground, 125, 50, 215, 225);
                    return c;
                }
            case ShapeKind.Rectangle:
                {
                    var c = New(753.21, 750.96);
                    RectEdges(c, foreground);
                    return c;
                }
            case ShapeKind.RectangleFromCenter:
                {
                    var c = New(753.21, 750.96);
                    RectEdges(c, foreground);
                    L(c, foreground, 376.56, 375.53, 376.66, 375.43, pen: 150);
                    return c;
                }
            case ShapeKind.Ellipse:
                {
                    var c = New(825, 649.3);
                    E(c, foreground, 412.5, 324.65, 387.5, 299.65);
                    return c;
                }
            case ShapeKind.Circle:
            case ShapeKind.CircleFromCenter:
                {
                    var c = New(825, 825);
                    E(c, foreground, 412.5, 412.5, 387.5, 387.5);
                    if (kind is ShapeKind.CircleFromCenter or ShapeKind.DashedCircleFromCenter)
                        L(c, foreground, 412.45, 412.55, 412.55, 412.45, pen: 150);
                    return c;
                }
            case ShapeKind.DashedCircle:
            case ShapeKind.DashedCircleFromCenter:
                {
                    var c = New(825, 825);
                    E(c, foreground, 412.5, 412.5, 387.5, 387.5, dash: new[] { 2.029, 2.029 });
                    if (kind is ShapeKind.CircleFromCenter or ShapeKind.DashedCircleFromCenter)
                        L(c, foreground, 412.45, 412.55, 412.55, 412.45, pen: 150);
                    return c;
                }
            case ShapeKind.EllipseFromCenter:
                {
                    var c = New(825, 649.3);
                    E(c, foreground, 412.5, 324.65, 387.5, 299.65);
                    L(c, foreground, 412.45, 324.7, 412.55, 324.6, pen: 150);
                    return c;
                }
            case ShapeKind.EllipseWithFoci:
            case ShapeKind.EllipseWithFociFromCenter:
                {
                    var c = New(825, 649.3);
                    E(c, foreground, 412.5, 324.65, 387.5, 299.65);
                    if (kind == ShapeKind.EllipseWithFociFromCenter) L(c, foreground, 412.45, 324.7, 412.55, 324.6, pen: 150);
                    L(c, foreground, 212.45, 324.7, 212.55, 324.6, pen: 75);
                    L(c, foreground, 612.45, 324.7, 612.55, 324.6, pen: 75);
                    return c;
                }
            case ShapeKind.Hyperbola:
            case ShapeKind.HyperbolaWithFoci:
            case ShapeKind.HyperbolaWithAsymptotes:
            case ShapeKind.HyperbolaWithFociAndAsymptotes:
                {
                    var c = New(777.7, 772.73);
                    const double tx = -111.15, ty = -113.63;
                    if (ShapeGenerator.HasHyperbolaAsymptotes(kind))
                    {
                        L(c, foreground, 25, 25, 750, 748, pen: 25, dash: new[] { 3.0, 3.0 });
                        L(c, foreground, 25, 748, 750, 25, pen: 25, dash: new[] { 3.0, 3.0 });
                    }
                    P(c, foreground, "M136.54,861.37A395.1,395.1,0,0,0,216.54,814.37C254.61,785.46,277.79,756.9,284.54,748.37A351.08,351.08,0,0,0,330.54,672.37C345.4,638.21,350.64,610.37,355.54,583.37A523.22,523.22,0,0,0,363.85,500.5", 57, tx, ty);
                    P(c, foreground, "M136.15,138.63A395.1,395.1,0,0,1,216.15,185.63C254.22,214.54,277.41,243.1,284.15,251.63A350.65,350.65,0,0,1,330.15,327.63C345.01,361.79,350.25,389.63,355.15,416.63A522,522,0,0,1,363.46,499.5", 57, tx, ty);
                    P(c, foreground, "M863.46,861.37A395.1,395.1,0,0,1,783.46,814.37C745.39,785.46,722.21,756.9,715.46,748.37A351.08,351.08,0,0,1,669.46,672.37C654.6,638.21,649.36,610.37,644.46,583.37A523.22,523.22,0,0,1,636.15,500.5", 57, tx, ty);
                    P(c, foreground, "M863.85,138.63A395.1,395.1,0,0,0,783.85,185.63C745.78,214.54,722.59,243.1,715.85,251.63A350.65,350.65,0,0,0,669.85,327.63C654.99,361.79,649.75,389.63,644.85,416.63A522,522,0,0,0,636.54,499.5", 57, tx, ty);
                    if (ShapeGenerator.HasHyperbolaFoci(kind))
                    {
                        L(c, foreground, 108.8, 386.42, 108.9, 386.32, pen: 85);
                        L(c, foreground, 668.8, 386.42, 668.9, 386.32, pen: 85);
                    }
                    return c;
                }
            case ShapeKind.ParabolaUp:
            case ShapeKind.ParabolaRight:
            case ShapeKind.ParabolaWithFoci:
            case ShapeKind.ParabolaUpWithFoci:
                {
                    var c = New(684.27, 635.2);
                    const double tx = -157.87, ty = -182.4;
                    P(c, foreground, "M182.87,207.4C191.75,296.78 210.54,423.16 252.87,553.03 260.15,575.33 290.28,665.09 352.09,727.67 378.68,754.59 401.88,767.11 413.96,772.86A203.34,203.34,0,0,0,496.84,792.4", 50, tx, ty);
                    P(c, foreground, "M817.13,207.6C808.25,296.98 789.46,423.36 747.13,553.23 739.85,575.53 709.72,665.29 647.91,727.87 621.32,754.79 598.12,767.31 586.04,773.06A203.34,203.34,0,0,1,503.16,792.6", 50, tx, ty);
                    if (kind is ShapeKind.ParabolaWithFoci or ShapeKind.ParabolaUpWithFoci)
                        L(c, foreground, 342.08, 417.65, 342.18, 417.55, pen: 75);
                    return c;
                }
            case ShapeKind.Cylinder:
                {
                    var c = New(579.5, 850);
                    E(c, foreground, 290, 125, 264.5, 100);
                    BottomArcs(c, foreground, -210, -75);
                    L(c, foreground, 25, 125, 25, 725);
                    L(c, foreground, 554, 125, 554, 725);
                    return c;
                }
            case ShapeKind.Cone:
                {
                    var c = New(579.5, 750);
                    BottomArcs(c, foreground, -210, -175);
                    L(c, foreground, 290, 25, 554, 625);
                    L(c, foreground, 25, 625, 290, 25);
                    return c;
                }
            case ShapeKind.Cuboid:
                {
                    var c = New(801.77, 798.2);
                    L(c, foreground, 25.23, 224.28, 572.99, 224.67);
                    L(c, foreground, 25.23, 772.23, 25.23, 224.47);
                    L(c, foreground, 575.5, 772.79, 575.5, 225.03);
                    L(c, foreground, 26.01, 772.23, 573.77, 772.62);
                    L(c, foreground, 25, 225, 225, 25);
                    L(c, foreground, 575.75, 225.25, 775.75, 25.25);
                    L(c, foreground, 224.5, 25.5, 775.5, 25.5);
                    L(c, foreground, 576.77, 773.2, 776.77, 573.2);
                    L(c, foreground, 775.75, 25.5, 776.25, 573.5);
                    // 隐藏棱：实线端头 + 虚线中段
                    L(c, foreground, 223.77, 573.08, 223.77, 523.08);
                    L(c, foreground, 223.77, 433.53, 223.77, 120.1, dash: new[] { 1.791, 1.791 });
                    L(c, foreground, 223.77, 75.32, 223.77, 25.32);
                    L(c, foreground, 25.77, 772.2, 61.12, 736.85);
                    L(c, foreground, 104.22, 693.75, 168.86, 629.1, dash: new[] { 1.219, 1.219 });
                    L(c, foreground, 190.41, 607.56, 225.77, 572.2);
                    L(c, foreground, 223.89, 572.01, 273.89, 572.04);
                    L(c, foreground, 363.44, 572.11, 676.87, 572.33, dash: new[] { 1.791, 1.791 });
                    L(c, foreground, 721.65, 572.36, 771.65, 572.4);
                    return c;
                }
            default:
                return New(32, 32);
        }
    }

    private static Canvas New(double w, double h) => new() { Width = w, Height = h };

    private static void L(Canvas c, Brush fg, double x1, double y1, double x2, double y2,
        double pen = 50, double[] dash = null)
    {
        var line = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = fg,
            StrokeThickness = pen,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Miter,
        };
        if (dash is not null)
        {
            line.StrokeDashArray = Dash(dash);
            line.StrokeDashCap = PenLineCap.Round;
        }
        c.Children.Add(line);
    }

    private static void E(Canvas c, Brush fg, double cx, double cy, double rx, double ry,
        double pen = 50, double[] dash = null)
    {
        var ellipse = new Ellipse
        {
            Width = rx * 2, Height = ry * 2,
            Stroke = fg,
            StrokeThickness = pen,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Miter,
        };
        if (dash is not null)
        {
            ellipse.StrokeDashArray = Dash(dash);
            ellipse.StrokeDashCap = PenLineCap.Round;
        }
        Canvas.SetLeft(ellipse, cx - rx);
        Canvas.SetTop(ellipse, cy - ry);
        c.Children.Add(ellipse);
    }

    /// <summary>Attribute-path figure (WPF Geometry='F1 M…z M0,0z M…' with the degenerate
    /// bounding figures stripped) inside a translated DrawingGroup.</summary>
    private static DoubleCollection Dash(double[] values)
    {
        var dc = new DoubleCollection();
        foreach (double v in values) dc.Add(v);
        return dc;
    }

    private static void P(Canvas c, Brush fg, string data, double pen, double tx, double ty,
        double[] dash = null)
    {
        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
            "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"" + data + "\"/>");
        path.Stroke = fg;
        path.StrokeThickness = pen;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Miter;
        if (dash is not null)
        {
            path.StrokeDashArray = Dash(dash);
            path.StrokeDashCap = PenLineCap.Round;
        }
        Canvas.SetLeft(path, tx);
        Canvas.SetTop(path, ty);
        c.Children.Add(path);
    }

    private static void RectEdges(Canvas c, Brush fg)
    {
        L(c, fg, 25, 25, 725, 25.5);
        L(c, fg, 25, 725.25, 25, 25.25);
        L(c, fg, 728.21, 725.96, 728.21, 25.96);
        L(c, fg, 26, 725.25, 726, 725.75);
    }

    private static void CoordX(Canvas c, Brush fg, double y)
    {
        L(c, fg, 25, y, 787.05, y);
        L(c, fg, 787.64, y - 0.46, 618.33, y - 90.09);
        L(c, fg, 617.22, y + 88.45, 788.58, y + 0.83);
    }

    private static void CoordY(Canvas c, Brush fg, double x)
    {
        L(c, fg, x, 788.58, x, 26.53);
        L(c, fg, x - 0.46, 25.94, x - 90.09, 195.25);
        L(c, fg, x + 88.45, 196.36, x + 0.83, 25);
    }

    /// <summary>圆柱/圆锥共用的底面椭圆：前半实线、后半虚线（2.0084），左右实线端头。</summary>
    private static void BottomArcs(Canvas c, Brush fg, double tx, double ty)
    {
        P(c, fg, "M764.5,800C764.5,855.23 646.08,900 500,900 353.92,900 235.5,855.23 235.5,800", 50, tx, ty);
        P(c, fg, "M235.5,800C235.5,785,244.17,770.87,259.72,758.14", 50, tx, ty);
        P(c, fg, "M350.75,717.43C393.23,706.43 444.63,700 500,700 578.18,700 648.44,712.82 696.87,733.21", 50, tx, ty, dash: new[] { 2.0084, 2.0084 });
        P(c, fg, "M740.28,758.14C755.83,770.87,764.5,785.05,764.5,800", 50, tx, ty);
    }
}

/// <summary>Shared shape menu with tap selection, hold-to-repeat and persistent pin state.</summary>
public sealed class ShapePreview : UserControl
{
    private const double RowHeight = 50;
    private const double RowOverlap = -8;

    private readonly InkController _controller;
    public PopupHeader Header { get; }
    private readonly DispatcherQueueTimer _holdTimer;
    private readonly System.Collections.Generic.List<Grid> _cells = new();
    private readonly SegmentedSelector _categories;
    private readonly StackPanel _categoryBodies = new() { Width = 230 };
    private readonly bool[] _createdCategories = new bool[ShapeMenuCatalog.Categories.Length];
    private readonly Brush _foreground;
    private readonly ShapeMenuItem[] _items = ShapeMenuCatalog.Rows.SelectMany(row => row).ToArray();
    private int _selectedCategory;
    private Grid _pressedCell;
    private bool _holdFired;
    private uint? _pressedPointer;
    private Point _pressPosition;
    private bool _autoHide = true;
    public static int AllCategoryIndex => ShapeMenuCatalog.Categories.Length;
    private bool ShowingAll => _selectedCategory == AllCategoryIndex;
    private static double CategoryHeight(ShapeMenuCategory category) => category.Rows.Length * RowHeight
        + Math.Max(0, category.Rows.Length - 1) * RowOverlap;
    private static double StandardBodyHeight => ShapeMenuCatalog.Categories.Max(CategoryHeight);

    /// <summary>Raised after a tap or long press selects a shape.</summary>
    public event Action<ShapeKind, bool> SelectionMade;

    /// <summary>Raised when the pin toggles auto-hide, so hosts can persist the state.</summary>
    public event Action AutoHideChanged;

    public int ShapeCount => _items.Length;
    public IReadOnlyList<Grid> VisibleCells => _cells.Where(cell => cell.Tag is ShapeMenuItem item
        && (ShowingAll || ShapeMenuCatalog.Categories[_selectedCategory].Rows.SelectMany(row => row).Contains(item)))
        .OrderBy(cell => Array.IndexOf(_items, (ShapeMenuItem)cell.Tag)).ToArray();
    public Grid GetCell(ShapeKind kind)
    {
        int index = Array.FindIndex(ShapeMenuCatalog.Categories,
            category => category.Rows.SelectMany(row => row).Any(item => item.Kind == kind));
        if (index >= 0) EnsureCategory(index);
        return _cells.Single(cell => ((ShapeMenuItem)cell.Tag).Kind == kind);
    }

    public void SelectCategoryFor(ShapeKind kind)
    {
        if (ShowingAll) return;
        int index = Array.FindIndex(ShapeMenuCatalog.Categories,
            category => category.Rows.SelectMany(row => row).Any(item => item.Kind == kind));
        if (index >= 0) _categories.SelectedIndex = index;
    }

    public void SelectCategory(int index)
    {
        if (index < 0 || index > AllCategoryIndex) return;
        _selectedCategory = index;
        if (ShowingAll)
            for (int category = 0; category < ShapeMenuCatalog.Categories.Length; category++) EnsureCategory(category);
        else EnsureCategory(index);
        for (int i = 0; i < _categoryBodies.Children.Count; i++)
            _categoryBodies.Children[i].Visibility = ShowingAll || i == index ? Visibility.Visible : Visibility.Collapsed;
        _categoryBodies.Height = ShowingAll ? ShapeMenuCatalog.Categories.Sum(CategoryHeight) : StandardBodyHeight;
        if (_categories.SelectedIndex != index) _categories.SelectedIndex = index;
    }

    public ShapePreview(InkController controller, int theme, bool boardMode = false)
    {
        _controller = controller;
        var fg = PopupBrushes.Foreground(theme);
        _foreground = fg;
        bool dark = PopupBrushes.IsDark(theme);
        _holdTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(500);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += OnHoldTick;

        Header = new PopupHeader("图形", theme);
        Header.PinChanged += () => { _autoHide = !Header.IsPinned; AutoHideChanged?.Invoke(); };

        _categories = new SegmentedSelector(ShapeMenuCatalog.Categories.Select(category => category.Name).Append("全部").ToArray(),
            columns: ShapeMenuCatalog.Categories.Length + 1);
        if (_categories.Content is Grid categoryTabs) categoryTabs.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
        _categories.SetOptionHelp(AllCategoryIndex, "显示全部形状，展开更高的面板");
        for (int index = 0; index < ShapeMenuCatalog.Categories.Length; index++)
        {
            _categories.SetOptionHelp(index, ShapeMenuCatalog.Categories[index].Name + "图形");
            _categoryBodies.Children.Add(new Border { Visibility = Visibility.Collapsed });
        }
        _categories.SelectedIndex = 0;
        _categories.SelectionChanged += () => SelectCategory(_categories.SelectedIndex);
        SelectCategory(0);
        var body = new Viewbox
        {
            Width = boardMode ? 240 : 200,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = boardMode ? new Thickness(0, 10, 0, 0) : new Thickness(0),
            Stretch = Stretch.Uniform,
            Child = _categoryBodies,
        };

        var panel = new StackPanel();
        panel.Children.Add(Header);
        panel.Children.Add(_categories);
        panel.Children.Add(body);
        // WPF BorderDrawShape / BoardBorderDrawShape chrome: FloatBarBackground /
        // BoardBarBackground (white@0.75 light both; dark black@0.6 screen / black@0.75 board)
        // with FloatBarBorderBrush, CornerRadius 5 (MainWindow.xaml:6188-6200, 2010-2030).
        Content = new Border
        {
            Background = boardMode && dark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(191, 0, 0, 0))
                : PopupBrushes.FloatBarBackground(theme),
            BorderBrush = PopupBrushes.ToolPanelBorder(theme),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };
        Loaded += (_, _) =>
        {
            _controller.ToolChanged += RefreshSelection;
            _controller.ShapeSelectionChanged += RefreshSelection;
            if (_controller.Tool == InkTool.Shape) SelectCategoryFor(_controller.ShapeKind);
            RefreshSelection();
        };
        Unloaded += (_, _) =>
        {
            _controller.ToolChanged -= RefreshSelection;
            _controller.ShapeSelectionChanged -= RefreshSelection;
            ResetPress();
        };
    }

    private void EnsureCategory(int index)
    {
        if (_createdCategories[index]) return;
        _createdCategories[index] = true;
        _categoryBodies.Children[index] = BuildCategoryRows(ShapeMenuCatalog.Categories[index], _foreground);
        _categoryBodies.Children[index].Visibility = ShowingAll || index == _selectedCategory ? Visibility.Visible : Visibility.Collapsed;
        RefreshSelection();
    }

    private StackPanel BuildCategoryRows(ShapeMenuCategory category, Brush foreground)
    {
        var rows = new StackPanel { Spacing = RowOverlap };
        foreach (var items in category.Rows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight };
            foreach (var item in items) row.Children.Add(BuildCell(item, foreground));
            rows.Children.Add(row);
        }
        return rows;
    }

    /// <summary>True = auto-hide armed (E718, default); false = pinned open (E77A).</summary>
    public bool AutoHide
    {
        get => _autoHide;
        set
        {
            _autoHide = value;
            Header.IsPinned = !value;
        }
    }

    private Grid BuildCell(ShapeMenuItem item, Brush fg)
    {
        var icon = new Viewbox
        {
            Height = RowHeight - item.Margin.Top - item.Margin.Bottom,
            Stretch = Stretch.Uniform,
            Child = ShapeIcons.Create(item.Kind, fg),
        };
        if (item.SlotWidth > 0) icon.Width = item.SlotWidth;
        if (item.Rotation != 0)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new RotateTransform { Angle = item.Rotation };
        }
        var cell = new Grid
        {
            Margin = item.Margin,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = item,
        };
        cell.Children.Add(icon);
        cell.Children.Add(new FontIcon { Glyph = "\uF60E", FontSize = 9,
            FontFamily = IconFontStyle.Font,
            Foreground = PopupBrushes.Accent(), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false });
        AutomationProperties.SetName(cell, item.Label);
        string instruction = item.Kind == ShapeKind.CoordinateTwoPoint
            ? "从原点拖动至另一角，确定两条坐标轴的长度和方向"
            : ShapeGenerator.IsTwoStepSolid(item.Kind)
            ? item.Kind == ShapeKind.SquarePyramid ? "第一笔绘制底面，第二笔落点确定顶点"
                : item.Kind == ShapeKind.Cube ? "第一笔绘制正方面，第二笔调整投影方向" : "第一笔绘制截面，第二笔拖动确定深度方向"
            : "拖动绘制";
        AutomationProperties.SetHelpText(cell, instruction + "；长按后可连续绘制");
        ToolTipService.SetToolTip(cell, item.Label + "\n" + instruction + "，长按可连续绘制");
        _cells.Add(cell);
        cell.PointerPressed += OnCellPressed;
        cell.PointerReleased += OnCellReleased;
        cell.PointerMoved += OnCellMoved;
        cell.PointerCanceled += OnCellCancel;
        cell.PointerCaptureLost += OnCellCancel;
        return cell;
    }

    /// <summary>Pick through the same entry point used by pointer release.</summary>
    public void SelectItem(int index, bool sticky = false)
    {
        if (index < 0 || index >= _items.Length) return;
        var item = _items[index];
        SelectCategoryFor(item.Kind);
        _controller.SelectShape(item.Kind, sticky);
        RefreshSelection();
        SelectionMade?.Invoke(item.Kind, sticky);
    }

    private void RefreshSelection()
    {
        foreach (var cell in _cells)
        {
            var item = (ShapeMenuItem)cell.Tag;
            bool selected = _controller.Tool == InkTool.Shape && _controller.ShapeKind == item.Kind;
            cell.Background = selected ? PopupBrushes.FloatBarSelected() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            cell.Children.OfType<FontIcon>().Single().Visibility = selected && _controller.StickyShape ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(cell, selected ? _controller.StickyShape ? "连续绘制" : "已选中" : "");
        }
    }

    private void OnCellPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedPointer.HasValue || sender is not Grid cell || cell.Tag is not ShapeMenuItem item) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
            && !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
        _pressedCell = cell;
        _pressedPointer = e.Pointer.PointerId;
        _pressPosition = e.GetCurrentPoint(null).Position;
        _holdFired = false;
        cell.CapturePointer(e.Pointer);
        if (item.LongPress) _holdTimer.Start();
        e.Handled = true;
    }

    private void OnCellMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedPointer != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(null).Position;
        double dx = position.X - _pressPosition.X, dy = position.Y - _pressPosition.Y;
        // A small contact wobble is normal on infrared screens. A deliberate move cancels the pick.
        if (dx * dx + dy * dy > 64) ResetPress();
    }

    private void OnHoldTick(object sender, object e)
    {
        if (_pressedCell?.Tag is not ShapeMenuItem item || !_pressedPointer.HasValue) return;
        _holdFired = true;
        _pressedCell.Opacity = .55;
        _controller.SelectShape(item.Kind, sticky: true);
    }

    private void OnCellReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid cell || !ReferenceEquals(_pressedCell, cell) || _pressedPointer != e.Pointer.PointerId) return;
        int index = Array.IndexOf(_items, (ShapeMenuItem)cell.Tag);
        bool sticky = _holdFired;
        // ReleasePointerCapture can synchronously raise PointerCaptureLost. Save the result first.
        ResetPress();
        cell.ReleasePointerCapture(e.Pointer);
        SelectItem(index, sticky);
        e.Handled = true;
    }

    private void OnCellCancel(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedPointer == e.Pointer.PointerId) ResetPress();
    }

    private void ResetPress()
    {
        _holdTimer.Stop();
        if (_pressedCell is not null) _pressedCell.Opacity = 1;
        _pressedCell = null;
        _pressedPointer = null;
        _holdFired = false;
    }
}
