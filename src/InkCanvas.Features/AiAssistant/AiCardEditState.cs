using System.Text.Json;
using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// AI 卡片编辑的纯逻辑状态：从 <see cref="AiPasteMetadata"/> 读取可编辑字段，
/// 应用编辑后返回保留其余元数据的新对象，并构建重渲染参数。不依赖 UI，可直接单测。
/// </summary>
public sealed class AiCardEditState
{
    private static readonly JsonSerializerOptions StyleJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool IsBoardPlan { get; private init; }

    public string BlockKind { get; private init; } = "text";

    public bool IsFormula { get; private init; }

    public bool IsPoem { get; private init; }

    public bool IsWordList { get; private init; }

    public bool HasTitleEditor => IsBoardPlan && (IsPoem || BlockKind == "text");

    public bool HasAuthorEditor => IsBoardPlan && IsPoem;

    public bool BodyAcceptsReturn => !IsFormula;

    public string DialogTitle => IsBoardPlan ? "编辑 AI 板书卡片" : "编辑 AI 卡片";

    public string TitleLabel => "标题";

    public string AuthorLabel => "作者 / 朝代";

    public string EditorLabel { get; private init; } = "卡片文本";

    public double EditorMinWidth => IsBoardPlan && !IsFormula ? 380 : 360;

    public double EditorMinHeight => IsFormula ? 60 : IsBoardPlan ? 150 : 140;

    public string Title { get; private init; } = "";

    public string Author { get; private init; } = "";

    public string Body { get; private init; } = "";

    public static AiCardEditState FromMetadata(AiPasteMetadata metadata)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));

        bool boardPlan = metadata.Kind == AiPasteKind.BoardPlan;
        string blockKind = boardPlan ? NormalizeKind(metadata.BlockKind) : "";
        bool formula = metadata.Kind == AiPasteKind.Formula || blockKind == "formula";
        bool poem = blockKind == "poem";
        bool wordList = blockKind == "wordlist";

        string body;
        if (boardPlan)
        {
            // 公式块以 Latex 为准（与写入时 BuildMetadata 的字段语义一致）。
            body = formula
                ? metadata.Latex ?? ""
                : metadata.Lines is { Count: > 0 }
                    ? string.Join("\n", metadata.Lines)
                    : metadata.Text ?? "";
        }
        else if (metadata.Kind == AiPasteKind.Formula)
        {
            // 旧公式卡片把 LaTeX 存在 Latex 字段；原实现误读 Text 导致编辑框为空，这里修正。
            body = metadata.Latex ?? "";
        }
        else if (metadata.Kind == AiPasteKind.Steps && metadata.Lines is { Count: > 0 })
        {
            body = string.Join("\n", metadata.Lines);
        }
        else
        {
            body = metadata.Text ?? "";
        }

        return new AiCardEditState
        {
            IsBoardPlan = boardPlan,
            BlockKind = blockKind,
            IsFormula = formula,
            IsPoem = poem,
            IsWordList = wordList,
            Title = metadata.Title ?? "",
            Author = metadata.Author ?? "",
            Body = body,
            EditorLabel = BuildEditorLabel(boardPlan, formula, poem, wordList),
        };
    }

    /// <summary>应用编辑结果；BoardPlan 保留 Schema/StyleJson/Instruction/CreatedAt/Notes 等元数据。</summary>
    public AiPasteMetadata ApplyTo(AiPasteMetadata metadata, string title, string author, string body)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        string text = body ?? "";

        if (IsBoardPlan)
        {
            AiPasteMetadata updated = CloneBoardPlanMetadata(metadata);
            updated.BlockKind = BlockKind;
            updated.Title = (title ?? "").Trim();
            updated.Author = (author ?? "").Trim();

            if (IsFormula)
            {
                updated.Latex = text.Trim();
                updated.Lines = new List<string>();
                updated.Text = "";
            }
            else
            {
                List<string> lines = SplitLines(text);
                updated.Lines = lines;
                updated.Text = BlockKind == "text" ? text : string.Join("\n", lines);
            }

            return updated;
        }

        var legacy = new AiPasteMetadata
        {
            Version = metadata.Version > 0 ? metadata.Version : AiPasteMetadata.CurrentVersion,
            Kind = metadata.Kind,
            Annotations = metadata.Annotations is { Count: > 0 }
                ? metadata.Annotations.Select(points => new List<double>(points)).ToList()
                : new List<List<double>>(),
        };

        if (IsFormula)
        {
            legacy.Latex = text.Trim();
        }
        else
        {
            legacy.Text = text;
            legacy.Lines = SplitLines(text);
        }

        return legacy;
    }

    /// <summary>BoardPlan 非公式块的重渲染参数（字体/对齐/列数来自 StyleJson）。</summary>
    public BoardTextCardSpec BuildTextSpec(AiPasteMetadata metadata, string title, string author, string body)
    {
        AssistantBoardStyle style = ParseStyle(metadata?.StyleJson);
        List<string> lines = SplitLines(body);
        string text = BlockKind == "text" ? body ?? "" : string.Join("\n", lines);

        return new BoardTextCardSpec
        {
            Kind = IsBoardPlan ? BlockKind : "text",
            Title = (title ?? "").Trim(),
            Subtitle = IsPoem ? (author ?? "").Trim() : "",
            Text = text,
            Lines = lines,
            FontSize = style.FontSize > 0 ? style.FontSize : 24,
            LineHeight = style.LineHeight > 0 ? style.LineHeight : 1.4,
            Align = style.Align is { Length: > 0 } ? style.Align : IsPoem ? "center" : "left",
            Columns = style.Columns > 0 ? style.Columns : 1,
        };
    }

    /// <summary>公式排版失败时的文本回落参数（保留 LaTeX 源）。</summary>
    public BoardTextCardSpec BuildFormulaFallbackSpec(AiPasteMetadata metadata, string latex)
    {
        AssistantBoardStyle style = ParseStyle(metadata?.StyleJson);
        return new BoardTextCardSpec
        {
            Kind = "text",
            Title = "公式（LaTeX）",
            Text = string.IsNullOrWhiteSpace(latex) ? "（空公式）" : latex,
            FontSize = style.FontSize > 0 ? style.FontSize : 22,
            LineHeight = style.LineHeight > 0 ? style.LineHeight : 1.5,
            Align = "left",
        };
    }

    /// <summary>旧卡片（贴回/步骤/公式）的文本行。</summary>
    public static List<string> ContentLines(AiPasteMetadata metadata)
    {
        if (metadata is null) return new List<string>();
        if (metadata.Kind == AiPasteKind.Formula)
            return new List<string> { metadata.Latex ?? "" };
        if (metadata.Lines is { Count: > 0 }) return metadata.Lines.ToList();
        return SplitLines(metadata.Text);
    }

    public static List<string> SplitLines(string text)
        => (text ?? "").Replace("\r", "").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>
    /// 教师修改原生文本后标记来源：Source 置为 teacher（旧对象默认 ai），
    /// Instruction 追加固定前缀，保存/导出/回放都能区分 AI 原文与教师订正。
    /// </summary>
    public static void MarkTeacherEdited(Board.AiObjects.BoardTextObject text)
    {
        if (text is null) return;
        text.Source = InkCanvas.Ink.BoardObjectProvenance.TeacherSource;
        text.Instruction = WithTeacherPrefix(text.Instruction);
    }

    /// <summary>教师修改原生公式后标记来源，规则与文本对象一致。</summary>
    public static void MarkTeacherEdited(Board.AiObjects.BoardFormulaObject formula)
    {
        if (formula is null) return;
        formula.Source = InkCanvas.Ink.BoardObjectProvenance.TeacherSource;
        formula.Instruction = WithTeacherPrefix(formula.Instruction);
    }

    private static string WithTeacherPrefix(string instruction)
    {
        const string prefix = InkCanvas.Ink.BoardObjectProvenance.TeacherInstructionPrefix;
        string value = instruction ?? "";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value : prefix + value;
    }

    public static string NormalizeKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "formula" => "formula",
        "wordlist" => "wordlist",
        "poem" => "poem",
        _ => "text",
    };

    public static AssistantBoardStyle ParseStyle(string styleJson)
    {
        if (string.IsNullOrWhiteSpace(styleJson)) return new AssistantBoardStyle();
        try
        {
            return JsonSerializer.Deserialize<AssistantBoardStyle>(styleJson, StyleJsonOptions)
                ?? new AssistantBoardStyle();
        }
        catch (JsonException)
        {
            return new AssistantBoardStyle();
        }
        catch (NotSupportedException)
        {
            return new AssistantBoardStyle();
        }
    }

    private static string BuildEditorLabel(bool boardPlan, bool formula, bool poem, bool wordList)
    {
        if (boardPlan)
        {
            if (formula) return "LaTeX 公式（支持分式/根号/上下标/矩阵/方程组）";
            if (wordList) return "词语（每行：词｜拼音｜释义）";
            if (poem) return "古诗正文（逐行）";
            return "正文（# / ## 标题、- 项目符号）";
        }

        return formula ? "LaTeX 公式" : "卡片文本";
    }

    private static AiPasteMetadata CloneBoardPlanMetadata(AiPasteMetadata source) => new()
    {
        Version = source.Version > 0 ? source.Version : AiPasteMetadata.CurrentVersion,
        Kind = source.Kind,
        Latex = source.Latex ?? "",
        Text = source.Text ?? "",
        Lines = source.Lines is { Count: > 0 } ? new List<string>(source.Lines) : new List<string>(),
        Annotations = source.Annotations is { Count: > 0 }
            ? source.Annotations.Select(points => new List<double>(points)).ToList()
            : new List<List<double>>(),
        Schema = source.Schema,
        StyleJson = source.StyleJson ?? "",
        Instruction = source.Instruction ?? "",
        CreatedAt = source.CreatedAt,
        Notes = source.Notes is { Count: > 0 } ? new List<string>(source.Notes) : new List<string>(),
        BlockKind = source.BlockKind ?? "",
        Title = source.Title ?? "",
        Author = source.Author ?? "",
    };
}