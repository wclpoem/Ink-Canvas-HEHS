using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkCanvas.Controls.Controls;

/// <summary>Standard left navigation and a scrollable page for application windows.</summary>
public sealed class WindowWorkspace : UserControl
{
    private readonly NavigationView _navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        ExpandedModeThresholdWidth = 850,
        CompactModeThresholdWidth = 560,
        OpenPaneLength = 220,
        CompactPaneLength = 48,
        IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = true,
        AlwaysShowHeader = true,
    };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private sealed record Page(string Name, Func<UIElement> Build, int? Parent);
    private sealed record Visit(int Index, double Offset);
    private readonly List<Page> _pages = new();
    private readonly List<NavigationViewItem> _pageItems = new();
    private readonly List<Visit> _history = new();
    private int _currentIndex = -1;
    private bool _changingSelection, _backNavigationEnabled;
    private FrameworkElement _scrollTarget;
    public string SelectedPage { get; private set; }
    public bool IsBackNavigationEnabled
    {
        get => _backNavigationEnabled;
        set { _backNavigationEnabled = value; UpdateBackButton(); }
    }
    public bool CanGoBack => IsBackNavigationEnabled && _history.Count > 0;
    public IReadOnlyList<string> NavigationStack => _history.Select(visit => _pages[visit.Index].Name)
        .Concat(_currentIndex >= 0 ? new[] { SelectedPage } : Array.Empty<string>()).ToArray();
    public AutoSuggestBox SearchBox
    {
        get => _navigation.AutoSuggestBox;
        set => _navigation.AutoSuggestBox = value;
    }

    public void FocusSearch()
    {
        _navigation.IsPaneOpen = true;
        DispatcherQueue.TryEnqueue(() => SearchBox?.Focus(FocusState.Programmatic));
    }

    public void RevealContent()
    {
        if (_navigation.DisplayMode != NavigationViewDisplayMode.Expanded) _navigation.IsPaneOpen = false;
    }

    public void ScrollToElement(FrameworkElement element)
    {
        _scrollTarget = element;
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scroll.LayoutUpdated += OnScrollLayoutUpdated;
        OnScrollLayoutUpdated(null, null);
    }

    private void OnScrollLayoutUpdated(object sender, object args)
    {
        if (_scrollTarget is null || _scroll.Content is not FrameworkElement page
            || _scroll.ViewportHeight <= 0 || _scroll.ExtentHeight <= 0) return;
        var origin = _scrollTarget.TransformToVisual(page).TransformPoint(default);
        // The first navigation request can arrive before the ScrollViewer computes its extent.
        if (origin.Y > _scroll.ViewportHeight && _scroll.ScrollableHeight <= 0) return;
        double offset = Math.Clamp(origin.Y - 12, 0, _scroll.ScrollableHeight);
        if (Math.Abs(_scroll.VerticalOffset - offset) > 1)
        {
            _scroll.ChangeView(null, offset, null, true);
            return;
        }
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scrollTarget = null;
    }
    public bool IsTitleBarAutoPaddingEnabled
    {
        get => _navigation.IsTitleBarAutoPaddingEnabled;
        set => _navigation.IsTitleBarAutoPaddingEnabled = value;
    }

    public WindowWorkspace()
    {
        _navigation.Content = _scroll;
        _navigation.SelectionChanged += (_, args) =>
        {
            if (!_changingSelection && args.SelectedItem is NavigationViewItem item && item.Tag is int index) Navigate(index);
        };
        _navigation.BackRequested += (_, _) => GoBack();
        var back = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Left, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
        back.Invoked += (_, args) => args.Handled = GoBack();
        KeyboardAccelerators.Add(back);
        Content = _navigation;
    }

    public void AddPage(string name, string glyph, Func<UIElement> build)
        => AddPage(name, glyph, build, null);

    public void AddSubPage(string parent, string name, string glyph, Func<UIElement> build)
    {
        int index = _pages.FindIndex(page => page.Name == parent);
        if (index < 0) throw new ArgumentException("The parent page must be added first.", nameof(parent));
        AddPage(name, glyph, build, index);
    }

    private void AddPage(string name, string glyph, Func<UIElement> build, int? parent)
    {
        int index = _pages.Count;
        _pages.Add(new(name, build, parent));
        var item = new NavigationViewItem { Content = name, Tag = index,
            Icon = new FontIcon { Glyph = glyph, FontFamily = IconFontStyle.Font, IsTextScaleFactorEnabled = false } };
        _pageItems.Add(item);
        if (parent is { } parentIndex) _pageItems[parentIndex].MenuItems.Add(item);
        else _navigation.MenuItems.Add(item);
    }

    public void AddGroup(string title) => _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = title });

    public void SelectPage(string name)
    {
        int index = _pages.FindIndex(page => page.Name == name);
        if (index < 0) return;
        Navigate(index);
    }

    public bool GoBack()
    {
        if (!CanGoBack) return false;
        var visit = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Navigate(visit.Index, false, visit.Offset);
        return true;
    }

    public void RestoreNavigation(IReadOnlyList<string> pages)
    {
        var indices = pages.Select(name => _pages.FindIndex(page => page.Name == name)).Where(index => index >= 0).ToArray();
        _history.Clear();
        if (indices.Length == 0) return;
        if (IsBackNavigationEnabled) _history.AddRange(indices.SkipLast(1).Select(index => new Visit(index, 0)));
        Navigate(indices[^1], false);
    }

    private void Navigate(int index, bool recordHistory = true, double offset = 0)
    {
        if (IsBackNavigationEnabled && recordHistory && _currentIndex >= 0 && _currentIndex != index)
            _history.Add(new(_currentIndex, _scroll.VerticalOffset));
        _changingSelection = true;
        try
        {
            for (int? parent = _pages[index].Parent; parent is { } ancestor; parent = _pages[ancestor].Parent)
                _pageItems[ancestor].IsExpanded = true;
            _navigation.SelectedItem = _pageItems[index];
            ShowPage(index);
            if (offset > 0)
            {
                _scroll.UpdateLayout();
                _scroll.ChangeView(null, offset, null, true);
            }
        }
        finally { _changingSelection = false; }
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        _navigation.IsBackButtonVisible = IsBackNavigationEnabled
            ? NavigationViewBackButtonVisible.Visible : NavigationViewBackButtonVisible.Collapsed;
        _navigation.IsBackEnabled = CanGoBack;
    }

    private void ShowPage(int index)
    {
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scrollTarget = null;
        _currentIndex = index;
        SelectedPage = _pages[index].Name;
        if (_pages[index].Parent is not null)
        {
            var path = new List<string>();
            for (int? next = index; next is { } page; next = _pages[page].Parent) path.Add(_pages[page].Name);
            path.Reverse();
            var breadcrumb = new BreadcrumbBar { ItemsSource = path, FontSize = 24 };
            breadcrumb.ItemClicked += (_, args) => { if (args.Index < path.Count - 1) SelectPage(path[args.Index]); };
            _navigation.Header = breadcrumb;
        }
        else _navigation.Header = new TextBlock { Text = SelectedPage, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        // ScrollViewer padding becomes a fixed viewport margin in the WinUI template.
        // Keep page spacing inside the scrollable content so rows reach the window edge.
        _scroll.Content = new Border { Padding = new Thickness(24, 8, 24, 24), Child = _pages[index].Build() };
        _scroll.ChangeView(null, 0, null, true);
    }
}
