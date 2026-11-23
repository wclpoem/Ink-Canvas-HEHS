using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Features.Board.AiObjects;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// 结构化板书计划的本地原生对象结果：只包含可编辑文本/公式对象，绝不含 PNG 卡片。
/// </summary>
public sealed class BoardPlanNativeResult
{
    public IReadOnlyList<BoardTextObject> Texts { get; init; } = Array.Empty<BoardTextObject>();

    public IReadOnlyList<BoardFormulaObject> Formulas { get; init; } = Array.Empty<BoardFormulaObject>();

    public string Error { get; init; } = "";

    public int Count => Texts.Count + Formulas.Count;

    public bool Success => Count > 0 && string.IsNullOrEmpty(Error);
}

/// <summary>
/// 把 <see cref="AssistantBoardPlan"/> 转换为原生展板对象。文本/词语/古诗/段落生成
/// <see cref="BoardTextObject"/>，公式生成 <see cref="BoardFormulaObject"/> 并保留 LaTeX；
/// 本类不调用任何 PNG 渲染器，也不生成 <c>BoardImageAsset</c>。
/// </summary>
public static class BoardPlanWriter
{
    private const string TextFontFamily = "Microsoft YaHei UI";
    private const string FormulaFontFamily = "Cambria Math";
    private const string TextColor = "#FF202024";
    private const double MinFontSize = 12;
    private const double MaxFontSize = 96;
    private const double MinTextWidth = 220;
    private const double MaxTextWidth = 640;
    private const double MinTextHeight = 80;
    private const double MaxTextHeight = 1200;

    public static BoardPlanNativeResult Create(
        AssistantBoardPlan plan, string instruction, long? nowMs = null)
    {
        if (plan is null || plan.IsEmpty)
            return new BoardPlanNativeResult { Error = "板书计划为空" };

        long createdAt = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var texts = new List<BoardTextObject>(plan.Blocks.Count);
        var formulas = new List<BoardFormulaObject>();

        for (int index = 0; index < plan.Blocks.Count; index++)
        {
            AssistantBoardBlock block = plan.Blocks[index];
            string kind = NormalizeKind(block.Kind);

            if (kind == "formula")
            {
                if (string.IsNullOrWhiteSpace(block.Latex))
                    return new BoardPlanNativeResult { Error = $"第 {index + 1} 块（公式）LaTeX 为空" };

                formulas.Add(CreateFormula(block, kind, instruction, createdAt));
                continue;
            }

            BoardTextObject? text = CreateText(block, kind, instruction, createdAt, index);
            if (text is null)
                return new BoardPlanNativeResult { Error = $"第 {index + 1} 块（{KindLabel(kind)}）内容为空" };
            texts.Add(text);
        }

        if (texts.Count + formulas.Count == 0)
            return new BoardPlanNativeResult { Error = "板书计划没有可写入的对象" };

        FileLog.WriteLogToFile(
            $"[AI] 板书原生对象：texts={texts.Count}，formulas={formulas.Count}，images=0，"
            + $"instruction={Shorten(instruction, 60)}",
            LogType.Event);

        return new BoardPlanNativeResult { Texts = texts, Formulas = formulas };
    }

    private static BoardTextObject? CreateText(
        AssistantBoardBlock block,
        string kind,
        string instruction,
        long createdAt,
        int blockIndex)
    {
        string content = BuildContent(block, kind);
        string title = (block.Title ?? "").Trim();
        if (content.Length == 0 && title.Length == 0) return null;

        AssistantBoardStyle style = block.Style ?? new AssistantBoardStyle();
        double fontSize = Clamp(style.FontSize > 0 ? style.FontSize : 24, MinFontSize, MaxFontSize);
        double lineHeight = Clamp(style.LineHeight > 0 ? style.LineHeight : 1.4, 1.0, 3.0);
        int columns = (int)Clamp(style.Columns <= 0 ? 1 : style.Columns, 1, 3);
        string align = NormalizeAlign(style.Align, kind);

        (double width, double height) = EstimateTextGeometry(
            content, title, block.Author, fontSize, lineHeight, columns);

        return new BoardTextObject
        {
            Type = kind,
            Content = content,
            Title = title,
            Author = (block.Author ?? "").Trim(),
            Dynasty = "",
            FontFamily = TextFontFamily,
            FontSize = fontSize,
            Color = TextColor,
            LineHeight = lineHeight,
            Align = align,
            Columns = columns,
            Left = 0,
            Top = 0,
            Width = (float)width,
            Height = (float)height,
            Source = "ai",
            Instruction = instruction ?? "",
            CreatedAt = createdAt,
        };
    }

    private static BoardFormulaObject CreateFormula(
        AssistantBoardBlock block,
        string kind,
        string instruction,
        long createdAt)
    {
        AssistantBoardStyle style = block.Style ?? new AssistantBoardStyle();
        double fontSize = Clamp(style.FontSize > 0 ? style.FontSize : 30, MinFontSize, MaxFontSize);
        string latex = (block.Latex ?? "").Trim();

        double width = Clamp(EstimateLineWidth(latex, fontSize, 0.55) + 48, 180, 720);
        double height = Clamp(fontSize * 2.4, 60, 480);

        return new BoardFormulaObject
        {
            Latex = latex,
            FontFamily = FormulaFontFamily,
            FontSize = fontSize,
            Color = TextColor,
            Left = 0,
            Top = 0,
            Width = (float)width,
            Height = (float)height,
            Source = "ai",
            Instruction = instruction ?? "",
            CreatedAt = createdAt,
        };
    }

    private static string BuildContent(AssistantBoardBlock block, string kind)
    {
        IReadOnlyList<string> lines = block.Lines ?? Array.Empty<string>();
        string text = (block.Text ?? "").TrimEnd();

        if (kind is "wordList" or "poem")
        {
            if (lines.Count > 0)
                return string.Join("\n", lines.Select(line => (line ?? "").TrimEnd()));
            return text.Trim();
        }

        if (text.Length > 0) return text;
        return lines.Count > 0 ? string.Join("\n", lines) : "";
    }

    private static (double Width, double Height) EstimateTextGeometry(
        string content,
        string title,
        string author,
        double fontSize,
        double lineHeight,
        int columns)
    {
        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        double longest = 0;
        foreach (string line in lines) longest = Math.Max(longest, EstimateLineWidth(line, fontSize, 0.6));

        double columnGap = columns > 1 ? fontSize * 0.5 : 0;
        double contentWidth = columns > 1 ? longest / columns : longest;
        double width = Clamp(
            contentWidth + columnGap * (columns - 1) + 32,
            MinTextWidth,
            MaxTextWidth);

        int rows;
        if (columns > 1 && lines.Length > 1)
        {
            rows = (lines.Length + columns - 1) / columns;
        }
        else
        {
            double usableWidth = Math.Max(1, width - 32);
            double charsPerLine = Math.Max(1, usableWidth / Math.Max(1, fontSize * 0.62));
            rows = Math.Max(1, (int)Math.Ceiling(content.Length / charsPerLine));
        }

        double headerHeight = 0;
        if (!string.IsNullOrWhiteSpace(title)) headerHeight += fontSize * 1.3 * 1.25 + fontSize * 0.25;
        if (!string.IsNullOrWhiteSpace(author)) headerHeight += fontSize * 0.78 * 1.25 + fontSize * 0.2;
        double bodyHeight = rows * fontSize * lineHeight;
        double height = Clamp(headerHeight + bodyHeight + 32, MinTextHeight, MaxTextHeight);

        return (width, height);
    }

    private static double EstimateLineWidth(string text, double fontSize, double asciiRatio)
    {
        double width = 0;
        foreach (char character in text ?? "")
        {
            if (char.IsWhiteSpace(character)) width += fontSize * 0.3;
            else if (character < 0x80) width += fontSize * asciiRatio;
            else width += fontSize;
        }

        return width;
    }

    private static string NormalizeKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "formula" => "formula",
        "wordlist" => "wordList",
        "poem" => "poem",
        "summary" => "summary",
        _ => "text",
    };

    private static string NormalizeAlign(string? align, string kind)
        => (align ?? "").Trim().ToLowerInvariant() switch
        {
            "center" => "center",
            "right" => "right",
            "justify" => "justify",
            "left" => "left",
            _ => kind == "poem" ? "center" : "left",
        };

    private static double Clamp(double value, double min, double max)
        => !double.IsFinite(value) ? min : Math.Clamp(value, min, max);

    private static string Shorten(string? text, int max)
    {
        string value = (text ?? "").ReplaceLineEndings(" ");
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static string KindLabel(string kind) => kind switch
    {
        "formula" => "公式",
        "wordList" => "词语",
        "poem" => "古诗",
        "summary" => "总结",
        _ => "文本",
    };
}