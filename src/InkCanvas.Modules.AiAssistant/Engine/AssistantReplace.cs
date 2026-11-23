namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 局部纠错入口：只替换会话正文中的目标区间，保留步骤、公式、批注、区域与统计字段，
/// 不清空会话，也不就地修改传入的会话对象。
/// </summary>
public static class AssistantReplace
{
    public static AssistantSession ReplaceText(AssistantSession session, int start, int length, string replacement)
    {
        if (session == null) return null;

        AssistantSession copy = Clone(session);
        copy.Text = ApplyToText(session.Text, start, length, replacement);
        return copy;
    }

    /// <summary>纯字符串区间替换；start/length 越界时钳制，未选中的字符逐字保留。</summary>
    public static string ApplyToText(string text, int start, int length, string replacement)
    {
        text ??= "";
        long begin = Math.Clamp((long)start, 0L, text.Length);
        long requestedEnd = (long)start + Math.Max(0, length);
        long end = Math.Clamp(requestedEnd, begin, text.Length);

        return text.Substring(0, (int)begin)
            + (replacement ?? "")
            + text.Substring((int)end);
    }

    public static AssistantSession ApplyRegionResult(AssistantSession session, AssistantRegion region, string replacement)
    {
        if (session == null) return null;
        if (region == null) return Clone(session);

        return ReplaceText(session, region.TextStart, region.TextLength, replacement);
    }

    /// <summary>克隆会话：列表字段做浅拷贝，不修改传入对象。</summary>
    public static AssistantSession Clone(AssistantSession session)
    {
        return new AssistantSession
        {
            State = session.State,
            Mode = session.Mode,
            SessionId = session.SessionId ?? "",
            Text = session.Text ?? "",
            Formula = session.Formula ?? "",
            Steps = new List<AssistantStep>(session.Steps ?? new List<AssistantStep>()),
            Annotations = new List<IReadOnlyList<IReadOnlyList<double>>>(
                session.Annotations ?? new List<IReadOnlyList<IReadOnlyList<double>>>()),
            Regions = new List<AssistantRegion>(session.Regions ?? new List<AssistantRegion>()),
            Confidence = session.Confidence,
            Verified = session.Verified,
            LocalHint = session.LocalHint ?? "",
            Error = session.Error ?? "",
            TokenCount = session.TokenCount,
            StartedAtMs = session.StartedAtMs,
            FirstTokenAtMs = session.FirstTokenAtMs,
        };
    }
}