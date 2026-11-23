namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 教学模式展示策略。步骤裁剪在渲染前完成，未展示的步骤不进入界面，
/// 避免仅靠隐藏导致答案提前泄露。
/// </summary>
public static class AssistantTeachingPolicy
{
    public static IReadOnlyList<AssistantTeachingMode> Order { get; } = new[]
    {
        AssistantTeachingMode.Hint,
        AssistantTeachingMode.StepByStep,
        AssistantTeachingMode.Full,
        AssistantTeachingMode.Teacher,
    };

    public static AssistantTeachingMode Next(AssistantTeachingMode mode)
    {
        for (int i = 0; i < Order.Count; i++)
        {
            if (Order[i] == mode) return Order[(i + 1) % Order.Count];
        }

        return Order[0];
    }

    public static string Label(AssistantTeachingMode mode) => mode switch
    {
        AssistantTeachingMode.Hint => "只给提示",
        AssistantTeachingMode.StepByStep => "分步引导",
        AssistantTeachingMode.Full => "完整解答",
        AssistantTeachingMode.Teacher => "教师解析",
        _ => "分步引导",
    };

    /// <summary>请求体 <c>teaching</c> 字段的取值，供服务端裁剪回答。</summary>
    public static string Wire(AssistantTeachingMode mode) => mode switch
    {
        AssistantTeachingMode.Hint => "hint",
        AssistantTeachingMode.Full => "full",
        AssistantTeachingMode.Teacher => "teacher",
        _ => "step",
    };

    public static AssistantTeachingMode Parse(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "hint" => AssistantTeachingMode.Hint,
        "full" => AssistantTeachingMode.Full,
        "teacher" => AssistantTeachingMode.Teacher,
        "step" or "stepbystep" => AssistantTeachingMode.StepByStep,
        _ => AssistantTeachingMode.StepByStep,
    };

    /// <summary>最终答案/公式在该模式下是否可见；分步模式需全部展开后才揭晓。</summary>
    public static bool ShowsFinalAnswer(AssistantTeachingMode mode, int revealed, int total)
    {
        return mode switch
        {
            AssistantTeachingMode.Hint => false,
            AssistantTeachingMode.StepByStep => total <= 0 || revealed >= total,
            _ => true,
        };
    }

    /// <summary>当前可见的步骤数量。</summary>
    public static int VisibleSteps(AssistantTeachingMode mode, int revealed, int total)
    {
        if (total <= 0) return 0;

        return mode switch
        {
            AssistantTeachingMode.Hint => 0,
            AssistantTeachingMode.StepByStep => Math.Clamp(revealed, 0, total),
            _ => total,
        };
    }

    /// <summary>是否还有可继续揭示的分步。</summary>
    public static bool HasMoreSteps(AssistantTeachingMode mode, int revealed, int total)
        => mode == AssistantTeachingMode.StepByStep && total > 0 && revealed < total;
}