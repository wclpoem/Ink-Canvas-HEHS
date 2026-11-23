using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// 把本地例题记录转成可写入画板的 <see cref="AssistantBoardPlan"/>：
/// 有 AI 步骤时按步骤生成文本/公式块，否则用答案/教师订正生成单个文本块。
/// 只读 <see cref="ExampleEntry"/>，绝不修改原例题或例题库文件。
/// </summary>
public static class ExampleBoardPlanFactory
{
    /// <summary>无可用内容时返回空计划（Blocks 为空，<see cref="AssistantBoardPlan.IsEmpty"/> 为 true）。</summary>
    public static AssistantBoardPlan Create(ExampleEntry entry)
    {
        var blocks = new List<AssistantBoardBlock>();
        if (entry is null) return new AssistantBoardPlan { Blocks = blocks };

        IReadOnlyList<AssistantStep> steps = ExampleEntryCodec.DecodeSteps(entry.StepsJson);
        if (steps.Count > 0)
        {
            int order = 1;
            foreach (AssistantStep step in steps)
            {
                if (step is null) continue;
                string text = (step.Text ?? "").Trim();
                string latex = (step.Latex ?? "").Trim();
                if (text.Length == 0 && latex.Length == 0) continue;

                blocks.Add(new AssistantBoardBlock
                {
                    Id = "step" + order,
                    Kind = latex.Length > 0 && text.Length == 0 ? "formula" : "text",
                    Text = text.Length > 0 ? text : latex,
                    Latex = latex,
                    Title = "第 " + order + " 步",
                });
                order++;
            }
        }

        if (blocks.Count == 0)
        {
            string content = FirstNonEmpty(entry.CorrectedText, entry.Answer, entry.Title);
            if (content.Length > 0)
            {
                blocks.Add(new AssistantBoardBlock
                {
                    Id = "b1",
                    Kind = "text",
                    Text = content,
                });
            }
        }

        return new AssistantBoardPlan
        {
            Intent = "example",
            TargetBoard = "main",
            Blocks = blocks,
        };
    }

    /// <summary>旧卡片兼容路径：把例题内容整理为逐行文本，用于 <c>PasteAssistantAsync</c> 的 PNG 卡片。</summary>
    public static List<string> LegacyLines(ExampleEntry entry)
    {
        var lines = new List<string>();
        if (entry is null) return lines;

        IReadOnlyList<AssistantStep> steps = ExampleEntryCodec.DecodeSteps(entry.StepsJson);
        foreach (AssistantStep step in steps)
        {
            if (step is null) continue;
            string line = FirstNonEmpty(step.Text, step.Latex);
            if (line.Length > 0) lines.Add(line);
        }

        if (lines.Count > 0) return lines;

        foreach (string value in new[] { entry.TeacherEdited ? entry.CorrectedText : "", entry.Answer, entry.Title })
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) continue;
            lines.AddRange(text.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0));
        }

        return lines;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }
}