using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Controls.Theming;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared compact selection actions for the desktop and board.</summary>
public sealed class SelectionToolbar : UserControl
{
    private readonly Func<InkController> _controller;
    private readonly StackPanel _primary = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly Border _secondary = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TouchIconButton _fold, _clone, _style, _geometry;
    private string _section;
    private bool _collapsed;
    private int _theme = 1;
    private string _imageSelection;
    private Action _refreshProperties;
    private Action _refreshLayers;

    public SelectionToolbar(Func<InkController> controller, Action export, Action sendToBoard, string sendLabel,
        Action ungroup = null, Action<string> aiEdit = null)
    {
        _controller = controller;
        _fold = Button("收起", "\uF2AB", ToggleCollapsed);
        _clone = Button("克隆", "\uF32C", () => { Controller.CloneMode = !Controller.CloneMode; Refresh(); });
        _style = Button("样式", "\uF2F6", () => ToggleSection("样式", BuildStyle));
        _geometry = Button("图形", Glyphs.ShapesWpf, () => ToggleSection("图形", BuildGeometry));
        _actions.Children.Add(_clone);
        _actions.Children.Add(_style);
        _actions.Children.Add(_geometry);
        _actions.Children.Add(Button("变换", "\uF13E", () => ToggleSection("变换", BuildTransform)));
        _actions.Children.Add(Button("层级", "\uF7BE", () => ToggleSection("层级", BuildLayers)));
        var more = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Button("存为图片", "\uE91B", () => { ClosePanels(); export(); }),
                Button(sendLabel, "\uF379", () => { ClosePanels(); sendToBoard(); }),
                Button("取消选择", "\uF36A", () => Controller.ClearSelection()),
            },
        };
        if (ungroup is not null)
            more.Children.Add(Button("取消 AI 分组", "\uF7BE", () => { ClosePanels(); ungroup(); }));
        if (aiEdit is not null)
        {
            more.Children.Add(Button("AI 字号+", "\uF10A", () => { ClosePanels(); aiEdit("字号大一点"); }));
            more.Children.Add(Button("AI 两列", "\uE833", () => { ClosePanels(); aiEdit("改成两列"); }));
            more.Children.Add(Button("只保留公式", "\uE600", () => { ClosePanels(); aiEdit("只保留公式"); }));
        }
        _actions.Children.Add(Button("更多", "\uE833", () => ToggleSection("更多", () => more)));
        _actions.Children.Add(Button("删除", "\uF34D", () => Controller.DeleteSelection()));
        _primary.Children.Add(_fold);
        _primary.Children.Add(_actions);
        Content = new StackPanel { Children = { _primary, _secondary } };
    }

    private InkController Controller => _controller();
    public bool IsCollapsed => _collapsed;
    public event Action LayoutChanged;

    public void ApplyTheme(int theme)
    {
        _theme = theme;
        RequestedTheme = PopupBrushes.IsDark(theme) ? ElementTheme.Dark : ElementTheme.Light;
        ThemeChildren(_primary);
        ThemeChildren(_secondary);
    }

    private void ThemeChildren(UIElement element)
    {
        if (element is PopupHeader header) header.ApplyTheme(_theme);
        else if (element is TouchIconButton button) ThemeButton(button);
        else if (element is TextBlock text) text.Foreground = PopupBrushes.FloatBarForeground(_theme);
        else if (element is Panel panel)
            foreach (var child in panel.Children) ThemeChildren(child);
        else if (element is Border border && border.Child is not null) ThemeChildren(border.Child);
    }

    private void ThemeButton(TouchIconButton button)
    {
        button.ContentForeground = PopupBrushes.FloatBarForeground(_theme);
        button.HoverBackground = PopupBrushes.FloatBarButtonHover(_theme);
        button.PressedBackground = PopupBrushes.FloatBarButtonPressed(_theme);
        button.SelectionBackground = PopupBrushes.FloatBarSelected();
        button.ApplyHoverResources(button.ContentForeground);
        button.RefreshVisual();
        if (button.Label is "线型" or "原图线型" or "实线" or "虚线" or "点线")
        {
            var kind = button.Label == "点线" ? ShapeKind.DotLine : button.Label == "实线" ? ShapeKind.Line : ShapeKind.DashedLine;
            button.SetContent(new Viewbox { Width = 28, Height = 18, Stretch = Stretch.Uniform,
                Child = ShapeIcons.Create(kind, PopupBrushes.FloatBarForeground(_theme)) });
        }
    }

    public void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        _actions.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _fold.Label = _collapsed ? "展开" : "收起";
        _fold.Glyph = _collapsed ? "\uF2B1" : "\uF2AB";
        AutomationProperties.SetName(_fold, _fold.Label);
        ClosePanels();
        LayoutChanged?.Invoke();
    }

    public void ClosePanels()
    {
        if (_section is null) return;
        _section = null;
        _refreshProperties = null;
        _refreshLayers = null;
        _secondary.Child = null;
        _secondary.Visibility = Visibility.Collapsed;
        LayoutChanged?.Invoke();
    }

    public void Refresh()
    {
        _clone.IsSelected = Controller.CloneMode;
        var images = SelectedImages();
        _style.IsEnabled = Controller.Selection.Count > 0 || images.Length > 0;
        _geometry.IsEnabled = Controller.HasEditableShapes;
        if (Controller.PreviewStrokes is not null) return;
        _style.Label = images.Length > 0 ? "属性" : "样式";
        string selection = string.Join(",", images.Select(image => image.Id)) + (Controller.Selection.Count > 0 ? "|ink" : "");
        if (selection != _imageSelection)
        {
            _imageSelection = selection;
            ClosePanels();
        }
        _refreshProperties?.Invoke();
        _refreshLayers?.Invoke();
    }

    private void ToggleSection(string name, Func<UIElement> build)
    {
        if (_section == name) { ClosePanels(); return; }
        _section = name;
        _refreshProperties = null;
        _refreshLayers = null;
        var header = new PopupHeader(name, _theme, canPin: false);
        header.CloseRequested += ClosePanels;
        _secondary.Child = new StackPanel { Children = { header, build() } };
        _secondary.Visibility = Visibility.Visible;
        LayoutChanged?.Invoke();
    }

    private UIElement BuildStyle()
    {
        if (Controller.Selection.Count == 0 && SelectedImages().Length > 0) return BuildImageProperties();
        var colors = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 5; i++) colors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        colors.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        colors.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        int index = 0;
        foreach (var (name, color) in new[]
        {
            ("黑色", InkColor.Opaque(0, 0, 0)), ("白色", InkColor.Opaque(255, 255, 255)),
            ("红色", InkColor.Opaque(220, 38, 38)), ("橙色", InkColor.Opaque(234, 88, 12)),
            ("黄色", InkColor.Opaque(234, 179, 8)), ("绿色", InkColor.Opaque(22, 163, 74)),
            ("青色", InkColor.Opaque(13, 148, 136)), ("蓝色", InkColor.Opaque(37, 99, 235)),
            ("粉色", InkColor.Opaque(199, 46, 199)),
        })
        {
            var button = Button(name, "", () => Controller.RecolorSelection(color));
            button.Height = 44;
            button.SetContent(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128)), BorderThickness = new Thickness(1) });
            Grid.SetColumn(button, index % 5);
            Grid.SetRow(button, index / 5);
            colors.Children.Add(button);
            index++;
        }
        var panel = new StackPanel { Spacing = 4, Children =
        {
            colors,
            new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                Button("加粗", "\uF10A", () => Controller.ScaleSelectionWidth(1.25f)),
                Button("变细", "\uEBCB", () => Controller.ScaleSelectionWidth(.8f)),
                Button("恢复粗细", "\uF1A0", () => Controller.RestoreSelectionWidth()),
            } },
        } };
        if (SelectedImages().Length > 0) panel.Children.Add(BuildImageProperties());
        return panel;
    }

    private UIElement BuildLayers()
    {
        var choices = new[]
        {
            ("置于顶层", "\uE74A", LayerAction.BringToFront),
            ("上移一层", "\uF2B7", LayerAction.MoveForward),
            ("下移一层", "\uF2A4", LayerAction.MoveBackward),
            ("置于底层", "\uE74B", LayerAction.SendToBack),
        };
        var panel = new StackPanel();
        var buttons = new List<(TouchIconButton Button, LayerAction Action)>();
        for (int rowIndex = 0; rowIndex < 2; rowIndex++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, glyph, action) in choices.Skip(rowIndex * 2).Take(2))
            {
                var button = Button(label, glyph, () => { Controller.ArrangeSelection(action); Refresh(); });
                row.Children.Add(button); buttons.Add((button, action));
            }
            panel.Children.Add(row);
        }
        _refreshLayers = () =>
        {
            foreach (var (button, action) in buttons) button.IsEnabled = Controller.CanArrangeSelection(action);
        };
        _refreshLayers();
        return panel;
    }

    private UIElement BuildGeometry()
    {
        var edit = Button("控制点", "\uF3DD", () => { Controller.ToggleShapeEditing(); ClosePanels(); });
        var fill = Button("填充", "\uF2F6", () => ToggleSection("图形填充", BuildShapeFill));
        var line = Button("线型", "\uF8D9", () => ToggleSection("图形线型", BuildShapeLines));
        var connect = Button("连接线", "\uE71B", () => { Controller.ConnectSelectedShapes(); Refresh(); });
        var detach = Button("解除连接", "\uE7A4", () => { Controller.DetachShapeConnections(); Refresh(); });
        _refreshProperties = () =>
        {
            edit.IsEnabled = Controller.CanEditShape; edit.IsSelected = Controller.ShapeEditEnabled;
            fill.IsEnabled = Controller.CanFillShapes;
            connect.IsEnabled = Controller.CanConnectShapes;
            detach.IsEnabled = Controller.HasConnectedShapes;
        };
        _refreshProperties();
        return new StackPanel { Spacing = 4, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, fill, line } },
            new StackPanel { Orientation = Orientation.Horizontal, Children = { connect, detach } },
        } };
    }

    private UIElement BuildShapeLines()
    {
        var panel = new StackPanel();
        var choices = new[] { ("原图线型", ShapeLineStyle.Original), ("实线", ShapeLineStyle.Solid),
            ("虚线", ShapeLineStyle.Dashed), ("点线", ShapeLineStyle.Dotted) };
        for (int rowIndex = 0; rowIndex < 2; rowIndex++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, style) in choices.Skip(rowIndex * 2).Take(2))
                row.Children.Add(Button(label, "\uF8D9", () => Controller.SetShapeLineStyle(style)));
            panel.Children.Add(row);
        }
        return panel;
    }

    private UIElement BuildShapeFill()
    {
        var panel = new StackPanel();
        panel.Children.Add(Button("无填充", "\uF36A", () => Controller.SetShapeFill(null)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, color) in new[] { ("红色", InkColor.Opaque(220, 38, 38)), ("黄色", InkColor.Opaque(250, 204, 21)),
            ("绿色", InkColor.Opaque(22, 163, 74)), ("蓝色", InkColor.Opaque(37, 99, 235)), ("白色", InkColor.Opaque(255, 255, 255)) })
        {
            var button = Button(label, "", () => Controller.SetShapeFill(color));
            button.SetContent(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128)), BorderThickness = new Thickness(1) });
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        return panel;
    }

    private IImageSelectionElement[] SelectedImages() => Controller.SelectedElements.OfType<IImageSelectionElement>()
        .Where(image => image.ImageData is { Length: > 0 }).ToArray();

    private UIElement BuildImageProperties()
    {
        bool keepAspect = true;
        bool refreshing = false;
        SegmentedSelector opacity = null;
        var width = new TextBlock { FontSize = 12, Foreground = PopupBrushes.FloatBarForeground(_theme),
            Width = 112, VerticalAlignment = VerticalAlignment.Center };
        var height = new TextBlock { FontSize = 12, Foreground = PopupBrushes.FloatBarForeground(_theme),
            Width = 112, VerticalAlignment = VerticalAlignment.Center };
        TouchIconButton aspect = null;
        aspect = Button("锁定比例", "\uE79E", () => { keepAspect = !keepAspect; aspect.IsSelected = keepAspect; });
        aspect.IsSelected = true;
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            width, Button("减宽", "\uE738", () => Resize(true, .9f)), Button("加宽", "\uF10A", () => Resize(true, 1.1f)),
        } });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            height, Button("减高", "\uE738", () => Resize(false, .9f)), Button("增高", "\uF10A", () => Resize(false, 1.1f)),
        } });
        panel.Children.Add(aspect);
        panel.Children.Add(new TextBlock { Text = "不透明度", FontSize = 12, Foreground = PopupBrushes.FloatBarForeground(_theme) });
        opacity = new SegmentedSelector(new[] { "0%", "25%", "50%", "75%", "100%" }) { Width = 220 };
        opacity.RequestedTheme = PopupBrushes.IsDark(_theme) ? ElementTheme.Dark : ElementTheme.Light;
        opacity.SelectedIndex = (int)Math.Round(SelectedImages()[0].Opacity * 4);
        opacity.SelectionChanged += () => { if (!refreshing) Controller.SetSelectionImageOpacity(opacity.SelectedIndex / 4f); };
        panel.Children.Add(opacity);
        _refreshProperties = UpdateSize;
        UpdateSize();
        return panel;

        void UpdateSize()
        {
            var frame = Controller.SelectionFrame;
            width.Text = $"宽度 {frame.Bounds.Width * Vector2.TransformNormal(Vector2.UnitX, frame.Transform).Length():0.#}";
            height.Text = $"高度 {frame.Bounds.Height * Vector2.TransformNormal(Vector2.UnitY, frame.Transform).Length():0.#}";
            refreshing = true;
            var images = SelectedImages();
            if (images.Length > 0) opacity.SelectedIndex = (int)Math.Round(images[0].Opacity * 4);
            refreshing = false;
        }

        void Resize(bool horizontal, float factor)
        {
            var frame = Controller.SelectionFrame;
            if (!Matrix3x2.Invert(frame.Transform, out var inverse)) return;
            var local = Matrix3x2.CreateTranslation(-frame.Bounds.Center)
                * Matrix3x2.CreateScale(horizontal || keepAspect ? factor : 1, !horizontal || keepAspect ? factor : 1)
                * Matrix3x2.CreateTranslation(frame.Bounds.Center);
            Controller.TransformSelectionUndoable(inverse * local * frame.Transform, "resize image", scaleWidth: false);
            UpdateSize();
        }
    }

    private UIElement BuildTransform()
    {
        var panel = new StackPanel();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Button("放大", "\uF8DD", () => Transform(Matrix3x2.CreateScale(1.25f), "enlarge selection")),
            Button("缩小", "\uE71F", () => Transform(Matrix3x2.CreateScale(.8f), "shrink selection")),
        } });
        foreach (int sign in new[] { -1, 1 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (int degrees in new[] { 15, 45, 90 })
            {
                int angle = sign * degrees;
                row.Children.Add(Button($"{angle}°", "\uF13E", () => Transform(Matrix3x2.CreateRotation(angle * MathF.PI / 180), "rotate selection")));
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Button("水平翻转", "\uF18E", () => Transform(Matrix3x2.CreateScale(-1, 1), "flip horizontal")),
            Button("垂直翻转", "\uF18E", () => Transform(Matrix3x2.CreateScale(1, -1), "flip vertical")),
        } });
        return panel;
    }

    private void Transform(Matrix3x2 local, string name)
    {
        var center = Controller.SelectionBounds.Center;
        Controller.TransformSelectionUndoable(Matrix3x2.CreateTranslation(-center) * local * Matrix3x2.CreateTranslation(center), name, scaleWidth: false);
    }

    private TouchIconButton Button(string name, string glyph, Action action)
    {
        var button = new TouchIconButton { Width = 44, Height = 48, Padding = new Thickness(2),
            Glyph = glyph, Label = name, LabelFontSize = name.Length > 3 ? 9 : 10, IconSize = 18, IconAreaHeight = 24,
            CornerRadius = new CornerRadius(5) };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name == "克隆" ? "开启后拖动选中对象可连续生成副本" : name);
        button.Click += (_, _) => action();
        ThemeButton(button);
        return button;
    }
}
