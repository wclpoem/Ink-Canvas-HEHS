using System.Globalization;
using System.Numerics;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed partial class GeometryWorkbenchWindow : Window
{
    private sealed class AlgebraRow
    {
        public ListViewItem Item { get; init; }
        public Button VisibilityButton { get; init; }
        public Ellipse VisibilityDot { get; init; }
        public MathFormulaView FormulaView { get; init; }
        public TextBlock FallbackTitle { get; init; }
        public TextBlock ValueText { get; init; }
        public Grid SliderPanel { get; init; }
        public PointerSlider Slider { get; init; }
        public TextBlock SliderRange { get; init; }
        public TouchIconButton PlayButton { get; init; }
        public bool SyncingSlider { get; set; }
    }

    private readonly ICanvasToolsHost _host;
    private CanvasToolTarget _target;
    private IReadOnlyList<InkStroke> _editing = Array.Empty<InkStroke>();
    private readonly object _previewOwner = new();
    private readonly Grid _root = new() { RowSpacing = 0 };
    private readonly ConstructionKernel _kernel = new();
    private readonly ConstructionView _view = new();
    private readonly ListView _objects = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, AlgebraRow> _rows = new();
    private readonly StackPanel _properties = new() { Spacing = 8, Padding = new Thickness(12) };
    private readonly StackPanel _casHistory = new() { Spacing = 10, Padding = new Thickness(12) };
    private readonly TextBox _input = FormulaBox("输入表达式或指令，例如 A=(1,2)、f(x)=a*x^2、Solve(x^2=2,x)");
    private readonly MathFormulaEditor _formulaEditor = new() { Visibility = Visibility.Collapsed };
    private readonly MathFormulaView _inputMath = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _mathDebounce;
    private TextBox _focused;
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.78, Margin = new Thickness(12, 4, 12, 6) };
    private readonly Button _insert = new() { Content = "插入画布", MinHeight = 38, Padding = new Thickness(14, 6, 14, 6) };
    private readonly CheckBox _live = new() { Content = "实时预览到画布", MinHeight = 38, VerticalAlignment = VerticalAlignment.Center };
    private readonly SegmentedSelector _mode = new(new[] { "2D", "3D" }) { Width = 108, Height = 36 };
    private Border _toolBanner;
    private TextBlock _toolBannerText;
    private Expander _keyboardDock;
    private SegmentedSelector _inputMethod;
    private SegmentedSelector _sidebarTabs;
    private Border _sidebarContainer;
    private ColumnDefinition _sidebarColumn;
    private bool _sidebarCollapsed;
    private bool _syncingView, _syncingFormula;
    private readonly DispatcherQueueTimer _debounce, _animation;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _calculation;
    private Task _pending = Task.CompletedTask;
    private bool _closed, _updatingRows, _casBusy, _playing;
    private int _renderedRevision = -1;
    private int _protocolStep = int.MaxValue;
    private int _suppressScriptsRevision = -1;
    private DateTime _animationTime;
    private readonly List<string> _toolPoints = new();

    public ConstructionDocument Document { get; } = new();
    public ConstructionEvaluation Evaluation { get; private set; }
    public AlgebraResult LastCasResult { get; private set; }
    public FormulaKeyboard Keyboard { get; } = new();
    public HandwritingInput Handwriting { get; } = new();
    public string StatusText => _status.Text;
    public TextBox FormulaInput => _input;
    public string FormulaMathML => _inputMath.MathML ?? _formulaEditor.MathML;
    public bool FormulaUsesRichEdit => _inputMath.UsesRichEdit || _formulaEditor.UsesRichEdit;

    public GeometryWorkbenchWindow(ICanvasToolsHost host)
    {
        _host = host;
        Title = "解析几何 — Ink Canvas";
        AutomationProperties.SetName(_inputMath, "输入公式排版");
        _mathDebounce = DispatcherQueue.CreateTimer();
        _mathDebounce.IsRepeating = false;
        _mathDebounce.Interval = TimeSpan.FromMilliseconds(90);
        _mathDebounce.Tick += (_, _) => UpdateMathPreview();
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.IsRepeating = false;
        _debounce.Interval = TimeSpan.FromMilliseconds(28);
        _debounce.Tick += (_, _) => _pending = CalculateCoreAsync();
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(40);
        _animation.Tick += (_, _) => Animate();
        BuildInterface();
        Content = _root;
        host.ConfigureWindow(this, 1380, 940);
        WindowFrame.Attach(this, _root).UseMica();
        Document.Changed += QueueCalculation;
        _view.GestureStarted += Document.BeginGesture;
        _view.GestureEnded += Document.EndGesture;
        _view.ViewChanged += Document.SetView;
        _view.PointMoved += (label, point) => Document.Update(label, item => item with { Definition = Coordinates(point) });
        _view.Clicked += GraphClicked;
        _view.ControlChanged += (label, definition) => Guard(() => Document.Update(label, item => item with { Definition = definition }));
        _view.ControlClicked += label => RunEventScript(Document.State.Objects.First(item => item.Label == label).OnClick);
        _objects.SelectionChanged += (_, _) => SelectObject((_objects.SelectedItem as ListViewItem)?.Tag as string);
        _root.ActualThemeChanged += (_, _) => { if (Evaluation is not null) RefreshObjects(); };
        _host.TargetChanged += TargetChanged;
        Closed += (_, _) => DisposeWorkbench();
        CaptureTarget();
        if (Document.State.Objects.Count == 0) Document.Execute("a=1;f(x)=a*x^2;A=(-2,1);B=(2,1);s=Segment(A,B);M=Midpoint(A,B)");
    }

    private void BuildInterface()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto }); // Row 0: GeoGebra Header Bar
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: Main Split Workspace
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto }); // Row 2: Bottom Keyboard / Handwriting Dock
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto }); // Row 3: Status Bar

        // --- Row 0: Compact GeoGebra Top Header Bar ---
        var topBar = new Grid { Padding = new Thickness(12, 4, 12, 6), ColumnSpacing = 8 };
        topBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        var leftActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += () => { if (!_syncingView) Document.SetView(Document.State with { ThreeDimensional = _mode.SelectedIndex == 1 }); };
        leftActions.Children.Add(_mode);
        leftActions.Children.Add(HeaderIconButton(Glyphs.Undo, "撤销", () => { StopAnimation(); Document.Undo(); }));
        leftActions.Children.Add(HeaderIconButton(Glyphs.Redo, "重做", () => { StopAnimation(); Document.Redo(); }));
        leftActions.Children.Add(HeaderIconButton(Glyphs.Play, "播放参数", ToggleAnimation));

        var fileButton = new Button { Content = "文件 ▾", MinHeight = 36, Padding = new Thickness(10, 4, 10, 4) };
        var fileMenu = new MenuFlyout();
        void AddMenuItem(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => Guard(action);
            fileMenu.Items.Add(item);
        }
        AddMenuItem("新建构造", () => { StopAnimation(); _protocolStep = int.MaxValue; _view.SelectedLabel = null; Document.Load(new()); _editing = Array.Empty<InkStroke>(); _insert.Content = "插入画布"; });
        AddMenuItem("打开 (.icgeo)…", () => RunFileAction(OpenAsync));
        AddMenuItem("保存 (.icgeo)…", () => RunFileAction(SaveAsync));
        fileMenu.Items.Add(new MenuFlyoutSeparator());
        var exportSub = new MenuFlyoutSubItem { Text = "导出" };
        foreach (string format in new[] { "SVG", "PNG", "PDF", "TikZ", "STL" })
        {
            string value = format;
            var sub = new MenuFlyoutItem { Text = value };
            sub.Click += (_, _) => RunFileAction(() => ExportAsync(value));
            exportSub.Items.Add(sub);
        }
        fileMenu.Items.Add(exportSub);
        fileMenu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem("使用当前画布 / 所选构造", CaptureTarget);
        fileButton.Flyout = fileMenu;
        leftActions.Children.Add(fileButton);

        var examplesButton = new Button { Content = "指令与示例", MinHeight = 36, Padding = new Thickness(10, 4, 10, 4) };
        examplesButton.Click += (_, _) => ShowCommandHelp(examplesButton);
        leftActions.Children.Add(examplesButton);
        topBar.Children.Add(leftActions);

        var rightActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        rightActions.Children.Add(_live);
        _insert.Style = Application.Current?.Resources.TryGetValue("AccentButtonStyle", out object accentStyle) == true
            ? accentStyle as Style : null;
        rightActions.Children.Add(_insert);
        Grid.SetColumn(rightActions, 2);
        topBar.Children.Add(rightActions);
        _root.Children.Add(topBar);

        _insert.Click += (_, _) => InsertShape();
        _live.Checked += (_, _) => PreviewCanvas();
        _live.Unchecked += (_, _) => _target?.Controller.ClearExtensionPreview(_previewOwner);

        // --- Row 1: GeoGebra Split Workspace (Left Rail + Sidebar + Full-Bleed Graphics View) ---
        var workspace = new Grid { ColumnSpacing = 0 };
        workspace.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); // Col 0: GeoGebra Vertical Nav Rail
        _sidebarColumn = new ColumnDefinition { Width = new GridLength(392) };
        workspace.ColumnDefinitions.Add(_sidebarColumn); // Col 1: Collapsible Algebra/Tools/Inspector/CAS Sidebar
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); // Col 2: Graphics Canvas
        Grid.SetRow(workspace, 1);
        _root.Children.Add(workspace);

        // Col 0: GeoGebra Vertical Navigation Rail
        var rail = new Grid { Width = 54, Padding = new Thickness(4, 6, 4, 8) };
        rail.RowDefinitions.Add(new() { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        rail.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var railStack = new StackPanel { Spacing = 4 };
        var railButtons = new TouchIconButton[4];
        var railDefs = new[]
        {
            (Glyphs.Calculator, "代数"),
            (Glyphs.ShapesWpf, "工具"),
            (Glyphs.Settings, "属性"),
            (Glyphs.Tools, "CAS"),
        };
        for (int i = 0; i < railDefs.Length; i++)
        {
            var btn = new TouchIconButton
            {
                Glyph = railDefs[i].Item1,
                Label = railDefs[i].Item2,
                IconSize = 18,
                LabelFontSize = 10,
                MinWidth = 46,
                MinHeight = 48,
                Padding = new Thickness(2, 4, 2, 4),
            };
            railButtons[i] = btn;
            railStack.Children.Add(btn);
        }
        rail.Children.Add(railStack);

        var collapseBtn = new TouchIconButton
        {
            Glyph = Glyphs.ChevronLeft,
            IconSize = 16,
            MinWidth = 44,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(collapseBtn, "折叠 / 展开左侧栏");
        collapseBtn.Click += (_, _) =>
        {
            ToggleSidebar();
            collapseBtn.Glyph = _sidebarCollapsed ? Glyphs.ChevronRight : Glyphs.ChevronLeft;
        };
        Grid.SetRow(collapseBtn, 2);
        rail.Children.Add(collapseBtn);
        workspace.Children.Add(rail);

        // Col 1: Sidebar Content (Algebra + Tools + Properties + CAS)
        var sidebarGrid = new Grid { RowSpacing = 6, Padding = new Thickness(6, 4, 8, 6) };
        sidebarGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); // Compact SegmentedSelector
        sidebarGrid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); // Active tab content
        sidebarGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); // GeoGebra '+' Formula Entry Card

        // Keep SegmentedSelector with Algebraic(0), Properties(1), CAS(2), Tools(3) or map rail cleanly!
        // Note: WorkbenchDiagnostics uses SegmentedSelector.First() with index 2 = CAS, index 0 = Algebra.
        _sidebarTabs = new SegmentedSelector(new[] { "代数", "属性", "CAS", "工具" }) { Height = 36 };
        AutomationProperties.SetName(_sidebarTabs, "工作区视图切换");
        sidebarGrid.Children.Add(_sidebarTabs);

        var toolsPanel = BuildToolsPanel();
        var propertyScroll = new ScrollViewer { Content = _properties, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var casScroll = new ScrollViewer { Content = _casHistory, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        foreach (var element in new FrameworkElement[] { _objects, propertyScroll, casScroll, toolsPanel })
        {
            Grid.SetRow(element, 1);
            sidebarGrid.Children.Add(element);
        }

        // Map rail buttons (0=Algebra, 1=Tools->3, 2=Properties->1, 3=CAS->2)
        int[] railToTab = { 0, 3, 1, 2 };
        for (int r = 0; r < railButtons.Length; r++)
        {
            int mapped = railToTab[r];
            railButtons[r].Click += (_, _) => { if (_sidebarCollapsed) ToggleSidebar(); _sidebarTabs.SelectedIndex = mapped; };
        }
        _sidebarTabs.SelectionChanged += () =>
        {
            int idx = _sidebarTabs.SelectedIndex;
            _objects.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            propertyScroll.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            casScroll.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            toolsPanel.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            for (int r = 0; r < railButtons.Length; r++) railButtons[r].IsSelected = railToTab[r] == idx;
        };
        _sidebarTabs.SelectedIndex = 0;
        _properties.Children.Add(new TextBlock { Text = "在画布或代数列表中选择一个对象以编辑定义、颜色、线宽与参数。", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        _casHistory.Children.Add(new TextBlock { Text = "支持精确化简、展开、因式分解、解方程、微积分、矩阵和复数计算。输入后点击“CAS 计算”。", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });

        // GeoGebra '+' Formula Entry Card (WYSIWYG MathML + Linear synced)
        var entryCard = BuildFormulaEntryCard();
        Grid.SetRow(entryCard, 2);
        sidebarGrid.Children.Add(entryCard);

        _sidebarContainer = new Border
        {
            Child = sidebarGrid,
            BorderThickness = new Thickness(1, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
        };
        Grid.SetColumn(_sidebarContainer, 1);
        workspace.Children.Add(_sidebarContainer);

        // Col 2: Full-Bleed GeoGebra Graphics Canvas + Floating Overlays
        var canvasHost = new Grid { Margin = new Thickness(6, 4, 10, 6) };
        var frame = new Border
        {
            Child = _view,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(44, 100, 116, 139)),
        };
        canvasHost.Children.Add(frame);

        // Floating Bottom-Center Active Tool Banner (GeoGebra style)
        _toolBannerText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
        var cancelTool = new Button
        {
            Content = "完成",
            MinHeight = 30,
            Padding = new Thickness(10, 2, 10, 2),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Background = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
        };
        cancelTool.Click += (_, _) => SelectTool(0);
        _toolBanner = new Border
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(14, 6, 8, 6),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(228, 30, 41, 59)),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _toolBannerText, cancelTool } },
        };
        canvasHost.Children.Add(_toolBanner);

        // Floating Bottom-Left Keyboard/Handwriting Toggle Button (GeoGebra signature!)
        var floatingKeyboardBtn = new Button
        {
            Content = "⌨ 键盘 / 手写",
            MinHeight = 40,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 0, 14),
            Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 100, 116, 139)),
        };
        floatingKeyboardBtn.Click += (_, _) => { if (_keyboardDock is not null) _keyboardDock.IsExpanded = !_keyboardDock.IsExpanded; };
        canvasHost.Children.Add(floatingKeyboardBtn);

        // Floating Bottom-Right GeoGebra View Controls (+, -, Home, Settings)
        var viewControls = BuildFloatingViewControls();
        canvasHost.Children.Add(viewControls);

        Grid.SetColumn(canvasHost, 2);
        workspace.Children.Add(canvasHost);

        // --- Row 2: Collapsible Bottom Math Keyboard & Windows-Style Math Handwriting Panel ---
        var inputPanel = new Grid { Padding = new Thickness(0, 4, 0, 0) };
        _inputMethod = new SegmentedSelector(new[] { "数学键盘", "手写公式画板" }) { Width = 280, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        _inputMethod.SetOptionContent(0, new TextBlock { Text = "数学键盘", VerticalAlignment = VerticalAlignment.Center }, 40);
        _inputMethod.SetOptionContent(1, new TextBlock { Text = "手写公式画板", VerticalAlignment = VerticalAlignment.Center }, 40);
        inputPanel.Children.Add(Keyboard);
        inputPanel.Children.Add(Handwriting);
        _inputMethod.SelectionChanged += () =>
        {
            Keyboard.Visibility = _inputMethod.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            Handwriting.Visibility = _inputMethod.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        _inputMethod.SelectedIndex = 0;

        var inputScroll = new ScrollViewer
        {
            Content = inputPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _keyboardDock = new Expander
        {
            Header = _inputMethod,
            Content = inputScroll,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(10, 0, 10, 0),
        };
        AutomationProperties.SetName(_keyboardDock, "数学键盘与手写输入");
        _inputMethod.SelectionChanged += () => _keyboardDock.IsExpanded = true;
        Grid.SetRow(_keyboardDock, 2);
        _root.Children.Add(_keyboardDock);

        Keyboard.KeyPressed += EnterKey;
        Handwriting.CandidateChosen += value => ApplyHandwritingFormula(value, null);
        Handwriting.FormulaInserted += (expr, ml) => ApplyHandwritingFormula(expr, ml);

        // --- Row 3: Status Bar ---
        Grid.SetRow(_status, 3);
        _root.Children.Add(_status);

        DeferredLayout.Observe(_root, () =>
        {
            if (_closed) return;
            if (!_sidebarCollapsed)
                _sidebarColumn.Width = new GridLength(Math.Clamp(_root.ActualWidth * 0.30, 300, 420));
            inputScroll.MaxHeight = Math.Clamp(_root.ActualHeight * 0.40, 180, 380);
        });
    }

    private FrameworkElement BuildFormulaEntryCard()
    {
        var cardStack = new StackPanel { Spacing = 6 };

        // Header row: "+" indicator + WYSIWYG MathML toggle + Handwriting quick launch
        var entryHeader = new Grid { ColumnSpacing = 6 };
        entryHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        entryHeader.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        entryHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        entryHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        entryHeader.Children.Add(new TextBlock
        {
            Text = "＋ 输入公式",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.78,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var wysiwygToggle = new ToggleButton
        {
            Content = "MathML 编辑",
            FontSize = 11,
            MinHeight = 28,
            Padding = new Thickness(8, 2, 8, 2),
            IsChecked = _formulaEditor.UsesRichEdit,
            IsEnabled = _formulaEditor.UsesRichEdit,
        };
        ToolTipService.SetToolTip(wysiwygToggle, "开启 WinUI 3 RichEditBox MathML 所见即所得直接编辑");
        wysiwygToggle.Checked += (_, _) => _formulaEditor.Visibility = Visibility.Visible;
        wysiwygToggle.Unchecked += (_, _) => _formulaEditor.Visibility = Visibility.Collapsed;
        _formulaEditor.Visibility = _formulaEditor.UsesRichEdit ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(wysiwygToggle, 2);
        entryHeader.Children.Add(wysiwygToggle);

        var openInkBtn = new Button
        {
            Content = "✍ 手写",
            FontSize = 11,
            MinHeight = 28,
            Padding = new Thickness(8, 2, 8, 2),
        };
        ToolTipService.SetToolTip(openInkBtn, "打开公式手写画板");
        openInkBtn.Click += (_, _) => { _inputMethod.SelectedIndex = 1; _keyboardDock.IsExpanded = true; };
        Grid.SetColumn(openInkBtn, 3);
        entryHeader.Children.Add(openInkBtn);

        cardStack.Children.Add(entryHeader);

        // Linear formula box (always available & synced)
        _focused = _input;
        _input.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { ExecuteInput(); args.Handled = true; } };
        _input.GotFocus += (_, _) => _focused = _input;
        _input.TextChanged += (_, _) =>
        {
            if (_syncingFormula) return;
            _mathDebounce.Stop();
            _mathDebounce.Start();
        };
        cardStack.Children.Add(_input);

        // Live WYSIWYG MathML editor & preview
        _formulaEditor.ExpressionChanged += expr =>
        {
            if (_syncingFormula) return;
            _syncingFormula = true;
            try
            {
                _input.Text = expr;
                _input.SelectionStart = expr.Length;
                _inputMath.Visibility = !string.IsNullOrWhiteSpace(expr) && _inputMath.SetExpression(expr)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _syncingFormula = false; }
        };
        _formulaEditor.Submitted += ExecuteInput;
        var mathFrame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 100, 116, 139)),
            Child = new StackPanel { Children = { _formulaEditor, _inputMath } },
        };
        cardStack.Children.Add(mathFrame);

        // Action buttons row
        var actions = new Grid { ColumnSpacing = 6 };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var execute = new Button { Content = "创建 / 更新", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        execute.Click += (_, _) => ExecuteInput();
        actions.Children.Add(execute);
        var cas = new Button { Content = "CAS 计算", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        cas.Click += async (_, _) => { _sidebarTabs.SelectedIndex = 2; await CalculateCasAsync(); };
        Grid.SetColumn(cas, 1);
        actions.Children.Add(cas);
        cardStack.Children.Add(actions);

        return new Border
        {
            Child = cardStack,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(44, 100, 116, 139)),
        };
    }

    private FrameworkElement BuildFloatingViewControls()
    {
        var stack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 14, 14),
        };
        Button Pill(string glyph, string tip, Action click)
        {
            var btn = new TouchIconButton
            {
                Glyph = glyph,
                IconSize = 16,
                MinWidth = 40,
                MinHeight = 40,
                CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromArgb(236, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(64, 100, 116, 139)),
            };
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, _) => click();
            return btn;
        }

        stack.Children.Add(Pill(Glyphs.ZoomIn, "放大视图", () => _view.ZoomIn()));
        stack.Children.Add(Pill(Glyphs.ZoomOut, "缩小视图", () => _view.ZoomOut()));
        stack.Children.Add(Pill(Glyphs.FitPage, "重置视图", () => _view.ResetView()));

        var settingsBtn = new TouchIconButton
        {
            Glyph = Glyphs.Settings,
            IconSize = 16,
            MinWidth = 40,
            MinHeight = 40,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(236, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 100, 116, 139)),
        };
        ToolTipService.SetToolTip(settingsBtn, "视图与网格选项");
        var viewMenu = new MenuFlyout();
        void ViewOption(string label, Func<ConstructionSnapshot, ConstructionSnapshot> update)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => Document.SetView(update(Document.State));
            viewMenu.Items.Add(item);
        }
        ViewOption("显示 / 隐藏网格", state => state with { Grid = !state.Grid });
        ViewOption("显示 / 隐藏坐标轴", state => state with { Axes = !state.Axes });
        ViewOption("极坐标 / 直角坐标网格", state => state with { PolarGrid = !state.PolarGrid });
        viewMenu.Items.Add(new MenuFlyoutSeparator());
        ViewOption("透视 / 正交投影", state => state with { Perspective = !state.Perspective, Oblique = false });
        ViewOption("倾斜投影", state => state with { Oblique = !state.Oblique, Perspective = false });
        var dragZ = new ToggleMenuFlyoutItem { Text = "3D 拖点沿 Z 轴移动" };
        dragZ.Click += (_, _) => _view.MoveAlongZAxis = dragZ.IsChecked;
        viewMenu.Items.Add(dragZ);
        settingsBtn.Flyout = viewMenu;
        stack.Children.Add(settingsBtn);

        return stack;
    }

    private static TouchIconButton HeaderIconButton(string glyph, string tooltip, Action action)
    {
        var button = new TouchIconButton
        {
            Glyph = glyph,
            IconSize = 17,
            MinWidth = 38,
            MinHeight = 36,
            Padding = new Thickness(6),
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        _sidebarContainer.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _sidebarColumn.Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(Math.Clamp(_root.ActualWidth * 0.30, 300, 420));
    }

    private void ApplyHandwritingFormula(string expression, string mathMl)
    {
        var box = _focused ?? _input;
        _syncingFormula = true;
        try
        {
            box.Text = expression;
            box.SelectionStart = expression.Length;
            if (ReferenceEquals(box, _input))
            {
                if (!string.IsNullOrWhiteSpace(mathMl))
                {
                    try { _formulaEditor.SetMathML(mathMl); _inputMath.SetMathML(mathMl); _inputMath.Visibility = Visibility.Visible; }
                    catch (Exception) { UpdateMathPreview(); }
                }
                else UpdateMathPreview();
            }
        }
        finally { _syncingFormula = false; }
    }

    private static TextBox FormulaBox(string placeholder)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            MinHeight = 42,
            FontFamily = new FontFamily("Cambria Math"),
            FontSize = 17,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            PreventKeyboardDisplayOnProgrammaticFocus = true,
            MaxLength = 4096,
        };
        AutomationProperties.SetName(box, placeholder);
        return box;
    }

    private void ExecuteInput()
    {
        Guard(() =>
        {
            string label = Document.Execute(_input.Text);
            _view.SelectedLabel = label;
            _input.Text = "";
            _formulaEditor.Clear();
            _inputMath.Visibility = Visibility.Collapsed;
        });
    }

    public string Execute(string input) => Document.Execute(input);

    private void QueueCalculation()
    {
        if (_closed) return;
        _syncingView = true;
        _mode.SelectedIndex = Document.State.ThreeDimensional ? 1 : 0;
        _syncingView = false;
        _calculation?.Cancel();
        _insert.IsEnabled = false;
        _debounce.Stop(); _debounce.Start();
    }

    public async Task CalculateAsync()
    {
        _debounce.Stop();
        _pending = CalculateCoreAsync();
        while (!_closed)
        {
            var pending = _pending;
            await pending;
            if (_debounce.IsRunning) { _debounce.Stop(); _pending = CalculateCoreAsync(); continue; }
            if (ReferenceEquals(pending, _pending)) return;
        }
    }

    private Task _worker = Task.CompletedTask;
    private async Task CalculateCoreAsync()
    {
        if (_closed) return;
        _debounce.Stop();
        _calculation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(8));
        _calculation = cancellation;
        int revision = Document.Revision;
        var snapshot = Document.State;
        var previous = _worker;
        try
        {
            var operation = Task.Run(async () =>
            {
                try { await previous; } catch (Exception) { }
                cancellation.Token.ThrowIfCancellationRequested();
                return _kernel.Evaluate(snapshot, cancellation.Token);
            }, cancellation.Token);
            _worker = operation;
            var result = await operation;
            if (_closed || cancellation.IsCancellationRequested || revision != Document.Revision) return;
            var previousEvaluation = Evaluation;
            Evaluation = result; _renderedRevision = revision;
            RefreshObjects(); RenderView();
            _insert.IsEnabled = CanInsert();
            PreviewCanvas();
            int errors = result.Values.Count(pair => !pair.Value.Defined);
            SetStatus($"{snapshot.Objects.Count} 个对象 · 重算 {result.EvaluatedObjects} 个 · {result.Elapsed.TotalMilliseconds:F0} ms" + (errors > 0 ? $" · {errors} 个定义需修改" : ""));
            if (previousEvaluation is not null && revision != _suppressScriptsRevision)
            {
                var scripts = snapshot.Objects.Where(item => item.OnUpdate.Length > 0
                    && previousEvaluation.Values.TryGetValue(item.Label, out var before)
                    && result.Values.TryGetValue(item.Label, out var after) && after.Defined && !ReferenceEquals(before, after))
                    .Select(item => item.OnUpdate).ToArray();
                if (scripts.Length > 0) RunEventScripts(scripts);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed && revision == Document.Revision) SetStatus("计算已取消或达到时间上限，请简化表达式或缩小范围。");
        }
        catch (Exception ex) { if (!_closed && revision == Document.Revision) SetStatus(ex.GetBaseException().Message); }
        finally
        {
            if (ReferenceEquals(_calculation, cancellation)) _calculation = null;
            cancellation.Dispose();
        }
    }

    private void RenderView()
    {
        if (Evaluation is null) return;
        var state = Document.State;
        if (_protocolStep < state.Objects.Count)
            state = state with { Objects = state.Objects.Select((item, index) => item with { Visible = item.Visible && index < _protocolStep }).ToList() };
        _view.SetConstruction(state, Evaluation);
    }

    private void RefreshObjects()
    {
        _updatingRows = true;
        try
        {
            var names = Document.State.Objects.Select(item => item.Label).ToHashSet();
            foreach (string name in _rows.Keys.Where(name => !names.Contains(name)).ToArray())
            { _objects.Items.Remove(_rows[name].Item); _rows.Remove(name); }

            foreach (var item in Document.State.Objects)
            {
                if (!_rows.TryGetValue(item.Label, out var row))
                {
                    row = CreateAlgebraRow(item.Label);
                    _objects.Items.Add(row.Item);
                    _rows[item.Label] = row;
                }

                var evaluated = Evaluation.Values[item.Label];
                var objColor = Color.FromArgb(255, (byte)(item.Color >> 16), (byte)(item.Color >> 8), (byte)item.Color);
                var brush = new SolidColorBrush(objColor);
                row.VisibilityDot.Stroke = brush;
                row.VisibilityDot.Fill = item.Visible ? brush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                // Render formula in WYSIWYG MathML (GeoGebra Algebra card style!)
                string expressionForMath = item.Label + "=" + item.Definition;
                bool hasMath = evaluated.Defined && row.FormulaView.SetExpression(expressionForMath);
                row.FormulaView.Visibility = hasMath ? Visibility.Visible : Visibility.Collapsed;
                row.FallbackTitle.Visibility = hasMath ? Visibility.Collapsed : Visibility.Visible;
                row.FallbackTitle.Text = item.Label + " = " + item.Definition;

                row.ValueText.Text = evaluated.Defined
                    ? (string.Equals(evaluated.Display, item.Definition, StringComparison.Ordinal) ? "" : "→ " + evaluated.Display)
                    : evaluated.Error;
                row.ValueText.Visibility = string.IsNullOrEmpty(row.ValueText.Text) ? Visibility.Collapsed : Visibility.Visible;
                row.ValueText.Foreground = new SolidColorBrush(evaluated.Defined
                    ? _root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(210, 214, 222, 234) : Color.FromArgb(210, 71, 85, 105)
                    : Microsoft.UI.Colors.Firebrick);

                // GeoGebra inline slider for free scalar parameters!
                bool isFreeScalar = evaluated.Defined && evaluated.Kind == GeometryKind.Scalar
                    && double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                row.SliderPanel.Visibility = isFreeScalar ? Visibility.Visible : Visibility.Collapsed;
                if (isFreeScalar)
                {
                    row.SyncingSlider = true;
                    try
                    {
                        row.Slider.Minimum = item.SliderMin;
                        row.Slider.Maximum = item.SliderMax;
                        row.Slider.StepFrequency = item.SliderStep;
                        row.Slider.Value = Math.Clamp(evaluated.Number, item.SliderMin, item.SliderMax);
                        row.SliderRange.Text = $"{item.SliderMin:G3} .. {item.SliderMax:G3}";
                        row.PlayButton.Glyph = _playing && item.Animate ? Glyphs.Pause : Glyphs.Play;
                    }
                    finally { row.SyncingSlider = false; }
                }

                ToolTipService.SetToolTip(row.Item, item.Label + " = " + item.Definition);
            }
            if (_view.SelectedLabel is string selected && _rows.TryGetValue(selected, out var selectedRow))
                _objects.SelectedItem = selectedRow.Item;
        }
        finally { _updatingRows = false; }
    }

    private AlgebraRow CreateAlgebraRow(string label)
    {
        var dot = new Ellipse { Width = 18, Height = 18, StrokeThickness = 2.2 };
        var visBtn = new Button
        {
            Content = dot,
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(visBtn, label + " 可见");
        visBtn.Click += (_, _) =>
        {
            if (!_updatingRows) Document.Update(label, current => current with { Visible = !current.Visible });
        };

        var formulaView = new MathFormulaView(compact: true);
        var fallbackTitle = new TextBlock { FontFamily = new FontFamily("Cambria Math"), FontSize = 16, TextWrapping = TextWrapping.Wrap };
        var valueText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };

        // Inline GeoGebra Slider row
        var playBtn = new TouchIconButton
        {
            Glyph = Glyphs.Play,
            IconSize = 14,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(17),
        };
        var slider = new PointerSlider { Minimum = -5, Maximum = 5, StepFrequency = 0.1, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        var sliderRange = new TextBlock { FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        var sliderPanel = new Grid { ColumnSpacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        sliderPanel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sliderPanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sliderPanel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sliderPanel.Children.Add(playBtn);
        Grid.SetColumn(slider, 1); sliderPanel.Children.Add(slider);
        Grid.SetColumn(sliderRange, 2); sliderPanel.Children.Add(sliderRange);

        // Overflow menu button ("⋯")
        var moreBtn = new Button
        {
            Content = "⋯",
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var menu = new MenuFlyout();
        var editItem = new MenuFlyoutItem { Text = "编辑对象属性" };
        editItem.Click += (_, _) => { SelectObject(label); _sidebarTabs.SelectedIndex = 1; };
        var copyItem = new MenuFlyoutItem { Text = "复制到输入栏" };
        copyItem.Click += (_, _) =>
        {
            var obj = Document.State.Objects.FirstOrDefault(o => o.Label == label);
            if (obj is not null) _input.Text = obj.Label + "=" + obj.Definition;
        };
        var deleteItem = new MenuFlyoutItem { Text = "删除" };
        deleteItem.Click += (_, _) => Document.Execute("Delete(" + label + ")");
        menu.Items.Add(editItem); menu.Items.Add(copyItem); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(deleteItem);
        moreBtn.Flyout = menu;

        var contentStack = new StackPanel { Spacing = 2 };
        contentStack.Children.Add(formulaView);
        contentStack.Children.Add(fallbackTitle);
        contentStack.Children.Add(valueText);
        contentStack.Children.Add(sliderPanel);

        var cardGrid = new Grid { ColumnSpacing = 6, Padding = new Thickness(4, 6, 4, 6) };
        cardGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        cardGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        cardGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        cardGrid.Children.Add(visBtn);
        Grid.SetColumn(contentStack, 1); cardGrid.Children.Add(contentStack);
        Grid.SetColumn(moreBtn, 2); cardGrid.Children.Add(moreBtn);

        var card = new Border
        {
            Child = cardGrid,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
        };
        var listItem = new ListViewItem
        {
            Content = card,
            Tag = label,
            Padding = new Thickness(2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var row = new AlgebraRow
        {
            Item = listItem,
            VisibilityButton = visBtn,
            VisibilityDot = dot,
            FormulaView = formulaView,
            FallbackTitle = fallbackTitle,
            ValueText = valueText,
            SliderPanel = sliderPanel,
            Slider = slider,
            SliderRange = sliderRange,
            PlayButton = playBtn,
        };

        slider.ValueChanged += (_, _) =>
        {
            if (row.SyncingSlider || _updatingRows) return;
            Document.Update(label, current => current with { Definition = ComputerAlgebra.Number(slider.Value) });
        };
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Document.BeginGesture()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        playBtn.Click += (_, _) =>
        {
            Document.Update(label, current => current with { Animate = true });
            ToggleAnimation();
        };

        return row;
    }

    private void SelectObject(string label)
    {
        _view.SelectedLabel = label; _view.InvalidateGeometry();
        _focused = _input;
        _properties.Children.Clear();
        var item = Document.State.Objects.FirstOrDefault(item => item.Label == label);
        if (item is null) return;
        _properties.Children.Add(new TextBlock { Text = label, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var definition = FormulaBox("对象定义"); definition.Text = item.Definition;
        definition.GotFocus += (_, _) => _focused = definition;
        _properties.Children.Add(definition);
        var formula = new MathFormulaView();
        formula.Visibility = formula.SetExpression(item.Definition) ? Visibility.Visible : Visibility.Collapsed;
        _properties.Children.Add(formula);
        definition.TextChanged += (_, _) => { formula.Visibility = formula.SetExpression(definition.Text) ? Visibility.Visible : Visibility.Collapsed; };
        var apply = new Button { Content = "应用定义", MinHeight = 40 };
        apply.Click += (_, _) => Guard(() => Document.Update(label, current => current with { Definition = definition.Text }));
        _properties.Children.Add(apply);

        void Check(string title, bool value, Func<ConstructionObject, bool, ConstructionObject> update)
        {
            var check = new CheckBox { Content = title, IsChecked = value, MinHeight = 38 };
            check.Checked += (_, _) => Document.Update(label, current => update(current, true));
            check.Unchecked += (_, _) => Document.Update(label, current => update(current, false));
            _properties.Children.Add(check);
        }
        Check("固定对象", item.Fixed, (current, value) => current with { Fixed = value });
        Check("显示标签", item.ShowLabel, (current, value) => current with { ShowLabel = value });
        Check("跟踪点轨迹", item.Trace, (current, value) => current with { Trace = value });

        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (uint color in new uint[] { 0xFF2269DE, 0xFFBA3847, 0xFF15936F, 0xFF8E4CBD, 0xFFCE7918, 0xFF333333 })
        {
            uint packed = color;
            var button = new Button
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
            };
            AutomationProperties.SetName(button, "对象颜色 " + color.ToString("X8"));
            button.Click += (_, _) => Document.Update(label, current => current with { Color = packed });
            colors.Children.Add(button);
        }
        _properties.Children.Add(colors);
        AddPropertySlider("线宽", .5, 8, .5, item.LineWidth, value => Document.Update(label, current => current with { LineWidth = value }));
        AddPropertySlider("填充不透明度", 0, 1, .05, item.Opacity, value => Document.Update(label, current => current with { Opacity = value }));

        if (Evaluation?.Values.TryGetValue(label, out var evaluated) == true && evaluated.Kind == GeometryKind.Scalar
            && double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            AddPropertySlider("参数值", item.SliderMin, item.SliderMax, item.SliderStep, evaluated.Number,
                value => Document.Update(label, current => current with { Definition = ComputerAlgebra.Number(value) }));
            Check("播放时驱动此参数", item.Animate, (current, value) => current with { Animate = value });
            var range = new StackPanel { Spacing = 6 };
            foreach (var option in new[] { ("最小值", item.SliderMin), ("最大值", item.SliderMax), ("步长", item.SliderStep) })
            {
                string name = option.Item1;
                var number = new NumberBox { Header = name, Value = option.Item2, MinHeight = 40 };
                number.ValueChanged += (_, _) =>
                {
                    if (!double.IsFinite(number.Value)) return;
                    Document.Update(label, current => name switch
                    {
                        "最小值" => current with { SliderMin = Math.Min(number.Value, current.SliderMax - .001) },
                        "最大值" => current with { SliderMax = Math.Max(number.Value, current.SliderMin + .001) },
                        _ => current with { SliderStep = Math.Clamp(number.Value, .0001, 1e6) },
                    });
                };
                range.Children.Add(number);
            }
            _properties.Children.Add(new Expander { Header = "滑动条范围", Content = range, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        if (Evaluation?.Values.TryGetValue(label, out var boolean) == true && boolean.ControlKind == "checkbox")
            Check("复选框值", boolean.Number != 0, (current, value) => current with { Definition = "Checkbox(" + (value ? "1" : "0") + ")" });

        var scriptsPanel = new StackPanel { Spacing = 8 };
        foreach (var script in new[] { ("点击时", item.OnClick), ("更新时", item.OnUpdate) })
        {
            string eventName = script.Item1;
            var box = new TextBox
            {
                Header = eventName,
                Text = script.Item2,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 80,
                MaxLength = 4096,
                PlaceholderText = "例如 SetValue(a,a+1); SetVisible(g,true)",
            };
            box.LostFocus += (_, _) => Document.Update(label, current => eventName == "点击时"
                ? current with { OnClick = box.Text } : current with { OnUpdate = box.Text });
            scriptsPanel.Children.Add(box);
        }
        var run = new Button { Content = "运行点击事件", MinHeight = 40 };
        run.Click += (_, _) => RunEventScript(Document.State.Objects.First(current => current.Label == label).OnClick);
        scriptsPanel.Children.Add(run);
        _properties.Children.Add(new Expander { Header = "构造事件脚本", Content = scriptsPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var remove = new Button { Content = "删除对象", MinHeight = 40 };
        remove.Click += (_, _) => Document.Execute("Delete(" + label + ")");
        _properties.Children.Add(remove);
    }

    private void AddPropertySlider(string title, double min, double max, double step, double value, Action<double> changed)
    {
        var slider = new PointerSlider { Header = title, Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max), MinHeight = 44 };
        slider.ValueChanged += (_, _) => changed(slider.Value);
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Document.BeginGesture()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        _properties.Children.Add(slider);
    }

    public async Task CalculateCasAsync()
    {
        if (_casBusy || string.IsNullOrWhiteSpace(_input.Text)) return;
        _casBusy = true;
        string input = _input.Text;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var snapshot = Document.State;
            var evaluation = Evaluation;
            var result = await Task.Run(() => ComputerAlgebra.Calculate(evaluation is null ? input
                : ConstructionKernel.ResolveExpression(snapshot, evaluation, input, cancellation.Token), cancellation.Token), cancellation.Token);
            if (_closed) return;
            LastCasResult = result;
            var exact = new TextBox { Text = result.Exact, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cambria Math"), FontSize = 18 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var use = new Button { Content = "送入输入栏", MinHeight = 36 };
            use.Click += (_, _) => _input.Text = result.Exact;
            var latex = new Button { Content = "复制 LaTeX", MinHeight = 36 };
            latex.Click += (_, _) => { var data = new DataPackage(); data.SetText(result.Latex); Clipboard.SetContent(data); };
            actions.Children.Add(use); actions.Children.Add(latex);
            var panel = new StackPanel
            {
                Spacing = 6,
                Children = {
                    new TextBlock { Text = input, TextWrapping = TextWrapping.Wrap, Opacity = 0.72 },
                    exact,
                    new TextBlock { Text = result.Numeric, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 },
                    actions,
                },
            };
            var formatted = new MathFormulaView();
            if (formatted.SetExpression(result.Exact)) panel.Children.Insert(1, formatted);
            _casHistory.Children.Add(new Border
            {
                Child = panel,
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 100, 116, 139)),
                CornerRadius = new CornerRadius(8),
            });
            while (_casHistory.Children.Count > 41) _casHistory.Children.RemoveAt(1);
            SetStatus(result.Evaluated ? "CAS 计算完成" : "结果保留未求出的符号运算。");
        }
        catch (OperationCanceledException) { SetStatus("符号计算已取消或超时。"); }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); }
        finally { _casBusy = false; }
    }

    private void ToggleAnimation()
    {
        if (_playing) { StopAnimation(); return; }
        if (!Document.State.Objects.Any(item => item.Animate))
        {
            SetStatus("在数值对象的属性中勾选“播放时驱动此参数”，或点击代数列表中滑动条左侧的播放按钮。"); return;
        }
        Document.BeginGesture(); _playing = true; _animationTime = DateTime.UtcNow; _animation.Start();
    }

    private void Animate()
    {
        if (!_playing || _renderedRevision != Document.Revision) return;
        double elapsed = Math.Clamp((DateTime.UtcNow - _animationTime).TotalSeconds, 0, .3);
        _animationTime = DateTime.UtcNow;
        foreach (var item in Document.State.Objects.Where(item => item.Animate).ToArray())
        {
            if (!double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            double span = item.SliderMax - item.SliderMin;
            double next = item.SliderMin + (value - item.SliderMin + span * elapsed / 8) % span;
            Document.Update(item.Label, current => current with { Definition = ComputerAlgebra.Number(next) });
        }
    }

    private void StopAnimation() { _animation.Stop(); if (_playing) Document.EndGesture(); _playing = false; }
    private void RunEventScript(string script)
    {
        if (!string.IsNullOrWhiteSpace(script)) RunEventScripts(new[] { script });
    }
    private bool _scriptsBusy;
    private async void RunEventScripts(IReadOnlyList<string> scripts)
    {
        if (_scriptsBusy || _closed) return;
        _scriptsBusy = true;
        var snapshot = Document.State;
        int revision = Document.Revision;
        try
        {
            var updated = await Task.Run(() =>
            {
                var transaction = new ConstructionDocument(); transaction.Load(snapshot);
                foreach (string script in scripts) { _lifetime.Token.ThrowIfCancellationRequested(); transaction.RunScript(script); }
                return transaction.State;
            }, _lifetime.Token);
            if (_closed || Document.Revision != revision) return;
            Document.Load(updated); _suppressScriptsRevision = Document.Revision;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); }
        finally { _scriptsBusy = false; }
    }
    private static string Coordinates(GeoVector p) => "(" + ComputerAlgebra.Number(p.X) + "," + ComputerAlgebra.Number(p.Y) + (p.Z == 0 ? ")" : "," + ComputerAlgebra.Number(p.Z) + ")");
    private void Guard(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.GetBaseException().Message); } }
    private void SetStatus(string text) { if (!_closed) _status.Text = text; }

    private void EnterKey(string key)
    {
        var box = _focused ?? _input;
        if (key == "⇥")
        {
            int from = box.SelectionStart + box.SelectionLength;
            int next = box.Text.IndexOf('□', from);
            if (next < 0) next = box.Text.IndexOf('□');
            if (next >= 0) { box.SelectionStart = next; box.SelectionLength = 1; }
            return;
        }
        Guard(() =>
        {
            var edit = FormulaEditing.Apply(box.Text, box.SelectionStart, box.SelectionLength, key);
            box.Text = edit.Text;
            box.SelectionStart = edit.SelectionStart;
            box.SelectionLength = edit.SelectionLength;
        });
    }

    private void UpdateMathPreview()
    {
        if (_closed) return;
        bool valid = !string.IsNullOrWhiteSpace(_input.Text) && _inputMath.SetExpression(_input.Text);
        _inputMath.Visibility = valid && !_formulaEditor.UsesRichEdit ? Visibility.Visible : Visibility.Collapsed;
        _syncingFormula = true;
        try
        {
            if (valid) _formulaEditor.SetExpression(_input.Text);
            else if (string.IsNullOrWhiteSpace(_input.Text)) _formulaEditor.Clear();
        }
        finally { _syncingFormula = false; }
    }

    private void DisposeWorkbench()
    {
        if (_closed) return;
        _closed = true; StopAnimation(); _lifetime.Cancel(); _calculation?.Cancel(); _debounce.Stop(); _mathDebounce.Stop();
        Document.Changed -= QueueCalculation; _host.TargetChanged -= TargetChanged;
        _target?.Controller.ClearExtensionPreview(_previewOwner);
        _view.Dispose(); Handwriting.Dispose();
    }
}
