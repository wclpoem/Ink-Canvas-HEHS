using InkCanvas.AnalyticGeometry.Handwriting;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>
/// Built-in mathematical handwriting input panel inspired by Windows Math Input Panel.
/// Combines our offline 2D math structure recognizer (fractions, radicals, superscripts, subscripts)
/// with live WinUI 3 MathML preview, ruled ink pad, stroke eraser, and Select &amp; Correct symbol correction.
/// </summary>
public sealed class HandwritingInput : UserControl, IDisposable
{
    private enum PadMode { Write, Erase, Correct }

    private readonly InkController _controller = new() { RecognizeShapes = false, MultiTouchEnabled = true, PalmErases = false };
    private readonly InkSurface _surface;
    private readonly Canvas _gridBackdrop = new() { IsHitTestVisible = false };
    private readonly Canvas _correctOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly MathFormulaView _liveMath = new();
    private readonly TextBlock _expressionBadge = new() { FontFamily = new FontFamily("Cambria Math"), FontSize = 14, Opacity = 0.78, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _insertButton = new() { Content = "插入公式", MinHeight = 40, MinWidth = 88, IsEnabled = false };
    private readonly StackPanel _candidates = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 };
    private readonly TouchIconButton _writeBtn, _eraseBtn, _correctBtn;
    private readonly Button _recognize = new() { Content = "识别", MinHeight = 40, MinWidth = 64 };
    private readonly DispatcherQueueTimer _autoRecognize;
    private readonly Dictionary<int, string> _symbolOverrides = new();
    private readonly CancellationTokenSource _lifetime = new();

    private InkRecognizerContainer _windowsRecognizer;
    private IReadOnlyList<InkRecognizer> _windowsRecognizers = Array.Empty<InkRecognizer>();
    private MathInkRecognitionResult _lastResult;
    private PadMode _mode = PadMode.Write;
    private int _version;
    private bool _busy, _disposed;

    public event Action<string> CandidateChosen;
    public event Action<string, string> FormulaInserted;
    public bool RecognitionAvailable => true;
    public InkController Controller => _controller;
    public MathInkRecognitionResult LastRecognition => _lastResult;

    public HandwritingInput()
    {
        _controller.CurrentAttributes.BaseWidth = 3.2f;
        _controller.CurrentAttributes.Color = new InkColor(255, 22, 34, 56);
        _controller.CurrentAttributes.TaperKind = TaperKind.None;

        _surface = new InkSurface
        {
            Controller = _controller,
            Height = 200,
            ClearColor = Color.FromArgb(0, 255, 255, 255),
        };
        _controller.Document.Changed += OnDocumentChanged;

        _autoRecognize = DispatcherQueue.CreateTimer();
        _autoRecognize.IsRepeating = false;
        _autoRecognize.Interval = TimeSpan.FromMilliseconds(360);
        _autoRecognize.Tick += async (_, _) => await RecognizeAsync();

        // Top MathML Preview Strip (Windows Math Input Panel signature yellow/cream preview bar)
        var previewHeader = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 6) };
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _liveMath.Visibility = Visibility.Collapsed;
        var previewHost = new Grid { MinHeight = 46 };
        var placeholder = new TextBlock
        {
            Text = "在下方网格区域手写公式（支持分式、根号、上标、下标）…",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            FontSize = 13,
        };
        previewHost.Children.Add(placeholder);
        previewHost.Children.Add(_liveMath);
        _liveMath.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            placeholder.Visibility = _liveMath.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        previewHeader.Children.Add(previewHost);
        Grid.SetColumn(_expressionBadge, 1); previewHeader.Children.Add(_expressionBadge);
        Grid.SetColumn(_insertButton, 2); previewHeader.Children.Add(_insertButton);
        _insertButton.Click += (_, _) => InsertCurrentFormula();

        var previewCard = new Border
        {
            Child = previewHeader,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(255, 255, 252, 236)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 212, 168)),
        };

        // Ink Pad with subtle ruled math grid + Select & Correct overlay
        var padGrid = new Grid { Height = 200 };
        padGrid.Children.Add(_gridBackdrop);
        padGrid.Children.Add(_surface);
        padGrid.Children.Add(_correctOverlay);
        padGrid.SizeChanged += (_, _) => DrawGridBackdrop(padGrid.ActualWidth, padGrid.ActualHeight);

        var padBorder = new Border
        {
            Child = padGrid,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(255, 252, 253, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 204, 213, 226)),
        };
        AutomationProperties.SetName(_surface, "公式手写区");

        // Right Tool Strip (Windows Math Input Panel style: Write, Erase, Select & Correct, Undo, Clear)
        _writeBtn = ToolButton(Glyphs.Pen, "书写", () => SetPadMode(PadMode.Write));
        _eraseBtn = ToolButton(Glyphs.Eraser, "擦除", () => SetPadMode(PadMode.Erase));
        _correctBtn = ToolButton(Glyphs.Select, "更正", () => SetPadMode(PadMode.Correct));
        var undoBtn = ToolButton(Glyphs.Undo, "撤销", () => { _controller.Undo(); });
        var clearBtn = ToolButton(Glyphs.Delete, "清空", ClearAll);

        var rightTools = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        rightTools.Children.Add(_writeBtn);
        rightTools.Children.Add(_eraseBtn);
        rightTools.Children.Add(_correctBtn);
        rightTools.Children.Add(undoBtn);
        rightTools.Children.Add(clearBtn);

        var mainRow = new Grid { ColumnSpacing = 8 };
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.Children.Add(padBorder);
        Grid.SetColumn(rightTools, 1); mainRow.Children.Add(rightTools);

        // Bottom candidate strip + status
        _recognize.Click += async (_, _) => await RecognizeAsync();
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.Children.Add(_recognize);
        var candidateScroll = new ScrollViewer
        {
            Content = _candidates,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(candidateScroll, 1); footer.Children.Add(candidateScroll);

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(previewCard);
        root.Children.Add(mainRow);
        root.Children.Add(footer);
        root.Children.Add(_status);
        Content = root;

        SetPadMode(PadMode.Write);
        _status.Text = "内置 2D 数学公式手写引擎已就绪 · 手写自动识别，点击“更正”可点选单个符号修改候选。";
        Loaded += (_, _) => TryInitializeWindowsRecognizer();
        ActualThemeChanged += (_, _) => ApplyTheme(previewCard, padBorder);
        ApplyTheme(previewCard, padBorder);
    }

    private static TouchIconButton ToolButton(string glyph, string label, Action action)
    {
        var btn = new TouchIconButton
        {
            Glyph = glyph,
            Label = label,
            IconSize = 16,
            LabelFontSize = 10,
            MinWidth = 48,
            MinHeight = 40,
            Padding = new Thickness(4, 3, 4, 3),
        };
        AutomationProperties.SetName(btn, "手写画板 " + label);
        ToolTipService.SetToolTip(btn, label);
        btn.Click += (_, _) => action();
        return btn;
    }

    private void ApplyTheme(Border previewCard, Border padBorder)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        previewCard.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 48, 45, 34) : Color.FromArgb(255, 255, 252, 236));
        previewCard.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 92, 84, 58) : Color.FromArgb(255, 224, 212, 168));
        padBorder.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 28, 31, 37) : Color.FromArgb(255, 252, 253, 255));
        padBorder.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 64, 72, 84) : Color.FromArgb(255, 204, 213, 226));
        _controller.CurrentAttributes.Color = dark ? new InkColor(255, 232, 240, 255) : new InkColor(255, 22, 34, 56);
    }

    private void DrawGridBackdrop(double width, double height)
    {
        _gridBackdrop.Children.Clear();
        if (width < 20 || height < 20) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        var minor = new SolidColorBrush(dark ? Color.FromArgb(28, 180, 198, 224) : Color.FromArgb(36, 120, 150, 196));
        var baseline = new SolidColorBrush(dark ? Color.FromArgb(52, 130, 170, 230) : Color.FromArgb(62, 92, 138, 206));
        const double step = 24;
        for (double x = step; x < width; x += step)
            _gridBackdrop.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = minor, StrokeThickness = 1 });
        for (double y = step; y < height; y += step)
            _gridBackdrop.Children.Add(new Line { X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = minor, StrokeThickness = 1 });
        // Center writing guide line (like Windows Math Input Panel)
        double midY = Math.Round(height * 0.56);
        _gridBackdrop.Children.Add(new Line
        {
            X1 = 12, Y1 = midY, X2 = width - 12, Y2 = midY,
            Stroke = baseline, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 },
        });
    }

    private void SetPadMode(PadMode mode)
    {
        _mode = mode;
        _writeBtn.IsSelected = mode == PadMode.Write;
        _eraseBtn.IsSelected = mode == PadMode.Erase;
        _correctBtn.IsSelected = mode == PadMode.Correct;
        _controller.Tool = mode == PadMode.Erase ? InkTool.Eraser : InkTool.Pen;
        _controller.EraserMode = InkEraserMode.WholeStroke;
        _correctOverlay.Visibility = mode == PadMode.Correct ? Visibility.Visible : Visibility.Collapsed;
        _surface.IsHitTestVisible = mode != PadMode.Correct;
        if (mode == PadMode.Correct) RebuildCorrectionOverlay();
    }

    public void ClearAll()
    {
        _autoRecognize.Stop();
        _symbolOverrides.Clear();
        _lastResult = null;
        _controller.Clear();
        _candidates.Children.Clear();
        _correctOverlay.Children.Clear();
        _liveMath.Visibility = Visibility.Collapsed;
        _expressionBadge.Text = "";
        _insertButton.IsEnabled = false;
        _status.Text = "画板已清空。";
    }

    private void TryInitializeWindowsRecognizer()
    {
        if (_disposed || _windowsRecognizer is not null) return;
        try
        {
            _windowsRecognizer = new InkRecognizerContainer();
            _windowsRecognizers = _windowsRecognizer.GetRecognizers();
        }
        catch (Exception)
        {
            _windowsRecognizer = null;
            _windowsRecognizers = Array.Empty<InkRecognizer>();
        }
    }

    private void OnDocumentChanged()
    {
        _version++;
        if (_controller.Document.Count == 0)
        {
            _symbolOverrides.Clear();
            _lastResult = null;
            _candidates.Children.Clear();
            _correctOverlay.Children.Clear();
            _liveMath.Visibility = Visibility.Collapsed;
            _expressionBadge.Text = "";
            _insertButton.IsEnabled = false;
            return;
        }
        _autoRecognize.Stop();
        _autoRecognize.Start();
    }

    public async Task RecognizeAsync()
    {
        if (_disposed || _busy || _controller.Document.Count == 0 || _controller.HasActiveInteraction) return;
        _busy = true;
        _recognize.IsEnabled = false;
        int version = _version;
        try
        {
            var inkStrokes = _controller.Document.Strokes
                .Select((stroke, index) => new MathInkStroke(
                    index,
                    stroke.Points.Select(p => new InkSample(p.X, p.Y)).ToArray()))
                .Where(s => s.Points.Count > 0)
                .ToArray();

            var overridesSnapshot = new Dictionary<int, string>(_symbolOverrides);
            var builtIn = await Task.Run(() => MathInkRecognizer.Recognize(inkStrokes, overridesSnapshot), _lifetime.Token);
            if (_disposed || _version != version) return;

            var allCandidates = new List<string>(builtIn.Candidates);

            // Optional secondary candidates from Windows offline ink recognizer if installed.
            if (_windowsRecognizer is not null && _windowsRecognizers.Count > 0)
            {
                try
                {
                    var container = new InkStrokeContainer();
                    var builder = new InkStrokeBuilder();
                    foreach (var stroke in _controller.Document.Strokes)
                    {
                        var points = stroke.Points.Select(point => new Windows.Foundation.Point(point.X, point.Y)).ToList();
                        if (points.Count == 1) points.Add(new(points[0].X + .1, points[0].Y));
                        if (points.Count > 1) container.AddStroke(builder.CreateStroke(points));
                    }
                    _windowsRecognizer.SetDefaultRecognizer(_windowsRecognizers[0]);
                    var winResults = await _windowsRecognizer.RecognizeAsync(container, InkRecognitionTarget.All).AsTask(_lifetime.Token);
                    if (!_disposed && _version == version && winResults.Count > 0)
                    {
                        var ordered = winResults.OrderBy(item => item.BoundingRect.Y / Math.Max(20, item.BoundingRect.Height))
                            .ThenBy(item => item.BoundingRect.X).ToArray();
                        string winText = NormalizeCandidate(string.Concat(ordered.Select(item => item.GetTextCandidates().FirstOrDefault() ?? "")));
                        if (!string.IsNullOrWhiteSpace(winText) && !allCandidates.Contains(winText))
                            allCandidates.Add(winText);
                    }
                }
                catch (Exception) { /* Built-in engine already succeeded; ignore OS recognizer errors. */ }
            }

            if (_disposed || _version != version) return;
            _lastResult = builtIn with { Candidates = allCandidates };
            UpdatePreview(_lastResult);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "识别异常 · " + ex.GetBaseException().Message;
        }
        finally
        {
            _busy = false;
            if (!_disposed) _recognize.IsEnabled = true;
        }
    }

    private void UpdatePreview(MathInkRecognitionResult result)
    {
        bool valid = !string.IsNullOrWhiteSpace(result.Expression);
        _insertButton.IsEnabled = valid;
        _expressionBadge.Text = valid ? result.Expression : "";
        _liveMath.Visibility = valid && _liveMath.SetExpression(result.Expression) ? Visibility.Visible : Visibility.Collapsed;

        _candidates.Children.Clear();
        foreach (string candidate in result.Candidates.Take(6))
        {
            string value = candidate;
            var btn = new Button
            {
                Content = value,
                MinHeight = 36,
                MinWidth = 56,
                Padding = new Thickness(10, 4, 10, 4),
                FontFamily = new FontFamily("Cambria Math"),
                FontSize = 15,
                AllowFocusOnInteraction = false,
            };
            btn.Click += (_, _) =>
            {
                _expressionBadge.Text = value;
                _liveMath.Visibility = _liveMath.SetExpression(value) ? Visibility.Visible : Visibility.Collapsed;
                CandidateChosen?.Invoke(value);
                FormulaInserted?.Invoke(value, _liveMath.MathML);
            };
            _candidates.Children.Add(btn);
        }

        if (_mode == PadMode.Correct) RebuildCorrectionOverlay();
        _status.Text = valid
            ? $"已识别 {result.Symbols.Count} 个符号 · 点击“插入公式”送入编辑器，或切到“更正”修改单个符号。"
            : "继续书写以生成数学公式。";
    }

    private void RebuildCorrectionOverlay()
    {
        _correctOverlay.Children.Clear();
        if (_lastResult is null) return;
        foreach (var sym in _lastResult.Symbols)
        {
            var captured = sym;
            var box = new Button
            {
                Width = Math.Max(28, sym.Bounds.Width + 10),
                Height = Math.Max(28, sym.Bounds.Height + 10),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(42, 0, 103, 192)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 0, 103, 192)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Content = new TextBlock
                {
                    Text = sym.Symbol,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -2, 2, 0),
                },
            };
            ToolTipService.SetToolTip(box, $"点击更正符号 “{sym.Symbol}”");
            box.Click += (_, _) => ShowCorrectionMenu(box, captured);
            Canvas.SetLeft(box, Math.Max(0, sym.Bounds.Left - 5));
            Canvas.SetTop(box, Math.Max(0, sym.Bounds.Top - 5));
            _correctOverlay.Children.Add(box);
        }
    }

    private void ShowCorrectionMenu(FrameworkElement anchor, RecognizedSymbol symbol)
    {
        var flyout = new MenuFlyout();
        var options = symbol.Alternatives
            .Concat(new[] { "x", "y", "a", "b", "2", "3", "+", "-", "=", "√", "π", "(", ")" })
            .Distinct()
            .Take(10);
        foreach (string option in options)
        {
            string chosen = option;
            var item = new MenuFlyoutItem { Text = chosen };
            item.Click += async (_, _) =>
            {
                if (symbol.StrokeIds.Count > 0) _symbolOverrides[symbol.StrokeIds[0]] = chosen;
                await RecognizeAsync();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private void InsertCurrentFormula()
    {
        string expression = _expressionBadge.Text;
        if (string.IsNullOrWhiteSpace(expression)) return;
        CandidateChosen?.Invoke(expression);
        FormulaInserted?.Invoke(expression, _liveMath.MathML);
    }

    private static string NormalizeCandidate(string value)
        => value.Replace(" ", "").Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("²", "^2").Replace("³", "^3");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoRecognize.Stop();
        _lifetime.Cancel();
        _controller.Document.Changed -= OnDocumentChanged;
        _surface.Dispose();
        _controller.Dispose();
        _windowsRecognizer = null;
    }
}
