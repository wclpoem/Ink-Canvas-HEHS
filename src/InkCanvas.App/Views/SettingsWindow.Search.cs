using InkCanvas.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private AutoSuggestBox _search;
    private DispatcherQueueTimer _highlightTimer;
    private readonly List<SettingsSearchEntry> _searchEntries = new();
    private readonly Dictionary<string, Border> _searchTargets = new();
    private string _buildingCategory;
    private bool _indexingSearch, _searchIndexed;
    private Border _highlighted;
    private Brush _previousHighlightBrush;

    private void InitializeSearch()
    {
        _highlightTimer = DispatcherQueue.CreateTimer();
        _highlightTimer.IsRepeating = false;
        _highlightTimer.Interval = TimeSpan.FromSeconds(3);
        _highlightTimer.Tick += (_, _) => ResetSearchHighlight();
    }

    private AutoSuggestBox CreateSearchBox(string query)
    {
        var box = new AutoSuggestBox { PlaceholderText = "查找设置", QueryIcon = new SymbolIcon(Symbol.Find),
            TextMemberPath = nameof(SettingsSearchEntry.Title), Text = query, MinHeight = 40 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "查找设置");
        box.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                box.ItemsSource = SearchSettings(box.Text).Take(30).ToArray();
        };
        box.QuerySubmitted += (_, args) =>
        {
            var entry = args.ChosenSuggestion as SettingsSearchEntry ?? SearchSettings(args.QueryText).FirstOrDefault();
            if (entry is null)
            {
                if (!string.IsNullOrWhiteSpace(args.QueryText)) ShowNotification("没有找到相关设置", InfoBarSeverity.Informational);
                return;
            }
            NavigateToSetting(entry);
        };
        return _search = box;
    }

    internal IReadOnlyList<SettingsSearchEntry> SearchSettings(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SettingsSearchEntry>();
        EnsureSearchIndex();
        return SettingsSearch.Find(_searchEntries, query);
    }

    private void EnsureSearchIndex()
    {
        if (_searchIndexed) return;
        var content = _content;
        var refresh = _refreshControls.ToArray();
        var updateStatus = _updateStatus;
        string category = _buildingCategory;
        _indexingSearch = true;
        _searchEntries.Clear();
        try
        {
            // Reuse the real category builders so search follows installed modules and labels.
            // Detached pages are discarded. Only the small text index survives this operation.
            foreach (string name in Categories) BuildPage(name);
            _searchIndexed = true;
        }
        finally
        {
            _indexingSearch = false;
            _content = content;
            _refreshControls.Clear(); _refreshControls.AddRange(refresh);
            _updateStatus = updateStatus;
            _buildingCategory = category;
        }
    }

    private Border Searchable(Border card, string title, string keywords = null)
    {
        string categoryTerms = _moduleSettingsPages.FirstOrDefault(page => page.Name == _buildingCategory)?.SearchTerms
            ?? SettingsCategoryCatalog.SearchTerms(_buildingCategory);
        var entry = new SettingsSearchEntry(_buildingCategory, title, string.Join(" ", keywords, categoryTerms));
        if (_indexingSearch) _searchEntries.Add(entry);
        else _searchTargets[entry.Key] = card;
        return card;
    }

    internal bool NavigateToSetting(SettingsSearchEntry entry)
    {
        if (!Categories.Contains(entry.Category)) return false;
        _workspace.SelectPage(entry.Category);
        if (!_searchTargets.TryGetValue(entry.Key, out var target)) return false;
        _workspace.RevealContent();
        _search.IsSuggestionListOpen = false;
        _highlighted = target;
        _previousHighlightBrush = target.BorderBrush;
        target.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 103, 192));
        _highlightTimer.Start();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || !ReferenceEquals(_highlighted, target)) return;
            target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .2 });
        });
        return true;
    }

    private void ResetSearchHighlight()
    {
        _highlightTimer.Stop();
        if (_highlighted is not null) _highlighted.BorderBrush = _previousHighlightBrush;
        _highlighted = null; _previousHighlightBrush = null;
    }

    private void SearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (control && args.Key == Windows.System.VirtualKey.F)
        {
            _workspace.FocusSearch();
            args.Handled = true;
        }
    }

    private void CloseSearch() => ResetSearchHighlight();
}
