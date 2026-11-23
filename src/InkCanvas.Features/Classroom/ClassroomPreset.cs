using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Features.Classroom;

public enum ClassroomTool
{
    PptAnnotation,
    SlideNavigator,
    AiAssistant,
    AiStepGuide,
    Spotlight,
    Magnifier,
    Board,
    Countdown,
    GroupScore,
    HandoutExport,
    Toolkit,
}

public enum ClassroomSceneKind
{
    Teach,
    Practice,
    Group,
    Summary,
}

/// <summary>场景预设：只描述「开哪些工具、什么布局、计时」，不复制工具自身业务逻辑。</summary>
public sealed class ClassroomPreset
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ClassroomSceneKind Scene { get; set; }

    public bool IsBuiltIn { get; set; }

    public List<ClassroomTool> Tools { get; set; } = new();

    public List<ClassroomTool> Hidden { get; set; } = new();

    public int CountdownMinutes { get; set; }

    public bool CollapseToolkit { get; set; }

    /// <summary>
    /// 个人教学预设的扩展字段，全部可空：旧配置缺少这些键时保持 null，行为与 v1 完全一致。
    /// 这些字段只描述「开课时用什么笔色 / 背景 / 模板 / AI 指令 / 练习时长」，
    /// 不复制任何工具自身的执行逻辑；<see cref="ClassroomPresetLibrary.Plan"/> 也不会读取它们。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string PenColor { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Background { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TemplateId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Prompt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PracticeMinutes { get; set; }

    public ClassroomPreset Clone() => new()
    {
        Version = Version,
        Id = Id,
        Name = Name,
        Scene = Scene,
        IsBuiltIn = IsBuiltIn,
        Tools = new List<ClassroomTool>(Tools),
        Hidden = new List<ClassroomTool>(Hidden),
        CountdownMinutes = CountdownMinutes,
        CollapseToolkit = CollapseToolkit,
        PenColor = PenColor,
        Background = Background,
        TemplateId = TemplateId,
        Prompt = Prompt,
        PracticeMinutes = PracticeMinutes,
    };

    public bool Shows(ClassroomTool tool) => Tools.Contains(tool);

    public bool Hides(ClassroomTool tool) => Hidden.Contains(tool);
}

public sealed class PresetAction
{
    public List<ClassroomTool> Open { get; set; } = new();

    public List<ClassroomTool> Close { get; set; } = new();

    public int CountdownMinutes { get; set; }

    public bool CollapseToolkit { get; set; }
}

public static class ClassroomPresetLibrary
{
    public static IReadOnlyList<ClassroomPreset> BuiltIns { get; } = new[]
    {
        new ClassroomPreset
        {
            Id = "teach",
            Name = "讲题模式",
            Scene = ClassroomSceneKind.Teach,
            IsBuiltIn = true,
            Tools = new List<ClassroomTool>
            {
                ClassroomTool.PptAnnotation,
                ClassroomTool.SlideNavigator,
                ClassroomTool.AiAssistant,
                ClassroomTool.AiStepGuide,
                ClassroomTool.Spotlight,
            },
            Hidden = new List<ClassroomTool> { ClassroomTool.Countdown, ClassroomTool.GroupScore },
        },
        new ClassroomPreset
        {
            Id = "practice",
            Name = "随堂练习",
            Scene = ClassroomSceneKind.Practice,
            IsBuiltIn = true,
            Tools = new List<ClassroomTool>
            {
                ClassroomTool.Board,
                ClassroomTool.Countdown,
            },
            Hidden = new List<ClassroomTool>
            {
                ClassroomTool.Spotlight,
                ClassroomTool.Magnifier,
                ClassroomTool.GroupScore,
            },
            CountdownMinutes = 3,
            CollapseToolkit = true,
        },
        new ClassroomPreset
        {
            Id = "group",
            Name = "小组活动",
            Scene = ClassroomSceneKind.Group,
            IsBuiltIn = true,
            Tools = new List<ClassroomTool>
            {
                ClassroomTool.GroupScore,
                ClassroomTool.Countdown,
                ClassroomTool.Board,
            },
            Hidden = new List<ClassroomTool> { ClassroomTool.Spotlight, ClassroomTool.Magnifier },
            CountdownMinutes = 5,
        },
        new ClassroomPreset
        {
            Id = "summary",
            Name = "课堂总结",
            Scene = ClassroomSceneKind.Summary,
            IsBuiltIn = true,
            Tools = new List<ClassroomTool>
            {
                ClassroomTool.SlideNavigator,
                ClassroomTool.Board,
                ClassroomTool.HandoutExport,
            },
            Hidden = new List<ClassroomTool> { ClassroomTool.Countdown, ClassroomTool.Spotlight },
        },
    };

    public static ClassroomPreset ForScene(ClassroomSceneKind scene)
    {
        foreach (ClassroomPreset preset in BuiltIns)
        {
            if (preset.Scene == scene) return preset.Clone();
        }

        return BuiltIns[0].Clone();
    }

    public static ClassroomPreset ForId(string id)
    {
        foreach (ClassroomPreset preset in BuiltIns)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
                return preset.Clone();
        }

        foreach (ClassroomPreset preset in BuiltIns)
        {
            if (string.Equals(preset.Name, id, StringComparison.Ordinal)) return preset.Clone();
        }

        return null;
    }

    /// <summary>教师调整后的预设：去重、保持顺序、补齐必填字段。</summary>
    public static ClassroomPreset Normalize(ClassroomPreset preset, string idFallback = null)
    {
        if (preset is null) return null;

        var uniqueTools = new List<ClassroomTool>();
        foreach (ClassroomTool tool in preset.Tools ?? new List<ClassroomTool>())
        {
            if (!uniqueTools.Contains(tool)) uniqueTools.Add(tool);
        }

        var hidden = new List<ClassroomTool>();
        foreach (ClassroomTool tool in preset.Hidden ?? new List<ClassroomTool>())
        {
            if (!hidden.Contains(tool) && !uniqueTools.Contains(tool)) hidden.Add(tool);
        }

        preset.Version = Math.Max(1, preset.Version);
        preset.Tools = uniqueTools;
        preset.Hidden = hidden;
        preset.Id = string.IsNullOrWhiteSpace(preset.Id) ? (idFallback ?? "custom") : preset.Id;
        preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "自定义场景" : preset.Name.Trim();
        if (preset.CountdownMinutes < 0) preset.CountdownMinutes = 0;
        preset.PenColor = NormalizeOptional(preset.PenColor);
        preset.Background = NormalizeOptional(preset.Background);
        preset.TemplateId = NormalizeOptional(preset.TemplateId);
        preset.Prompt = NormalizeOptional(preset.Prompt);
        if (preset.PracticeMinutes is < 0) preset.PracticeMinutes = null;
        return preset;
    }

    private static string NormalizeOptional(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 把预设翻译成「打开/收起哪些工具」的动作清单；不直接操作窗口。
    /// 笔色 / 背景 / 模板 / AI 指令 / 练习时长属于个人教学偏好，故意不进入动作清单，
    /// 避免预设承担工具自身的行为逻辑。
    /// </summary>
    public static PresetAction Plan(ClassroomPreset preset)
    {
        var action = new PresetAction();
        if (preset is null) return action;

        ClassroomPreset normalized = Normalize(preset.Clone(), preset.Id);
        foreach (ClassroomTool tool in normalized.Tools)
        {
            if (tool is ClassroomTool.PptAnnotation or ClassroomTool.Toolkit) continue;
            if (!action.Open.Contains(tool)) action.Open.Add(tool);
        }

        foreach (ClassroomTool tool in normalized.Hidden)
        {
            if (!action.Close.Contains(tool)) action.Close.Add(tool);
        }

        action.CountdownMinutes = normalized.CountdownMinutes;
        action.CollapseToolkit = normalized.CollapseToolkit;
        return action;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IReadOnlyList<ClassroomPreset> presets)
        => JsonSerializer.Serialize(presets ?? Array.Empty<ClassroomPreset>(), JsonOptions);

    public static IReadOnlyList<ClassroomPreset> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ClassroomPreset>();
        try
        {
            var presets = JsonSerializer.Deserialize<List<ClassroomPreset>>(json);
            if (presets is null) return Array.Empty<ClassroomPreset>();

            var result = new List<ClassroomPreset>();
            foreach (ClassroomPreset preset in presets)
            {
                ClassroomPreset normalized = Normalize(preset);
                if (normalized is not null) result.Add(normalized);
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<ClassroomPreset>();
        }
    }
}