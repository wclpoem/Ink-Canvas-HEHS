using System.Diagnostics;
using System.Numerics;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

/// <summary>
/// 板中板：以独立 <see cref="BoardService"/> 承载子展板的独立页/笔画/视口/撤销栈，
/// 复用 <see cref="InkSurface"/> 与 Board 元素层；窗口隐藏时只停渲染，不销毁内容。
/// </summary>
public sealed class SideBoardWindow : Window
{
    private const double AnimationMilliseconds = 210;
    private const double SaveDebounceMilliseconds = 600;
    private const double ClampDebounceMilliseconds = 300;
    private static readonly FontFamily IconFont = new("ms-appx:///Assets/SegoeFluentIcons.ttf#Segoe Fluent Icons");

    private readonly AppServices _services;
    private readonly BoardService _board;
    private readonly IShellActions _shell;
    private readonly Window _owner;
    private readonly Grid _body;
    private readonly Grid _surfaceHost;
    private readonly InkSurface _surface = new();
    private readonly BoardImageLayer _images = new();
    private readonly BoardMediaLayer _media = new();
    private readonly BoardDrawingLayers _drawingLayers = new();
    private readonly CanvasControl _paper;
    private readonly Border _background;
    private readonly Border _bar;
    private readonly Border _pagePane;
    private readonly List<TouchIconButton> _buttons = new();
    private TouchIconButton _penButton, _selectButton, _highlighterButton, _eraserButton;
    private TouchIconButton _undoButton, _redoButton, _prevButton, _nextButton, _pasteButton, _clearButton;
    private bool _pasting;
    private DispatcherQueueTimer _saveTimer, _animationTimer;
    private DispatcherQueueTimer _clampTimer;
    private InkController _boundController;
    private Matrix3x2 _lastTransform = Matrix3x2.Identity;
    private long _lastImageTapTicks;
    private Windows.Foundation.Point _lastImageTapPoint;
    private Windows.Foundation.Point _pendingAiCardEditPoint;
    private bool _pendingAiCardEdit;
    private bool _editingAiCard;
    private MinSizeHook _minSize;
    private bool _closed, _visible, _animating, _pagePaneOpen;

    public SideBoardWindow(AppServices services, IShellActions shell, Window owner)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _board = services.SideBoard;
        _shell = shell;
        _owner = owner;
        Title = "板中板";

        _background = new Border();
        _paper = new CanvasControl { ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _paper.Draw += OnDrawPaper;

        _surfaceHost = new Grid { ManipulationMode = Microsoft.UI.Xaml.Input.ManipulationModes.None };
        _surfaceHost.Children.Add(_background);
        _surfaceHost.Children.Add(_paper);
        _images.RenderInScene = true;
        _images.Visibility = Visibility.Collapsed;
        _surfaceHost.Children.Add(_images);
        _surfaceHost.Children.Add(_drawingLayers);
        _media.SharedHost = _drawingLayers;
        _media.LayerIndex = _drawingLayers.LayerIndex;
        _surface.DrawCommitted = false;
        _surfaceHost.Children.Add(_surface);
        _surface.InputStarting += p =>
        {
            var controller = _board.Current;
            Vector2 world = new(p.X, p.Y);
            bool mapped = Matrix3x2.Invert(controller.ViewTransform, out var inverse);
            if (mapped) world = Vector2.Transform(world, inverse);
            var last = controller.Document.Count > 0 ? controller.Document.Strokes[^1].Bounds : default;
            FileLog.WriteLogToFile(
                $"[SideBoardInput] down tool={controller.Tool} kind={p.Kind} pos=({p.X:0.0},{p.Y:0.0}) "
                + $"mapped={mapped} world=({world.X:0.0},{world.Y:0.0}) selected={controller.HasSelection} "
                + $"strokes={controller.Document.Count} lastBounds=({last.Left:0.0},{last.Top:0.0},{last.Right:0.0},{last.Bottom:0.0})",
                LogType.Trace);
        };
        _surface.InputEnded += () => FileLog.WriteLogToFile(
            $"[SideBoardInput] up tool={_board.Current.Tool} selected={_board.Current.HasSelection} "
            + $"undo={_board.Current.History.UndoCount} top={TopHistoryName(_board.Current)}",
            LogType.Trace);
        _surface.InputCanceled += () => FileLog.WriteLogToFile("[SideBoardInput] canceled", LogType.Trace);

        // 图片由场景层渲染；选择工具复用 InkController 命中/拖动/缩放。
        // 双击编辑只观察指针，不设置 Handled：移动即取消，释放且未拖动才进入编辑。
        _surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnAiCardPointerPressed), true);
        _surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnAiCardPointerMoved), true);
        _surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnAiCardPointerReleased), true);
        _surface.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnAiCardDoubleTapped), true);

        PageNavigator = new BoardPageNavigator(_board,
            () => new Vector2(Math.Max(1, (float)_surfaceHost.ActualWidth), Math.Max(1, (float)_surfaceHost.ActualHeight)),
            NavigateToPage, InsertPage, DuplicatePage, DeleteCurrentPage, () => { });
        _pagePane = new Border
        {
            Width = 232,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Visibility = Visibility.Collapsed,
            Child = PageNavigator,
        };
        PageNavigator.CloseRequested += () => SetPagePaneOpen(false);

        _body = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 31, 31, 31)) };
        _body.Children.Add(_surfaceHost);
        _bar = BuildToolbar();
        _body.Children.Add(_bar);
        _body.Children.Add(_pagePane);
        _body.SizeChanged += (_, _) => PageNavigator.InvalidateAll();
        _body.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSideBoardKeyDown), true);

        Content = _body;
        WindowChrome.MakeStandard(this, 720, 640);
        WindowFrame.Attach(this, _body);
        WindowChrome.SuppressTouchFeedback(this);
        _minSize = new MinSizeHook(this, SideBoardLayout.MinimumWidthDip, SideBoardLayout.MinimumHeightDip);

        _board.PageChanged += OnPageChanged;
        _board.BackgroundChanged += RefreshBackground;
        _board.ImagesChanged += OnImagesChanged;
        _board.ImagePlacementChanged += _images.UpdateElement;
        _board.MediaPlacementChanged += _media.UpdateElement;
        AppWindow.Changed += OnWindowChanged;
        Closed += OnClosed;

BindPage();
        RefreshBackground();
        ApplyInitialBounds();
    }

    public InkController CurrentController => _board.Current;
    internal BoardService Board => _board;
    public bool IsShown => _visible;
    /// <summary>滑入/滑出动画进行中；诊断等待动画完成时使用，不参与产品逻辑。</summary>
    internal bool IsAnimating => _animating;
    internal BoardPageNavigator PageNavigator { get; }

    /// <summary>子板可见性变化（true=已显示，false=开始收回）；供 Shell 统一联动浮窗。</summary>
    public event Action<bool> SideBoardVisibilityChanged;

    /// <summary>窗口内「收回」等入口统一走这里，由 Shell 通过 SideBoardVisibilityChanged 联动。</summary>
    public void RequestHide() => HideBoard();

    public void ShowBoard(bool animate = true)
    {
        FileLog.WriteLogToFile(
            $"[SideBoard] ShowBoard 入口：closed={_closed}，visible={_visible}，animate={animate}",
            LogType.Info);
        if (_closed) return;
        StopAnimation();
        if (_visible)
        {
            AppWindow.Show();
            ResumeContent();
            WindowChrome.EnsureAbove(this, OwnerHwnd);
            Activate();
            SideBoardVisibilityChanged?.Invoke(true);
            FileLog.WriteLogToFile(
                $"[SideBoard] SideBoardVisibilityChanged(true) 已触发：订阅数={SideBoardVisibilityChanged?.GetInvocationList().Length ?? 0}",
                LogType.Info);
            return;
        }
        _visible = true;
        ResumeContent();
        var rect = ResolveRect();
        var work = WorkArea();
        if (!animate)
        {
            WindowChrome.Place(this, rect.X, rect.Y, rect.Width, rect.Height);
            AppWindow.Show();
            WindowChrome.EnsureAbove(this, OwnerHwnd);
            Activate();
            SideBoardVisibilityChanged?.Invoke(true);
            FileLog.WriteLogToFile(
                $"[SideBoard] SideBoardVisibilityChanged(true) 已触发：订阅数={SideBoardVisibilityChanged?.GetInvocationList().Length ?? 0}",
                LogType.Info);
            return;
        }
        int offX = SlideFromRight(rect, work) ? work.Right : work.X - rect.Width;
        WindowChrome.Place(this, offX, rect.Y, rect.Width, rect.Height);
        AppWindow.Show();
        WindowChrome.EnsureAbove(this, OwnerHwnd);
        AnimateWindow(rect.X, rect.Y, hideAtEnd: false);
        Activate();
        SideBoardVisibilityChanged?.Invoke(true);
        FileLog.WriteLogToFile(
            $"[SideBoard] SideBoardVisibilityChanged(true) 已触发：订阅数={SideBoardVisibilityChanged?.GetInvocationList().Length ?? 0}",
            LogType.Info);
    }

    public void HideBoard(bool animate = true)
    {
        FileLog.WriteLogToFile(
            $"[SideBoard] HideBoard 入口：closed={_closed}，visible={_visible}，animate={animate}",
            LogType.Info);
        if (_closed || !_visible) return;
        StopAnimation();
        SaveBounds();
        var rect = CurrentRect();
        var work = WorkArea();
        _visible = false;
        FileLog.WriteLogToFile(
            $"[SideBoard] 触发 SideBoardVisibilityChanged(false)：订阅数={SideBoardVisibilityChanged?.GetInvocationList().Length ?? 0}",
            LogType.Info);
        SideBoardVisibilityChanged?.Invoke(false);
        FileLog.WriteLogToFile("[SideBoard] SideBoardVisibilityChanged(false) 已触发", LogType.Info);
        if (!animate)
        {
            SuspendContent();
            AppWindow.Hide();
            return;
        }
        int offX = SlideFromRight(rect, work) ? work.Right : work.X - rect.Width;
        AnimateWindow(offX, rect.Y, hideAtEnd: true);
    }

    /// <summary>拖拽/缩放结束或显示变化后，把窗口夹回其中心所在显示器的工作区。</summary>
    public void EnsureOnScreen()
    {
        if (_closed || !_visible || _animating) return;
        var current = WindowBounds();
        if (current.IsEmpty) return;
        var (work, scale) = WindowWorkArea(current);
        if (work.IsEmpty) return;
        var clamped = SideBoardLayout.Clamp(current, work, scale);
        if (clamped == current) return;
        WindowChrome.Place(this, clamped.X, clamped.Y, clamped.Width, clamped.Height);
    }

    /// <summary>
    /// AI 自动布局安全区（屏幕 DIP）：顶部标题 48，底部按工具栏实测高度；
    /// 上下再叠加选中对象浮动工具栏的余量。
    /// </summary>
    public BoardSafeInsets GetContentSafeInsets()
    {
        double reserve = BoardContentLayoutPlanner.SelectionToolbarReserve;
        return new(
            48 + reserve,
            Math.Max(96, _bar.ActualHeight + 12) + reserve,
            12,
            12);
    }

    private Border BuildToolbar()
    {
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        _penButton = ToolButton(row1, Glyphs.PenWpf, "笔", "画笔", () => SetTool(InkTool.Pen));
        _selectButton = ToolButton(row1, Glyphs.SelectWpf, "选择", "选择", () => SetTool(InkTool.Select));
        ToolTipService.SetToolTip(_selectButton, "选择（点选/框选，可拖动、缩放、删除）");
        _highlighterButton = ToolButton(row1, Glyphs.HighlightWpf, "荧光笔", "荧光笔", () => SetTool(InkTool.Highlighter));
        _highlighterButton.IconFont = "ms-appx:///Assets/SegoeMDL2Assets.ttf#Segoe MDL2 Assets";
        _eraserButton = ToolButton(row1, Glyphs.Eraser, "橡皮", "橡皮", () => SetTool(InkTool.Eraser));
        _clearButton = ToolButton(row1, Glyphs.Delete, "清空", "清空当前页全部内容", ClearOrDeleteSelection);
        ToolButton(row1, Glyphs.Board, "背景", "切换黑板/白板纸型", CycleBackground);
        _pasteButton = ToolButton(row1, Glyphs.Import, "贴回", "贴到主展板", () => _ = PasteToMainBoardAsync());
        ToolButton(row1, Glyphs.Ai, "AI", "AI 助教", () => _shell?.OpenAiAssistant());

        _undoButton = ToolButton(row2, Glyphs.Undo, "撤销", "撤销", () => _board.Current.Undo());
        _redoButton = ToolButton(row2, Glyphs.Redo, "重做", "重做", () => _board.Current.Redo());
        _prevButton = ToolButton(row2, Glyphs.ChevronLeft, "上一页", "上一页", NavigatePrevious);
        ToolButton(row2, Glyphs.SlideList, "页面", "页面管理与总览", () => SetPagePaneOpen(!_pagePaneOpen));
        _nextButton = ToolButton(row2, Glyphs.ChevronRight, "下一页", "下一页", NavigateNext);
        ToolButton(row2, Glyphs.Add, "加页", "新增一页", InsertPage);
        ToolButton(row2, Glyphs.Delete, "解组", "取消 AI 对象分组", UngroupSelectedAiGroup);
        ToolButton(row2, Glyphs.Ai, "A+", "AI 字号放大", () => _shell?.ApplyAiBoardEdit("字号大一点"), 38);
        ToolButton(row2, Glyphs.Ai, "两列", "AI 改为两列", () => _shell?.ApplyAiBoardEdit("改成两列"), 44);
        ToolButton(row2, Glyphs.Ai, "公式", "AI 只保留公式", () => _shell?.ApplyAiBoardEdit("只保留公式"), 44);
        ToolButton(row2, Glyphs.FoldWpf, "收回", "收回板中板", RequestHide);

        var rows = new StackPanel { Spacing = 2 };
        rows.Children.Add(row1);
        rows.Children.Add(row2);
        var bar = new Border
        {
            Child = rows,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 10),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Opacity = 0.82,
        };
        bar.PointerEntered += (_, _) => bar.Opacity = 1;
        bar.PointerExited += (_, _) => bar.Opacity = 0.82;
        AutomationProperties.SetName(bar, "板中板工具栏");
        return bar;
    }

    private TouchIconButton ToolButton(Panel host, string glyph, string label, string name, Action click, double width = 40)
    {
        var button = new TouchIconButton
        {
            Glyph = glyph,
            Label = label,
            IconSize = 17,
            LabelFontSize = 9.5,
            Width = width,
            Height = 48,
            IconAreaHeight = 25,
            LabelMargin = new Thickness(0, -1, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        host.Children.Add(button);
        _buttons.Add(button);
        return button;
    }

    private void BindPage()
    {
        _boundController?.CancelLassoErase();
        if (_boundController is not null)
        {
            _boundController.History.Changed -= RefreshHistoryState;
            _boundController.Invalidated -= OnControllerInvalidated;
            _boundController.ToolChanged -= RefreshToolSelection;
            _boundController.Document.Changed -= OnDrawingOrderChanged;
            _boundController.LayersChanged -= OnDrawingOrderChanged;
            _boundController.SelectionChanged -= OnSelectionChanged;
        }
        _boundController = _board.Current;
        _boundController.SelectedElements.Clear();
        _boundController.History.Changed += RefreshHistoryState;
        _boundController.Invalidated += OnControllerInvalidated;
        _boundController.ToolChanged += RefreshToolSelection;
        _boundController.Document.Changed += OnDrawingOrderChanged;
        _boundController.LayersChanged += OnDrawingOrderChanged;
        _boundController.SelectionChanged += OnSelectionChanged;
        _drawingLayers.UpdateOrder(_boundController);
        _surface.Controller = _boundController;
        _images.SetPage(_board);
        _images.UpdateViewTransform(_boundController.ViewTransform);
        _media.SetPage(_board);
        _media.UpdateViewTransform(_boundController.ViewTransform);
        _paper.RenderTransform = new MatrixTransform { Matrix = ToUiMatrix(_boundController.ViewTransform) };
        _lastTransform = _boundController.ViewTransform;
        RefreshHistoryState();
        RefreshToolSelection();
        RefreshSelectionActions();
        RefreshPageState();
        PageNavigator.InvalidateAll();
        _paper.Invalidate();
    }

    private void OnPageChanged()
    {
        _services.ApplySettings();
        BindPage();
        RefreshBackground();
    }

    private void OnControllerInvalidated()
    {
        _drawingLayers.Invalidate();
        if (_boundController is null) return;
        var transform = _boundController.ViewTransform;
        if (transform == _lastTransform) return;
        _lastTransform = transform;
        _images.UpdateViewTransform(transform);
        _media.UpdateViewTransform(transform);
        if (_paper.RenderTransform is not MatrixTransform matrix) _paper.RenderTransform = matrix = new MatrixTransform();
        matrix.Matrix = ToUiMatrix(transform);
        PageNavigator.InvalidateCurrent();
    }

    private void OnDrawingOrderChanged()
    {
        if (_boundController is null) return;
        _drawingLayers.UpdateOrder(_boundController);
        _media.Refresh();
    }

    private void OnImagesChanged()
    {
        if (_boundController is null) return;
        _drawingLayers.UpdateOrder(_boundController);
        _images.Refresh();
        _images.UpdateViewTransform(_boundController.ViewTransform);
        _media.Refresh();
        _media.UpdateViewTransform(_boundController.ViewTransform);
    }

    private void OnSelectionChanged()
    {
        _images.SetSelection(
            _board.Current.SelectedElements.OfType<BoardSelectionElement>().Where(e => e.Image is not null).Select(e => e.Image));
        RefreshSelectionActions();
        FileLog.WriteLogToFile(
            $"[SideBoardSelection] strokes={_board.Current.Selection.Count} elements={_board.Current.SelectedElements.Count}",
            LogType.Trace);
    }

    // ---- AI 卡片双击编辑 -------------------------------------------------

    private void OnAiCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_surface).Position;
        long now = Environment.TickCount64;
        bool isDoubleTap = now - _lastImageTapTicks <= 450
            && Math.Abs(point.X - _lastImageTapPoint.X) <= 6
            && Math.Abs(point.Y - _lastImageTapPoint.Y) <= 6;
        _lastImageTapTicks = isDoubleTap ? 0 : now;
        _lastImageTapPoint = point;
        _pendingAiCardEdit = isDoubleTap;
        _pendingAiCardEditPoint = point;
        if (isDoubleTap)
            FileLog.WriteLogToFile(
                $"[AI] 板中板双击候选：view=({point.X:0.0},{point.Y:0.0})，等待释放确认（不拦截拖动）",
                LogType.Trace);
        // 关键：双击候选只观察，绝不设置 Handled，InkSurface 必须继续收到按下/移动/释放。
    }

    private void OnAiCardPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pendingAiCardEdit) return;
        var point = e.GetCurrentPoint(_surface).Position;
        if (Math.Abs(point.X - _pendingAiCardEditPoint.X) > 6
            || Math.Abs(point.Y - _pendingAiCardEditPoint.Y) > 6)
        {
            _pendingAiCardEdit = false;
            FileLog.WriteLogToFile("[AI] 板中板双击候选已取消：指针移动，按选择拖动处理", LogType.Trace);
        }
    }

    private void OnAiCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pendingAiCardEdit) return;
        var point = e.GetCurrentPoint(_surface).Position;
        _pendingAiCardEdit = false;
        if (Math.Abs(point.X - _pendingAiCardEditPoint.X) > 6
            || Math.Abs(point.Y - _pendingAiCardEditPoint.Y) > 6)
        {
            FileLog.WriteLogToFile("[AI] 板中板双击候选已取消：释放位置偏离", LogType.Trace);
            return;
        }
        // 先让 InkSurface/InkController 完成本次点击与选择，再在下一帧打开编辑。
        var editPoint = _pendingAiCardEditPoint;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) TryEditAiCardAt(editPoint, "双击释放");
        });
    }

    private void OnAiCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_closed) return;
        _pendingAiCardEdit = false;
        var point = e.GetPosition(_surface);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) TryEditAiCardAt(point, "DoubleTapped");
        });
        // 不设置 Handled：双击编辑不应打断已开始的选择手势。
    }

    private bool TryEditAiCardAt(Windows.Foundation.Point point, string source)
    {
        if (_boundController is null) return false;
        Vector2 viewPoint = new((float)point.X, (float)point.Y);
        var nativeHit = BoardAiObjectHitTest.HitTestView(
            _board.CurrentTextObjects, _board.CurrentFormulaObjects,
            viewPoint.X, viewPoint.Y, _boundController.ViewTransform,
            out Vector2 world, out int objectCount, out float nearestDistance);
        string distance = float.IsPositiveInfinity(nearestDistance) ? "∞" : nearestDistance.ToString("0.##");
        if (nativeHit is not null)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板原生对象双击（{source}）：view=({point.X:0.0},{point.Y:0.0}) "
                + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 命中={nativeHit.GetType().Name} "
                + $"id={nativeHit.Id} nearestDistance={distance}，进入编辑",
                LogType.Event);
            _ = EditAiObjectAsync(nativeHit);
            return true;
        }

        FileLog.WriteLogToFile(
            $"[AI] 板中板原生对象双击（{source}）：view=({point.X:0.0},{point.Y:0.0}) "
            + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 未命中，nearestDistance={distance}",
            LogType.Trace);

        BoardImageAsset asset = BoardImageHitTest.HitTest(_board.CurrentImages, world.X, world.Y);
        if (asset is null)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片双击（{source}）：view=({point.X:0.0},{point.Y:0.0}) "
                + $"world=({world.X:0.0},{world.Y:0.0}) 未命中资产",
                LogType.Trace);
            return false;
        }

        if (!string.Equals(asset.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片双击（{source}）：命中 {asset.Name}，"
                + $"ContentType={asset.ContentType} 非 image/png，忽略",
                LogType.Trace);
            return false;
        }

        AiPasteMetadata metadata = AiPasteMetadata.TryParse(asset.Metadata);
        if (metadata is null)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片双击（{source}）：命中 {asset.Name}，"
                + $"Metadata 无法解析（长度 {asset.Metadata?.Length ?? 0}），忽略",
                LogType.Trace);
            return false;
        }

        FileLog.WriteLogToFile(
            $"[AI] 板中板 AI 卡片双击（{source}）：命中 {asset.Name}，Kind={metadata.Kind}，进入编辑",
            LogType.Event);
        _ = EditAiCardAsync(asset, metadata);
        return true;
    }

    private async Task EditAiCardAsync(BoardImageAsset asset, AiPasteMetadata metadata)
    {
        if (_editingAiCard) return;

        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片编辑：XamlRoot 为空（Content={Content?.GetType().Name ?? "null"}）",
                LogType.Error);
            return;
        }

        _editingAiCard = true;
        try
        {
            bool updated = await AiCardEditor.EditAsync(_board, asset, metadata, root.XamlRoot);
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片编辑：{asset.Name}，updated={updated}",
                updated ? LogType.Event : LogType.Trace);

            if (updated)
            {
                _paper.Invalidate();
                _drawingLayers.Invalidate();
            }
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板 AI 卡片编辑失败：{asset.Name}，{ex.Message ?? ex.GetType().Name}",
                LogType.Error);
        }
        finally
        {
            _editingAiCard = false;
        }
    }

    private async Task EditAiObjectAsync(ISelectionElement element)
    {
        if (_editingAiCard) return;
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;

        _editingAiCard = true;
        try
        {
            bool updated = element switch
            {
                BoardTextObject text => await AiCardEditor.EditTextAsync(_board, text, root.XamlRoot),
                BoardFormulaObject formula => await AiCardEditor.EditFormulaAsync(_board, formula, root.XamlRoot),
                _ => false,
            };
            if (updated)
            {
                _board.Current.NotifyElementsChanged();
                _paper.Invalidate();
                _drawingLayers.Invalidate();
            }
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile(
                $"[AI] 板中板原生对象编辑失败：{ex.Message ?? ex.GetType().Name}", LogType.Error);
        }
        finally
        {
            _editingAiCard = false;
        }
    }

    /// <summary>取消当前选中 AI 对象的共享分组。</summary>
    private void UngroupSelectedAiGroup()
    {
        string group = _board.Current.SelectedElements
            .OfType<IGroupedSelectionElement>()
            .Select(element => element.SelectionGroupId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(group)) return;

        if (_board.UngroupAiObjects(group) > 0)
        {
            _board.Current.ClearSelection();
            _board.Current.NotifyElementsChanged();
            _paper.Invalidate();
            _drawingLayers.Invalidate();
        }
    }

    private static Microsoft.UI.Xaml.Media.Matrix ToUiMatrix(Matrix3x2 m)
        => new(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

    private void SetTool(InkTool tool)
    {
        _board.Current.EndPlayback();
        _board.Current.Tool = tool;
        RefreshToolSelection();
    }

    private void RefreshToolSelection()
    {
        _penButton.IsSelected = _boundController?.Tool == InkTool.Pen;
        _selectButton.IsSelected = _boundController?.Tool == InkTool.Select;
        _highlighterButton.IsSelected = _boundController?.Tool == InkTool.Highlighter;
        _eraserButton.IsSelected = _boundController?.Tool == InkTool.Eraser;
    }

    private void RefreshSelectionActions()
    {
        if (_clearButton is null) return;
        bool selected = _board.Current.HasSelection;
        _clearButton.Label = selected ? "删除" : "清空";
        string hint = selected ? "删除选中（Delete / Ctrl+E）" : "清空当前页全部内容";
        ToolTipService.SetToolTip(_clearButton, hint);
        AutomationProperties.SetName(_clearButton, hint);
    }

    private void ClearOrDeleteSelection()
    {
        var controller = _board.Current;
        if (controller.HasSelection) controller.DeleteSelection();
        else _board.ClearCurrentPage();
    }

    private void OnSideBoardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closed || _editingAiCard || e.Handled) return;
        if (KeyboardShortcutScope.IsTextInput(e.OriginalSource as DependencyObject)) return;
        bool control = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x11) < 0;
        bool shift = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x10) < 0;
        bool alt = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x12) < 0;
        if (shift || alt) return;
        var controller = _board.Current;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Z when control:
                controller.Undo(); e.Handled = true; break;
            case Windows.System.VirtualKey.Y when control:
                controller.Redo(); e.Handled = true; break;
            case Windows.System.VirtualKey.A when control:
                controller.SelectAll(); e.Handled = true; break;
            case Windows.System.VirtualKey.E when control:
                if (controller.HasSelection) controller.DeleteSelection();
                else _board.ClearCurrentPage();
                e.Handled = true; break;
            case Windows.System.VirtualKey.Delete when controller.HasSelection:
                controller.DeleteSelection(); e.Handled = true; break;
            case Windows.System.VirtualKey.Escape:
                controller.EndPlayback();
                controller.CancelLassoErase();
                controller.ClearSelection();
                e.Handled = true; break;
        }
        if (e.Handled) RefreshHistoryState();
    }

    private void RefreshHistoryState()
    {
        _undoButton.IsEnabled = _boundController?.History.CanUndo == true;
        _redoButton.IsEnabled = _boundController?.History.CanRedo == true;
        if (_boundController is not null && _boundController.History.CanUndo)
            FileLog.WriteLogToFile(
                $"[SideBoardHistory] undo={_boundController.History.UndoCount} top={TopHistoryName(_boundController)}",
                LogType.Trace);
    }

    private static string TopHistoryName(InkController controller)
    {
        var state = controller?.History.Capture();
        return state is { Undo.Count: > 0 } ? state.Undo[^1].Name : "-";
    }

    private void RefreshPageState()
    {
        _prevButton.IsEnabled = _board.CurrentIndex > 0;
        _nextButton.IsEnabled = _board.CurrentIndex < _board.PageCount - 1 || _board.CanAddPages();
    }

    private void NavigatePrevious() => _board.PreviousPage();

    private void NavigateNext() => _board.NextPage();

    private void NavigateToPage(int index)
    {
        if (index < 0 || index >= _board.PageCount || index == _board.CurrentIndex) return;
        _board.GoTo(index);
    }

    private void InsertPage()
    {
        if (_board.CanAddPages()) _board.InsertPageAfterCurrent();
    }

    private void DuplicatePage()
    {
        if (!_board.Current.HasActiveInteraction) _board.DuplicateCurrentPage();
    }

    private void DeleteCurrentPage()
    {
        if (_board.PageCount <= 1 || _board.Current.HasActiveInteraction) return;
        _board.RemoveCurrentPage();
    }

    private void SetPagePaneOpen(bool open)
    {
        _pagePaneOpen = open;
        _pagePane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PageNavigator.SetOpen(open);
        PageNavigator.SetSuspended(!open || !_visible);
        if (open) PageNavigator.InvalidateAll();
    }

    /// <summary>
    /// 把子板当前页复制为透明 PNG 并插入主展板当前页。复制不改子板内容；
    /// 插入走 <see cref="BoardService.AddImage"/>，由主展板当前页撤销栈整体撤销。
    /// </summary>
    private async Task PasteToMainBoardAsync()
    {
        if (_pasting || _closed) return;
        if (!_board.CurrentPageHasPasteableContent)
        {
            await ShowMessageAsync("子板当前页没有可贴回的内容。先在子板写点内容，再贴到主展板。");
            return;
        }
        if (_owner is not BoardWindow main || main.IsClosed)
        {
            await ShowMessageAsync("主展板未打开，无法贴回。请先打开画板，再点“贴到主展板”。");
            return;
        }

        _pasting = true;
        if (_pasteButton is not null) _pasteButton.IsEnabled = false;
        try
        {
            if (!await PasteCurrentPageToAsync(main))
                await ShowMessageAsync("子板当前页没有可贴回的内容。");
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"板中板贴到主展板失败：{ex}", LogType.Error);
            if (!_closed) await ShowMessageAsync($"贴到主展板失败：{ex.Message}");
        }
        finally
        {
            _pasting = false;
            if (_pasteButton is not null) _pasteButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 无对话框版本的贴回：渲染子板当前页并插入主展板，供按钮流程与运行时诊断共用。
    /// 无可贴内容或目标已关闭时返回 false，渲染/插入失败抛出具体异常。
    /// </summary>
    internal async Task<bool> PasteCurrentPageToAsync(BoardWindow main)
    {
        if (_closed || main is null || main.IsClosed) return false;
        var controller = _board.Current;
        if (!_board.CurrentPageHasPasteableContent) return false;

        // 有界等待：最后一次落笔/点阵动画完成后截图，避免漏掉刚写完的一笔。
        for (int i = 0; i < 60 && !_closed && controller.HasActiveInteraction; i++)
            await Task.Delay(16);
        if (_closed) return false;

        var bounds = CurrentPageContentBounds(controller);
        if (bounds.IsEmpty) return false;
        byte[] png = await RenderPagePngAsync(controller, bounds);
        if (png is not { Length: > 0 }) throw new InvalidOperationException("渲染结果为空。");

        var viewport = main.PasteViewport;
        var asset = BoardPasteLayout.CreateImageAsset(
            $"板中板-{DateTime.Now:yyyyMMdd-HHmmss}.png", png,
            bounds.Width, bounds.Height, viewport.X, viewport.Y, main.PasteViewTransform);
        if (asset is null) throw new InvalidOperationException("生成图片尺寸无效。");
        if (!main.InsertImage(asset)) throw new InvalidOperationException("主展板当前页未接受图片。");
        return true;
    }

    private static Rect CurrentPageContentBounds(InkController controller)
    {
        Rect bounds = Rect.Empty;
        foreach (var item in controller.DrawingObjects)
        {
            if (item.Stroke is { } stroke) bounds = bounds.Union(stroke.Bounds);
            else if (item.Element is IImageSelectionElement { ImageData.Length: > 0 } image)
                bounds = bounds.Union(image.Bounds);
        }
        return bounds;
    }

    private static async Task<byte[]> RenderPagePngAsync(InkController controller, Rect bounds)
    {
        const float padding = 16f;
        const float desiredScale = 2f;
        const int maxPixels = 8192;
        float dipWidth = Math.Max(1f, bounds.Width + padding * 2);
        float dipHeight = Math.Max(1f, bounds.Height + padding * 2);
        float scale = Math.Min(desiredScale, maxPixels / Math.Max(1f, dipWidth));
        scale = Math.Min(scale, maxPixels / Math.Max(1f, dipHeight));
        if (!(scale > 0)) throw new InvalidOperationException("内容尺寸无效。");
        int pixelWidth = Math.Max(1, (int)Math.Round(dipWidth * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round(dipHeight * scale));

        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, 96);
        using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
        await renderer.Images.PrepareAsync(device, controller);
        var view = Matrix3x2.CreateTranslation(padding - bounds.Left, padding - bounds.Top)
            * Matrix3x2.CreateScale(scale);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            foreach (var item in controller.DrawingObjects)
            {
                if (item.Stroke is null && item.Element is not IImageSelectionElement { ImageData.Length: > 0 }) continue;
                renderer.DrawObject(session, controller, item, view);
            }
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask();
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task ShowMessageAsync(string message)
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = "板中板",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = root.XamlRoot,
        };
        try { await dialog.ShowAsync(); }
        catch { /* 窗口正在关闭或已有对话框时，不因提示失败而中断贴回流程。 */ }
    }

    private void CycleBackground()
    {
        BoardBackground[] order =
        {
            BoardBackground.Black, BoardBackground.White, BoardBackground.Grid,
            BoardBackground.Lined, BoardBackground.Green, BoardBackground.Cream, BoardBackground.Coordinate,
        };
        int index = Array.IndexOf(order, _board.Background);
        _board.SetBackground(order[(index + 1 + order.Length) % order.Length]);
    }

    private void RefreshBackground()
    {
        var background = _board.Background;
        bool dark = background is BoardBackground.Black or BoardBackground.Green;
        _background.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(255, 31, 31, 31)
            : Windows.UI.Color.FromArgb(255, 242, 242, 242));
        _body.Background = _background.Background;
        var barBackground = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(224, 18, 18, 18)
            : Windows.UI.Color.FromArgb(224, 250, 250, 250));
        _bar.Background = barBackground;
        _bar.BorderBrush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(150, 255, 255, 255)
            : Windows.UI.Color.FromArgb(160, 0, 0, 0));
        _pagePane.Background = PopupBrushes.WindowSurface(dark ? 2 : 1);
        _pagePane.BorderBrush = _bar.BorderBrush;
        var foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var hover = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(46, 0, 205, 205)
            : Windows.UI.Color.FromArgb(30, 0, 205, 205));
        var pressed = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(72, 0, 205, 205)
            : Windows.UI.Color.FromArgb(56, 0, 205, 205));
        foreach (var button in _buttons)
        {
            button.ContentForeground = foreground;
            button.HoverBackground = hover;
            button.PressedBackground = pressed;
            button.SelectionBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 102, 191));
            button.RefreshVisual();
        }
        PageNavigator.ApplyTheme(dark ? 2 : 1);
        WindowChrome.ApplyTheme(this, dark ? 2 : 1);
        _paper.Invalidate();
    }

    private void OnDrawPaper(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_closed) return;
        var background = _board.Background;
        bool dark = background is BoardBackground.Black or BoardBackground.Green;
        float spacing = Math.Clamp(_board.GridSpacing, 12f, 160f);
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        var line = dark
            ? Windows.UI.Color.FromArgb(70, 235, 235, 235)
            : Windows.UI.Color.FromArgb(55, 92, 113, 121);
        switch (background)
        {
            case BoardBackground.Grid:
                for (float y = spacing; y < height; y += spacing)
                    args.DrawingSession.DrawLine(new Vector2(0, y), new Vector2(width, y), line, 1f);
                for (float x = spacing; x < width; x += spacing)
                    args.DrawingSession.DrawLine(new Vector2(x, 0), new Vector2(x, height), line, 1f);
                break;
            case BoardBackground.Lined:
                for (float y = spacing; y < height; y += spacing)
                    args.DrawingSession.DrawLine(new Vector2(0, y), new Vector2(width, y), line, 1f);
                break;
            case BoardBackground.Coordinate:
                DrawCoordinateGrid(args, dark, spacing, width, height);
                break;
        }
    }

    private static void DrawCoordinateGrid(CanvasDrawEventArgs args, bool dark, float spacing, float width, float height)
    {
        var session = args.DrawingSession;
        var axis = dark
            ? Windows.UI.Color.FromArgb(200, 230, 230, 230)
            : Windows.UI.Color.FromArgb(190, 60, 80, 90);
        var tick = dark
            ? Windows.UI.Color.FromArgb(150, 220, 220, 220)
            : Windows.UI.Color.FromArgb(140, 60, 80, 90);
        float originX = width * 0.5f;
        float originY = height * 0.5f;
        const float tickHalf = 4f;
        session.DrawLine(new Vector2(0, originY), new Vector2(width, originY), axis, 1.6f);
        session.DrawLine(new Vector2(originX, 0), new Vector2(originX, height), axis, 1.6f);
        for (float x = originX + spacing; x < width; x += spacing)
            session.DrawLine(new Vector2(x, originY - tickHalf), new Vector2(x, originY + tickHalf), tick, 1f);
        for (float x = originX - spacing; x > 0; x -= spacing)
            session.DrawLine(new Vector2(x, originY - tickHalf), new Vector2(x, originY + tickHalf), tick, 1f);
        for (float y = originY + spacing; y < height; y += spacing)
            session.DrawLine(new Vector2(originX - tickHalf, y), new Vector2(originX + tickHalf, y), tick, 1f);
        for (float y = originY - spacing; y > 0; y -= spacing)
            session.DrawLine(new Vector2(originX - tickHalf, y), new Vector2(originX + tickHalf, y), tick, 1f);
        using var format = new CanvasTextFormat { FontSize = 12 };
        session.DrawText("O", originX + 4, originY + 4, axis, format);
        session.DrawText("x", width - 18, originY + 4, axis, format);
        session.DrawText("y", originX + 4, 4, axis, format);
    }

    private void SuspendContent()
    {
        _surface.SuspendRendering();
        _drawingLayers.SuspendRendering();
        PageNavigator.SetSuspended(true);
    }

    private void ResumeContent()
    {
        _surface.ResumeRendering();
        _drawingLayers.ResumeRendering();
        PageNavigator.SetSuspended(!_pagePaneOpen);
        _paper.Invalidate();
    }

    private SideBoardRect ResolveRect()
    {
        var work = WorkArea();
        return SideBoardLayout.Resolve(_services.Settings.SideBoard, work, DpiScale(work));
    }

    private SideBoardRect CurrentRect()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new SideBoardRect(position.X, position.Y, size.Width, size.Height);
    }

    private void ApplyInitialBounds()
    {
        var rect = ResolveRect();
        WindowChrome.Place(this, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private SideBoardWorkArea WorkArea()
    {
        try
        {
            if (_owner is not null)
            {
                var work = WindowChrome.GetWorkArea(_owner);
                return new SideBoardWorkArea(work.X, work.Y, work.Width, work.Height);
            }
        }
        catch { }
        var primary = ScreenService.WorkArea;
        return new SideBoardWorkArea(primary.X, primary.Y, primary.Width, primary.Height);
    }

    private double DpiScale(SideBoardWorkArea work)
        => WindowChrome.GetDpiScale(new Windows.Graphics.RectInt32(work.X, work.Y,
            Math.Max(1, work.Width), Math.Max(1, work.Height)));

    /// <summary>取窗口矩形中心点所在显示器的工作区与 DPI，跨屏拖动时按目标屏夹取。</summary>
    private (SideBoardWorkArea Work, double Scale) WindowWorkArea(SideBoardRect rect)
    {
        int centerX = rect.X + rect.Width / 2;
        int centerY = rect.Y + rect.Height / 2;
        var screen = ScreenService.FromRect(new ScreenRect(centerX, centerY, 1, 1));
        var work = screen.WorkArea;
        var area = new SideBoardWorkArea(work.X, work.Y, work.Width, work.Height);
        return (area, screen.DpiScale > 0 ? screen.DpiScale : DpiScale(area));
    }

    /// <summary>Win32 外框；与复测脚本 GetWindowRect 保持一致。</summary>
    private SideBoardRect WindowBounds()
    {
        var bounds = WindowChrome.GetWindowBounds(this);
        return new SideBoardRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static bool SlideFromRight(SideBoardRect rect, SideBoardWorkArea work)
        => rect.X + rect.Width / 2 >= work.X + work.Width / 2;

    private IntPtr OwnerHwnd => _owner is null ? IntPtr.Zero : WindowChrome.GetHwnd(_owner);

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closed || _animating || !_visible) return;
        if (!args.DidPositionChange && !args.DidSizeChange) return;
        // 拖动/缩放过程中每次变化都重置计时器，停下来 300ms 后再夹取。
        ScheduleClamp();
        ScheduleSave();
    }

    private void ScheduleClamp()
    {
        _clampTimer ??= DispatcherQueue.CreateTimer();
        _clampTimer.Interval = TimeSpan.FromMilliseconds(ClampDebounceMilliseconds);
        _clampTimer.IsRepeating = false;
        _clampTimer.Stop();
        _clampTimer.Tick -= OnClampTimerTick;
        _clampTimer.Tick += OnClampTimerTick;
        _clampTimer.Start();
    }

    private void OnClampTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        EnsureOnScreen();
    }

    private void ScheduleSave()
    {
        _saveTimer ??= DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(SaveDebounceMilliseconds);
        _saveTimer.IsRepeating = false;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
        _saveTimer.Tick += OnSaveTimerTick;
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        SaveBounds();
    }

    private void SaveBounds()
    {
        if (_closed || _animating || !_visible) return;
        var size = AppWindow.Size;
        var position = AppWindow.Position;
        if (size.Width <= 0 || size.Height <= 0) return;
        var settings = _services.Settings.SideBoard;
        settings.X = position.X;
        settings.Y = position.Y;
        settings.W = size.Width;
        settings.H = size.Height;
        _services.SaveSettings();
    }

    private void AnimateWindow(int targetX, int targetY, bool hideAtEnd)
    {
        StopAnimation();
        var start = AppWindow.Position;
        _animating = true;
        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(15);
        _animationTimer.IsRepeating = true;
        var clock = Stopwatch.StartNew();
        _animationTimer.Tick += (_, _) =>
        {
            double progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / AnimationMilliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            int x = (int)Math.Round(start.X + (targetX - start.X) * eased);
            int y = (int)Math.Round(start.Y + (targetY - start.Y) * eased);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
            if (progress < 1) return;
            StopAnimation();
            if (hideAtEnd)
            {
                SuspendContent();
                AppWindow.Hide();
            }
            else
            {
                WindowChrome.EnsureAbove(this, OwnerHwnd);
            }
        };
        _animationTimer.Start();
    }

    private void StopAnimation()
    {
        if (_animationTimer is not null)
        {
            _animationTimer.Stop();
            _animationTimer = null;
        }
        _animating = false;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;
        _visible = false;
        StopAnimation();
        _saveTimer?.Stop();
        _clampTimer?.Stop();
        _board.PageChanged -= OnPageChanged;
        _board.BackgroundChanged -= RefreshBackground;
        _board.ImagesChanged -= OnImagesChanged;
        _board.ImagePlacementChanged -= _images.UpdateElement;
        _board.MediaPlacementChanged -= _media.UpdateElement;
        AppWindow.Changed -= OnWindowChanged;
        if (_boundController is not null)
        {
            _boundController.History.Changed -= RefreshHistoryState;
            _boundController.Invalidated -= OnControllerInvalidated;
            _boundController.ToolChanged -= RefreshToolSelection;
            _boundController.Document.Changed -= OnDrawingOrderChanged;
            _boundController.LayersChanged -= OnDrawingOrderChanged;
            _boundController.SelectionChanged -= OnSelectionChanged;
            _boundController.CancelLassoErase();
        }
        PageNavigator.Dispose();
        _media.Dispose();
        _drawingLayers.Dispose();
        _surface.Dispose();
        _paper.Draw -= OnDrawPaper;
        _paper.RemoveFromVisualTree();
        _minSize?.Dispose();
        _minSize = null;
    }
}