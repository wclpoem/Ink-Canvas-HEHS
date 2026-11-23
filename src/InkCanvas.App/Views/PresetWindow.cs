using InkCanvas.App.Services;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>场景预设编辑器：勾选工具、设置倒计时并保存为教师个人预设。</summary>
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

    private readonly TextBox _name = new() { MinWidth = 220, Height = 34 };
    private readonly NumberBox _minutes = new()
    {
        Minimum = 0,
        Maximum = 60,
        Value = 3,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        MinWidth = 100,
    };
    private readonly Dictionary<ClassroomTool, CheckBox> _checks = new();

    public PresetWindow(ClassroomPreset current, int theme, bool aiAvailable = false)
    {
        Title = "场景预设";

        ClassroomPreset source = current?.Clone() ?? ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach);
        _name.Text = source.Name;
        _minutes.Value = source.CountdownMinutes;

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
        form.Children.Add(new TextBlock { Text = "名称", FontSize = 12, Opacity = 0.75 });
        form.Children.Add(_name);
        form.Children.Add(new TextBlock { Text = "默认倒计时（分钟，0 = 不启动）", FontSize = 12, Opacity = 0.75 });
        form.Children.Add(_minutes);
        form.Children.Add(new TextBlock { Text = "打开的工具", FontSize = 12, Opacity = 0.75 });
        form.Children.Add(new ScrollViewer
        {
            Content = toolPanel,
            Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

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
            Child = panel,
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 460, 560);
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
        };

        Saved?.Invoke(ClassroomPresetLibrary.Normalize(preset, preset.Id));
        Close();
    }

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
