using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI.Text;

namespace InkCanvas.App.Views;

/// <summary>
/// AI 助教浮窗：纯 code-behind，无 XAML。正文按 <see cref="AssistantText"/> 的解析结果
/// 渲染 Markdown/公式，底部提供停止、重试、复制、贴回与追问。窗口不持有流，只把点击
/// 转成事件，由 ShellController 统一管理会话与取消。
/// </summary>
public sealed class AssistantWindow : Window
{
    public const double CardWidth = 380;
    public const double MaxHeightRatio = 0.62;

    private const double MaxCardHeight = 460;
    private const double TitleHeight = 40;
    private const double MonoFontSize = 12;
    private const double BodyFontSize = 13.5;
    private const double BodyLineHeight = 21;
    private const double MaxThumbnailHeight = 96;
    private const double MinRegionSize = 6;
    private const double DragThreshold = 4;
    private const int MaxBoardPlanPreviewBlocks = 8;

    private static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(1.6);

    private readonly Grid _titleBar;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _titleText;
    private readonly Button _modeCapsule;
    private readonly TextBlock _modeText;
    private readonly ProgressRing _progressRing;
    private readonly Border _thumbnailHost;
    private readonly Image _thumbnail;
    private readonly ScrollViewer _problemScroll;
    private readonly StackPanel _problemList;
    private readonly Button _addRegionLink;
    private readonly Dictionary<string, BitmapImage> _problemImages = new();
    private readonly List<InMemoryRandomAccessStream> _problemStreams = new();
    private readonly Canvas _selectionOverlay;
    private readonly Rectangle _selectionRect;
    private readonly Canvas _annotationCanvas = new() { IsHitTestVisible = false };
    private readonly Button _variantBasic;
    private readonly Button _variantAdvanced;
    private readonly Button _toggleAnnotations;
    private readonly StackPanel _studyRow;
    private bool _annotationsVisible = true;
    private readonly Border _hintRow;
    private readonly TextBlock _hintText;
    private readonly Border _statusBadge;
    private readonly TextBlock _statusBadgeText;
    private readonly Border _formulaBadge;
    private readonly TextBlock _formulaBadgeText;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _body;
    private readonly StackPanel _content;
    private readonly Border _formulaCard;
    private readonly TextBlock _formulaText;
    private readonly Grid _actions;
    private readonly Button _stop;
    private readonly Button _retry;
    private readonly Button _copy;
    private readonly Button _paste;
    private readonly Button _favorite;
    private readonly Button _revealNext;
    private readonly Button _organizeBoard;
    private readonly Button _aiWrite;
    private readonly Button _boardWriteConfirm;
    private readonly Button _boardWriteCancel;
    private readonly Border _boardPlanPreview;
    private readonly TextBlock _boardPlanTitle;
    private readonly StackPanel _boardPlanList;
    private readonly Button _boardWriteSelected;
    private readonly Button _boardPlanFontUp;
    private readonly Button _boardPlanFontDown;
    private readonly Button _boardPlanTwoColumns;
    private readonly Button _boardPlanKeepFormula;
    private readonly Button _boardPlanMoveTarget;
    private readonly CheckBox _replaceOriginal;
    private readonly StackPanel _boardPlanWarnings;
    private readonly StackPanel _boardPlanOriginalList;
    private readonly StackPanel _boardPlanOriginalPane;
    private readonly StackPanel _boardPlanResultPane;
    private readonly Grid _boardPlanPair;
    private readonly List<(string Id, CheckBox Box)> _boardPlanChecks = new();
    private static readonly Brush ReviewBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0x8A, 0x00));
    private bool _boardPlanWideLayout;
    private AssistantBoardPlan _boardPlan;
    private bool _boardPlanBusy;
    private readonly Button _close;
    private readonly TextBlock _cursor;
    private readonly DispatcherTimer _cursorTimer;
    private readonly TextBox _followUp;
    private readonly Button _send;
    private readonly Border _root;

    private readonly Brush _rootBgBrush;
    private readonly Brush _highlightBrush;
    private readonly Brush _dividerBrush;
    private readonly Brush _textPrimary;
    private readonly Brush _textSecondary;
    private readonly Brush _accentBrush;
    private readonly Brush _accentSoftBrush;
    private readonly Brush _accentSolidBrush;
    private readonly Brush _pillBrush;
    private readonly Brush _codeBgBrush;
    private readonly Brush _controlStrokeBrush;
    private readonly Brush _inputBgBrush;

    private static readonly Brush OkBrush = Solid(255, 16, 124, 16);
    private static readonly Brush ErrorBrush = Solid(255, 232, 17, 35);
    private static readonly Brush WhiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
    private static readonly Brush ThumbnailBgBrush = Solid(0x33, 0, 0, 0);

    private InMemoryRandomAccessStream _thumbStream;
    private string _thumbBase64 = "";
    private ProblemContext _problem;
    private AssistantSession _lastSession;
    private AssistantRect _anchor;
    private Windows.Foundation.Point _thumbStart;
    private bool _thumbTracking;
    private bool _thumbDragging;
    private readonly DispatcherTimer _highlightTimer;
    private readonly DispatcherTimer _noticeTimer;
    private bool _busy;
    private bool _pasted;
    private bool _hasCopyable;
    private readonly FeatureSettings _features;
    private bool _aiTeachingEnabled = true;
    private string _sourcePresentation = "";
    private int _sourceSlideId;
    private int _sourceBoardPage;
    private string _sourceSessionId = "";
    private string _sourceRequestId = "";
    private string _sourceBoardId = "";
    private Guid _sourceBoardPageId;
    private string _sourceGroupId = "";
    private IReadOnlyList<string> _sourceObjectIds = Array.Empty<string>();

    public new event Action Closed;

    public event Action StopRequested;

    public event Action RetryRequested;

    public event Action PasteRequested;

    public event Action CopyRequested;

    public event Action<string> FollowUpRequested;

    /// <summary>请求整理板书或 AI 书写；参数为 (instruction, intent)，intent 为 organize / write。</summary>
    public event Action<string, string> BoardWriteRequested;

    /// <summary>
    /// 阶段 3 确认写入：<c>selectedIds</c> 为勾选的块 id（部分采用），
    /// <c>replaceOriginal</c> 表示删除原笔画后替换。UI 只触发本事件。
    /// </summary>
    public event Action<IReadOnlyList<string>, bool> BoardWriteConfirmedSelection;

    /// <summary>
    /// 阶段 5 本地调整预览（字号/列/过滤/移动/行距）；Shell 只应用
    /// <c>BoardPlanEditPolicy</c>，不调用 AI。
    /// </summary>
    public event Action<string> BoardPlanEditRequested;

    /// <summary>取消当前板书预览；ShellController 保留指令以便重试。</summary>
    public event Action BoardWriteCancelled;

    /// <summary>讲题模式切换请求；由 ShellController 决定是否重发请求。</summary>
    public event Action<AssistantTeachingMode> TeachingModeRequested;

    /// <summary>分步引导下请求展示下一步。</summary>
    public event Action RevealNextRequested;

    /// <summary>请求生成同知识点变式题；参数 true 表示提高难度。</summary>
    public event Action<bool> VariantRequested;

    /// <summary>把画布当前选区追加进拼题上下文。</summary>
    public event Action AddRegionRequested;

    public event Action<string> RemoveRegionRequested;

    /// <summary>请求只重新识别某个选区（不改变其他选区与顺序）。</summary>
    public event Action<string> ReRecognizeRegionRequested;

    public event Action<string, int> MoveRegionRequested;

    /// <summary>缩略图上框选完成；矩形为原选区锚点的屏幕 DIP 坐标。</summary>
    public event Action<AssistantRect> RegionSelected;

    /// <summary>关闭“局部纠错”开关时禁用缩略图圈选交互。</summary>
    public bool RegionSelectionEnabled { get; set; } = true;

    public AssistantWindow(int theme, FeatureSettings features = null)
    {
        Title = "AI 助教";
        _features = features;

        bool light = theme == 1 || (theme != 2 && Application.Current.RequestedTheme == ApplicationTheme.Light);
        if (light)
        {
            _rootBgBrush = Solid(0xF2, 0xFF, 0xFF, 0xFF);
            _highlightBrush = Solid(0xCC, 0xFF, 0xFF, 0xFF);
            _dividerBrush = Solid(0x14, 0x00, 0x00, 0x00);
            _textPrimary = Solid(0xE4, 0x00, 0x00, 0x00);
            _textSecondary = Solid(0x9E, 0x00, 0x00, 0x00);
            _accentBrush = Solid(0xFF, 0x00, 0x67, 0xC0);
            _accentSoftBrush = Solid(0x1A, 0x00, 0x67, 0xC0);
            _codeBgBrush = Solid(0x0F, 0x00, 0x00, 0x00);
            _controlStrokeBrush = Solid(0x22, 0x00, 0x00, 0x00);
            _inputBgBrush = Solid(0x0F, 0x00, 0x00, 0x00);
        }
        else
        {
            _rootBgBrush = Solid(0xE6, 0x20, 0x21, 0x24);
            _highlightBrush = Solid(0x22, 0xFF, 0xFF, 0xFF);
            _dividerBrush = Solid(0x1A, 0xFF, 0xFF, 0xFF);
            _textPrimary = Solid(0xF2, 0xFF, 0xFF, 0xFF);
            _textSecondary = Solid(0xA8, 0xFF, 0xFF, 0xFF);
            _accentBrush = Solid(0xFF, 0x4C, 0xC2, 0xFF);
            _accentSoftBrush = Solid(0x26, 0x4C, 0xC2, 0xFF);
            _codeBgBrush = Solid(0x22, 0xFF, 0xFF, 0xFF);
            _controlStrokeBrush = Solid(0x33, 0xFF, 0xFF, 0xFF);
            _inputBgBrush = Solid(0x14, 0xFF, 0xFF, 0xFF);
        }

        _pillBrush = _accentSoftBrush;
        _accentSolidBrush = GetSystemAccent() ?? _accentBrush;

        // ---- header ------------------------------------------------------
        _statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _titleText = new TextBlock
        {
            Text = "AI 助教",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 标题前缀「AI」徽标独立成控件：WinUI 3 的 TextBlock.Inlines 不接受
        // InlineUIContainer 时会抛 E_INVALIDARG，直接把徽标放进标题行更稳。
        var titleBadge = new Border
        {
            Background = _accentSoftBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = AiGlyph.CreateLabel(AiGlyph.BadgeLabelSize, _accentBrush),
        };
        _modeText = new TextBlock
        {
            Text = "自动",
            FontSize = 11,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _modeCapsule = new Button
        {
            Content = _modeText,
            Background = Solid(0x0F, 0x00, 0x67, 0xC0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 24,
            MinWidth = 0,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(_modeCapsule, "切换讲题模式：只给提示 → 分步引导 → 完整解答 → 教师解析");
        _modeCapsule.Click += (_, _) =>
        {
            if (_lastSession is null) return;
            TeachingModeRequested?.Invoke(AssistantTeachingPolicy.Next(_lastSession.TeachingMode));
        };

        var headerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        headerLeft.Children.Add(_statusDot);
        headerLeft.Children.Add(titleBadge);
        headerLeft.Children.Add(_titleText);
        headerLeft.Children.Add(_modeCapsule);

        var closeButton = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12, Foreground = _textPrimary },
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        closeButton.Click += (_, _) => Close();

        _titleBar = new Grid { Height = TitleHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(closeButton, 2);
        _titleBar.Children.Add(headerLeft);
        _titleBar.Children.Add(closeButton);

        var divider = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = _dividerBrush,
        };
        Grid.SetColumnSpan(divider, 3);
        _titleBar.Children.Add(divider);

        // ---- status row --------------------------------------------------
        _progressRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "就绪",
        };
        _statusBadge = CreateBadge(out _statusBadgeText);
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12, 8, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusRow.Children.Add(_progressRing);
        statusRow.Children.Add(_statusText);
        statusRow.Children.Add(_statusBadge);

        // ---- thumbnail ---------------------------------------------------
        _thumbnail = new Image
        {
            Stretch = Stretch.Fill,
            MaxHeight = MaxThumbnailHeight,
        };
        _selectionRect = new Rectangle
        {
            Stroke = _accentBrush,
            StrokeThickness = 1.5,
            Fill = Solid(0x22, 0x00, 0x67, 0xC0),
            RadiusX = 3,
            RadiusY = 3,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _selectionOverlay = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _selectionOverlay.Children.Add(_selectionRect);
        _selectionOverlay.PointerPressed += OnThumbnailPointerPressed;
        _selectionOverlay.PointerMoved += OnThumbnailPointerMoved;
        _selectionOverlay.PointerReleased += OnThumbnailPointerReleased;
        _selectionOverlay.PointerCanceled += OnThumbnailPointerCanceled;
        _selectionOverlay.PointerCaptureLost += OnThumbnailPointerCanceled;

        var thumbGrid = new Grid();
        thumbGrid.Children.Add(_thumbnail);
        thumbGrid.Children.Add(_annotationCanvas);
        thumbGrid.Children.Add(_selectionOverlay);
        _thumbnailHost = new Border
        {
            Background = ThumbnailBgBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = thumbGrid,
        };
        _thumbnailHost.SizeChanged += (_, _) => ApplyAnnotations(_lastSession);

        _highlightTimer = new DispatcherTimer { Interval = HighlightDuration };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _selectionRect.Visibility = Visibility.Collapsed;
        };

        _problemList = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        _problemScroll = new ScrollViewer
        {
            Content = _problemList,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
            MaxHeight = MaxThumbnailHeight + 12,
        };

        _addRegionLink = CreateSecondaryButton("＋ 追加选区");
        _addRegionLink.Height = 28;
        _addRegionLink.HorizontalAlignment = HorizontalAlignment.Left;
        _addRegionLink.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_addRegionLink, "先回画布用选择工具框选题干/图形/条件，再点此追加为同一题");
        _addRegionLink.Click += (_, _) => AddRegionRequested?.Invoke();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            if (_lastSession is not null) UpdateStatus(_lastSession);
        };

        // ---- local hint ---------------------------------------------------
        _hintText = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            Foreground = _textSecondary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hintRow = new Border
        {
            Background = _inputBgBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Visibility = Visibility.Collapsed,
            Child = _hintText,
        };

        // ---- body --------------------------------------------------------
        _content = new StackPanel { Spacing = 6 };
        _formulaText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = _textPrimary,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var formulaLabel = new TextBlock
        {
            Text = "LaTeX",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _formulaBadge = CreateBadge(out _formulaBadgeText);
        _formulaBadge.HorizontalAlignment = HorizontalAlignment.Right;
        var formulaHeader = new Grid();
        formulaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        formulaHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(formulaLabel, 0);
        Grid.SetColumn(_formulaBadge, 1);
        formulaHeader.Children.Add(formulaLabel);
        formulaHeader.Children.Add(_formulaBadge);

        var formulaStack = new StackPanel { Spacing = 4 };
        formulaStack.Children.Add(formulaHeader);
        formulaStack.Children.Add(_formulaText);
        _formulaCard = new Border
        {
            Background = _accentSoftBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0),
            Child = formulaStack,
            Visibility = Visibility.Collapsed,
        };

        _boardPlanTitle = new TextBlock
        {
            Text = "板书计划",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textPrimary,
            TextWrapping = TextWrapping.Wrap,
        };
        _boardPlanWarnings = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        _boardPlanOriginalList = new StackPanel { Spacing = 3 };
        _boardPlanList = new StackPanel { Spacing = 4 };

        _boardPlanOriginalPane = new StackPanel { Spacing = 3, MinWidth = 0 };
        _boardPlanOriginalPane.Children.Add(BoardPlanSectionLabel("原始内容"));
        _boardPlanOriginalPane.Children.Add(_boardPlanOriginalList);

        _boardPlanResultPane = new StackPanel { Spacing = 3, MinWidth = 0 };
        _boardPlanResultPane.Children.Add(BoardPlanSectionLabel("整理结果"));
        _boardPlanResultPane.Children.Add(_boardPlanList);

        _boardPlanPair = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        _boardPlanPair.Children.Add(_boardPlanOriginalPane);
        _boardPlanPair.Children.Add(_boardPlanResultPane);
        ApplyBoardPlanOrientation(CardWidth);

        _replaceOriginal = new CheckBox
        {
            Content = "替换原板书（删除原笔画）",
            FontSize = 11,
            IsChecked = false,
            Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetName(_replaceOriginal, "替换原板书");
        ToolTipService.SetToolTip(_replaceOriginal,
            "勾选后写入时删除当前页原笔画（有选区时仅删除选区），一次撤销即可恢复");

        _boardWriteConfirm = CreatePrimaryButton("全部采用");
        _boardWriteConfirm.Height = 28;
        _boardWriteConfirm.MinHeight = 28;
        AutomationProperties.SetName(_boardWriteConfirm, "全部采用整理结果");
        _boardWriteConfirm.Click += (_, _) => _ = ConfirmBoardWriteSelectionAsync(all: true);

        _boardWriteSelected = CreateSecondaryButton("仅采用选中");
        _boardWriteSelected.Height = 28;
        _boardWriteSelected.MinHeight = 28;
        AutomationProperties.SetName(_boardWriteSelected, "仅采用选中的整理结果");
        _boardWriteSelected.Click += (_, _) => _ = ConfirmBoardWriteSelectionAsync(all: false);

        _boardWriteCancel = CreateSecondaryButton("取消");
        _boardWriteCancel.Height = 28;
        _boardWriteCancel.MinHeight = 28;
        AutomationProperties.SetName(_boardWriteCancel, "取消板书写入");
        _boardWriteCancel.Click += (_, _) =>
        {
            ClearBoardPlanPreview();
            BoardWriteCancelled?.Invoke();
        };

        Button CreateBoardPlanQuickButton(string label, string instruction, string tooltip)
        {
            Button button = CreateSecondaryButton(label);
            button.Height = 26;
            button.MinHeight = 26;
            button.FontSize = 11;
            button.Padding = new Thickness(8, 0, 8, 0);
            AutomationProperties.SetName(button, tooltip);
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) => BoardPlanEditRequested?.Invoke(instruction);
            return button;
        }

        _boardPlanFontUp = CreateBoardPlanQuickButton("A+", "字号大一点", "字号放大（本地调整，不重新生成）");
        _boardPlanFontDown = CreateBoardPlanQuickButton("A-", "字号小一点", "字号缩小（本地调整）");
        _boardPlanTwoColumns = CreateBoardPlanQuickButton("两列", "改成两列", "改为两列排版（本地调整）");
        _boardPlanKeepFormula = CreateBoardPlanQuickButton("只保留公式", "只保留公式", "过滤为只保留公式块（本地调整）");

        _boardPlanMoveTarget = CreateSecondaryButton("移到板中板");
        _boardPlanMoveTarget.Height = 26;
        _boardPlanMoveTarget.MinHeight = 26;
        _boardPlanMoveTarget.FontSize = 11;
        _boardPlanMoveTarget.Padding = new Thickness(8, 0, 8, 0);
        AutomationProperties.SetName(_boardPlanMoveTarget, "切换写入目标画板");
        ToolTipService.SetToolTip(_boardPlanMoveTarget, "切换写入目标（本地调整，不改变内容）");
        _boardPlanMoveTarget.Click += (_, _) =>
            BoardPlanEditRequested?.Invoke(
                string.Equals(_boardPlan?.TargetBoard, "side", StringComparison.OrdinalIgnoreCase)
                    ? "移到主展板"
                    : "移到板中板");

        var adjustRowOne = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0),
        };
        adjustRowOne.Children.Add(_boardPlanFontUp);
        adjustRowOne.Children.Add(_boardPlanFontDown);
        adjustRowOne.Children.Add(_boardPlanTwoColumns);

        var adjustRowTwo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 0),
        };
        adjustRowTwo.Children.Add(_boardPlanKeepFormula);
        adjustRowTwo.Children.Add(_boardPlanMoveTarget);

        var confirmRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        confirmRow.Children.Add(_boardWriteConfirm);
        confirmRow.Children.Add(_boardWriteSelected);

        var cancelRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 0),
        };
        cancelRow.Children.Add(_boardWriteCancel);

        var boardPlanStack = new StackPanel { Spacing = 4 };
        boardPlanStack.Children.Add(_boardPlanTitle);
        boardPlanStack.Children.Add(_boardPlanWarnings);
        boardPlanStack.Children.Add(_boardPlanPair);
        boardPlanStack.Children.Add(adjustRowOne);
        boardPlanStack.Children.Add(adjustRowTwo);
        boardPlanStack.Children.Add(_replaceOriginal);
        boardPlanStack.Children.Add(confirmRow);
        boardPlanStack.Children.Add(cancelRow);

        _boardPlanPreview = new Border
        {
            Background = _accentSoftBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = boardPlanStack,
        };

        _cursor = new TextBlock
        {
            Text = "▍",
            FontSize = 14,
            Foreground = _accentBrush,
            Margin = new Thickness(2, -2, 0, 0),
        };
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _cursorTimer.Tick += (_, _) => _cursor.Opacity = _cursor.Opacity < 0.5 ? 1 : 0.15;

        _body = new StackPanel { Spacing = 10 };
        _body.Children.Add(_thumbnailHost);
        _body.Children.Add(_problemScroll);
        _body.Children.Add(_addRegionLink);
        _body.Children.Add(_hintRow);
        _body.Children.Add(_content);

        _body.Children.Add(_formulaCard);

        _body.Children.Add(_boardPlanPreview);

        _scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 8),
        };

        _scroll.SizeChanged += (_, e) => ApplyBoardPlanOrientation(e.NewSize.Width);

        // ---- footer actions ---------------------------------------------
        _paste = CreatePrimaryButton("贴回白板");
        _favorite = CreateSecondaryButton("收藏例题");
        _stop = CreateSecondaryButton("停止生成");
        _copy = CreateSecondaryButton("复制");
        _retry = CreateSecondaryButton("重试");
        _revealNext = CreateSecondaryButton("下一步");
        _variantBasic = CreateSecondaryButton("基础变式");
        _variantAdvanced = CreateSecondaryButton("提高变式");
        _toggleAnnotations = CreateSecondaryButton("隐藏标注");

        _organizeBoard = CreateSecondaryButton("整理板书");
        _organizeBoard.Height = 30;
        AutomationProperties.SetName(_organizeBoard, "整理板书");
        ToolTipService.SetToolTip(_organizeBoard, "把当前选区/整页交给 AI 整理成结构化板书，预览确认后写入");
        _organizeBoard.Click += (_, _) => RequestBoardWrite("organize", "整理当前页板书");

        _aiWrite = CreateSecondaryButton("AI 书写");
        _aiWrite.Height = 30;
        AutomationProperties.SetName(_aiWrite, "AI 书写");
        ToolTipService.SetToolTip(_aiWrite, "按追问框中的指令生成板书内容，预览确认后写入");
        _aiWrite.Click += (_, _) => RequestBoardWrite("write", "写一句本课总结");

        _toggleAnnotations.Visibility = Visibility.Collapsed;
        _variantBasic.Click += (_, _) => VariantRequested?.Invoke(false);
        _variantAdvanced.Click += (_, _) => VariantRequested?.Invoke(true);
        _toggleAnnotations.Click += (_, _) =>
        {
            _annotationsVisible = !_annotationsVisible;
            _toggleAnnotations.Content = _annotationsVisible ? "隐藏标注" : "显示标注";
            ApplyAnnotations(_lastSession);
        };

        _studyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Visibility = Visibility.Collapsed,
        };
        _studyRow.Children.Add(_variantBasic);
        _studyRow.Children.Add(_variantAdvanced);
        _studyRow.Children.Add(_toggleAnnotations);
        _body.Children.Insert(0, _studyRow);
        _close = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12, Foreground = _textPrimary },
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        _stop.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Collapsed;
        _paste.Visibility = Visibility.Collapsed;
        _favorite.Visibility = Visibility.Collapsed;
        _copy.IsEnabled = false;
        _revealNext.Visibility = Visibility.Collapsed;

        _stop.Click += (_, _) => StopRequested?.Invoke();
        _retry.Click += (_, _) => RetryRequested?.Invoke();
        _copy.Click += (_, _) => CopyRequested?.Invoke();
        _paste.Click += (_, _) => PasteRequested?.Invoke();
        _favorite.Click += (_, _) => FavoriteExample();
        _revealNext.Click += (_, _) => RevealNextRequested?.Invoke();
        _close.Click += (_, _) => Close();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionRow.Children.Add(_paste);
        actionRow.Children.Add(_favorite);
        actionRow.Children.Add(_stop);
        actionRow.Children.Add(_revealNext);
        actionRow.Children.Add(_copy);
        actionRow.Children.Add(_retry);

        // 底部最多同时出现「贴回/收藏/停止/下一步/复制/重试」，380 宽卡片放不下，
        // 放进横向滚动容器避免新增按钮把「重试」挤出可视区，纵向仍不滚动。
        var actionScroll = new ScrollViewer
        {
            Content = actionRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _actions = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actionScroll, 0);
        Grid.SetColumn(_close, 1);
        _actions.Children.Add(actionScroll);
        _actions.Children.Add(_close);

        _followUp = new TextBox
        {
            PlaceholderText = "继续追问…",
            Height = 36,
            FontSize = 13,
            CornerRadius = new CornerRadius(8),
            Background = _inputBgBrush,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _send = CreatePrimaryButton("发送");
        _send.Height = 36;
        _send.Click += (_, _) => SendFollowUp();
        _followUp.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) SendFollowUp();
        };

        var followRow = new Grid { ColumnSpacing = 8 };
        followRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        followRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_followUp, 0);
        Grid.SetColumn(_send, 1);
        followRow.Children.Add(_followUp);
        followRow.Children.Add(_send);

        var footer = new StackPanel { Spacing = 8, Padding = new Thickness(12, 8, 12, 12) };

        var boardWriteRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        boardWriteRow.Children.Add(_organizeBoard);
        boardWriteRow.Children.Add(_aiWrite);

        footer.Children.Add(_actions);
        footer.Children.Add(boardWriteRow);
        footer.Children.Add(followRow);

        // ---- root --------------------------------------------------------
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_titleBar, 0);
        Grid.SetRow(statusRow, 1);
        Grid.SetRow(_scroll, 2);
        Grid.SetRow(footer, 3);
        rootGrid.Children.Add(_titleBar);
        rootGrid.Children.Add(statusRow);
        rootGrid.Children.Add(_scroll);
        rootGrid.Children.Add(footer);

        _root = new Border
        {
            Background = _rootBgBrush,
            BorderBrush = _highlightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = rootGrid,
        };
        Content = _root;

        SystemBackdrop = new DesktopAcrylicBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        WindowChrome.ApplyTheme(this, theme);
        ApplyRoundedCorners();
        UpdateActionState();

        base.Closed += (_, _) =>
        {
            _cursorTimer.Stop();
            _highlightTimer.Stop();
            _noticeTimer.Stop();
            _thumbStream?.Dispose();
            _thumbStream = null;
            foreach (InMemoryRandomAccessStream stream in _problemStreams)
            {
                try { stream.Dispose(); }
                catch (Exception) { }
            }

            _problemStreams.Clear();
            _problemImages.Clear();
            Closed?.Invoke();
        };
    }

    /// <summary>按 DIP 定位并显示；高度取目标工作区高的 <see cref="MaxHeightRatio"/>（上限 460）。</summary>
    public void ShowAt(AssistantPlacement placement, double dpiScale, double workHeightDip = 0)
    {
        double dpi = dpiScale > 0 ? dpiScale : 1.0;
        double workHeight = workHeightDip > 0
            ? workHeightDip
            : InkCanvas.Platform.System.ScreenService.WorkArea.Height / dpi;
        double cardHeight = Math.Min(MaxCardHeight, workHeight * MaxHeightRatio);
        cardHeight = Math.Max(200, cardHeight);

        _scroll.MaxHeight = Math.Max(120, cardHeight - TitleHeight - 144);

        try
        {
            AppWindow.MoveAndResize(new RectInt32(
                (int)Math.Round(placement.X * dpi),
                (int)Math.Round(placement.Y * dpi),
                (int)Math.Round(CardWidth * dpi),
                (int)Math.Round(cardHeight * dpi)));
            // 窗口可能在隐藏状态创建；AppWindow.Show 后再 Activate，
            // 否则首次打开只显示空白原生边框。
            AppWindow.Show();
            Activate();
            ApplyRoundedCorners();
        }
        catch (Exception ex)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] ShowAt 异常：{ex}", InkCanvas.Core.Diagnostics.LogType.Error);
            throw;
        }
    }

    public void Apply(AssistantSession session, string thumbnailBase64, bool preserveScroll = false)
    {
        if (session is null) return;
        _lastSession = session;

        double offset = preserveScroll ? _scroll.VerticalOffset : 0;

        UpdateStatus(session);
        UpdateVerification(session);
        UpdateHint(session);
        _modeText.Text = AssistantTeachingPolicy.Label(session.TeachingMode);
        _modeText.Foreground = session.TeachingMode == AssistantTeachingMode.Hint
            ? Solid(0xFF, 0xB8, 0x86, 0x0B)
            : _accentBrush;

        if (!string.IsNullOrEmpty(thumbnailBase64) && thumbnailBase64 != _thumbBase64)
            _ = LoadThumbnailAsync(thumbnailBase64);

        UpdateThumbnailSize();
        ApplyAnnotations(session);

        _content.Children.Clear();
        _hasCopyable = !string.IsNullOrEmpty(session.Text) || !string.IsNullOrEmpty(session.Formula);

        foreach (MarkdownBlock block in AssistantText.ParseMarkdown(session.Text ?? ""))
            AddBlock(block);

        AddSteps(session);

        bool active = IsActive(session.State);
        if (active)
        {
            _cursor.Opacity = 1;
            _content.Children.Add(_cursor);
            if (!_cursorTimer.IsEnabled) _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
        }

        if (session.State == AssistantState.Error)
            AddError(session);

        if (!string.IsNullOrEmpty(session.Formula) && ShowFinalAnswer(session))
        {
            _formulaText.Text = AssistantText.LatexToPlain(session.Formula);
            _formulaCard.Visibility = Visibility.Visible;
        }
        else
        {
            _formulaText.Text = "";
            _formulaCard.Visibility = Visibility.Collapsed;
        }

        UpdateActionState();

        if (preserveScroll && offset > 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _scroll.ChangeView(null, offset, null, true);
                }
                catch (Exception)
                {
                    // 滚动位置恢复失败不影响内容更新。
                }
            });
        }
    }

    /// <summary>设置缩略图对应的原选区锚点（屏幕 DIP），用于把框选换算回选区坐标。</summary>
    public void SetAnchor(AssistantRect anchor)
    {
        _anchor = anchor;
        UpdateThumbnailSize();
        ApplyAnnotations(_lastSession);
    }

    /// <summary>
    /// 渲染拼题选区列表。单选区保持原有大图与圈选纠错；多选区改为横向缩略图，
    /// 支持按顺序追加、删除与前后移动，重排后由 ShellController 重新请求。
    /// </summary>
    public void ApplyProblem(ProblemContext problem, string notice = null)
    {
        if (!string.IsNullOrWhiteSpace(notice)) ShowNotice(notice);

        _problem = problem;

        _problemList.Children.Clear();
        if (problem is null || problem.Count <= 1)
        {
            _problemScroll.Visibility = Visibility.Collapsed;
            _thumbnailHost.Visibility = string.IsNullOrEmpty(_thumbBase64)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _addRegionLink.Visibility = problem is { Count: 1 }
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        _addRegionLink.Visibility = Visibility.Collapsed;

        foreach (ProblemRegion region in ProblemContextPolicy.Ordered(problem))
            _problemList.Children.Add(CreateProblemCard(region));

        _problemList.Children.Add(CreateAddRegionCard());
        _problemScroll.Visibility = Visibility.Visible;
        _thumbnailHost.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement CreateProblemCard(ProblemRegion region)
    {
        var image = new Image
        {
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (!string.IsNullOrEmpty(region.ImageBase64))
        {
            if (_problemImages.TryGetValue(region.ImageBase64, out BitmapImage cached))
            {
                image.Source = cached;
            }
            else
            {
                _ = LoadProblemImageAsync(region.ImageBase64, image);
            }
        }

        var badge = new Border
        {
            Background = _accentSolidBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 2, 0, 0),
            Child = new TextBlock
            {
                Text = region.Order.ToString(),
                FontSize = 10,
                Foreground = WhiteBrush,
            },
        };

        var moveLeft = ProblemCardButton("◀", "前移", () => MoveRegionRequested?.Invoke(region.Id, -1));
        var moveRight = ProblemCardButton("▶", "后移", () => MoveRegionRequested?.Invoke(region.Id, 1));
        var remove = ProblemCardButton("✕", "移除此选区", () => RemoveRegionRequested?.Invoke(region.Id));
        var retry = ProblemCardButton("↻", "重新识别此选区",
            () => ReRecognizeRegionRequested?.Invoke(region.Id));
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        controls.Children.Add(moveLeft);
        controls.Children.Add(moveRight);
        controls.Children.Add(retry);
        controls.Children.Add(remove);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(image, 0);
        Grid.SetRow(badge, 0);
        Grid.SetRow(controls, 1);
        layout.Children.Add(image);
        layout.Children.Add(badge);
        layout.Children.Add(controls);

        return new Border
        {
            Width = 104,
            Background = _inputBgBrush,
            BorderBrush = _controlStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Child = layout,
        };
    }

    private FrameworkElement CreateAddRegionCard()
    {
        var content = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = "＋",
            FontSize = 20,
            Foreground = _accentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "追加选区",
            FontSize = 10.5,
            Foreground = _textSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new Button
        {
            Width = 104,
            Height = 88,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = _controlStrokeBrush,
            BorderThickness = new Thickness(1),
            Content = content,
        };
        ToolTipService.SetToolTip(button, "先回画布框选题干/图形/条件，再点此追加为同一题");
        button.Click += (_, _) => AddRegionRequested?.Invoke();
        return button;
    }

    private Button ProblemCardButton(string glyph, string tip, Action invoke)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 9.5,
                Foreground = _textSecondary,
            },
            Width = 28,
            Height = 22,
            MinWidth = 28,
            MinHeight = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => invoke();
        return button;
    }

    private async Task LoadProblemImageAsync(string base64, Image target)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _problemImages[base64] = bitmap;
            _problemStreams.Add(stream);
            target.Source = bitmap;
        }
        catch (Exception)
        {
            // 单个缩略图损坏不影响其他选区与正文。
        }
    }

    /// <summary>状态行临时提示（例如局部纠错失败），不改变会话状态。</summary>
    public void ShowNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _noticeTimer.Stop();
        _statusText.Text = message;
        _statusText.Foreground = _accentBrush;
        _noticeTimer.Start();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateActionState();
    }

    public void SetPasted(bool pasted)
    {
        _pasted = pasted;
        UpdateActionState();
    }

    /// <summary>
    /// 设置「收藏为例题」可取得的来源信息：课件名、幻灯片 id、画板页。
    /// 由 ShellController 在创建/更新浮窗时注入；取不到时传空或 0。
    /// boardPage 语义原样透传：0 = 第 1 页，-1 = 无白板，N = 第 N+1 页。
    /// </summary>
    public void SetFavoriteSource(string presentation, int slideId, int boardPage)
    {
        _sourcePresentation = presentation ?? "";
        _sourceSlideId = slideId > 0 ? slideId : 0;
        _sourceBoardPage = boardPage;
    }

    /// <summary>
    /// 收藏为例题的完整来源：除课件/页码外记录课堂会话、请求、画板/页标识，
    /// 便于「再讲一次」「贴回白板」还原上下文。空值按未记录处理，旧调用点保持兼容。
    /// </summary>
    public void SetFavoriteContext(
        string sessionId, string requestId, string boardId, Guid boardPageId,
        string groupId, IReadOnlyList<string> objectIds)
    {
        _sourceSessionId = sessionId ?? "";
        _sourceRequestId = requestId ?? "";
        _sourceBoardId = boardId ?? "";
        _sourceBoardPageId = boardPageId;
        _sourceGroupId = groupId ?? "";
        _sourceObjectIds = objectIds is { Count: > 0 } ? objectIds.ToArray() : Array.Empty<string>();
    }

    /// <summary>AI 板书写入后记录原生对象分组，下一次收藏时一并写入例题关联。</summary>
    public void SetFavoriteObjects(string groupId, IReadOnlyList<Guid> objectIds)
    {
        _sourceGroupId = groupId ?? "";
        _sourceObjectIds = objectIds is { Count: > 0 }
            ? objectIds.Select(id => id.ToString("N")).ToArray()
            : Array.Empty<string>();
    }

    private void FavoriteExample()
    {
        AssistantSession session = _lastSession;
        if (session is null || session.State != AssistantState.Done) return;

        string answer = BuildFavoriteAnswer(session);
        if (string.IsNullOrWhiteSpace(answer)) return;

        try
        {
            var entry = new ExampleEntry
            {
                Title = FavoriteTitle(answer),
                Answer = answer,
                Topic = "",
                KnowledgePoints = new List<string>(),
                SourcePresentation = _sourcePresentation,
                SourceSlideId = _sourceSlideId,
                BoardPage = _sourceBoardPage,
                CorrectedText = "",
                CreatedAt = DateTime.Now,
                AiSessionId = _sourceSessionId,
                RequestId = _sourceRequestId,
                BoardId = _sourceBoardId,
                BoardPageId = _sourceBoardPageId,
                GroupId = _sourceGroupId,
                ObjectIds = new List<string>(_sourceObjectIds),
                StepsJson = ExampleEntryCodec.EncodeSteps(session.Steps),
                AnnotationsJson = ExampleEntryCodec.EncodeAnnotations(session.Annotations),
                UpdatedAt = DateTime.Now,
            };

            bool added = ExampleLibraryStore.Add(entry, settings: _features);
            ShowNotice(added ? "已收藏为例题" : "收藏失败");
        }
        catch (Exception ex)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"收藏为例题失败：{ex.Message}", InkCanvas.Core.Diagnostics.LogType.Error);
            ShowNotice("收藏失败");
        }
    }

    /// <summary>完整讲评文本；公式为 LaTeX 源，追加在正文之后以保留来源。</summary>
    private static string BuildFavoriteAnswer(AssistantSession session)
    {
        string text = session.Text ?? "";
        string formula = session.Formula ?? "";
        if (string.IsNullOrEmpty(formula)) return text;
        return string.IsNullOrEmpty(text) ? formula : text.TrimEnd() + "\n" + formula;
    }

    /// <summary>标题取当前回答的首个非空行并截断到 60 字符；为空时回退默认标题。</summary>
    private static string FavoriteTitle(string answer)
    {
        foreach (string line in answer.Split('\n'))
        {
            string trimmed = (line ?? "").Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length <= 60 ? trimmed : trimmed.Substring(0, 60);
        }

        return "AI 讲题收藏";
    }

    private static string ModeLabel(AssistantMode mode) => mode switch
    {
        AssistantMode.Formula => "公式",
        AssistantMode.Solve => "求解",
        AssistantMode.Shape => "图形",
        AssistantMode.Chat => "问答",
        _ => "自动",
    };

    private static bool ShowFinalAnswer(AssistantSession session)
        => AssistantTeachingPolicy.ShowsFinalAnswer(
            session.TeachingMode, session.RevealedSteps, session.Steps?.Count ?? 0);

    private Border CreateBadge(out TextBlock text)
    {
        text = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            Background = _inputBgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = text,
        };
    }

    private void UpdateVerification(AssistantSession session)
    {
        string label;
        Brush background;
        Brush foreground;

        if (session.Confidence >= 0 && session.Confidence < 0.5)
        {
            label = "低置信";
            background = Solid(0x24, 0xB8, 0x86, 0x0B);
            foreground = Solid(0xFF, 0xB8, 0x86, 0x0B);
        }
        else if (session.Verified == true)
        {
            label = "已验证";
            background = Solid(0x22, 0x10, 0x7C, 0x10);
            foreground = OkBrush;
        }
        else if (session.Verified == false)
        {
            label = "未验证";
            background = _inputBgBrush;
            foreground = _textSecondary;
        }
        else
        {
            SetBadge(_statusBadge, _statusBadgeText, null, null, null);
            SetBadge(_formulaBadge, _formulaBadgeText, null, null, null);
            return;
        }

        SetBadge(_statusBadge, _statusBadgeText, label, background, foreground);
        SetBadge(_formulaBadge, _formulaBadgeText, label, background, foreground);
    }

    private static void SetBadge(Border badge, TextBlock text, string label, Brush background, Brush foreground)
    {
        if (label is null)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        text.Text = label;
        text.Foreground = foreground;
        badge.Background = background;
        badge.Visibility = Visibility.Visible;
    }

    private void UpdateHint(AssistantSession session)
    {
        string hint = session.LocalHint ?? "";
        bool show = hint.Length > 0 && IsActive(session.State);
        _hintText.Text = show ? "本地提示：" + hint : "";
        _hintRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateThumbnailSize()
    {
        if (_anchor.IsEmpty)
        {
            _thumbnailHost.Height = MaxThumbnailHeight;
            return;
        }

        double available = _scroll.ActualWidth > 0
            ? Math.Max(80, _scroll.ActualWidth - 24)
            : CardWidth - 48;
        double ratio = _anchor.Height / Math.Max(1.0, _anchor.Width);
        _thumbnailHost.Height = Math.Clamp(available * ratio, 24, MaxThumbnailHeight);
    }

    private Button CreatePrimaryButton(string text) => new()
    {
        Content = text,
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(14, 0, 14, 0),
        FontSize = 13,
        CornerRadius = new CornerRadius(6),
        Background = _accentSolidBrush,
        Foreground = WhiteBrush,
        BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private Button CreateSecondaryButton(string text) => new()
    {
        Content = text,
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(12, 0, 12, 0),
        FontSize = 13,
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        Foreground = _textPrimary,
        BorderBrush = _controlStrokeBrush,
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// 预填追问框（例题「再讲一次」）：只填入文本、不自动提交，避免在识别流未完成时抢占会话。
    /// </summary>
    public void PrefillFollowUp(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        _followUp.Text = question.Trim();
        _followUp.Focus(FocusState.Programmatic);
    }

    /// <summary>提交一条追问：复用现有 <see cref="FollowUpRequested"/> 通道，不新建教学模式。</summary>
    public void SubmitFollowUp(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        _followUp.Text = question.Trim();
        SendFollowUp();
    }

    private void SendFollowUp()
    {
        if (!_aiTeachingEnabled) return;
        if (_busy) return;
        string text = _followUp.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // 可识别的连续修改统一交给 Shell：优先作用于当前选中的 AI 对象组，
// 无选中时才回到预览 plan；两条路径都不调用 AI、不新增内容。
        if (BoardPlanEditPolicy.TryParseObjectEdit(text, out _, out _))
        {
            _followUp.Text = "";
            BoardPlanEditRequested?.Invoke(text);
            return;
        }

        _followUp.Text = "";
        FollowUpRequested?.Invoke(text);
    }

    /// <summary>整理板书 / AI 书写：优先复用追问框文本，为空时使用默认指令。</summary>
    private void RequestBoardWrite(string intent, string defaultInstruction)
    {
        if (_busy || !_aiTeachingEnabled) return;

        string instruction = (_followUp.Text ?? "").Trim();
        if (instruction.Length == 0) instruction = defaultInstruction;
        BoardWriteRequested?.Invoke(instruction, intent);
    }

    /// <summary>
    /// 显示“原始内容 | 整理结果”对照预览；窄屏自动改为上下堆叠。
    /// 勾选状态在确认时通过 <see cref="BoardWriteConfirmedSelection"/> 传出。
    /// </summary>
    public void ApplyBoardPlanPreview(AssistantBoardPlan plan, string instruction)
    {
        _boardPlan = plan;
        _boardPlanList.Children.Clear();
        _boardPlanOriginalList.Children.Clear();
        _boardPlanWarnings.Children.Clear();
        _boardPlanChecks.Clear();
        _replaceOriginal.IsChecked = false;

        if (plan is null || plan.IsEmpty)
        {
            _boardPlanPreview.Visibility = Visibility.Collapsed;
            UpdateActionState();
            return;
        }

        string intentLabel = (plan.Intent ?? "").Trim().ToLowerInvariant() switch
        {
            "organize" => "整理板书",
            "write" => "AI 书写",
            _ => "板书计划",
        };
        string instructionText = string.IsNullOrWhiteSpace(instruction)
            ? ""
            : " · " + TrimText(instruction, 24);
        string confidenceText = plan.Confidence < 0.999 ? $" · 置信度 {plan.Confidence:0.00}" : "";
        _boardPlanTitle.Text = $"{intentLabel}{instructionText} · {plan.Blocks.Count} 块{confidenceText}";

        bool onSideBoard = string.Equals(plan.TargetBoard, "side", StringComparison.OrdinalIgnoreCase);
        _boardPlanMoveTarget.Content = onSideBoard ? "移到主展板" : "移到板中板";
        ToolTipService.SetToolTip(_boardPlanMoveTarget,
            onSideBoard ? "切换写入目标到主展板（不改变内容）" : "切换写入目标到板中板（不改变内容）");

        if (plan.Originals is { Count: > 0 })
        {
            foreach (string line in plan.Originals)
            {
                _boardPlanOriginalList.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 11.5,
                    Foreground = _textSecondary,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        else
        {
            _boardPlanOriginalList.Children.Add(new TextBlock
            {
                Text = "（AI 未返回识别原文）",
                FontSize = 11,
                Foreground = _textSecondary,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (string warning in plan.Warnings ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(warning)) continue;
            _boardPlanWarnings.Children.Add(CreateWarningText("⚠ " + warning.Trim()));
        }

        if (plan.NeedsReview)
        {
            _boardPlanWarnings.Children.Add(CreateWarningText(
                $"整体置信度 {plan.Confidence:0.00} 低于 "
                + $"{AssistantBoardPlan.ReviewThreshold:0.00}，建议逐块复核。"));
        }

        string[] lowBlocks = plan.Blocks
            .Where(block => block.Confidence < AssistantBoardPlan.ReviewThreshold)
            .Select(block => block.Id)
            .ToArray();
        if (lowBlocks.Length > 0)
            _boardPlanWarnings.Children.Add(CreateWarningText("需复核块：" + string.Join("、", lowBlocks)));

        _boardPlanWarnings.Visibility = _boardPlanWarnings.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        int shown = 0;
        foreach (AssistantBoardBlock block in plan.Blocks)
        {
            if (shown >= MaxBoardPlanPreviewBlocks) break;
            _boardPlanList.Children.Add(CreateBoardPlanRow(block, shown + 1));
            shown++;
        }

        if (plan.Blocks.Count > shown)
        {
            _boardPlanList.Children.Add(new TextBlock
            {
                Text = $"…另有 {plan.Blocks.Count - shown} 块未展开",
                FontSize = 10.5,
                Foreground = _textSecondary,
            });
        }

        if (plan.Notes is { Count: > 0 })
        {
            _boardPlanList.Children.Add(new TextBlock
            {
                Text = "提示：" + string.Join("；", plan.Notes),
                FontSize = 10.5,
                Foreground = _textSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _boardPlanPreview.Visibility = Visibility.Visible;
        UpdateActionState();
    }

    /// <summary>宽屏左右两栏（原始 | 结果），窄屏上下堆叠。</summary>
    private void ApplyBoardPlanOrientation(double width)
    {
        bool wide = width >= 520;
        if (wide == _boardPlanWideLayout
            && _boardPlanPair.ColumnDefinitions.Count + _boardPlanPair.RowDefinitions.Count > 0)
            return;

        _boardPlanWideLayout = wide;
        _boardPlanPair.ColumnDefinitions.Clear();
        _boardPlanPair.RowDefinitions.Clear();

        if (wide)
        {
            _boardPlanPair.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _boardPlanPair.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _boardPlanPair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_boardPlanOriginalPane, 0);
            Grid.SetColumn(_boardPlanOriginalPane, 0);
            Grid.SetRow(_boardPlanResultPane, 0);
            Grid.SetColumn(_boardPlanResultPane, 1);
        }
        else
        {
            _boardPlanPair.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _boardPlanPair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _boardPlanPair.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_boardPlanOriginalPane, 0);
            Grid.SetColumn(_boardPlanOriginalPane, 0);
            Grid.SetRow(_boardPlanResultPane, 1);
            Grid.SetColumn(_boardPlanResultPane, 0);
        }
    }

    private async Task ConfirmBoardWriteSelectionAsync(bool all)
    {
        if (_boardPlanBusy || _boardPlan is not { IsEmpty: false }) return;

        List<string> ids = all
            ? _boardPlan.Blocks.Select(block => block.Id).Distinct().ToList()
            : _boardPlanChecks.Where(item => item.Box.IsChecked == true)
                .Select(item => item.Id).Distinct().ToList();

        if (ids.Count == 0)
        {
            ShowNotice("请至少选择一个整理结果块。");
            return;
        }

        bool replace = _replaceOriginal?.IsChecked == true;
        if (replace && !await ConfirmReplaceOriginalAsync()) return;

        BoardWriteConfirmedSelection?.Invoke(ids, replace);
    }

    private async Task<bool> ConfirmReplaceOriginalAsync()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            ShowNotice("无法打开替换确认框，已取消写入。");
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "替换原板书？",
            Content = "将删除当前页原笔画（有选区时仅删除选区），再写入所选新卡片；"
                + "可用一次 Ctrl+Z 撤销并恢复原迹。",
            PrimaryButtonText = "替换并写入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private TextBlock CreateWarningText(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Foreground = ReviewBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock BoardPlanSectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = _textSecondary,
    };

    public void ClearBoardPlanPreview()
    {
        _boardPlan = null;
        _boardPlanList.Children.Clear();
        _boardPlanOriginalList.Children.Clear();
        _boardPlanWarnings.Children.Clear();
        _boardPlanChecks.Clear();
        _replaceOriginal.IsChecked = false;
        _boardPlanPreview.Visibility = Visibility.Collapsed;
        UpdateActionState();
    }

    public void SetBoardPlanBusy(bool busy)
    {
        _boardPlanBusy = busy;
        UpdateActionState();
    }

    private FrameworkElement CreateBoardPlanRow(AssistantBoardBlock block, int order)
    {
        string kind = (block.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "formula" => "公式",
            "wordlist" => "词语",
            "poem" => "古诗",
            _ => "文本",
        };
        bool needsReview = block.Confidence < AssistantBoardPlan.ReviewThreshold;
        string title = TrimText(block.Title, 24);
        string header = string.IsNullOrEmpty(title) ? $"{order}. {kind}" : $"{order}. {kind} · {title}";
        if (needsReview) header += $" · 需复核 {block.Confidence:0.00}";

        var row = new StackPanel { Spacing = 1, MinWidth = 0 };
        row.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = needsReview ? ReviewBrush : _textPrimary,
            TextWrapping = TextWrapping.Wrap,
        });

        string summary = BuildBoardPlanSummary(block);
        if (!string.IsNullOrEmpty(summary))
        {
            row.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 11,
                Foreground = _textSecondary,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            });
        }

        if (!string.IsNullOrWhiteSpace(block.Note))
        {
            row.Children.Add(new TextBlock
            {
                Text = "备注：" + TrimText(block.Note, 60),
                FontSize = 10,
                Foreground = ReviewBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = DescribeBoardBlockRect(block.Rect),
            FontSize = 10,
            Foreground = _textSecondary,
        });

        var check = new CheckBox
        {
            IsChecked = true,
            MinWidth = 0,
            Margin = new Thickness(0, -4, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(check, $"采用第 {order} 块");
        ToolTipService.SetToolTip(check, needsReview ? "该块置信度偏低，建议复核后再采用" : "勾选以部分采用");
        _boardPlanChecks.Add((block.Id, check));

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(check, 0);
        Grid.SetColumn(row, 1);
        grid.Children.Add(check);
        grid.Children.Add(row);
        return grid;
    }

    private static string BuildBoardPlanSummary(AssistantBoardBlock block)
    {
        string kind = (block.Kind ?? "").Trim().ToLowerInvariant();
        if (kind == "formula") return TrimText(block.Latex, 90);
        if (block.Lines is { Count: > 0 }) return TrimText(string.Join(" / ", block.Lines), 90);
        return TrimText(block.Text, 90);
    }

    private static string DescribeBoardBlockRect(AssistantRect rect)
    {
        if (rect.IsEmpty) return "位置：自动落点";
        return $"位置：x {Math.Round(rect.Left * 100)}% · y {Math.Round(rect.Top * 100)}% · "
            + $"w {Math.Round(rect.Width * 100)}% · h {Math.Round(rect.Height * 100)}%";
    }

    private static string TrimText(string text, int max)
    {
        string value = (text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        if (value.Length <= max) return value;
        return value.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    private void UpdateStatus(AssistantSession session)
    {
        string text;
        Brush dot;
        Brush textForeground = _textSecondary;
        bool ring = false;
        switch (session.State)
        {
            case AssistantState.Capturing:
            case AssistantState.Recognizing:
                text = "识别中";
                dot = _accentBrush;
                ring = true;
                break;
            case AssistantState.Streaming:
                text = "生成中";
                dot = _accentBrush;
                ring = true;
                break;
            case AssistantState.Done:
                text = "已完成";
                dot = OkBrush;
                break;
            case AssistantState.Error:
                text = "出错";
                dot = ErrorBrush;
                textForeground = ErrorBrush;
                break;
            default:
                text = "就绪";
                dot = _textSecondary;
                break;
        }

        _statusText.Text = _aiTeachingEnabled ? text : "AI 讲题已关闭";
        _statusText.Foreground = textForeground;
        _statusDot.Fill = dot;
        _progressRing.IsActive = ring;
        _progressRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActionState()
    {
        bool aiTeachingEnabled = _features?.EnableAiTeaching ?? true;
        bool exerciseFlowEnabled = _features?.EnableExerciseFlow ?? true;
        _aiTeachingEnabled = aiTeachingEnabled;

        bool active = _lastSession is not null && IsActive(_lastSession.State);
        bool error = _lastSession?.State == AssistantState.Error;
        bool done = _lastSession?.State == AssistantState.Done;
        bool moreSteps = _lastSession is not null && AssistantTeachingPolicy.HasMoreSteps(
            _lastSession.TeachingMode,
            _lastSession.RevealedSteps,
            _lastSession.Steps?.Count ?? 0);

        _stop.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        _paste.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        _revealNext.Visibility = moreSteps ? Visibility.Visible : Visibility.Collapsed;

        bool hasAnnotations = _lastSession?.Annotations is { Count: > 0 };
        _studyRow.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        _toggleAnnotations.Visibility = hasAnnotations ? Visibility.Visible : Visibility.Collapsed;
        _toggleAnnotations.Content = _annotationsVisible ? "隐藏标注" : "显示标注";

        // 例题库开关关闭时不出现入口；忙碌/出错/无内容时与既有按钮一样禁用。
        bool exampleLibraryEnabled = _features is null || _features.EnableExampleLibrary;
        bool hasFavoriteContent = !string.IsNullOrWhiteSpace(_lastSession?.Text)
            || !string.IsNullOrWhiteSpace(_lastSession?.Formula);
        _favorite.Visibility = done && exampleLibraryEnabled ? Visibility.Visible : Visibility.Collapsed;
        _favorite.IsEnabled = !_busy && done && hasFavoriteContent;

        _stop.IsEnabled = active;
        _retry.IsEnabled = !_busy && error;
        _revealNext.IsEnabled = !_busy && moreSteps;
        _copy.IsEnabled = !_busy && _hasCopyable;
        _paste.IsEnabled = !_busy && done && !_pasted;
        _paste.Content = _pasted ? "已贴回" : "贴回白板";
        _send.IsEnabled = !_busy && aiTeachingEnabled;
        _followUp.IsEnabled = !_busy && aiTeachingEnabled;
        _organizeBoard.IsEnabled = !_busy && aiTeachingEnabled;
        _aiWrite.IsEnabled = !_busy && aiTeachingEnabled;

        bool hasBoardPlan = _boardPlan is { IsEmpty: false };
        _boardPlanPreview.Visibility = hasBoardPlan ? Visibility.Visible : Visibility.Collapsed;
        _boardWriteConfirm.Content = _boardPlanBusy ? "写入中…" : "全部采用";
        _boardWriteConfirm.IsEnabled = !_busy && !_boardPlanBusy && hasBoardPlan && done;
        _boardWriteSelected.IsEnabled = _boardWriteConfirm.IsEnabled;
        _replaceOriginal.IsEnabled = !_boardPlanBusy && hasBoardPlan;
        bool boardPlanEditable = !_busy && !_boardPlanBusy && hasBoardPlan;
        _boardPlanFontUp.IsEnabled = boardPlanEditable;
        _boardPlanFontDown.IsEnabled = boardPlanEditable;
        _boardPlanTwoColumns.IsEnabled = boardPlanEditable;
        _boardPlanKeepFormula.IsEnabled = boardPlanEditable;
        _boardPlanMoveTarget.IsEnabled = boardPlanEditable;
        _boardWriteCancel.IsEnabled = !_boardPlanBusy;
        _variantBasic.IsEnabled = exerciseFlowEnabled;
        _variantAdvanced.IsEnabled = exerciseFlowEnabled;
        if (!exerciseFlowEnabled)
        {
            ToolTipService.SetToolTip(_variantBasic, "随堂练习已关闭");
            ToolTipService.SetToolTip(_variantAdvanced, "随堂练习已关闭");
        }

        if (!aiTeachingEnabled)
        {
            _statusText.Text = "AI 讲题已关闭";
            _statusText.Foreground = _textSecondary;
        }

        _close.IsEnabled = true;
    }

    private static bool IsActive(AssistantState state)
        => state is AssistantState.Capturing or AssistantState.Recognizing or AssistantState.Streaming;

    private void AddBlock(MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
            {
                var text = CreateRichText(block.Text, 15, FontWeights.SemiBold, _textPrimary);
                text.LineHeight = BodyLineHeight;
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                text.Margin = new Thickness(0, block.Level == 1 ? 8 : 5, 0, 0);
                _content.Children.Add(text);
                break;
            }

            case MarkdownBlockKind.ListItem:
            {
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = BodyFontSize,
                    LineHeight = BodyLineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = _textPrimary,
                    Margin = new Thickness(6, 0, 0, 0),
                };
                text.Inlines.Add(new Run { Text = "• ", Foreground = _accentBrush, FontWeight = FontWeights.SemiBold });
                AppendSegments(text, block.Text);
                _content.Children.Add(text);
                break;
            }

            case MarkdownBlockKind.Code:
            {
                _content.Children.Add(new Border
                {
                    Background = _codeBgBrush,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Child = new TextBlock
                    {
                        Text = block.Text ?? "",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = MonoFontSize,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _textPrimary,
                        IsTextSelectionEnabled = true,
                    },
                });
                break;
            }

            case MarkdownBlockKind.Table:
                _content.Children.Add(CreateTable(block));
                break;

            default:
                _content.Children.Add(CreateRichText(block.Text, BodyFontSize, FontWeights.Normal, _textPrimary));
                break;
        }
    }

    private TextBlock CreateRichText(string text, double fontSize, FontWeight weight, Brush foreground)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            LineHeight = BodyLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            FontWeight = weight,
            Foreground = foreground,
            IsTextSelectionEnabled = true,
        };
        AppendSegments(block, text);
        return block;
    }

    private void AppendSegments(TextBlock target, string text)
    {
        foreach (FormulaSegment segment in AssistantText.SplitFormulas(text ?? ""))
        {
            if (segment.IsFormula)
            {
                target.Inlines.Add(new InlineUIContainer { Child = CreateFormulaPill(segment) });
            }
            else
            {
                target.Inlines.Add(new Run { Text = segment.Text });
            }
        }
    }

    private FrameworkElement CreateFormulaPill(FormulaSegment segment)
    {
        var label = new TextBlock
        {
            Text = segment.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = MonoFontSize + 1,
            Foreground = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            Background = _pillBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };

        if (segment.Complete) return pill;

        var dashed = new Rectangle
        {
            Stroke = _accentBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            RadiusX = 4,
            RadiusY = 4,
        };
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(dashed);
        host.Children.Add(pill);
        return host;
    }

    private FrameworkElement CreateTable(MarkdownBlock block)
    {
        var rows = block.Rows ?? Array.Empty<IReadOnlyList<string>>();
        int columns = 1;
        foreach (var row in rows) columns = Math.Max(columns, row?.Count ?? 0);

        var grid = new Grid { ColumnSpacing = 0, RowSpacing = 0 };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int c = 0; c < columns; c++)
            {
                string value = c < (row?.Count ?? 0) ? row[c] : "";
                var cell = new Border
                {
                    BorderBrush = _controlStrokeBrush,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock
                    {
                        Text = value ?? "",
                        FontSize = 12,
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = _textPrimary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private void AddSteps(AssistantSession session)
    {
        if (session.Steps is null || session.Steps.Count == 0) return;

        int total = session.Steps.Count;
        int visible = AssistantTeachingPolicy.VisibleSteps(
            session.TeachingMode, session.RevealedSteps, total);

        for (int i = 0; i < visible && i < total; i++)
        {
            AssistantStep step = session.Steps[i];
            string latex = AssistantText.LatexToPlain(step.Latex ?? "");
            string line = step.Text ?? "";
            if (string.IsNullOrEmpty(line) && string.IsNullOrEmpty(latex)) continue;

            int number = step.Index > 0 ? step.Index : i + 1;

            var badge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = _accentSoftBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
                Child = new TextBlock
                {
                    Text = number.ToString(),
                    FontSize = 10,
                    Foreground = _accentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var right = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = BodyFontSize,
                LineHeight = BodyLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = _textSecondary,
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AppendSegments(right, line);
            if (!string.IsNullOrEmpty(latex))
            {
                if (!string.IsNullOrEmpty(line)) right.Inlines.Add(new Run { Text = " " });
                right.Inlines.Add(new InlineUIContainer
                {
                    Child = CreateFormulaPill(new FormulaSegment
                    {
                        IsFormula = true,
                        Complete = true,
                        Text = latex,
                        Raw = step.Latex ?? "",
                    }),
                });
            }

            var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(badge, 0);
            Grid.SetColumn(right, 1);
            row.Children.Add(badge);
            row.Children.Add(right);
            _content.Children.Add(row);
        }

        int hidden = total - visible;
        if (hidden > 0) AddStepsHint(session, hidden, visible, total);
    }

    /// <summary>
    /// 把解释步骤涉及的顶点/边/角绘制为缩略图上的高亮层。定位不可靠时不绘制，
    /// 只保留文字解释，避免误导学生。
    /// </summary>
    private void ApplyAnnotations(AssistantSession session)
    {
        _annotationCanvas.Children.Clear();
        if (session is null) return;

        AnnotationLayer layer = AnnotationLayerPolicy.Build(session, _anchor);
        AnnotationLayerPolicy.SyncVisibility(layer, session);
        if (!_annotationsVisible) AnnotationLayerPolicy.SetAllVisible(layer, false);

        double width = _annotationCanvas.ActualWidth;
        double height = _annotationCanvas.ActualHeight;
        if (width <= 0) width = _thumbnailHost.ActualWidth;
        if (height <= 0) height = _thumbnailHost.ActualHeight;
        if (!layer.CanLocate || width <= 0 || height <= 0) return;

        foreach (AnnotationHighlightItem item in layer.Items)
        {
            if (!item.Visible) continue;

            List<ShapeAnnotationPoint> projected = ShapeAnnotationGeometry.Project(
                item.Points, 0, 0, width, height);
            if (projected.Count < 2) continue;

            var line = new Polyline
            {
                Stroke = _accentBrush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            foreach (ShapeAnnotationPoint point in projected)
                line.Points.Add(new Windows.Foundation.Point(point.X, point.Y));
            _annotationCanvas.Children.Add(line);
        }
    }

    private void AddStepsHint(AssistantSession session, int hidden, int visible, int total)
    {
        string text = session.TeachingMode == AssistantTeachingMode.Hint
            ? $"只给提示模式：已隐藏 {hidden} 个步骤与最终答案，点上方模式标签切换"
            : $"还有 {hidden} 步未展开（{visible}/{total}），点「下一步」继续";

        _content.Children.Add(new Border
        {
            Background = _inputBgBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 2, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = _textSecondary,
                TextWrapping = TextWrapping.Wrap,
            },
        });
    }

    private void AddError(AssistantSession session)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = session.Error ?? "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = BodyFontSize,
            Foreground = ErrorBrush,
            IsTextSelectionEnabled = true,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = CreateSecondaryButton("重试");
        retry.Click += (_, _) => RetryRequested?.Invoke();
        var rephrase = CreateSecondaryButton("换个问法");
        rephrase.Click += (_, _) =>
        {
            _followUp.PlaceholderText = "换个问法：例如再讲详细一点";
            _followUp.Focus(FocusState.Programmatic);
        };
        row.Children.Add(retry);
        row.Children.Add(rephrase);
        panel.Children.Add(row);
        _content.Children.Add(panel);
    }

    private void OnThumbnailPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 圈选按下：enabled={RegionSelectionEnabled} anchorEmpty={_anchor.IsEmpty}",
            InkCanvas.Core.Diagnostics.LogType.Trace);
        if (!RegionSelectionEnabled || _anchor.IsEmpty) return;

        _thumbStart = e.GetCurrentPoint(_selectionOverlay).Position;
        _thumbTracking = true;
        _thumbDragging = false;
        _selectionOverlay.CapturePointer(e.Pointer);
        _highlightTimer.Stop();
        _selectionRect.Visibility = Visibility.Collapsed;
    }

    private void OnThumbnailPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbTracking) return;

        Windows.Foundation.Point current = e.GetCurrentPoint(_selectionOverlay).Position;
        double dx = current.X - _thumbStart.X;
        double dy = current.Y - _thumbStart.Y;
        if (!_thumbDragging && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

        if (!_thumbDragging)
        {
            _thumbDragging = true;
            _selectionOverlay.CapturePointer(e.Pointer);
        }

        // 只有真正开始画框才吞掉指针事件，避免轻触时阻塞 ScrollViewer 滚动。
        e.Handled = true;
        UpdateSelectionVisual(_thumbStart, current);
    }

    private void OnThumbnailPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbTracking) return;

        _thumbTracking = false;
        if (!_thumbDragging)
        {
            _selectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        _thumbDragging = false;
        Windows.Foundation.Point current = e.GetCurrentPoint(_selectionOverlay).Position;
        try
        {
            _selectionOverlay.ReleasePointerCapture(e.Pointer);
        }
        catch (Exception)
        {
            // 指针可能已被系统释放。
        }

        (double left, double top, double right, double bottom) = Normalize(_thumbStart, current);
        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 缩略图圈选松手：rect=({left:0.0},{top:0.0})-({right:0.0},{bottom:0.0})，"
            + $"anchor=({_anchor.Left:0.0},{_anchor.Top:0.0})-({_anchor.Right:0.0},{_anchor.Bottom:0.0})，"
            + $"RegionSelectionEnabled={RegionSelectionEnabled}",
            InkCanvas.Core.Diagnostics.LogType.Trace);
        if (right - left < MinRegionSize || bottom - top < MinRegionSize)
        {
            _selectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateSelectionVisual(_thumbStart, current);
        AssistantRect region = MapToAnchor(left, top, right, bottom);
        if (region.IsEmpty)
        {
            InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
                $"AI 缩略图圈选：映射后区域为空（缩略图 {_selectionOverlay.ActualWidth:0.0}x{_selectionOverlay.ActualHeight:0.0}），忽略",
                InkCanvas.Core.Diagnostics.LogType.Trace);
            _selectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 缩略图圈选：发送 RegionSelected，region=({region.Left:0.0},{region.Top:0.0})-({region.Right:0.0},{region.Bottom:0.0})",
            InkCanvas.Core.Diagnostics.LogType.Event);
        _highlightTimer.Start();
        RegionSelected?.Invoke(region);
    }

    private void OnThumbnailPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        InkCanvas.Core.Diagnostics.FileLog.WriteLogToFile(
            $"AI 缩略图圈选取消：tracking={_thumbTracking} dragging={_thumbDragging}",
            InkCanvas.Core.Diagnostics.LogType.Trace);
        _thumbTracking = false;
        _thumbDragging = false;
        _selectionRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelectionVisual(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        (double left, double top, double right, double bottom) = Normalize(start, end);
        Canvas.SetLeft(_selectionRect, left);
        Canvas.SetTop(_selectionRect, top);
        _selectionRect.Width = Math.Max(0, right - left);
        _selectionRect.Height = Math.Max(0, bottom - top);
        _selectionRect.Visibility = Visibility.Visible;
    }

    private static (double Left, double Top, double Right, double Bottom) Normalize(
        Windows.Foundation.Point start,
        Windows.Foundation.Point end)
    {
        return (
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X),
            Math.Max(start.Y, end.Y));
    }

    private AssistantRect MapToAnchor(double left, double top, double right, double bottom)
    {
        double width = _selectionOverlay.ActualWidth;
        double height = _selectionOverlay.ActualHeight;
        if (width <= 0 || height <= 0 || _anchor.IsEmpty) return default;

        double scaleX = _anchor.Width / width;
        double scaleY = _anchor.Height / height;

        return new AssistantRect(
            _anchor.Left + left * scaleX,
            _anchor.Top + top * scaleY,
            _anchor.Left + right * scaleX,
            _anchor.Top + bottom * scaleY);
    }

    private async Task LoadThumbnailAsync(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _thumbStream?.Dispose();
            _thumbStream = stream;
            _thumbnail.Source = bitmap;
            _thumbnailHost.Visibility = _problem is { Count: > 1 }
                ? Visibility.Collapsed
                : Visibility.Visible;
            _thumbBase64 = base64;
            UpdateThumbnailSize();
        }
        catch (Exception)
        {
            // 截图可能是空或损坏的 base64；正文仍应正常显示。
        }
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            IntPtr hwnd = WindowChrome.GetHwnd(this);
            if (hwnd == IntPtr.Zero) return;
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 旧版 Windows 或 DWM 不可用：忽略，保留方形窗口。
        }
    }

    private static Brush GetSystemAccent()
    {
        try
        {
            var color = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new SolidColorBrush(color);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush Solid(byte a, byte r, byte g, byte b)
        => new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}