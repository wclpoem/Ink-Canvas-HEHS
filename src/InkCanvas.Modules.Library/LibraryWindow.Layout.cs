using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Library;

internal sealed partial class LibraryWindow
{
    private readonly SplitView _navigation = new() { OpenPaneLength = 216, IsPaneOpen = true, DisplayMode = SplitViewDisplayMode.Inline };
    private readonly Grid _content = new() { ColumnSpacing = 12, Padding = new Thickness(0, 0, 16, 16) };
    private readonly Grid _canvas = new() { Padding = new Thickness(16), RowSpacing = 12 };
    private readonly Grid _inspector = new() { Padding = new Thickness(16), RowSpacing = 16 };
    private readonly Border _canvasCard = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly Border _inspectorCard = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly StackPanel _empty = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, Margin = new Thickness(12, 0, 12, 24), Visibility = Visibility.Collapsed };
    private readonly Button _clearFilters = new() { Content = "清除筛选", MinHeight = 44 };
    private readonly TouchIconButton _navigationButton = IconButton("文稿位置", "\uF4F8");
    private readonly TouchIconButton _up = IconButton("上一级文件夹", "\uF19C");
    private readonly TouchIconButton _detailsButton = IconButton("文稿详情", "\uF4AB");
    private readonly TouchIconButton _gridView = IconButton("缩略图视图", "\uF467");
    private readonly TouchIconButton _listView = IconButton("列表视图", "\uF4F8");
    private readonly Button _compactAction = new() { Content = "打开文稿", MinHeight = 44 };
    private readonly Grid _filterRow = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly StackPanel _viewControls = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Grid _saveRow = new() { ColumnSpacing = 8, RowSpacing = 8 };
    private readonly Flyout _inspectorFlyout = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly List<(FrameworkElement Element, string Token)> _surfaces = new();
    private bool _listMode, _compactNavigation, _inspectorInFlyout;

    private static FontIcon Icon(string glyph, double size = 18) => new()
    { Glyph = glyph, FontFamily = IconFontStyle.Font, FontSize = size, IsTextScaleFactorEnabled = false };
    private static TouchIconButton IconButton(string label, string glyph)
    {
        var button = new TouchIconButton { Glyph = glyph, IconSize = 18, Width = 40, Height = 44,
            MinWidth = 40, MinHeight = 44, Padding = new Thickness(6), CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
        return button;
    }
    private Button TextButton(string label, string glyph, Func<Task> action)
    {
        var button = new Button { MinHeight = 44, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { Icon(glyph), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } } };
        AutomationProperties.SetName(button, label);
        void RefreshButtonTheme()
        {
            if (ReferenceEquals(button.Style, Application.Current.Resources["AccentButtonStyle"])) return;
            button.Background = ThemeBrushes.Resolve(_root, "IcSurfaceAltBrush");
            button.Foreground = ThemeBrushes.Resolve(_root, "IcTextPrimaryBrush");
            button.BorderBrush = ThemeBrushes.Resolve(_root, "IcStrokeBrush");
        }
        button.Loaded += (_, _) => RefreshButtonTheme();
        button.ActualThemeChanged += (_, _) => RefreshButtonTheme();
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private TextBlock Label(string text, double size = 12)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        _surfaces.Add((block, "IcTextSecondaryBrush")); return block;
    }
    private void Build()
    {
        _root.Children.Add(_navigation);
        _navigation.Content = _content;
        _content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _content.ColumnDefinitions.Add(new() { Width = new GridLength(296) });
        _canvasCard.Child = _canvas; _content.Children.Add(_canvasCard);
        _inspectorCard.Child = _inspector; Grid.SetColumn(_inspectorCard, 1); _content.Children.Add(_inspectorCard);
        _surfaces.Add((_canvasCard, "IcSurfaceAltBrush"));
        _surfaces.Add((_inspectorCard, "IcSurfaceBrush"));
        BuildNavigation(); BuildFiles(); BuildInspector();
        _root.SizeChanged += (_, _) => UpdateLayout();
        _root.ActualThemeChanged += (_, _) => ApplyTheme();
        _root.Loaded += (_, _) => { ApplyTheme(); UpdateLayout(); };
        _canvas.SizeChanged += (_, _) => UpdateFileLayout();
        _inspectorFlyout.Closed += (_, _) => RestoreInspector();
        _navigationButton.Click += (_, _) => _navigation.IsPaneOpen = !_navigation.IsPaneOpen;
        _detailsButton.Click += (_, _) => ShowInspector();
        _action.Click += async (_, _) => await RunAsync(CommitAsync);
        _compactAction.Click += async (_, _) => await RunAsync(CommitAsync);
        AutomationProperties.SetName(_action, Mode == LibraryMode.Save ? "保存文稿" : "打开文稿");
        AutomationProperties.SetName(_compactAction, "打开所选文稿");
        _action.Style = _compactAction.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    private void BuildNavigation()
    {
        var pane = new Grid { Padding = new Thickness(12, 4, 12, 0), RowSpacing = 12 };
        pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pane.Children.Add(new TextBlock { Text = "文稿", FontSize = 24, Margin = new Thickness(8, 4, 0, 4) });
        var sources = new StackPanel { Spacing = 6 };
        var template = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid ColumnSpacing="12" MinHeight="40" ToolTipService.ToolTip="{Binding Path}">
                <Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Border Width="3" Height="20" CornerRadius="2" HorizontalAlignment="Left" Margin="-12,0,0,0" Visibility="{Binding Indicator}" Background="{ThemeResource AccentFillColorDefaultBrush}"/>
                <FontIcon Glyph="{Binding Icon}" FontFamily="{StaticResource IcModuleIconFontFamily}" FontSize="18" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="1" Text="{Binding Title}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        foreach (var list in new[] { _locations, _folders })
        {
            list.ItemTemplate = template;
            foreach (int theme in new[] { 1, 2 })
            {
                var resources = new ResourceDictionary();
                resources["ListViewItemBackgroundSelected"] = ThemeBrushes.Resolve(theme, "IcAccentSoftBrush");
                resources["ListViewItemBackgroundSelectedPointerOver"] = ThemeBrushes.Resolve(theme, "IcAccentSoftBrush");
                resources["ListViewItemForegroundSelected"] = ThemeBrushes.Resolve(theme, "IcTextPrimaryBrush");
                list.Resources.ThemeDictionaries[theme == 1 ? "Light" : "Dark"] = resources;
            }
            list.IsItemClickEnabled = true;
            list.SelectionChanged += (_, _) =>
            {
                if (_loading || _selectingLocation || list.SelectedItem is not LibraryLocation location) return;
                _ = RunAsync(() => NavigateAsync(location.Path, location.Title));
            };
            list.ItemClick += (_, _) => { if (_compactNavigation) _navigation.IsPaneOpen = false; };
        }
        AutomationProperties.SetName(_locations, "常用文稿位置");
        AutomationProperties.SetName(_folders, "文稿文件夹");
        sources.Children.Add(_locations);
        sources.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 8, 8, 4), Background = ThemeBrushes.Resolve(_root, "IcModuleBorderBrush") });
        var heading = Label("文稿文件夹"); heading.Margin = new Thickness(12, 4, 0, 4); sources.Children.Add(heading);
        sources.Children.Add(_folders);
        sources.Children.Add(TextButton("添加文件夹…", "\uF421", AddLocationAsync));
        var scroll = new ScrollViewer { Content = sources, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); pane.Children.Add(scroll);
        var browse = TextButton(Mode == LibraryMode.Save ? "其他保存位置…" : "浏览本地文件…", "\uF433", SystemPickerAsync);
        Grid.SetRow(browse, 2); pane.Children.Add(browse);
        _navigation.Pane = pane;
    }

    private void BuildFiles()
    {
        for (int row = 0; row < 5; row++) _canvas.RowDefinitions.Add(new() { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var pathRow = new Grid { ColumnSpacing = 4 };
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(_navigationButton); pathRow.Children.Add(_up); Grid.SetColumn(_up, 1);
        _path.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_path, 2); pathRow.Children.Add(_path);
        _up.Click += async (_, _) => await RunAsync(async () => { if (Directory.GetParent(_folder) is { } parent) await NavigateAsync(parent.FullName); });
        _path.ItemClicked += async (_, args) => await RunAsync(() => args.Item is FolderCrumb { Path: { } path } ? NavigateAsync(path) : Task.CompletedTask);
        var refresh = IconButton("刷新文稿", "\uF13E"); Grid.SetColumn(refresh, 3); pathRow.Children.Add(refresh);
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        Grid.SetColumn(_detailsButton, 4); pathRow.Children.Add(_detailsButton); _canvas.Children.Add(pathRow);
        _filterRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _filterRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _filterRow.RowDefinitions.Add(new() { Height = GridLength.Auto }); _filterRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _filterRow.Children.Add(_search);
        _search.QueryIcon = new SymbolIcon(Symbol.Find);
        _search.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { _debounce.Stop(); _debounce.Start(); } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunAsync(RefreshAsync); };
        _sort.SelectionChanged += (_, _) => { if (!_loading && _ready) _ = RunAsync(RefreshAsync); };
        _viewControls.Children.Add(_sort); _viewControls.Children.Add(_gridView); _viewControls.Children.Add(_listView);
        var more = IconButton("文件夹选项", "\uE833");
        var folderMenu = new MenuFlyout();
        var create = new MenuFlyoutItem { Text = "新建文件夹", Icon = new SymbolIcon(Symbol.NewFolder) };
        create.Click += async (_, _) => await RunAsync(NewFolderAsync); folderMenu.Items.Add(create);
        var recursive = new ToggleMenuFlyoutItem { Text = "搜索子文件夹", IsChecked = true };
        recursive.Click += (_, _) => { _recursive.IsOn = recursive.IsChecked; if (_ready) _ = RunAsync(RefreshAsync); };
        folderMenu.Items.Add(recursive); more.Flyout = folderMenu; _viewControls.Children.Add(more);
        _gridView.Click += async (_, _) => { SetView(false); await RunAsync(_store.SaveAsync); };
        _listView.Click += async (_, _) => { SetView(true); await RunAsync(_store.SaveAsync); };
        _filterRow.Children.Add(_viewControls); Grid.SetColumn(_viewControls, 1);
        Grid.SetRow(_filterRow, 1); _canvas.Children.Add(_filterRow);
        var documentArea = new Grid(); Grid.SetRow(documentArea, 2); _canvas.Children.Add(documentArea);
        _files.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _files.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="GridViewItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="VerticalContentAlignment" Value="Stretch"/>
              <Setter Property="Padding" Value="4"/><Setter Property="Margin" Value="2"/>
              <Setter Property="MinWidth" Value="0"/>
            </Style>
            """);
        _files.ContainerContentChanging += OnContainerChanged;
        _files.Loaded += (_, _) => UpdateFileLayout();
        _files.SizeChanged += (_, _) => UpdateFileLayout();
        _files.SelectionChanged += async (_, _) => await RunAsync(async () =>
        {
            _pageLoad?.Cancel(); _aiLoad?.Cancel(); _aiSuggestion = null; _suggestion.Visibility = Visibility.Collapsed;
            _page = _pageCount = 0; _preview.Source = null; _description.Text = ""; _description.Visibility = Visibility.Collapsed;
            UpdateSelection(); await LoadPageAsync();
        });
        _files.ItemClick += async (_, args) =>
        { if (args.ClickedItem is LibraryEntry { IsFolder: true } folder) await RunAsync(() => NavigateAsync(folder.Path)); };
        _files.DoubleTapped += async (_, args) =>
        {
            if (Mode == LibraryMode.Save || EntryFrom(args.OriginalSource as DependencyObject) is not { IsFolder: false } entry) return;
            _files.SelectedItem = entry; await RunAsync(CommitAsync);
        };
        documentArea.Children.Add(_files); documentArea.Children.Add(_empty);
        _empty.Children.Add(Icon("\uF379", 44));
        _empty.Children.Add(new TextBlock { Text = "此处没有匹配的文稿", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var emptyHint = Label("可切换文件夹、调整搜索条件，或浏览本地文件。"); emptyHint.TextAlignment = TextAlignment.Center; _empty.Children.Add(emptyHint);
        _clearFilters.HorizontalAlignment = HorizontalAlignment.Center;
        _clearFilters.Click += async (_, _) => { _search.Text = ""; await RunAsync(RefreshAsync); }; _empty.Children.Add(_clearFilters);
        _empty.Children.Add(TextButton("浏览本地文件…", "\uF433", SystemPickerAsync));
        var footer = new Grid { ColumnSpacing = 8 }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_status); _surfaces.Add((_status, "IcTextSecondaryBrush"));
        Grid.SetColumn(_busy, 1); footer.Children.Add(_busy);
        Grid.SetColumn(_compactAction, 2); footer.Children.Add(_compactAction);
        Grid.SetRow(footer, 3); _canvas.Children.Add(footer);
        if (Mode == LibraryMode.Save)
        {
            _saveRow.ColumnDefinitions.Add(new()); _saveRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _saveRow.RowDefinitions.Add(new() { Height = GridLength.Auto }); _saveRow.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _saveRow.Children.Add(_name); Grid.SetColumn(_action, 1); _saveRow.Children.Add(_action); _action.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(_saveRow, 4); _canvas.Children.Add(_saveRow);
        }
        AutomationProperties.SetName(_files, "文稿与文件夹"); AutomationProperties.SetName(_search, "搜索文稿");
        AutomationProperties.SetName(_empty, "文稿空状态"); AutomationProperties.SetName(_path, "文件夹面包屑");
    }

    private void SetView(bool list)
    {
        _listMode = list; _store.State.ListView = list;
        _gridView.IsSelected = !list; _listView.IsSelected = list;
        _files.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(list ? """
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsStackPanel/></ItemsPanelTemplate>
            """ : """
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsWrapGrid Orientation="Horizontal" ItemWidth="220" ItemHeight="208"/></ItemsPanelTemplate>
            """);
        _files.ItemTemplate = (DataTemplate)XamlReader.Load(list ? ListTemplate : GridTemplate);
        UpdateFileLayout();
    }

    private void UpdateLayout()
    {
        if (_root.ActualWidth <= 0) return;
        bool narrow = _root.ActualWidth < 640;
        if (narrow != _compactNavigation)
        {
            _compactNavigation = narrow;
            _navigation.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            _navigation.IsPaneOpen = !narrow;
        }
        _navigation.OpenPaneLength = _root.ActualWidth < 1100 ? 192 : 216;
        bool showDetails = _root.ActualWidth >= 880;
        if (showDetails && _inspectorInFlyout) _inspectorFlyout.Hide();
        _content.ColumnDefinitions[1].Width = showDetails ? new GridLength(296) : new GridLength(0);
        _content.ColumnSpacing = showDetails ? 12 : 0;
        _inspectorCard.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        _content.Padding = new Thickness(narrow ? 12 : 0, 0, 12, 12);
        _navigationButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        _detailsButton.Visibility = !showDetails && _files.SelectedItem is { } ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection(); UpdateFileLayout();
    }

    private void UpdateFileLayout()
    {
        double width = Math.Max(0, _canvas.ActualWidth - _canvas.Padding.Left - _canvas.Padding.Right);
        bool stacked = width < 540;
        Grid.SetRow(_viewControls, stacked ? 1 : 0); Grid.SetColumn(_viewControls, stacked ? 0 : 1);
        Grid.SetColumnSpan(_search, stacked ? 2 : 1); Grid.SetColumnSpan(_viewControls, stacked ? 2 : 1);
        _viewControls.HorizontalAlignment = HorizontalAlignment.Right;
        if (Mode == LibraryMode.Save)
        {
            bool small = width < 360;
            Grid.SetRow(_action, small ? 1 : 0); Grid.SetColumn(_action, small ? 0 : 1);
            Grid.SetColumnSpan(_name, small ? 2 : 1); Grid.SetColumnSpan(_action, small ? 2 : 1);
        }
        if (!_listMode && _files.ItemsPanelRoot is ItemsWrapGrid grid && width > 0)
        {
            int columns = Math.Max(1, (int)(width / 218));
            double itemWidth = Math.Max(1, (width - 16) / columns);
            if (Math.Abs(grid.ItemWidth - itemWidth) > 1) grid.ItemWidth = itemWidth;
            double height = (itemWidth - 16) * 9 / 16 + 88;
            if (Math.Abs(grid.ItemHeight - height) > 1) grid.ItemHeight = height;
        }
    }

    private void ApplyTheme()
    {
        foreach (var (element, token) in _surfaces)
        {
            var brush = ThemeBrushes.Resolve(_root, token);
            if (element is Border border) { border.Background = brush; border.BorderBrush = ThemeBrushes.Resolve(_root, "IcStrokeBrush"); }
            else if (element is TextBlock text) text.Foreground = brush;
        }
        _navigation.PaneBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_navigation.Pane is FrameworkElement pane) pane.RequestedTheme = _root.ActualTheme;
        _content.RequestedTheme = _inspector.RequestedTheme = _root.ActualTheme;
    }

    private sealed record FolderCrumb(string Name, string Path) { public override string ToString() => Name; }
    private async Task NavigateAsync(string path, string category = "文件夹")
    {
        _category = path is null ? category : "文件夹";
        if (path is not null) _folder = path;
        _search.Text = ""; await RefreshAsync();
    }
    private void UpdateBreadcrumb()
    {
        if (_category != "文件夹") { _path.ItemsSource = new[] { new FolderCrumb(_category, null) }; _up.IsEnabled = false; return; }
        var parts = new List<FolderCrumb>();
        for (var folder = new DirectoryInfo(_folder); folder is not null; folder = folder.Parent)
            parts.Insert(0, new(folder.Parent is null ? folder.FullName : folder.Name, folder.FullName));
        _path.ItemsSource = parts; _up.IsEnabled = parts.Count > 1;
    }
    private void SelectLocation()
    {
        _selectingLocation = true;
        _locations.SelectedItem = _locations.Items.Cast<LibraryLocation>().FirstOrDefault(location => _category == location.Title);
        _folders.SelectedItem = _category == "文件夹" ? _folders.Items.Cast<LibraryLocation>().FirstOrDefault(location =>
            _folder.Equals(location.Path, StringComparison.OrdinalIgnoreCase)) : null;
        foreach (var list in new[] { _locations, _folders })
            foreach (var location in list.Items.Cast<LibraryLocation>()) location.Selected = ReferenceEquals(list.SelectedItem, location);
        _selectingLocation = false;
    }

    private void ShowInspector()
    {
        if (_inspectorInFlyout || _closed) return;
        _inspectorInFlyout = true; _inspectorCard.Child = null;
        _inspector.Width = Math.Min(296, Math.Max(200, _root.ActualWidth - 48));
        _inspector.Height = Math.Min(680, Math.Max(220, _root.ActualHeight - 64));
        _inspectorFlyout.Content = _inspector; _inspectorFlyout.ShowAt(_detailsButton);
    }
    private void RestoreInspector()
    {
        if (!_inspectorInFlyout) return;
        _inspectorFlyout.Content = null; _inspectorInFlyout = false;
        _inspector.ClearValue(FrameworkElement.WidthProperty); _inspector.ClearValue(FrameworkElement.HeightProperty);
        if (!_closed) _inspectorCard.Child = _inspector;
    }

    private const string GridTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid RowSpacing="6" HorizontalAlignment="Stretch">
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
            <Border x:Name="CardPreview" CornerRadius="8" BorderBrush="{ThemeResource IcStrokeBrush}" BorderThickness="1" Background="{ThemeResource IcSurfaceBrush}">
              <Grid>
                <FontIcon Glyph="{Binding Icon}" FontFamily="{StaticResource IcModuleIconFontFamily}" FontSize="32" Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                <Image Source="{Binding Preview}" Stretch="Uniform"/>
                <Border VerticalAlignment="Bottom" HorizontalAlignment="Left" Margin="6" Background="{ThemeResource IcSurfaceAltBrush}" CornerRadius="8" Padding="8,3" Visibility="{Binding FileVisibility}">
                  <TextBlock Text="{Binding PageLabel}" FontSize="11" Foreground="{ThemeResource IcTextPrimaryBrush}"/>
                </Border>
                <ToggleButton x:Name="FavoriteButton" IsChecked="{Binding Favorite, Mode=OneWay}" Background="{ThemeResource IcSurfaceAltBrush}" Foreground="{ThemeResource IcTextPrimaryBrush}" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="4" Width="40" Height="40" Padding="6" Visibility="{Binding FileVisibility}" ToolTipService.ToolTip="{Binding FavoriteHint}" AutomationProperties.Name="{Binding FavoriteHint}">
                  <FontIcon Glyph="{Binding FavoriteGlyph}" FontFamily="{StaticResource IcModuleIconFontFamily}" FontSize="16"/>
                </ToggleButton>
              </Grid>
            </Border>
            <TextBlock Grid.Row="1" Text="{Binding Name}" TextTrimming="CharacterEllipsis" FontWeight="SemiBold" ToolTipService.ToolTip="{Binding Name}"/>
            <TextBlock Grid.Row="2" Text="{Binding Details}" FontSize="12" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
          </Grid>
        </DataTemplate>
        """;
    private const string ListTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid ColumnSpacing="12" Padding="4" MinHeight="72">
            <Grid.ColumnDefinitions><ColumnDefinition Width="80"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <Border Height="45" CornerRadius="6" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
              <Grid><FontIcon Glyph="{Binding Icon}" FontFamily="{StaticResource IcModuleIconFontFamily}" FontSize="24"/><Image Source="{Binding Preview}" Stretch="Uniform"/></Grid>
            </Border>
            <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="4">
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" FontWeight="SemiBold"/>
              <TextBlock Text="{Binding Details}" TextTrimming="CharacterEllipsis" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
            </StackPanel>
            <ToggleButton x:Name="FavoriteButton" IsChecked="{Binding Favorite, Mode=OneWay}" Background="{ThemeResource IcSurfaceAltBrush}" Foreground="{ThemeResource IcTextPrimaryBrush}" Grid.Column="2" Width="40" Height="40" Padding="6" Visibility="{Binding FileVisibility}" ToolTipService.ToolTip="{Binding FavoriteHint}" AutomationProperties.Name="{Binding FavoriteHint}">
              <FontIcon Glyph="{Binding FavoriteGlyph}" FontFamily="{StaticResource IcModuleIconFontFamily}" FontSize="16"/>
            </ToggleButton>
          </Grid>
        </DataTemplate>
        """;
}
