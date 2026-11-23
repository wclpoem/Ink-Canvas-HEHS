using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Controls.Theming;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Typography = Microsoft.UI.Xaml.Documents.Typography;

namespace InkCanvas.App.Views;

public sealed partial class LotteryWindow
{
    private enum WindowLayout { Mini, Compact, Wide }
    private WindowLayout? _layout;
    private Grid _page, _workspace, _controls, _quickRow, _stepper, _topBar;
    private Border _results, _controlCard, _noticeHost;
    private ItemsRepeater _resultView;
    private ScrollViewer _resultScroll;
    private readonly ResultGridLayout _resultLayout = new()
    {
        Orientation = Orientation.Horizontal, ItemsStretch = UniformGridLayoutItemsStretch.Fill,
        MinColumnSpacing = 12, MinRowSpacing = 12, MinItemWidth = 140, MinItemHeight = 94,
    };
    private Viewbox _singleHost, _emptyHost;
    private TextBlock _placeholder, _singleName, _resultTitle, _countLabel, _summary, _strategyText, _history, _notice, _countTitle;
    private ProgressRing _progress;
    private TouchIconButton _minusButton, _plusButton, _rollButton, _importButton, _resetRound, _copy, _optionsButton, _rosterChip, _strategyChip;
    private ComboBox _samplingMode;
    private NumberBox _poolInput, _cooldownPeriod;
    private readonly List<(TouchIconButton Button, int Count)> _quickCounts = new();
    private readonly Dictionary<TouchIconButton, TextBlock> _actionLabels = new();
    private List<ResultItem> _items = new();
    private int _measuredCount = -1;
    private double _measuredWidth;
    private Flyout _options;
    private StackPanel _optionsContent;

    private sealed class ResultGridLayout : UniformGridLayout
    {
        protected override Windows.Foundation.Size MeasureOverride(VirtualizingLayoutContext context, Windows.Foundation.Size availableSize)
        {
            // WinUI 1.8 can compute zero columns when shrinking below the previous item
            // width. Keep one item measurable until the deferred size update catches up.
            availableSize.Width = Math.Max(MinItemWidth, availableSize.Width);
            return base.MeasureOverride(context, availableSize);
        }
    }

    private void BuildUi()
    {
        _page = new Grid { Margin = new Thickness(20, 8, 20, 20), RowSpacing = 12 };
        _page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(_page);
        BuildOptions();
        BuildTopBar();
        BuildResultStage();
        BuildControls();
        _workspace = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(292) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _workspace.Children.Add(_results);
        Grid.SetColumn(_controlCard, 1); _workspace.Children.Add(_controlCard);
        Grid.SetRow(_workspace, 1); _page.Children.Add(_workspace);
        DeferredLayout.Observe(_root, UpdateLayout);
        DeferredLayout.Observe(_resultScroll, UpdateResultLayout);
        AutomationProperties.SetName(_results, "抽选结果舞台");
        AutomationProperties.SetName(_controlCard, "抽选控制面板");
    }

    private TouchIconButton ActionButton(string text, string glyph, Action action = null, bool primary = false)
    {
        var button = new TouchIconButton
        {
            MinWidth = 44, MinHeight = 44, Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), SelectionBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _actionLabels.Add(button, label);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new FontIcon { Glyph = glyph, FontFamily = IconFontStyle.Font, FontSize = 18, IsTextScaleFactorEnabled = false }, label,
            },
        };
        if (primary)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.ClearValue(Control.BackgroundProperty);
        }
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    private void BuildTopBar()
    {
        _topBar = new Grid { ColumnSpacing = 8 };
        _topBar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _topBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var chips = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        chips.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        chips.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _rosterChip = ActionButton("共 0 人", Glyphs.People, OpenNamesInput);
        _summary = _actionLabels[_rosterChip];
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        _rosterChip.CornerRadius = new CornerRadius(20);
        AutomationProperties.SetName(_rosterChip, "当前名单与人数");
        _strategyChip = ActionButton("软冷却", Glyphs.Stopwatch, ShowOptions);
        _strategyText = _actionLabels[_strategyChip];
        _strategyChip.CornerRadius = new CornerRadius(20);
        AutomationProperties.SetName(_strategyChip, "当前点名策略");
        chips.Children.Add(_rosterChip);
        Grid.SetColumn(_strategyChip, 1); chips.Children.Add(_strategyChip);
        _topBar.Children.Add(chips);
        _importButton = ActionButton("名单管理", Glyphs.People, OpenNamesInput);
        AutomationProperties.SetName(_importButton, "导入名单");
        _optionsButton = ActionButton("选项", Glyphs.Settings);
        _optionsButton.Flyout = _options;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _importButton, _optionsButton } };
        Grid.SetColumn(actions, 1); _topBar.Children.Add(actions);
        _page.Children.Add(_topBar);
        AutomationProperties.SetName(_topBar, "点名状态与管理");
    }

    private void BuildOptions()
    {
        _samplingMode = new ComboBox { Header = "点名方式", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string label in new[] { "软冷却点名（默认）", "均匀随机", "本轮不重复" }) _samplingMode.Items.Add(label);
        AutomationProperties.SetName(_samplingMode, "点名方式");
        _samplingMode.SelectedIndex = (int)_lottery.SamplingMode;
        _samplingMode.SelectionChanged += (_, _) =>
        {
            if (_syncing || _rolling || _samplingMode.SelectedIndex < 0) return;
            _lottery.SamplingMode = (LotterySamplingMode)_samplingMode.SelectedIndex;
            OptionsChanged?.Invoke();
        };
        _cooldownPeriod = new NumberBox { Header = "冷却半衰期（天）", Value = _lottery.CooldownHalfLifeDays,
            Minimum = .25, Maximum = 3650, SmallChange = 1, MinHeight = 44, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(_cooldownPeriod, "冷却半衰期（天）");
        _cooldownPeriod.ValueChanged += (_, args) =>
        {
            if (_syncing || _rolling || !double.IsFinite(args.NewValue)) return;
            _lottery.CooldownHalfLifeDays = args.NewValue;
            OptionsChanged?.Invoke();
        };
        _poolInput = new NumberBox { Header = "数字点名总人数", Value = _lottery.PeopleCount, Minimum = 1, Maximum = 9999,
            SmallChange = 1, LargeChange = 10, MinHeight = 44, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        AutomationProperties.SetName(_poolInput, "数字点名总人数");
        _poolInput.ValueChanged += (_, args) =>
        {
            if (_syncing || _rolling || !double.IsFinite(args.NewValue)) return;
            _lottery.PeopleCount = Math.Clamp((int)Math.Round(args.NewValue), 1, 9999);
            OptionsChanged?.Invoke(); RefreshSummary();
        };
        _resetRound = ActionButton("重置本轮", Glyphs.Restart, () =>
        {
            if (_rolling) return;
            _lottery.Reset(); ResetResults();
            _notice.Text = "已开始新一轮，全部人员重新参与抽选。"; RefreshSummary();
        });
        _history = new TextBlock { FontSize = 12, Opacity = .7, MaxLines = 5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _optionsContent = new StackPanel { Width = 280, Spacing = 14, Children =
        {
            new TextBlock { Text = "点名设置", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            _samplingMode, _cooldownPeriod,
            new TextBlock { Text = "近期抽中越多，概率越低。随时间逐渐恢复，仍可再次抽中。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 },
            _poolInput, _resetRound, _history,
        } };
        var scroll = new ScrollViewer { Content = _optionsContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _options = new Flyout { Content = scroll, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        _options.Opening += (_, _) => UpdateOptionsBounds();
    }

    private void ShowOptions()
    {
        if (!_rolling) _options.ShowAt(_optionsButton);
    }

    private void UpdateOptionsBounds()
    {
        double width = _root.XamlRoot?.Size.Width ?? 320, height = _root.XamlRoot?.Size.Height ?? 480;
        _optionsContent.Width = Math.Max(200, Math.Min(280, width - 64));
        double available = height - 150;
        if (_topBar?.IsLoaded == true && _rollButton?.IsLoaded == true)
        {
            double anchorBottom = _topBar.TransformToVisual(_root).TransformPoint(default).Y + _topBar.ActualHeight;
            double actionTop = _rollButton.TransformToVisual(_root).TransformPoint(default).Y;
            available = Math.Min(available, actionTop - anchorBottom - 48);
        }
        ((ScrollViewer)_options.Content).MaxHeight = Math.Max(80, available);
    }

    private void BuildResultStage()
    {
        _resultTitle = new TextBlock { Text = "抽选结果", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _progress = new ProgressRing { Width = 18, Height = 18, MinWidth = 0, MinHeight = 0, Visibility = Visibility.Collapsed };
        _copy = ActionButton("复制结果", "\uF32C", CopyResult);
        var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(16, 8, 8, 4) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_progress); Grid.SetColumn(_resultTitle, 1); header.Children.Add(_resultTitle);
        Grid.SetColumn(_copy, 2); header.Children.Add(_copy);
        _placeholder = new TextBlock { Text = "准备抽选", FontSize = 48, Opacity = .55 };
        _emptyHost = new Viewbox { Child = _placeholder, Stretch = Stretch.Uniform, MaxHeight = 100, Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _singleName = new TextBlock { FontSize = 96, MaxWidth = 900, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Typography.SetNumeralAlignment(_singleName, FontNumeralAlignment.Tabular);
        _singleHost = new Viewbox { Child = _singleName, Stretch = Stretch.Uniform, MaxHeight = 320, Margin = new Thickness(28), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_singleHost, "单人抽选结果");
        _resultView = new ItemsRepeater { Layout = _resultLayout, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_resultView, "抽选结果");
        _resultView.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border CornerRadius="12" BorderThickness="1" Padding="12" Margin="2" Translation="0,0,3"
                    Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}">
                    <Border.Shadow><ThemeShadow /></Border.Shadow>
                    <Viewbox Stretch="Uniform" StretchDirection="DownOnly">
                        <TextBlock Text="{Binding Name}" FontSize="{Binding FontSize}" MaxWidth="{Binding TextWidth}" FontWeight="SemiBold"
                            Typography.NumeralAlignment="Tabular" TextAlignment="Center" TextWrapping="Wrap"
                            VerticalAlignment="Center" HorizontalAlignment="Stretch" ToolTipService.ToolTip="{Binding Name}" AutomationProperties.Name="{Binding Name}" />
                    </Viewbox>
                </Border>
            </DataTemplate>
            """);
        _resultScroll = new ScrollViewer { Content = _resultView, Padding = new Thickness(12, 8, 12, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        var body = new Grid(); body.Children.Add(_emptyHost); body.Children.Add(_singleHost); body.Children.Add(_resultScroll);
        _notice = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _noticeHost = new Border { Padding = new Thickness(16, 6, 16, 12), Child = _notice, Visibility = Visibility.Collapsed };
        _notice.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => _noticeHost.Visibility = string.IsNullOrWhiteSpace(_notice.Text) ? Visibility.Collapsed : Visibility.Visible);
        var content = new Grid();
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.Children.Add(header); Grid.SetRow(body, 1); content.Children.Add(body);
        Grid.SetRow(_noticeHost, 2); content.Children.Add(_noticeHost);
        _results = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = content };
    }

    private void BuildControls()
    {
        _controls = new Grid { RowSpacing = 14, ColumnSpacing = 12 };
        _controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _controls.ColumnDefinitions.Add(new() { Width = new GridLength(0) });
        for (int row = 0; row < 4; row++) _controls.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _countTitle = new TextBlock { Text = "本次抽选人数", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _controls.Children.Add(_countTitle);
        _minusButton = ActionButton("减少人数", "\uEBCB", () => ChangeCount(DrawCount - 1));
        _plusButton = ActionButton("增加人数", Glyphs.Add, () => ChangeCount(DrawCount + 1));
        foreach (var button in new[] { _minusButton, _plusButton }) _actionLabels[button].Visibility = Visibility.Collapsed;
        _countLabel = new TextBlock { FontSize = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 32 };
        Typography.SetNumeralAlignment(_countLabel, FontNumeralAlignment.Tabular);
        _stepper = new Grid { ColumnSpacing = 4 };
        _stepper.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _stepper.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _stepper.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _stepper.Children.Add(_minusButton); Grid.SetColumn(_countLabel, 1); _stepper.Children.Add(_countLabel);
        Grid.SetColumn(_plusButton, 2); _stepper.Children.Add(_plusButton);
        Grid.SetRow(_stepper, 1); _controls.Children.Add(_stepper);
        _quickRow = new Grid { ColumnSpacing = 6 };
        foreach (int count in new[] { 1, 2, 3, 5, 10 })
        {
            int column = _quickRow.ColumnDefinitions.Count;
            _quickRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var button = new TouchIconButton { Content = count.ToString(), FontSize = 15, MinWidth = 0, MinHeight = 44, Padding = new Thickness(0),
                SelectionBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => ChangeCount(count);
            AutomationProperties.SetName(button, $"抽选 {count} 人");
            Grid.SetColumn(button, column); _quickRow.Children.Add(button); _quickCounts.Add((button, count));
        }
        Grid.SetRow(_quickRow, 2); _controls.Children.Add(_quickRow);
        _rollButton = ActionButton("开始抽选", Glyphs.Rand, () => _ = RollAsync(), primary: true);
        AutomationProperties.SetName(_rollButton, "抽奖");
        Grid.SetRow(_rollButton, 3); _controls.Children.Add(_rollButton);
        _controlCard = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = _controls };
    }

    private void UpdateLayout()
    {
        if (_closed) return;
        double width = _root.XamlRoot?.Size.Width ?? _root.ActualWidth;
        var layout = width >= 720 ? WindowLayout.Wide : width >= 440 ? WindowLayout.Compact : WindowLayout.Mini;
        UpdateOptionsBounds();
        if (_layout == layout) return;
        _layout = layout;
        bool wide = layout == WindowLayout.Wide, mini = layout == WindowLayout.Mini;
        _page.Margin = new Thickness(mini ? 12 : 20, 8, mini ? 12 : 20, mini ? 12 : 20);
        _workspace.ColumnDefinitions[1].Width = new GridLength(wide ? 292 : 0);
        _workspace.ColumnSpacing = wide ? 16 : 0;
        Grid.SetRow(_controlCard, wide ? 0 : 1); Grid.SetColumn(_controlCard, wide ? 1 : 0);
        _controlCard.Padding = new Thickness(wide ? 16 : mini ? 8 : 12);
        _controlCard.VerticalAlignment = wide ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        _controls.ColumnDefinitions[0].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(mini ? 132 : 160);
        _controls.ColumnDefinitions[1].Width = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _controls.ColumnSpacing = mini ? 8 : 12;
        _controls.RowSpacing = wide ? 14 : 8;
        _countTitle.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _quickRow.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(_quickRow, wide ? 2 : 0); Grid.SetColumnSpan(_quickRow, wide ? 1 : 2);
        Grid.SetRow(_stepper, 1);
        Grid.SetRow(_rollButton, wide ? 3 : 1); Grid.SetColumn(_rollButton, wide ? 0 : 1);
        _rollButton.Height = wide ? 64 : 52;
        _actionLabels[_rollButton].FontSize = wide ? 18 : 15;
        foreach (var button in new[] { _importButton, _optionsButton, _copy })
            _actionLabels[button].Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        _strategyChip.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        _countLabel.FontSize = wide ? 36 : 26;
        RefreshStatusChips(); RefreshStartLabel();
        UpdateResultLayout();
    }

    private void RefreshStartLabel()
    {
        bool mini = _layout == WindowLayout.Mini;
        _actionLabels[_rollButton].Text = _rolling ? mini ? "抽选中" : "正在抽选"
            : _lottery.RemainingCount == 0 ? "已抽完" : mini ? "开始" : "开始抽选";
    }

    private void RefreshStatusChips()
    {
        string strategy = _lottery.SamplingMode switch { LotterySamplingMode.SoftCooldown => "软冷却", LotterySamplingMode.Uniform => "均匀随机", _ => "本轮不重复" };
        string state = _lottery.NoRepeat ? $"剩余 {_lottery.RemainingCount} 人" : strategy;
        _summary.Text = _layout == WindowLayout.Mini ? $"{_lottery.PoolSize} 人 · {state}" : $"共 {_lottery.PoolSize} 人";
        _strategyText.Text = _lottery.NoRepeat ? "不重复 · " + state : strategy;
        ToolTipService.SetToolTip(_rosterChip, "管理当前名单 · 共 " + _lottery.PoolSize + " 人");
        ToolTipService.SetToolTip(_strategyChip, _lottery.NoRepeat ? $"本轮剩余 {_lottery.RemainingCount} 人 · 点击调整点名策略" : strategy + " · 点击调整点名策略");
    }

    private void UpdateResultLayout()
    {
        if (_closed || _items.Count < 2 || _resultScroll.ActualWidth <= 0) return;
        double width = Math.Max(1, _resultScroll.ActualWidth - 48);
        if (_measuredCount == _items.Count && Math.Abs(_measuredWidth - width) < 1) return;
        _measuredCount = _items.Count; _measuredWidth = width;
        int columns = _layout == WindowLayout.Mini ? 1 : Math.Clamp((int)((width + 12) / 150), 1, 4);
        columns = Math.Min(columns, _items.Count);
        double itemWidth = Math.Floor((width - (columns - 1) * 12) / columns);
        _resultLayout.MaximumRowsOrColumns = columns;
        _resultLayout.MinItemWidth = Math.Max(1, itemWidth);
        _resultLayout.MinItemHeight = _items.Count <= 10 ? 110 : 88;
        foreach (var item in _items)
        {
            double characters = item.Name.Sum(character => character > 255 ? 1 : .6);
            item.TextWidth = Math.Max(40, itemWidth - 28);
            item.FontSize = Math.Clamp((itemWidth - 28) * 2 / Math.Max(1, characters), 16, _items.Count <= 10 ? 32 : 26);
        }
    }

    private void ApplyPalette()
    {
        int theme = _root.ActualTheme == ElementTheme.Dark ? 2 : 1;
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _results.Background = _controlCard.Background = PopupBrushes.Surface(theme);
        _results.BorderBrush = _controlCard.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        foreach (var text in new[] { _placeholder, _singleName, _resultTitle, _countLabel, _summary, _strategyText, _history, _notice, _countTitle })
            text.Foreground = PopupBrushes.Foreground(theme);
        foreach (var button in _actionLabels.Keys.Where(button => button != _rollButton))
            button.Foreground = PopupBrushes.Foreground(theme);
        foreach (var chip in new[] { _rosterChip, _strategyChip })
            chip.Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(22, 255, 255, 255) : Windows.UI.Color.FromArgb(12, 0, 0, 0));
    }
}
