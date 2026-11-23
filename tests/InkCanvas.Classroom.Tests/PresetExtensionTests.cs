using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 个人教学预设的扩展字段：只追加可空字段，旧配置保持 null；Plan 仍只输出工具动作，
/// 不复制笔色 / 背景 / 模板 / AI 指令 / 练习时长的工具逻辑。
/// </summary>
public sealed class PresetExtensionTests
{
    [Fact]
    public void ExtensionFields_RoundTripThroughJson()
    {
        var preset = new ClassroomPreset
        {
            Id = "custom-1",
            Name = "个人讲题",
            Scene = ClassroomSceneKind.Teach,
            Tools = new List<ClassroomTool> { ClassroomTool.Board, ClassroomTool.AiAssistant },
            CountdownMinutes = 5,
            PenColor = "#2563EB",
            Background = "Cream",
            TemplateId = "grid",
            Prompt = "先让学生说思路",
            PracticeMinutes = 12,
        };

        IReadOnlyList<ClassroomPreset> restored =
            ClassroomPresetLibrary.Deserialize(ClassroomPresetLibrary.Serialize(new[] { preset }));

        ClassroomPreset item = Assert.Single(restored);
        Assert.Equal("#2563EB", item.PenColor);
        Assert.Equal("Cream", item.Background);
        Assert.Equal("grid", item.TemplateId);
        Assert.Equal("先让学生说思路", item.Prompt);
        Assert.Equal(12, item.PracticeMinutes);
        Assert.Equal(preset.Tools, item.Tools);
    }

    [Fact]
    public void LegacyPresetJson_WithoutExtensionFields_ReadsAsNull()
    {
        const string legacy = """
        [
          {
            "Version": 1,
            "Id": "legacy",
            "Name": "旧预设",
            "Scene": 0,
            "IsBuiltIn": false,
            "Tools": [0, 6],
            "Hidden": [],
            "CountdownMinutes": 3,
            "CollapseToolkit": false
          }
        ]
        """;

        ClassroomPreset preset = Assert.Single(ClassroomPresetLibrary.Deserialize(legacy));

        Assert.Equal("legacy", preset.Id);
        Assert.Null(preset.PenColor);
        Assert.Null(preset.Background);
        Assert.Null(preset.TemplateId);
        Assert.Null(preset.Prompt);
        Assert.Null(preset.PracticeMinutes);
    }

    [Fact]
    public void Plan_IgnoresPersonalPreferenceFields()
    {
        ClassroomPreset plain = ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach);
        ClassroomPreset decorated = plain.Clone();
        decorated.PenColor = "#DC2626";
        decorated.Background = "Black";
        decorated.TemplateId = "coordinate";
        decorated.Prompt = "只提示第一步";
        decorated.PracticeMinutes = 20;

        PresetAction plainAction = ClassroomPresetLibrary.Plan(plain);
        PresetAction decoratedAction = ClassroomPresetLibrary.Plan(decorated);

        Assert.Equal(plainAction.Open, decoratedAction.Open);
        Assert.Equal(plainAction.Close, decoratedAction.Close);
        Assert.Equal(plainAction.CountdownMinutes, decoratedAction.CountdownMinutes);
        Assert.Equal(plainAction.CollapseToolkit, decoratedAction.CollapseToolkit);
        // Plan 只暴露动作字段，追加偏好不改变动作类型。
        Assert.Equal(
            new[] { nameof(PresetAction.Open), nameof(PresetAction.Close), nameof(PresetAction.CountdownMinutes), nameof(PresetAction.CollapseToolkit) }
                .OrderBy(name => name, StringComparer.Ordinal),
            typeof(PresetAction).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Normalize_TrimsBlankPreferenceFieldsToNull()
    {
        var preset = new ClassroomPreset
        {
            Id = "mine",
            Name = "  ",
            PenColor = "  ",
            Background = "",
            TemplateId = "   ",
            Prompt = "  指令  ",
            PracticeMinutes = -1,
        };

        ClassroomPreset normalized = ClassroomPresetLibrary.Normalize(preset, "mine");

        Assert.Equal("自定义场景", normalized.Name);
        Assert.Null(normalized.PenColor);
        Assert.Null(normalized.Background);
        Assert.Null(normalized.TemplateId);
        Assert.Equal("指令", normalized.Prompt);
        Assert.Null(normalized.PracticeMinutes);
    }

    [Fact]
    public void Clone_CopiesPreferenceFields()
    {
        var preset = new ClassroomPreset
        {
            PenColor = "#059669",
            Background = "Green",
            TemplateId = "lined",
            Prompt = "复习",
            PracticeMinutes = 8,
        };

        ClassroomPreset clone = preset.Clone();

        Assert.Equal(preset.PenColor, clone.PenColor);
        Assert.Equal(preset.Background, clone.Background);
        Assert.Equal(preset.TemplateId, clone.TemplateId);
        Assert.Equal(preset.Prompt, clone.Prompt);
        Assert.Equal(preset.PracticeMinutes, clone.PracticeMinutes);
    }

    [Fact]
    public void Serialize_OmitsNullPreferenceFields()
    {
        string json = ClassroomPresetLibrary.Serialize(new[] { ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Practice) });

        Assert.DoesNotContain("PenColor", json);
        Assert.DoesNotContain("TemplateId", json);
        Assert.DoesNotContain("PracticeMinutes", json);
    }
}