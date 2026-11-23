using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.AnalyticGeometry;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed class GeometryWindow : Window
{
    private readonly ICanvasToolsHost _host;
    private CanvasToolTarget _target;
    private IReadOnlyList<InkStroke> _editing = Array.Empty<InkStroke>();
    private readonly object _previewOwner = new();
    private readonly Grid _root = new();
    private readonly ComboBox _kind = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "直角坐标 · 函数或隐式方程", "参数方程 · x(t)、y(t)", "极坐标 · r(t)" } };
    private readonly TextBox _formula = FormulaBox("公式"), _second = FormulaBox("y(t)");
    private readonly RichEditBox _formulaMath = MathBox("公式排版"), _secondMath = MathBox("y(t) 排版");
    private readonly MathFormulaPreview _formulaFallback = new(), _secondFallback = new();
    private readonly TextBlock _mathStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .76 };
    private TextBox _focused;
    private readonly Dictionary<string, NumberBox> _numbers = new();
    private readonly CheckBox _axes = new() { Content = "插入坐标轴", IsChecked = true, MinHeight = 40 };
    private readonly CheckBox _live = new() { Content = "在画布中实时预览", IsChecked = true, MinHeight = 40 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8, 16, 12), FontSize = 13 };
    private readonly Button _insert = new() { Content = "插入形状", MinHeight = 48, MinWidth = 132 };
    private readonly PlotPreview _preview = new();
    private readonly HandwritingInput _handwriting = new();
    private readonly DispatcherQueueTimer _debounce;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _calculation;
    private CompiledPlot _compiled;
    private string _compiledKey;
    private PlotMesh _mesh;
    private PlotDefinition _definition;
    private int _generation;
    private bool _settingInputs, _closed, _previewUpdating;
    private bool _mathModeAvailable;
    private string _formulaMathMl, _secondMathMl;
    private SegmentedSelector _formulaMode;
    private Matrix3x2 _lastView;
    private SelectionFrame? _lastFrame;
    public FormulaKeyboard Keyboard { get; } = new();
    public bool MathModeAvailable => _mathModeAvailable;
    public SegmentedSelector FormulaMode => _formulaMode;
    public RichEditBox MathFormulaEditor => _formulaMath;
    public PlotDefinition CurrentDefinition => _definition ?? ReadDefinition();
    public PlotMesh CurrentMesh => _mesh;
    public HandwritingInput Handwriting => _handwriting;
    public int CompletedGeneration { get; private set; }
    public long LastCalculationMilliseconds { get; private set; }
    public string StatusText => _status.Text;

    public GeometryWindow(ICanvasToolsHost host)
    {
        _host = host; Title = "解析几何 — Ink Canvas";
        _mathModeAvailable = EnableMathMode(_formulaMath) & EnableMathMode(_secondMath);
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "解析几何", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var target = new Button { Content = "使用当前画布 / 所选图形", MinHeight = 44 };
        var create = new Button { Content = "新建图形", MinHeight = 44 };
        create.Click += (_, _) =>
        {
            _target.Controller.ClearSelection(); _editing = Array.Empty<InkStroke>(); _insert.Content = "插入形状";
            _live.IsChecked = true; QueueCalculation();
        };
        Grid.SetColumn(create, 1); header.Children.Add(create);
        target.Click += (_, _) => Capture(); Grid.SetColumn(target, 2); header.Children.Add(target);
        Grid.SetColumn(_insert, 3); header.Children.Add(_insert); _root.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(396) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); _root.Children.Add(body);
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
        _kind.SelectedIndex = 0; panel.Children.Add(_kind);
        _formulaMode = new SegmentedSelector(new[] { "线性公式", "公式预览" }) { Height = 40 };
        AutomationProperties.SetName(_formulaMode, "公式输入模式");
        panel.Children.Add(_formulaMode);
        panel.Children.Add(_formula); panel.Children.Add(_second);
        panel.Children.Add(_formulaMath); panel.Children.Add(_secondMath);
        panel.Children.Add(_formulaFallback); panel.Children.Add(_secondFallback); panel.Children.Add(_mathStatus);
        _formulaMath.Visibility = _secondMath.Visibility = Visibility.Collapsed;
        _formulaFallback.Visibility = _secondFallback.Visibility = Visibility.Collapsed;
        _formulaMode.SelectedIndex = 1;
        _formulaMode.SelectionChanged += UpdateFormulaInputMode;
        UpdateFormulaInputMode();
        var inputMode = new SegmentedSelector(new[] { "公式键盘", "手写输入" }) { Height = 44 }; inputMode.SelectedIndex = 0;
        AutomationProperties.SetName(inputMode, "公式输入方式");
        panel.Children.Add(inputMode); panel.Children.Add(Keyboard); panel.Children.Add(_handwriting); _handwriting.Visibility = Visibility.Collapsed;
        inputMode.SelectionChanged += () => { Keyboard.Visibility = inputMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; _handwriting.Visibility = inputMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; };
        Keyboard.KeyPressed += HandleKeyboardKey;
        _handwriting.CandidateChosen += value => SetFocusedFormula(value);
        panel.Children.Add(BuildExamples());
        var ranges = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        ranges.ColumnDefinitions.Add(new ColumnDefinition()); ranges.ColumnDefinitions.Add(new ColumnDefinition());
        AddRange(ranges, "xMin", "x 最小", 0, 0); AddRange(ranges, "xMax", "x 最大", 0, 1);
        AddRange(ranges, "yMin", "y 最小", 1, 0); AddRange(ranges, "yMax", "y 最大", 1, 1);
        AddRange(ranges, "tMin", "t 最小", 2, 0); AddRange(ranges, "tMax", "t 最大", 2, 1);
        panel.Children.Add(new Expander { Header = "坐标范围", Content = ranges, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
        var parameters = new StackPanel { Spacing = 6 };
        foreach (string name in new[] { "a", "b", "c" })
        {
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 });
            var slider = new PointerSlider { Minimum = -10, Maximum = 10, StepFrequency = .1, Height = 44, VerticalAlignment = VerticalAlignment.Center };
            var number = Number(name, null); _numbers[name] = number;
            Grid.SetColumn(slider, 1); row.Children.Add(slider); Grid.SetColumn(number, 2); row.Children.Add(number);
            bool sync = false;
            slider.ValueChanged += (_, _) => { if (sync) return; sync = true; number.Value = slider.Value; sync = false; };
            number.ValueChanged += (_, _) => { if (!sync && double.IsFinite(number.Value)) { sync = true; slider.Value = Math.Clamp(number.Value, -10, 10); sync = false; } };
            parameters.Children.Add(row);
        }
        panel.Children.Add(new Expander { Header = "参数 a、b、c", Content = parameters, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        var width = Number("lineWidth", "线宽"); width.Minimum = .5; width.Maximum = 12; width.SmallChange = .5; _numbers["lineWidth"] = width;
        panel.Children.Add(width); panel.Children.Add(_axes); panel.Children.Add(_live);
        body.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var previewFrame = new Border { Child = _preview, Background = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 0, 16, 0), CornerRadius = new CornerRadius(8) };
        Grid.SetColumn(previewFrame, 1); body.Children.Add(previewFrame); Grid.SetRow(_status, 2); _root.Children.Add(_status);
        Content = _root; _host.ConfigureWindow(this);
        WindowFrame.Attach(this, _root).UseMica();
        _debounce = DispatcherQueue.CreateTimer(); _debounce.Interval = TimeSpan.FromMilliseconds(110); _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => _pendingCalculation = CalculateCoreAsync();
        _kind.SelectionChanged += (_, _) =>
        {
            if (_settingInputs) return;
            SetDefinition(_kind.SelectedIndex switch {
                1 => ReadDefinition() with { Kind = PlotKind.Parametric, Formula = "4*cos(t)", SecondFormula = "3*sin(t)", FormulaMathML = null, SecondFormulaMathML = null },
                2 => ReadDefinition() with { Kind = PlotKind.Polar, Formula = "4*cos(3*t)", FormulaMathML = null, SecondFormulaMathML = null },
                _ => ReadDefinition() with { Kind = PlotKind.Cartesian, Formula = "y=a*x^2+b*x+c", FormulaMathML = null, SecondFormulaMathML = null } });
            UpdateFormulaInputMode();
        };
        _formula.TextChanged += (_, _) => OnLinearFormulaChanged(_formula, false);
        _second.TextChanged += (_, _) => OnLinearFormulaChanged(_second, true);
        _formula.GotFocus += (_, _) => _focused = _formula; _second.GotFocus += (_, _) => _focused = _second;
        if (!_mathModeAvailable) _formulaMode.SelectedIndex = 0;
        _axes.Checked += (_, _) => QueueCalculation(); _axes.Unchecked += (_, _) => QueueCalculation();
        _live.Checked += (_, _) => UpdateCanvasPreview(); _live.Unchecked += (_, _) => _target?.Controller.ClearExtensionPreview(_previewOwner);
        _insert.Click += (_, _) => InsertShape();
        _preview.RangeChanged += definition => SetDefinition(definition);
        _host.TargetChanged += OnTargetChanged;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _calculation?.Cancel(); _debounce.Stop(); _host.TargetChanged -= OnTargetChanged;
            if (_target is not null) { _target.Controller.Invalidated -= OnCanvasInvalidated; _target.Controller.ClearExtensionPreview(_previewOwner); }
            _preview.Dispose(); _handwriting.Dispose();
        };
        _root.SizeChanged += (_, _) => { body.ColumnDefinitions[0].Width = new GridLength(_root.ActualWidth < 850 ? Math.Min(360, _root.ActualWidth * .47) : 396); };
        Capture();
    }
    private static TextBox FormulaBox(string header)
    {
        var box = new TextBox { Header = header, FontFamily = new FontFamily("Cambria Math"), FontSize = 20, MinHeight = 48,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, PreventKeyboardDisplayOnProgrammaticFocus = true, MaxLength = 2048 };
        AutomationProperties.SetName(box, header); return box;
    }
    private static RichEditBox MathBox(string header)
    {
        var box = new RichEditBox
        {
            Header = header,
            MinHeight = 88,
            MaxHeight = 180,
            FontSize = 22,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            PreventKeyboardDisplayOnProgrammaticFocus = true,
            AcceptsReturn = false,
            IsReadOnly = true,
        };
        AutomationProperties.SetName(box, header);
        return box;
    }

    private static bool EnableMathMode(RichEditBox editor)
    {
        try
        {
            editor.IsReadOnly = false;
            editor.TextDocument.SetMathMode(RichEditMathMode.MathOnly);
            editor.TextDocument.SetMathML("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><msup><mi>x</mi><mn>2</mn></msup></math>");
            editor.TextDocument.GetMathML(out _);
            editor.IsReadOnly = true;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private void UpdateFormulaInputMode()
    {
        bool math = _formulaMode?.SelectedIndex == 1;
        _formula.Visibility = Visibility.Visible;
        _second.Visibility = (PlotKind)_kind.SelectedIndex == PlotKind.Parametric ? Visibility.Visible : Visibility.Collapsed;
        _formulaMath.Visibility = math && _mathModeAvailable && _formulaMathMl is not null ? Visibility.Visible : Visibility.Collapsed;
        _secondMath.Visibility = math && _mathModeAvailable && _secondMathMl is not null && (PlotKind)_kind.SelectedIndex == PlotKind.Parametric ? Visibility.Visible : Visibility.Collapsed;
        _formulaFallback.Visibility = math && !_mathModeAvailable && _formulaMathMl is not null ? Visibility.Visible : Visibility.Collapsed;
        _secondFallback.Visibility = math && !_mathModeAvailable && _secondMathMl is not null && (PlotKind)_kind.SelectedIndex == PlotKind.Parametric ? Visibility.Visible : Visibility.Collapsed;
        _mathStatus.Text = math ? "在输入栏编辑公式，或使用下方公式键盘。" : "线性表达式输入";
    }

    private void SetMathEditor(RichEditBox editor, string expression, bool second)
    {
        try
        {
            // The editable expression is authoritative, including when opening older saved plots.
            string mathMl = MathMarkup.ToMathML(expression);
            if (_mathModeAvailable)
            {
                try
                {
                    editor.IsReadOnly = false;
                    editor.TextDocument.SetMathML(mathMl);
                    editor.IsReadOnly = true;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    _mathModeAvailable = false;
                    UpdateFormulaInputMode();
                }
            }
            if (!_mathModeAvailable) (second ? _secondFallback : _formulaFallback).SetMathML(mathMl);
            if (second) _secondMathMl = mathMl;
            else _formulaMathMl = mathMl;
            UpdateFormulaInputMode();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or FormatException or ArgumentException)
        {
            if (second) _secondMathMl = null;
            else _formulaMathMl = null;
            UpdateFormulaInputMode();
            _mathStatus.Text = "公式无法排版：" + exception.GetBaseException().Message;
        }
    }

    private void OnLinearFormulaChanged(TextBox box, bool second)
    {
        if (_settingInputs) return;
        SetMathEditor(second ? _secondMath : _formulaMath, box.Text, second);
        QueueCalculation();
    }

    private void SetFocusedFormula(string value)
    {
        var box = _formulaMode.SelectedIndex == 1
            ? ReferenceEquals(_focused, _second) ? _second : _formula
            : _focused ?? _formula;
        box.Text = value;
        box.SelectionStart = value.Length;
    }

    private void HandleKeyboardKey(string key) => EnterKey(key);
    public void SendFormulaKey(string key) => HandleKeyboardKey(key);
    private NumberBox Number(string name, string header)
    {
        var box = new NumberBox { Header = header, Minimum = -1e8, Maximum = 1e8, SmallChange = .5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, MinHeight = 42 };
        AutomationProperties.SetName(box, "解析几何 " + name); box.ValueChanged += (_, _) => QueueCalculation(); return box;
    }
    private void AddRange(Grid grid, string name, string label, int row, int column)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var number = Number(name, label); _numbers[name] = number; Grid.SetRow(number, row); Grid.SetColumn(number, column); grid.Children.Add(number);
    }
    private UIElement BuildExamples()
    {
        var panel = new Grid { ColumnSpacing = 5, RowSpacing = 5 };
        for (int i = 0; i < 3; i++) panel.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 2; i++) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var examples = new[] { ("抛物线", new PlotDefinition()), ("圆", new PlotDefinition { Formula = "x^2+y^2=a^2", A = 5 }),
            ("双曲线", new PlotDefinition { Formula = "x^2/9-y^2/4=1" }), ("正弦", new PlotDefinition { Formula = "y=a*sin(x)+b", A = 3 }),
            ("椭圆", new PlotDefinition { Kind = PlotKind.Parametric, Formula = "a*cos(t)", SecondFormula = "b*sin(t)", A = 5, B = 3 }),
            ("玫瑰线", new PlotDefinition { Kind = PlotKind.Polar, Formula = "a*cos(b*t)", A = 5, B = 3 }) };
        for (int i = 0; i < examples.Length; i++)
        {
            var example = examples[i]; var button = new Button { Content = example.Item1, MinHeight = 40, MinWidth = 0, Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => SetDefinition(example.Item2); Grid.SetColumn(button, i % 3); Grid.SetRow(button, i / 3); panel.Children.Add(button);
        }
        return panel;
    }
    private void Capture()
    {
        if (_target is not null) { _target.Controller.ClearExtensionPreview(_previewOwner); _target.Controller.Invalidated -= OnCanvasInvalidated; }
        _target = _host.CaptureTarget(); _target.Controller.Invalidated += OnCanvasInvalidated;
        var selection = _target.Controller.Selection;
        var source = selection.FirstOrDefault()?.ShapeSource;
        _editing = source?.Provider == PlotShapes.Provider && selection.All(stroke => stroke.ShapeSource?.GroupId == source.GroupId)
            ? _target.Controller.Document.Strokes.Where(stroke => stroke.ShapeSource?.GroupId == source.GroupId).ToArray() : Array.Empty<InkStroke>();
        PlotDefinition definition = new();
        if (_editing.Count > 0)
        {
            try { definition = JsonSerializer.Deserialize<PlotDefinition>(source.Definition, PlotShapes.JsonOptions) ?? new(); definition.Validate(); }
            catch (Exception ex) when (ex is JsonException or FormatException) { _editing = Array.Empty<InkStroke>(); SetStatus("所选图形的公式数据无法读取，可插入新图形。"); }
        }
        _numbers["lineWidth"].Value = _editing.Count > 0 ? Math.Clamp(_editing[0].Attributes.BaseWidth, .5, 12) : 2.5;
        _insert.Content = _editing.Count > 0 ? "更新所选形状" : "插入形状";
        SetDefinition(definition);
    }
    public void SetDefinition(PlotDefinition definition)
    {
        _settingInputs = true;
        try
        {
            _kind.SelectedIndex = (int)definition.Kind; _formula.Text = definition.Formula ?? ""; _second.Text = definition.SecondFormula ?? "";
            SetMathEditor(_formulaMath, _formula.Text, false);
            SetMathEditor(_secondMath, _second.Text, true);
            _focused = _formula;
            _formula.Header = definition.Kind == PlotKind.Parametric ? "x(t)" : definition.Kind == PlotKind.Polar ? "r(t) · 弧度" : "公式";
            _numbers["xMin"].Value = definition.XMin; _numbers["xMax"].Value = definition.XMax; _numbers["yMin"].Value = definition.YMin; _numbers["yMax"].Value = definition.YMax;
            _numbers["tMin"].Value = definition.TMin; _numbers["tMax"].Value = definition.TMax; _numbers["a"].Value = definition.A; _numbers["b"].Value = definition.B; _numbers["c"].Value = definition.C;
            _numbers["tMin"].Visibility = _numbers["tMax"].Visibility = definition.Kind == PlotKind.Cartesian ? Visibility.Collapsed : Visibility.Visible;
            _axes.IsChecked = definition.IncludeAxes;
        }
        finally { _settingInputs = false; }
        UpdateFormulaInputMode();
        QueueCalculation();
    }

    private PlotDefinition ReadDefinition() => new()
    {
        Kind = (PlotKind)Math.Max(0, _kind.SelectedIndex), Formula = _formula.Text, SecondFormula = _second.Text,
        FormulaMathML = _formulaMathMl, SecondFormulaMathML = _secondMathMl,
        XMin = Value("xMin"), XMax = Value("xMax"), YMin = Value("yMin"), YMax = Value("yMax"),
        TMin = Value("tMin"), TMax = Value("tMax"), A = Value("a"), B = Value("b"), C = Value("c"), IncludeAxes = _axes.IsChecked == true,
    };
    private double Value(string key) => _numbers[key].Value;
    private void QueueCalculation()
    {
        if (_settingInputs || _closed || _debounce is null) return;
        _generation++; _calculation?.Cancel(); _insert.IsEnabled = false;
        _debounce.Stop(); _debounce.Start();
    }
    public async Task CalculateAsync()
    {
        if (_closed) return;
        _debounce.Stop();
        _pendingCalculation = CalculateCoreAsync();
        while (!_closed)
        {
            var task = _pendingCalculation;
            await task;
            if (_debounce.IsRunning) { _debounce.Stop(); _pendingCalculation = CalculateCoreAsync(); continue; }
            if (ReferenceEquals(task, _pendingCalculation)) return;
        }
    }
    private Task _pendingCalculation = Task.CompletedTask;
    private async Task CalculateCoreAsync()
    {
        if (_closed) return;
        _debounce.Stop();
        int generation = _generation; _calculation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _calculation = cancellation;
        try
        {
            var definition = ReadDefinition(); definition.Validate();
            string key = definition.Kind + "\n" + definition.Formula + "\n" + definition.SecondFormula;
            var cached = _compiledKey == key ? _compiled : null;
            var watch = Stopwatch.StartNew(); SetStatus("正在计算…");
            var result = await Task.Run(() =>
            {
                var compiled = cached ?? CompiledPlot.Compile(definition);
                cancellation.Token.ThrowIfCancellationRequested();
                return (Compiled: compiled, Mesh: PlotSampler.Sample(compiled, definition, cancellation.Token));
            }, cancellation.Token);
            if (_closed || generation != _generation || cancellation.IsCancellationRequested) return;
            _compiledKey = key; _compiled = result.Compiled; _mesh = result.Mesh; _definition = definition;
            CompletedGeneration = generation; LastCalculationMilliseconds = watch.ElapsedMilliseconds;
            _preview.SetPlot(_mesh, definition); UpdateCanvasPreview();
            _insert.IsEnabled = CanInsert();
            SetStatus(_mesh.Limited ? "已达到采样上限，可缩小坐标范围提高精度。" : _mesh.Paths.Count == 0 ? "当前范围内没有可绘制曲线。" : "已生成 " + _mesh.Paths.Sum(path => path.Length) + " 个顶点 · " + LastCalculationMilliseconds + " ms");
            if (!_host.IsCurrent(_target)) SetStatus("画布页面已切换，点击“使用当前画布 / 所选图形”可重新选择目标。");
            else if (!_target.CanEdit) SetStatus("当前文档只允许查看，请选择可编辑画布。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && generation == _generation)
            {
                _insert.IsEnabled = false; _mesh = null; _preview.SetPlot(null, _definition ?? new());
                _target.Controller.ClearExtensionPreview(_previewOwner); SetStatus(ex.GetBaseException().Message);
            }
        }
        finally { cancellation.Dispose(); if (ReferenceEquals(_calculation, cancellation)) _calculation = null; }
    }
    private bool CanInsert() => !_closed && _mesh?.Paths.Count > 0 && _host.IsCurrent(_target) && _target.CanEdit && CompletedGeneration == _generation;
    private Matrix3x2 Placement()
    {
        if (_editing.Count > 0) return _editing[0].ObjectFrame.Transform;
        if (!Matrix3x2.Invert(_target.Controller.ViewTransform, out var inverse)) throw new InvalidOperationException("当前画布缩放无效。");
        var center = _target.Controller.ViewCenter;
        float scale = Math.Min(1, Math.Min(center.X * 1.5f / (float)PlotSampler.Width, center.Y * 1.4f / (float)PlotSampler.Height));
        return Matrix3x2.CreateScale(Math.Max(.1f, scale)) * Matrix3x2.CreateTranslation(center.X - (float)PlotSampler.Width * scale / 2, center.Y - (float)PlotSampler.Height * scale / 2) * inverse;
    }
    private StrokeAttributes Attributes()
    {
        var attributes = (_editing.Count > 0 ? _editing[0].Attributes : _target.Controller.CurrentAttributes).Clone();
        attributes.BaseWidth = (float)Math.Clamp(Value("lineWidth"), .5, 12); return attributes;
    }
    private void UpdateCanvasPreview()
    {
        if (_closed || _previewUpdating || _mesh is null) return;
        _previewUpdating = true;
        try
        {
            _lastView = _target.Controller.ViewTransform; _lastFrame = _editing.Count > 0 ? _editing[0].ObjectFrame : null;
            if (_live.IsChecked != true || !CanInsert()) { _target.Controller.ClearExtensionPreview(_previewOwner); return; }
            _host.EnsureVisible(_target);
            _target.Controller.SetExtensionPreview(_previewOwner, PlotShapes.Create(_mesh, _definition, Attributes(), Placement(), preview: true));
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { _previewUpdating = false; }
    }
    public bool InsertShape()
    {
        if (!CanInsert()) return false;
        try
        {
            var strokes = PlotShapes.Create(_mesh, _definition, Attributes(), Placement(), _editing.FirstOrDefault()?.ShapeSource?.GroupId);
            if (!_host.CommitShape(_target, strokes, _editing)) { SetStatus("目标画布或图形已变化，请重新选择目标后插入。"); return false; }
            _editing = strokes; _insert.Content = "更新所选形状";
            _lastFrame = _editing[0].ObjectFrame; _lastView = _target.Controller.ViewTransform;
            _target.Controller.ClearExtensionPreview(_previewOwner);
            SetStatus("已插入可编辑形状 · 支持选择、旋转、克隆和撤销"); return true;
        }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); return false; }
    }
    private void OnTargetChanged()
    {
        if (_closed || _host.IsCurrent(_target)) return;
        _target.Controller.ClearExtensionPreview(_previewOwner); _insert.IsEnabled = false;
        SetStatus("画布页面已切换，点击“使用当前画布 / 所选图形”可重新选择目标。");
    }
    private void OnCanvasInvalidated()
    {
        if (_previewUpdating || _closed) return;
        if (_lastView != _target.Controller.ViewTransform || (_editing.Count > 0 && _lastFrame != _editing[0].ObjectFrame)) UpdateCanvasPreview();
    }
    private void SetStatus(string text) { if (!_closed) _status.Text = text; }
    private void EnterKey(string key)
    {
        var box = _focused ?? _formula;
        if (key == "⇥")
        {
            int next = box.Text.IndexOf('□', box.SelectionStart + box.SelectionLength);
            if (next < 0) next = box.Text.IndexOf('□');
            if (next >= 0) { box.SelectionStart = next; box.SelectionLength = 1; }
            return;
        }
        try
        {
            var edit = FormulaEditing.Apply(box.Text, box.SelectionStart, box.SelectionLength, key, 2048);
            box.Text = edit.Text;
            SetMathEditor(ReferenceEquals(box, _second) ? _secondMath : _formulaMath, box.Text, ReferenceEquals(box, _second));
            box.SelectionStart = edit.SelectionStart; box.SelectionLength = edit.SelectionLength;
        }
        catch (FormatException ex) { SetStatus(ex.Message); }
    }
}
