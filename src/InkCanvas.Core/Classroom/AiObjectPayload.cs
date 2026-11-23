using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 时光机回放中的一个 AI 板书对象。新会话使用 <see cref="IsNative"/> = true 的结构化文本/公式
/// 快照，由矢量渲染器绘制；旧会话仍可携带 <see cref="RenderPngBase64"/>（图片兼容路径）。
/// 回放只读取事件数据，不重新请求模型、不重新渲染为 PNG。
/// </summary>
public sealed class TimelineAiObject
{
    public string Id { get; init; } = "";

    /// <summary>formula / text / wordList / poem / summary；旧图片为 image。</summary>
    public string Kind { get; init; } = "";

    public int PageIndex { get; init; }

    /// <summary>0 = 主展板，1 = 板中板；旧会话缺失时为 0。</summary>
    public int BoardId { get; init; }

    /// <summary>
    /// 对象级仿射变换（6 元：m11, m12, m21, m22, dx, dy）；schema=2 旧载荷缺失时为 null，
    /// 回放按 identity 处理，可用 <see cref="HasTransform"/> 判断是否需要标记。
    /// </summary>
    public float[] Transform { get; init; }

    /// <summary>对象级来源（userInk/aiOrganize/aiWrite/aiShape/pptAnnotation/teacherEdit）；旧载荷为空串。</summary>
    public string Source { get; init; } = "";

    /// <summary>对象级课件幻灯片标识；缺失时回退载荷级，再缺失为 null。</summary>
    public int? SlideId { get; init; }

    /// <summary>生成该对象的 AI 请求标识；旧载荷为空串。</summary>
    public string RequestId { get; init; } = "";

    /// <summary>是否带有可用且有限的 6 元变换；旧 schema=2 对象为 false。</summary>
    public bool HasTransform => Transform is { Length: 6 } && Transform.All(float.IsFinite);

    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public long? LayerOrder { get; init; }

    /// <summary>对象结构版本；schema=3 原生对象默认 1，旧载荷缺字段同样按 1 处理。</summary>
    public int Version { get; init; } = 1;

    // ---- 结构化原生对象（IsNative=true） ----

    public bool IsNative { get; init; }

    public string Content { get; init; } = "";

    public string Title { get; init; } = "";

    public string Author { get; init; } = "";

    public string Dynasty { get; init; } = "";

    public string Latex { get; init; } = "";

    public string FontFamily { get; init; } = "Microsoft YaHei UI";

    public double FontSize { get; init; } = 32;

    public string Color { get; init; } = "#FF202024";

    public double LineHeight { get; init; } = 1.4;

    public string Align { get; init; } = "left";

    public int Columns { get; init; } = 1;

    public string GroupId { get; init; } = "";

    // ---- 旧 PNG 卡片兼容字段 ----

    public string Text { get; init; } = "";

    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    public string RenderPngBase64 { get; init; } = "";

    public bool RenderMissing { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool HasRenderPng => !IsNative && !RenderMissing && !string.IsNullOrEmpty(RenderPngBase64);

    public bool IsFormula => IsNative && string.Equals(Kind, "formula", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条 <see cref="TimelineEventKind.AiStep"/> 事件的结构化载荷。</summary>
public sealed class AiObjectsPayload
{
    /// <summary>add / replace / update / remove。</summary>
    public string Action { get; init; } = "";

    public int PageIndex { get; init; }

    public int BoardId { get; init; }

    /// <summary>旧图片卡片对象（兼容旧会话/旧卡片）。</summary>
    public IReadOnlyList<TimelineAiObject> Objects { get; init; } = Array.Empty<TimelineAiObject>();

    /// <summary>结构化原生文本/公式对象（新会话）。</summary>
    public IReadOnlyList<TimelineAiObject> NativeObjects { get; init; } = Array.Empty<TimelineAiObject>();

    /// <summary>落盘时的课件幻灯片标识；schema=2 旧载荷缺失时为 null。</summary>
    public int? SlideId { get; init; }

    /// <summary>落盘时的动画阶段；schema=2 旧载荷缺失时为 null。</summary>
    public int? AnimationStep { get; init; }

    /// <summary>课堂会话标识；schema=2 旧载荷缺失时为空串。</summary>
    public string SessionId { get; init; } = "";

    /// <summary>内容来源（userInk/aiOrganize/aiWrite/aiShape/pptAnnotation）；旧载荷为空串。</summary>
    public string Source { get; init; } = "";

    /// <summary>生成这批对象的 AI 请求标识；旧载荷为空串。</summary>
    public string RequestId { get; init; } = "";

    public IEnumerable<TimelineAiObject> AllObjects => Objects.Concat(NativeObjects);
}

/// <summary>
/// AI 板书对象事件的 JSON 编解码。字段顺序固定为 objectId → action → schema，
/// 便于 <see cref="InkRecorder"/> 的去重签名在前缀内包含对象 id 与动作。
/// 纯数据、无 UI/网络依赖。
/// </summary>
public static class AiObjectPayloadCodec
{
    /// <summary>载荷 schema 版本；3 新增 slideId/source/requestId，旧 schema=2 载荷仍可读。</summary>
    public const int Schema = 3;

    /// <summary>单对象 PNG 直接入时间轴的上限；仅旧卡片路径使用。</summary>
    public const int MaxRenderPngBytes = 400 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Encode(
        string action,
        int pageIndex,
        int boardId,
        IReadOnlyList<TimelineAiObject> objects,
        IReadOnlyList<TimelineAiObject> nativeObjects = null,
        int? slideId = null,
        string source = null,
        string requestId = null,
        string sessionId = null,
        int? animationStep = null)
    {
        var images = objects?.Where(item => item is not null).ToList() ?? new List<TimelineAiObject>();
        var natives = nativeObjects?.Where(item => item is not null).ToList() ?? new List<TimelineAiObject>();
        if (images.Count + natives.Count == 0) return null;

        var dto = new PayloadDto
        {
            ObjectId = images.Count > 0 ? images[0].Id ?? "" : natives[0].Id ?? "",
            Action = action ?? "",
            Schema = Schema,
            PageIndex = pageIndex,
            BoardId = boardId,
            SlideId = slideId,
            AnimationStep = animationStep,
            SessionId = sessionId ?? "",
            Source = source ?? "",
            RequestId = requestId ?? "",
            Objects = images.Select(ToImageDto).ToList(),
            NativeObjects = natives.Select(ToNativeDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static bool TryDecode(string payload, out AiObjectsPayload result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        try
        {
            PayloadDto dto = JsonSerializer.Deserialize<PayloadDto>(payload, Options);
            if (dto is null) return false;

            int pageIndex = dto.PageIndex;
            int boardId = dto.BoardId;
            var objects = new List<TimelineAiObject>(dto.Objects?.Count ?? 0);
            foreach (ImageObjectDto item in dto.Objects ?? new List<ImageObjectDto>())
            {
                if (item is null || string.IsNullOrEmpty(item.Id)) continue;
                objects.Add(new TimelineAiObject
                {
                    Id = item.Id,
                    Kind = item.Kind ?? "",
                    PageIndex = pageIndex,
                    BoardId = boardId,
                    Transform = NormalizeTransform(item.Transform),
                    Source = string.IsNullOrEmpty(item.Source) ? dto.Source ?? "" : item.Source,
                    SlideId = item.SlideId ?? dto.SlideId,
                    RequestId = string.IsNullOrEmpty(item.RequestId) ? dto.RequestId ?? "" : item.RequestId,
                    Left = item.Left,
                    Top = item.Top,
                    Width = item.Width,
                    Height = item.Height,
                    LayerOrder = item.LayerOrder,
                    Latex = item.Latex ?? "",
                    Text = item.Text ?? "",
                    Lines = item.Lines ?? new List<string>(),
                    RenderPngBase64 = item.RenderPngBase64 ?? "",
                    RenderMissing = item.RenderMissing,
                    Notes = item.Notes ?? new List<string>(),
                });
            }

            var natives = new List<TimelineAiObject>(dto.NativeObjects?.Count ?? 0);
            foreach (NativeObjectDto item in dto.NativeObjects ?? new List<NativeObjectDto>())
            {
                if (item is null || string.IsNullOrEmpty(item.Id)) continue;
                natives.Add(new TimelineAiObject
                {
                    Id = item.Id,
                    Kind = item.Type ?? "text",
                    PageIndex = pageIndex,
                    BoardId = boardId,
                    Transform = NormalizeTransform(item.Transform),
                    Source = string.IsNullOrEmpty(item.Source) ? dto.Source ?? "" : item.Source,
                    SlideId = item.SlideId ?? dto.SlideId,
                    RequestId = string.IsNullOrEmpty(item.RequestId) ? dto.RequestId ?? "" : item.RequestId,
                    IsNative = true,
                    Content = item.Content ?? "",
                    Title = item.Title ?? "",
                    Author = item.Author ?? "",
                    Dynasty = item.Dynasty ?? "",
                    Latex = item.Latex ?? "",
                    FontFamily = string.IsNullOrWhiteSpace(item.FontFamily) ? "Microsoft YaHei UI" : item.FontFamily,
                    FontSize = item.FontSize > 0 ? item.FontSize : 32,
                    Color = item.Color ?? "#FF202024",
                    LineHeight = item.LineHeight > 0 ? item.LineHeight : 1.4,
                    Align = item.Align ?? "left",
                    Columns = item.Columns <= 0 ? 1 : Math.Clamp(item.Columns, 1, 3),
                    Left = item.Left,
                    Top = item.Top,
                    Width = item.Width,
                    Height = item.Height,
                    LayerOrder = item.LayerOrder,
                    Version = item.Version > 0 ? item.Version : 1,
                    GroupId = item.GroupId ?? "",
                    Text = item.Content ?? "",
                    Lines = SplitLines(item.Content),
                });
            }

            if (objects.Count + natives.Count == 0) return false;
            result = new AiObjectsPayload
            {
                Action = (dto.Action ?? "").Trim().ToLowerInvariant(),
                PageIndex = pageIndex,
                BoardId = boardId,
                SlideId = dto.SlideId,
                AnimationStep = dto.AnimationStep,
                SessionId = dto.SessionId ?? "",
                Source = dto.Source ?? "",
                RequestId = dto.RequestId ?? "",
                Objects = objects,
                NativeObjects = natives,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static ImageObjectDto ToImageDto(TimelineAiObject item) => new()
    {
        Id = item.Id ?? "",
        Kind = item.Kind ?? "",
        Transform = NormalizeTransform(item.Transform),
        Source = item.Source ?? "",
        SlideId = item.SlideId,
        RequestId = item.RequestId ?? "",
        Left = item.Left,
        Top = item.Top,
        Width = item.Width,
        Height = item.Height,
        LayerOrder = item.LayerOrder,
        Latex = item.Latex ?? "",
        Text = item.Text ?? "",
        Lines = item.Lines?.ToList() ?? new List<string>(),
        RenderPngBase64 = item.RenderPngBase64 ?? "",
        RenderMissing = item.RenderMissing,
        Notes = item.Notes?.ToList() ?? new List<string>(),
    };

    private static NativeObjectDto ToNativeDto(TimelineAiObject item) => new()
    {
        Id = item.Id ?? "",
        Type = item.Kind ?? "text",
        Transform = NormalizeTransform(item.Transform),
        Source = item.Source ?? "",
        SlideId = item.SlideId,
        RequestId = item.RequestId ?? "",
        Content = item.Content ?? "",
        Title = item.Title ?? "",
        Author = item.Author ?? "",
        Dynasty = item.Dynasty ?? "",
        Latex = item.Latex ?? "",
        FontFamily = item.FontFamily ?? "Microsoft YaHei UI",
        FontSize = item.FontSize,
        Color = item.Color ?? "#FF202024",
        LineHeight = item.LineHeight,
        Align = item.Align ?? "left",
        Columns = item.Columns,
        Left = item.Left,
        Top = item.Top,
        Width = item.Width,
        Height = item.Height,
        LayerOrder = item.LayerOrder,
        Version = item.Version,
        GroupId = item.GroupId ?? "",
    };

    private static IReadOnlyList<string> SplitLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<string>();
        return content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>只接受 6 元且全部有限值的变换；其余（含旧载荷缺失）一律归一为 null。</summary>
    private static float[] NormalizeTransform(float[] values)
    {
        if (values is not { Length: 6 } || !values.All(float.IsFinite)) return null;
        return values.ToArray();
    }

    private sealed class PayloadDto
    {
        public string ObjectId { get; set; } = "";
        public string Action { get; set; } = "";
        public int Schema { get; set; } = AiObjectPayloadCodec.Schema;
        public int PageIndex { get; set; }
        public int BoardId { get; set; }
        public int? SlideId { get; set; }
        public int? AnimationStep { get; set; }
        public string SessionId { get; set; } = "";
        public string Source { get; set; } = "";
        public string RequestId { get; set; } = "";
        public List<ImageObjectDto> Objects { get; set; } = new();
        public List<NativeObjectDto> NativeObjects { get; set; } = new();
    }

    private sealed class ImageObjectDto
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public float[] Transform { get; set; }
        public string Source { get; set; } = "";
        public int? SlideId { get; set; }
        public string RequestId { get; set; } = "";
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public long? LayerOrder { get; set; }
        public string Latex { get; set; } = "";
        public string Text { get; set; } = "";
        public List<string> Lines { get; set; } = new();
        public string RenderPngBase64 { get; set; } = "";
        public bool RenderMissing { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private sealed class NativeObjectDto
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "text";
        public float[] Transform { get; set; }
        public string Source { get; set; } = "";
        public int? SlideId { get; set; }
        public string RequestId { get; set; } = "";
        public string Content { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Dynasty { get; set; } = "";
        public string Latex { get; set; } = "";
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public double FontSize { get; set; }
        public string Color { get; set; } = "#FF202024";
        public double LineHeight { get; set; }
        public string Align { get; set; } = "left";
        public int Columns { get; set; } = 1;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public long? LayerOrder { get; set; }
        public string GroupId { get; set; } = "";

        /// <summary>对象结构版本；旧载荷缺字段时按 1 处理。</summary>
        public int Version { get; set; } = 1;
    }
}