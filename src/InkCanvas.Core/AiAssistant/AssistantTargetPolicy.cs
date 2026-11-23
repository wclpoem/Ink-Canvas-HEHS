using System.Runtime.CompilerServices;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// AI 请求发往的画布种类。
/// </summary>
public enum AssistantCanvasKind
{
    /// <summary>悬浮窗画布（无 BoardService 实例）。</summary>
    Overlay,

    /// <summary>主展板画布。</summary>
    MainBoard,

    /// <summary>板中板画布。</summary>
    SideBoard,

    /// <summary>文档（PDF/Word 等）工作区画布，写回落主展板。</summary>
    Document = 3,

    /// <summary>PowerPoint 放映覆层，写回落主展板。</summary>
    PptOverlay = 4,
}

/// <summary>
/// AI 请求发起时记录的目标画布快照：画布对象引用、画布种类、当前页对象引用、
/// 页索引与会话 id，用于异步响应回来后校验目标是否仍然有效。
/// </summary>
public sealed class AssistantTarget
{
    /// <summary>BoardService 实例；overlay 时为 null。</summary>
    public object Canvas { get; set; }

    /// <summary>目标页 InkController 实例；overlay 时为 null。</summary>
    public object Page { get; set; }

    /// <summary>目标画布种类。</summary>
    public AssistantCanvasKind Kind { get; set; }

    /// <summary>目标页索引。</summary>
    public int PageIndex { get; set; }

    /// <summary>发起请求时的会话 id。</summary>
    public string SessionId { get; set; } = "";
}

/// <summary>
/// 校验 AI 异步响应是否仍属于发起请求时的同一画布、同一页与会话。
/// 纯逻辑实现，画布与页仅以对象引用参与比较，不依赖 Features/App 层类型。
/// </summary>
public static class AssistantTargetPolicy
{
    /// <summary>
    /// 是否为同一画布：两者非 null、种类相同且画布对象引用相同
    /// （overlay 时双方 Canvas 均为 null 也算相同）。
    /// </summary>
    public static bool SameCanvas(AssistantTarget requested, AssistantTarget current)
    {
        if (requested is null || current is null)
        {
            return false;
        }

        if (requested.Kind != current.Kind)
        {
            return false;
        }

        return ReferenceEquals(requested.Canvas, current.Canvas);
    }

    /// <summary>
    /// 是否属于同一会话：同一画布且会话 id 序数相等（null 归一为 ""）。
    /// </summary>
    public static bool SameSession(AssistantTarget requested, AssistantTarget current)
    {
        if (!SameCanvas(requested, current))
        {
            return false;
        }

        return string.Equals(requested.SessionId ?? "", current.SessionId ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// 是否为同一页：页索引相同，且页对象引用相同
    /// （requested.Page 为 null 时要求 current.Page 也为 null）。
    /// 可检测"页被删除/文件重开后索引相同但页对象不同"的情况。
    /// </summary>
    public static bool SamePage(AssistantTarget requested, AssistantTarget current)
    {
        if (requested is null || current is null)
        {
            return false;
        }

        if (requested.PageIndex != current.PageIndex)
        {
            return false;
        }

        if (requested.Page is null)
        {
            return current.Page is null;
        }

        return ReferenceEquals(requested.Page, current.Page);
    }

    /// <summary>
    /// 目标是否仍然有效：同一会话且同一页。
    /// </summary>
    public static bool IsCurrent(AssistantTarget requested, AssistantTarget current)
        => SameSession(requested, current) && SamePage(requested, current);

    /// <summary>
    /// 是否允许把结果写回：与 <see cref="IsCurrent"/> 一致。
    /// </summary>
    public static bool CanWriteBack(AssistantTarget requested, AssistantTarget current)
        => IsCurrent(requested, current);

    /// <summary>
    /// 生成供日志使用的目标描述，例如
    /// "kind=SideBoard,page=2,pageRef=0x1A2B,canvasRef=0x2B3C,session=abc"；
    /// Page/Canvas 为 null 时对应引用输出 null。
    /// </summary>
    public static string Describe(AssistantTarget target)
    {
        if (target is null)
        {
            return "null";
        }

        return $"kind={target.Kind},page={target.PageIndex},pageRef={RefText(target.Page)},canvasRef={RefText(target.Canvas)},session={target.SessionId ?? ""}";
    }

    /// <summary>
    /// 生成过期提示，按画布、页、会话的优先级说明本次结果为什么没有写回。
    /// </summary>
    public static string StaleNotice(AssistantTarget requested, AssistantTarget current)
    {
        if (requested is null || current is null)
        {
            return "AI 请求已过期，本次结果未写回；请重新框选后再试。";
        }

        if (!SameCanvas(requested, current))
        {
            return "发起 AI 请求的画板已关闭或已切换到其他画板，本次结果未写回；请回到原画板重新框选后再试。";
        }

        if (!SamePage(requested, current))
        {
            return "发起 AI 请求的页面已切换或被删除，本次结果未写回；请回到原页重新框选后再试。";
        }

        return "AI 请求已过期（已被新的请求替代），本次结果未写回；请查看最新结果。";
    }

    private static string RefText(object value)
        => value is null ? "null" : "0x" + RuntimeHelpers.GetHashCode(value).ToString("X4");
}