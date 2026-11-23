using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 例题库与 Shell 的协作入口：再讲一次、贴回白板需要复用 AI 浮窗与画板服务，
/// 窗口本身不直接访问画板/AI，由 ShellController 实现。
/// </summary>
public interface IExampleLibraryHost
{
    /// <summary>复用 AI 助教入口再讲一次；不得修改原例题。</summary>
    void ReExplainExample(ExampleEntry entry);

    /// <summary>把例题内容贴回当前画板页；成功返回 true，不得修改原例题。</summary>
    Task<bool> PasteExampleToBoardAsync(ExampleEntry entry);
}

/// <summary>
/// 本地例题库窗口：按关键词 / 课题 / 知识点检索已有例题，查看题目、答案与教师订正，
/// 可再讲一次、贴回白板、编辑订正或删除。数据全部来自本地 <see cref="ExampleLibraryStore"/>，不联网。
/// 关闭按钮只隐藏窗口（<see cref="Microsoft.UI.Windowing.AppWindow.Hide"/>），保留实例便于再次打开。
/// </summary>
public sealed class ExampleLibraryWindow : Window
{
    private readonly FeatureSettings _features;
    private readonly bool _enabled;
    private readonly IExampleLibraryHost _host;
    private readonly TextBox _keyword = new()
    {
        PlaceholderText = "关键词（标题 / 答案 / 订正）",
        Width = 200,
        MinHeight = 32,
    };
    private readonly TextBox _topic = new()
    {
        PlaceholderText = "课题",
        Width = 150,
        MinHeight = 32,
    };
    private readonly TextBox _knowledge = new()
    {
        PlaceholderText = "知识点",
        Width = 150,
        MinHeight = 32,
    };
    private readonly ListView _list = new()
    {
        Height = 196,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly TextBlock _empty = new()
    {
        Text = "暂无例题",
        FontSize = 12,
        Opacity = 0.7,
        Margin = new Thickness(2, 0, 0, 0),
    };
    private readonly TextBlock _closedHint = new()
    {
        Text = "例题库已关闭",
        FontSize = 13,
        Opacity = 0.8,
        Margin = new Thickness(2, 8, 0, 8),
    };
    private readonly TextBlock _titleText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _answerText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _correctedText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _metaText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Opacity = 0.7,
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Opacity = 0.8,
    };
    private readonly Border _detailCard;
    private readonly Button _search;
    private readonly Button _clear;
    private readonly Button _delete;
    private readonly Button _reExplain;
    private readonly Button _paste;
    private readonly Button _edit;

    public ExampleLibraryWindow(AppSettings settings, int theme, IExampleLibraryHost host = null)
    {
        _features = settings?.Features;
        _enabled = _features?.EnableExampleLibrary ?? true;
        _host = host;
        Title = "例题库";

        _search = new Button { Content = "检索", MinHeight = 34, MinWidth = 76 };
        _search.Click += (_, _) => Refresh();

        _clear = new Button { Content = "清空", MinHeight = 34, MinWidth = 68 };
        _clear.Click += (_, _) =>
        {
            _keyword.Text = "";
            _topic.Text = "";
            _knowledge.Text = "";
            Refresh();
        };

        var filterRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        filterRow.Children.Add(_keyword);
        filterRow.Children.Add(_topic);
        filterRow.Children.Add(_knowledge);
        filterRow.Children.Add(_search);
        filterRow.Children.Add(_clear);

        _list.SelectionChanged += (_, _) => UpdateSelection();

        _delete = new Button
        {
            Content = "删除",
            MinHeight = 34,
            MinWidth = 80,
            IsEnabled = false,
        };
        _delete.Click += (_, _) => DeleteSelected();

        _reExplain = new Button
        {
            Content = "再讲一次",
            MinHeight = 34,
            MinWidth = 96,
            IsEnabled = false,
        };
        _reExplain.Click += (_, _) => ReExplainSelected();

        _paste = new Button
        {
            Content = "贴回白板",
            MinHeight = 34,
            MinWidth = 96,
            IsEnabled = false,
        };
        _paste.Click += (_, _) => _ = PasteSelectedAsync();

        _edit = new Button
        {
            Content = "编辑订正",
            MinHeight = 34,
            MinWidth = 96,
            IsEnabled = false,
        };
        _edit.Click += (_, _) => _ = EditSelectedAsync();

        var close = new Button { Content = "关闭", MinHeight = 34, MinWidth = 72 };
        close.Click += (_, _) => HideWindow();

        var primaryActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        primaryActions.Children.Add(_reExplain);
        primaryActions.Children.Add(_paste);
        primaryActions.Children.Add(_edit);
        primaryActions.Children.Add(_delete);

        var actionRow = new Grid();
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(primaryActions, 0);
        Grid.SetColumn(close, 1);
        actionRow.Children.Add(primaryActions);
        actionRow.Children.Add(close);

        var detailRows = new StackPanel { Spacing = 10 };
        detailRows.Children.Add(DetailField("题目", _titleText));
        detailRows.Children.Add(DetailField("答案", _answerText));
        detailRows.Children.Add(DetailField("教师订正", _correctedText));
        detailRows.Children.Add(DetailField("关联信息", _metaText));

        _detailCard = new Border
        {
            Background = ThemeBrushes.Resolve(theme, "IcSurfaceAltBrush"),
            BorderBrush = ThemeBrushes.Resolve(theme, "IcStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                Content = detailRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 170,
            },
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "本地例题库",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(filterRow);
        panel.Children.Add(_list);
        panel.Children.Add(_closedHint);
        panel.Children.Add(_empty);
        panel.Children.Add(_detailCard);
        panel.Children.Add(_status);
        panel.Children.Add(actionRow);

        var body = new Border
        {
            Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush"),
            Child = panel,
        };

        var root = WindowChrome.AddTitleBar(this, "例题库", body, closeButton: false);
        if (root.Children.Count > 0 && root.Children[0] is Grid header)
            header.Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush");

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { HideWindow(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 800, 680);
        WindowChrome.ApplyTheme(this, theme);

        ApplyEnabledState();
        Refresh();
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

    private void ApplyEnabledState()
    {
        _keyword.IsEnabled = _enabled;
        _topic.IsEnabled = _enabled;
        _knowledge.IsEnabled = _enabled;
        _search.IsEnabled = _enabled;
        _clear.IsEnabled = _enabled;
        if (!_enabled)
        {
            _delete.IsEnabled = false;
            _reExplain.IsEnabled = false;
            _paste.IsEnabled = false;
            _edit.IsEnabled = false;
        }

        _closedHint.Visibility = _enabled ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        _list.Items.Clear();

        if (!_enabled)
        {
            _empty.Visibility = Visibility.Collapsed;
            _detailCard.Visibility = Visibility.Collapsed;
            _status.Text = "";
            _delete.IsEnabled = false;
            _reExplain.IsEnabled = false;
            _paste.IsEnabled = false;
            _edit.IsEnabled = false;
            return;
        }

        var query = new ExampleQuery
        {
            Keyword = Trimmed(_keyword.Text),
            Topic = Trimmed(_topic.Text),
            KnowledgePoint = Trimmed(_knowledge.Text),
        };
        IReadOnlyList<ExampleEntry> entries = ExampleLibraryStore.Search(query, settings: _features);

        foreach (ExampleEntry entry in entries)
        {
            _list.Items.Add(new ListViewItem
            {
                Tag = entry,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = BuildRow(entry),
            });
        }

        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailCard.Visibility = Visibility.Collapsed;
        _delete.IsEnabled = false;
        _reExplain.IsEnabled = false;
        _paste.IsEnabled = false;
        _edit.IsEnabled = false;
        _status.Text = "";
    }

    private void UpdateSelection()
    {
        ExampleEntry entry = SelectedEntry();
        bool hasSelection = _enabled && entry is not null;
        _delete.IsEnabled = hasSelection;
        _reExplain.IsEnabled = hasSelection && _host is not null;
        _paste.IsEnabled = hasSelection && _host is not null;
        _edit.IsEnabled = hasSelection;

        if (!hasSelection)
        {
            _detailCard.Visibility = Visibility.Collapsed;
            return;
        }

        _titleText.Text = string.IsNullOrWhiteSpace(entry.Title) ? "（未命名例题）" : entry.Title;
        _answerText.Text = string.IsNullOrWhiteSpace(entry.Answer) ? "（无答案）" : entry.Answer;
        _correctedText.Text = string.IsNullOrWhiteSpace(entry.CorrectedText)
            ? "（无教师订正）"
            : entry.CorrectedText;
        _metaText.Text = BuildMeta(entry);
        _detailCard.Visibility = Visibility.Visible;
    }

    private void ReExplainSelected()
    {
        ExampleEntry entry = SelectedEntry();
        if (!_enabled || entry is null || _host is null) return;

        try
        {
            _host.ReExplainExample(entry);
            _status.Text = "已复用 AI 助教打开再讲，请在浮窗中确认发送。";
        }
        catch (Exception ex)
        {
            _status.Text = "再讲失败：" + ex.Message;
        }
    }

    private async Task PasteSelectedAsync()
    {
        ExampleEntry entry = SelectedEntry();
        if (!_enabled || entry is null || _host is null) return;

        _paste.IsEnabled = false;
        try
        {
            bool ok = await _host.PasteExampleToBoardAsync(entry);
            _status.Text = ok ? "已贴回当前画板页（原始例题未修改）。" : "贴回失败：画板不可用或内容为空。";
        }
        catch (Exception ex)
        {
            _status.Text = "贴回失败：" + ex.Message;
        }
        finally
        {
            if (_enabled && SelectedEntry() is not null) _paste.IsEnabled = true;
        }
    }

    private async Task EditSelectedAsync()
    {
        ExampleEntry entry = SelectedEntry();
        if (!_enabled || entry is null) return;

        var editor = new TextBox
        {
            Text = entry.CorrectedText ?? "",
            PlaceholderText = "教师订正后的讲评文本",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 420,
            MinHeight = 160,
        };

        var dialog = new ContentDialog
        {
            Title = "编辑教师订正",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        try
        {
            dialog.XamlRoot = Content.XamlRoot;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception ex)
        {
            _status.Text = "打开订正编辑失败：" + ex.Message;
            return;
        }

        entry.CorrectedText = editor.Text?.Trim() ?? "";
        entry.TeacherEdited = true;
        entry.UpdatedAt = DateTime.Now;
        bool saved = ExampleLibraryStore.Add(entry, settings: _features);
        _status.Text = saved ? "已保存教师订正。" : "保存失败：例题库已关闭。";
        if (saved) Refresh();
    }

    private void DeleteSelected()
    {
        ExampleEntry entry = SelectedEntry();
        if (!_enabled || entry is null) return;

        if (ExampleLibraryStore.Delete(entry.Id, settings: _features))
            Refresh();
    }

    private ExampleEntry SelectedEntry()
        => (_list.SelectedItem as ListViewItem)?.Tag as ExampleEntry;

    private static FrameworkElement BuildRow(ExampleEntry entry)
    {
        string knowledge = entry.KnowledgePoints is { Count: > 0 }
            ? string.Join("，", entry.KnowledgePoints)
            : "（无知识点）";
        string source = string.IsNullOrWhiteSpace(entry.SourcePresentation)
            ? "（无来源课件）"
            : entry.SourcePresentation;
        string created = entry.CreatedAt == default ? "（无日期）" : entry.CreatedAt.ToString("yyyy-MM-dd");

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Title) ? "（未命名例题）" : entry.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "课题：" + (string.IsNullOrWhiteSpace(entry.Topic) ? "（未填写）" : entry.Topic)
                + "    知识点：" + knowledge,
            FontSize = 11,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "来源课件：" + source + "    创建日期：" + created
                + (entry.TeacherEdited ? "    · 已订正" : ""),
            FontSize = 11,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
        });
        return content;
    }

    private static string BuildMeta(ExampleEntry entry)
    {
        var parts = new List<string>();
        if (entry.SourceSlideId > 0) parts.Add("幻灯片 " + entry.SourceSlideId);
        if (entry.BoardPage >= 0) parts.Add("板书页 " + (entry.BoardPage + 1));
        if (!string.IsNullOrWhiteSpace(entry.BoardId)) parts.Add("画板 " + entry.BoardId);
        if (!string.IsNullOrWhiteSpace(entry.AiSessionId)) parts.Add("会话 " + Short(entry.AiSessionId));
        if (!string.IsNullOrWhiteSpace(entry.GroupId)) parts.Add("对象组 " + Short(entry.GroupId));
        if (entry.ObjectIds is { Count: > 0 }) parts.Add("原生对象 " + entry.ObjectIds.Count + " 个");
        if (entry.TeacherEdited) parts.Add("教师已订正");
        if (entry.UpdatedAt != default) parts.Add("更新于 " + entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm"));
        return parts.Count == 0 ? "（无 AI 关联信息）" : string.Join("；", parts);
    }

    private static string Short(string value)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= 10 ? value : value.Substring(0, 10) + "…";

    private static FrameworkElement DetailField(string label, TextBlock value)
    {
        var field = new StackPanel { Spacing = 3 };
        field.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.7,
        });
        field.Children.Add(value);
        return field;
    }

    private static string Trimmed(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}