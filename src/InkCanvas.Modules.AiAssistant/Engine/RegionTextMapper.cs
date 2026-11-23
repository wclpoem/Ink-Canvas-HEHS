namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 把屏幕 DIP 圈选矩形映射为文本字符区间：仅按圈选位置定点替换，
/// 未圈选部分保持逐字不变。采用等宽字符近似，横向比例钳制在 0..1。
/// </summary>
public static class RegionTextMapper
{
    public static (int Start, int Length) Map(AssistantRect anchor, AssistantRect region, string text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        if (anchor.Width <= 0 || double.IsNaN(anchor.Width) || double.IsInfinity(anchor.Width))
            return (0, 0);

        double startRatio = Clamp01((region.Left - anchor.Left) / anchor.Width);
        double endRatio = Clamp01((region.Right - anchor.Left) / anchor.Width);
        if (endRatio < startRatio) (startRatio, endRatio) = (endRatio, startRatio);

        // 圈选完全落在锚点左侧：映射到文本开头，区间为空。
        if (endRatio <= 0) return (0, 0);

        int start = (int)Math.Floor(startRatio * text.Length);
        int end = (int)Math.Ceiling(endRatio * text.Length);

        start = Math.Clamp(start, 0, text.Length - 1);
        if (end <= start) end = start + 1;
        end = Math.Clamp(end, start + 1, text.Length);

        return (start, end - start);
    }

    public static string Apply(string text, int start, int length, string replacement)
        => AssistantReplace.ApplyToText(text, start, length, replacement);

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value)) return 0;
        return Math.Clamp(value, 0, 1);
    }
}