using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using InkCanvas.Controls.Lifecycle;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly AutoSuggestBox _sidebarSearch = new()
    {
        PlaceholderText = "搜索文档文字", QueryIcon = new SymbolIcon(Symbol.Find), Margin = new Thickness(8, 0, 8, 8), MinHeight = 40,
    };
    private readonly Border _floatingSearch = new()
    {
        Width = 360, Padding = new Thickness(8), Margin = new Thickness(12), CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
    };
    private DispatcherQueueTimer _searchDebounce;
    private bool _synchronizingSearch;

    private void ConfigureFloatingSearch()
    {
        var content = new StackPanel { Spacing = 4 };
        var row = new Grid { ColumnSpacing = 2 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int column = 0; column < 3; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _search.Margin = new Thickness(0);
        _search.MinHeight = 40;
        row.Children.Add(_search);
        _previousSearch = Command("上一个搜索结果", Symbol.Up, () => NextSearchResult(-1));
        _nextSearch = Command("下一个搜索结果", "\uE70D", () => NextSearchResult(1));
        ToolTipService.SetToolTip(_previousSearch, "上一个结果 (Shift+F3)");
        ToolTipService.SetToolTip(_nextSearch, "下一个结果 (F3)");
        var close = Command("关闭 PDF 搜索", Symbol.Cancel, CloseSearch);
        Grid.SetColumn(_previousSearch, 1); row.Children.Add(_previousSearch);
        Grid.SetColumn(_nextSearch, 2); row.Children.Add(_nextSearch);
        Grid.SetColumn(close, 3); row.Children.Add(close);
        content.Children.Add(row);
        _searchCount.FontSize = 12;
        _searchCount.Margin = new Thickness(4, 0, 4, 2);
        content.Children.Add(_searchCount);
        _floatingSearch.Child = content;
        Canvas.SetZIndex(_floatingSearch, 10);
        _viewport.Children.Add(_floatingSearch);
        AutomationProperties.SetName(_floatingSearch, "PDF 悬浮搜索条");
        AutomationProperties.SetName(_search, "PDF 搜索关键词");
        AutomationProperties.SetName(_sidebarSearch, "PDF 侧栏搜索关键词");
        DeferredLayout.Observe(_viewport, () => _floatingSearch.MaxWidth = Math.Max(0, _viewport.ActualWidth - 24));
    }

    private void ConfigureSearchEvents()
    {
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(280);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += async (_, _) => await SearchAsync(_search.Text);
        foreach (var input in new[] { _search, _sidebarSearch })
        {
            input.QuerySubmitted += async (_, _) => { _searchDebounce.Stop(); await SearchAsync(input.Text); };
            input.TextChanged += (_, args) =>
            {
                if (_synchronizingSearch) return;
                _synchronizingSearch = true;
                try
                {
                    string text = input.Text;
                    if (_search.Text != text) _search.Text = text;
                    if (_sidebarSearch.Text != text) _sidebarSearch.Text = text;
                    if (string.IsNullOrEmpty(text)) { _searchDebounce.Stop(); ClearSearch(); }
                    else if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                    { _searchDebounce.Stop(); _searchDebounce.Start(); }
                }
                finally { _synchronizingSearch = false; }
            };
        }
        Closed += (_, _) => _searchDebounce.Stop();
    }

    private void OpenSearch()
    {
        _floatingSearch.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { if (!_closed) _search.Focus(FocusState.Programmatic); });
    }

    private void CloseSearch()
    {
        _floatingSearch.Visibility = Visibility.Collapsed;
        _searchDebounce?.Stop();
        _search.Text = "";
        ClearSearch();
        _ink.Focus(FocusState.Programmatic);
    }
}
