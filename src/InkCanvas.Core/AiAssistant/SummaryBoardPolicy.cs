namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 课堂总结写入策略（纯逻辑）：识别「总结」目标、决定总结页名。
/// 只读取计划与指令，不依赖 UI、画板或设置存储。
/// </summary>
public static class SummaryBoardPolicy
{
    /// <summary>总结块类型标识（<c>board_plan.blocks[].kind</c>）。</summary>
    public const string BlockKind = "summary";

    /// <summary>默认总结页名。</summary>
    public const string DefaultPageName = "课堂总结";

    /// <summary>
    /// 是否为总结写入：计划 intent 为 summary，或任一内容块 kind 为 summary，
    /// 或指令明确要求本课/课堂总结。
    /// </summary>
    public static bool IsSummary(AssistantBoardPlan plan, string instruction)
    {
        if (plan is not null)
        {
            if (string.Equals((plan.Intent ?? "").Trim(), BlockKind, StringComparison.OrdinalIgnoreCase))
                return true;

            if (plan.Blocks is { Count: > 0 }
                && plan.Blocks.Any(block => block is not null
                    && string.Equals((block.Kind ?? "").Trim(), BlockKind, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        string text = (instruction ?? "").Trim();
        return text.Contains("本课总结", StringComparison.Ordinal)
               || text.Contains("课堂总结", StringComparison.Ordinal);
    }

    /// <summary>总结页名：优先用户配置，空或全空白回退默认值。</summary>
    public static string ResolvePageName(string configuredName)
    {
        string name = (configuredName ?? "").Trim();
        return name.Length == 0 ? DefaultPageName : name;
    }
}