using InkCanvas.App.Services;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 场景预设编辑器：勾选工具、设置倒计时，并记录个人教学偏好
/// （笔色 / 背景 / 模板 / AI 指令 / 练习时长）。偏好只随预设保存，不在此执行工具逻辑。
/// </summary>
public sealed class PresetWindow : Window
{
    private static readonly ClassroomTool[] EditableTools =
    {
        ClassroomTool.PptAnnotation,
        ClassroomTool.SlideNavigator,
        ClassroomTool.AiAssistant,
        ClassroomTool.AiStepGuide,
        ClassroomTool.Spotlight,
        ClassroomTool.Magnifier,
        ClassroomTool.Board,
        ClassroomTool.Countdown,
        ClassroomTool.GroupScore,
        ClassroomTool.HandoutExport,
    };

    private static readonly (string Label, string Value)[] PenColorChoices =
    {
        ("跟随当前笔色", null),
        ("深灰", "#1F2937"),
        ("红色", "#DC2626"),
        ("蓝色", "#2563EB"),
        ("绿色", "#059669"),
        ("紫色", "#7C3AED"),
    };

    private static readonly (string Label, string Value)[] BackgroundChoices =
    {
        ("跟随当前背景", null),
        ("黑板", "Black"),
        ("白板", "White"),
        ("米色", "Cream"),
        ("绿板", "Green"),
        ("方格", "Grid"),
        ("横线", "Lined"),
        ("坐标", "Coordinate"),
    };

    private static readonly (string Label, string Value)[] TemplateChoices =
    {
        ("不指定模板", null),
        ("空白页", "blank"),
        ("横线页", "lined"),
        ("方格页", "grid"),
        ("田字格", "tianzige"),
        ("坐标纸", "coordinate"),
    };

    private readonly TextBox _name = new() { MinWidth = 220, Height = 34 };
    private readonly NumberBox _minutes = new()
    {
        Minimum = 0,
        Maximum = 60,
        Value = 3,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = 100,
    };
    private readonly ComboBox _penColor = CreateChoiceBox(PenColorChoices);
    private readonly ComboBox _background = CreateChoiceBox(BackgroundChoices);
    private readonly ComboBox _template = CreateChoiceBox(TemplateChoices);
    private readonly NumberBox _practiceMinutes = new()
    {
        Minimum = 0,
        Maximum = 180,
        Value = 0,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = 100,
    };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 88,
        PlaceholderText = "例如：先让学生口述思路，再逐步板书关键步骤",
    };
    private readonly Dictionary<ClassroomTool, CheckBox> _checks = new();

    public PresetWindow(ClassroomPreset current, int theme, bool aiAvailable = false)
    {
        Title = "场景预设";

        ClassroomPreset source = current?.Clone() ?? ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach);
        _name.Text = source.Name;
        _minutes.Value = source.CountdownMinutes;
        SelectValue(_penColor, source.PenColor);
        SelectValue(_background, source.Background);
        SelectValue(_template, source.TemplateId);
        _practiceMinutes.Value = source.PracticeMinutes ?? 0;
        _prompt.Text = source.Prompt ?? "";

        var toolPanel = new StackPanel { Spacing = 2 };
        foreach (ClassroomTool tool in EditableTools)
        {
            if (!aiAvailable && tool is ClassroomTool.AiAssistant or ClassroomTool.AiStepGuide) continue;
            var check = new CheckBox
            {
                Content = ToolLabel(tool),
                IsChecked = source.Tools.Contains(tool) || (tool == ClassroomTool.PptAnnotation),
                MinHeight = 30,
            };
            _checks[tool] = check;
            toolPanel.Children.Add(check);
        }

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(Label("名称"));
        form.Children.Add(_name);
        form.Children.Add(Label("默认倒计时（分钟，0 = 不启动）"));
        form.Children.Add(_minutes);

        var preferences = new StackPanel { Spacing = 8 };
        preferences.Children.Add(Label("板书笔色"));
        preferences.Children.Add(_penColor);
        preferences.Children.Add(Label("页面背景"));
        preferences.Children.Add(_background);
        preferences.Children.Add(Label("页面模板"));
        preferences.Children.Add(_template);
        preferences.Children.Add(Label("练习时长（分钟，0 = 不设置）"));
        preferences.Children.Add(_practiceMinutes);
        if (aiAvailable)
        {
            preferences.Children.Add(Label("AI 指令（随预设附带，开课时由 AI 助手读取）"));
            preferences.Children.Add(_prompt);
        }

        form.Children.Add(Label("打开的工具"));
        form.Children.Add(new ScrollViewer
        {
            Content = toolPanel,
            Height = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        form.Children.Add(new TextBlock
        {
            Text = "个人教学偏好（只保存到预设，不改变工具行为）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.75,
        });
        form.Children.Add(preferences);

        var save = new Button { Content = "保存并应用", MinHeight = 36, MinWidth = 108 };
        save.Click += (_, _) => Save(source.Scene);

        var cancel = new Button { Content = "取消", MinHeight = 36, MinWidth = 72 };
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(save);
        actions.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "自定义课堂场景",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(form);
        panel.Children.Add(actions);

        var root = new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceBrush"],
            Child = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 480, 760);
        WindowChrome.ApplyTheme(this, theme);
    }

    /// <summary>保存后的个人预设；IsBuiltIn=false，由外层写入 classroom.customPresets。</summary>
    public event Action<ClassroomPreset> Saved;

    private void Save(ClassroomSceneKind scene)
    {
        var tools = new List<ClassroomTool>();
        foreach (KeyValuePair<ClassroomTool, CheckBox> pair in _checks)
        {
            if (pair.Value.IsChecked == true) tools.Add(pair.Key);
        }

        var preset = new ClassroomPreset
        {
            Id = "custom-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = string.IsNullOrWhiteSpace(_name.Text) ? "我的场景" : _name.Text.Trim(),
            Scene = scene,
            IsBuiltIn = false,
            Tools = tools,
            CountdownMinutes = (int)Math.Clamp(_minutes.Value, 0, 60),
            CollapseToolkit = tools.Contains(ClassroomTool.Board) && tools.Contains(ClassroomTool.Countdown),
            PenColor = SelectedValue(_penColor),
            Background = SelectedValue(_background),
            TemplateId = SelectedValue(_template),
            Prompt = string.IsNullOrWhiteSpace(_prompt.Text) ? null : _prompt.Text.Trim(),
            PracticeMinutes = _practiceMinutes.Value > 0 ? (int)Math.Clamp(_practiceMinutes.Value, 0, 180) : null,
        };

        Saved?.Invoke(ClassroomPresetLibrary.Normalize(preset, preset.Id));
        Close();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Opacity = 0.75 };

    private static ComboBox CreateChoiceBox(IReadOnlyList<(string Label, string Value)> choices)
    {
        var box = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, value) in choices)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedIndex = 0;
        return box;
    }

    private static void SelectValue(ComboBox box, string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        for (int index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item
                && string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = index;
                return;
            }
        }

        // 旧预设里的未知取值：追加一项保留原值，避免保存时被静默丢弃。
        if (normalized is not null)
        {
            box.Items.Add(new ComboBoxItem { Content = normalized, Tag = normalized });
            box.SelectedIndex = box.Items.Count - 1;
            return;
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedValue(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static string ToolLabel(ClassroomTool tool) => tool switch
    {
        ClassroomTool.PptAnnotation => "课件批注",
        ClassroomTool.SlideNavigator => "批注导航",
        ClassroomTool.AiAssistant => "AI 助手",
        ClassroomTool.AiStepGuide => "AI 分步引导",
        ClassroomTool.Spotlight => "聚光灯",
        ClassroomTool.Magnifier => "放大镜",
        ClassroomTool.Board => "画板",
        ClassroomTool.Countdown => "倒计时",
        ClassroomTool.GroupScore => "分组积分",
        ClassroomTool.HandoutExport => "讲义导出",
        _ => tool.ToString(),
    };
}