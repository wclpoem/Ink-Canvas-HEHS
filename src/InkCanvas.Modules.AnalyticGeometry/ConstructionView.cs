using System.Numerics;
using InkCanvas.AnalyticGeometry.Construction;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>Native Win2D GeoGebra-style geometry view with crisp major/minor grid, axes arrowheads, and cached geometry.</summary>
public sealed class ConstructionView : UserControl, IDisposable
{
    private sealed record Drawing(ConstructionObject Object, GeometryValue Value, CanvasGeometry Lines, CanvasGeometry Fill, Vector2[] Points);
    private readonly CanvasControl _canvas = new() { ClearColor = Microsoft.UI.Colors.White };
    private readonly Canvas _widgets = new();
    private readonly Dictionary<string, FrameworkElement> _controls = new();
    private readonly CanvasTextFormat _font = new() { FontFamily = "Segoe UI", FontSize = 12 };
    private readonly CanvasTextFormat _labelFont = new() { FontFamily = "Cambria Math", FontSize = 14 };
    private readonly List<Drawing> _drawings = new();
    private readonly Dictionary<string, Queue<GeoVector>> _traces = new();
    private ConstructionSnapshot _state = new();
    private ConstructionEvaluation _evaluation;
    private GeometryProjection _projection;
    private string _dragging;
    private Vector2 _pressed;
    private GeoVector _dragOrigin;
    private readonly HashSet<uint> _pointers = new();
    private bool _multiplePointers;
    private bool _dirty = true, _disposed;
    private string _cameraSignature;
    public string SelectedLabel { get; set; }
    public bool ConstructionToolActive { get; set; }
    public bool MoveAlongZAxis { get; set; }
    public event Action<GeoVector, string> Clicked;
    public event Action<string, GeoVector> PointMoved;
    public event Action<ConstructionSnapshot> ViewChanged;
    public event Action GestureStarted;
    public event Action GestureEnded;
    public event Action<string, string> ControlChanged;
    public event Action<string> ControlClicked;

    public ConstructionView()
    {
        Content = new Grid { Children = { _canvas, _widgets } };
        _canvas.Draw += Draw;
        SizeChanged += (_, _) => InvalidateGeometry();
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        ManipulationStarted += (_, _) => GestureStarted?.Invoke();
        ManipulationDelta += (_, args) =>
        {
            if (_dragging is not null || ActualWidth < 1 || ActualHeight < 1) return;
            double scale = Math.Clamp(args.Delta.Scale, .5, 2);
            var delta = args.Delta.Translation;
            if (_state.ThreeDimensional && Math.Abs(scale - 1) < .001)
                SetView(_state with { Yaw = _state.Yaw + delta.X * .008, Pitch = Math.Clamp(_state.Pitch + delta.Y * .008, -1.5, 1.5) });
            else
            {
                double units = Math.Max((_state.XMax - _state.XMin) / ActualWidth, (_state.YMax - _state.YMin) / ActualHeight);
                double dx = delta.X * units;
                double dy = delta.Y * units;
                SetView(Zoom(_state with { XMin = _state.XMin - dx, XMax = _state.XMax - dx, YMin = _state.YMin + dy, YMax = _state.YMax + dy },
                    scale, new((float)args.Position.X, (float)args.Position.Y)));
            }
            args.Handled = true;
        };
        ManipulationCompleted += (_, _) => GestureEnded?.Invoke();
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += (_, args) => ClearPointer(args);
        PointerCaptureLost += (_, args) => ClearPointer(args);
        PointerWheelChanged += (_, args) =>
        {
            var point = args.GetCurrentPoint(this);
            GestureStarted?.Invoke();
            SetView(Zoom(_state, Math.Pow(1.15, point.Properties.MouseWheelDelta / 120d), new((float)point.Position.X, (float)point.Position.Y)));
            GestureEnded?.Invoke();
            args.Handled = true;
        };
    }

    public void ZoomIn()
    {
        if (_disposed || ActualWidth < 1 || ActualHeight < 1) return;
        GestureStarted?.Invoke();
        SetView(Zoom(_state, 1.25, new((float)(ActualWidth * 0.5), (float)(ActualHeight * 0.5))));
        GestureEnded?.Invoke();
    }

    public void ZoomOut()
    {
        if (_disposed || ActualWidth < 1 || ActualHeight < 1) return;
        GestureStarted?.Invoke();
        SetView(Zoom(_state, 0.8, new((float)(ActualWidth * 0.5), (float)(ActualHeight * 0.5))));
        GestureEnded?.Invoke();
    }

    public void ResetView()
    {
        if (_disposed) return;
        GestureStarted?.Invoke();
        SetView(_state with { XMin = -10, XMax = 10, YMin = -7.5, YMax = 7.5, Yaw = -.6, Pitch = .65 });
        GestureEnded?.Invoke();
    }

    public void SetConstruction(ConstructionSnapshot state, ConstructionEvaluation evaluation)
    {
        _state = state;
        _evaluation = evaluation;
        UpdateWidgets();
        foreach (var item in state.Objects.Where(item => item.Trace))
        {
            if (!evaluation.Values.TryGetValue(item.Label, out var value) || value.Kind != GeometryKind.Point || !value.Defined) continue;
            if (!_traces.TryGetValue(item.Label, out var queue)) _traces[item.Label] = queue = new();
            if (queue.Count == 0 || (queue.Last() - value.Origin).Length > 1e-5) queue.Enqueue(value.Origin);
            while (queue.Count > 2048) queue.Dequeue();
        }
        foreach (string label in _traces.Keys.Where(label => !state.Objects.Any(item => item.Label == label && item.Trace)).ToArray()) _traces.Remove(label);
        InvalidateGeometry();
    }

    private void UpdateWidgets()
    {
        int index = 0;
        var retained = new HashSet<string>();
        foreach (var item in _state.Objects.Where(item => item.Visible))
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            string kind = value.ControlKind;
            if (kind.Length == 0) continue;
            string key = item.Label + ":" + kind;
            retained.Add(key);
            if (!_controls.TryGetValue(key, out var element))
            {
                string label = item.Label;
                switch (kind)
                {
                    case "checkbox":
                        var checkbox = new CheckBox { Content = label, MinHeight = 44 };
                        checkbox.Click += (_, _) => ControlChanged?.Invoke(label, "Checkbox(" + (checkbox.IsChecked == true ? "1" : "0") + ")");
                        element = checkbox; break;
                    case "button":
                        var button = new Button { Content = value.Display, MinHeight = 44 };
                        button.Click += (_, _) => ControlClicked?.Invoke(label); element = button; break;
                    case "input":
                        var input = new TextBox { Header = value.ControlTarget, Text = value.Display, MinHeight = 44, Width = 170 };
                        input.LostFocus += (_, _) => ControlChanged?.Invoke(value.ControlTarget, input.Text);
                        input.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { ControlChanged?.Invoke(value.ControlTarget, input.Text); args.Handled = true; } };
                        element = input; break;
                    case "dropdown":
                        var dropdown = new ComboBox { MinHeight = 44, MinWidth = 140, ItemsSource = value.Options };
                        dropdown.SelectionChanged += (_, _) =>
                        {
                            if (dropdown.Tag is true || dropdown.SelectedIndex < 0) return;
                            ControlChanged?.Invoke(label, "Dropdown(" + value.ControlTarget + "," + (dropdown.SelectedIndex + 1) + ")");
                        };
                        element = dropdown; break;
                    default: continue;
                }
                element.PointerPressed += (_, args) => args.Handled = true;
                element.PointerReleased += (_, args) => args.Handled = true;
                element.ManipulationMode = ManipulationModes.System;
                _controls[key] = element; _widgets.Children.Add(element);
            }
            if (element is CheckBox check) check.IsChecked = value.Number != 0;
            if (element is Button action) action.Content = value.Display;
            if (element is TextBox box && box.FocusState == FocusState.Unfocused) box.Text = value.Display;
            if (element is ComboBox combo)
            {
                combo.Tag = true;
                combo.ItemsSource = value.Options;
                if (InkCanvas.AnalyticGeometry.Algebra.MathSyntax.Call(item.Definition, out _, out var args) && args.Length > 1
                    && int.TryParse(args[1], out int selected)) combo.SelectedIndex = selected - 1;
                else combo.SelectedIndex = 0;
                combo.Tag = false;
            }
            Canvas.SetLeft(element, 16); Canvas.SetTop(element, 12 + index * 72); index++;
        }
        foreach (string key in _controls.Keys.Where(key => !retained.Contains(key)).ToArray())
        { _widgets.Children.Remove(_controls[key]); _controls.Remove(key); }
    }

    public void InvalidateGeometry()
    {
        if (_disposed) return;
        _dirty = true;
        _projection = new(_state, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        _canvas.Invalidate();
    }

    private void SetView(ConstructionSnapshot state)
    {
        _state = state;
        InvalidateGeometry();
        ViewChanged?.Invoke(state);
    }

    private ConstructionSnapshot Zoom(ConstructionSnapshot state, double scale, Vector2 center)
    {
        double units = Math.Max((state.XMax - state.XMin) / Math.Max(1, ActualWidth), (state.YMax - state.YMin) / Math.Max(1, ActualHeight));
        double x = (state.XMin + state.XMax) / 2 + (center.X - ActualWidth / 2) * units;
        double y = (state.YMin + state.YMax) / 2 - (center.Y - ActualHeight / 2) * units;
        double span = (state.XMax - state.XMin) / scale;
        if (span is < 1e-4 or > 1e6) return state;
        return state with { XMin = x + (state.XMin - x) / scale, XMax = x + (state.XMax - x) / scale,
            YMin = y + (state.YMin - y) / scale, YMax = y + (state.YMax - y) / scale };
    }

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || _projection is null) return;
        var point = args.GetCurrentPoint(this);
        _pointers.Add(args.Pointer.PointerId);
        if (_pointers.Count > 1) { _multiplePointers = true; EndDrag(); return; }
        _pressed = new((float)point.Position.X, (float)point.Position.Y);
        string hit = HitTest(_pressed);
        if (!ConstructionToolActive && hit is not null)
        {
            SelectedLabel = hit;
            var item = _state.Objects.First(item => item.Label == hit);
            if (!item.Fixed && _evaluation.Values[hit].Kind == GeometryKind.Point
                && ConstructionDocument.IsFreePoint(item))
            {
                _dragging = hit;
                _dragOrigin = _evaluation.Values[hit].Origin;
                ManipulationMode = ManipulationModes.None;
                CapturePointer(args.Pointer);
                GestureStarted?.Invoke();
                args.Handled = true;
            }
        }
        _canvas.Invalidate();
    }

    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging is null) return;
        var point = args.GetCurrentPoint(this).Position;
        var screen = new Vector2((float)point.X, (float)point.Y);
        try
        {
            GeoVector position;
            if (_state.ThreeDimensional && MoveAlongZAxis)
            {
                var axis = _projection.Project(_dragOrigin + new GeoVector(0, 0, 1)) - _projection.Project(_dragOrigin);
                double dz = Vector2.Dot(screen - _pressed, axis) / Math.Max(.01f, axis.LengthSquared());
                position = _dragOrigin + new GeoVector(0, 0, dz);
            }
            else position = _projection.Unproject(screen, _dragOrigin.Z);
            PointMoved?.Invoke(_dragging, position);
        }
        catch (InvalidOperationException) { }
        args.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(this).Position;
        var point = new Vector2((float)position.X, (float)position.Y);
        if (!_multiplePointers && _dragging is null && Vector2.Distance(point, _pressed) < 12 && _projection is not null)
        {
            try { Clicked?.Invoke(_projection.Unproject(point), HitTest(point)); }
            catch (InvalidOperationException) { }
        }
        _pointers.Remove(args.Pointer.PointerId);
        if (_pointers.Count == 0) _multiplePointers = false;
        EndDrag();
        ReleasePointerCapture(args.Pointer);
    }

    private void EndDrag()
    {
        if (_dragging is null) return;
        _dragging = null;
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        GestureEnded?.Invoke();
    }

    private void ClearPointer(PointerRoutedEventArgs args)
    {
        EndDrag(); _pointers.Remove(args.Pointer.PointerId);
        if (_pointers.Count == 0) _multiplePointers = false;
    }

    private string HitTest(Vector2 point)
    {
        if (_evaluation is null || _projection is null) return null;
        foreach (var item in _state.Objects.Where(item => item.Visible).Reverse())
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            if (value.Kind == GeometryKind.Point && value.Points.Any(p => Vector2.Distance(_projection.Project(p), point) < 16)) return item.Label;
        }
        foreach (var item in _state.Objects.Where(item => item.Visible).Reverse())
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            foreach (var path in _projection.Paths(value))
            for (int i = 1; i < path.Length; i++)
            {
                var d = path[i] - path[i - 1];
                float t = Math.Clamp(Vector2.Dot(point - path[i - 1], d) / Math.Max(.001f, d.LengthSquared()), 0, 1);
                if (Vector2.Distance(path[i - 1] + t * d, point) < 10) return item.Label;
            }
        }
        return null;
    }

    private void Rebuild(CanvasControl sender)
    {
        var previous = _drawings.ToDictionary(drawing => drawing.Object.Label);
        _drawings.Clear();
        string camera = $"{ActualWidth},{ActualHeight},{_state.XMin},{_state.XMax},{_state.YMin},{_state.YMax},{_state.Yaw},{_state.Pitch},{_state.ThreeDimensional},{_state.Perspective},{_state.Oblique}";
        bool sameCamera = camera == _cameraSignature;
        _cameraSignature = camera;
        if (_evaluation is null || _projection is null)
        {
            foreach (var drawing in previous.Values) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
            return;
        }
        foreach (var item in _state.Objects.Where(item => item.Visible))
        {
            if (!_evaluation.Values.TryGetValue(item.Label, out var value) || !value.Defined) continue;
            if (sameCamera && previous.TryGetValue(item.Label, out var cached) && ReferenceEquals(cached.Value, value))
            {
                _drawings.Add(cached with { Object = item }); previous.Remove(item.Label); continue;
            }
            using var lineBuilder = new CanvasPathBuilder(sender);
            bool hasLines = false;
            foreach (var path in _projection.Paths(value))
            {
                lineBuilder.BeginFigure(path[0]);
                foreach (var point in path.Skip(1)) lineBuilder.AddLine(point);
                lineBuilder.EndFigure(CanvasFigureLoop.Open);
                hasLines = true;
            }
            using var fillBuilder = new CanvasPathBuilder(sender);
            fillBuilder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
            bool hasFill = false;
            foreach (var face in value.Faces)
            {
                var a = _projection.Project(face.A);
                var b = _projection.Project(face.B);
                var c = _projection.Project(face.C);
                if (!Valid(a) || !Valid(b) || !Valid(c)) continue;
                if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) (b, c) = (c, b);
                fillBuilder.BeginFigure(a); fillBuilder.AddLine(b); fillBuilder.AddLine(c);
                fillBuilder.EndFigure(CanvasFigureLoop.Closed);
                hasFill = true;
            }
            if (value.Kind == GeometryKind.Region && !hasFill)
            foreach (var path in _projection.Paths(value))
            {
                fillBuilder.BeginFigure(path[0]);
                foreach (var point in path.Skip(1)) fillBuilder.AddLine(point);
                fillBuilder.EndFigure(CanvasFigureLoop.Closed);
                hasFill = true;
            }
            _drawings.Add(new(item, value, hasLines ? CanvasGeometry.CreatePath(lineBuilder) : null,
                hasFill ? CanvasGeometry.CreatePath(fillBuilder) : null, value.Points.Select(_projection.Project).Where(Valid).ToArray()));
        }
        foreach (var drawing in previous.Values) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
        _dirty = false;
    }

    private static bool Valid(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
        && Math.Abs(point.X) < 1e7 && Math.Abs(point.Y) < 1e7;

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || ActualWidth < 2 || ActualHeight < 2) return;
        if (_dirty) Rebuild(sender);
        var ds = args.DrawingSession;
        DrawGrid(ds);
        foreach (var drawing in _drawings)
        {
            uint packed = drawing.Object.Color;
            var color = Color.FromArgb(255, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
            if (drawing.Fill is not null)
                ds.FillGeometry(drawing.Fill, Color.FromArgb((byte)(255 * Math.Clamp(drawing.Object.Opacity, 0, 1)), color.R, color.G, color.B));
            if (_traces.TryGetValue(drawing.Object.Label, out var trace))
                foreach (var point in trace) ds.FillCircle(_projection.Project(point), 1.5f, color);
            if (drawing.Lines is not null)
            {
                if (drawing.Object.Label == SelectedLabel)
                    ds.DrawGeometry(drawing.Lines, Color.FromArgb(64, color.R, color.G, color.B), (float)drawing.Object.LineWidth + 7);
                ds.DrawGeometry(drawing.Lines, color, (float)drawing.Object.LineWidth);
            }
            foreach (var point in drawing.Points)
            {
                if (drawing.Object.Label == SelectedLabel)
                    ds.FillCircle(point, 11, Color.FromArgb(56, color.R, color.G, color.B));
                ds.FillCircle(point, 4.8f, color);
                ds.DrawCircle(point, 4.8f, Microsoft.UI.Colors.White, 1.4f);
            }
            if (drawing.Object.ShowLabel && drawing.Points.Length > 0)
                ds.DrawText(drawing.Object.Label, drawing.Points[0] + new Vector2(9, -22), color, _labelFont);
        }
    }

    private void DrawGrid(CanvasDrawingSession ds)
    {
        if (_projection is null) return;
        var majorGrid = Color.FromArgb(255, 224, 229, 237);
        var minorGrid = Color.FromArgb(255, 241, 244, 249);
        var axisColor = Color.FromArgb(255, 71, 85, 105);
        var labelColor = Color.FromArgb(255, 100, 116, 139);
        float w = (float)ActualWidth;
        float h = (float)ActualHeight;

        if (_state.ThreeDimensional)
        {
            if (_state.Grid)
            for (int i = -10; i <= 10; i++)
            {
                var c = i % 5 == 0 ? majorGrid : minorGrid;
                ds.DrawLine(_projection.Project(new(i, -10)), _projection.Project(new(i, 10)), c);
                ds.DrawLine(_projection.Project(new(-10, i)), _projection.Project(new(10, i)), c);
            }
            if (_state.Axes)
            {
                var origin = _projection.Project(default);
                ds.DrawLine(origin, _projection.Project(new(8, 0, 0)), Microsoft.UI.Colors.IndianRed, 1.8f);
                ds.DrawLine(origin, _projection.Project(new(0, 8, 0)), Microsoft.UI.Colors.SeaGreen, 1.8f);
                ds.DrawLine(origin, _projection.Project(new(0, 0, 8)), Microsoft.UI.Colors.RoyalBlue, 1.8f);
            }
            return;
        }

        double step = Math.Pow(10, Math.Floor(Math.Log10((_state.XMax - _state.XMin) / 8)));
        while ((_state.XMax - _state.XMin) / step > 18) step *= 2;
        var origin2D = _projection.Project(default);

        if (_state.PolarGrid && _state.Grid)
        {
            float scale = Vector2.Distance(origin2D, _projection.Project(new(1, 0)));
            double limit = Math.Max(Math.Abs(_state.XMin), Math.Abs(_state.XMax)) + Math.Max(Math.Abs(_state.YMin), Math.Abs(_state.YMax));
            for (double radius = step; radius < limit; radius += step) ds.DrawCircle(origin2D, (float)radius * scale, majorGrid);
            for (int i = 0; i < 24; i++)
            { double angle = i * Math.Tau / 24; ds.DrawLine(origin2D, _projection.Project(new(limit * Math.Cos(angle), limit * Math.Sin(angle))), i % 2 == 0 ? majorGrid : minorGrid); }
        }
        else if (_state.Grid)
        {
            // Minor gridlines (5 subdivisions, GeoGebra-style)
            double minorStep = step / 5.0;
            for (double x = Math.Ceiling(_state.XMin / minorStep) * minorStep; x < _state.XMax; x += minorStep)
            {
                float px = _projection.Project(new(x, 0)).X;
                ds.DrawLine(px, 0, px, h, minorGrid, 0.8f);
            }
            for (double y = Math.Ceiling(_state.YMin / minorStep) * minorStep; y < _state.YMax; y += minorStep)
            {
                float py = _projection.Project(new(0, y)).Y;
                ds.DrawLine(0, py, w, py, minorGrid, 0.8f);
            }
            // Major gridlines
            for (double x = Math.Ceiling(_state.XMin / step) * step; x < _state.XMax; x += step)
            {
                float px = _projection.Project(new(x, 0)).X;
                ds.DrawLine(px, 0, px, h, majorGrid, 1.0f);
            }
            for (double y = Math.Ceiling(_state.YMin / step) * step; y < _state.YMax; y += step)
            {
                float py = _projection.Project(new(0, y)).Y;
                ds.DrawLine(0, py, w, py, majorGrid, 1.0f);
            }
        }

        if (_state.Axes)
        {
            float axisY = Math.Clamp(origin2D.Y, 16, h - 24);
            float axisX = Math.Clamp(origin2D.X, 24, w - 16);

            // Axes lines
            if (origin2D.X >= 0 && origin2D.X <= w)
            {
                ds.DrawLine(origin2D.X, 6, origin2D.X, h, axisColor, 1.5f);
                // Y-axis arrowhead
                ds.DrawLine(origin2D.X, 4, origin2D.X - 5, 13, axisColor, 1.5f);
                ds.DrawLine(origin2D.X, 4, origin2D.X + 5, 13, axisColor, 1.5f);
            }
            if (origin2D.Y >= 0 && origin2D.Y <= h)
            {
                ds.DrawLine(0, origin2D.Y, w - 6, origin2D.Y, axisColor, 1.5f);
                // X-axis arrowhead
                ds.DrawLine(w - 4, origin2D.Y, w - 13, origin2D.Y - 5, axisColor, 1.5f);
                ds.DrawLine(w - 4, origin2D.Y, w - 13, origin2D.Y + 5, axisColor, 1.5f);
            }

            // Tick marks and GeoGebra-style numbers right along the axes
            for (double x = Math.Ceiling(_state.XMin / step) * step; x < _state.XMax; x += step)
            {
                if (Math.Abs(x) < step * 0.05) continue;
                float px = _projection.Project(new(x, 0)).X;
                if (px < 12 || px > w - 24) continue;
                if (origin2D.Y >= 0 && origin2D.Y <= h)
                    ds.DrawLine(px, origin2D.Y - 3, px, origin2D.Y + 3, axisColor, 1.2f);
                ds.DrawText(x.ToString("G4"), px - 6, axisY + 5, labelColor, _font);
            }
            for (double y = Math.Ceiling(_state.YMin / step) * step; y < _state.YMax; y += step)
            {
                if (Math.Abs(y) < step * 0.05) continue;
                float py = _projection.Project(new(0, y)).Y;
                if (py < 18 || py > h - 18) continue;
                if (origin2D.X >= 0 && origin2D.X <= w)
                    ds.DrawLine(origin2D.X - 3, py, origin2D.X + 3, py, axisColor, 1.2f);
                ds.DrawText(y.ToString("G4"), axisX + 6, py - 8, labelColor, _font);
            }
            if (origin2D.X >= 10 && origin2D.X <= w - 20 && origin2D.Y >= 10 && origin2D.Y <= h - 22)
                ds.DrawText("0", origin2D.X - 13, origin2D.Y + 4, labelColor, _font);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var drawing in _drawings) { drawing.Lines?.Dispose(); drawing.Fill?.Dispose(); }
        _drawings.Clear(); _traces.Clear(); _font.Dispose(); _labelFont.Dispose();
        _canvas.Draw -= Draw; _canvas.RemoveFromVisualTree(); Content = null;
    }
}
