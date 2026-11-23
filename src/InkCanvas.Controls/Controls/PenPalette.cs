using InkCanvas.Controls.Theming;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>
/// WPF-parity palette, split into two independent menus like the original flyouts:
/// pen mode ("墨迹选项") shows only pen colours + width + pen style; highlighter mode
/// ("荧光笔") shows only highlighter colours + alpha + width. Layout follows upstream
/// MainWindow.xaml PenPalette: 笔锋 combo, 粗细 slider (menu units 1–30), then the colour
/// grid (pen 5×2 circular swatches with the 亮系/暗系 theme cell; highlighter 3×2 rounded).
/// </summary>
public sealed class PenPalette : UserControl
{
    private const double SwatchSize = 40;
    private const double SwatchCell = SwatchSize + 4;
    private const double RowHeight = 32;

    private readonly bool _highlighter;
    private readonly PenStrokePreview _preview = new() { PreviewHeight = 60 };
    private readonly PointerSlider _width = new()
    {
        Minimum = OriginalMenuCatalog.PenWidthMinimum,
        Maximum = OriginalMenuCatalog.PenWidthMaximum,
        StepFrequency = OriginalMenuCatalog.PenWidthStep,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly PointerSlider _alpha = new()
    {
        Minimum = OriginalMenuCatalog.HighlighterAlphaMinimum,
        Maximum = OriginalMenuCatalog.HighlighterAlphaMaximum,
        StepFrequency = OriginalMenuCatalog.HighlighterAlphaStep,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _widthValue = new() { MinWidth = 32, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _alphaValue = new() { Text = "140", MinWidth = 32, TextAlignment = TextAlignment.Right };
    private readonly PenStylePicker _style;
    private readonly SegmentedSelector _nib = new(new[] { "圆形", "斜椭圆" }) { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _alphaAvailability = new()
    {
        Text = "透明度尚未接入笔迹模型，因此不可调整。",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
    };
    private readonly List<(Button Cell, OriginalMenuCatalog.Swatch Swatch, FontIcon Check)> _swatches = new();
    private readonly SolidColorBrush _foreground = new(Microsoft.UI.Colors.Black);
    private readonly SolidColorBrush _swatchBorder = new(Windows.UI.Color.FromArgb(204, 0, 0, 0));
    private readonly Border _card;
    private readonly StackPanel _options;
    private ToggleSwitch _nibMode;
    private Action<bool> _nibModeChanged;
    private bool _configuringNibMode;
    public PopupHeader Header { get; }
    private FontIcon _themeGlyph;
    private TextBlock _themeText;
    private bool _suppress;
    private Action<int> _inkAlphaChanged;
    private int _highlighterAlpha = 140;
    private bool _lightColorScheme;

    public PenPalette(bool highlighter = false)
    {
        _highlighter = highlighter;
        if (!highlighter) _style = new PenStylePicker { HorizontalAlignment = HorizontalAlignment.Stretch };
        if (highlighter) _preview.PreviewHeight = 96;
        _width.Maximum = highlighter ? OriginalMenuCatalog.HighlighterWidthMaximum : OriginalMenuCatalog.PenWidthMaximum;
        if (_style is not null) _style.SelectedIndex = 0;
        Accessible(_nib, "笔尖", "圆形，或长短轴比为 2 比 1 的右斜 45 度椭圆");
        _nib.SelectionChanged += () =>
        {
            if (Controller is null || _suppress || _nib.SelectedIndex < 0) return;
            Controller.Tool = _highlighter ? InkTool.Highlighter : InkTool.Pen;
            Controller.CurrentAttributes.Nib = (PenNib)_nib.SelectedIndex;
            PenNibChanged?.Invoke(Controller.CurrentAttributes.Nib);
            AttributesChanged?.Invoke();
            UpdatePreview();
        };
        _alpha.Value = _highlighterAlpha;
        if (_style is not null)
        {
            Accessible(_style, "笔迹风格", string.Join("、", OriginalMenuCatalog.PenStyles));
            _style.SelectionChanged += () =>
            {
                if (Controller is null || _suppress || _style.SelectedIndex < 0) return;
                Controller.Tool = _highlighter ? InkTool.Highlighter : InkTool.Pen;
                Controller.CurrentAttributes.TaperKind = OriginalMenuCatalog.ToTaperKind(_style.SelectedIndex);
                InkStyleChanged?.Invoke(_style.SelectedIndex);
                AttributesChanged?.Invoke();
                RefreshFromController();
            };
        }

        _width.ValueChanged += (_, e) =>
        {
            if (Controller is null || _suppress) return;
            double snapped = Snap(e.NewValue, OriginalMenuCatalog.PenWidthMinimum,
                OriginalMenuCatalog.PenWidthStep);
            if (Math.Abs(snapped - e.NewValue) > 0.001)
            {
                _width.Value = snapped;
                return;
            }

            bool waterInk = !_highlighter && Controller.CurrentAttributes.TaperKind == TaperKind.InkWash;
            double stored = waterInk ? snapped : OriginalMenuCatalog.ToStoredPenWidth(snapped);
            Controller.Tool = _highlighter ? InkTool.Highlighter : InkTool.Pen;
            // Standard pens retain the original menu scale. Water-ink uses its independent pixel width.
            _widthValue.Text = snapped.ToString("0") + (waterInk ? " px" : "");
            if (waterInk) Controller.InkWashWidth = (float)stored;
            else Controller.CurrentAttributes.BaseWidth = (float)stored;
            InkWidthChanged?.Invoke(stored);
            AttributesChanged?.Invoke();
            UpdatePreview();
        };

        _alpha.ValueChanged += (_, e) =>
        {
            if (Controller is null || _suppress || InkAlphaChanged is null) return;
            int snapped = (int)Snap(e.NewValue, OriginalMenuCatalog.HighlighterAlphaMinimum,
                OriginalMenuCatalog.HighlighterAlphaStep);
            if (Math.Abs(snapped - e.NewValue) > 0.001)
            {
                _alpha.Value = snapped;
                return;
            }
            _highlighterAlpha = snapped;
            Controller.Tool = InkTool.Highlighter;
            _alphaValue.Text = snapped.ToString();
            InkAlphaChanged.Invoke(snapped);
            AttributesChanged?.Invoke();
            UpdatePreview();
        };

        var panel = new StackPanel { Spacing = 4 };
        _options = panel;
        panel.Children.Add(ChoiceRow("笔尖", _nib));
        if (highlighter)
        {
            // 原右栏顺序：荧光笔标题 → 不透明度 → 3×2 色板；粗细为拆分菜单后保留的调节项。
            panel.Children.Add(SliderRow("不透明度", _alpha, _alphaValue, "荧光笔透明度，范围 20 到 240，步长 20"));
            panel.Children.Add(SwatchGrid(OriginalMenuCatalog.HighlighterColors, 3, circular: false));
            panel.Children.Add(SliderRow("粗细", _width, _widthValue, "荧光笔粗细，范围 1 到 120，步长 1"));
            panel.Children.Add(_preview);
            panel.Children.Add(_alphaAvailability);
        }
        else
        {
            panel.Children.Add(SliderRow("粗细", _width, _widthValue, "画笔粗细，范围 1 到 30，步长 1"));
            panel.Children.Add(_preview);
            panel.Children.Add(SwatchGrid(OriginalMenuCatalog.PenColors, 5, circular: true));
            panel.Children.Add(_style);
        }

        Header = new PopupHeader(_highlighter ? "荧光笔" : "墨迹选项", 1);
        Header.CloseRequested += () => RequestClose?.Invoke();
        var stack = new StackPanel();
        stack.Children.Add(Header);
        stack.Children.Add(new Border { Padding = new Thickness(12), Child = panel });
        _card = new Border
        {
            Width = DesignWidthDip,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = PopupBrushes.FloatBarBackground(1),
            BorderBrush = PopupBrushes.FloatBarBorder(1),
            Child = stack,
        };
        Content = _card;
        ApplyChrome(1);
        UpdateAlphaAvailability();
    }

    public void ConfigureNibMode(bool visible, bool enabled, Action<bool> changed)
    {
        _nibModeChanged = changed;
        if (_nibMode is null)
        {
            _nibMode = new ToggleSwitch { Header = "启用笔尖模式" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_nibMode, "启用笔尖模式");
            _nibMode.Toggled += (_, _) => { if (!_configuringNibMode) _nibModeChanged?.Invoke(_nibMode.IsOn); };
            _options.Children.Insert(0, _nibMode);
        }
        _configuringNibMode = true;
        try
        {
            _nibMode.IsOn = enabled;
            _nibMode.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _configuringNibMode = false; }
    }

    /// <summary>Fixed design size (DIP) so the host window can size itself without a rooted measure.</summary>
    public double DesignWidthDip => _highlighter ? 208 : 248;

    /// <summary>
    /// Fallback height for the title, controls, shared preview, color grid and compact style choices.
    /// Hosts normally measure the unrooted control.
    /// </summary>
    public double DesignHeightDip =>
        27 + 2 + 24 + (_highlighter ? 3 : 2) * RowHeight + 4 * 10 + 60 + SwatchCell * 2
        + (!_highlighter ? 2 * RowHeight + 2 : _alphaAvailability.Visibility == Visibility.Visible ? 10 + 32 : 0);

    /// <summary>Apply the exact WPF popup palette for the window theme (0/1/2), fixed at open.</summary>
    public void ApplyChrome(int theme)
    {
        bool dark = PopupBrushes.IsDark(theme);
        _preview.ApplyTheme(theme);
        _style?.Update(theme);
        // WPF PenPalette/BorderDrawShape chrome: FloatBarBackground + FloatBarBorderBrush.
        _card.Background = PopupBrushes.FloatBarBackground(theme);
        _card.BorderBrush = PopupBrushes.ToolPanelBorder(theme);
        Header.ApplyTheme(theme);
        _foreground.Color = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        // WPF FloatBarBorderBrush: Black@0.8 light / White@0.6 dark.
        _swatchBorder.Color = dark
            ? Windows.UI.Color.FromArgb(153, 255, 255, 255)
            : Windows.UI.Color.FromArgb(204, 0, 0, 0);
        foreach (var (cell, _, _) in _swatches) if (cell.Content is Border face) face.BorderBrush = _swatchBorder;
        RefreshThemeCell();
    }

    private Grid ChoiceRow(string label, SegmentedSelector selector)
    {
        var grid = RowGrid();
        var text = Label(label);
        Grid.SetColumn(text, 0);
        Grid.SetColumn(selector, 1);
        grid.Children.Add(text);
        grid.Children.Add(selector);
        return grid;
    }

    private Grid SliderRow(string label, Slider slider, TextBlock readout, string name)
    {
        var grid = RowGrid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Label(label);
        Accessible(slider, name);
        readout.Foreground = _foreground;
        Grid.SetColumn(text, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        return grid;
    }

    private static Grid RowGrid()
    {
        var grid = new Grid { MinHeight = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 14,
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = _foreground,
    };

    private Grid SwatchGrid(IReadOnlyList<OriginalMenuCatalog.Swatch> colors, int columns, bool circular)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SwatchCell) });
        int extra = !_highlighter ? 1 : 0; // 亮系/暗系切换格
        int count = colors.Count + extra;
        int rows = (count + columns - 1) / columns;
        for (int i = 0; i < rows; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SwatchCell) });

        int index = 0;
        if (!_highlighter)
        {
            var themeCell = BuildThemeCell();
            Grid.SetColumn(themeCell, 0);
            Grid.SetRow(themeCell, 0);
            grid.Children.Add(themeCell);
            index = 1;
        }

        for (int i = 0; i < colors.Count; i++, index++)
        {
            var swatch = colors[i];
            var check = new FontIcon
            {
                Glyph = "\uF295",
                FontFamily = IconFontStyle.Font,
                FontSize = 16,
                Foreground = new SolidColorBrush(swatch.Name == "白色"
                    ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cellBorder = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Background = new SolidColorBrush(ToWindowsColor(GetSwatchColor(swatch))),
                BorderBrush = _swatchBorder,
                BorderThickness = new Thickness(circular ? 1.5 : 1),
                CornerRadius = new CornerRadius(circular ? SwatchSize / 2 : 4),
                Child = check,
                Tag = swatch,
            };
            // Button semantics: screen-reader name + UIA Invoke (headless tests), same look.
            var cell = new Button
            {
                Content = cellBorder,
                Width = SwatchSize,
                Height = SwatchSize,
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Accessible(cell, swatch.Name, "选择此墨迹颜色");
            cell.Click += (_, _) =>
            {
                if (Controller is null || _suppress) return;
                Controller.Tool = _highlighter ? InkTool.Highlighter : InkTool.Pen;
                Controller.CurrentAttributes.Color = GetSwatchColor(swatch);
                AttributesChanged?.Invoke();
                UpdatePreview();
                RefreshFromController();
                // WPF ColorSwitchCheck -> HideSubPanels("color") closes the palette on a
                // colour pick (MW_PenColors.cs:34-37). Width/style/alpha edits keep it open.
                if (!Header.IsPinned) RequestClose?.Invoke();
            };
            _swatches.Add((cell, swatch, check));
            Grid.SetColumn(cell, index % columns);
            Grid.SetRow(cell, index / columns);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>原色板首格：亮系/暗系切换（E706/E708 + 文字），只改墨迹配色，不改窗口主题。</summary>
    private Button BuildThemeCell()
    {
        _themeGlyph = new FontIcon
        {
            FontFamily = IconFontStyle.Font,
            FontSize = 14,
            Foreground = _foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _themeText = new TextBlock
        {
            FontSize = 9,
            Foreground = _foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(_themeGlyph);
        stack.Children.Add(_themeText);
        var cellBorder = new Border
        {
            Width = SwatchSize,
            Height = SwatchSize,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = _swatchBorder,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(SwatchSize / 2),
            Child = stack,
        };
        var cell = new Button
        {
            Content = cellBorder,
            Width = SwatchSize,
            Height = SwatchSize,
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Accessible(cell, "切换亮系/暗系墨迹颜色");
        cell.Click += (_, _) => LightColorScheme = !LightColorScheme;
        RefreshThemeCell();
        return cell;
    }

    /// <summary>Outer card used by the window to fit and clip its native backdrop.</summary>
    public Border CardSurface => _card;

    private void RefreshThemeCell()
    {
        if (_themeGlyph is null) return;
        // 与原版一致：显示将要切换到的目标色系。
        _themeGlyph.Glyph = _lightColorScheme ? "\uF33C" : "\uF8BA";
        _themeText.Text = _lightColorScheme ? "暗系" : "亮系";
    }

    private InkColor GetSwatchColor(OriginalMenuCatalog.Swatch swatch) =>
        _lightColorScheme ? swatch.LightTheme : swatch.DarkTheme;

    private static Windows.UI.Color ToWindowsColor(InkColor color) =>
        Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static double Snap(double value, double minimum, double step) =>
        minimum + Math.Round((value - minimum) / step, MidpointRounding.AwayFromZero) * step;

    private static void Accessible(DependencyObject control, string name, string helpText = null)
    {
        AutomationProperties.SetName(control, name);
        if (!string.IsNullOrWhiteSpace(helpText)) AutomationProperties.SetHelpText(control, helpText);
    }

    public InkController Controller { get; set; }

    public Action RequestClose { get; set; }

    /// <summary>Persists the active brush width. Water-ink uses pixels, other pens use Canvas.InkWidth units.</summary>
    public Action<double> InkWidthChanged { get; set; }
    public Action<int> InkStyleChanged { get; set; }
    public Action<PenNib> PenNibChanged { get; set; }
    public Action<int> InkAlphaChanged
    {
        get => _inkAlphaChanged;
        set
        {
            _inkAlphaChanged = value;
            _suppress = true;
            try
            {
                if (_inkAlphaChanged is not null)
                {
                    _alpha.Value = Math.Clamp(_highlighterAlpha, OriginalMenuCatalog.HighlighterAlphaMinimum,
                        OriginalMenuCatalog.HighlighterAlphaMaximum);
                    _alphaValue.Text = _highlighterAlpha.ToString();
                }
            }
            finally
            {
                _suppress = false;
            }
            UpdateAlphaAvailability();
        }
    }

    public int HighlighterAlpha
    {
        get => _highlighterAlpha;
        set
        {
            _highlighterAlpha = (int)Snap(Math.Clamp(value,
                (int)OriginalMenuCatalog.HighlighterAlphaMinimum,
                (int)OriginalMenuCatalog.HighlighterAlphaMaximum),
                OriginalMenuCatalog.HighlighterAlphaMinimum, OriginalMenuCatalog.HighlighterAlphaStep);
            if (InkAlphaChanged is null) return;
            _suppress = true;
            try
            {
                _alpha.Value = _highlighterAlpha;
                _alphaValue.Text = _highlighterAlpha.ToString();
            }
            finally { _suppress = false; }
        }
    }

    /// <summary>Raised after a color, width, style or alpha selection is applied.</summary>
    public Action AttributesChanged { get; set; }

    /// <summary>Controls which of the original theme-specific nine pen swatches is used.</summary>
    public bool LightColorScheme
    {
        get => _lightColorScheme;
        set
        {
            if (_lightColorScheme == value) return;
            _lightColorScheme = value;
            RefreshFromController();
            RefreshThemeCell();
        }
    }

    public void RefreshFromController()
    {
        if (Controller is null) return;
        _suppress = true;
        try
        {
            bool waterInk = !_highlighter && Controller.CurrentAttributes.TaperKind == TaperKind.InkWash;
            _width.Maximum = waterInk ? 120 : _highlighter ? OriginalMenuCatalog.HighlighterWidthMaximum : OriginalMenuCatalog.PenWidthMaximum;
            Accessible(_width, _highlighter ? "荧光笔粗细" : "画笔粗细", waterInk ? "水墨笔刷宽度，范围 1 到 120 像素"
                : _highlighter ? "荧光笔粗细，范围 1 到 120，步长 1" : "画笔粗细，范围 1 到 30，步长 1");
            double menuWidth = Math.Clamp(waterInk ? Controller.InkWashWidth : OriginalMenuCatalog.ToMenuPenWidth(Controller.GetDrawingAttributes(_highlighter).BaseWidth),
                OriginalMenuCatalog.PenWidthMinimum, _width.Maximum);
            _width.Value = menuWidth;
            _nib.SelectedIndex = Enum.IsDefined(Controller.CurrentAttributes.Nib) ? (int)Controller.CurrentAttributes.Nib : 0;
            _widthValue.Text = menuWidth.ToString("0") + (waterInk ? " px" : "");
            if (_style is not null) _style.SelectedIndex = OriginalMenuCatalog.ToPenStyle(Controller.CurrentAttributes.TaperKind);
            if (InkAlphaChanged is not null)
            {
                _alpha.Value = Math.Clamp(HighlighterAlpha,
                    OriginalMenuCatalog.HighlighterAlphaMinimum,
                    OriginalMenuCatalog.HighlighterAlphaMaximum);
                _alphaValue.Text = HighlighterAlpha.ToString();
            }
            var current = Controller.CurrentAttributes.Color;
            foreach (var (cell, swatch, check) in _swatches)
            {
                InkColor candidate = GetSwatchColor(swatch);
                if (cell.Content is Border face) face.Background = new SolidColorBrush(ToWindowsColor(candidate));
                bool selected = candidate.R == current.R && candidate.G == current.G && candidate.B == current.B;
                check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _suppress = false;
            UpdateAlphaAvailability();
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (Controller is null) return;
        _preview.Update(Controller.GetDrawingAttributes(_highlighter), _highlighter);
    }

    private void UpdateAlphaAvailability()
    {
        if (_highlighter)
        {
            bool wired = InkAlphaChanged is not null;
            _alpha.IsEnabled = wired;
            _alphaAvailability.Text = wired
                ? "原菜单范围 20–240，步长 20。"
                : "透明度尚未接入笔迹模型，因此不可调整。";
            _alphaAvailability.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
            Accessible(_alpha, "荧光笔透明度", wired
                ? "原菜单范围 20 到 240，步长 20"
                : "暂不可用：荧光笔透明度尚未接入笔迹模型");
        }
    }
}
