using System.Text;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 「再讲一次」的纯文本构造：把例题记录的题目、答案、教师订正与来源页拼成一次追问，
/// 只读 <see cref="ExampleEntry"/>，不修改原例题、不访问画板或网络。
/// </summary>
public static class ExampleReExplainPrompt
{
    /// <summary>构造再讲追问文本；空例题返回空串。</summary>
    public static string Build(ExampleEntry entry)
    {
        if (entry is null) return "";

        var builder = new StringBuilder();
        builder.AppendLine("请基于下面这道已收藏的例题，从头再讲一次，并给出可板书的分步讲解。");

        AppendField(builder, "例题", entry.Title);
        AppendField(builder, "答案", entry.Answer);
        AppendField(builder, "教师订正", entry.CorrectedText);

        if (!string.IsNullOrWhiteSpace(entry.SourcePresentation))
        {
            builder.Append("来源课件：").Append(entry.SourcePresentation.Trim());
            if (entry.SourceSlideId > 0) builder.Append(" 第 ").Append(entry.SourceSlideId).Append(" 页");
            builder.AppendLine();
        }

        if (entry.BoardPage >= 0)
            builder.AppendLine("原板书页序号：" + entry.BoardPage);

        return builder.ToString().TrimEnd();
    }

    private static void AppendField(StringBuilder builder, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        builder.Append(label).Append("：").AppendLine(value.Trim());
    }
}