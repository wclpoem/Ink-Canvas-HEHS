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
/// 课堂资源包窗口：把最近课堂会话（会话 JSON、快照、增量日志）打包成 zip，
/// 也可以把已有资源包解包到 <c>%APPDATA%\InkCanvas\bundles\&lt;时间戳&gt;</c>。
/// 打包与解包都只读源文件，解包遇到同名文件追加后缀，绝不覆盖既有数据。
/// 关闭按钮只隐藏窗口（<see cref="Microsoft.UI.Windowing.AppWindow.Hide"/>），保留实例便于再次打开。
/// </summary>
public sealed class ResourceBundleWindow : Window
{
    private const int MaxListedFiles = 8;

    private readonly FeatureSettings _features;
    private readonly string _sourceDirectory;
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
    private IReadOnlyList<FileInfo> _files = Array.Empty<FileInfo>();

    public ResourceBundleWindow(FeatureSettings features, int theme, string sourceDirectory)
    {
        _features = features;
        _sourceDirectory = sourceDirectory;
        _enabled = features?.EnableResourceBundle ?? true;
        _theme = theme;
        Title = "课堂资源包";

        var description = new TextBlock
        {
            Text = "把最近的板书存档（.icink）打包；解包不覆盖已有文件。",
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
    /// 板书存档目录下最近的 <c>*.icink</c>，按修改时间倒序，最多 <see cref="MaxListedFiles"/> 个；
    /// 不读取文件内容，只列元数据。时光机课堂录制数据属插件私有，不在本体打包范围内。
    /// </summary>
    private IReadOnlyList<FileInfo> CollectSessionFiles()
    {
        if (string.IsNullOrWhiteSpace(_sourceDirectory) || !Directory.Exists(_sourceDirectory))
            return Array.Empty<FileInfo>();

        var files = new List<FileInfo>();
        foreach (string path in Directory.EnumerateFiles(_sourceDirectory))
        {
            if (!path.EndsWith(".icink", StringComparison.OrdinalIgnoreCase)) continue;
            try { files.Add(new FileInfo(path)); }
            catch { /* 单个文件元数据不可读时跳过，不影响其它存档。 */ }
        }

        return files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxListedFiles)
            .ToList();
    }

    private void RefreshFiles()
    {
        _files = _enabled ? CollectSessionFiles() : Array.Empty<FileInfo>();
        _list.Items.Clear();

        foreach (FileInfo file in _files)
        {
            var name = new TextBlock
            {
                Text = file.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var time = new TextBlock
            {
                Text = file.LastWriteTime.ToString("MM-dd HH:mm"),
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
            ToolTipService.SetToolTip(item, file.FullName);
            _list.Items.Add(item);
        }

        ApplyState();
    }

    private void ApplyState()
    {
        bool hasFiles = _enabled && _files.Count > 0;

        _archive.IsEnabled = hasFiles;
        _restore.IsEnabled = _enabled;
        _list.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
        _summary.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
        _disabledHint.Visibility = _enabled ? Visibility.Collapsed : Visibility.Visible;
        _emptyHint.Visibility = _enabled && _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!_enabled)
        {
            _summary.Text = "";
            return;
        }

        _summary.Text = _files.Count == 0
            ? ""
            : $"将打包最近 {_files.Count} 个文件（按修改时间倒序）";
    }

    private static string BuildManifest(IReadOnlyList<FileInfo> files)
    {
        var manifest = new
        {
            version = 1,
            createdAt = DateTime.Now.ToString("o"),
            files = files.Select(file => file.Name).ToArray(),
            app = "InkCanvas",
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task ArchiveAsync()
    {
        IReadOnlyList<FileInfo> files = CollectSessionFiles();
        _files = files;
        ApplyState();
        if (!_enabled || files.Count == 0) return;

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
                files.Select(item => item.FullName).ToList(),
                BuildManifest(files),
                file.Path,
                _features);
            SetStatus($"已保存资源包：{file.Path}", isError: false);
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