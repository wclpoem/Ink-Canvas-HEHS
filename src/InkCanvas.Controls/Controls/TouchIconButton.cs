using InkCanvas.Controls.Animation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace InkCanvas.Controls.Controls;

/// <summary>
/// An icon button that meets the touch contract: at least a 40x40 hit target, no hover-only
/// affordance, a visible selected state, and Composition-driven press feedback.
/// <para>
/// Opt-in WPF floating-bar styling: <see cref="Label"/> adds the 8px caption under the icon,
/// <see cref="SelectionBackground"/> / <see cref="ContentForeground"/> / <see cref="HoverBackground"/>
/// replace the token brushes with the exact upstream FloatBar palette. Defaults keep the
/// original token look so other windows (replay transport) are unaffected.
/// </para>
/// </summary>
public sealed class TouchIconButton : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(TouchIconButton),
        new PropertyMetadata(false, OnIsSelectedChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(TouchIconButton),
        new PropertyMetadata("", OnGlyphChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(TouchIconButton),
        new PropertyMetadata(20d, OnIconSizeChanged));

    private readonly FontIcon _icon;
    private readonly Viewbox _glyphHost;
    private readonly FontIcon _aiText;
    private readonly Border _contentBorder;
    private readonly List<FontIcon> _icons = new();
    private TextBlock _label;
    private UIElement _customContent;
    private Grid _customIcon;
    private StackPanel _lastStack;
    private Grid _lastIconArea;
    private Microsoft.UI.Xaml.Shapes.Path _customIconOutline;
    private string _labelText;
    private bool _showLabel = true;
    private double _labelFontSize = 11;
    private double _labelMaxWidth = double.PositiveInfinity;
    private double _iconAreaHeight = double.NaN;
    private Thickness _labelMargin = new(0, -1, 0, 0);

    public TouchIconButton()
    {
        // Theme-style defaults allow an explicit module style to override touch sizing.
        DefaultStyleKey = typeof(TouchIconButton);

        _icon = new FontIcon
        {
            FontFamily = IconFontStyle.Font,
            FontSize = IconSize,
            IsTextScaleFactorEnabled = false,
            // Apply the current value explicitly: assigning a value equal to the DP default
            // does not raise the change callback, which would leave the icon blank.
            Glyph = Glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _glyphHost = new Viewbox
        {
            Child = _icon,
            Width = Math.Ceiling(IconSize + 4),
            Height = Math.Ceiling(IconSize + 4),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _aiText = AiGlyph.CreateLabel(AiGlyph.ButtonLabelSize);
        _icons.Add(_icon);
        _contentBorder = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ActualThemeChanged += (_, _) => ApplyVisualState();
        Content = _contentBorder;
        RebuildContent();

        PointerPressed += (_, _) => { if (Motion.AnimationsEnabled) Motion.Pulse(this, 0.92f); };
        Loaded += (_, _) =>
        {
            UpdateGlyph();
            ApplyVisualState();
        };
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Override for glyphs that live outside Segoe Fluent Icons (e.g. geometric shapes).</summary>
    public string IconFont
    {
        get => _icon.FontFamily.Source;
        set => _icon.FontFamily = new FontFamily(value);
    }

    /// <summary>Caption under the icon (WPF FloatBarSelectIconTextBlock). Null = icon only.</summary>
    public string Label
    {
        get => _labelText;
        set { _labelText = value; RebuildContent(); ApplyVisualState(); }
    }

    /// <summary>IsEnableDisPlayFloatBarText gate for the caption.</summary>
    public bool ShowLabel
    {
        get => _showLabel;
        set { _showLabel = value; if (_label is not null) _label.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    public double LabelFontSize
    {
        get => _labelFontSize;
        // Stored so a later RebuildContent (IconAreaHeight/LabelMargin change) keeps the size.
        set { _labelFontSize = value; if (_label is not null) _label.FontSize = value; }
    }

    public double LabelMaxWidth
    {
        get => _labelMaxWidth;
        set { _labelMaxWidth = value; RebuildContent(); ApplyVisualState(); }
    }

    /// <summary>
    /// Fixed height of the icon area above the caption (WPF FloatBar buttons use a 22-DIP
    /// FontIcon box with the 8pt caption below, inside the 32-DIP cell). NaN = let the icon
    /// measure naturally. A fixed area keeps the icon centered on the same point whether the
    /// caption is visible or collapsed, and stops font line-box height from shifting layout.
    /// </summary>
    public double IconAreaHeight
    {
        get => _iconAreaHeight;
        set { _iconAreaHeight = value; RebuildContent(); ApplyVisualState(); }
    }

    /// <summary>Caption margin (WPF bar: 1,-1,0,0; gesture button: 0,-2,0,-1).</summary>
    public Thickness LabelMargin
    {
        get => _labelMargin;
        set { _labelMargin = value; RebuildContent(); ApplyVisualState(); }
    }

    /// <summary>Exact FloatBarForeground; when set overrides the token foreground in both states.</summary>
    public Brush ContentForeground { get; set; }

    /// <summary>WPF check-box-background selection wash; when set the button body stays transparent.</summary>
    public Brush SelectionBackground { get; set; }

    /// <summary>Corner radius of the selection wash / content border.</summary>
    public double SelectionCornerRadius
    {
        get => _contentBorder.CornerRadius.TopLeft;
        set => _contentBorder.CornerRadius = new CornerRadius(value);
    }

    /// <summary>FloatBarButton hover/pressed brushes, injected as per-instance template resources.</summary>
    public Brush HoverBackground { get; set; }
    public Brush PressedBackground { get; set; }

    /// <summary>Replace the icon with custom content (composite glyphs, vector pictograms).</summary>
    public void SetContent(UIElement content)
    {
        _customContent = content;
        RebuildContent();
        ApplyVisualState();
    }

    /// <summary>Register an extra FontIcon so theme/selection foreground reaches it.</summary>
    public void AddFontIcon(FontIcon icon)
    {
        icon.IsTextScaleFactorEnabled = false;
        _icons.Add(icon);
        ApplyVisualState();
    }

    /// <summary>Use a native vector mouse-pointer pictogram instead of a font glyph.</summary>
    public void UseMousePointerIcon()
    {
        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(3, 2),
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new PolyLineSegment
        {
            Points = new PointCollection
            {
                new Windows.Foundation.Point(3, 22),
                new Windows.Foundation.Point(8, 17),
                new Windows.Foundation.Point(12, 23),
                new Windows.Foundation.Point(15, 21),
                new Windows.Foundation.Point(11, 15),
                new Windows.Foundation.Point(19, 15),
                new Windows.Foundation.Point(3, 2),
            },
        });
        var geometry = new PathGeometry { Figures = new PathFigureCollection { figure } };
        _customIconOutline = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 24,
            Height = 24,
        };
        _customIcon = new Grid { Width = 24, Height = 24 };
        _customIcon.Children.Add(_customIconOutline);
        SetContent(_customIcon);
    }

    private void RebuildContent()
    {
        UIElement body = _customContent ?? (UIElement)_glyphHost;
        // WinUI does not set FrameworkElement.Parent inside unrooted trees, so Parent-based
        // detaching misses the real owner. Clear the containers we own explicitly instead:
        // the body may sit in _contentBorder, the previous icon area, or the caption stack.
        if (_lastIconArea is not null)
        {
            _lastIconArea.Children.Remove(body);
            _lastIconArea = null;
        }
        if (_lastStack is not null)
        {
            _lastStack.Children.Remove(body);
            _lastStack = null;
        }
        _contentBorder.Child = null;
        if (_labelText is null)
        {
            _label = null;
            _contentBorder.Child = body;
            return;
        }
        _label = new TextBlock
        {
            Text = _labelText,
            FontSize = _labelFontSize,
            MaxWidth = _labelMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = _showLabel ? Visibility.Visible : Visibility.Collapsed,
            Margin = LabelMargin,
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!double.IsNaN(IconAreaHeight) && IconAreaHeight > 0)
        {
            // WPF centers the icon inside a fixed icon area with the caption below; the whole
            // stack is centered in the cell, so the caption never shifts the icon sideways and
            // hiding the caption re-centers the icon exactly on the cell center.
            var area = new Grid { Height = IconAreaHeight };
            area.Children.Add(body);
            stack.Children.Add(area);
            _lastIconArea = area;
        }
        else
        {
            stack.Children.Add(body);
        }
        stack.Children.Add(_label);
        _contentBorder.Child = stack;
        _lastStack = stack;
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TouchIconButton)d).ApplyVisualState();

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TouchIconButton)d).UpdateGlyph();

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (TouchIconButton)d;
        if (button._glyphHost is null) return;
        double size = (double)e.NewValue;
        button._icon.FontSize = size;
        if (button._aiText is not null) button._aiText.FontSize = size;
        // Keep the complete glyph line box inside the icon cell, including its descender.
        button._glyphHost.Width = Math.Ceiling(size + 4);
        button._glyphHost.Height = Math.Ceiling(size + 4);
    }

    private void UpdateGlyph()
    {
        if (_glyphHost is null) return;
        _icon.Glyph = Glyph == Glyphs.Ai ? string.Empty : Glyph ?? string.Empty;
        _glyphHost.Child = Glyph == Glyphs.Ai ? _aiText : _icon;
    }

    /// <summary>Re-apply foreground/selection after theme brush properties change.</summary>
    public void RefreshVisual() => ApplyVisualState();

    /// <summary>Push hover/pressed/foreground template resources for the WPF FloatBarButtonStyle.</summary>
    public void ApplyHoverResources(Brush foreground)
    {
        if (HoverBackground is not null) Resources["ButtonBackgroundPointerOver"] = HoverBackground;
        if (PressedBackground is not null) Resources["ButtonBackgroundPressed"] = PressedBackground;
        Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Resources["ButtonForegroundPointerOver"] = foreground;
        Resources["ButtonForegroundPressed"] = foreground;
        Resources["ButtonForegroundDisabled"] = foreground;
    }

    private void ApplyVisualState()
    {
        Brush fg = ContentForeground
            ?? ThemeBrushes.Resolve(this, IsSelected ? "IcAccentBrush" : "IcTextPrimaryBrush");
        _aiText.Foreground = fg;
        foreach (var icon in _icons) icon.Foreground = fg;
        if (_label is not null) _label.Foreground = fg;

        if (SelectionBackground is not null)
        {
            // WPF paints the selection wash behind the icon stack; the button body keeps its
            // own (transparent or solid) background and hover comes from template resources.
            _contentBorder.Background = IsSelected
                ? SelectionBackground
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            Background = IsSelected
                ? ThemeBrushes.Resolve(this, "IcAccentSoftBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        if (_customIconOutline is not null) _customIconOutline.Stroke = fg;
    }
}
