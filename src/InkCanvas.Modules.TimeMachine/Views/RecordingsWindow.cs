using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
using Windows.Storage.Pickers;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>
/// 录制管理窗口：录制列表、章节书签查看与视频导出设置。
/// 只做管理与导出，不提供交互式回放窗口。
/// </summary>
public sealed class RecordingsWindow : Window
{
    private readonly ITimeMachineHost _host;
    private readonly TimeMachineService _service;
    private readonly CancellationTokenSource _lifetime = new();

    private ClassroomSession _session;
    private InkTimeline _timeline;
    private RecordingInfo _info;

    private readonly ListView _recordings = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _bookmarks = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _meta = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Value = 0, Height = 8 };

    private readonly NumberBox _startBox = MakeNumberBox("起始（秒）", 0);
    private readonly NumberBox _endBox = MakeNumberBox("结束（秒）", 0);
    private readonly ComboBox _chapter = new() { MinWidth = 220, SelectedIndex = 0 };
    private readonly ComboBox _resolution = new() { MinWidth = 170 };
    private readonly ComboBox _speed = new() { MinWidth = 110, SelectedIndex = 0 };
    private readonly NumberBox _fpsBox = MakeNumberBox("帧率", 30);
    private readonly NumberBox _bitrateBox = MakeNumberBox("码率（Mbps）", 4);
    private readonly NumberBox _widthBox = MakeNumberBox("宽", 1920);
    private readonly NumberBox _heightBox = MakeNumberBox("高", 1080);
    private readonly CheckBox _boardOnly = new() { Content = "仅板书（不导出 PPT 截图）" };
    private readonly CheckBox _showCursor = new() { Content = "叠加书写光标" };
    private readonly CheckBox _showChapter = new() { Content = "叠加章节标题" };
    private readonly Button _export = new() { Content = "导出 MP4", MinHeight = 44, MinWidth = 140 };
    private readonly Button _rename = new() { Content = "重命名", MinHeight = 40, MinWidth = 88 };
    private readonly Button _delete = new() { Content = "删除", MinHeight = 40, MinWidth = 88 };
    private readonly Button _copy = new() { Content = "另存", MinHeight = 40, MinWidth = 88 };

    private bool _closed;
    private bool _exporting;

    public bool IsClosed => _closed;

    public RecordingsWindow(ITimeMachineHost host, TimeMachineService service,
        RecordingInfo initial, ClassroomSession session)
    {
        _host = host;
        _service = service;
        Title = "时光机 · 录制管理";

        BuildLayout();
        Content = Root;
        WindowFrame.Attach(this, Root).UseMica();

        base.Closed += OnClosed;

        if (session is not null)
        {
            AttachSession(session, initial);
        }
        else
        {
            RefreshList();
        }
    }

    private Grid Root { get; } = new() { Margin = new Thickness(16) };

    private void BuildLayout()
    {
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Spacing = 10 };
        var leftHeader = new TextBlock { Text = "录制列表", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var refresh = new Button { Content = "刷新", MinHeight = 40, MinWidth = 96 };
        refresh.Click += (_, _) => RefreshList();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_rename);
        actions.Children.Add(_delete);
        actions.Children.Add(_copy);

        _rename.Click += async (_, _) => await RenameSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _copy.Click += async (_, _) => await CopySelectedAsync();

        left.Children.Add(leftHeader);
        left.Children.Add(refresh);
        left.Children.Add(_recordings);
        left.Children.Add(actions);
        Grid.SetColumn(left, 0);

        _recordings.SelectionChanged += OnRecordingSelected;

        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(_meta);

        var bookmarkHeader = new TextBlock { Text = "章节书签", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _bookmarks.MaxHeight = 160;
        panel.Children.Add(bookmarkHeader);
        panel.Children.Add(_bookmarks);

        var exportHeader = new TextBlock { Text = "导出设置", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        panel.Children.Add(exportHeader);

        _chapter.Items.Add("整段录制");
        panel.Children.Add(HeaderWrapPanel("导出区间", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _startBox, _endBox, _chapter },
        }));

        _resolution.Items.Add("1920 × 1080");
        _resolution.Items.Add("1280 × 720");
        _resolution.Items.Add("源尺寸");
        _resolution.Items.Add("自定义");
        _resolution.SelectedIndex = 0;
        _resolution.SelectionChanged += (_, _) => UpdateCustomResolutionVisibility();

        _speed.Items.Add("1×");
        _speed.Items.Add("2×");
        _speed.Items.Add("4×");

        var resolutionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _resolution, _widthBox, _heightBox, _speed, _fpsBox, _bitrateBox },
        };
        panel.Children.Add(HeaderWrapPanel("画面 / 帧率", resolutionRow));

        panel.Children.Add(_boardOnly);
        panel.Children.Add(_showCursor);
        panel.Children.Add(_showChapter);

        var exportRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _export, _progress, _status },
        };
        _progress.Width = 180;
        panel.Children.Add(exportRow);

        _export.Click += async (_, _) => await ExportAsync();
        UpdateCustomResolutionVisibility();

        right.Content = panel;
        Grid.SetColumn(right, 1);
        Root.Children.Add(left);
        Root.Children.Add(right);
    }

    private static NumberBox MakeNumberBox(string header, double defaultValue) => new()
    {
        Header = header,
        MinWidth = 100,
        Minimum = 0,
        Value = defaultValue,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
    };

    private static StackPanel HeaderWrapPanel(string header, UIElement content) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = header, FontSize = 13, Opacity = 0.8 },
            content,
        },
    };

    private void UpdateCustomResolutionVisibility()
    {
        bool custom = _resolution.SelectedIndex == 3;
        _widthBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _heightBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshList()
    {
        var rows = _service.ListRecordings()
            .Select(info => new RecordingRow { Info = info })
            .ToList();
        _recordings.ItemsSource = rows;
        if (rows.Count > 0) _recordings.SelectedIndex = 0;
    }

    private void OnRecordingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_recordings.SelectedItem is not RecordingRow row) return;
        try
        {
            var session = _service.OpenSession(row.Info);
            AttachSession(session, row.Info);
        }
        catch (Exception ex)
        {
            _status.Text = $"打开录制失败：{ex.Message}";
        }
    }

    private void AttachSession(ClassroomSession session, RecordingInfo info)
    {
        _session = session;
        _info = info;
        _timeline = new InkTimeline(session);
        _timeline.ApplyAutoBookmarks();

        long duration = _timeline.Duration;
        _meta.Text = new StringBuilder()
            .AppendLine($"名称：{session.Name}")
            .AppendLine($"创建：{session.CreatedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"时长：{TimeSpan.FromMilliseconds(duration):mm\\:ss}　事件：{session.Events.Count}　书签：{session.Bookmarks.Count}")
            .ToString();

        _startBox.Value = 0;
        _endBox.Value = Math.Round(duration / 1000.0, 2);

        _chapter.Items.Clear();
        _chapter.Items.Add("整段录制");
        foreach (var bookmark in session.Bookmarks)
        {
            _chapter.Items.Add(new BookmarkRow { Bookmark = bookmark, Timeline = _timeline });
        }
        _chapter.SelectedIndex = 0;

        _bookmarks.ItemsSource = session.Bookmarks
            .Select(bookmark => new BookmarkRow { Bookmark = bookmark, Timeline = _timeline })
            .ToList();

        _status.Text = "";
    }

    private async Task RenameSelectedAsync()
    {
        if (_info is null) { await Notify("请先选择一条录制。"); return; }

        var dialog = new ContentDialog
        {
            Title = "重命名录制",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBox { Header = "新名称", Text = _info.Name, MinWidth = 260 },
        };
        await ShowDialogAsync(dialog);
        if (dialog.Content is not TextBox box || string.IsNullOrWhiteSpace(box.Text)) return;

        if (_service.RenameRecording(_info, box.Text.Trim())) RefreshList();
        else await Notify("重命名失败：文件可能已被移动或占用。");
    }

    private async Task DeleteSelectedAsync()
    {
        if (_info is null) { await Notify("请先选择一条录制。"); return; }

        var dialog = new ContentDialog
        {
            Title = "删除录制",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = $"确定删除「{_info.Name}」吗？此操作不可恢复。", TextWrapping = TextWrapping.Wrap },
        };
        await ShowDialogAsync(dialog);
        if (dialog is not { } d || d.PrimaryButtonText != "删除") return;

        if (_service.DeleteRecording(_info))
        {
            _session = null; _timeline = null; _info = null;
            RefreshList();
        }
        else
        {
            await Notify("删除失败：文件可能已被移动或占用。");
        }
    }

    private async Task CopySelectedAsync()
    {
        if (_info is null) { await Notify("请先选择一条录制。"); return; }

        var picker = new FileSavePicker
        {
            SuggestedFileName = _info.Name + InkCanvas.Host.Contracts.ClassroomRecordingFormat.Extension,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("时光机录制", new List<string> { InkCanvas.Host.Contracts.ClassroomRecordingFormat.Extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            _service.CopyRecording(_info, file.Path);
            await Notify("已另存为新的录制文件。");
        }
        catch (Exception ex)
        {
            await Notify($"另存失败：{ex.Message}");
        }
    }

    private async Task ExportAsync()
    {
        if (_exporting) return;
        if (_session is null || _timeline is null)
        {
            await Notify("请先选择一条录制。");
            return;
        }

        var options = BuildOptions(out string error);
        if (options is null)
        {
            await Notify(error);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = _session.Name + ".mp4",
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        picker.FileTypeChoices.Add("MP4 视频", new List<string> { ".mp4" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        options = options with { OutputPath = file.Path };

        _exporting = true;
        _export.IsEnabled = false;
        _progress.Value = 0;
        _status.Text = "准备导出…";

        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                _progress.Value = p.Value;
                _status.Text = p.Message;
            });
            var result = await _service.ExportAsync(_session, options, progress, _lifetime.Token);
            _progress.Value = 1;
            _status.Text = $"导出完成：{result.FrameCount} 帧";
            await _host.NotifyAsync("时光机", $"已导出 {result.FrameCount} 帧视频。");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "导出已取消。";
        }
        catch (Exception ex)
        {
            _status.Text = $"导出失败：{ex.Message}";
        }
        finally
        {
            _exporting = false;
            _export.IsEnabled = true;
        }
    }

    private ExportOptions BuildOptions(out string error)
    {
        error = null;

        if (!double.TryParse(_startBox.Value.ToString(), out double startSec) || startSec < 0)
        {
            error = "起始时间不合法。"; return null;
        }
        if (!double.TryParse(_endBox.Value.ToString(), out double endSec) || endSec <= startSec)
        {
            error = "结束时间必须大于起始时间。"; return null;
        }

        long startMs = (long)Math.Round(startSec * 1000);
        long endMs = (long)Math.Round(endSec * 1000);
        if (endMs > _timeline.Duration) endMs = _timeline.Duration;
        if (endMs <= startMs)
        {
            error = "所选区间内没有可导出的内容。"; return null;
        }

        var (width, height) = Resolution();
        int fps = (int)Math.Clamp(double.IsNaN(_fpsBox.Value) ? 30 : _fpsBox.Value, 1, 120);
        int bitRate = (int)Math.Clamp((double.IsNaN(_bitrateBox.Value) ? 4 : _bitrateBox.Value) * 1_000_000, 100_000, 100_000_000);
        double speed = Speed();

        return new ExportOptions(
            OutputPath: "",
            StartMs: startMs,
            EndMs: endMs,
            Fps: fps,
            BitRate: bitRate,
            Width: width,
            Height: height,
            Speed: speed,
            BoardOnly: _boardOnly.IsChecked == true,
            ShowCursor: _showCursor.IsChecked == true,
            ShowChapterInfo: _showChapter.IsChecked == true);
    }

    /// <summary>
    /// 解析导出分辨率。索引 2 为「源尺寸」预设：旧 App 版该预设取自回放画布的
    /// <c>ActualWidth / ActualHeight</c>，而本插件是只含会话列表与导出设置的管理窗口，
    /// 不提供交互回放画布；共享程序集中也没有可复用的公开页面 / 视口尺寸成员——
    /// BoardService 上的 Width/Height 属于图片 / 媒体素材而非页面，InkDocument.Bounds
    /// 是笔画内容包围盒（空文档为空矩形），InkController 公开的 Bounds 均为选区 /
    /// 光标 / 视觉包围盒，ViewTransform 是变换矩阵而非尺寸。因此「源尺寸」在缺少真实视口
    /// 来源时回退 1920×1080；需要真实画幅时请在「自定义」中指定宽高。
    /// </summary>
    private (int Width, int Height) Resolution()
    {
        switch (_resolution.SelectedIndex)
        {
            case 1: return (1280, 720);
            case 2: return (1920, 1080); // 源尺寸：无公开视口尺寸来源，回退默认画幅。
            case 3:
                int w = (int)Math.Clamp(double.IsNaN(_widthBox.Value) ? 1920 : _widthBox.Value, 320, 3840);
                int h = (int)Math.Clamp(double.IsNaN(_heightBox.Value) ? 1080 : _heightBox.Value, 320, 3840);
                return (w, h);
            default: return (1920, 1080);
        }
    }

    private double Speed() => _speed.SelectedIndex switch
    {
        1 => 2.0,
        2 => 4.0,
        _ => 1.0,
    };

    private async Task Notify(string message)
    {
        _status.Text = message;
        await _host.NotifyAsync("时光机", message);
    }

    private Task ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = Root.XamlRoot;
        return dialog.ShowAsync().AsTask();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
    }

    public new void Activate() => base.Activate();

    private sealed class RecordingRow
    {
        public RecordingInfo Info;
        public override string ToString()
        {
            string duration = TimeSpan.FromMilliseconds(Info.DurationMs).ToString(@"mm\:ss");
            return $"{Info.Name} · {duration} · {Info.EventCount} 事件";
        }
    }

    private sealed class BookmarkRow
    {
        public SessionBookmark Bookmark;
        public InkTimeline Timeline;
        public override string ToString() => Timeline is null
            ? $"{Bookmark.TimeMs} ms · {Bookmark.Name}"
            : Timeline.DescribeBookmark(Bookmark, 24);
    }
}
