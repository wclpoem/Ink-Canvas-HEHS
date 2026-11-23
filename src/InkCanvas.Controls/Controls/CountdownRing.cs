using InkCanvas.Controls.Animation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace InkCanvas.Controls.Controls;

/// <summary>Reusable vector dial with a theme-aware track and rounded progress ends.</summary>
public sealed class CountdownRing : UserControl
{
    private readonly SolidColorBrush _brush = new(Microsoft.UI.Colors.Transparent);
    private readonly Ellipse _track = new() { Width = 96.6, Height = 96.6, StrokeThickness = 2.6 };
    private readonly ArcSegment _arc = new() { Size = new(47, 47), SweepDirection = SweepDirection.Clockwise };
    private readonly ArcSegment _finish = new() { Size = new(47, 47), SweepDirection = SweepDirection.Clockwise, Point = new(50, 3) };
    private readonly PathFigure _figure = new() { StartPoint = new(50, 3), IsClosed = false, IsFilled = false };
    private readonly Microsoft.UI.Xaml.Shapes.Path _progress;
    private double _value = -1;
    private Color _color;
    private Storyboard _transition;
    public bool IsPaused { get; set; }
    public bool IsWarning { get; private set; }

    public CountdownRing()
    {
        IsHitTestVisible = false;
        var canvas = new Canvas { Width = 100, Height = 100 };
        Canvas.SetLeft(_track, 1.7); Canvas.SetTop(_track, 1.7);
        var geometry = new PathGeometry(); geometry.Figures.Add(_figure); _figure.Segments.Add(_arc);
        _progress = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = _brush, StrokeThickness = 2.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        canvas.Children.Add(_track); canvas.Children.Add(_progress);
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        Unloaded += (_, _) => { _transition?.Stop(); _brush.Color = _color; };
        Value = 1;
    }

    public double Value
    {
        get => _value;
        set
        {
            double fraction = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (_value == fraction) return;
            _value = fraction;
            _progress.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool full = fraction >= 1;
            double angle = -Math.PI / 2 + fraction * Math.PI * 2;
            _arc.Point = full ? new(50, 97) : new(50 + 47 * Math.Cos(angle), 50 + 47 * Math.Sin(angle));
            _arc.IsLargeArc = !full && fraction > .5;
            if (full && _figure.Segments.Count == 1) _figure.Segments.Add(_finish);
            if (!full && _figure.Segments.Count == 2) _figure.Segments.RemoveAt(1);
        }
    }

    public void ApplyPalette(Color progress, Brush track, bool warning)
    {
        _track.Stroke = track;
        IsWarning = warning;
        if (_color == progress) return;
        _color = progress;
        var current = _brush.Color;
        _transition?.Stop(); _brush.Color = progress;
        if (IsLoaded && Motion.AnimationsEnabled)
        {
            var animation = new ColorAnimation { From = current, To = progress, Duration = new Duration(Motion.Normal) };
            Storyboard.SetTarget(animation, _brush); Storyboard.SetTargetProperty(animation, "Color");
            _transition = new Storyboard(); _transition.Children.Add(animation); _transition.Begin();
        }
    }
}
