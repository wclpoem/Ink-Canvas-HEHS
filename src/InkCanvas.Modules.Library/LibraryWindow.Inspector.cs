using InkCanvas.Controls.Controls;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Library;

internal sealed partial class LibraryWindow
{
    private readonly Grid _selectedDetails = new();
    private readonly StackPanel _noSelection = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Visibility = Visibility.Collapsed };
    private readonly Border _suggestion = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Padding = new Thickness(12), Visibility = Visibility.Collapsed };
    private readonly TextBox _suggestedTitle = new() { Header = "建议名称", MaxLength = 100 };
    private readonly TextBox _suggestedDescription = new() { Header = "内容摘要", TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 88, MaxHeight = 180 };
    private readonly TouchIconButton _previousPage = IconButton("预览上一页", Glyphs.ChevronLeft);
    private readonly TouchIconButton _nextPage = IconButton("预览下一页", Glyphs.ChevronRight);
    private TouchIconButton _favorite;
    private Button _describe, _applySuggestion;
    private AiDocumentResult _aiSuggestion;
    private string _suggestionPath;
    private long _suggestionLength;
    private DateTime _suggestionWritten;
    private bool _analyzing, _applyingSuggestion;

    private void BuildInspector()
    {
        _inspector.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _inspector.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _noSelection.Children.Add(Icon("\uF490", 32));
        var hint = Label("选择文稿以预览页面", 14); hint.TextAlignment = TextAlignment.Center; _noSelection.Children.Add(hint);
        _inspector.Children.Add(_noSelection);
        _inspector.Children.Add(_selectedDetails);
        AutomationProperties.SetName(_noSelection, "未选择文稿");
        _selectedDetails.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var details = new StackPanel { Spacing = 16 };
        var previewCard = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6) };
        _surfaces.Add((previewCard, "IcSurfaceAltBrush"));
        var previewArea = new Grid(); previewCard.Child = previewArea; previewArea.Children.Add(_preview);
        previewArea.SizeChanged += (_, _) => { double height = previewArea.ActualWidth * 9 / 16; if (Math.Abs(previewArea.Height - height) > .5 || double.IsNaN(previewArea.Height)) previewArea.Height = height; };
        details.Children.Add(previewCard);
        var pages = new Grid(); pages.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); pages.ColumnDefinitions.Add(new()); pages.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pages.Children.Add(_previousPage); Grid.SetColumn(_pageText, 1); pages.Children.Add(_pageText); _pageText.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_nextPage, 2); pages.Children.Add(_nextPage); details.Children.Add(pages);
        _previousPage.Click += async (_, _) => await RunAsync(async () => { if (_page > 0) { _page--; await LoadPageAsync(); } });
        _nextPage.Click += async (_, _) => await RunAsync(async () => { if (_page + 1 < _pageCount) { _page++; await LoadPageAsync(); } });
        details.Children.Add(_previewTitle); details.Children.Add(_previewDetails); details.Children.Add(_description);
        _surfaces.Add((_previewDetails, "IcTextSecondaryBrush")); _surfaces.Add((_description, "IcTextSecondaryBrush"));
        _describe = TextButton("AI 命名与描述", Glyphs.Assistant, DescribeAsync);
        _describe.Visibility = _host.DocumentAi?.Available == true ? Visibility.Visible : Visibility.Collapsed;
        details.Children.Add(_describe);
        var suggestionContent = new StackPanel { Spacing = 10 };
        suggestionContent.Children.Add(new TextBlock { Text = "检查建议后应用", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        suggestionContent.Children.Add(_suggestedTitle); suggestionContent.Children.Add(_suggestedDescription);
        var suggestions = new Grid { ColumnSpacing = 8 }; suggestions.ColumnDefinitions.Add(new()); suggestions.ColumnDefinitions.Add(new());
        _applySuggestion = TextButton("应用", Glyphs.SaveStrokes, ApplySuggestionAsync);
        _applySuggestion.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        suggestions.Children.Add(_applySuggestion);
        var cancel = TextButton("取消建议", Glyphs.Close, () => { _aiSuggestion = null; _suggestion.Visibility = Visibility.Collapsed; return Task.CompletedTask; });
        Grid.SetColumn(cancel, 1); suggestions.Children.Add(cancel); suggestionContent.Children.Add(suggestions);
        _suggestion.Child = suggestionContent; details.Children.Add(_suggestion); _surfaces.Add((_suggestion, "IcSurfaceAltBrush"));
        var management = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 3; i++) management.ColumnDefinitions.Add(new());
        _favorite = ManagementButton("收藏", "\uF719", () => ToggleFavoriteAsync(_files.SelectedItem as LibraryEntry));
        management.Children.Add(_favorite);
        var rename = ManagementButton("重命名", "\uF674", RenameAsync); Grid.SetColumn(rename, 1); management.Children.Add(rename);
        var move = ManagementButton("移动", "\uE65A", () => TransferAsync(false)); Grid.SetColumn(move, 2); management.Children.Add(move);
        details.Children.Add(management);
        var more = TextButton("更多操作", "\uE833", () => Task.CompletedTask);
        var menu = new MenuFlyout();
        AddMenuItem(menu, "制作副本…", Symbol.Copy, () => TransferAsync(true));
        AddMenuItem(menu, "在文件资源管理器中显示", Symbol.OpenFile, () =>
        {
            if (_files.SelectedItem is LibraryEntry entry) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            { UseShellExecute = true, ArgumentList = { "/select,", entry.Path } });
            return Task.CompletedTask;
        });
        more.Flyout = menu; details.Children.Add(more);
        _selectedDetails.Children.Add(new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (Mode != LibraryMode.Save) { Grid.SetRow(_action, 1); _inspector.Children.Add(_action); _action.HorizontalAlignment = HorizontalAlignment.Stretch; }
        AutomationProperties.SetName(_preview, "所选白板的页面预览"); AutomationProperties.SetName(_suggestion, "AI 命名建议");
    }

    private TouchIconButton ManagementButton(string label, string glyph, Func<Task> action)
    {
        var button = new TouchIconButton { Label = label, LabelFontSize = 12, Glyph = glyph, IconSize = 18, MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 68, Padding = new Thickness(4), CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private void UpdateSelection()
    {
        var entry = _files.SelectedItem as LibraryEntry;
        bool document = entry is { IsFolder: false };
        _selectedDetails.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
        _noSelection.Visibility = document ? Visibility.Collapsed : Visibility.Visible;
        _previewTitle.Text = document ? entry.Name : "";
        _previewDetails.Text = document ? entry.Details : "";
        if (Mode != LibraryMode.Save) _action.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
        _action.IsEnabled = _compactAction.IsEnabled = _ready && !_committing && (Mode == LibraryMode.Save || document);
        bool compact = _root.ActualWidth < 880;
        _compactAction.Visibility = compact && document && Mode != LibraryMode.Save ? Visibility.Visible : Visibility.Collapsed;
        _detailsButton.Visibility = compact && document ? Visibility.Visible : Visibility.Collapsed;
        _describe.IsEnabled = document && !_analyzing && _host.DocumentAi?.SupportsSuggestions == true;
        ToolTipService.SetToolTip(_describe, _host.DocumentAi?.SupportsSuggestions == true ? "生成可编辑的名称和摘要建议" : "请更新 AI 助手插件以支持确认后命名。");
        _favorite.IsSelected = document && entry.Favorite;
        _favorite.Label = document && entry.Favorite ? "已收藏" : "收藏";
        ToolTipService.SetToolTip(_favorite, document && entry.Favorite ? "取消收藏" : "收藏文稿");
        UpdatePageButtons();
    }
    private void UpdatePageButtons()
    {
        _previousPage.IsEnabled = _page > 0;
        _nextPage.IsEnabled = _pageCount > _page + 1;
        if (_pageCount == 0) _pageText.Text = "正在读取页面…";
    }
    private async void OnCardFavoriteClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: LibraryEntry entry }) await RunAsync(() => ToggleFavoriteAsync(entry));
    }
    private void OnThumbnailSizeChanged(object sender, SizeChangedEventArgs args) => SizeThumbnail((Border)sender);
    private static void SizeThumbnail(Border card)
    {
        double height = card.ActualWidth * 9 / 16;
        if (height > 0 && (double.IsNaN(card.Height) || Math.Abs(card.Height - height) > .5)) card.Height = height;
    }
    private async Task ToggleFavoriteAsync(LibraryEntry entry)
    {
        if (entry is null || entry.IsFolder) return;
        int removed = _store.State.Favorites.RemoveAll(path => path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) _store.State.Favorites.Add(entry.Path);
        entry.Favorite = removed == 0;
        UpdateSelection(); await _store.SaveAsync();
        _status.Text = entry.Favorite ? "已收藏文稿" : "已取消收藏";
        if (_category == "收藏") await RefreshAsync();
    }
    private LibraryEntry EntryFrom(DependencyObject source)
    {
        LibraryEntry candidate = null;
        for (var current = source; current is not null && current != _files; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return null;
            if (current is FrameworkElement { DataContext: LibraryEntry entry }) candidate = entry;
        }
        return candidate;
    }
    private void OnEntryContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not SelectorItem { Content: LibraryEntry entry } container) return;
        _files.SelectedItem = entry;
        var menu = new MenuFlyout();
        if (entry.IsFolder) AddMenuItem(menu, "打开文件夹", Symbol.Folder, () => NavigateAsync(entry.Path));
        else
        {
            if (Mode != LibraryMode.Save) AddMenuItem(menu, "打开文稿", Symbol.OpenFile, CommitAsync);
            AddMenuItem(menu, entry.Favorite ? "取消收藏" : "收藏", Symbol.Favorite, () => ToggleFavoriteAsync(entry));
            AddMenuItem(menu, "移动到…", Symbol.MoveToFolder, () => TransferAsync(false));
            AddMenuItem(menu, "制作副本…", Symbol.Copy, () => TransferAsync(true));
        }
        AddMenuItem(menu, "重命名…", Symbol.Rename, RenameAsync);
        menu.ShowAt(container); args.Handled = true;
    }
    private void AddMenuItem(MenuFlyout menu, string label, Symbol icon, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(icon) };
        item.Click += async (_, _) => await RunAsync(action); menu.Items.Add(item);
    }

    private async Task DescribeAsync()
    {
        if (_analyzing || _host.DocumentAi?.SupportsSuggestions != true || _files.SelectedItem is not LibraryEntry { IsFolder: false } entry) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _aiLoad = request; _analyzing = true; UpdateSelection();
        _status.Text = "正在生成文稿名称和摘要…";
        try
        {
            var file = new FileInfo(entry.Path); long length = file.Length; DateTime written = file.LastWriteTimeUtc;
            var result = await _host.DocumentAi.DescribeAsync(new(entry.Path) { PreviewOnly = true }, request.Token);
            if (request.IsCancellationRequested || _closed || !ReferenceEquals(_files.SelectedItem, entry)) return;
            _aiSuggestion = result; _suggestionPath = entry.Path; _suggestionLength = length; _suggestionWritten = written;
            _suggestedTitle.Text = result.Title; _suggestedDescription.Text = result.Description;
            _suggestion.Visibility = Visibility.Visible; _status.Text = "建议已生成，可编辑后应用。";
        }
        finally { if (ReferenceEquals(_aiLoad, request)) _aiLoad = null; _analyzing = false; if (!_closed) UpdateSelection(); }
    }
    private async Task ApplySuggestionAsync()
    {
        if (_applyingSuggestion || _aiSuggestion is null || _files.SelectedItem is not LibraryEntry entry
            || !entry.Path.Equals(_suggestionPath, StringComparison.OrdinalIgnoreCase)) return;
        string title = _suggestedTitle.Text.Trim();
        LibraryStore.NamedPath(System.IO.Path.GetDirectoryName(entry.Path), title, true);
        _applyingSuggestion = true; _applySuggestion.IsEnabled = false;
        try
        {
            string path = await BoardService.DescribeArchiveAsync(entry.Path, title, _suggestedDescription.Text.Trim(),
                _suggestionLength, _suggestionWritten, _lifetime.Token);
            RelocatePaths(entry.Path, path); await _store.SaveAsync();
            _host.DocumentAi?.Completed(_aiSuggestion with { OriginalPath = entry.Path, Path = path, Title = title,
                Description = _suggestedDescription.Text.Trim(), CompletedAtUtc = DateTimeOffset.UtcNow });
            _aiSuggestion = null; _suggestion.Visibility = Visibility.Collapsed;
            await RefreshAsync();
            _files.SelectedItem = _entries.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            _status.Text = "已应用文稿名称和摘要。";
        }
        finally { _applyingSuggestion = false; if (!_closed) _applySuggestion.IsEnabled = true; }
    }
}
