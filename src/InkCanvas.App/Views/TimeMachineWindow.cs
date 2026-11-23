using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Line = Microsoft.UI.Xaml.Shapes.Line;
using System.Diagnostics;
using System.Numerics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace InkCanvas.App.Views;

/// <summary>
/// 板书时光机：基于 <see cref="InkTimeline"/> 的笔画级回放。使用独立的回放控制器渲染，
/// 绝不修改原记录；退出时恢复进入前的页与背景。
/// <para>
/// 复用 <see cref="ReplayClock"/> 做倍速计时，用 <see cref="InkTimeline.BuildFrame"/> 提供
/// 任意时刻、任意页的笔画。工具栏支持播放/暂停/停止、上一笔/下一笔、倍速 0.5/1/2/4/8，
/// 底部进度条拖动定位并显示当前时间 / 总时长、页码切换。
/// </para>
/// <para>
/// 进度条下方的书签轨道按 <see cref="SessionBookmark.TimeMs"/> 定位：单击跳转到该时刻，
/// 右键重命名 / 删除并落盘；播放时高亮最近一个书签。
/// </para>
/// <para>
/// 「导出视频」通过 <see cref="TimeMachineExporter"/> 把所选时间段编码为 MP4（Media Foundation H.264），
/// 导出期间显示进度与取消按钮，完成后打开文件所在目录。
/// </para>
/// </summary>
public sealed class TimeMachineWindow : Window
{
    private const double BookmarkTrackWidth = 320;
    private const double BookmarkMarkerWidth = 6;
    private const double BookmarkIdleOpacity = 0.65;

    private readonly ClassroomSession _session;
    private readonly BoardService _board;
    private readonly InkTimeline _timeline;
    private readonly ReplayClock _clock;
    private readonly InkController _replayController = new();
    private readonly InkSurface _surface = new() { IsHitTestVisible = false };
    private readonly CanvasControl _nativeCanvas = new()
    {
        IsHitTestVisible = false,
        ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
    };
    private readonly AiTextSceneRenderer _nativeTextRenderer = new();
    private readonly FormulaSceneRenderer _nativeFormulaRenderer = new();
    private IReadOnlyList<TimelineAiObject> _nativeFrame = Array.Empty<TimelineAiObject>();
    private Matrix3x2 _nativeView = Matrix3x2.Identity;
    private string _lastNativeKey = "";
    private readonly Canvas _objectLayer = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, BitmapImage> _objectBitmaps = new(StringComparer.Ordinal);
    private readonly List<InMemoryRandomAccessStream> _objectStreams = new();
    private readonly HashSet<string> _objectLoading = new(StringComparer.Ordinal);
    private string _lastObjectKey = "";
    private readonly DispatcherQueueTimer _timer;
    private readonly TouchIconButton _playPause;
    private readonly Slider _progress;
    private readonly Canvas _bookmarkCanvas = new() { Height = 16, Width = BookmarkTrackWidth };
    private readonly TextBlock _bookmarkLabel = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 72,
        FontSize = 12,
        Opacity = 0.75,
    };
    private readonly List<(SessionBookmark Bookmark, Border Marker)> _bookmarkMarkers = new();
    private double _appliedViewScale = 1;
    private double _appliedViewTx;
    private double _appliedViewTy;
    private readonly TextBlock _timeLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 132 };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 96 };
    private readonly ComboBox _pageCombo = new() { Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly TouchIconButton _exportButton;
    private readonly TouchIconButton _cancelExport;
    private readonly TouchIconButton _retryExport;
    private readonly TouchIconButton _addBookmark;
    private readonly ToggleButton _boardToggle;
    private readonly ProgressBar _exportProgress = new()
    {
        Width = 160,
        Height = 4,
        Minimum = 0,
        Maximum = 1,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _exportStatus = new()
    {
        FontSize = 12,
        Opacity = 0.75,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _background = new();
    private readonly Image[] _pptImages =
    {
        new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false },
        new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false },
    };
    private readonly Canvas _gridCanvas = new() { IsHitTestVisible = false };
    private readonly HashSet<string> _failedScreenshotPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _pageCount;
    private readonly int _savedIndex;
    private readonly BoardBackground _savedBackground;
    private int _activeImage;
    private string _activeScreenshotPath = "";
    private string _pendingScreenshotPath = "";
    private string _gridKey;
    private DateTime _lastTick;
    private bool _suppressSeek;
    private int _activeBookmark = -2;
    private CancellationTokenSource _exportCts;
    private CancellationTokenSource _seekCts;
    private int _seekVersion;
    private volatile bool _seekInFlight;
    private readonly SemaphoreSlim _seekGate = new(1, 1);
    private ExportOptions _lastExportOptions;
    private bool _boardOnly;
    private readonly ComboBox _speed;
    private readonly ComboBox _zoom;
    private readonly Canvas _keyframeCanvas = new() { Height = 8, Width = BookmarkTrackWidth };
    private double _timelineZoom = 1;

    public TimeMachineWindow(ClassroomSession session, BoardService board, int theme)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _timeline = new InkTimeline(_session);
        // 旧会话（或录制中尚未落盘）没有书签时按事件规则补齐。
        _timeline.ApplyAutoBookmarks();
        _clock = new ReplayClock(TimeSpan.FromMilliseconds(_timeline.Duration));

        _savedIndex = _board.CurrentIndex;
        _savedBackground = _board.Background;
        _pageCount = Math.Max(_board.PageCount, MaxPageInSession() + 1);

        _replayController.RecognizeShapes = false;
        _replayController.MultiTouchEnabled = false;
        _replayController.Tool = InkTool.Select;
        _surface.Controller = _replayController;
        foreach (Image image in _pptImages)
        {
            image.ImageOpened += OnPptImageOpened;
            image.ImageFailed += OnPptImageFailed;
        }

        Title = "板书时光机";

        _playPause = Icon(Glyphs.Play, "播放 / 暂停");
        _playPause.Click += (_, _) =>
        {
            _seekCts?.Cancel();
            _clock.Toggle();
            SyncPlayGlyph();
        };
        var stop = Icon(Glyphs.Restart, "停止并从开头重新开始");
        stop.Click += (_, _) => SeekAsync(0);

        var previous = Icon(Glyphs.ChevronLeft, "上一笔");
        previous.Click += (_, _) => StepEvent(-1);
        var next = Icon(Glyphs.ChevronRight, "下一笔");
        next.Click += (_, _) => StepEvent(+1);

        var previousEvent = Icon(Glyphs.ChevronUp, "上一事件");
        previousEvent.Click += (_, _) => StepEvent(-1);
        var nextEvent = Icon(Glyphs.ChevronDown, "下一事件");
        nextEvent.Click += (_, _) => StepEvent(+1);

        _boardToggle = new ToggleButton
        {
            Content = BoardOnlyLabel(false),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 40,
        };
        ToolTipService.SetToolTip(_boardToggle, "切换「完整课堂 / 仅板书」：仅板书时隐藏 PPT 截图");
        _boardToggle.Checked += (_, _) => SetBoardOnly(true);
        _boardToggle.Unchecked += (_, _) => SetBoardOnly(false);

        _speed = new ComboBox { Width = 96, SelectedIndex = 1, VerticalAlignment = VerticalAlignment.Center };
        foreach (var s in new[] { "0.5x", "1x", "2x", "4x", "8x" }) _speed.Items.Add(s);
        _speed.SelectionChanged += (_, _) => _clock.Speed = _speed.SelectedIndex switch
        {
            0 => 0.5f,
            2 => 2f,
            3 => 4f,
            4 => 8f,
            _ => 1f,
        };

        _zoom = new ComboBox { Width = 76, SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var z in new[] { "1x", "2x", "4x" }) _zoom.Items.Add(z);
        ToolTipService.SetToolTip(_zoom, "时间轴缩放：长课可按 1× / 2× / 4× 展开，便于精确定位");
        _zoom.SelectionChanged += (_, _) =>
        {
            _timelineZoom = _zoom.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 };
            ApplyTimelineZoom();
        };

        var close = Icon(Glyphs.Close, "关闭时光机");
        close.Click += (_, _) => Close();

        _exportButton = Icon(Glyphs.Download, "导出视频（MP4）");
        _exportButton.Click += OnExportClicked;
        _cancelExport = Icon(Glyphs.Close, "取消导出");
        _cancelExport.Visibility = Visibility.Collapsed;
        _cancelExport.Click += (_, _) => _exportCts?.Cancel();

        _retryExport = Icon(Glyphs.Restart, "重试上次失败的导出");
        _retryExport.Visibility = Visibility.Collapsed;
        _retryExport.Click += async (_, _) =>
        {
            if (_lastExportOptions is not null) await RunExportAsync(_lastExportOptions);
        };

        _addBookmark = Icon(Glyphs.Add, "在当前位置添加书签");
        _addBookmark.Click += async (_, _) => await AddBookmarkAsync();

        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topBar.Children.Add(_playPause);
        topBar.Children.Add(stop);
        topBar.Children.Add(previous);
        topBar.Children.Add(next);
        topBar.Children.Add(previousEvent);
        topBar.Children.Add(nextEvent);
        topBar.Children.Add(_speed);
        topBar.Children.Add(_boardToggle);
        topBar.Children.Add(_timeLabel);
        topBar.Children.Add(_exportButton);
        topBar.Children.Add(_retryExport);
        topBar.Children.Add(_cancelExport);
        topBar.Children.Add(close);

        _progress = new Slider { Minimum = 0, Maximum = 1000, VerticalAlignment = VerticalAlignment.Center };
        _progress.ValueChanged += (_, e) =>
        {
            if (_suppressSeek) return;
            SeekAsync((long)(e.NewValue / 1000.0 * _clock.DurationMs));
        };

        for (int i = 0; i < _pageCount; i++) _pageCombo.Items.Add($"第 {i + 1} 页");
        _pageCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressSeek) return;
            int target = _pageCombo.SelectedIndex;
            if (target < 0) return;
            SeekAsync(FirstEventTimeForPage(target));
        };

        var bottomBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _progress.Width = BookmarkTrackWidth;
        bottomBar.Children.Add(_progress);
        bottomBar.Children.Add(_pageLabel);
        bottomBar.Children.Add(_pageCombo);

        var bookmarkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bookmarkRow.Children.Add(_addBookmark);
        bookmarkRow.Children.Add(new TextBlock
        {
            Text = "书签",
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        });
        bookmarkRow.Children.Add(_bookmarkCanvas);
        bookmarkRow.Children.Add(_bookmarkLabel);

        var exportRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        exportRow.Children.Add(new TextBlock
        {
            Text = "导出",
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        });
        exportRow.Children.Add(_exportProgress);
        exportRow.Children.Add(_exportStatus);
        exportRow.Children.Add(_retryExport);

        var timelineRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        timelineRow.Children.Add(new TextBlock
        {
            Text = "时间轴",
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        });
        timelineRow.Children.Add(_keyframeCanvas);
        timelineRow.Children.Add(_zoom);

        var shortcutHint = new TextBlock
        {
            Text = "空格 播放/暂停 · ← → 逐事件 · ↑ ↓ 章节 · + / - 倍速 · Home 回到开头",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };

        var bottomPanel = new StackPanel { Spacing = 4 };
        bottomPanel.Children.Add(bookmarkRow);
        bottomPanel.Children.Add(bottomBar);
        bottomPanel.Children.Add(timelineRow);
        bottomPanel.Children.Add(exportRow);
        bottomPanel.Children.Add(shortcutHint);

        var topHost = Wrap(topBar, bottom: true);
        var bottomHost = Wrap(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = bottomPanel,
        }, bottom: false);

        var canvasHost = new Grid();
        canvasHost.Children.Add(_background);
        canvasHost.Children.Add(_pptImages[0]);
        canvasHost.Children.Add(_pptImages[1]);
        canvasHost.Children.Add(_gridCanvas);
        canvasHost.Children.Add(_surface);
        _nativeCanvas.Draw += OnDrawNativeObjects;
        canvasHost.Children.Add(_nativeCanvas);
        // 旧 PNG AI 卡片叠加层：与笔画同样的视图变换，不参与命中测试。
        canvasHost.Children.Add(_objectLayer);
        // 窗口尺寸变化时按新尺寸重画网格；同帧内背景层一起刷新，避免切换时闪白。
        canvasHost.SizeChanged += (_, _) =>
        {
            _gridKey = null;
            _lastObjectKey = "";
            _lastNativeKey = "";
            _nativeCanvas.Invalidate();
            RenderFrame();
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(topHost, 0);
        Grid.SetRow(canvasHost, 1);
        Grid.SetRow(bottomHost, 2);
        root.Children.Add(topHost);
        root.Children.Add(canvasHost);
        root.Children.Add(bottomHost);
        Content = root;
        AttachKeyboardShortcuts(root);

        WindowChrome.MakeBorderless(this, topmost: false, resizable: true);
        WindowChrome.CenterOnPrimary(this, 980, 680);
        WindowChrome.ApplyTheme(this, theme);

        RebuildBookmarks();
        RebuildKeyframes();

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => OnTick();
        _lastTick = DateTime.UtcNow;
        _timer.Start();

        Closed += OnClosed;
        Activated += (_, _) => Motion.PlayEntrance(topHost, 16f);

        RenderFrame();
        if (_timeline.Duration > 0) { _clock.Play(); SyncPlayGlyph(); }
    }

    private int MaxPageInSession()
    {
        int max = 0;
        foreach (var e in _session.Events)
            if (e.PageIndex > max) max = e.PageIndex;
        return max;
    }

    private long FirstEventTimeForPage(int page)
    {
        foreach (var e in _session.Events)
        {
            if (e.Kind == TimelineEventKind.PageChanged && e.PageIndex == page) return e.TimeMs;
        }
        foreach (var e in _session.Events)
        {
            if (e.PageIndex == page) return e.TimeMs;
        }
        return 0;
    }

    private static Border Wrap(UIElement child, bool bottom)
    {
        return new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["IcStrokeBrush"],
            BorderThickness = new Thickness(0, bottom ? 0 : 1, 0, bottom ? 1 : 0),
            Padding = new Thickness(12, 8, 12, 8),
            Child = child,
        };
    }

    private static TouchIconButton Icon(string glyph, string tooltip)
    {
        var button = new TouchIconButton { Glyph = glyph };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void SyncPlayGlyph() => _playPause.Glyph = _clock.IsPlaying ? Glyphs.Pause : Glyphs.Play;

    // ---- 键盘与时间轴缩放 ---------------------------------------------------

    /// <summary>
    /// 键盘操作：空格播放/暂停、方向键逐事件、上下键跳章节、加减号切换倍速、Home 回到开头。
    /// 全部标记为已处理，避免同时触发滚动或按钮默认行为。
    /// </summary>
    private void AttachKeyboardShortcuts(UIElement root)
    {
        AddAccelerator(root, VirtualKey.Space, () =>
        {
            _seekCts?.Cancel();
            _clock.Toggle();
            SyncPlayGlyph();
        });
        AddAccelerator(root, VirtualKey.Left, () => StepEvent(-1));
        AddAccelerator(root, VirtualKey.Right, () => StepEvent(+1));
        AddAccelerator(root, VirtualKey.Up, () => JumpBookmark(-1));
        AddAccelerator(root, VirtualKey.Down, () => JumpBookmark(+1));
        AddAccelerator(root, VirtualKey.Home, () => SeekAsync(0));
        AddAccelerator(root, VirtualKey.Add, () => CycleSpeed(+1));
        AddAccelerator(root, VirtualKey.Subtract, () => CycleSpeed(-1));
    }

    private static void AddAccelerator(UIElement root, VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        root.KeyboardAccelerators.Add(accelerator);
    }

    private void CycleSpeed(int direction)
    {
        int index = Math.Clamp(_speed.SelectedIndex + direction, 0, _speed.Items.Count - 1);
        _speed.SelectedIndex = index;
    }

    private void JumpBookmark(int direction)
    {
        var bookmarks = _session.Bookmarks;
        if (bookmarks is null || bookmarks.Count == 0) return;

        long time = (long)_clock.ElapsedMs;
        int index = direction > 0
            ? bookmarks.FindIndex(b => b is not null && b.TimeMs > time)
            : bookmarks.FindLastIndex(b => b is not null && b.TimeMs < time);
        if (index < 0) return;

        SeekAsync(bookmarks[index].TimeMs);
    }

    /// <summary>按缩放档位同步进度条、书签轨道与关键位置刻度。</summary>
    private void ApplyTimelineZoom()
    {
        double width = BookmarkTrackWidth * _timelineZoom;
        _progress.Width = width;
        _bookmarkCanvas.Width = width;
        _keyframeCanvas.Width = width;
        RebuildBookmarks();
        RebuildKeyframes();
    }

    /// <summary>
    /// 关键位置刻度：每 10% 时长一条刻度并标注时间，长课时配合缩放档位做快速粗定位。
    /// 不渲染画面缩略图，避免为长会话构建额外位图内存。
    /// </summary>
    private void RebuildKeyframes()
    {
        _keyframeCanvas.Children.Clear();
        long duration = Math.Max(1, _timeline.Duration);
        double width = _keyframeCanvas.Width > 0 ? _keyframeCanvas.Width : BookmarkTrackWidth;
        var tickBrush = (Brush)Application.Current.Resources["IcStrokeBrush"];

        for (int i = 0; i <= 10; i++)
        {
            var tick = new Border
            {
                Width = 1,
                Height = i % 5 == 0 ? 8 : 5,
                Background = tickBrush,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            double x = width / 10.0 * i;
            Canvas.SetLeft(tick, Math.Clamp(x, 0, Math.Max(0, width - 1)));
            ToolTipService.SetToolTip(tick, Format(duration / 10 * i));
            _keyframeCanvas.Children.Add(tick);
        }
    }

    // ---- 书签轨道 ---------------------------------------------------------

    private void RebuildBookmarks()
    {
        _bookmarkCanvas.Children.Clear();
        _bookmarkMarkers.Clear();
        _activeBookmark = -2;

        var bookmarks = _session.Bookmarks ??= new List<SessionBookmark>();
        long duration = Math.Max(1, _timeline.Duration);
        double width = _bookmarkCanvas.Width > 0 ? _bookmarkCanvas.Width : BookmarkTrackWidth;

        var idleBrush = (Brush)Application.Current.Resources["IcStrokeBrush"];

        for (int i = 0; i < bookmarks.Count; i++)
        {
            SessionBookmark bookmark = bookmarks[i];
            if (bookmark is null) continue;
            int index = i;

            var marker = new Border
            {
                Width = BookmarkMarkerWidth,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = idleBrush,
                Opacity = BookmarkIdleOpacity,
            };
            ToolTipService.SetToolTip(marker, _timeline.DescribeBookmark(bookmark));

            double x = (double)bookmark.TimeMs / duration * width - BookmarkMarkerWidth / 2;
            Canvas.SetLeft(marker, Math.Clamp(x, 0, Math.Max(0, width - BookmarkMarkerWidth)));
            Canvas.SetTop(marker, 1);

            marker.PointerEntered += (_, _) => marker.Opacity = 1;
            marker.PointerExited += (_, _) =>
                marker.Opacity = ReferenceEquals(marker.Background,
                    Application.Current.Resources["IcAccentBrush"]) ? 1 : BookmarkIdleOpacity;
            marker.PointerPressed += (_, e) => OnBookmarkPressed(index, e);

            _bookmarkCanvas.Children.Add(marker);
            _bookmarkMarkers.Add((bookmark, marker));
        }

        _bookmarkLabel.Text = $"{bookmarks.Count} 个";
        UpdateBookmarkHighlight((long)_clock.ElapsedMs, force: true);
    }

    private static string DisplayName(SessionBookmark bookmark)
        => string.IsNullOrWhiteSpace(bookmark.Name) ? Format(bookmark.TimeMs) : bookmark.Name;

    private void OnBookmarkPressed(int index, PointerRoutedEventArgs e)
    {
        var bookmarks = _session.Bookmarks;
        if (bookmarks is null || index < 0 || index >= bookmarks.Count) return;

        if (e.GetCurrentPoint(_bookmarkCanvas).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _ = RenameBookmarkAsync(index);
            return;
        }

        SeekAsync(bookmarks[index].TimeMs);
        e.Handled = true;
    }

    private async Task AddBookmarkAsync()
    {
        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;

        long time = (long)_clock.ElapsedMs;
        int manualCount = (_session.Bookmarks?.Count(b => b is not null && !b.Auto) ?? 0) + 1;
        string defaultName = $"书签 {manualCount}";
        int index = _timeline.AddBookmark(time, defaultName, "manual");
        SaveSession();
        RebuildBookmarks();

        var input = new TextBox
        {
            Text = defaultName,
            MinWidth = 240,
            PlaceholderText = "书签名称",
        };
        var dialog = new ContentDialog
        {
            Title = $"已添加书签（{Format(time)} · 第 {_timeline.PageAt(time) + 1} 页）",
            Content = input,
            PrimaryButtonText = "保存名称",
            CloseButtonText = "保留默认名",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(input.Text)) return;
        if (!_timeline.RenameBookmark(index, input.Text)) return;
        SaveSession();
        RebuildBookmarks();
    }

    private async Task RenameBookmarkAsync(int index)
    {
        var bookmarks = _session.Bookmarks;
        if (bookmarks is null || index < 0 || index >= bookmarks.Count) return;
        if (_bookmarkCanvas.XamlRoot is null) return;

        var input = new TextBox
        {
            Text = bookmarks[index].Name ?? "",
            MinWidth = 240,
            PlaceholderText = "书签名称",
        };
        var dialog = new ContentDialog
        {
            Title = $"编辑书签（{Format(bookmarks[index].TimeMs)}）",
            Content = input,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _bookmarkCanvas.XamlRoot,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            if (!_timeline.RenameBookmark(index, input.Text)) return;
        }
        else if (result == ContentDialogResult.Secondary)
        {
            if (!_timeline.RemoveBookmark(index)) return;
        }
        else
        {
            return;
        }

        SaveSession();
        RebuildBookmarks();
    }

    private void SaveSession()
    {
        try { ClassroomSessionStore.Save(_session); }
        catch
        {
            // 落盘失败不应影响回放；书签只保留在内存中的会话里。
        }
    }

    private void UpdateBookmarkHighlight(long time, bool force = false)
    {
        int active = -1;
        for (int i = 0; i < _bookmarkMarkers.Count; i++)
        {
            if (_bookmarkMarkers[i].Bookmark.TimeMs <= time) active = i;
        }
        if (!force && active == _activeBookmark) return;
        _activeBookmark = active;

        var accent = (Brush)Application.Current.Resources["IcAccentBrush"];
        var idle = (Brush)Application.Current.Resources["IcStrokeBrush"];
        for (int i = 0; i < _bookmarkMarkers.Count; i++)
        {
            Border marker = _bookmarkMarkers[i].Marker;
            bool isActive = i == active;
            marker.Background = isActive ? accent : idle;
            marker.Opacity = isActive ? 1 : BookmarkIdleOpacity;
        }
    }

    // ---- 回放 -------------------------------------------------------------

    private void StepEvent(int delta)
    {
        if (_session.Events.Count == 0) return;
        int cutoff = _timeline.IndexAt((long)_clock.ElapsedMs);
        int target = Math.Clamp(cutoff + delta, 0, _session.Events.Count - 1);
        SeekAsync(_timeline.TimeAtEvent(target));
    }

    /// <summary>
    /// 异步定位：新请求先取消旧任务，后台重建目标帧；回调前校验 token / 版本，
    /// 只呈现最后一次目标。定位期间暂停，完成后仍停在目标帧。
    /// </summary>
    private async void SeekAsync(long targetMs)
    {
        _seekCts?.Cancel();
        var cts = new CancellationTokenSource();
        _seekCts = cts;
        int version = ++_seekVersion;
        _seekInFlight = true;

        _clock.Pause();
        SyncPlayGlyph();
        long clamped = Math.Clamp(targetMs, 0, _timeline.Duration);

        void Finish()
        {
            // 只让最后一次请求清除在途标记，避免旧请求提前放开播放帧渲染。
            if (version == _seekVersion) _seekInFlight = false;
        }

        try
        {
            await _seekGate.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Finish();
            return;
        }

        IReadOnlyList<InkStroke> visible;
        try
        {
            visible = await Task.Run(() => _timeline.BuildFrame(clamped), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Finish();
            return;
        }
        finally
        {
            _seekGate.Release();
        }

        if (!InkTimeline.ShouldApplySeek(cts.Token, version, _seekVersion))
        {
            Finish();
            return;
        }

        _clock.Seek(clamped);
        RenderFrame(clamped, visible);
        SyncPlayGlyph();
        Finish();
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        float delta = (float)(now - _lastTick).TotalMilliseconds;
        _lastTick = now;
        // 定位在后台重建帧时跳过本次渲染，避免与 BuildFrame 并发读取时间轴。
        if (!_clock.IsPlaying || _seekInFlight) return;
        _clock.Advance(delta);
        RenderFrame();
        SyncPlayGlyph();
    }

    private void RenderFrame()
    {
        long time = (long)_clock.ElapsedMs;
        RenderFrame(time, _timeline.BuildFrame(time));
    }

    private void RenderFrame(long time, IReadOnlyList<InkStroke> visible)
    {
        _replayController.Document.Clear();
        _replayController.Document.AddRange(visible);

        ClassroomScene scene = _timeline.SceneAt(time);
        ApplyBackground(scene);
        ApplyViewState(scene);
        RenderObjects(time, scene);

        int page = scene.PageIndex;
        _pageLabel.Text = $"第 {page + 1} 页 / 共 {_pageCount} 页";

        _suppressSeek = true;
        _progress.Value = _clock.Progress * 1000;
        if (page >= 0 && page < _pageCombo.Items.Count) _pageCombo.SelectedIndex = page;
        _suppressSeek = false;

        _timeLabel.Text = $"{Format(time)} / {Format(_timeline.Duration)}";
        UpdateBookmarkHighlight(time);
    }

    /// <summary>
    /// 叠加当前时刻的 AI 板书对象：原生文本/公式走矢量渲染（CanvasControl），
    /// 旧 PNG 卡片走图片层；坐标与回放控制器使用同一视图变换，不请求模型。
    /// </summary>
    private void RenderObjects(long time, ClassroomScene scene)
    {
        IReadOnlyList<TimelineAiObject> objects = _timeline.BuildObjectFrame(time);
        IReadOnlyList<TimelineAiObject> nativeObjects = objects.Where(item => item.IsNative).ToList();
        IReadOnlyList<TimelineAiObject> legacyObjects = objects.Where(item => !item.IsNative).ToList();

        double scale = double.IsFinite(scene.ViewScale) && scene.ViewScale > 0 ? scene.ViewScale : 1;
        double tx = double.IsFinite(scene.ViewTx) ? scene.ViewTx : 0;
        double ty = double.IsFinite(scene.ViewTy) ? scene.ViewTy : 0;
        Matrix3x2 view = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation((float)tx, (float)ty);

        string nativeKey = BuildNativeKey(nativeObjects, scene);
        if (!string.Equals(nativeKey, _lastNativeKey, StringComparison.Ordinal))
        {
            _lastNativeKey = nativeKey;
            _nativeFrame = nativeObjects;
            _nativeView = view;
            _nativeCanvas.Invalidate();
        }

        string key = BuildObjectKey(legacyObjects, scene);
        if (string.Equals(key, _lastObjectKey, StringComparison.Ordinal)) return;
        _lastObjectKey = key;

        _objectLayer.Children.Clear();
        foreach (TimelineAiObject item in legacyObjects)
        {
            FrameworkElement visual = item.HasRenderPng
                ? CreateObjectImage(item)
                : CreateObjectPlaceholder(item);

            // 旧 PNG 卡片：元素保持世界尺寸，由 RenderTransform 一次性合成
            // 「对象变换 × 回放视图」，与原生文本/公式的 frame.Transform * view 同源。
            Matrix3x2 world = BoardElementMath.Read(item.HasTransform ? item.Transform : null);
            Matrix3x2 render = Matrix3x2.CreateTranslation((float)item.Left, (float)item.Top) * world * view;
            visual.Width = Math.Max(1, item.Width);
            visual.Height = Math.Max(1, item.Height);
            visual.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            visual.RenderTransform = new MatrixTransform
            {
                Matrix = new Matrix(render.M11, render.M12, render.M21, render.M22, render.M31, render.M32),
            };
            Canvas.SetLeft(visual, 0);
            Canvas.SetTop(visual, 0);
            _objectLayer.Children.Add(visual);
        }
    }

    private void OnDrawNativeObjects(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_nativeFrame.Count == 0) return;

        foreach (TimelineAiObject item in _nativeFrame)
        {
            if (item.IsFormula)
                _nativeFormulaRenderer.Draw(
                    args.DrawingSession, new TimelineNativeFormulaElement(item), _nativeView);
            else
                _nativeTextRenderer.Draw(
                    args.DrawingSession, new TimelineNativeTextElement(item), _nativeView);
        }
    }

    private FrameworkElement CreateObjectImage(TimelineAiObject item)
    {
        var image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        string key = ObjectBitmapKey(item);
        if (_objectBitmaps.TryGetValue(key, out BitmapImage cached))
        {
            image.Source = cached;
        }
        else if (_objectLoading.Add(key))
        {
            _ = LoadObjectBitmapAsync(key, item.RenderPngBase64, image);
        }

        return image;
    }

    private FrameworkElement CreateObjectPlaceholder(TimelineAiObject item)
    {
        string label = string.IsNullOrWhiteSpace(item.Kind) ? "AI 对象" : item.Kind;
        if (item.RenderMissing) label += " · 回放缺失";

        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x00, 0x67, 0xC0)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x00, 0x67, 0xC0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
    }

    private async Task LoadObjectBitmapAsync(string key, string base64, Image target)
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
            _objectBitmaps[key] = bitmap;
            _objectStreams.Add(stream);
            if (target is not null) target.Source = bitmap;
        }
        catch (Exception)
        {
            // 单张 PNG 解码失败时保留占位框，不影响其他对象与笔画回放。
        }
        finally
        {
            _objectLoading.Remove(key);
        }
    }

    private static string ObjectBitmapKey(TimelineAiObject item)
        => item.Id + ":" + item.RenderPngBase64.Length + ":"
           + (item.RenderPngBase64.Length > 0 ? item.RenderPngBase64[0] : '\0');

    private static string BuildObjectKey(IReadOnlyList<TimelineAiObject> objects, ClassroomScene scene)
    {
        if (objects.Count == 0) return "empty";

        var builder = new System.Text.StringBuilder(64 + objects.Count * 24);
        builder.Append(scene.ViewScale.ToString("0.###")).Append('|')
            .Append(scene.ViewTx.ToString("0.##")).Append('|')
            .Append(scene.ViewTy.ToString("0.##"));
        foreach (TimelineAiObject item in objects)
        {
            builder.Append('|').Append(item.Id)
                .Append(':').Append(item.Left.ToString("0.##"))
                .Append(',').Append(item.Top.ToString("0.##"))
                .Append(',').Append(item.Width.ToString("0.##"))
                .Append(',').Append(item.Height.ToString("0.##"))
                .Append(':').Append(FormatTransform(item))
                .Append(':').Append(item.RenderPngBase64.Length)
                .Append(item.RenderMissing ? "m" : "p");
        }

        return builder.ToString();
    }

    private static string FormatTransform(TimelineAiObject item)
    {
        if (!item.HasTransform) return "identity";
        return string.Join(",", item.Transform.Select(value => value.ToString("0.###")));
    }

    private static string BuildNativeKey(IReadOnlyList<TimelineAiObject> objects, ClassroomScene scene)
    {
        var builder = new System.Text.StringBuilder(96 + objects.Count * 48);
        builder.Append(scene.ViewScale.ToString("0.###")).Append('|')
            .Append(scene.ViewTx.ToString("0.##")).Append('|')
            .Append(scene.ViewTy.ToString("0.##"));
        foreach (TimelineAiObject item in objects)
        {
            builder.Append('|').Append(item.Id)
                .Append(':').Append(item.Kind)
                .Append(':').Append(item.Content)
                .Append(':').Append(item.Title)
                .Append(':').Append(item.Author)
                .Append(':').Append(item.Dynasty)
                .Append(':').Append(item.Latex)
                .Append(':').Append(item.FontFamily)
                .Append(':').Append(item.FontSize.ToString("R"))
                .Append(':').Append(item.Color)
                .Append(':').Append(item.LineHeight.ToString("R"))
                .Append(':').Append(item.Align)
                .Append(':').Append(item.Columns)
                .Append(':').Append(item.GroupId)
                .Append(':').Append(item.Left.ToString("0.##"))
                .Append(',').Append(item.Top.ToString("0.##"))
                .Append(',').Append(item.Width.ToString("0.##"))
                .Append(',').Append(item.Height.ToString("0.##"))
                .Append(',').Append(item.LayerOrder?.ToString() ?? "")
                .Append(':').Append(FormatTransform(item));
        }

        return builder.ToString();
    }

    private static string BoardOnlyLabel(bool boardOnly) => boardOnly ? "仅板书" : "完整课堂";

    private void SetBoardOnly(bool boardOnly)
    {
        _boardToggle.Content = BoardOnlyLabel(boardOnly);
        if (_boardOnly == boardOnly)
        {
            if (boardOnly) HideAllPptImages();
            return;
        }

        _boardOnly = boardOnly;
        // 清空已加载路径：切回完整课堂时即使同一张截图也会重新加载，切换即时生效。
        _activeScreenshotPath = "";
        if (boardOnly) HideAllPptImages();
        RenderFrame();
    }

    // ---- 背景层：PPT 截图 + 网格 -----------------------------------------

    /// <summary>
    /// 同帧更新背景：先纯色，再尝试叠 PPT 截图，最后按背景枚举画网格。
    /// 截图不可用时仅保留纯色，不会留下上一页的图片。
    /// </summary>
    /// <summary>
    /// 定位后按时间轴记录的视图状态（缩放 + 平移）设置回放控制器；
    /// 与当前值相同则跳过，避免每帧重复赋值。
    /// </summary>
    private void ApplyViewState(ClassroomScene scene)
    {
        double scale = double.IsFinite(scene.ViewScale) && scene.ViewScale > 0 ? scene.ViewScale : 1;
        double tx = double.IsFinite(scene.ViewTx) ? scene.ViewTx : 0;
        double ty = double.IsFinite(scene.ViewTy) ? scene.ViewTy : 0;
        if (Math.Abs(scale - _appliedViewScale) < 0.0001
            && Math.Abs(tx - _appliedViewTx) < 0.01
            && Math.Abs(ty - _appliedViewTy) < 0.01) return;

        _appliedViewScale = scale;
        _appliedViewTx = tx;
        _appliedViewTy = ty;
        _replayController.SetView(
            Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation((float)tx, (float)ty));
    }

    private void ApplyBackground(ClassroomScene scene)
    {
        _background.Background = new SolidColorBrush(BackgroundColor(scene.Background));
        UpdatePptImage(_boardOnly ? "" : scene.PptScreenshotPath);
        UpdateGrid(scene.Background);
    }

    private void UpdatePptImage(string path)
    {
        bool available = !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && !_failedScreenshotPaths.Contains(path);

        if (!available)
        {
            if (_activeScreenshotPath.Length == 0) return;
            _activeScreenshotPath = "";
            HideAllPptImages();
            return;
        }

        if (string.Equals(_activeScreenshotPath, path, StringComparison.OrdinalIgnoreCase)) return;

        int nextIndex = 1 - _activeImage;
        Image next = _pptImages[nextIndex];

        try
        {
            var bitmap = new BitmapImage { UriSource = new Uri(path) };
            next.Source = bitmap;
            next.Visibility = Visibility.Visible;
            Canvas.SetZIndex(next, 1);
            Canvas.SetZIndex(_pptImages[_activeImage], 0);
            _activeImage = nextIndex;
            _activeScreenshotPath = path;
            _pendingScreenshotPath = path;
        }
        catch
        {
            _failedScreenshotPaths.Add(path);
            next.Source = null;
            next.Visibility = Visibility.Collapsed;
            _activeScreenshotPath = "";
        }
    }

    /// <summary>新图解码完成后释放下面的旧图，解码期间旧图仍在，避免切页闪白。</summary>
    private void OnPptImageOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _pptImages[_activeImage])) return;
        Image old = _pptImages[1 - _activeImage];
        old.Source = null;
        old.Visibility = Visibility.Collapsed;
    }

    /// <summary>图片加载失败时静默回退纯色，并记住该路径避免每帧重复尝试。</summary>
    private void OnPptImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _pptImages[_activeImage])) return;
        string path = _pendingScreenshotPath;
        if (!string.IsNullOrEmpty(path)) _failedScreenshotPaths.Add(path);
        _activeScreenshotPath = "";
        HideAllPptImages();
        Core.Diagnostics.FileLog.WriteLogToFile(
            $"TimeMachine: PPT screenshot load failed {path}", Core.Diagnostics.LogType.Info);
    }

    private void HideAllPptImages()
    {
        foreach (Image image in _pptImages)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateGrid(string background)
    {
        double width = _gridCanvas.ActualWidth;
        double height = _gridCanvas.ActualHeight;
        string key = $"{background}|{width:0}|{height:0}";
        if (string.Equals(key, _gridKey, StringComparison.Ordinal)) return;
        _gridKey = key;

        _gridCanvas.Children.Clear();
        ReplayBackgroundStyle style = ReplayBackgrounds.Resolve(background);
        if (!style.HasGrid || width <= 0 || height <= 0) return;

        const double spacing = 40;
        var gridBrush = new SolidColorBrush(style.IsLight
            ? Windows.UI.Color.FromArgb(0x2E, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

        for (double x = spacing; x < width; x += spacing)
            _gridCanvas.Children.Add(Line(x, 0, x, height, gridBrush, 0.5));
        for (double y = spacing; y < height; y += spacing)
            _gridCanvas.Children.Add(Line(0, y, width, y, gridBrush, 0.5));

        if (!style.HasAxes) return;

        var axisBrush = new SolidColorBrush(style.IsLight
            ? Windows.UI.Color.FromArgb(0x88, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        double centerX = Math.Round(width / 2) + 0.5;
        double centerY = Math.Round(height / 2) + 0.5;
        _gridCanvas.Children.Add(Line(centerX, 0, centerX, height, axisBrush, 1.4));
        _gridCanvas.Children.Add(Line(0, centerY, width, centerY, axisBrush, 1.4));
    }

    private static Line Line(double x1, double y1, double x2, double y2, Brush brush, double thickness)
        => new()
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false,
        };

    private static string Format(double ms) => TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss");

    // ---- MP4 导出 ---------------------------------------------------------

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (_exportCts is not null) return;

        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;

        long durationMs = Math.Max(0, _timeline.Duration);
        double durationSec = Math.Max(1, durationMs / 1000.0);

        double actualWidth = _gridCanvas.ActualWidth;
        double actualHeight = _gridCanvas.ActualHeight;
        int sourceWidth = 1920;
        int sourceHeight = 1080;
        if (actualWidth >= 320 && actualHeight >= 320)
        {
            sourceWidth = Math.Clamp((int)Math.Round(actualWidth / 2) * 2, 320, 3840);
            sourceHeight = Math.Clamp((int)Math.Round(actualHeight / 2) * 2, 320, 3840);
        }

        var startBox = new NumberBox
        {
            Header = "开始（秒）",
            Value = 0,
            Minimum = 0,
            Maximum = durationSec,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 240,
        };
        var endBox = new NumberBox
        {
            Header = "结束（秒）",
            Value = durationSec,
            Minimum = 0,
            Maximum = durationSec,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 240,
        };
        var audio = new CheckBox
        {
            Content = "同步录制讲解音频（系统声音）",
            IsChecked = false,
        };
        var audioHint = new TextBlock
        {
            Text = "勾选后按所选时长实时录制系统声音，导出耗时会相应增加。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        var hint = new TextBlock
        {
            Text = $"总时长 {Format(durationMs)}，默认导出全段。姓名、头像与讲解音频均在本机处理，不会上传。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var resolution = new ComboBox { Header = "分辨率", Width = 220, SelectedIndex = 0 };
        resolution.Items.Add("1920 × 1080");
        resolution.Items.Add("1280 × 720");
        resolution.Items.Add($"源尺寸（{sourceWidth} × {sourceHeight}）");

        var speedCombo = new ComboBox { Header = "倍速", Width = 120, SelectedIndex = 0 };
        speedCombo.Items.Add("1x");
        speedCombo.Items.Add("2x");
        speedCombo.Items.Add("4x");

        var boardOnly = new CheckBox
        {
            Content = "仅导出板书（不含 PPT 截图）",
            IsChecked = false,
        };

        var showCursor = new CheckBox
        {
            Content = "显示书写光标",
            IsChecked = false,
        };
        var showChapter = new CheckBox
        {
            Content = "显示章节信息",
            IsChecked = false,
        };

        // 按章节选片段：以书签时间为区间边界，仍可手动微调开始 / 结束秒。
        var chapterCombo = new ComboBox { Header = "片段（按章节）", Width = 240, SelectedIndex = 0 };
        chapterCombo.Items.Add("完整会话");
        var chapterList = _session.Bookmarks ??= new List<SessionBookmark>();
        foreach (var bookmark in chapterList)
        {
            if (bookmark is null) continue;
            chapterCombo.Items.Add(_timeline.DescribeBookmark(bookmark, 18));
        }

        var hintText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        (int Width, int Height) SelectedSize() => resolution.SelectedIndex switch
        {
            1 => (1280, 720),
            2 => (sourceWidth, sourceHeight),
            _ => (1920, 1080),
        };

        double SelectedSpeed() => speedCombo.SelectedIndex switch
        {
            1 => 2.0,
            2 => 4.0,
            _ => 1.0,
        };

        void UpdateHint()
        {
            double startValue = double.IsNaN(startBox.Value) ? 0 : startBox.Value;
            double endValue = double.IsNaN(endBox.Value) ? durationSec : endBox.Value;
            long startMsPreview = (long)Math.Round(Math.Clamp(startValue, 0, durationSec) * 1000);
            long endMsPreview = (long)Math.Round(Math.Clamp(endValue, 0, durationSec) * 1000);
            if (endMsPreview <= startMsPreview)
            {
                hintText.Text = "结束时间必须大于开始时间。";
                return;
            }

            (int width, int height) = SelectedSize();
            try
            {
                var preview = new ExportOptions(
                    "preview.mp4", startMsPreview, endMsPreview, Fps: 30,
                    Width: width, Height: height, Speed: SelectedSpeed(),
                    BoardOnly: boardOnly.IsChecked == true,
                    ShowCursor: showCursor.IsChecked == true,
                    ShowChapterInfo: showChapter.IsChecked == true);
                int frames = TimeMachineExporter.EstimateFrameCount(preview);
                double outputSeconds = TimeMachineExporter.EffectiveDurationMs(preview) / 1000.0;
                hintText.Text = $"预计 {frames} 帧 · 输出时长 {outputSeconds:0.0} 秒 · "
                    + $"{width}×{height} · {SelectedSpeed():0.#}x"
                    + (boardOnly.IsChecked == true ? " · 仅板书" : "");
            }
            catch (Exception ex)
            {
                hintText.Text = ex.Message;
            }
        }

        startBox.ValueChanged += (_, _) => UpdateHint();
        endBox.ValueChanged += (_, _) => UpdateHint();
        resolution.SelectionChanged += (_, _) => UpdateHint();
        speedCombo.SelectionChanged += (_, _) => UpdateHint();
        boardOnly.Checked += (_, _) => UpdateHint();
        boardOnly.Unchecked += (_, _) => UpdateHint();
        showCursor.Checked += (_, _) => UpdateHint();
        showCursor.Unchecked += (_, _) => UpdateHint();
        showChapter.Checked += (_, _) => UpdateHint();
        showChapter.Unchecked += (_, _) => UpdateHint();
        chapterCombo.SelectionChanged += (_, _) =>
        {
            int index = chapterCombo.SelectedIndex - 1;
            if (index < 0 || index >= chapterList.Count) return;

            SessionBookmark bookmark = chapterList[index];
            if (bookmark is null) return;
            startBox.Value = Math.Clamp(bookmark.TimeMs / 1000.0, 0, durationSec);
            long next = index + 1 < chapterList.Count && chapterList[index + 1] is not null
                ? chapterList[index + 1].TimeMs
                : durationMs;
            endBox.Value = Math.Clamp(next / 1000.0, 0, durationSec);
            UpdateHint();
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(hint);
        panel.Children.Add(startBox);
        panel.Children.Add(endBox);
        panel.Children.Add(resolution);
        panel.Children.Add(speedCombo);
        panel.Children.Add(chapterCombo);
        panel.Children.Add(boardOnly);
        panel.Children.Add(showCursor);
        panel.Children.Add(showChapter);
        panel.Children.Add(hintText);
        panel.Children.Add(audio);
        panel.Children.Add(audioHint);
        UpdateHint();

        var settings = new ContentDialog
        {
            Title = "导出视频",
            Content = panel,
            PrimaryButtonText = "选择路径并导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await settings.ShowAsync() != ContentDialogResult.Primary) return;

        double startValue = double.IsNaN(startBox.Value) ? 0 : startBox.Value;
        double endValue = double.IsNaN(endBox.Value) ? durationSec : endBox.Value;
        long startMs = (long)Math.Round(Math.Clamp(startValue, 0, durationSec) * 1000);
        long endMs = (long)Math.Round(Math.Clamp(endValue, 0, durationSec) * 1000);
        if (endMs <= startMs)
        {
            await ShowMessageAsync("导出视频", "结束时间必须大于开始时间。");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = SafeFileName(_session.Name),
        };
        picker.FileTypeChoices.Add("MP4 视频", new List<string> { ".mp4" });
        InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        (int outputWidth, int outputHeight) = SelectedSize();
        var options = new ExportOptions(
            file.Path, startMs, endMs, IncludeAudio: audio.IsChecked == true,
            Width: outputWidth, Height: outputHeight, Speed: SelectedSpeed(),
            BoardOnly: boardOnly.IsChecked == true,
            ShowCursor: showCursor.IsChecked == true,
            ShowChapterInfo: showChapter.IsChecked == true);
        await RunExportAsync(options);
    }

    private async Task RunExportAsync(ExportOptions options)
    {
        _lastExportOptions = options;
        _retryExport.Visibility = Visibility.Collapsed;
        _exportCts = new CancellationTokenSource();
        _exportButton.IsEnabled = false;
        _cancelExport.Visibility = Visibility.Visible;
        _exportProgress.Visibility = Visibility.Visible;
        _exportProgress.Value = 0;
        _exportStatus.Visibility = Visibility.Visible;
        _exportStatus.Text = "准备导出…";

        var progress = new Progress<ExportProgress>(value =>
        {
            if (_exportCts is null) return;
            _exportProgress.Value = Math.Clamp(value.Value, 0, 1);
            _exportStatus.Text = string.IsNullOrWhiteSpace(value.Message)
                ? "正在导出…"
                : value.Message;
        });

        try
        {
            ExportResult result = await TimeMachineExporter.ExportWithResultAsync(
                _session, options, progress, _exportCts.Token);

            _exportProgress.Value = 1;
            _exportStatus.Text = $"已导出 {result.FrameCount} 帧 · {result.AudioNote}";
            _lastExportOptions = null;
            _retryExport.Visibility = Visibility.Collapsed;
            TimeMachineExporter.CleanupArtifacts(options);
            OpenContainingFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            _exportProgress.Value = 0;
            _exportStatus.Text = "导出已取消。";
            _retryExport.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _exportProgress.Value = 0;
            _exportStatus.Text = "导出失败，可点击重试。";
            _retryExport.Visibility = Visibility.Visible;
            await ShowMessageAsync("导出视频失败", ex.Message);
        }
        finally
        {
            var cts = _exportCts;
            _exportCts = null;
            cts?.Dispose();
            _exportButton.IsEnabled = true;
            _cancelExport.Visibility = Visibility.Collapsed;
        }
    }

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "classroom-session";
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? "classroom-session" : name;
    }

    private static void OpenContainingFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开资源管理器失败不影响导出结果。
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var root = (Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }

    private static Windows.UI.Color BackgroundColor(string name)
    {
        if (Enum.TryParse<BoardBackground>(name, ignoreCase: true, out var background))
        {
            return background switch
            {
                BoardBackground.Black => (Windows.UI.Color)Application.Current.Resources["IcBoardBlackColor"],
                BoardBackground.Green => Windows.UI.Color.FromArgb(255, 0x1E, 0x3B, 0x2F),
                BoardBackground.Cream => Windows.UI.Color.FromArgb(255, 0xF5, 0xEF, 0xDC),
                _ => Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA),
            };
        }
        return Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        try { _exportCts?.Cancel(); } catch { /* 取消导出失败不应影响关闭 */ }
        try { _seekCts?.Cancel(); } catch { /* 取消定位失败不应影响关闭 */ }
        _seekCts?.Dispose();
        _seekCts = null;
        foreach (InMemoryRandomAccessStream stream in _objectStreams)
        {
            try { stream.Dispose(); }
            catch { /* 对象流释放失败不影响关闭 */ }
        }

        _objectStreams.Clear();
        _objectBitmaps.Clear();
        _nativeTextRenderer.Dispose();
        _nativeFormulaRenderer.Dispose();
        try { _nativeCanvas.RemoveFromVisualTree(); } catch { /* 原生回放画布释放失败不影响关闭 */ }
        _replayController.Dispose();
        try
        {
            if (_savedIndex >= 0 && _savedIndex < _board.PageCount) _board.GoTo(_savedIndex);
            _board.SetBackground(_savedBackground);
        }
        catch
        {
            // 恢复失败不应影响关闭流程。
        }
    }
}