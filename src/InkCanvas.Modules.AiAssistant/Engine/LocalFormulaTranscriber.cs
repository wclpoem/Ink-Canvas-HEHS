namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 弱网首屏本地提示：本地已完成的公式直接给出纯文本，否则只报告笔数，不伪造识别结果。
/// 仅用于首屏占位，不参与最终结果。
/// </summary>
public static class LocalFormulaTranscriber
{
    public static string Hint(IReadOnlyList<AssistantStroke> strokes, AssistantSession session)
    {
        if (session != null && !string.IsNullOrWhiteSpace(session.Formula))
        {
            return AssistantText.LatexToPlain(session.Formula);
        }

        int count = strokes?.Count ?? 0;
        return "本地已识别到 " + count + " 笔，正在云端转写…";
    }
}