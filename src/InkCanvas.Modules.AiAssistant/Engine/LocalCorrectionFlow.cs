namespace InkCanvas.Core.AiAssistant;

/// <summary>局部纠错结果：克隆后的会话、目标字符区间与目标字段（Formula/Text）。</summary>
public sealed record LocalCorrectionResult(AssistantSession Session, int Start, int Length, string Field);

/// <summary>
/// 局部纠错的完整纯逻辑流程：圈选 → 定点替换 → 其余内容逐字不变。
/// 只修改目标字段的目标区间；空替换或空映射时原样返回克隆会话。
/// </summary>
public static class LocalCorrectionFlow
{
    public static LocalCorrectionResult Apply(
        AssistantSession session, AssistantRect anchor, AssistantRect region, string replacement)
    {
        if (session is null)
        {
            return new LocalCorrectionResult(session, 0, 0, nameof(AssistantSession.Text));
        }

        bool formulaTarget = !string.IsNullOrWhiteSpace(session.Formula);
        string field = formulaTarget ? nameof(AssistantSession.Formula) : nameof(AssistantSession.Text);
        string target = formulaTarget ? session.Formula : session.Text;

        var (start, length) = RegionTextMapper.Map(anchor, region, target);
        AssistantSession copy = AssistantReplace.Clone(session);

        if (string.IsNullOrEmpty(replacement) || (start == 0 && length == 0))
        {
            return new LocalCorrectionResult(copy, start, length, field);
        }

        string updated = RegionTextMapper.Apply(target, start, length, replacement);
        if (formulaTarget) copy.Formula = updated;
        else copy.Text = updated;

        return new LocalCorrectionResult(copy, start, length, field);
    }
}