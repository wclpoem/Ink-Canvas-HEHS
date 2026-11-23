using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>WPF BoardPenPalette: one ink-options panel with pen and highlighter columns.</summary>
public sealed class BoardPenPalette : UserControl
{
    private readonly InkController _controller;
    private readonly AppSettings _settings;
    private readonly SolidColorBrush _foreground;
    private readonly SolidColorBrush _border;
    private readonly List<(Button Button, OriginalMenuCatalog.Swatch Swatch, bool Highlighter, FontIcon Check)> _swatches = new();
    private bool _lightColors;
    private readonly FrameworkElement _nibModeRow;
    private readonly ToggleSwitch _nibMode;
    private bool _refreshingNibMode;
    private readonly PenStrokePreview _penPreview = new() { PreviewHeight = 60 };
    private readonly PenStrokePreview _highlighterPreview = new() { PreviewHeight = 96 };
    private readonly PenStylePicker _styles = new();
    private readonly Slider _width = new() { Minimum = 1, StepFrequency = 1, Width = 106, MinHeight = 0, Height = 28 };
    private bool _refreshingWidth;
    public event Action SettingsChanged;
    public event Action ColorPicked;
    public PopupHeader Header { get; }

    public BoardPenPalette(InkController controller, AppSettings settings, int theme)
    {
        _controller = controller; _settings = settings; _lightColors = theme == 1;
        _penPreview.ApplyTheme(theme); _highlighterPreview.ApplyTheme(theme);
        _styles.Update(theme);
        _foreground = new(theme == 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        _border = new(Windows.UI.Color.FromArgb(theme == 2 ? (byte)153 : (byte)204,
            theme == 2 ? (byte)255 : (byte)0, theme == 2 ? (byte)255 : (byte)0, theme == 2 ? (byte)255 : (byte)0));
        var columns = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 10, 12, 12) };
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(268) });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(130) });
        var pen = new StackPanel { Spacing = 6 };
        var options = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        options.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
        options.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        options.RowDefinitions.Add(new() { Height = GridLength.Auto }); options.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _styles.SelectedIndex = Math.Clamp(settings.Canvas.InkStyle, 0, OriginalMenuCatalog.PenStyles.Count - 1);
        _styles.SelectionChanged += () =>
        {
            if (_refreshingWidth) return;
            settings.Canvas.InkStyle = _styles.SelectedIndex;
            _controller.CurrentAttributes.TaperKind = OriginalMenuCatalog.ToTaperKind(_styles.SelectedIndex);
            NotifySettingsChanged();
            RefreshWidth();
        };
        Add(options, Text("书写笔迹", 12), 0, 0);
        var nibMode = Toggle("笔尖模式", settings.Startup.IsEnableNibMode, value => settings.Startup.IsEnableNibMode = value);
        _nibModeRow = nibMode;
        _nibMode = ((StackPanel)nibMode).Children.OfType<ToggleSwitch>().Single();
        nibMode.Visibility = settings.Appearance.IsEnableDisPlayNibModeToggler ? Visibility.Visible : Visibility.Collapsed;
        Add(options, nibMode, 0, 1);
        AutomationProperties.SetName(_width, "画笔粗细");
        RefreshWidth();
        _width.ValueChanged += (_, e) =>
        {
            if (_refreshingWidth) return;
            if (_controller.CurrentAttributes.TaperKind == TaperKind.InkWash)
            {
                settings.Canvas.InkWashWidth = e.NewValue;
                _controller.InkWashWidth = (float)e.NewValue;
            }
            else
            {
                settings.Canvas.InkWidth = e.NewValue / 2;
                _controller.CurrentAttributes.BaseWidth = (float)(e.NewValue / 2);
            }
            NotifySettingsChanged();
        };
        Add(options, Row("粗细", _width), 1, 0);
        Add(options, Toggle("墨迹识别", settings.InkToShape.IsInkToShapeEnabled, value => settings.InkToShape.IsInkToShapeEnabled = value), 1, 1);
        pen.Children.Add(options);
        var nib = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, FontSize = 12 };
        foreach (string name in OriginalMenuCatalog.PenNibs) nib.Items.Add(name);
        nib.SelectedIndex = Enum.IsDefined(settings.Canvas.PenNib) ? (int)settings.Canvas.PenNib : 0;
        AutomationProperties.SetName(nib, "笔尖");
        nib.SelectionChanged += (_, _) =>
        {
            if (nib.SelectedIndex < 0) return;
            settings.Canvas.PenNib = (PenNib)nib.SelectedIndex;
            controller.CurrentAttributes.Nib = settings.Canvas.PenNib;
            NotifySettingsChanged();
        };
        var nibRow = new Grid { ColumnSpacing = 8 };
        nibRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        nibRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Add(nibRow, Text("笔尖", 12), 0, 0); Add(nibRow, nib, 0, 1);
        pen.Children.Add(nibRow);
        pen.Children.Add(_penPreview);
        var grid = new Grid();
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(53.6) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(45) }); grid.RowDefinitions.Add(new() { Height = new GridLength(45) });
        var themeButton = new Button { Width = 46, Height = 40, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 12,
            Content = _lightColors ? "暗系" : "亮系", Foreground = _foreground };
        AutomationProperties.SetName(themeButton, "切换色板亮系暗系");
        themeButton.Click += (_, _) => { _lightColors = !_lightColors; themeButton.Content = _lightColors ? "暗系" : "亮系"; RefreshSwatches(); };
        Add(grid, themeButton, 0, 0);
        for (int i = 0; i < OriginalMenuCatalog.PenColors.Count; i++)
            Add(grid, Swatch(OriginalMenuCatalog.PenColors[i], false), (i + 1) / 5, (i + 1) % 5);
        pen.Children.Add(grid);
        pen.Children.Add(_styles);
        var highlighter = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        highlighter.Children.Add(Text("荧光笔", 13));
        highlighter.Children.Add(Text("粗细", 10));
        var highlighterWidth = new Slider { Minimum = 1, Maximum = OriginalMenuCatalog.HighlighterWidthMaximum, StepFrequency = 1,
            Value = Math.Clamp((settings.Canvas.HighlighterWidth ?? settings.Canvas.InkWidth) * 2, 1, OriginalMenuCatalog.HighlighterWidthMaximum), MinHeight = 28 };
        AutomationProperties.SetName(highlighterWidth, "荧光笔粗细");
        highlighterWidth.ValueChanged += (_, args) =>
        {
            settings.Canvas.HighlighterWidth = args.NewValue / 2;
            NotifySettingsChanged();
        };
        highlighter.Children.Add(highlighterWidth);
        highlighter.Children.Add(Text("不透明度", 10));
        var alpha = new Slider { Minimum = 20, Maximum = 240, StepFrequency = 20, Value = Math.Clamp(settings.Canvas.InkAlpha, 20, 240), MinHeight = 0, Height = 28 };
        AutomationProperties.SetName(alpha, "荧光笔不透明度");
        alpha.ValueChanged += (_, e) =>
        {
            settings.Canvas.InkAlpha = (int)e.NewValue;
            _controller.CurrentAttributes.HighlighterAlpha = (byte)e.NewValue;
            NotifySettingsChanged();
        };
        highlighter.Children.Add(alpha);
        highlighter.Children.Add(_highlighterPreview);
        var highlighterGrid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (int i = 0; i < 3; i++) highlighterGrid.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
        highlighterGrid.RowDefinitions.Add(new() { Height = new GridLength(28) }); highlighterGrid.RowDefinitions.Add(new() { Height = new GridLength(28) });
        for (int i = 0; i < OriginalMenuCatalog.HighlighterColors.Count; i++)
            Add(highlighterGrid, Swatch(OriginalMenuCatalog.HighlighterColors[i], true), i / 3, i % 3);
        highlighter.Children.Add(highlighterGrid);
        Add(columns, pen, 0, 0); Add(columns, highlighter, 0, 1);
        var content = new StackPanel();
        Header = new PopupHeader("墨迹选项", theme);
        content.Children.Add(Header);
        content.Children.Add(columns);
        Content = new Border { Width = 436, Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(191, 0, 0, 0) : Windows.UI.Color.FromArgb(191, 255, 255, 255)),
            BorderBrush = Theming.PopupBrushes.ToolPanelBorder(theme), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = content };
        RefreshSwatches();
    }

    private FrameworkElement Toggle(string name, bool value, Action<bool> update)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, Width = 44,
            RenderTransform = new ScaleTransform { ScaleX = 0.8, ScaleY = 0.8 }, RenderTransformOrigin = new(0, 0.5) };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => { if (!_refreshingNibMode) { update(toggle.IsOn); NotifySettingsChanged(); } };
        return Row(name, toggle);
    }

    public void RefreshNibMode()
    {
        _refreshingNibMode = true;
        try
        {
            _nibMode.IsOn = _settings.Startup.IsEnableNibMode;
            _nibModeRow.Visibility = _settings.Appearance.IsEnableDisPlayNibModeToggler ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshingNibMode = false; }
    }
    public void RefreshSettings()
    {
        RefreshNibMode();
        RefreshWidth();
        RefreshPreviews();
    }
    private StackPanel Row(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Text(label, 12)); row.Children.Add(control); return row;
    }
    private TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, Foreground = _foreground, VerticalAlignment = VerticalAlignment.Center };
    private Button Swatch(OriginalMenuCatalog.Swatch swatch, bool highlighter)
    {
        var check = new FontIcon { Glyph = "\uF295", FontSize = 14, Visibility = Visibility.Collapsed,
            FontFamily = new(IconFontStyle.Family) };
        var button = new Button { Width = highlighter ? 36 : 40, Height = highlighter ? 25 : 40, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(highlighter ? 4 : 20), BorderBrush = _border, BorderThickness = new Thickness(1), Content = check };
        AutomationProperties.SetName(button, swatch.Name);
        ToolTipService.SetToolTip(button, swatch.Name);
        _swatches.Add((button, swatch, highlighter, check));
        button.Click += (_, _) =>
        {
            _controller.Tool = highlighter ? InkTool.Highlighter : InkTool.Pen;
            _controller.CurrentAttributes.Color = _lightColors ? swatch.LightTheme : swatch.DarkTheme;
            RefreshSwatches(); ColorPicked?.Invoke();
        };
        return button;
    }
    private void NotifySettingsChanged()
    {
        SettingsChanged?.Invoke();
        RefreshPreviews();
    }
    private void RefreshWidth()
    {
        _refreshingWidth = true;
        try
        {
            _styles.SelectedIndex = OriginalMenuCatalog.ToPenStyle(_controller.CurrentAttributes.TaperKind);
            bool waterInk = _controller.CurrentAttributes.TaperKind == TaperKind.InkWash;
            _width.Maximum = waterInk ? 120 : OriginalMenuCatalog.PenWidthMaximum;
            _width.Value = Math.Clamp(waterInk ? _settings.Canvas.InkWashWidth : _settings.Canvas.InkWidth * 2, 1, _width.Maximum);
            AutomationProperties.SetHelpText(_width, waterInk ? "水墨笔刷宽度，范围 1 到 120 像素" : "画笔粗细，范围 1 到 30，步长 1");
        }
        finally { _refreshingWidth = false; }
    }
    private void RefreshPreviews()
    {
        var pen = _controller.GetDrawingAttributes(false);
        if (pen.EffectiveTaper != TaperKind.InkWash) pen.BaseWidth = (float)_settings.Canvas.InkWidth;
        _penPreview.Update(pen, false);
        var highlighter = _controller.CurrentAttributes.Clone();
        highlighter.BaseWidth = (float)(_settings.Canvas.HighlighterWidth ?? _settings.Canvas.InkWidth);
        _highlighterPreview.Update(highlighter, true);
    }
    private void RefreshSwatches()
    {
        RefreshPreviews();
        foreach (var item in _swatches)
        {
            var color = _lightColors ? item.Swatch.LightTheme : item.Swatch.DarkTheme;
            item.Button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));
            item.Check.Foreground = new SolidColorBrush(item.Swatch.Name == "白色" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            item.Check.Visibility = _controller.CurrentAttributes.Color == color && (_controller.Tool == InkTool.Highlighter) == item.Highlighter
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private static void Add(Grid grid, FrameworkElement element, int row, int column)
    { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
}
