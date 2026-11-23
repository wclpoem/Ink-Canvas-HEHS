namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 画布种类：AI 请求发起时所在的画布目标。用于判定「同一画板」，
/// 避免桌面覆层、课件覆层、文档与主/子板之间互相串写。
/// </summary>
public enum BoardCanvasKind
{
    /// <summary>桌面或窗口覆层画布。</summary>
    Overlay,

    /// <summary>课件（PPT/WPS）放映覆层画布。</summary>
    PptOverlay,

    /// <summary>PDF/文档阅读画布。</summary>
    Document,

    /// <summary>主展板。</summary>
    MainBoard,

    /// <summary>板中板（侧边子板）。</summary>
    SideBoard,
}

/// <summary>
/// 内容来源：区分用户墨迹与各类 AI 产出，供溯源标记与时光机回放使用。
/// </summary>
public enum ContentSource
{
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
}

/// <summary>
/// 统一课堂上下文快照：AI 请求发起时的画板、页面、课件与选区身份。
/// 纯数据、纯逻辑，无 UI/网络依赖；只依赖 Core。
/// </summary>
public sealed record ClassroomContext
{
    /// <summary>课堂会话标识，用于拒绝跨会话写回。</summary>
    public string SessionId { get; init; } = "";

    /// <summary>本次 AI 请求标识，贯穿截图/流/写回/日志。</summary>
    public string RequestId { get; init; } = "";

    /// <summary>同一会话内的请求代数；新请求自增，用于丢弃过期结果。</summary>
    public int Generation { get; init; }

    /// <summary>发起请求的画布种类。</summary>
    public BoardCanvasKind Kind { get; init; }

    /// <summary>画板标识："main"/"side"/"overlay"/"document"/"ppt"。</summary>
    public string BoardId { get; init; } = "";

    /// <summary>页面持久标识；Guid.Empty 表示该画板尚无稳定页标识（需回退比较页索引）。</summary>
    public Guid PageId { get; init; }

    /// <summary>页面在画板内的序号。</summary>
    public int PageIndex { get; init; }

    /// <summary>课件演示标识（文件名或放映窗口标识）；非课件场景为空。</summary>
    public string PresentationKey { get; init; }

    /// <summary>课件幻灯片标识；非课件场景为空。</summary>
    public int? SlideId { get; init; }

    /// <summary>课件幻灯片序号；非课件场景为空。</summary>
    public int? SlideIndex { get; init; }

    /// <summary>幻灯片内动画阶段；非课件场景为空。</summary>
    public int? AnimationStep { get; init; }

    /// <summary>选区标识，用于判定请求发起后选区是否变化。</summary>
    public string SelectionId { get; init; } = "";

    /// <summary>参与请求的墨迹 Id（InkStroke.Id 为 Guid，这里取其字符串形式）。</summary>
    public IReadOnlyList<string> StrokeIds { get; init; } = Array.Empty<string>();

    /// <summary>选区所属分组标识。</summary>
    public string GroupId { get; init; } = "";

    /// <summary>参与请求的板书对象 Id（文本/公式/图片/媒体卡片）。</summary>
    public IReadOnlyList<string> ObjectIds { get; init; } = Array.Empty<string>();

    /// <summary>内容来源（用户墨迹或各类 AI 产出）。</summary>
    public ContentSource Source { get; init; }

    /// <summary>快照创建时间（Unix 毫秒）。</summary>
    public long CreatedAtMs { get; init; }

    /// <summary>是否存在稳定页面标识；便于 JSON 与测试判断。</summary>
    public bool HasPageIdentity => PageId != Guid.Empty;

    /// <summary>
    /// 便捷复制：只覆盖显式传入的字段，null 表示保持原值。
    /// 切换画板/页面时用 <paramref name="clearSlide"/> 清空课件与动画身份，
    /// 避免旧课件字段被带到新上下文。
    /// </summary>
    public ClassroomContext With(
        string sessionId = null,
        string requestId = null,
        int? generation = null,
        BoardCanvasKind? kind = null,
        string boardId = null,
        Guid? pageId = null,
        int? pageIndex = null,
        string presentationKey = null,
        int? slideId = null,
        int? slideIndex = null,
        int? animationStep = null,
        string selectionId = null,
        IReadOnlyList<string> strokeIds = null,
        string groupId = null,
        IReadOnlyList<string> objectIds = null,
        ContentSource? source = null,
        long? createdAtMs = null,
        bool clearSlide = false)
        => this with
        {
            SessionId = sessionId ?? SessionId,
            RequestId = requestId ?? RequestId,
            Generation = generation ?? Generation,
            Kind = kind ?? Kind,
            BoardId = boardId ?? BoardId,
            PageId = pageId ?? PageId,
            PageIndex = pageIndex ?? PageIndex,
            PresentationKey = clearSlide ? "" : presentationKey ?? PresentationKey,
            SlideId = clearSlide ? null : slideId ?? SlideId,
            SlideIndex = clearSlide ? null : slideIndex ?? SlideIndex,
            AnimationStep = clearSlide ? null : animationStep ?? AnimationStep,
            SelectionId = selectionId ?? SelectionId,
            StrokeIds = strokeIds ?? StrokeIds,
            GroupId = groupId ?? GroupId,
            ObjectIds = objectIds ?? ObjectIds,
            Source = source ?? Source,
            CreatedAtMs = createdAtMs ?? CreatedAtMs,
        };
}

/// <summary>
/// 上下文比较策略：只做纯比较，不依赖 Features/App/WinUI。
/// 显示（贴回前预览）只要求同一画板与同一页；写回还要求同一课件页、同一选区与同一请求代数。
/// </summary>
public static class ClassroomContextPolicy
{
    /// <summary>同一会话：双方均非 null 且 SessionId 序数相等。</summary>
    public static bool SameSession(ClassroomContext requested, ClassroomContext current)
        => requested is not null
            && current is not null
            && string.Equals(Normalize(requested.SessionId), Normalize(current.SessionId), StringComparison.Ordinal);

    /// <summary>同一画板：画布种类相同且 BoardId 序数相等。</summary>
    public static bool SameBoard(ClassroomContext requested, ClassroomContext current)
        => requested is not null
            && current is not null
            && requested.Kind == current.Kind
            && string.Equals(Normalize(requested.BoardId), Normalize(current.BoardId), StringComparison.Ordinal);

    /// <summary>同一页：先判同一画板；双方都有稳定 PageId 时比较 PageId，否则回退比较 PageIndex。</summary>
    public static bool SamePage(ClassroomContext requested, ClassroomContext current)
    {
        if (!SameBoard(requested, current)) return false;
        if (requested.PageId != Guid.Empty && current.PageId != Guid.Empty)
            return requested.PageId == current.PageId;
        return requested.PageIndex == current.PageIndex;
    }

    /// <summary>
    /// 同一课件页：双方都没有幻灯片身份时视为相同；否则
    /// PresentationKey、SlideId 与 AnimationStep 必须全部相等（SlideIndex 仅作展示，不参与比较）。
    /// </summary>
    public static bool SameSlide(ClassroomContext requested, ClassroomContext current)
    {
        if (requested is null || current is null) return false;
        if (!requested.SlideId.HasValue && !current.SlideId.HasValue) return true;

        return string.Equals(Normalize(requested.PresentationKey), Normalize(current.PresentationKey), StringComparison.Ordinal)
            && requested.SlideId == current.SlideId
            && requested.AnimationStep == current.AnimationStep;
    }

    /// <summary>
    /// 同一选区：SelectionId/GroupId/StrokeIds/ObjectIds 都是可选的，
    /// 某一维度双方都未指定时视为不约束；双方都指定时按值（集合忽略顺序）相等。
    /// </summary>
    public static bool SameSelection(ClassroomContext requested, ClassroomContext current)
    {
        if (requested is null || current is null) return false;

        return OptionalEqual(requested.SelectionId, current.SelectionId)
            && OptionalEqual(requested.GroupId, current.GroupId)
            && OptionalSetEquals(requested.StrokeIds, current.StrokeIds)
            && OptionalSetEquals(requested.ObjectIds, current.ObjectIds);
    }

    /// <summary>是否仍可向用户显示该结果：仅要求同一画板与同一页。</summary>
    public static bool IsCurrentForDisplay(ClassroomContext requested, ClassroomContext current)
        => SameBoard(requested, current) && SamePage(requested, current);

    /// <summary>
    /// 是否允许写回：同一会话、画板与页（必须有稳定 PageId），且课件页、选区与请求代数全部一致。
    /// 仅页索引相同、缺 PageId 的旧上下文不满足写回条件。
    /// </summary>
    public static bool CanWriteBack(ClassroomContext requested, ClassroomContext current)
        => SameSession(requested, current)
            && IsCurrentForDisplay(requested, current)
            && requested.HasPageIdentity
            && current.HasPageIdentity
            && SameSlide(requested, current)
            && SameSelection(requested, current)
            && requested.Generation == current.Generation;

    /// <summary>
    /// 面向教师的中文过期提示：按画板 → 页面 → 课件页 → 选区 → 请求代数的优先级给出首个原因。
    /// </summary>
    public static string StaleNotice(ClassroomContext requested, ClassroomContext current)
    {
        if (requested is null || current is null) return ExpiredNotice;

        if (!SameBoard(requested, current))
            return "发起 AI 请求的画板已关闭或已切换到其他画板，本次结果未写回；请回到原画板重新框选后再试。";

        if (!SamePage(requested, current))
            return "发起 AI 请求的页面已切换或被删除，本次结果未写回；请回到原页重新框选后再试。";

        if (!SameSlide(requested, current))
            return "课件已翻页或切换放映，本次结果未写回；请回到原幻灯片重新框选后再试。";

        if (!SameSelection(requested, current))
            return "发起请求时的选区已变化，本次结果未写回；请重新框选后再试。";

        if (requested.Generation != current.Generation)
            return "AI 请求已过期（已被新的请求替代），本次结果未写回；请查看最新结果。";

        return ExpiredNotice;
    }

    /// <summary>
    /// 供日志使用的紧凑描述（统一 "[AI]" 前缀），形如
    /// <c>[AI] context kind=MainBoard,board=main,pageId=&lt;guid 或 none&gt;,page=2,slide=3,anim=0,selection=none,source=AiWrite,gen=7,request=&lt;ReqId&gt;</c>。
    /// 只输出身份与计数，不输出指令、文本等敏感内容。
    /// </summary>
    public static string Describe(ClassroomContext context)
    {
        if (context is null) return "[AI] context=none";

        string pageId = context.PageId == Guid.Empty ? "none" : context.PageId.ToString();
        string slide = context.SlideIndex.HasValue ? context.SlideIndex.Value.ToString() : "none";
        string animation = context.AnimationStep.HasValue ? context.AnimationStep.Value.ToString() : "none";
        string selection = string.IsNullOrEmpty(context.SelectionId) ? "none" : context.SelectionId;
        return $"[AI] context kind={context.Kind},board={Normalize(context.BoardId)},pageId={pageId},"
            + $"page={context.PageIndex},slide={slide},anim={animation},selection={selection},"
            + $"strokes={context.StrokeIds?.Count ?? 0},objects={context.ObjectIds?.Count ?? 0},"
            + $"source={context.Source},gen={context.Generation},request={Normalize(context.RequestId)}";
    }

    private const string ExpiredNotice = "AI 请求已过期，本次结果未写回；请重新框选后再试。";

    private static string Normalize(string value) => value ?? "";

    private static bool OptionalEqual(string requested, string current)
    {
        string left = Normalize(requested);
        string right = Normalize(current);
        if (left.Length == 0 || right.Length == 0) return true;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool OptionalSetEquals(IReadOnlyList<string> requested, IReadOnlyList<string> current)
    {
        var leftSet = ToSortedSet(requested);
        var rightSet = ToSortedSet(current);
        if (leftSet.Count == 0 || rightSet.Count == 0) return true;
        if (leftSet.Count != rightSet.Count) return false;
        for (int i = 0; i < leftSet.Count; i++)
        {
            if (!string.Equals(leftSet[i], rightSet[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static List<string> ToSortedSet(IReadOnlyList<string> values)
    {
        var items = new List<string>();
        if (values is null) return items;
        foreach (string value in values)
        {
            string text = Normalize(value);
            if (!items.Exists(item => string.Equals(item, text, StringComparison.Ordinal))) items.Add(text);
        }

        items.Sort(StringComparer.Ordinal);
        return items;
    }
}