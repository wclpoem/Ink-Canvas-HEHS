using System.Globalization;
using System.Text.Json;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// AI 板书计划：服务端以 <c>board_plan</c> 事件下发的结构化写入描述。
/// 只描述「写什么、写在哪」，不包含任何 UI 或写入实现。
/// </summary>
public sealed class AssistantBoardPlan
{
    /// <summary>单次计划允许的最大块数，超出部分直接截断。</summary>
    public const int MaxBlockCount = 64;

    /// <summary>单个文本字段允许的最大长度，超出部分直接截断。</summary>
    public const int MaxTextLength = 2000;

    /// <summary>词语/诗句/原文行允许的最大行数，超出部分直接截断。</summary>
    public const int MaxLineCount = 200;

    public int Schema { get; set; } = 1;

    public string Intent { get; set; } = "";

    public string TargetBoard { get; set; } = "main";

    public int TargetPageIndex { get; set; }

    public IReadOnlyList<AssistantBoardBlock> Blocks { get; set; } = Array.Empty<AssistantBoardBlock>();

    /// <summary>计划级提示（例如“标题为自动生成”）。</summary>
    public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 识别原文行（<c>originals</c>）。本项目以“行列表”为主：服务端返回对象数组时，
    /// 每项取 <c>text</c>（缺失则 <c>latex</c>）转成一行写入这里，同时保留
    /// <see cref="OriginalBlocks"/> 结构；两种形态不会重复展示。
    /// </summary>
    public IReadOnlyList<string> Originals { get; set; } = Array.Empty<string>();

    /// <summary>结构化识别原文块（含 rect 与 latex）；<c>originals</c> 为字符串数组时为空。</summary>
    public IReadOnlyList<AssistantOriginalBlock> OriginalBlocks { get; set; } = Array.Empty<AssistantOriginalBlock>();

    /// <summary>整份计划的识别置信度，0..1；缺省 1.0。</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>低置信度、疑似笔误或前后矛盾等需要教师复核的提示。</summary>
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

    /// <summary>低于该阈值的内容块在预览中标记“需复核”。</summary>
    public const double ReviewThreshold = 0.8;

    public bool IsEmpty => Blocks is null || Blocks.Count == 0;

    public bool NeedsReview => Confidence < ReviewThreshold;

    /// <summary>
    /// 按选中的块 id 生成子计划（部分采用）；<paramref name="selectedIds"/> 为空时返回空 Blocks，
    /// 由调用方提示“未选择任何块”。原文/警告/置信度等计划级字段原样保留。
    /// </summary>
    public AssistantBoardPlan SelectBlocks(IReadOnlyCollection<string> selectedIds)
    {
        var selected = new HashSet<string>(
            selectedIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        IReadOnlyList<AssistantBoardBlock> blocks = Blocks is null
            ? Array.Empty<AssistantBoardBlock>()
            : Blocks.Where(block => block is not null && selected.Contains(block.Id)).ToArray();

        return new AssistantBoardPlan
        {
            Schema = Schema,
            Intent = Intent,
            TargetBoard = TargetBoard,
            TargetPageIndex = TargetPageIndex,
            Blocks = blocks,
            Notes = Notes ?? Array.Empty<string>(),
            Originals = Originals ?? Array.Empty<string>(),
            OriginalBlocks = OriginalBlocks ?? Array.Empty<AssistantOriginalBlock>(),
            Confidence = Confidence,
            Warnings = Warnings ?? Array.Empty<string>(),
        };
    }

    /// <summary>
    /// 解析 <c>board_plan</c> 事件 JSON。非法 JSON、根节点非对象、blocks 缺失或为空时返回
    /// <c>false</c> 并写入 <paramref name="error"/>，任何情况下都不抛异常。
    /// </summary>
    public static bool TryParse(string json, out AssistantBoardPlan plan, out string error)
    {
        plan = null;
        error = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "board_plan 内容为空";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "board_plan JSON 非法：" + (ex.Message ?? "");
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "board_plan 根节点必须是 JSON 对象";
                return false;
            }

            List<AssistantBoardBlock> blocks = ReadBlocks(root);
            if (blocks.Count == 0)
            {
                error = "board_plan 缺少有效的 blocks";
                return false;
            }

            (List<string> originals, List<AssistantOriginalBlock> originalBlocks) = ReadOriginals(root);
            plan = new AssistantBoardPlan
            {
                Schema = TryReadInt(root, "schema", out int schema) ? schema : 1,
                Intent = Trimmed(ReadString(root, "intent")),
                Blocks = blocks,
                Notes = ReadStringList(root, "notes"),
                Originals = originals,
                OriginalBlocks = originalBlocks,
                Confidence = ReadConfidence(root) ?? 1.0,
                Warnings = ReadStringList(root, "warnings"),
            };

            ReadTarget(root, plan);
            return true;
        }
    }

    private static void ReadTarget(JsonElement root, AssistantBoardPlan plan)
    {
        if (!TryGetProperty(root, "target", out JsonElement target) ||
            target.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string board = Trimmed(ReadString(target, "board"));
        if (!string.IsNullOrEmpty(board)) plan.TargetBoard = board;

        if (TryReadInt(target, "pageIndex", out int pageIndex) && pageIndex >= 0)
            plan.TargetPageIndex = pageIndex;
    }

    private static List<AssistantBoardBlock> ReadBlocks(JsonElement root)
    {
        var blocks = new List<AssistantBoardBlock>();
        if (!TryGetProperty(root, "blocks", out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (blocks.Count >= MaxBlockCount) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            blocks.Add(ReadBlock(item, index));
            index++;
        }

        return blocks;
    }

    private static AssistantBoardBlock ReadBlock(JsonElement element, int index)
    {
        string id = Trimmed(ReadString(element, "id"));
        if (string.IsNullOrEmpty(id)) id = "b" + (index + 1);

        return new AssistantBoardBlock
        {
            Id = ClampText(id),
            Kind = NormalizeKind(ReadString(element, "kind")),
            Latex = ClampText(ReadString(element, "latex")),
            Text = ClampText(ReadString(element, "text")),
            Title = ClampText(ReadString(element, "title")),
            Author = ClampText(ReadString(element, "author")),
            Lines = ReadLines(element),
            Rect = ReadBlockRect(element),
            Style = ReadStyle(element),
            Note = ClampText(ReadString(element, "note")),
            Confidence = ReadConfidence(element) ?? 1.0,
        };
    }

    private static IReadOnlyList<string> ReadLines(JsonElement block)
    {
        var lines = new List<string>();
        if (!TryGetProperty(block, "lines", out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return lines;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (lines.Count >= MaxLineCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            lines.Add(ClampText(item.GetString()));
        }

        return lines;
    }

    /// <summary>读取 <c>originals</c>：字符串数组按行展示；对象数组取 text/latex 转行并保留结构。</summary>
    private static (List<string> Lines, List<AssistantOriginalBlock> Blocks) ReadOriginals(JsonElement root)
    {
        var lines = new List<string>();
        var blocks = new List<AssistantOriginalBlock>();
        if (!TryGetProperty(root, "originals", out JsonElement element)) return (lines, blocks);

        if (element.ValueKind == JsonValueKind.String)
        {
            string normalized = (element.GetString() ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string line in normalized.Split('\n'))
            {
                if (lines.Count >= MaxLineCount) break;
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                lines.Add(ClampText(trimmed));
            }

            return (lines, blocks);
        }

        if (element.ValueKind != JsonValueKind.Array) return (lines, blocks);

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (lines.Count >= MaxLineCount) break;

            if (item.ValueKind == JsonValueKind.String)
            {
                string text = Trimmed(item.GetString());
                if (text.Length > 0) lines.Add(ClampText(text));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;

            string blockText = ClampText(ReadString(item, "text"));
            string latex = ClampText(ReadString(item, "latex"));
            string display = !string.IsNullOrWhiteSpace(blockText) ? blockText : latex;
            if (string.IsNullOrWhiteSpace(display)) continue;

            lines.Add(ClampText(display));
            blocks.Add(new AssistantOriginalBlock
            {
                Text = blockText,
                Latex = latex,
                Rect = ReadBlockRect(item),
            });
        }

        return (lines, blocks);
    }

    private static AssistantRect ReadBlockRect(JsonElement block)
    {
        if (!TryGetProperty(block, "rect", out JsonElement rect)) return new AssistantRect(0, 0, 0, 0);
        return ReadRect(rect);
    }

    private static AssistantRect ReadRect(JsonElement rect)
    {
        if (rect.ValueKind == JsonValueKind.Object)
        {
            double x = ReadDouble(rect, "x") ?? ReadDouble(rect, "left") ?? 0;
            double y = ReadDouble(rect, "y") ?? ReadDouble(rect, "top") ?? 0;
            double w = ReadDouble(rect, "w") ?? ReadDouble(rect, "width") ?? 0;
            double h = ReadDouble(rect, "h") ?? ReadDouble(rect, "height") ?? 0;
            return MakeRect(x, y, w, h);
        }

        if (rect.ValueKind == JsonValueKind.Array)
        {
            var values = new List<double>();
            foreach (JsonElement item in rect.EnumerateArray())
            {
                if (values.Count >= 4) break;
                if (item.ValueKind == JsonValueKind.Number) values.Add(item.GetDouble());
            }

            if (values.Count == 4) return MakeRect(values[0], values[1], values[2], values[3]);
        }

        return new AssistantRect(0, 0, 0, 0);
    }

    private static AssistantRect MakeRect(double x, double y, double w, double h)
    {
        double left = Clamp01(x);
        double top = Clamp01(y);
        double right = Clamp01(x + w);
        double bottom = Clamp01(y + h);

        if (right < left) right = left;
        if (bottom < top) bottom = top;

        return new AssistantRect(left, top, right, bottom);
    }

    private static AssistantBoardStyle ReadStyle(JsonElement block)
    {
        var style = new AssistantBoardStyle();
        if (!TryGetProperty(block, "style", out JsonElement element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return style;
        }

        if (TryReadDouble(element, "fontSize", out double fontSize) && fontSize > 0)
            style.FontSize = fontSize;

        string align = Trimmed(ReadString(element, "align"));
        if (!string.IsNullOrEmpty(align)) style.Align = align.ToLowerInvariant();

        if (TryReadInt(element, "columns", out int columns) && columns > 0)
            style.Columns = columns;

        if (TryReadDouble(element, "lineHeight", out double lineHeight) && lineHeight > 0)
            style.LineHeight = lineHeight;

        return style;
    }

    private static List<string> ReadStringList(JsonElement root, string name)
    {
        var values = new List<string>();
        if (!TryGetProperty(root, name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (values.Count >= MaxBlockCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            values.Add(ClampText(item.GetString()));
        }

        return values;
    }

    /// <summary>读取 confidence（数字或字符串），夹取到 0..1；缺失返回 null。</summary>
    private static double? ReadConfidence(JsonElement element)
    {
        if (!TryReadDouble(element, "confidence", out double value)) return null;
        if (double.IsNaN(value)) return null;
        return Clamp01(value);
    }

    private static string NormalizeKind(string kind)
    {
        string value = Trimmed(kind).ToLowerInvariant();
        return value switch
        {
            "formula" => "formula",
            "wordlist" => "wordlist",
            "poem" => "poem",
            "summary" => "summary",
            _ => "text",
        };
    }

    private static string Trimmed(string value) => (value ?? "").Trim();

    private static string ClampText(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? ReadDouble(JsonElement element, string name) =>
        TryReadDouble(element, name, out double value) ? value : null;

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out JsonElement node)) return false;

        if (node.ValueKind == JsonValueKind.Number) return node.TryGetDouble(out value);

        if (node.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out JsonElement node)) return false;

        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetInt32(out value)) return true;
            if (!node.TryGetDouble(out double number)) return false;
            if (number < int.MinValue || number > int.MaxValue) return false;
            value = (int)number;
            return true;
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }
}

/// <summary>板书计划中的单个内容块，<see cref="Kind"/> 为 text / formula / wordlist / poem。</summary>
public sealed class AssistantBoardBlock
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "text";

    public string Latex { get; set; } = "";

    public string Text { get; set; } = "";

    public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    public AssistantRect Rect { get; set; }

    public AssistantBoardStyle Style { get; set; } = new();

    public string Note { get; set; } = "";

    /// <summary>该块识别/整理置信度，0..1；缺省 1.0。</summary>
    public double Confidence { get; set; } = 1.0;
}

/// <summary>识别原文块：服务端以对象数组返回 originals 时保留 text/latex/rect。</summary>
public sealed class AssistantOriginalBlock
{
    public string Text { get; set; } = "";

    public string Latex { get; set; } = "";

    public AssistantRect Rect { get; set; }
}

/// <summary>板书块的排版样式；缺省字号 24、左对齐、单列、行高 1.4。</summary>
public sealed class AssistantBoardStyle
{
    public double FontSize { get; set; } = 24;

    public string Align { get; set; } = "left";

    public int Columns { get; set; } = 1;

    public double LineHeight { get; set; } = 1.4;
}