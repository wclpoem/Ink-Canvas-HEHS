using InkCanvas.Controls.Controls;
using InkCanvas.Ink;
using InkCanvas.Controls.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly StackPanel _fileCommands = CommandGroup(), _readingCommands = CommandGroup(), _documentCommands = CommandGroup();
    private TouchIconButton _openButton, _previousPage, _nextPage, _zoomOut, _zoomIn, _fitToggle, _searchButton, _readButton;
    private SplitButton _saveButton;
    private Button _importButton;
    private TextBlock _importLabel;
    private ToggleMenuFlyoutItem _readMenuItem;
    private Func<Task> _openDocument;
    private InkTool _writingTool = InkTool.Pen;

    private static StackPanel CommandGroup() => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
    };

    private TouchIconButton Command(string label, string glyph, Action action)
    {
        var button = new TouchIconButton
        {
            Label = label, ShowLabel = false, Glyph = glyph, IconFont = "Segoe Fluent Icons",
            Width = 40, Height = 40, MinWidth = 40, MinHeight = 40, IconSize = 18, Padding = new Thickness(6),
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        _commandButtons.Add(button);
        return button;
    }

    private TouchIconButton Command(string label, Symbol symbol, Action action) => Command(label, ((char)symbol).ToString(), action);

    private static IconElement NativeIcon(Symbol symbol) => new SymbolIcon(symbol) { RenderTransform = new ScaleTransform { ScaleX = .8, ScaleY = .8 }, RenderTransformOrigin = new(.5, .5) };

    private static Border Divider() => new()
    {
        Width = 1, Height = 20, Margin = new Thickness(5, 0, 5, 0),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
    };

    private static MenuFlyoutItem MenuCommand(string title, Symbol symbol, Action action)
    {
        var item = new MenuFlyoutItem { Text = title, Icon = new SymbolIcon(symbol) };
        item.Click += (_, _) => action();
        return item;
    }

    private void ConfigureToolbar(Func<Task> open, Func<Task> import)
    {
        _openDocument = open;
        _commandHost.Height = 44;
        _commandHost.Padding = new Thickness(8, 0, 8, 0);
        _commandHost.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _commandHost.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _commandHost.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _fileCommands.HorizontalAlignment = HorizontalAlignment.Left;
        _documentCommands.HorizontalAlignment = HorizontalAlignment.Right;
        _commandHost.Children.Add(_fileCommands);
        Grid.SetColumn(_readingCommands, 1); _commandHost.Children.Add(_readingCommands);
        Grid.SetColumn(_documentCommands, 2); _commandHost.Children.Add(_documentCommands);
        _root.Children.Add(_commandHost);
        AutomationProperties.SetName(_commandHost, "PDF 阅读工具栏");

        _sidebarToggle = Command("显示或隐藏 PDF 侧栏", Symbol.List, () => SetSidebarOpen(!_split.IsPaneOpen));
        _openButton = Command("打开", Symbol.OpenFile, async () => await open());
        ToolTipService.SetToolTip(_openButton, "打开 PDF (Ctrl+O)");
        var saveMenu = new MenuFlyout();
        saveMenu.Items.Add(MenuCommand("保存 (Ctrl+S)", Symbol.Save, async () => await SaveAsync()));
        saveMenu.Items.Add(MenuCommand("另存为… (Ctrl+Shift+S)", Symbol.SaveLocal, async () => await SaveAsync(true)));
        _saveButton = new SplitButton
        {
            Content = NativeIcon(Symbol.Save), Flyout = saveMenu, MinHeight = 40, Padding = new Thickness(8, 4, 8, 4),
            IsEnabled = !_file.ReadOnly, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(_saveButton, "保存 PDF");
        ToolTipService.SetToolTip(_saveButton, _file.ReadOnly ? "此 PDF 只允许查看" : "保存 PDF (Ctrl+S)，展开可另存为");
        _saveButton.Click += async (_, _) => await SaveAsync();
        _fileCommands.Children.Add(_sidebarToggle); _fileCommands.Children.Add(Divider());
        _fileCommands.Children.Add(_openButton); _fileCommands.Children.Add(_saveButton);

        _previousPage = Command("上一页", Symbol.Back, () => GoToPage(_pageIndex - 1));
        _nextPage = Command("下一页", Symbol.Forward, () => GoToPage(_pageIndex + 1));
        _zoomOut = Command("缩小", Symbol.ZoomOut, () => ZoomBy(.8f));
        _zoomIn = Command("放大", Symbol.ZoomIn, () => ZoomBy(1.25f));
        _fitToggle = Command("切换整页或页宽", "\uE9A6", () => Fit(_fitMode == FitMode.Width ? FitMode.Page : FitMode.Width));
        ToolTipService.SetToolTip(_zoomOut, "缩小 (Ctrl+-)");
        ToolTipService.SetToolTip(_zoomIn, "放大 (Ctrl++)");
        ToolTipService.SetToolTip(_zoom, "缩放比例 · Ctrl + 滚轮缩放 · 拖动浏览页面");
        AutomationProperties.SetName(_zoom, "PDF 缩放比例");
        var zoomMenu = new MenuFlyout();
        zoomMenu.Items.Add(MenuCommand("适合页宽", Symbol.FullScreen, () => Fit(FitMode.Width)));
        zoomMenu.Items.Add(MenuCommand("适合整页 (Ctrl+0)", Symbol.FullScreen, () => Fit(FitMode.Page)));
        zoomMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (int percentage in new[] { 50, 75, 100, 125, 150, 200, 400 })
        {
            var preset = new MenuFlyoutItem { Text = percentage + "%" };
            preset.Click += (_, _) =>
            {
                float current = MathF.Sqrt(MathF.Abs(Controller.ViewTransform.GetDeterminant()));
                ZoomBy(percentage / 100f / current);
            };
            zoomMenu.Items.Add(preset);
        }
        _zoom.Flyout = zoomMenu;
        foreach (var control in new UIElement[] { _previousPage, _pageNumber, _pageCount, _nextPage, Divider(), _zoomOut, _zoom, _zoomIn, _fitToggle })
            _readingCommands.Children.Add(control);

        _searchButton = Command("搜索", Symbol.Find, OpenSearch);
        ToolTipService.SetToolTip(_searchButton, "查找文档内容 (Ctrl+F)");
        _readButton = Command("阅读模式", "\uE736", ToggleReadMode);
        ToolTipService.SetToolTip(_readButton, "阅读模式 · 单指拖动浏览，关闭后使用主工具栏批注");
        var more = Command("更多 PDF 操作", Symbol.More, () => { });
        var moreMenu = new MenuFlyout();
        moreMenu.Items.Add(MenuCommand("打开 PDF (Ctrl+O)", Symbol.OpenFile, async () => await open()));
        moreMenu.Items.Add(MenuCommand("搜索文档 (Ctrl+F)", Symbol.Find, OpenSearch));
        _readMenuItem = new ToggleMenuFlyoutItem { Text = "阅读模式" };
        _readMenuItem.Click += (_, _) => ToggleReadMode();
        moreMenu.Items.Add(_readMenuItem);
        moreMenu.Items.Add(MenuCommand("适合页宽", Symbol.FullScreen, () => Fit(FitMode.Width)));
        moreMenu.Items.Add(MenuCommand("适合整页", Symbol.FullScreen, () => Fit(FitMode.Page)));
        more.Flyout = moreMenu;
        _importLabel = new TextBlock { Text = "导入画板", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var importContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        importContent.Children.Add(NativeIcon(Symbol.Import)); importContent.Children.Add(_importLabel);
        _importButton = new Button
        {
            Content = importContent, MinHeight = 40, Padding = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(8),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        AutomationProperties.SetName(_importButton, "导入画板");
        ToolTipService.SetToolTip(_importButton, "将 PDF 页面与批注导入白板");
        _importButton.Click += async (_, _) => await import();
        foreach (var control in new UIElement[] { _searchButton, _readButton, more, _importButton }) _documentCommands.Children.Add(control);
        DeferredLayout.Observe(_root, UpdateToolbarLayout);
    }

    private void UpdateToolbarLayout()
    {
        if (_closed) return;
        double width = _root.XamlRoot?.Size.Width ?? _root.ActualWidth;
        if (width <= 0) return;
        _zoomIn.Visibility = _zoomOut.Visibility = width >= 1040 ? Visibility.Visible : Visibility.Collapsed;
        _searchButton.Visibility = _readButton.Visibility = width >= 920 ? Visibility.Visible : Visibility.Collapsed;
        _openButton.Visibility = width >= 820 ? Visibility.Visible : Visibility.Collapsed;
        _fitToggle.Visibility = width >= 700 ? Visibility.Visible : Visibility.Collapsed;
        _importLabel.Visibility = width >= 600 ? Visibility.Visible : Visibility.Collapsed;
        _importButton.Padding = new Thickness(width >= 600 ? 12 : 8, 4, width >= 600 ? 12 : 8, 4);
    }

    private void CommitPageNumber()
    {
        if (_closed || _pageChanging || Controller is null) return;
        if (int.TryParse(_pageNumber.Text, out int page)) GoToPage(Math.Clamp(page, 1, _file.Pages.Count) - 1);
        _pageNumber.Text = (_pageIndex + 1).ToString();
    }

    private void ToggleReadMode()
    {
        if (Controller is null) return;
        Controller.Tool = Controller.Tool == InkTool.Pan && !_file.ReadOnly ? _writingTool : InkTool.Pan;
        UpdateReadingControls();
    }

    private void UpdateReadingControls()
    {
        if (_closed || Controller is null) return;
        UpdateThumbnailSelection();
        _previousPage.IsEnabled = _pageIndex > 0;
        _nextPage.IsEnabled = _pageIndex < _file.Pages.Count - 1;
        bool reading = Controller.Tool == InkTool.Pan;
        if (!reading) _writingTool = Controller.Tool;
        _readButton.IsSelected = _readMenuItem.IsChecked = reading;
        ToolTipService.SetToolTip(_fitToggle, _fitMode == FitMode.Width ? "切换为适合整页" : "切换为适合页宽");
    }
}
