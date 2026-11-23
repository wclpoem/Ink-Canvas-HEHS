using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;

namespace InkCanvas.Ink;

/// <summary>统一板书对象类型：笔画、原生文本、原生公式、图片（含 SVG）与媒体。</summary>
public enum BoardObjectType
{
    Stroke,
    Text,
    Formula,
    Image,
    Media,
}

/// <summary>只读样式视图；不承载渲染参数，仅用于统一命令与序列化读取。</summary>
public readonly record struct BoardObjectStyle(
    string FontFamily = "",
    double FontSize = 0,
    string Color = "",
    double LineHeight = 0,
    string Align = "",
    int Columns = 1,
    float StrokeWidth = 0);

/// <summary>
/// 只读板书对象视图。用于把笔画、原生文本/公式、图片/媒体放进同一套命令与序列化入口，
/// 不改动 <see cref="ISelectionElement"/> 的渲染/选择契约。
/// <see cref="Source"/> 暴露底层可变实例，写入仍走既有 BoardService / InkController 命令。
/// </summary>
public interface IBoardObject
{
    Guid Id { get; }

    BoardObjectType Type { get; }

    /// <summary>文本/公式为正文或 LaTeX；图片/媒体为名称或元数据摘要；笔画为空串。</summary>
    string Content { get; }

    BoardObjectStyle Style { get; }

    /// <summary>未应用 <see cref="RectTransform"/> 的对象矩形（世界坐标）。</summary>
    Rect Rect { get; }

    /// <summary>对象级仿射变换；无变换时为单位矩阵。</summary>
    Matrix3x2 RectTransform { get; }

    /// <summary>统一字符串分组标识；空串表示未分组。</summary>
    string GroupId { get; }

    ObjectProvenance Provenance { get; }

    /// <summary>对象结构版本；旧数据视为 1。</summary>
    int Version { get; }

    /// <summary>底层可变实例（InkStroke / BoardTextObject / ...）；只读调用方应只读取。</summary>
    object Source { get; }
}

/// <summary>从既有字段构造 <see cref="ObjectProvenance"/>，避免各适配器重复映射。</summary>
public static class BoardObjectProvenance
{
    public const int DefaultVersion = 1;

    /// <summary>教师订正 AI 原生对象后的 Source 标记（映射到 <see cref="ObjectSource.TeacherEdit"/>）。</summary>
    public const string TeacherSource = "teacher";

    /// <summary>教师订正时追加到 Instruction 的固定前缀，保证旧数据可区分来源。</summary>
    public const string TeacherInstructionPrefix = "[教师订正] ";

    /// <summary>把旧 Source 字符串（"ai"/"userInk" 等）映射为枚举；未知值回退 <see cref="ObjectSource.Unknown"/>。</summary>
    public static ObjectProvenance FromLegacy(
        string source, string instruction = "", long createdAtMs = 0, string groupId = "")
        => new()
        {
            Source = ParseSource(source),
            Instruction = instruction ?? "",
            CreatedAtMs = createdAtMs < 0 ? 0 : createdAtMs,
            GroupId = groupId ?? "",
        };

    public static ObjectSource ParseSource(string source)
    {
        string value = (source ?? "").Trim();
        if (value.Length == 0) return ObjectSource.Unknown;
        if (string.Equals(value, TeacherSource, StringComparison.OrdinalIgnoreCase))
            return ObjectSource.TeacherEdit;
        return Enum.TryParse(value, ignoreCase: true, out ObjectSource parsed) ? parsed : ObjectSource.Unknown;
    }
}

/// <summary>
/// 只读笔画视图：几何取 <see cref="InkStroke.Bounds"/> 与 <see cref="InkStroke.ObjectFrame"/>，
/// 分组取形状来源的 <see cref="ShapeSource.GroupId"/>。不改变笔画本身。
/// </summary>
public sealed class StrokeObjectAdapter : IBoardObject
{
    public StrokeObjectAdapter(InkStroke stroke) => Stroke = stroke ?? throw new ArgumentNullException(nameof(stroke));

    public InkStroke Stroke { get; }

    public Guid Id => Stroke.Id;

    public BoardObjectType Type => BoardObjectType.Stroke;

    public string Content => "";

    public BoardObjectStyle Style => new(
        Color: ToHex(Stroke.Attributes.Color),
        StrokeWidth: Stroke.Attributes.BaseWidth);

    public Rect Rect => Stroke.Bounds;

    public Matrix3x2 RectTransform => Stroke.ObjectFrame.Transform;

    public string GroupId => Stroke.ShapeSource is { } source && source.GroupId != Guid.Empty
        ? source.GroupId.ToString("N")
        : "";

    public ObjectProvenance Provenance => BoardObjectProvenance.FromLegacy("userInk", groupId: GroupId);

    public int Version => BoardObjectProvenance.DefaultVersion;

    public object Source => Stroke;

    private static string ToHex(InkColor color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}