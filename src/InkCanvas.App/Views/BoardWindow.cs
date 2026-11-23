using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Manipulation;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using WinRT.Interop;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace InkCanvas.App.Views;

/// <summary>
/// WinUI board port. Restores the upstream WPF board chrome (MainWindow.xaml 797-2976):
/// three floating 55-DIP icon+label strips over a full-screen canvas — left page navigation,
/// center tools, right page-navigation mirror — themed to the black/white board, plus the
/// top-left selection action panel (MW_SelectionGestures.cs).
/// </summary>
public sealed partial class BoardWindow : Window
{
    // WPF board glyph codes (Segoe Fluent Icons / MDL2), MainWindow.xaml board regions.
    private const string GlyphPrev = "\uF16D";
    private const string GlyphNext = "\uF16B";
    private const string GlyphAddPage = Glyphs.AddPage;
    private const string GlyphExit = Glyphs.ExitBoard;
    private const string GlyphGesture = "\uE6E5";
    private const string GlyphCanvas = "\uF2F8";
    private const string GlyphSelect = Glyphs.Select;
    private const string GlyphPen = "\uF253";
    private const string GlyphHighlighter = "\uF481";
    private const string GlyphEraser = "\uE5F2";
    private const string GlyphShape = "\uF6B7";
    private const string GlyphImage = "\uF490";
    private const string GlyphUndo = "\uF19A";
    private const string GlyphRedo = "\uF16F";
    private const string GlyphMore = "\uF467";
    private const string GlyphClear = "\uF34D";

    private static FontFamily IconFont => IconFontStyle.Font;

    private readonly AppServices _services;
    private readonly BoardService _board;
    private readonly IShellActions _shell;
    private readonly InkSurface _surface = new();
    private readonly BoardImageLayer _images = new();
    private bool _editingAiCard;
    private readonly BoardMediaLayer _media = new();
    private readonly BoardDrawingLayers _drawingLayers = new();
    private readonly CanvasControl _paper;
    private readonly Border _background;
    private readonly Grid _surfaceHost;
    private readonly Grid _contentRoot;
    private readonly Grid _windowedContent;
    private readonly Border _selectionPanel;
    private readonly Border _pagePane;
    private readonly Grid _pageOverlay;
    private bool _pagePaneOpen, _pagePaneRight;
    private int _pagePaneAnimation;
    private Microsoft.UI.Composition.CompositionPathGeometry _pageClipGeometry;
    private Microsoft.UI.Composition.CompositionGeometricClip _pageClip;
    private CanvasGeometry _pageClipSource;
    internal BoardPageNavigator PageNavigator { get; }
    internal bool PagePaneOpen => _pagePaneOpen;
    internal FrameworkElement PagePaneSurface => _pagePane;

    private readonly StackPanel _leftHost;
    private readonly StackPanel _centerHost;
    private readonly StackPanel _rightHost;
    private readonly Grid _dock;
    private readonly Viewbox _leftDock, _centerDock, _rightDock;
    private FlyoutBase _activeBoardFlyout;
    private PopupHeader _activeBoardHeader;
    private Flyout _inkOptionsFlyout, _moreFlyout, _gestureFlyout, _eraserFlyout;
    private readonly Dictionary<FlyoutBase, ToolOptionsWindow> _toolPanels = new();
    internal IEnumerable<ToolOptionsWindow> ToolPanels => _toolPanels.Values;
    public event Action CanvasInputBegan;
    private readonly List<ToolCell> _allCells = new();
    private readonly List<(ToolCell Cell, Func<bool> Active)> _toolCells = new();
    private readonly List<Border> _indicatorBorders = new();
    private readonly List<TextBlock> _indicatorTexts = new();

    private ToolCell _prevL, _nextL, _prevR, _nextR, _addR;
    private ToolCell _gestureCell, _selectCell, _penCell, _eraserCell, _shapeCell;
    private ToolCell _undoCell, _redoCell, _moreCell;
    private readonly TextBlock _pageTextL = new() { FontSize = 15, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _pageTextR = new() { FontSize = 15, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

    // Board-themed bar brushes (Dark-Board.xaml / Light-Board.xaml upstream). Rebuilt on
    // every black/white switch; never resolved from the application theme.
    private SolidColorBrush _barBg = new(Windows.UI.Color.FromArgb(191, 0, 0, 0));
    private SolidColorBrush _barBorder = new(Windows.UI.Color.FromArgb(153, 255, 255, 255));
    private SolidColorBrush _barFg = new(Microsoft.UI.Colors.White);
    private SolidColorBrush _barTitleBg = new(Windows.UI.Color.FromArgb(128, 33, 86, 201));
    private SolidColorBrush _hoverBg = new(Windows.UI.Color.FromArgb(204, 0, 40, 40));
    private SolidColorBrush _pressedBg = new(Windows.UI.Color.FromArgb(212, 0, 55, 55));
    private SolidColorBrush _selectedBg = new(Windows.UI.Color.FromArgb(200, 10, 40, 70));
    private int _boardTheme = 2;

    private InkController _boundController;
    private BoardPenPalette _boardPenPalette;
    private bool _isFullscreen;
    private bool _entrancePlayed;
    private bool _closed;
    internal bool IsClosed => _closed;
    internal long CachedBoardRasterBytes => _drawingLayers.RasterBytes;
    internal int DrawingSurfaceCount => _drawingLayers.SurfaceCount;
    internal bool IdleResourcesReleased => _idleResources.IsReleased;
    private readonly WindowIdleResources _idleResources;
    private bool _imagePicking;
    private CancellationTokenSource _imageImportCancellation;
    private float _viewScale = 1f;
    private long _lastImageTapTicks;
    private Windows.Foundation.Point _lastImageTapPoint;
    private long _lastNativeTapTicks;
    private Windows.Foundation.Point _lastNativeTapPoint;
    private bool _pendingAiNativeEdit;
    private Windows.Foundation.Point _pendingAiNativeEditPoint;

    // Selection panel clone toggle (WPF ToggleButtonStrokeSelectionClone) and its icon/label,
    // whose foreground swaps when checked (BorderStrokeSelectionClone_Click).
    private SelectionToolbar _selectionToolbar;
    private bool _selectionPlacementPending;
    private bool _selectionMeasureDirty = true;
    private Windows.Foundation.Size _selectionDesiredSize;

    // Live view-transform sync: two-finger gestures mutate controller.ViewTransform; the element
    // layers and paper must follow on every invalidation, not only on page bind / scale presets.
    private Matrix3x2 _lastLayerTransform = Matrix3x2.Identity;

    // The controller owns both ink and embedded-object interactions. Keep the last captured
    // sample so capture loss can finish an interaction without a stale XAML position.
    private readonly Dictionary<uint, OverlayPointer> _selectionPointers = new();
    /// <summary>The controller currently bound to the board surface and its floating tools.</summary>
    public InkController CurrentController => _board.Current;
    internal BoardMediaLayer MediaLayer => _media;
    internal IReadOnlyList<FrameworkElement> DockSurfaces => new FrameworkElement[] { _leftDock, _centerDock, _rightDock };
    internal Vector2 PasteViewport
    {
        get
        {
            double width = _surface.ActualWidth > 0 ? _surface.ActualWidth : _contentRoot.ActualWidth;
            double height = _surface.ActualHeight > 0 ? _surface.ActualHeight : _contentRoot.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                var work = WindowChrome.GetWorkArea(this);
                double dpi = WindowChrome.GetDpiScale(this);
                width = work.Width / Math.Max(1, dpi);
                height = work.Height / Math.Max(1, dpi);
            }
            return new Vector2((float)Math.Max(1, width), (float)Math.Max(1, height));
        }
    }
    internal Matrix3x2 PasteViewTransform => _board.Current.ViewTransform;

    /// <summary>Raised after navigation or loading has rebound this window to its current page.</summary>
    public event Action CurrentPageChanged;

    public BoardWindow(AppServices services, IShellActions shell = null, Window owner = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _board = services.Board;
        _shell = shell;
        Title = "Ink Canvas 画板";

        _background = new Border();
        _paper = new CanvasControl { ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _paper.Draw += OnDrawPaper;
        _images.SelectionChanged += OnImageSelectionChanged;
        // Image views mark pointer presses as handled for dragging, so observe them at the layer
        // level to detect a double tap without touching the existing drag/delete interaction.
        _images.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnImageLayerPointerPressed), true);
        // Native gesture is registered in parallel so system-injected or synthesized double taps
        // still reach the same edit entry point.
        _images.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnImageDoubleTapped), true);
        _media.SelectionChanged += OnMediaSelectionChanged;

        // WPF compositing: ink renders ABOVE embedded elements; elements are interactive only
        // in Select mode. In Select mode the ink surface stops hit-testing and unhandled
        // presses on this host are forwarded to the controller for stroke selection.
        _surfaceHost = new Grid { ManipulationMode = ManipulationModes.None };
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
        _surface.InputBegan += OnCanvasInputBegan;
        _surface.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnNativeObjectDoubleTapped), true);
        // Select mode makes _surface non-hit-testable, so the double-tap observer must live on the
        // always-hit-testable host as well; otherwise native objects never receive the edit gesture.
        _surfaceHost.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnNativeObjectDoubleTapped), true);
        // handledEventsToo: the host must also observe presses the element layers already handled,
        // so a second finger can start a selection gesture even when the first landed on an image.
        _surfaceHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSurfaceHostPointerPressed), true);
        _surfaceHost.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnSurfaceHostPointerMoved), true);
        _surfaceHost.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSurfaceHostPointerReleased), true);
        _surfaceHost.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnSurfaceHostPointerCanceled), true);
        _surfaceHost.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSurfaceHostPointerCanceled), true);
        _surfaceHost.PointerWheelChanged += OnSurfaceHostPointerWheelChanged;

        (_leftHost, _centerHost, _rightHost) = BuildStrips();

        _selectionPanel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 50, 0, 0),
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Visibility = Visibility.Collapsed,
        };

        // Single-cell overlay dock: the center strip is truly screen-centered (WPF Viewboxes
        // overlap in one Grid), the side strips hug the edges, and the canvas keeps full size.
        _dock = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0) };
        _dock.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _dock.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _leftDock = DockStrip(_leftHost, HorizontalAlignment.Left);
        _centerDock = DockStrip(_centerHost, HorizontalAlignment.Center);
        _rightDock = DockStrip(_rightHost, HorizontalAlignment.Right);
        _dock.Children.Add(_leftDock); _dock.Children.Add(_centerDock); _dock.Children.Add(_rightDock);

        _contentRoot = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _contentRoot.KeyDown += OnBoardKeyDown;
        _contentRoot.Children.Add(_surfaceHost);
        _contentRoot.Children.Add(_dock);
        _contentRoot.Children.Add(_selectionPanel);
        _contentRoot.SizeChanged += (_, _) =>
        {
            _selectionPanel.MaxWidth = Math.Max(1, _contentRoot.ActualWidth - 16);
            _selectionMeasureDirty = true;
            ApplyBoardScale(); PlaceSelectionPanel();
            PageNavigator?.InvalidateAll();
        };

        WindowChrome.MakeBorderless(this, topmost: true, resizable: true);
        _idleResources = new WindowIdleResources(this, () =>
        {
            _surface.SuspendRendering();
            _drawingLayers.SuspendRendering();
            _media.ReleasePlayers();
            PageNavigator?.SetSuspended(true);
        }, () =>
        {
            _surface.ResumeRendering();
            _drawingLayers.ResumeRendering();
            _media.Refresh();
            _paper.Invalidate();
            PageNavigator?.SetSuspended(false);
        });
        // No contact rings / press-and-hold dots while touch-writing on the board (same set as the overlay).
        WindowChrome.SuppressTouchFeedback(this);
        _windowedContent = WindowChrome.AddTitleBar(this, Title, _contentRoot, closeButton: true, minMax: true);

        PageNavigator = new BoardPageNavigator(_board,
            () => new Vector2(Math.Max(1, (float)_surfaceHost.ActualWidth), Math.Max(1, (float)_surfaceHost.ActualHeight)),
            NavigateToPage, InsertPage, DuplicatePage, DeleteCurrentPage, () => _ = RenameCurrentPageAsync());
        _pagePane = new Border { Child = PageNavigator, Width = BoardPageNavigator.SidebarWidth, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        _pageOverlay = new Grid { Visibility = Visibility.Collapsed };
        var dismissPages = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        dismissPages.PointerPressed += (_, args) => { SetPagePaneOpen(false); args.Handled = true; };
        _pageOverlay.Children.Add(dismissPages);
        _pageOverlay.Children.Add(_pagePane);
        _pageOverlay.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            SetPagePaneOpen(false);
            args.Handled = true;
        };
        PageNavigator.CloseRequested += () => SetPagePaneOpen(false);
        PageNavigator.ViewModeChanged += _ =>
        {
            UpdatePagePaneLayout();
        };
        _contentRoot.SizeChanged += (_, _) => UpdatePagePaneLayout();
        _contentRoot.Children.Add(_pageOverlay);

        _board.PageChanged += OnPageChanged;
        _board.BackgroundChanged += RefreshBackground;
        _board.ImagesChanged += OnImagesChanged;
        _board.ImagePlacementChanged += _images.UpdateElement;
        _board.MediaPlacementChanged += _media.UpdateElement;
        Closed += OnClosed;
        AppWindow.Closing += OnArchiveClosing;
        Activated += (_, _) => OnActivated();

        BindPage();
        RefreshBackground();
        UpdatePageNavigation();
        ApplyBoardScale();
        // WPF board mode is the maximized borderless main window covering the monitor.
        if (owner is not null)
            WindowChrome.MoveToMonitor(this, Platform.System.ScreenService.FromWindow(WindowChrome.GetHwnd(owner)));
        SetFullscreen(true);
    }

    // ---- strip construction -------------------------------------------------

    private (StackPanel Left, StackPanel Center, StackPanel Right) BuildStrips()
    {
        var firstBorder = new Thickness(1, 1, 0.25, 1);
        var midBorder = new Thickness(0, 1, 0.25, 1);
        var lastBorder = new Thickness(0, 1, 1, 1);
        var firstCorner = new CornerRadius(5, 0, 0, 5);
        var lastCorner = new CornerRadius(0, 5, 5, 0);

        // Left: 上一页 | 页码 | 下一页/加页 | 退出 (MainWindow.xaml 848-987).
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _prevL = MakeCell(GlyphPrev, "上一页", NavigatePrevious, 60, 24, firstBorder, firstCorner);
        _nextL = MakeCell(GlyphNext, "下一页", NavigateNext, 60, 24, lastBorder, lastCorner);
        var exitL = MakeCell(GlyphExit, "退出", ExitBoard, 55, 24, new Thickness(1), new CornerRadius(5));
        exitL.Button.Margin = new Thickness(5, 0, 0, 0);
        left.Children.Add(_prevL.Button);
        left.Children.Add(MakeIndicator(_pageTextL, midBorder));
        left.Children.Add(_nextL.Button);
        left.Children.Add(exitL.Button);

        // The center strip keeps one entry for each tool family.
        var center = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _gestureCell = MakeCell(GlyphGesture, "手势", ShowGestureFlyout, 60, 28, firstBorder, firstCorner);
        var canvas = MakeCell(GlyphCanvas, "画布", null, 60, 22, midBorder);
        AttachClickAndHold(canvas.Button, ToggleBoardColor, BuildCanvasFlyout);
        ToolTipService.SetToolTip(canvas.Button, "点击切换黑板/白板；长按选择纸型、缩放与网格间距");
        _selectCell = MakeCell(GlyphSelect, "选择", SelectOrSelectAll, 60, 22, midBorder);
        _penCell = MakeCell(GlyphPen, "墨迹", () => ActivateToolOptions(InkTool.Pen, ShowPenPalette), 60, 22, midBorder);
        _eraserCell = MakeCell(GlyphEraser, "橡皮", () => ActivateToolOptions(InkTool.Eraser, ShowEraserPalette), 60, 22, midBorder);
        ToolTipService.SetToolTip(_eraserCell.Button, "选择面积擦、墨迹擦、套索擦和橡皮大小");
        var clear = MakeCell(GlyphClear, "清屏", () =>
        {
            DismissBoardFlyout();
            ClearCurrentPage();
        }, 60, 22, midBorder);
        ToolTipService.SetToolTip(clear.Button, "清除当前页上的全部墨迹、图片和视频");
        _shapeCell = MakeCell(GlyphShape, "图形", () => ActivateToolOptions(InkTool.Shape, () => _shell?.OpenShapePicker()), 60, 22, midBorder);
        var image = MakeCell(GlyphImage, "插入图片", async () => await InsertImageAsync(), 60, 22, midBorder);
        _undoCell = MakeCell(GlyphUndo, "撤销", () => _board.Current.Undo(), 60, 22, midBorder);
        _redoCell = MakeCell(GlyphRedo, "重做", () => _board.Current.Redo(), 60, 22, midBorder);
        var aiCell = MakeCell(Glyphs.Ai, "AI 助教", () => _shell?.OpenAiAssistant(), 60, 22, midBorder);
        var sideBoardCell = MakeCell(Glyphs.Add, "板中板", () => _shell?.ToggleSideBoard(), 60, 22, midBorder);
        ToolTipService.SetToolTip(sideBoardCell.Button, "板中板 — 快捷键 Alt+N");
        _moreCell = MakeCell(GlyphMore, "更多", ShowMoreFlyout, 60, 24, midBorder);
        var exitC = MakeCell(GlyphExit, "退出", ExitBoard, 60, 24, lastBorder, lastCorner);
        foreach (var cell in new[] { _gestureCell, canvas, _selectCell, _penCell, _eraserCell, clear, _shapeCell, image, _undoCell, _redoCell, aiCell, sideBoardCell, _moreCell, exitC })
            center.Children.Add(cell.Button);

        _toolCells.Add((_selectCell, () => _board.Current.Tool == InkTool.Select));
        _toolCells.Add((_penCell, () => _board.Current.Tool is InkTool.Pen or InkTool.Highlighter));
        _toolCells.Add((_eraserCell, () => _board.Current.Tool == InkTool.Eraser));
        _toolCells.Add((_shapeCell, () => _board.Current.Tool == InkTool.Shape));

        // Right mirror: 加页 | 上一页 | 页码 | 下一页/加页 (MainWindow.xaml 2837-2976).
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _addR = MakeCell(GlyphAddPage, "加页", InsertPage, 55, 24, new Thickness(1), new CornerRadius(3));
        _addR.Button.Margin = new Thickness(0, 0, 5, 0);
        _prevR = MakeCell(GlyphPrev, "上一页", NavigatePrevious, 60, 24, firstBorder, new CornerRadius(3, 0, 0, 3));
        _nextR = MakeCell(GlyphNext, "下一页", NavigateNext, 60, 24, lastBorder, lastCorner);
        right.Children.Add(_addR.Button);
        right.Children.Add(_prevR.Button);
        right.Children.Add(MakeIndicator(_pageTextR, midBorder));
        right.Children.Add(_nextR.Button);

        return (left, center, right);
    }

    private Border MakeIndicator(TextBlock indexText, Thickness border)
    {
        indexText.Margin = new Thickness(0, 4, 0, 2);
        var caption = new TextBlock
        {
            Text = "页面",
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Viewbox { Width = 52, Height = 23, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, Child = indexText });
        stack.Children.Add(caption);
        var openPages = new Button { Content = stack, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        openPages.Click += (_, _) =>
        {
            bool right = ReferenceEquals(indexText, _pageTextR);
            bool open = !PagePaneOpen || _pagePaneRight != right;
            _pagePaneRight = right;
            SetPagePaneOpen(open);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(openPages, "页面管理与总览");
        var indicator = new Border { Width = 58, Height = 55, BorderThickness = border, Child = openPages };
        ToolTipService.SetToolTip(indicator, "页面管理与总览");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(indicator, "页面管理与总览");
        _indicatorBorders.Add(indicator);
        _indicatorTexts.Add(indexText);
        _indicatorTexts.Add(caption);
        return indicator;
    }

    /// <summary>A 60x55 WPF board button: Fluent glyph above a 12px Chinese label.</summary>
    private ToolCell MakeCell(string glyph, string label, Action onClick, double width, double glyphSize,
        Thickness border, CornerRadius corner = default, string badge = null, double badgeSize = 14)
    {
        var icon = glyph == Glyphs.Ai ? null
            : new FontIcon { Glyph = glyph, FontFamily = IconFont, FontSize = glyphSize, IsTextScaleFactorEnabled = false };
        FontIcon badgeIcon = null;
        FontIcon aiLabel = null;
        UIElement iconHost;
        if (glyph == Glyphs.Ai)
        {
            // AI is a sentinel glyph: render the shared AiGlyph label inside the same metric box.
            aiLabel = AiGlyph.CreateLabel(AiGlyph.ButtonLabelSize, _barFg);
            iconHost = new Viewbox { Width = 28, Height = 28, Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, Child = aiLabel,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        }
        else if (badge is null)
        {
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            iconHost = new Viewbox { Width = 28, Height = 28, Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, Child = icon,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        }
        else
        {
            // WPF overlays a small mode badge on the base glyph (选择/面积擦/墨迹擦).
            icon.Width = 28;
            icon.Height = 28;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Top;
            badgeIcon = new FontIcon
            {
                Glyph = badge,
                FontFamily = IconFont,
                FontSize = badgeSize,
                IsTextScaleFactorEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -2, -3),
            };
            iconHost = new Grid { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            ((Grid)iconHost).Children.Add(icon);
            ((Grid)iconHost).Children.Add(badgeIcon);
        }
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var content = new Grid { Margin = new Thickness(5) };
        content.Children.Add(iconHost);
        content.Children.Add(labelText);
        var face = new Border { BorderThickness = border, CornerRadius = corner, Child = content };

        var button = new Button
        {
            Width = width,
            Height = 55,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = corner,
            Content = face,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        if (onClick is not null) button.Click += (_, _) => onClick();
        var cell = new ToolCell { Button = button, Face = face, Icon = icon, AiLabel = aiLabel, BadgeIcon = badgeIcon, Label = labelText, Content = content };
        button.PointerEntered += (_, _) => { cell.Hovered = true; RefreshCellFace(cell); };
        button.PointerExited += (_, _) => { cell.Hovered = false; cell.Pressed = false; RefreshCellFace(cell); };
        button.PointerPressed += (_, _) => { cell.Pressed = true; RefreshCellFace(cell); };
        button.PointerReleased += (_, _) => { cell.Pressed = false; RefreshCellFace(cell); };
        button.PointerCanceled += (_, _) => { cell.Pressed = false; cell.Hovered = false; RefreshCellFace(cell); };
        _allCells.Add(cell);
        ApplyCellTheme(cell);
        return cell;
    }

    private static SolidColorBrush CloneBrush(SolidColorBrush b)
        => new() { Color = b.Color, Opacity = b.Opacity };

    private void ApplyCellTheme(ToolCell cell)
    {
        ApplyButtonTheme(cell.Button);
        // Paint the board face from explicit state; native button theme animations cannot retain an old active brush.
        cell.Button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            cell.Button.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        cell.Hovered = false; cell.Pressed = false;
        cell.Face.BorderBrush = CloneBrush(_barBorder);
        RefreshCellFace(cell);
        if (cell.Icon is not null) cell.Icon.Foreground = _barFg;
        if (cell.AiLabel is not null) cell.AiLabel.Foreground = _barFg;
        if (cell.BadgeIcon is not null) cell.BadgeIcon.Foreground = _barFg;
        cell.Label.Foreground = _barFg;
    }

    private void RefreshCellFace(ToolCell cell)
    {
        var brush = !cell.Button.IsEnabled ? _barBg : cell.Pressed ? _pressedBg : cell.Selected ? _selectedBg : cell.Hovered ? _hoverBg : _barBg;
        cell.Face.Background = CloneBrush(brush);
    }

    /// <summary>
    /// Pins the WinUI button template states to the WPF board bar palette: transparent-state
    /// hover/pressed tints (#00CDCD over the bar) and a stable foreground/border.
    /// </summary>
    private void ApplyButtonTheme(Button button)
    {
        button.Background = _barBg;
        button.BorderBrush = _barBorder;
        button.Foreground = _barFg;
        // Fresh brush instances per resource slot: WinUI rejects inserting a brush that is
        // already parented by a property assignment (COMException 0x800F0902).
        button.Resources["ButtonBackground"] = CloneBrush(_barBg);
        button.Resources["ButtonBackgroundPointerOver"] = CloneBrush(_hoverBg);
        button.Resources["ButtonBackgroundPressed"] = CloneBrush(_pressedBg);
        button.Resources["ButtonBackgroundDisabled"] = CloneBrush(_barBg);
        button.Resources["ButtonForeground"] = CloneBrush(_barFg);
        button.Resources["ButtonForegroundPointerOver"] = CloneBrush(_barFg);
        button.Resources["ButtonForegroundPressed"] = CloneBrush(_barFg);
        button.Resources["ButtonForegroundDisabled"] = CloneBrush(_barFg);
        button.Resources["ButtonBorderBrush"] = CloneBrush(_barBorder);
        button.Resources["ButtonBorderBrushPointerOver"] = CloneBrush(_barBorder);
        button.Resources["ButtonBorderBrushPressed"] = CloneBrush(_barBorder);
        button.Resources["ButtonBorderBrushDisabled"] = CloneBrush(_barBorder);
        VisualStateManager.GoToState(button, button.IsEnabled ? "Normal" : "Disabled", false);
    }

    private void SetCellEnabled(ToolCell cell, bool enabled)
    {
        cell.Button.IsEnabled = enabled;
        // WPF Element_IsEnabledChanged dims the button content to 0.5.
        cell.Content.Opacity = enabled ? 1 : 0.5;
        cell.Hovered = false; cell.Pressed = false; RefreshCellFace(cell);
    }

    /// <summary>Composite a semi-transparent tint over a semi-transparent base brush.</summary>
    private static SolidColorBrush Blend(SolidColorBrush baseBrush, Windows.UI.Color tint)
    {
        float ba = baseBrush.Color.A / 255f, ta = tint.A / 255f;
        float outA = ba + ta * (1 - ba);
        if (outA <= 0) return new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        byte Mix(byte bc, byte tc) => (byte)Math.Clamp(Math.Round((bc * ba + tc * ta * (1 - ba)) / outA), 0, 255);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)Math.Round(outA * 255),
            Mix(baseBrush.Color.R, tint.R),
            Mix(baseBrush.Color.G, tint.G),
            Mix(baseBrush.Color.B, tint.B)));
    }

    // ---- click + long-press -------------------------------------------------

    /// <summary>
    /// WPF board keeps 画布 a one-tap black/white toggle; the port's paper/scale options and
    /// the eraser size picker ride on a long-press so the strip order stays authentic.
    /// </summary>
    private void AttachClickAndHold(Button button, Action onClick, Func<FlyoutBase> holdFlyoutFactory)
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(550);
        bool pressed = false, opened = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!pressed) return;
            opened = true;
            var flyout = holdFlyoutFactory();
            flyout.Placement = FlyoutPlacementMode.Top;
            ShowBoardFlyout(flyout, button);
        };
        button.PointerPressed += (_, _) => { pressed = true; opened = false; timer.Start(); };
        button.PointerReleased += (_, _) => { pressed = false; timer.Stop(); };
        button.PointerCanceled += (_, _) => { pressed = false; timer.Stop(); };
        button.PointerCaptureLost += (_, _) => { pressed = false; timer.Stop(); };
        button.Click += (_, _) =>
        {
            if (opened) { opened = false; return; }
            onClick();
        };
    }

    // ---- tools --------------------------------------------------------------

    private void SetTool(InkTool tool)
    {
        CurrentController.EndPlayback();
        if (_board.Current.Tool == tool) { OnToolChanged(); return; }
        _board.Current.Tool = tool;
    }

    public void SelectOrSelectAll()
    {
        if (CurrentController.Tool == InkTool.Select) CurrentController.SelectAll();
        else SetTool(InkTool.Select);
    }

    public void ShowPenPalette()
    {
        if (ShowDetachedPanel(_inkOptionsFlyout)) return;
        if (_inkOptionsFlyout is not null && ReferenceEquals(_activeBoardFlyout, _inkOptionsFlyout)) { DismissBoardFlyout(); return; }
        var palette = _boardPenPalette = new BoardPenPalette(CurrentController, _services.Settings, _boardTheme);
        var flyout = new Flyout { Content = palette, Placement = FlyoutPlacementMode.Top };
        palette.SettingsChanged += () => { _services.ApplySettings(); _services.SaveSettings(); };
        palette.ColorPicked += () => { _services.RememberToolOptions(CurrentController); HideBoardFlyout(flyout); };
        _inkOptionsFlyout = flyout;
        ShowBoardFlyout(_inkOptionsFlyout, _penCell.Button);
    }

    internal Windows.Graphics.RectInt32 GetToolbarBounds() => WindowChrome.GetScreenBounds(this, _dock);

    /// <summary>
    /// AI 自动布局安全区（屏幕 DIP）：顶部标题/悬浮栏 96，底部按工具栏实测高度；
    /// 上下再叠加选中对象浮动工具栏的余量，避免卡片被浮动条遮挡。
    /// </summary>
    public BoardSafeInsets GetContentSafeInsets()
    {
        double reserve = BoardContentLayoutPlanner.SelectionToolbarReserve;
        return new(
            96 + reserve,
            Math.Max(120, _dock.ActualHeight + 16) + reserve,
            24,
            24);
    }

    private void ShowBoardFlyout(FlyoutBase flyout, FrameworkElement anchor)
    {
        DismissBoardFlyout();
        _activeBoardFlyout = flyout;
        if (flyout is Flyout { Content: FrameworkElement panelContent } panelFlyout)
        {
            var header = panelContent switch
            {
                BoardPenPalette palette => palette.Header,
                BoardGesturePanel gestures => gestures.Header,
                Border { Child: StackPanel panel } => panel.Children.OfType<PopupHeader>().FirstOrDefault(),
                _ => null,
            };
            if (header is not null)
            {
                _activeBoardHeader = header;
                panelFlyout.Content = null;
                var window = new ToolOptionsWindow(this, panelContent, header, () => WindowChrome.GetScreenBounds(this, anchor),
                    () => _services.Settings.Appearance.BlackboardScale / 100.0, _boardTheme, GetToolbarBounds);
                _toolPanels.Add(flyout, window);
                SizeChangedEventHandler fit = (_, _) => { DispatcherQueue.TryEnqueue(window.Fit); };
                _contentRoot.SizeChanged += fit;
                _dock.SizeChanged += fit;
                window.Closed += (_, _) =>
                {
                    _contentRoot.SizeChanged -= fit;
                    _dock.SizeChanged -= fit;
                    _toolPanels.Remove(flyout);
                    ClearFlyoutReferences(flyout);
                };
                return;
            }
        }
        flyout.OverlayInputPassThroughElement = _contentRoot;
        flyout.ShowMode = FlyoutShowMode.Transient;
        if (flyout is Flyout card)
        {
            StyleBoardFlyout(card);
            if (card.Content is FrameworkElement content)
            {
                _activeBoardHeader = content switch
                {
                    BoardPenPalette palette => palette.Header,
                    BoardGesturePanel gestures => gestures.Header,
                    Border { Child: StackPanel panel } => panel.Children.OfType<PopupHeader>().FirstOrDefault(),
                    _ => null,
                };
                if (_activeBoardHeader is { } header)
                {
                    header.CloseRequested += DismissBoardFlyout;
                    card.Closing += (_, args) => args.Cancel = ReferenceEquals(_activeBoardFlyout, flyout) && header.IsPinned;
                }
                card.Content = null;
                var host = new Viewbox
                {
                    Child = content, Stretch = Stretch.Uniform, UseLayoutRounding = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                };
                card.Content = host;
                bool fitPending = false;
                void FitContent()
                {
                    if (_closed || !ReferenceEquals(_activeBoardFlyout, flyout)) return;
                    // Button templates and theme resources resolve after attachment. Their final
                    // minimum widths can differ from an unrooted measurement, changing the aspect ratio.
                    content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    var desired = content.DesiredSize;
                    if (desired.Width <= 0 || desired.Height <= 0) return;
                    double scale = Math.Clamp(_services.Settings.Appearance.BlackboardScale, 50, 150) / 100.0;
                    scale = Math.Min(scale, Math.Min(Math.Max(1, _contentRoot.ActualWidth - 24) / desired.Width,
                        Math.Max(1, _contentRoot.ActualHeight - _dock.ActualHeight - 24) / desired.Height));
                    double width = desired.Width * scale, height = desired.Height * scale;
                    if (double.IsNaN(host.Width) || Math.Abs(host.Width - width) > .01) host.Width = width;
                    if (double.IsNaN(host.Height) || Math.Abs(host.Height - height) > .01) host.Height = height;
                }
                void QueueFit(object sender, SizeChangedEventArgs args)
                {
                    if (fitPending) return;
                    fitPending = true;
                    DispatcherQueue.TryEnqueue(() => { fitPending = false; FitContent(); });
                }
                void OnLoaded(object sender, RoutedEventArgs args) => FitContent();
                content.Loaded += OnLoaded;
                content.SizeChanged += QueueFit;
                _contentRoot.SizeChanged += QueueFit;
                _dock.SizeChanged += QueueFit;
                flyout.Closed += (_, _) =>
                {
                    content.Loaded -= OnLoaded;
                    content.SizeChanged -= QueueFit;
                    _contentRoot.SizeChanged -= QueueFit;
                    _dock.SizeChanged -= QueueFit;
                };
                FitContent();
            }
        }
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeBoardFlyout, flyout)) { _activeBoardFlyout = null; _activeBoardHeader = null; }
            if (ReferenceEquals(_inkOptionsFlyout, flyout)) _inkOptionsFlyout = null;
            if (ReferenceEquals(_moreFlyout, flyout)) _moreFlyout = null;
            if (ReferenceEquals(_gestureFlyout, flyout)) _gestureFlyout = null;
            if (ReferenceEquals(_eraserFlyout, flyout)) _eraserFlyout = null;
        };
        flyout.ShowAt(anchor);
    }
    private void DismissBoardFlyout()
    {
        var current = _activeBoardFlyout;
        if (current is null) return;
        if (_toolPanels.TryGetValue(current, out var window))
        {
            if (!window.IsPinned) window.Close();
            return;
        }
        ClearFlyoutReferences(current);
        current.Hide();
    }

    private void ClearFlyoutReferences(FlyoutBase current)
    {
        if (ReferenceEquals(_activeBoardFlyout, current)) { _activeBoardFlyout = null; _activeBoardHeader = null; }
        if (ReferenceEquals(_inkOptionsFlyout, current)) _inkOptionsFlyout = null;
        if (ReferenceEquals(_moreFlyout, current)) _moreFlyout = null;
        if (ReferenceEquals(_gestureFlyout, current)) _gestureFlyout = null;
        if (ReferenceEquals(_eraserFlyout, current)) _eraserFlyout = null;
    }

    private bool ShowDetachedPanel(FlyoutBase flyout)
    {
        if (flyout is null || !_toolPanels.TryGetValue(flyout, out var window) || !window.IsPinned) return false;
        WindowChrome.ShowWithoutActivation(window);
        WindowChrome.EnsureAbove(window);
        return true;
    }

    private void ActivateToolOptions(InkTool tool, Action open)
    {
        bool active = CurrentController.Tool == tool || tool == InkTool.Pen && CurrentController.Tool == InkTool.Highlighter;
        if (active) open();
        else SetTool(tool);
    }

    private void HideBoardFlyout(FlyoutBase flyout)
    {
        if (_toolPanels.TryGetValue(flyout, out var window))
        {
            if (!window.IsPinned) window.Close();
        }
        else flyout.Hide();
    }
    internal void OnCanvasInputBegan()
    {
        if (_activeBoardHeader?.IsPinned != true) DismissBoardFlyout();
        _surface.Focus(FocusState.Programmatic); CanvasInputBegan?.Invoke();
    }

    private void OnBoardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x5B) < 0 || InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x5C) < 0) return;
        bool control = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x11) < 0;
        bool shift = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x10) < 0;
        bool alt = InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x12) < 0;
        e.Handled = TryHandleBoardShortcut(e.Key, control, shift, alt, e.OriginalSource as DependencyObject);
    }

    internal bool TryHandleBoardShortcut(Windows.System.VirtualKey key, bool control, bool shift, bool alt, DependencyObject source)
    {
        if (_closed || CurrentController.Tool == InkTool.Pan || KeyboardShortcutScope.IsTextInput(source)
            || KeyboardShortcutScope.HasTextInputFocus(_contentRoot) || shift) return false;
        if (alt && !control && key == Windows.System.VirtualKey.A)
        {
            _shell?.OpenAiAssistant();
            return true;
        }
        if (alt) return false;
        if (control && key == Windows.System.VirtualKey.Z) CurrentController.Undo();
        else if (control && key == Windows.System.VirtualKey.Y) CurrentController.Redo();
        else if (control && key == Windows.System.VirtualKey.E)
        {
            if (CurrentController.HasSelection) DeleteSelection();
            else if (_shell is not null) _shell.ClearCanvas();
            else _board.ClearCurrentPage();
        }
        else if (control && key == Windows.System.VirtualKey.A) CurrentController.SelectAll();
        else if (!control && key == Windows.System.VirtualKey.Delete && CurrentController.HasSelection) DeleteSelection();
        else if (!control && key == Windows.System.VirtualKey.Escape)
        {
            DismissBoardFlyout(); CurrentController.EndPlayback(); CurrentController.CancelLassoErase(); CurrentController.ClearSelection();
        }
        else return false;
        return true;
    }

    private void OnToolChanged()
    {
        var tool = _board.Current.Tool;
        bool select = tool == InkTool.Select;
        _surface.IsHitTestVisible = !select;
        _images.IsHitTestVisible = select;
        _media.IsHitTestVisible = select;
        if (!select)
        {
            _pendingAiNativeEdit = false;
            _lastNativeTapTicks = 0;
            _images.ClearSelection();
            _media.ClearSelection();
            // WPF InkCanvas drops the selection when the editing mode leaves Select.
            _board.Current.ClearSelection();
            FinishSelectionPointers();
        }
        UpdateToolHighlight();
        UpdateSelectionActions();
    }

    private void UpdateToolHighlight()
    {
        foreach (var (cell, active) in _toolCells)
        {
            cell.Selected = active();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(cell.Button, cell.Selected ? "已选中" : "");
            RefreshCellFace(cell);
        }
    }

    private void ToggleBoardColor()
    {
        // One-tap 画布 toggle (MW_BoardIcons.cs BoardChangeBackgroundColorBtn_Click).
        SetBackground(_board.Background == BoardBackground.Black ? BoardBackground.White : BoardBackground.Black);
    }

    public void ShowEraserPalette()
    {
        if (ShowDetachedPanel(_eraserFlyout)) return;
        if (_eraserFlyout is not null && ReferenceEquals(_activeBoardFlyout, _eraserFlyout)) { DismissBoardFlyout(); return; }
        SetTool(InkTool.Eraser);
        ApplyEraserSize();
        var palette = new EraserPalette(CurrentController, _services.Settings.Canvas.EraserSize, _boardTheme);
        var header = new PopupHeader("橡皮", _boardTheme);
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        palette.ModePicked += mode =>
        {
            _services.Settings.Canvas.EraserType = mode switch
            {
                InkEraserMode.Area => 1,
                InkEraserMode.WholeStroke => 2,
                _ => 3,
            };
            CurrentController.EraserMode = mode;
            _services.ApplySettings();
            ApplyEraserSize();
            _services.SaveSettings();
            SetTool(InkTool.Eraser);
            HideBoardFlyout(flyout);
        };
        palette.SizePicked += size =>
        {
            _services.Settings.Canvas.EraserSize = size;
            _services.ApplySettings();
            ApplyEraserSize();
            _services.SaveSettings();
        };
        flyout.Content = new Border { Background = _barBg, BorderBrush = InkCanvas.Controls.Theming.PopupBrushes.ToolPanelBorder(_boardTheme), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = new StackPanel { Children = { header, palette } } };
        _eraserFlyout = flyout;
        ShowBoardFlyout(_eraserFlyout, _eraserCell.Button);
    }

    private void ApplyEraserSize()
    {
        float radius = OriginalMenuCatalog.ToEraserRadius(_services.Settings.Canvas.EraserSize);
        foreach (var page in _board.Pages)
            page.EraserRadius = page.EraserMode == InkEraserMode.WholeStroke ? 2.5f : radius;
    }

    private static void StyleBoardFlyout(Flyout flyout)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FlyoutPresenter.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxHeightProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(FlyoutPresenter.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(FlyoutPresenter.VerticalContentAlignmentProperty, VerticalAlignment.Top));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = style;
    }

    // ---- flyouts ------------------------------------------------------------

    private void ShowMoreFlyout()
    {
        if (ShowDetachedPanel(_moreFlyout)) return;
        if (_moreFlyout is not null && ReferenceEquals(_activeBoardFlyout, _moreFlyout)) { DismissBoardFlyout(); return; }
        _moreFlyout = BuildMoreFlyout(); ShowBoardFlyout(_moreFlyout, _moreCell.Button);
    }

    internal void RefreshExtensionCommands()
    {
        if (_moreFlyout is null) return;
        if (_toolPanels.TryGetValue(_moreFlyout, out var window)) window.Close();
        else _moreFlyout.Hide();
        _moreFlyout = null;
    }

    private void ShowGestureFlyout()
    {
        if (ShowDetachedPanel(_gestureFlyout)) return;
        if (_gestureFlyout is not null && ReferenceEquals(_activeBoardFlyout, _gestureFlyout)) { DismissBoardFlyout(); return; }
        _gestureFlyout = BuildGestureFlyout(); ShowBoardFlyout(_gestureFlyout, _gestureCell.Button);
    }

    /// <summary>更多功能: WPF 3x3 icon grid (plus the local AI 助教 cell) + 视频展台|Desmos row, plus relocated port extras.</summary>
    private Flyout BuildMoreFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        StyleBoardFlyout(flyout);

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnSpacing = 6;
        grid.RowSpacing = 6;
        var items = new (string Glyph, string Label, Action Click, bool Badge)[]
        {
            (Glyphs.TimerWpf, "倒计时", () => { HideBoardFlyout(flyout); _shell?.OpenTimer(); }, false),
            (Glyphs.Rand, "随机抽选", () => { HideBoardFlyout(flyout); _shell?.OpenLottery(); }, false),
            (Glyphs.Rand, "单次抽选", () => { HideBoardFlyout(flyout); _shell?.OpenLotteryQuick(); }, true),
            (Glyphs.SaveStrokes, "保存墨迹", async () => { HideBoardFlyout(flyout); await SavePageAsync(); }, false),
            (Glyphs.OpenStrokes, "打开墨迹", async () => { HideBoardFlyout(flyout); await OpenPageAsync(); }, false),
            (Glyphs.ReplayWpf, "播放墨迹", () => { HideBoardFlyout(flyout); _shell?.OpenReplay(); }, false),
            (Glyphs.Camera, "截屏", async () => { HideBoardFlyout(flyout); if (_shell is not null) await _shell.ScreenshotAsync(); }, false),
            (Glyphs.Help, "指南", () => { HideBoardFlyout(flyout); _shell?.OpenGuide(); }, false),
            (Glyphs.Settings, "设置", () => { HideBoardFlyout(flyout); _shell?.OpenSettings(); }, false),
            (Glyphs.Ai, "AI 助教", () => { HideBoardFlyout(flyout); _shell?.OpenAiAssistant(); }, false),
            (Glyphs.TimeMachine, "时光机", () => { HideBoardFlyout(flyout); _shell?.OpenTimeMachine(); }, false),
            (Glyphs.InsertMedia, "插入", ShowBoardInsertMenu, false),
        };
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var iconHost = new Grid { Width = 40, Height = 40 };
            if (item.Glyph == Glyphs.Ai)
            {
                iconHost.Children.Add(AiGlyph.CreateFramed(40, 40, _barFg));
            }
            else
            {
                iconHost.Children.Add(new FontIcon
                {
                    Glyph = item.Glyph,
                    FontFamily = IconFont,
                    FontSize = 24,
                    Foreground = _barFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (item.Badge)
            {
                var badge = new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(6),
                    Background = PopupBrushes.Accent(),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "1", FontSize = 8, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                iconHost.Children.Add(badge);
            }
            var cellButton = new Button
            {
                Width = 56,
                Height = 56,
                Padding = new Thickness(0),
                Content = iconHost,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
            };
            cellButton.Resources["ButtonBackgroundPointerOver"] = _hoverBg;
            cellButton.Resources["ButtonBackgroundPressed"] = _pressedBg;
            ToolTipService.SetToolTip(cellButton, item.Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cellButton, item.Label);
            var click = item.Click;
            cellButton.Click += (_, _) => click();
            Grid.SetRow(cellButton, i / 3);
            Grid.SetColumn(cellButton, i % 3);
            grid.Children.Add(cellButton);
        }

        // WPF bottom row: retain the original bundled 视频展台 / Desmos artwork.
        var launcherRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        launcherRow.Children.Add(MakeLauncherButton("\uF865", "视频展台", async () =>
        {
            HideBoardFlyout(flyout);
            // Original BoardLaunchEasiCamera_Click first switched back from board mode.
            if (_shell is not null) _shell.ToggleBoard();
            await Task.Delay(120);
            if (!Platform.System.SoftwareLauncher.LaunchEasiCamera("希沃视频展台"))
                await ShowMessageAsync("未找到希沃视频展台安装路径，或启动器无法运行。");
        }));
        launcherRow.Children.Add(new Border { Width = 1, Height = 25, Margin = new Thickness(4, 0, 4, 0), Background = _barFg, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        launcherRow.Children.Add(MakeLauncherButton("\uE247", "Desmos", () =>
        {
            HideBoardFlyout(flyout);
            if (_shell is not null) _shell.ToggleBoard();
            Platform.System.SoftwareLauncher.LaunchDesmos();
        }));

        // Port additions beyond WPF, relocated out of the center strip.
        var extrasRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Margin = new Thickness(0, 2, 0, 6) };
        extrasRow2.Children.Add(MakeExtraButton("\uF34D", "删除本页", () => { HideBoardFlyout(flyout); DeleteCurrentPage(); }));
        extrasRow2.Children.Add(MakeExtraButton("\uF8DD", _isFullscreen ? "窗口模式" : "全屏", () => { HideBoardFlyout(flyout); SetFullscreen(!_isFullscreen); }));
        Button canvasButton = null;
        canvasButton = MakeExtraButton(GlyphCanvas, "画布纸型", () => ShowBoardFlyout(BuildCanvasFlyout(), _moreCell.Button));
        extrasRow2.Children.Add(canvasButton);

        var title = new PopupHeader("更多功能", _boardTheme);
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(grid);
        panel.Children.Add(launcherRow);
        panel.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 0, 8, 4), Background = _barBorder });
        panel.Children.Add(extrasRow2);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        navigation.Children.Add(MakeExtraButton(Glyphs.GestureTranslate, "单指拖动", () => { HideBoardFlyout(flyout); SetTool(InkTool.Pan); }));
        navigation.Children.Add(MakeExtraButton(Glyphs.Restart, "复位画布", () =>
        {
            HideBoardFlyout(flyout); CurrentController.ViewTransform = Matrix3x2.Identity; CurrentController.Document.Touch();
        }));
        panel.Children.Add(navigation);
        panel.Children.Add(MakeExtraButton("\uF379", "页面管理", () => { HideBoardFlyout(flyout); SetPagePaneOpen(true); }));
        if (_shell is not null)
        foreach (var commands in _shell.ExtensionCommands.Where(command => command.PluginId != "inkcanvas.library"
            && !(command.PluginId == "inkcanvas.pdf" && command.CommandId == "import")).Chunk(3))
        {
            var extensionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
            foreach (var command in commands)
                extensionRow.Children.Add(MakeExtraButton(ExtensionCommandIcons.For(command), command.Title, async () => { HideBoardFlyout(flyout); await _shell.ExecuteExtensionCommandAsync(command); }));
            panel.Children.Add(extensionRow);
        }

        panel.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 6, 8, 4), Background = _barBorder });
        var files = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        files.Children.Add(MakeExtraButton(Glyphs.SaveStrokes, "保存墨迹", async () => { HideBoardFlyout(flyout); await SavePageAsync(); }));
        files.Children.Add(MakeExtraButton(Glyphs.OpenStrokes, "打开文件", async () => { HideBoardFlyout(flyout); await OpenFileAsync(); }));
        files.Children.Add(MakeExtraButton(Glyphs.Pages, "文件选项", () => ShowBoardFlyout(BuildFileFlyout(), _moreCell.Button)));
        panel.Children.Add(files);
        var application = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        application.Children.Add(MakeExtraButton(Glyphs.Settings, "设置", () => { HideBoardFlyout(flyout); _shell?.OpenSettings(); }));
        if (_services.Settings.Appearance.IsShowExitButton)
            application.Children.Add(MakeExtraButton(Glyphs.ExitWpf, "退出", () => { HideBoardFlyout(flyout); _shell?.Exit(); }));
        panel.Children.Add(application);

        flyout.Content = new Border
        {
            Background = _barBg,
            BorderBrush = InkCanvas.Controls.Theming.PopupBrushes.ToolPanelBorder(_boardTheme),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };
        return flyout;
    }

    private Button MakeLauncherButton(string glyph, string text, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Height = 35, Margin = new Thickness(10, 0, 10, 0) };
        row.Children.Add(new Image { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"ms-appx:///Assets/Board/{(text == "Desmos" ? "Desmos" : "EasiCamera")}.png")) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = _barFg, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
        };
        button.Resources["ButtonBackgroundPointerOver"] = _hoverBg;
        button.Resources["ButtonBackgroundPressed"] = _pressedBg;
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button MakeExtraButton(string glyph, string text, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontFamily = IconFont, FontSize = 13, Foreground = _barFg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = _barFg, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = row,
            Height = 30,
            Padding = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        button.Resources["ButtonBackgroundPointerOver"] = _hoverBg;
        button.Resources["ButtonBackgroundPressed"] = _pressedBg;
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ShowBoardInsertMenu()
    {
        var pdf = _shell?.ExtensionCommands.FirstOrDefault(command => command.PluginId == "inkcanvas.pdf" && command.CommandId == "import");
        InsertFlyout insert = null;
        insert = new InsertFlyout(_boardTheme,
            async () => { HideBoardFlyout(insert); await InsertImageAsync(); },
            async () => { HideBoardFlyout(insert); await InsertMediaAsync(); },
            pdf is null ? null : async () => { HideBoardFlyout(insert); if (_shell is not null) await _shell.ExecuteExtensionCommandAsync(pdf); });
        ShowBoardFlyout(insert, _moreCell.Button);
    }

    private Flyout BuildGestureFlyout()
    {
        var panel = new BoardGesturePanel(_services.Settings.Gesture, _boardTheme,
            _services.BoardModeActive && _services.Settings.Gesture.AutoSwitchTwoFingerGesture);
        panel.Changed += () => { _services.ApplySettings(); _services.SaveSettings(); };
        return new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
    }

    /// <summary>Port addition: paper types, view scale presets and grid spacing (画布 long-press).</summary>
    private MenuFlyout BuildCanvasFlyout()
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        foreach (var (label, background) in new[]
        {
            ("黑板", BoardBackground.Black), ("白板", BoardBackground.White),
            ("网格纸", BoardBackground.Grid), ("横线纸", BoardBackground.Lined),
            ("绿板", BoardBackground.Green), ("米色纸", BoardBackground.Cream), ("坐标纸", BoardBackground.Coordinate),
        })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => SetBackground(background);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var (label, scale) in new[] { ("缩小 · 80%", 0.8f), ("实际大小 · 100%", 1f), ("放大 · 125%", 1.25f), ("放大 · 150%", 1.5f) })
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => SetViewScale(scale);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var spacing = new MenuFlyoutSubItem { Text = "网格间距" };
        foreach (var value in new[] { 24f, 32f, 40f, 56f, 72f })
        {
            var item = new MenuFlyoutItem { Text = $"{value:0} DIP" };
            item.Click += (_, _) => { _board.GridSpacing = value; _paper.Invalidate(); };
            spacing.Items.Add(item);
        }
        flyout.Items.Add(spacing);
        return flyout;
    }

    private MenuFlyout BuildFileFlyout()
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        var savePage = new MenuFlyoutItem { Text = "保存当前页…" };
        savePage.Click += async (_, _) => await SavePageAsync();
        var saveBoard = new MenuFlyoutItem { Text = "保存整个画板…" };
        saveBoard.Click += async (_, _) => await SaveBoardAsync();
        var openPage = new MenuFlyoutItem { Text = "打开到当前页…" };
        openPage.Click += async (_, _) => await OpenPageAsync();
        var openBoard = new MenuFlyoutItem { Text = "打开整个画板…" };
        openBoard.Click += async (_, _) => await OpenBoardAsync();
        flyout.Items.Add(savePage);
        flyout.Items.Add(saveBoard);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(openPage);
        flyout.Items.Add(openBoard);
        return flyout;
    }

    // ---- selection panel (WPF BorderStrokeSelectionControl, top-left) --------

    private UIElement BuildSelectionPanelContent()
    {
        if (_selectionToolbar is null)
        {
            _selectionToolbar = new SelectionToolbar(() => CurrentController,
                async () => await SaveSelectionAsImageAsync(), CloneSelectionToNewPage, "衍至新页",
                UngroupSelectedAiGroup, instruction => _shell?.ApplyAiBoardEdit(instruction));
            _selectionToolbar.LayoutChanged += () => { _selectionMeasureDirty = true; PlaceSelectionPanel(); };
            _selectionPanel.SizeChanged += (_, _) => PlaceSelectionPanel();
        }
        _selectionToolbar.ApplyTheme(_boardTheme);
        _selectionToolbar.Refresh();
        return _selectionToolbar;
    }

    private void SyncCloneButton() => _selectionToolbar?.Refresh();

    private void TransformSelectionMatrix(Matrix3x2 local, string name, bool scaleWidth)
    {
        var controller = _board.Current;
        if (!controller.HasSelection) return;
        var center = CombinedSelectionBounds().Center;
        var matrix = Matrix3x2.CreateTranslation(-center) * local * Matrix3x2.CreateTranslation(center);
        controller.TransformSelectionUndoable(matrix, name, scaleWidth);
        PlaceSelectionPanel();
    }

    /// <summary>World-space bounds of strokes + selected elements (WPF inkCanvas.GetSelectionBounds).</summary>
    private Rect CombinedSelectionBounds()
    {
        return _board.Current.SelectionBounds;
    }

    private void PlaceSelectionPanel()
    {
        if (_selectionPlacementPending || _selectionPanel.Visibility != Visibility.Visible) return;
        _selectionPlacementPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _selectionPlacementPending = false;
            if (_selectionPanel.Visibility != Visibility.Visible || _contentRoot.ActualWidth <= 16 || _contentRoot.ActualHeight <= 16) return;
            if (_selectionMeasureDirty)
            {
                _selectionToolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                _selectionDesiredSize = _selectionToolbar.DesiredSize;
                _selectionMeasureDirty = false;
            }
            float width = (float)Math.Min(_selectionDesiredSize.Width + 10, _contentRoot.ActualWidth - 16);
            float height = (float)Math.Min(_selectionDesiredSize.Height + 10, _contentRoot.ActualHeight - 16);
            float left = PagePaneOpen && !PageNavigator.IsOverview && !_pagePaneRight ? (float)_pagePane.ActualWidth + 16 : 8;
            var work = new Rect(left, 8, (float)_contentRoot.ActualWidth - 8, (float)_contentRoot.ActualHeight - 8);
            var dock = new Rect(0, (float)(_contentRoot.ActualHeight - _dock.ActualHeight), (float)_contentRoot.ActualWidth, (float)_contentRoot.ActualHeight);
            var place = FloatingToolbarLayout.Place(work, CurrentController.SelectionVisualBounds, dock,
                new Vector2(width, height), gap: 8);
            var margin = new Thickness(Math.Round(place.Left), Math.Round(place.Top), 0, 0);
            if (_selectionPanel.Margin != margin) _selectionPanel.Margin = margin;
        });
    }

    /// <summary>衍至新页 (board mode of 衍至画板): clone the selection onto a fresh page inserted after this one.</summary>
    private void CloneSelectionToNewPage()
    {
        if (!CurrentController.HasSelection) return;
        if (!_board.CanAddPages())
        {
            _ = ShowMessageAsync("当前画板无法继续增加页面。");
            return;
        }
        AutoSaveCurrentPage("board-clone");
        _board.CloneSelectionToNewPage();
        UpdateSelectionActions();
    }

    /// <summary>
    /// 存为图片 (BtnStrokeSelectionSaveToImage_Click, MW_SelectionGestures.cs:90-152): PNG sized
    /// bounds+10 with content flush to the top-left (translate -bounds.X/Y, no centering pad),
    /// strokes first then selected elements drawn through their RenderTransform.
    /// </summary>
    private async Task SaveSelectionAsImageAsync()
    {
        if (!CurrentController.HasSelection) return;
        var picker = CreateSavePicker("PNG 图片", ".png", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff"));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { await ExportSelectionImageAsync(file.Path); }
        catch (Exception ex) { await ShowMessageAsync($"存为图片失败：{ex.Message}"); }
    }

    public async Task ExportSelectionImageAsync(string path)
    {
        var controller = _board.Current;
        var strokes = controller.Selection.ToList();
        var ids = controller.SelectedElements.Select(e => e.Id).ToHashSet();
        var images = _board.CurrentImages.Where(image => ids.Contains(image.Id)).ToList();
        var mediaObjects = _board.CurrentMedia.Where(media => ids.Contains(media.Id)).ToList();
        if (strokes.Count + images.Count + mediaObjects.Count == 0) return;
        var bounds = CombinedSelectionBounds();
        if (bounds.IsEmpty) return;

        {
            int width = Math.Clamp((int)Math.Ceiling(bounds.Width + 10), 1, 8192);
            int height = Math.Clamp((int)Math.Ceiling(bounds.Height + 10), 1, 8192);
            var device = CanvasDevice.GetSharedDevice();
            var offset = Matrix3x2.CreateTranslation(-bounds.Left, -bounds.Top);
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
            await renderer.Images.PrepareAsync(device, controller);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                foreach (var item in controller.DrawingObjects.Where(item => item.Stroke is not null
                    ? strokes.Contains(item.Stroke) : ids.Contains(item.Id)))
                {
                    session.Transform = Matrix3x2.Identity;
                    if (item.Element is not BoardSelectionElement { Media: not null } mediaItem)
                    {
                        renderer.DrawObject(session, controller, item, offset);
                        continue;
                    }
                    var media = mediaItem.Media;
                    // Best-effort current-frame capture of the video element (WPF used a VisualBrush).
                    try
                    {
                        var element = _media.GetViewElement(media);
                        if (element is not null)
                        {
                            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                            await rtb.RenderAsync(element);
                            var pixels = await rtb.GetPixelsAsync();
                            byte[] frame;
                            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
                            {
                                frame = new byte[pixels.Length];
                                reader.ReadBytes(frame);
                            }
                            using var mediaBitmap = CanvasBitmap.CreateFromBytes(device, frame, rtb.PixelWidth, rtb.PixelHeight,
                                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                            session.Transform = ElementExportTransform(media.Left, media.Top, media.Transform, offset);
                            session.DrawImage(mediaBitmap, new Windows.Foundation.Rect(0, 0, media.Width, media.Height));
                        }
                    }
                    catch
                    {
                        // Codec/frame unavailable: export the rest rather than failing the whole save.
                    }
                }
            }
            await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        }
    }

    /// <summary>Maps element-local coords to the export target: p -> (p + t) * M - boundsOrigin.</summary>
    private static Matrix3x2 ElementExportTransform(float left, float top, float[] transform, Matrix3x2 offset)
        => Matrix3x2.CreateTranslation(left, top) * BoardElementMath.Read(transform) * offset;

    private void DeleteSelection()
    {
        _board.Current.DeleteSelection();
    }

    // ---- background / scale ---------------------------------------------------

    private void SetBackground(BoardBackground background)
    {
        _board.SetBackground(background);
        bool white = background != BoardBackground.Black;
        _services.Settings.Canvas.UsingWhiteboard = white;
        _services.SaveSettings();
        PageNavigator?.InvalidateAll();
    }

    private void SetViewScale(float scale)
    {
        scale = Math.Clamp(scale, 0.5f, 2f);
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0) { _viewScale = scale; return; }
        float currentScale = ExtractScale(_board.Current.ViewTransform);
        float factor = scale / currentScale;
        var center = new Vector2((float)_surface.ActualWidth / 2, (float)_surface.ActualHeight / 2);
        var aroundCenter = Matrix3x2.CreateTranslation(-center)
            * Matrix3x2.CreateScale(factor)
            * Matrix3x2.CreateTranslation(center);
        _board.Current.ViewTransform *= aroundCenter;
        _viewScale = scale;
        _board.Current.Document.Touch();
        _images.UpdateViewTransform(_board.Current.ViewTransform);
        _media.UpdateViewTransform(_board.Current.ViewTransform);
        _paper.Invalidate();
    }

    /// <summary>Settings.Appearance.BlackboardScale (50-150), like the WPF strip ScaleTransforms.</summary>
    private void ApplyBoardScale()
    {
        if (_dock is null) return;
        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        _leftHost.Measure(infinite); _centerHost.Measure(infinite); _rightHost.Measure(infinite);
        double left = _leftHost.DesiredSize.Width, center = _centerHost.DesiredSize.Width, right = _rightHost.DesiredSize.Width;
        double width = _contentRoot.ActualWidth > 0 ? _contentRoot.ActualWidth : WindowChrome.GetWorkArea(this).Width / WindowChrome.GetDpiScale(this);
        var layout = BoardDockLayout.Calculate(width, _services.Settings.Appearance.BlackboardScale / 100.0, left, center, right);
        Fit(_leftDock, left); Fit(_centerDock, center); Fit(_rightDock, right);
        Grid.SetRow(_centerDock, layout.TwoRows ? 1 : 0);
        _centerDock.Margin = new Thickness(0, layout.TwoRows ? 4 * layout.Scale : 0, 0, 0);
        void Fit(Viewbox host, double designWidth) { host.Width = designWidth * layout.Scale; host.Height = 55 * layout.Scale; }
    }

    private static Viewbox DockStrip(UIElement child, HorizontalAlignment alignment) => new()
    { Child = child, Stretch = Stretch.Uniform, HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Bottom };

    /// <summary>
    /// Re-read Appearance.BlackboardScale and re-apply it to the three strips. The shell calls
    /// this from OnSettingsChanged so the 画板 UI 缩放 slider live-applies while the board is open.
    /// The 55-DIP bottom row keeps its proportions at 100%.
    /// </summary>
    public void RefreshUiScale()
    {
        ApplyBoardScale();
        foreach (var panel in _toolPanels.Values) panel.Fit();
    }

    internal void RefreshToolSettings() => _boardPenPalette?.RefreshSettings();

    internal void RestoreFromAnnotation()
    {
        _services.ConfigureController(CurrentController);
        ApplyEraserSize();
        RefreshBackground();
    }

    internal async Task<bool> HideForAnnotationAsync()
    {
        if (_closed || _archiveClosing || CurrentController.HasActiveInteraction) return false;
        try
        {
            await SaveArchiveBeforeCloseAsync("退出白板");
            if (_closed) return false;
            CurrentController.EndPlayback();
            CurrentController.HideEraserPreview();
            foreach (var panel in _toolPanels.Values.ToArray()) panel.Close();
            DismissBoardFlyout();
            SetPagePaneOpen(false);
            _media.PausePlayers();
            AutoSaveCurrentPage("board-exit");
            AppWindow.Hide();
            return true;
        }
        catch (Exception ex)
        {
            if (!_closed) await ShowMessageAsync("白板存档失败，内容已保留。\n" + ex.GetBaseException().Message);
            return false;
        }
    }

    internal void CloseForShutdown() => base.Close();

    private void RefreshBackground()
    {
        DismissBoardFlyout();
        bool dark = _board.Background is BoardBackground.Black or BoardBackground.Green;
        PageNavigator.ApplyTheme(dark ? 2 : 1);
        // WPF board cover: neutral #1F1F1F / #F2F2F2, independent of the application theme.
        _background.Background = new SolidColorBrush(_board.Background switch
        {
            BoardBackground.Black => Windows.UI.Color.FromArgb(255, 31, 31, 31),
            BoardBackground.Green => Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F),
            BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC),
            _ => Windows.UI.Color.FromArgb(255, 242, 242, 242),
        });
        _boardTheme = dark ? 2 : 1;
        WindowChrome.ApplyTheme(this, _boardTheme);

        _barBg = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(191, 0, 0, 0) : Windows.UI.Color.FromArgb(191, 255, 255, 255));
        _barBorder = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(153, 255, 255, 255) : Windows.UI.Color.FromArgb(204, 0, 0, 0));
        _barFg = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        _barTitleBg = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(128, 33, 86, 201) : Windows.UI.Color.FromArgb(64, 88, 139, 252));
        _hoverBg = Blend(_barBg, dark ? Windows.UI.Color.FromArgb(54, 0, 205, 205) : Windows.UI.Color.FromArgb(34, 0, 205, 205));
        _pressedBg = Blend(_barBg, dark ? Windows.UI.Color.FromArgb(85, 0, 205, 205) : Windows.UI.Color.FromArgb(68, 0, 205, 205));
        _selectedBg = Blend(_barBg, Windows.UI.Color.FromArgb(90, 0, 102, 191));

        foreach (var cell in _allCells) ApplyCellTheme(cell);
        foreach (var border in _indicatorBorders)
        {
            border.Background = _barBg;
            border.BorderBrush = _barBorder;
        }
        foreach (var text in _indicatorTexts) text.Foreground = _barFg;
        _selectionPanel.Background = _barBg;
        _selectionPanel.BorderBrush = _barBorder;
        _selectionPanel.Child = BuildSelectionPanelContent();
        UpdateToolHighlight();

        bool savedColor = CurrentController.Tool == InkTool.Highlighter ? _services.Settings.Canvas.HighlighterColor.HasValue : _services.Settings.Canvas.PenColor.HasValue;
        if (!savedColor && dark && IsBlackLike(_board.Current.CurrentAttributes.Color))
            _board.Current.CurrentAttributes.Color = InkColor.Opaque(245, 245, 245);
        else if (!savedColor && !dark && IsWhiteLike(_board.Current.CurrentAttributes.Color))
            _board.Current.CurrentAttributes.Color = InkColor.Opaque(20, 20, 20);
        _paper.Invalidate();
    }

    private static bool IsDarkBackground(BoardBackground background)
        => background is BoardBackground.Black or BoardBackground.Green;

    private static Windows.UI.Color PageBackgroundColor(BoardBackground background) => background switch
    {
        BoardBackground.Black => (Windows.UI.Color)Application.Current.Resources["IcBoardBlackColor"],
        BoardBackground.Green => Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F),
        BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC),
        _ => Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA),
    };

    private static bool IsBlackLike(InkColor color)
        => color.R <= 32 && color.G <= 32 && color.B <= 32;

    private static bool IsWhiteLike(InkColor color)
        => color.R >= 240 && color.G >= 240 && color.B >= 240;

    private void OnDrawPaper(CanvasControl sender, CanvasDrawEventArgs args)
    {
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

        using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 12 };
        session.DrawText("O", originX + 4, originY + 4, axis, format);
        session.DrawText("x", width - 18, originY + 4, axis, format);
        session.DrawText("y", originX + 4, 4, axis, format);
        session.DrawText($"单位：1 格 = {spacing:0} DIP", 8, height - 20, axis, format);
    }

    private void OnImageLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_images).Position;
        var viewPoint = e.GetCurrentPoint(_surfaceHost).Position;
        long now = Environment.TickCount64;
        bool isDoubleTap = now - _lastImageTapTicks <= 450
            && Math.Abs(viewPoint.X - _lastImageTapPoint.X) <= 6
            && Math.Abs(viewPoint.Y - _lastImageTapPoint.Y) <= 6;
        _lastImageTapTicks = isDoubleTap ? 0 : now;
        _lastImageTapPoint = viewPoint;
        if (!isDoubleTap) return;

        if (TryEditAiObjectAtView(new Vector2((float)viewPoint.X, (float)viewPoint.Y), "手动双击判定"))
        {
            e.Handled = true;
            return;
        }
        if (TryEditAiCardAt(point, "手动双击判定"))
            e.Handled = true;
    }

    private void OnImageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var viewPoint = e.GetPosition(_surfaceHost);
        if (TryEditAiObjectAtView(new Vector2((float)viewPoint.X, (float)viewPoint.Y), "DoubleTapped"))
        {
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(_images);
        if (TryEditAiCardAt(point, "DoubleTapped"))
            e.Handled = true;
    }

    /// <summary>Shared edit entry point for both the manual tap heuristic and DoubleTapped.</summary>
    private bool TryEditAiCardAt(Windows.Foundation.Point point, string source)
    {
        var asset = HitTestImage((float)point.X, (float)point.Y);
        if (asset is null)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 卡片双击（{source}）：坐标 ({point.X:0.0},{point.Y:0.0}) 未命中任何资产",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return false;
        }

        if (!string.Equals(asset.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 卡片双击（{source}）：命中资产 {asset.Name}，坐标 ({point.X:0.0},{point.Y:0.0})，ContentType={asset.ContentType} 非 image/png，忽略",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return false;
        }

        var metadata = AiPasteMetadata.TryParse(asset.Metadata);
        if (metadata is null)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 卡片双击（{source}）：命中资产 {asset.Name}，坐标 ({point.X:0.0},{point.Y:0.0})，Metadata 为空或无法解析（长度 {asset.Metadata?.Length ?? 0}），忽略",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return false;
        }

        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 卡片双击（{source}）：命中资产 {asset.Name}，坐标 ({point.X:0.0},{point.Y:0.0})，Kind={metadata.Kind}，进入编辑",
            InkCanvas.Core.Diagnostics.LogType.Event);
        _ = EditAiCardAsync(asset, metadata);
        return true;
    }

    private BoardImageAsset HitTestImage(float x, float y)
        => BoardImageHitTest.HitTest(_board.CurrentImages, x, y);

    private async Task EditAiCardAsync(BoardImageAsset asset, AiPasteMetadata metadata)
    {
        if (_editingAiCard)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                "[AI] AI 卡片编辑：已有编辑对话框，忽略重复请求",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return;
        }

        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] AI 卡片编辑：XamlRoot 为空（Content={Content?.GetType().Name ?? "null"}），无法打开对话框",
                InkCanvas.Core.Diagnostics.LogType.Error);
            return;
        }

        _editingAiCard = true;
        try
        {
            bool updated = await AiCardEditor.EditAsync(_board, asset, metadata, root.XamlRoot);
            if (!updated)
            {
                InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] AI 卡片编辑：{asset.Name} 未更新（取消或渲染失败）",
                    InkCanvas.Core.Diagnostics.LogType.Trace);
            }
        }
        finally
        {
            _editingAiCard = false;
        }
    }

    /// <summary>原生 AI 文本/公式双击：视图坐标反变换到世界坐标后命中。</summary>
    private void OnNativeObjectDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_board?.Current is null) return;
        var point = e.GetPosition(_surfaceHost);
        if (TryEditAiObjectAtView(new Vector2((float)point.X, (float)point.Y), "DoubleTapped"))
            e.Handled = true;
    }

    /// <summary>
    /// Shared native-object edit entry point. Logs both hit and miss (world point, object count,
    /// nearest bounds distance). Uses the same transformed Bounds as the committed canvas renderer.
    /// </summary>
    private bool TryEditAiObjectAtView(Vector2 viewPoint, string source)
    {
        var controller = _board?.Current;
        if (controller is null) return false;

        ISelectionElement hit = BoardAiObjectHitTest.HitTestView(
            _board.CurrentTextObjects, _board.CurrentFormulaObjects,
            viewPoint.X, viewPoint.Y, controller.ViewTransform,
            out Vector2 world, out int objectCount, out float nearestDistance);
        string distance = float.IsPositiveInfinity(nearestDistance) ? "∞" : nearestDistance.ToString("0.##");

        if (hit is null)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 原生对象双击（{source}）：view=({viewPoint.X:0.0},{viewPoint.Y:0.0}) "
                + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 未命中，nearestDistance={distance}",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return false;
        }

        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 原生对象双击（{source}）：view=({viewPoint.X:0.0},{viewPoint.Y:0.0}) "
            + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 命中={hit.GetType().Name} "
            + $"id={hit.Id} nearestDistance={distance}，进入编辑",
            InkCanvas.Core.Diagnostics.LogType.Event);
        _ = EditAiObjectAsync(hit);
        return true;
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
                _drawingLayers.Invalidate();
                _paper.Invalidate();
            }
        }
        finally
        {
            _editingAiCard = false;
        }
    }

    /// <summary>取消当前选中 AI 对象的共享分组；组内对象随后可独立移动/缩放。</summary>
    private void UngroupSelectedAiGroup()
    {
        string group = _board.Current.SelectedElements
            .OfType<IGroupedSelectionElement>()
            .Select(element => element.SelectionGroupId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(group))
        {
            _ = ShowMessageAsync("当前选择没有 AI 分组。");
            return;
        }

        if (_board.UngroupAiObjects(group) > 0)
        {
            _board.Current.ClearSelection();
            _board.Current.NotifyElementsChanged();
            _drawingLayers.Invalidate();
            _paper.Invalidate();
        }
    }

    // ---- navigation -----------------------------------------------------------

    private void NavigateNext()
    {
        AutoSaveCurrentPage("board-next");
        _board.NextPage();
    }

    private void NavigatePrevious() => _board.PreviousPage();

    private void InsertPage()
    {
        AutoSaveCurrentPage("board-add");
        _board.InsertPageAfterCurrent();
    }

    private void DeleteCurrentPage() => _ = ConfirmDeleteCurrentPageAsync();

    private async Task ConfirmDeleteCurrentPageAsync()
    {
        var target = CurrentController;
        if (!await ConfirmAsync("删除当前页", "删除当前页及其墨迹、图片和视频？此操作无法从其他页恢复。")) return;
        if (_closed) return;
        int index = _board.Pages.ToList().IndexOf(target);
        if (index < 0) return;
        var automation = _services.Settings.Automation;
        if (automation.IsAutoSaveStrokesAtClear && target.Document.Count > automation.MinimumAutomationStrokeNumber)
            _ = _services.AutoSave.SaveSnapshotAsync(target.Document.Strokes, "board-delete");
        _board.RemovePage(index);
        UpdatePageNavigation();
    }

    private void ClearCurrentPage()
    {
        AutoSaveCurrentPage("board-clear");
        // WPF BoardSymbolIconDelete_Click switches back to the pen before clearing (MW_BoardIcons.cs:106-110).
        SetTool(InkTool.Pen);
        _board.ClearCurrentPage();
    }

    /// <summary>WPF auto-saves the outgoing page on next/add/clear/exit when the setting is on.</summary>
    private void AutoSaveCurrentPage(string reason)
    {
        var automation = _services.Settings.Automation;
        if (!automation.IsAutoSaveStrokesAtClear) return;
        if (_board.Current.Document.Count <= automation.MinimumAutomationStrokeNumber) return;
        if (reason == "board-clear" && _shell is not null) _ = _shell.ScreenshotAsync();
        _ = _services.AutoSave.SaveSnapshotAsync(_board.Current.Document.Strokes, reason);
    }

    private void UpdatePageNavigation()
    {
        string index = $"{_board.CurrentIndex + 1} / {_board.PageCount}";
        _pageTextL.Text = index;
        _pageTextR.Text = index;
        // WPF UpdateIndexInfoDisplay: Next becomes 加页 on the last page, in both strips.
        bool atLast = _board.CurrentIndex == _board.PageCount - 1;
        string nextGlyph = atLast ? GlyphAddPage : GlyphNext;
        string nextLabel = atLast ? "加页" : "下一页";
        _nextL.Icon.Glyph = nextGlyph;
        _nextL.Label.Text = nextLabel;
        _nextR.Icon.Glyph = nextGlyph;
        _nextR.Label.Text = nextLabel;
        ToolTipService.SetToolTip(_nextL.Button, nextLabel);
        ToolTipService.SetToolTip(_nextR.Button, nextLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_nextL.Button, nextLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_nextR.Button, nextLabel);
        bool canNext = !atLast || _board.CanAddPages();
        SetCellEnabled(_prevL, _board.CurrentIndex > 0);
        SetCellEnabled(_prevR, _board.CurrentIndex > 0);
        SetCellEnabled(_nextL, canNext);
        SetCellEnabled(_nextR, canNext);
        SetCellEnabled(_addR, _board.CanAddPages());
    }

    internal void SetPagePaneOpen(bool open)
    {
        if (_closed) return;
        DismissBoardFlyout();
        _pagePaneOpen = open;
        _pagePane.IsHitTestVisible = open;
        int generation = ++_pagePaneAnimation;
        if (open) _pageOverlay.Visibility = Visibility.Visible;
        PageNavigator.SetOpen(open);
        UpdatePagePaneLayout();
        PlaceSelectionPanel();
        _ = AnimatePagePaneAsync(open, generation);
    }

    private void UpdatePagePaneLayout()
    {
        if (_pagePane is null) return;
        bool overview = PageNavigator.IsOverview;
        _pagePane.Width = overview ? double.NaN : Math.Min(BoardPageNavigator.SidebarWidth, Math.Max(120, _contentRoot.ActualWidth - 16));
        _pagePane.Height = overview ? double.NaN : Math.Max(120, _contentRoot.ActualHeight - Math.Max(60, _dock.ActualHeight) - 28);
        _pagePane.MaxHeight = double.PositiveInfinity;
        _pagePane.HorizontalAlignment = overview ? HorizontalAlignment.Stretch : _pagePaneRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _pagePane.VerticalAlignment = overview ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        _pagePane.Margin = overview ? new Thickness(0) : new Thickness(8, 8, 8, Math.Max(60, _dock.ActualHeight) + 12);
        _pagePane.CornerRadius = new CornerRadius(overview ? 0 : 12);
        _pagePane.BorderBrush = PopupBrushes.ToolPanelBorder(_boardTheme);
        UpdatePageMaterialClip();
    }

    private void UpdatePageMaterialClip()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_surfaceHost);
        if (_pageOverlay.Visibility != Visibility.Visible)
        {
            visual.Clip = null;
            _pageClip?.Dispose(); _pageClip = null;
            _pageClipGeometry?.Dispose(); _pageClipGeometry = null;
            _pageClipSource?.Dispose(); _pageClipSource = null;
            _dock.Visibility = Visibility.Visible;
            return;
        }
        bool overview = PageNavigator.IsOverview;
        _dock.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        float width = (float)_contentRoot.ActualWidth, height = (float)_contentRoot.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Expose the window's native Mica only beneath the page surface.
        // Ink stays opaque everywhere else, including on Windows' solid-material fallback.
        using var board = CanvasGeometry.CreateRectangle(CanvasDevice.GetSharedDevice(), 0, 0, width, height);
        float paneWidth = overview ? width : (float)_pagePane.Width;
        float paneHeight = overview ? height : (float)_pagePane.Height;
        float x = overview ? 0 : _pagePaneRight ? width - paneWidth - 8 : 8;
        float y = overview ? 0 : height - (float)_pagePane.Margin.Bottom - paneHeight;
        using var panel = CanvasGeometry.CreateRoundedRectangle(CanvasDevice.GetSharedDevice(), x, y, paneWidth, paneHeight,
            overview ? 0 : 12, overview ? 0 : 12);
        var remaining = board.CombineWith(panel, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        var path = new Microsoft.UI.Composition.CompositionPath(remaining);
        _pageClipGeometry ??= visual.Compositor.CreatePathGeometry();
        _pageClipGeometry.Path = path;
        _pageClip ??= visual.Compositor.CreateGeometricClip(_pageClipGeometry);
        visual.Clip = _pageClip;
        _pageClipSource?.Dispose();
        _pageClipSource = remaining;
    }

    private async Task AnimatePagePaneAsync(bool open, int generation)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_pagePane, true);
        var visual = ElementCompositionPreview.GetElementVisual(_pagePane);
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        if (Motion.AnimationsEnabled)
        {
            float distance = PageNavigator.IsOverview ? 0 : (float)(_pagePane.Width + 16) * (_pagePaneRight ? 1 : -1);
            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0, new Vector3(open ? distance : 0, 0, 0));
            slide.InsertKeyFrame(1, new Vector3(open ? 0 : distance, 0, 0),
                visual.Compositor.CreateCubicBezierEasingFunction(new(.16f, 1), new(.3f, 1)));
            slide.Duration = Motion.Normal;
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, open ? 0 : 1);
            fade.InsertKeyFrame(1, open ? 1 : 0);
            fade.Duration = Motion.Fast;
            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
            await Task.Delay(Motion.Normal);
        }
        if (_closed || generation != _pagePaneAnimation) return;
        visual.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Opacity = 1;
        if (!open)
        {
            _pageOverlay.Visibility = Visibility.Collapsed;
            PageNavigator.SetOverview(false);
            UpdatePageMaterialClip();
        }
    }

    private void NavigateToPage(int index)
    {
        if (index < 0 || index >= _board.PageCount || index == _board.CurrentIndex || CurrentController.HasActiveInteraction) return;
        AutoSaveCurrentPage("board-page-navigate");
        _board.GoTo(index);
    }

    private void DuplicatePage()
    {
        if (CurrentController.HasActiveInteraction) return;
        AutoSaveCurrentPage("board-page-duplicate");
        _board.DuplicateCurrentPage();
    }

    private async Task RenameCurrentPageAsync()
    {
        if (_contentRoot.XamlRoot is null) return;
        var page = _board.Current;
        var input = new TextBox { Text = _board.PageName(_board.CurrentIndex), PlaceholderText = "页面名称" };
        var dialog = new ContentDialog
        {
            Title = "重命名页面", Content = input, PrimaryButtonText = "保存", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = _contentRoot.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        int index = _board.Pages.ToList().IndexOf(page);
        _board.RenamePage(index, input.Text);
    }

    private void UpdateUndoRedo()
    {
        var history = _board.Current.History;
        SetCellEnabled(_undoCell, history.CanUndo);
        SetCellEnabled(_redoCell, history.CanRedo);
    }

    private void OnHistoryChanged() => UpdateUndoRedo();

    // ---- page binding ---------------------------------------------------------

    private void BindPage()
    {
        _imageImportCancellation?.Cancel();
        _mediaImportCancellation?.Cancel();
        _boardPenPalette = null;
        foreach (var window in _toolPanels.Values.ToArray()) window.Close();
        DismissBoardFlyout();
        _boundController?.CancelLassoErase();
        FinishSelectionPointers();
        if (_boundController is not null)
        {
            _boundController.SelectionChanged -= OnStrokeSelectionChanged;
            _boundController.History.Changed -= OnHistoryChanged;
            _boundController.Invalidated -= OnControllerInvalidated;
            _boundController.PlaybackChanged -= OnPlaybackChanged;
            _boundController.ToolChanged -= OnToolChanged;
            _boundController.Document.Changed -= OnDrawingOrderChanged;
            _boundController.LayersChanged -= OnDrawingOrderChanged;
        }
        _boundController = _board.Current;
        _boundController.SelectedElements.Clear();
        _boundController.SelectionChanged += OnStrokeSelectionChanged;
        _boundController.History.Changed += OnHistoryChanged;
        _boundController.Invalidated += OnControllerInvalidated;
        _boundController.PlaybackChanged += OnPlaybackChanged;
        _boundController.ToolChanged += OnToolChanged;
        _boundController.Document.Changed += OnDrawingOrderChanged;
        _boundController.LayersChanged += OnDrawingOrderChanged;
        _drawingLayers.UpdateOrder(_boundController);
        if (!ReferenceEquals(_surface.Controller, _boundController))
            _surface.Controller = _boundController;
        _images.SetPage(_board);
        _images.UpdateViewTransform(_boundController.ViewTransform);
        _media.SetPage(_board);
        _media.UpdateViewTransform(_boundController.ViewTransform);
        _paper.RenderTransform = new MatrixTransform { Matrix = ToUiMatrix(_boundController.ViewTransform) };
        _lastLayerTransform = _boundController.ViewTransform;
        _viewScale = ExtractScale(_boundController.ViewTransform);
        // ApplySettings (called on page change) uses a different radius formula; restore WPF's.
        ApplyEraserSize();
        SetTool(_boundController.Tool);
        UpdatePageNavigation();
        UpdateSelectionActions();
        UpdateUndoRedo();
        _paper.Invalidate();
    }

    /// <summary>
    /// Two-finger gestures mutate controller.ViewTransform live; keep the element layers and paper
    /// in sync on every invalidation (cheap matrix compare), and re-place the selection panel while
    /// a selection is being dragged. Without this, ink zooms but inserted images stay put.
    /// </summary>
    private void OnControllerInvalidated()
    {
        _drawingLayers.Invalidate();
        var t = _board.Current.ViewTransform;
        if (t != _lastLayerTransform)
        {
            _lastLayerTransform = t;
            _images.UpdateViewTransform(t);
            _media.UpdateViewTransform(t);
            if (_paper.RenderTransform is not MatrixTransform matrix) _paper.RenderTransform = matrix = new MatrixTransform();
            matrix.Matrix = ToUiMatrix(t);
            _viewScale = ExtractScale(t);
            PageNavigator.InvalidateCurrent();
        }
        if (_board.Current.Playback is not null || _board.Current.IsMarqueeSelecting || _board.Current.IsRotatingSelection)
            _selectionPanel.Visibility = Visibility.Collapsed;
        else if (_selectionPanel.Visibility == Visibility.Visible) PlaceSelectionPanel();
    }

    private static Microsoft.UI.Xaml.Media.Matrix ToUiMatrix(Matrix3x2 m)
        => new(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

    private static float ExtractScale(Matrix3x2 transform)
        => Math.Clamp(MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12), 0.5f, 2f);

    private void OnPageChanged()
    {
        if (ReferenceEquals(_boundController, _board.Current))
        {
            UpdatePageNavigation();
            return;
        }
        _services.ConfigureController(_board.Current);
        BindPage();
        RefreshBackground();
        CurrentPageChanged?.Invoke();
    }

    private void OnStrokeSelectionChanged()
    {
        _images.SetSelection(_board.Current.SelectedElements.OfType<BoardSelectionElement>().Where(e => e.Image is not null).Select(e => e.Image));
        _media.SetSelection(_board.Current.SelectedElements.OfType<BoardSelectionElement>().Where(e => e.Media is not null).Select(e => e.Media));
        UpdateSelectionActions();
    }

    private void OnPlaybackChanged() { DismissBoardFlyout(); UpdateSelectionActions(); }

    private void OnImageSelectionChanged() => UpdateSelectionActions();

    private void OnMediaSelectionChanged() => UpdateSelectionActions();

    private void UpdateSelectionActions()
    {
        bool any = _board.Current.HasSelection;
        _selectionPanel.Visibility = any && _board.Current.Playback is null && !_board.Current.IsMarqueeSelecting && !_board.Current.IsRotatingSelection
            ? Visibility.Visible : Visibility.Collapsed;
        SyncCloneButton();
        if (any) PlaceSelectionPanel();
    }

    private void OnImagesChanged()
    {
        _drawingLayers.UpdateOrder(CurrentController);
        _images.Refresh();
        _images.UpdateViewTransform(_board.Current.ViewTransform);
        _media.Refresh();
        _media.UpdateViewTransform(_board.Current.ViewTransform);
        UpdateSelectionActions();
    }

    private void OnDrawingOrderChanged()
    {
        _drawingLayers.UpdateOrder(CurrentController);
        _media.Refresh();
    }

    // ---- Select-mode input routing ---------------------------------------------

    private void OnSurfaceHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_board.Current.Tool != InkTool.Select || BoardMediaLayer.IsPlaybackControl(e.OriginalSource as DependencyObject)) return;
        OnCanvasInputBegan();
        var p = ToOverlayPointer(e);
        _surfaceHost.CapturePointer(e.Pointer);
        _selectionPointers[p.Id] = p;
        _board.Current.OnPointerDown(p);
        e.Handled = true;
        TrackNativeDoubleTap(p);
    }

    private void OnSurfaceHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_board.Current.Tool != InkTool.Select || !_selectionPointers.ContainsKey(e.Pointer.PointerId)) return;
        if (_pendingAiNativeEdit)
        {
            var point = e.GetCurrentPoint(_surfaceHost).Position;
            if (Math.Abs(point.X - _pendingAiNativeEditPoint.X) > 6
                || Math.Abs(point.Y - _pendingAiNativeEditPoint.Y) > 6)
            {
                _pendingAiNativeEdit = false;
                InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                    "[AI] 原生对象双击候选已取消：指针移动，按选择拖动处理",
                    InkCanvas.Core.Diagnostics.LogType.Trace);
            }
        }
        var p = ToOverlayPointer(e);
        _selectionPointers[p.Id] = p;
        _board.Current.OnPointerMove(p);
        e.Handled = true;
    }

    private void OnSurfaceHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_board.Current.Tool != InkTool.Select)
        {
            _pendingAiNativeEdit = false;
            return;
        }
        var viewPoint = e.GetCurrentPoint(_surfaceHost).Position;
        bool pending = _pendingAiNativeEdit;
        _pendingAiNativeEdit = false;
        if (pending)
        {
            if (Math.Abs(viewPoint.X - _pendingAiNativeEditPoint.X) > 6
                || Math.Abs(viewPoint.Y - _pendingAiNativeEditPoint.Y) > 6)
            {
                InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                    "[AI] 原生对象双击候选已取消：释放位置偏离",
                    InkCanvas.Core.Diagnostics.LogType.Trace);
            }
            else
            {
                var editPoint = _pendingAiNativeEditPoint;
                InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                    $"[AI] 原生对象双击确认：view=({editPoint.X:0.0},{editPoint.Y:0.0})，释放后打开编辑",
                    InkCanvas.Core.Diagnostics.LogType.Trace);
                // Complete this click/selection first, then open the editor on the next dispatcher turn.
                DispatcherQueue.TryEnqueue(() =>
                    TryEditAiObjectAtView(
                        new Vector2((float)editPoint.X, (float)editPoint.Y), "双击释放"));
            }
        }
        var p = ToOverlayPointer(e);
        if (_selectionPointers.Remove(p.Id))
        {
            _board.Current.OnPointerUp(p);
            _surfaceHost.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnSurfaceHostPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pendingAiNativeEdit = false;
        if (_board.Current.Tool != InkTool.Select) return;
        if (_selectionPointers.Remove(e.Pointer.PointerId, out var last))
            _board.Current.OnPointerUp(last);
    }

    /// <summary>
    /// Manual fallback for native-object double tap: observes presses only (never suppresses the
    /// selection pointer stream). A second press within 450 ms / 6 DIP becomes a candidate that is
    /// confirmed on release without movement, so drag transforms keep working.
    /// </summary>
    private void TrackNativeDoubleTap(OverlayPointer p)
    {
        var point = new Windows.Foundation.Point(p.X, p.Y);
        long now = Environment.TickCount64;
        bool isDoubleTap = now - _lastNativeTapTicks <= 450
            && Math.Abs(point.X - _lastNativeTapPoint.X) <= 6
            && Math.Abs(point.Y - _lastNativeTapPoint.Y) <= 6;
        _lastNativeTapTicks = isDoubleTap ? 0 : now;
        _lastNativeTapPoint = point;
        _pendingAiNativeEdit = false;
        if (!isDoubleTap) return;

        var controller = _board?.Current;
        if (controller is null) return;
        ISelectionElement hit = BoardAiObjectHitTest.HitTestView(
            _board.CurrentTextObjects, _board.CurrentFormulaObjects,
            p.X, p.Y, controller.ViewTransform,
            out Vector2 world, out int objectCount, out float nearestDistance);
        string distance = float.IsPositiveInfinity(nearestDistance) ? "∞" : nearestDistance.ToString("0.##");
        if (hit is null)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 原生对象双击候选（手动）：view=({p.X:0.0},{p.Y:0.0}) "
                + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 未命中，nearestDistance={distance}",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            return;
        }

        _pendingAiNativeEdit = true;
        _pendingAiNativeEditPoint = point;
        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"[AI] 原生对象双击候选（手动）：view=({p.X:0.0},{p.Y:0.0}) "
            + $"world=({world.X:0.0},{world.Y:0.0}) objects={objectCount} 命中={hit.GetType().Name} "
            + $"id={hit.Id} nearestDistance={distance}，等待释放确认（不拦截拖动）",
            InkCanvas.Core.Diagnostics.LogType.Trace);
    }
    private static List<string> ContentLines(AiPasteMetadata metadata)
    {
        if (metadata.Kind == AiPasteKind.Formula)
            return new List<string> { metadata.Latex };
        if (metadata.Lines.Count > 0) return metadata.Lines.ToList();
        return SplitCardLines(metadata.Text);
    }

    private static List<string> SplitCardLines(string text)
        => (text ?? "").Replace("\r", "").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    private void OnSurfaceHostPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_board.Current.Tool != InkTool.Select) return;
        bool any = _board.Current.Selection.Count > 0
            || _images.SelectedImage is not null || _media.SelectedMedia is not null;
        if (!any) return;
        int delta = e.GetCurrentPoint(_surfaceHost).Properties.MouseWheelDelta;
        if (delta == 0) return;
        float scale = delta > 0 ? 1.1f : 0.9f;
        TransformSelectionMatrix(Matrix3x2.CreateScale(scale), "zoom selection", scaleWidth: true);
        e.Handled = true;
    }

    private void FinishSelectionPointers()
    {
        var samples = _selectionPointers.Values.ToArray();
        _selectionPointers.Clear();
        if (_boundController is not null)
            foreach (var sample in samples) _boundController.OnPointerUp(sample);
        _surfaceHost.ReleasePointerCaptures();
    }
    private OverlayPointer ToOverlayPointer(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_surfaceHost);
        var props = point.Properties;
        var kind = point.PointerDeviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Pen => OverlayPointerKind.Pen,
            Microsoft.UI.Input.PointerDeviceType.Touch => OverlayPointerKind.Touch,
            _ => OverlayPointerKind.Mouse,
        };
        var contact = props.ContactRect;
        return new OverlayPointer(
            e.Pointer.PointerId,
            (float)point.Position.X,
            (float)point.Position.Y,
            props.Pressure <= 0 ? 0.5f : props.Pressure,
            (float)contact.Width,
            (float)contact.Height,
            kind,
            (uint)point.Timestamp / 1000,
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0,
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0);
    }

    // ---- window mechanics ---------------------------------------------------------

    private void OnActivated()
    {
        if (_closed) return;
        // The shell applies the app theme after construction; the board always re-asserts
        // its own black/white theme, and picks up settings changes on focus return.
        WindowChrome.ApplyTheme(this, _boardTheme);
        // The HWND may not exist during construction; re-assert feedback suppression once active.
        WindowChrome.SuppressTouchFeedback(this);
        ApplyBoardScale();
        if (!_entrancePlayed)
        {
            _entrancePlayed = true;
            Motion.PlayEntrance(_leftHost, 20f);
            Motion.PlayEntrance(_centerHost, 20f);
            Motion.PlayEntrance(_rightHost, 20f);
        }
    }

    private void SetFullscreen(bool fullscreen)
    {
        // 窗口已在关闭流程中时，Window.set_Content 会抛 ArgumentException（旧 error.log 记录）。
        if (_closed || fullscreen == _isFullscreen) return;
        try
        {
            if (fullscreen)
            {
                _windowedContent.Children.Remove(_contentRoot);
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                Content = _contentRoot;
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                WindowChrome.MakeBorderless(this, topmost: true, resizable: true);
                WindowChrome.SuppressTouchFeedback(this);
                Content = null;
                Grid.SetRow(_contentRoot, 1);
                if (!_windowedContent.Children.Contains(_contentRoot))
                    _windowedContent.Children.Add(_contentRoot);
                Content = _windowedContent;
                var work = WindowChrome.GetWorkArea(this);
                WindowChrome.Place(this, work.X, work.Y, work.Width, work.Height);
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[BoardWindow] 切换全屏失败：fullscreen={fullscreen}，{ex.GetType().Name}: {ex.Message}",
                Core.Diagnostics.LogType.Trace);
            // Content 可能已置空；窗口仍存活时把手风琴内容挂回，避免留下黑屏空壳。
            if (!_closed && Content is null)
            {
                try
                {
                    Grid.SetRow(_contentRoot, 1);
                    if (!_windowedContent.Children.Contains(_contentRoot))
                        _windowedContent.Children.Add(_contentRoot);
                    Content = _windowedContent;
                }
                catch (Exception restore)
                {
                    Core.Diagnostics.FileLog.WriteLogToFile(
                        $"[BoardWindow] 全屏失败后恢复内容失败：{restore.GetType().Name}: {restore.Message}",
                        Core.Diagnostics.LogType.Trace);
                }
            }
            return;
        }
        _isFullscreen = fullscreen;
        SystemBackdrop ??= new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        WindowChrome.ApplyTheme(this, _boardTheme);
        UpdatePageNavigation();
    }

    private void ExitBoard()
    {
        if (_shell is not null) _shell.ToggleBoard();
        else Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        foreach (var window in _toolPanels.Values.ToArray()) window.Close();
        CurrentController.EndPlayback();
        _closed = true;
        _pageOverlay.Visibility = Visibility.Collapsed;
        UpdatePageMaterialClip();
        SystemBackdrop = null;
        PageNavigator.Dispose();
        _imageImportCancellation?.Cancel();
        _mediaImportCancellation?.Cancel();
        DismissBoardFlyout(); _surface.InputBegan -= OnCanvasInputBegan;
        _boundController?.CancelLassoErase();
        FinishSelectionPointers();
        AutoSaveCurrentPage("board-exit");
        _board.PageChanged -= OnPageChanged;
        _board.BackgroundChanged -= RefreshBackground;
        _board.ImagesChanged -= OnImagesChanged;
        _board.ImagePlacementChanged -= _images.UpdateElement;
        _board.MediaPlacementChanged -= _media.UpdateElement;
        _images.SelectionChanged -= OnImageSelectionChanged;
        _media.SelectionChanged -= OnMediaSelectionChanged;
        _media.Dispose();
        _drawingLayers.Dispose();
        _surface.Dispose();
        _paper.Draw -= OnDrawPaper;
        _paper.RemoveFromVisualTree();
        if (_boundController is not null)
        {
            _boundController.SelectionChanged -= OnStrokeSelectionChanged;
            _boundController.History.Changed -= OnHistoryChanged;
            _boundController.Invalidated -= OnControllerInvalidated;
            _boundController.PlaybackChanged -= OnPlaybackChanged;
            _boundController.ToolChanged -= OnToolChanged;
            _boundController.Document.Changed -= OnDrawingOrderChanged;
            _boundController.LayersChanged -= OnDrawingOrderChanged;
        }
    }

    // ---- insert / save / open -------------------------------------------------------

    public async Task InsertImageAsync()
    {
        if (_imagePicking || _closed) return;
        _imagePicking = true;
        var cancellation = new CancellationTokenSource();
        _imageImportCancellation = cancellation;
        var page = CurrentController;
        bool IsCurrent() => !_closed && ReferenceEquals(page, CurrentController);
        try
        {
            var viewport = new Vector2(
                _surface.ActualWidth > 0 ? (float)_surface.ActualWidth : WindowChrome.GetWorkArea(this).Width / (float)WindowChrome.GetDpiScale(this),
                _surface.ActualHeight > 0 ? (float)_surface.ActualHeight : WindowChrome.GetWorkArea(this).Height / (float)WindowChrome.GetDpiScale(this));
            var asset = await ImageImport.PickAsync(this, _services.Settings.Canvas.IsCompressPicturesUploaded,
                viewport, page.ViewTransform, cancellation.Token);
            if (asset is not null && await ImageImport.WaitForCanvasAsync(page, IsCurrent, cancellation.Token))
            {
                ImageImport.PlaceInView(asset, new((float)_surface.ActualWidth, (float)_surface.ActualHeight), page.ViewTransform);
                InsertImage(asset);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) await ShowMessageAsync($"图片插入失败：{ex.Message}");
        }
        finally
        {
            _imagePicking = false;
            if (ReferenceEquals(_imageImportCancellation, cancellation)) _imageImportCancellation = null;
            cancellation.Dispose();
        }
    }

    internal bool InsertImage(BoardImageAsset asset)
    {
        if (!_board.AddImage(asset)) return false;
        CurrentController.SelectElement(_board.GetSelectionElements().Single(element => element.Id == asset.Id));
        return true;
    }

    private bool _mediaPicking;
    private CancellationTokenSource _mediaImportCancellation;

    internal async Task InsertMediaAsync()
    {
        if (_mediaPicking) return;
        _mediaPicking = true;
        using var cancellation = new CancellationTokenSource();
        _mediaImportCancellation = cancellation;
        var page = CurrentController;
        try
        {
            var viewport = new Vector2((float)_surface.ActualWidth, (float)_surface.ActualHeight);
            var asset = await MediaImport.PickAsync(this, viewport, page.ViewTransform, cancellation.Token);
            if (asset is not null && await ImageImport.WaitForCanvasAsync(page, () => !_closed && ReferenceEquals(page, CurrentController), cancellation.Token)) InsertMedia(asset);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) await ShowMessageAsync($"视频插入失败：{ex.Message}"); }
        finally { _mediaPicking = false; if (ReferenceEquals(_mediaImportCancellation, cancellation)) _mediaImportCancellation = null; }
    }

    internal void InsertMedia(BoardMediaAsset asset)
    {
        if (!_board.AddMedia(asset)) return;
        CurrentController.SelectElement(_board.GetSelectionElements().Single(element => element.Id == asset.Id));
    }

    private async Task SavePageAsync()
    {
        try
        {
            string name = ArchivePolicy.FileName(_services.Settings.Archive, DateTimeOffset.Now, 1, "当前页");
            var library = _services.Modules.GetExport<InkCanvas.Host.Contracts.IBoardLibraryService>();
            string path = library is not null ? await library.PickSaveAsync(name) : await ImageFilePicker.SaveArchiveAsync(WindowChrome.GetWorkArea(this),
                ArchivePolicy.ResolveDirectory(_services.Settings.Archive.DefaultDirectory), name);
            if (path is null || _closed) return;
            await _services.Archives.SaveManualAsync(_board, path, entireBoard: false);
        }
        catch (Exception ex) { await ShowMessageAsync($"页面保存失败：{ex.Message}"); }
    }

    private async Task SaveBoardAsync()
    {
        try
        {
            string name = ArchivePolicy.FileName(_services.Settings.Archive, DateTimeOffset.Now, _board.PageCount, "整板");
            var library = _services.Modules.GetExport<InkCanvas.Host.Contracts.IBoardLibraryService>();
            string path = library is not null ? await library.PickSaveAsync(name) : await ImageFilePicker.SaveArchiveAsync(WindowChrome.GetWorkArea(this),
                ArchivePolicy.ResolveDirectory(_services.Settings.Archive.DefaultDirectory), name);
            if (path is null || _closed) return;
            await _services.Archives.SaveManualAsync(_board, path, entireBoard: true);
        }
        catch (Exception ex) { await ShowMessageAsync($"画板保存失败：{ex.Message}"); }
    }

    private Task OpenPageAsync() => OpenInkAsync(false);

    private Task OpenBoardAsync() => OpenInkAsync(true);

    private Task OpenFileAsync()
    {
        if (_services.Modules.GetExport<InkCanvas.Host.Contracts.IBoardLibraryService>() is { } library)
        {
            library.Open();
            return Task.CompletedTask;
        }
        return OpenPageAsync();
    }

    private async Task OpenInkAsync(bool entireBoard)
    {
        var page = CurrentController;
        try
        {
            var file = await PickInkFileAsync();
            if (file is null || _closed) return;
            var apply = await _services.PrepareInkOpenAsync(file.Path, _board, entireBoard);
            if (_closed || page != CurrentController) return;
            bool hasContent = entireBoard ? _board.HasContent
                : page.Document.Count > 0 || _board.CurrentImages.Count > 0 || _board.CurrentMedia.Count > 0;
            if (hasContent && !await ConfirmAsync(entireBoard ? "替换整个画板" : "替换当前页",
                entireBoard ? "打开文件会替换所有画板页面。继续吗？" : "打开文件会替换当前页上的墨迹、图片和视频。继续吗？")) return;
            if (!_closed && page == CurrentController) apply();
        }
        catch (Exception ex) { if (!_closed) await ShowMessageAsync($"文件打开失败：{ex.Message}"); }
    }

    private FileSavePicker CreateSavePicker(string typeName, string extension, string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(this));
        return picker;
    }

    private async Task<StorageFile> PickInkFileAsync()
    {
        var extensions = new[] { ".icink" }.Concat(_services.Plugins.ImportExtensions).Distinct().ToArray();
        var library = _services.Modules.GetExport<InkCanvas.Host.Contracts.IBoardLibraryService>();
        string path = library is not null ? await library.PickOpenAsync(extensions) : await ImageFilePicker.OpenArchiveAsync(WindowChrome.GetWorkArea(this),
            ArchivePolicy.ResolveDirectory(_services.Settings.Archive.DefaultDirectory), extensions);
        return path is null ? null : await StorageFile.GetFileFromPathAsync(path);
    }

    private async Task ShowMessageAsync(string message)
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = root.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public Windows.Graphics.RectInt32 GetToolAnchor(InkTool tool)
        => WindowChrome.GetScreenBounds(this, (tool switch
        {
            InkTool.Highlighter => _penCell,
            InkTool.Eraser => _eraserCell,
            InkTool.Select => _selectCell,
            InkTool.Shape => _shapeCell,
            _ => _penCell,
        }).Button);

    private sealed class ToolCell
    {
        public Button Button { get; init; }
        public Border Face { get; init; }
        public bool Selected, Hovered, Pressed;
        public FontIcon Icon { get; init; }
        public FontIcon AiLabel { get; init; }
        public FontIcon BadgeIcon { get; init; }
        public TextBlock Label { get; init; }
        public Grid Content { get; init; }
    }
}
