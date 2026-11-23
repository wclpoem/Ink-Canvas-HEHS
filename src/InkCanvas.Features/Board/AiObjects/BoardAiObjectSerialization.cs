using System.Numerics;
using InkCanvas.Core.Latex;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board.AiObjects;

/// <summary>JSON DTO for a native text object; the optional field keeps old v4 files readable.</summary>
internal sealed class BoardTextObjectFile
{
    public Guid? Id { get; set; }

    /// <summary>对象结构版本；旧文件缺字段时按 1 处理。</summary>
    public int Version { get; set; } = BoardObjectProvenance.DefaultVersion;
    public string Type { get; set; }
    public string Content { get; set; }
    public string Title { get; set; }
    public string Author { get; set; }
    public string Dynasty { get; set; }
    public string FontFamily { get; set; }
    public double FontSize { get; set; }
    public string Color { get; set; }
    public double LineHeight { get; set; }
    public string Align { get; set; }
    public int Columns { get; set; } = 1;
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public long? LayerOrder { get; set; }
    public string GroupId { get; set; }
    public string Source { get; set; }
    public string Instruction { get; set; }
    public long CreatedAt { get; set; }
    public float[] Transform { get; set; }
}

/// <summary>JSON DTO for a native formula object; LaTeX remains the editable source of truth.</summary>
internal sealed class BoardFormulaObjectFile
{
    public Guid? Id { get; set; }

    /// <summary>对象结构版本；旧文件缺字段时按 1 处理。</summary>
    public int Version { get; set; } = BoardObjectProvenance.DefaultVersion;
    public string Latex { get; set; }
    public string FontFamily { get; set; }
    public double FontSize { get; set; }
    public string Color { get; set; }
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public long? LayerOrder { get; set; }
    public string GroupId { get; set; }
    public string Source { get; set; }
    public string Instruction { get; set; }
    public long CreatedAt { get; set; }
    public float[] Transform { get; set; }
}

/// <summary>
/// Conversion and defensive validation for native AI objects. All clamps are silent so a partially
/// corrupt or legacy file never throws while opening; invalid objects are dropped by the caller.
/// </summary>
internal static class BoardAiObjectSerialization
{
    public const int MaxContentLength = 20000;
    public const int MaxTitleLength = 200;
    public const int MaxNameLength = 100;
    public const int MaxInstructionLength = 2000;
    public const int MaxGroupIdLength = 64;
    public const int MaxCoordinate = 1_000_000;
    public const float MaxExtent = 20000f;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 400;
    public const double MinLineHeight = 0.8;
    public const double MaxLineHeight = 3.0;
    public const float DefaultTextWidth = 360f;
    public const float DefaultTextHeight = 200f;
    public const float DefaultFormulaWidth = 200f;
    public const float DefaultFormulaHeight = 80f;

    public static BoardTextObjectFile ToFile(BoardTextObject text) => new()
    {
        Id = text.Id,
        Version = text.Version,
        Type = text.Type,
        Content = text.Content,
        Title = text.Title,
        Author = text.Author,
        Dynasty = text.Dynasty,
        FontFamily = text.FontFamily,
        FontSize = text.FontSize,
        Color = text.Color,
        LineHeight = text.LineHeight,
        Align = text.Align,
        Columns = text.Columns,
        Left = text.Left,
        Top = text.Top,
        Width = text.Width,
        Height = text.Height,
        LayerOrder = text.LayerOrder,
        GroupId = text.GroupId,
        Source = text.Source,
        Instruction = text.Instruction,
        CreatedAt = text.CreatedAt,
        Transform = text.TransformMatrix?.ToArray(),
    };

    public static BoardTextObject FromFile(BoardTextObjectFile file)
    {
        var text = new BoardTextObject
        {
            Id = file?.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            Version = file?.Version > 0 ? file.Version : BoardObjectProvenance.DefaultVersion,
            Type = file?.Type,
            Content = file?.Content,
            Title = file?.Title,
            Author = file?.Author,
            Dynasty = file?.Dynasty,
            FontFamily = file?.FontFamily,
            FontSize = file?.FontSize ?? 0,
            Color = file?.Color,
            LineHeight = file?.LineHeight ?? 0,
            Align = file?.Align,
            Columns = file?.Columns ?? 1,
            Left = file?.Left ?? 0,
            Top = file?.Top ?? 0,
            Width = file?.Width ?? 0,
            Height = file?.Height ?? 0,
            LayerOrder = file?.LayerOrder,
            GroupId = file?.GroupId,
            Source = file?.Source,
            Instruction = file?.Instruction,
            CreatedAt = file?.CreatedAt ?? 0,
            TransformMatrix = file?.Transform?.ToArray(),
        };
        Normalize(text);
        if (text.Width <= 0) text.Width = DefaultTextWidth;
        if (text.Height <= 0) text.Height = DefaultTextHeight;
        return text;
    }

    public static BoardFormulaObjectFile ToFile(BoardFormulaObject formula) => new()
    {
        Id = formula.Id,
        Version = formula.Version,
        Latex = formula.Latex,
        FontFamily = formula.FontFamily,
        FontSize = formula.FontSize,
        Color = formula.Color,
        Left = formula.Left,
        Top = formula.Top,
        Width = formula.Width,
        Height = formula.Height,
        LayerOrder = formula.LayerOrder,
        GroupId = formula.GroupId,
        Source = formula.Source,
        Instruction = formula.Instruction,
        CreatedAt = formula.CreatedAt,
        Transform = formula.TransformMatrix?.ToArray(),
    };

    public static BoardFormulaObject FromFile(BoardFormulaObjectFile file)
    {
        var formula = new BoardFormulaObject
        {
            Id = file?.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            Version = file?.Version > 0 ? file.Version : BoardObjectProvenance.DefaultVersion,
            Latex = file?.Latex,
            FontFamily = file?.FontFamily,
            FontSize = file?.FontSize ?? 0,
            Color = file?.Color,
            Left = file?.Left ?? 0,
            Top = file?.Top ?? 0,
            Width = file?.Width ?? 0,
            Height = file?.Height ?? 0,
            LayerOrder = file?.LayerOrder,
            GroupId = file?.GroupId,
            Source = file?.Source,
            Instruction = file?.Instruction,
            CreatedAt = file?.CreatedAt ?? 0,
            TransformMatrix = file?.Transform?.ToArray(),
        };
        Normalize(formula);
        if (formula.Width <= 0) formula.Width = DefaultFormulaWidth;
        if (formula.Height <= 0) formula.Height = DefaultFormulaHeight;
        return formula;
    }

    public static void Normalize(BoardTextObject text)
    {
        if (text is null) return;
        text.Version = text.Version < BoardObjectProvenance.DefaultVersion
            ? BoardObjectProvenance.DefaultVersion : text.Version;
        text.Type = NormalizeType(text.Type);
        text.Content = Limit(text.Content, MaxContentLength);
        text.Title = Limit(text.Title, MaxTitleLength);
        text.Author = Limit(text.Author, MaxNameLength);
        text.Dynasty = Limit(text.Dynasty, MaxNameLength);
        text.FontFamily = Limit(text.FontFamily, 64);
        if (text.FontFamily.Length == 0) text.FontFamily = "Microsoft YaHei UI";
        text.FontSize = Clamp(text.FontSize <= 0 ? 32 : text.FontSize, MinFontSize, MaxFontSize);
        text.Color = NormalizeColor(text.Color);
        text.LineHeight = Clamp(text.LineHeight <= 0 ? 1.4 : text.LineHeight, MinLineHeight, MaxLineHeight);
        text.Align = NormalizeAlign(text.Align);
        text.Columns = Math.Clamp(text.Columns <= 0 ? 1 : text.Columns, 1, 3);
        text.Left = ClampCoordinate(text.Left);
        text.Top = ClampCoordinate(text.Top);
        text.Width = ClampExtent(text.Width, DefaultTextWidth);
        text.Height = ClampExtent(text.Height, DefaultTextHeight);
        text.LayerOrder = text.LayerOrder is { } order ? Math.Max(0, order) : null;
        text.GroupId = Limit(text.GroupId, MaxGroupIdLength);
        text.Source = Limit(text.Source, 32);
        if (text.Source.Length == 0) text.Source = "ai";
        text.Instruction = Limit(text.Instruction, MaxInstructionLength);
        text.CreatedAt = Math.Max(0, text.CreatedAt);
        text.TransformMatrix = NormalizeTransform(text.TransformMatrix);
    }

    public static void Normalize(BoardFormulaObject formula)
    {
        if (formula is null) return;
        formula.Version = formula.Version < BoardObjectProvenance.DefaultVersion
            ? BoardObjectProvenance.DefaultVersion : formula.Version;
        formula.Latex = Limit(formula.Latex, LatexToMathML.MaxLatexLength);
        formula.FontFamily = Limit(formula.FontFamily, 64);
        if (formula.FontFamily.Length == 0) formula.FontFamily = "Cambria Math";
        formula.FontSize = Clamp(formula.FontSize <= 0 ? 30 : formula.FontSize, MinFontSize, MaxFontSize);
        formula.Color = NormalizeColor(formula.Color);
        formula.Left = ClampCoordinate(formula.Left);
        formula.Top = ClampCoordinate(formula.Top);
        formula.Width = ClampExtent(formula.Width, DefaultFormulaWidth);
        formula.Height = ClampExtent(formula.Height, DefaultFormulaHeight);
        formula.LayerOrder = formula.LayerOrder is { } order ? Math.Max(0, order) : null;
        formula.GroupId = Limit(formula.GroupId, MaxGroupIdLength);
        formula.Source = Limit(formula.Source, 32);
        if (formula.Source.Length == 0) formula.Source = "ai";
        formula.Instruction = Limit(formula.Instruction, MaxInstructionLength);
        formula.CreatedAt = Math.Max(0, formula.CreatedAt);
        formula.TransformMatrix = NormalizeTransform(formula.TransformMatrix);
    }

    public static bool IsValid(BoardTextObject text)
        => text is not null
           && (!string.IsNullOrWhiteSpace(text.Content) || !string.IsNullOrWhiteSpace(text.Title))
           && text.Width > 0 && text.Height > 0
           && float.IsFinite(text.Left) && float.IsFinite(text.Top)
           && float.IsFinite(text.Width) && float.IsFinite(text.Height);

    public static bool IsValid(BoardFormulaObject formula)
        => formula is not null && !string.IsNullOrWhiteSpace(formula.Latex)
           && formula.Width > 0 && formula.Height > 0
           && float.IsFinite(formula.Left) && float.IsFinite(formula.Top)
           && float.IsFinite(formula.Width) && float.IsFinite(formula.Height);

    public static bool SameState(BoardTextObject a, BoardTextObject b)
        => a is not null && b is not null
           && a.Type == b.Type && a.Content == b.Content && a.Title == b.Title
           && a.Author == b.Author && a.Dynasty == b.Dynasty
           && a.FontFamily == b.FontFamily && a.FontSize == b.FontSize && a.Color == b.Color
           && a.LineHeight == b.LineHeight && a.Align == b.Align
           && a.Columns == b.Columns
           && a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && a.LayerOrder == b.LayerOrder && a.GroupId == b.GroupId
           && BoardElementMath.Same(a.TransformMatrix, b.TransformMatrix);

    public static bool SameState(BoardFormulaObject a, BoardFormulaObject b)
        => a is not null && b is not null
           && a.Latex == b.Latex && a.FontFamily == b.FontFamily && a.FontSize == b.FontSize
           && a.Color == b.Color
           && a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && a.LayerOrder == b.LayerOrder && a.GroupId == b.GroupId
           && BoardElementMath.Same(a.TransformMatrix, b.TransformMatrix);

    private static string Limit(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string NormalizeType(string type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "wordlist" => "wordList",
        "poem" => "poem",
        "summary" => "summary",
        _ => "text",
    };

    private static string NormalizeAlign(string align) => (align ?? "").Trim().ToLowerInvariant() switch
    {
        "center" => "center",
        "right" => "right",
        "justify" => "justify",
        _ => "left",
    };

    private static string NormalizeColor(string color)
    {
        string value = Limit(color, 32).Trim();
        return value.Length == 0 ? "#FF202024" : value;
    }

    private static double Clamp(double value, double min, double max)
        => !double.IsFinite(value) ? min : Math.Clamp(value, min, max);

    private static float ClampCoordinate(float value)
        => float.IsFinite(value) ? Math.Clamp(value, -MaxCoordinate, MaxCoordinate) : 0;

    private static float ClampExtent(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value <= 0 ? fallback : value, 1f, MaxExtent) : fallback;

    private static float[] NormalizeTransform(float[] transform)
    {
        if (transform is not { Length: 6 }) return null;
        foreach (float value in transform)
            if (!float.IsFinite(value)) return null;
        return transform.ToArray();
    }
}