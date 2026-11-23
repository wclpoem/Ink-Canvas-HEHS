using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkCanvas.App.Views;

/// <summary>
/// 课堂资源包窗口：把最近课堂会话相关的只读文件打包成 zip，也可以在解包时追加后缀、
/// 绝不覆盖既有数据。可打包内容：白板存档（.icink）、课件批注目录（按 slideId 的
/// manifest 与 .icink）、例题库（examples.json）以及插件录制包（Recordings/*.icink）。
/// 关闭按钮只隐藏窗口（<see cref="Microsoft.UI.Windowing.AppWindow.Hide"/>），保留实例便于再次打开。
/// </summary>
public sealed class ResourceBundleWindow : Window
{
    private const int MaxListedFiles = 8;

    private readonly FeatureSettings _features;
    private readonly ClassroomBundleSourceOptions _sourceOptions;
    private readonly bool _enabled;
    private readonly ListView _list = new()
    {
        Height = 168,
        SelectionMode = ListViewSelectionMode.None,
    };
    private readonly TextBlock _summary = new() { FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _emptyHint = new()
    {
        Text = "暂无可打包的板书存档",
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _disabledHint = new()
    {
        Text = "课堂资源包已关闭",
        FontSize = 13,
        Opacity = 0.8,
        Margin = new Thickness(2, 0, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Opacity = 0.85,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly Button _archive;
    private readonly Button _restore;
    private readonly int _theme;
    private ClassroomBundleSourcePlan _plan = ClassroomBundleSourcePlan.Empty;

    /// <summary>兼容旧调用：只收集白板存档目录。</summary>
    public ResourceBundleWindow(FeatureSettings features, int theme, string sourceDirectory)
        : this(features, theme, sourceDirectory, pptAnnotationRoot: null,
            exampleLibraryDirectory: null, recordingDirectory: null)
    {
    }

    /// <summary>
    /// 创建资源包窗口。<paramref name="pptAnnotationRoot"/> 是课件批注根目录（其下为
    /// <c>Auto Saved - Presentations</c>）；<paramref name="exampleLibraryDirectory"/> 是例题库目录；
    /// <paramref name="recordingDirectory"/> 是插件录制目录。任一为空表示本机没有该类数据。
    /// </summary>
    public ResourceBundleWindow(FeatureSettings features, int theme, string sourceDirectory,
        string pptAnnotationRoot, string exampleLibraryDirectory, string recordingDirectory)
    {
        _features = features;
        _sourceOptions = new ClassroomBundleSourceOptions
        {
            WhiteboardDirectory = sourceDirectory,
            PptAnnotationRoot = pptAnnotationRoot,
            ExampleLibraryDirectory = exampleLibraryDirectory,
            RecordingDirectory = recordingDirectory,
        };
        _enabled = features?.EnableResourceBundle ?? true;
        _theme = theme;
        Title = "课堂资源包";

        var description = new TextBlock
        {
            Text = "把最近的板书存档（.icink）、课件批注、例题库与课堂录制一起打包；解包不覆盖已有文件。",
            FontSize = 12,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        };

        _archive = new Button { Content = "打包", MinHeight = 34, MinWidth = 88 };
        _archive.Click += async (_, _) => await ArchiveAsync();

        _restore = new Button { Content = "打开资源包", MinHeight = 34, MinWidth = 108 };
        _restore.Click += async (_, _) => await RestoreAsync();

        var close = new Button { Content = "关闭", MinHeight = 34, MinWidth = 72 };
        close.Click += (_, _) => HideWindow();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actionRow.Children.Add(_archive);
        actionRow.Children.Add(_restore);
        actionRow.Children.Add(close);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "课堂资源包",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(description);
        panel.Children.Add(_disabledHint);
        panel.Children.Add(_summary);
        panel.Children.Add(_list);
        panel.Children.Add(_emptyHint);
        panel.Children.Add(_status);
        panel.Children.Add(actionRow);

        var body = new Border
        {
            Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush"),
            Child = panel,
        };

        var root = WindowChrome.AddTitleBar(this, "课堂资源包", body, closeButton: false);
        if (root.Children.Count > 0 && root.Children[0] is Grid header)
            header.Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush");

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { HideWindow(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 560, 520);
        WindowChrome.ApplyTheme(this, theme);

        RefreshFiles();
        Activated += (_, _) => RefreshFiles();
    }

    /// <summary>显示或重新置前；用于单实例入口从隐藏状态恢复。</summary>
    public void ShowWindow()
    {
        try { AppWindow.Show(); }
        catch (Exception) { }
        Activate();
    }

    private void HideWindow()
    {
        try { AppWindow.Hide(); }
        catch (Exception) { }
    }

    /// <summary>
    /// 收集本次可打包的会话文件：白板存档 + 课件批注 + 例题库 + 插件录制。
    /// 只读取目录元数据与 PPT manifest，不解码笔画、不加载录制时间轴；源文件始终只读。
    /// </summary>
    private ClassroomBundleSourcePlan CollectSessionFiles()
        => ClassroomBundleSourceCollector.Collect(_sourceOptions);

    private void RefreshFiles()
    {
        _plan = _enabled ? CollectSessionFiles() : ClassroomBundleSourcePlan.Empty;
        _list.Items.Clear();

        foreach (ClassroomBundleSourceFile file in _plan.Files.Take(MaxListedFiles))
        {
            var name = new TextBlock
            {
                Text = KindLabel(file.Kind) + " · " + Path.GetFileName(file.FileName),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var time = new TextBlock
            {
                Text = $"{FormatBytes(file.SizeBytes)}",
                FontSize = 11,
                Opacity = 0.65,
            };
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(name);
            row.Children.Add(time);

            var item = new ListViewItem
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(item, file.SourcePath);
            _list.Items.Add(item);
        }

        ApplyState();
    }

    private void ApplyState()
    {
        bool hasFiles = _enabled && !_plan.IsEmpty;

        _archive.IsEnabled = hasFiles;
        _restore.IsEnabled = _enabled;
        _list.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
        _summary.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
        _disabledHint.Visibility = _enabled ? Visibility.Collapsed : Visibility.Visible;
        _emptyHint.Visibility = _enabled && _plan.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (!_enabled)
        {
            _summary.Text = "";
            return;
        }

        if (_plan.IsEmpty)
        {
            _summary.Text = "";
            return;
        }

        int whiteboard = _plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Whiteboard);
        int ppt = _plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.PptAnnotation);
        int examples = _plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.ExampleLibrary);
        int recordings = _plan.Files.Count(file => file.Kind == ClassroomBundleSourceKind.Recording);
        string text = $"将打包 {_plan.Files.Count} 个文件（白板 {whiteboard}、课件批注 {ppt}、例题 {examples}、" +
            $"录制 {recordings}，共 {FormatBytes(_plan.TotalBytes)}）";
        if (_plan.Truncated)
            text += $"；已按上限跳过 {_plan.SkippedByLimit + _plan.SkippedBySize} 个文件";
        _summary.Text = text;
    }

    private static string KindLabel(ClassroomBundleSourceKind kind) => kind switch
    {
        ClassroomBundleSourceKind.Whiteboard => "白板",
        ClassroomBundleSourceKind.PptAnnotation => "课件批注",
        ClassroomBundleSourceKind.ExampleLibrary => "例题",
        ClassroomBundleSourceKind.Recording => "录制",
        _ => "文件",
    };

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB"
            : Math.Max(1, bytes / 1024).ToString() + " KB";

    private static string BuildManifest(IReadOnlyList<ClassroomBundleSourceFile> files)
    {
        // 结构保持 version / createdAt / files / app；files 使用与 zip 条目一致的唯一名。
        IReadOnlyList<string> entryNames = ClassroomResourceBundle.PlanEntryNames(
            files.Select(file => file.SourcePath).ToList());
        var manifest = new
        {
            version = 1,
            createdAt = DateTime.Now.ToString("o"),
            files = entryNames.ToArray(),
            app = "InkCanvas",
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task ArchiveAsync()
    {
        ClassroomBundleSourcePlan plan = CollectSessionFiles();
        _plan = plan;
        ApplyState();
        if (!_enabled || plan.IsEmpty) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"课堂资源包-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("课堂资源包（zip）", new List<string> { ".zip" });
        InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(this));

        StorageFile file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            ClassroomResourceBundle.Archive(
                plan.Files.Select(item => item.SourcePath).ToList(),
                BuildManifest(plan.Files),
                file.Path,
                _features);
            string skipped = plan.Truncated
                ? $"（已按上限跳过 {plan.SkippedByLimit + plan.SkippedBySize} 个文件）"
                : "";
            SetStatus($"已保存资源包：{file.Path}{skipped}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus("打包失败：" + ex.Message, isError: true);
        }

        RefreshFiles();
    }

    private async Task RestoreAsync()
    {
        if (!_enabled) return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(this));

        StorageFile file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            string targetDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "InkCanvas",
                "bundles",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(file.Path, targetDirectory);
            SetStatus($"已解包 {result.Files.Count} 个文件到：{targetDirectory}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus("解包失败：" + ex.Message, isError: true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.Opacity = isError ? 1.0 : 0.85;
        _status.Foreground = isError
            ? ThemeBrushes.Resolve(_theme, "IcAccentBrush")
            : ThemeBrushes.Resolve(_theme, "IcTextSecondaryBrush");
        _status.Visibility = Visibility.Visible;
    }
}