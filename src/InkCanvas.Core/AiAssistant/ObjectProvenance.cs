using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 板书对象来源：AI 产出、教师修改、导入与用户墨迹分开记录，
/// 便于课堂复盘时区分「AI 写的」与「教师改过的」。
/// </summary>
public enum ObjectSource
{
    /// <summary>来源未知（旧数据或缺字段）。</summary>
    Unknown,

    /// <summary>用户手写墨迹。</summary>
    UserInk,

    /// <summary>AI 整理板书（board_plan 重排）。</summary>
    AiOrganize,

    /// <summary>AI 直接书写（文本/公式原生对象）。</summary>
    AiWrite,

    /// <summary>AI 识别的图形/形状标注。</summary>
    AiShape,

    /// <summary>课件批注。</summary>
    PptAnnotation,

    /// <summary>教师对 AI 产出的人工修改。</summary>
    TeacherEdit,

    /// <summary>从其他文件/题库导入的对象。</summary>
    Imported,
}

/// <summary>
/// 板书对象溯源信息：记录该对象由哪次 AI 请求、在哪个画板/页面/幻灯片上生成。
/// 纯数据、纯逻辑，无 UI/网络依赖；随对象元数据与时光机 AiStep 载荷一起持久化。
/// </summary>
public sealed record ObjectProvenance
{
    /// <summary>会话标识长度上限，超出截断，避免异常载荷撑大 JSON。</summary>
    public const int MaxIdLength = 128;

    /// <summary>画板标识长度上限。</summary>
    public const int MaxBoardIdLength = 64;

    /// <summary>自然语言指令长度上限；超出截断。</summary>
    public const int MaxInstructionLength = 512;

    /// <summary>分组标识长度上限。</summary>
    public const int MaxGroupIdLength = 128;

    /// <summary>生成该对象的课堂会话标识。</summary>
    public string SessionId { get; init; } = "";

    /// <summary>生成该对象的 AI 请求标识。</summary>
    public string RequestId { get; init; } = "";

    /// <summary>落盘的画板标识："main"/"side"/"overlay"/"document"/"ppt"。</summary>
    public string BoardId { get; init; } = "";

    /// <summary>落盘页面标识；Guid.Empty 表示旧数据未记录。</summary>
    public Guid PageId { get; init; }

    /// <summary>落盘时的课件幻灯片标识；非课件场景为空。</summary>
    public int? SlideId { get; init; }

    /// <summary>内容来源（用户墨迹、AI 产出、教师修改或导入）。</summary>
    public ObjectSource Source { get; init; }

    /// <summary>生成该对象时的用户指令原文（超长截断）。</summary>
    public string Instruction { get; init; } = "";

    /// <summary>生成时间（Unix 毫秒）。</summary>
    public long CreatedAtMs { get; init; }

    /// <summary>对象所属分组标识。</summary>
    public string GroupId { get; init; } = "";

    /// <summary>序列化为 camelCase JSON；null 字段输出空串/默认值，不抛异常。</summary>
    public string ToJson() => ObjectProvenanceCodec.ToJson(this);

    /// <summary>从 JSON 还原；空串、非法 JSON 或字段越界时返回 false，不抛异常。</summary>
    public static bool TryParse(string json, out ObjectProvenance provenance)
        => ObjectProvenanceCodec.TryParse(json, out provenance);
}

/// <summary>
/// <see cref="ObjectProvenance"/> 的 JSON 编解码：camelCase 命名、大小写不敏感读取，
/// 便于与既有 AiStep 载荷及 AiPasteMetadata v2 复用。纯数据、无 UI/网络依赖。
/// </summary>
public static class ObjectProvenanceCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>序列化为 JSON；传入 null 时返回 null。</summary>
    public static string ToJson(ObjectProvenance provenance)
    {
        if (provenance is null) return null;
        return JsonSerializer.Serialize(provenance.Normalize(), Options);
    }

    /// <summary>从 JSON 还原；空串或 JSON 非法时返回 false，且 <paramref name="provenance"/> 为 null。</summary>
    public static bool TryParse(string json, out ObjectProvenance provenance)
    {
        provenance = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            ObjectProvenance decoded = JsonSerializer.Deserialize<ObjectProvenance>(json, Options);
            if (decoded is null) return false;
            provenance = decoded.Normalize();
            return true;
        }
        catch (JsonException)
        {
            provenance = null;
            return false;
        }
        catch (NotSupportedException)
        {
            provenance = null;
            return false;
        }
        catch (ArgumentException)
        {
            provenance = null;
            return false;
        }
    }

    /// <summary>旧调用点兼容入口；行为与 <see cref="ToJson"/> 一致。</summary>
    public static string Encode(ObjectProvenance provenance) => ToJson(provenance);

    /// <summary>旧调用点兼容入口；行为与 <see cref="TryParse"/> 一致。</summary>
    public static bool TryDecode(string json, out ObjectProvenance provenance) => TryParse(json, out provenance);
}

internal static class ObjectProvenanceNormalization
{
    /// <summary>空值归一、长度截断与未知枚举归一；不修改传入实例。</summary>
    public static ObjectProvenance Normalize(this ObjectProvenance provenance)
    {
        if (provenance is null) return null;

        return provenance with
        {
            SessionId = Clamp(provenance.SessionId, ObjectProvenance.MaxIdLength),
            RequestId = Clamp(provenance.RequestId, ObjectProvenance.MaxIdLength),
            BoardId = Clamp(provenance.BoardId, ObjectProvenance.MaxBoardIdLength),
            Instruction = Clamp(provenance.Instruction, ObjectProvenance.MaxInstructionLength),
            GroupId = Clamp(provenance.GroupId, ObjectProvenance.MaxGroupIdLength),
            Source = Enum.IsDefined(provenance.Source) ? provenance.Source : ObjectSource.Unknown,
            CreatedAtMs = provenance.CreatedAtMs < 0 ? 0 : provenance.CreatedAtMs,
        };
    }

    private static string Clamp(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}