using System.Security.Cryptography;
using System.Text;

namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 统一课堂上下文的纯工厂：确定性页面标识与上下文归一化。
/// 不依赖 UI/网络，只做字符串与 Guid 运算，便于单元测试。
/// </summary>
public static class ClassroomContextFactory
{
    /// <summary>
    /// 由课件演示标识与幻灯片 Id 生成稳定的页面 Guid（SHA256 前 16 字节）。
    /// 演示标识为空或幻灯片 Id 非正数时返回 <see cref="Guid.Empty"/>，由调用方回退画板页标识。
    /// </summary>
    public static Guid DeterministicPageId(string presentationKey, int slideId)
    {
        if (string.IsNullOrWhiteSpace(presentationKey) || slideId <= 0) return Guid.Empty;

        byte[] input = Encoding.UTF8.GetBytes(presentationKey.Trim() + "|" + slideId);
        byte[] hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16).ToArray());
    }

    /// <summary>
    /// 归一化上下文：null 输入返回空实例；字符串字段空值归一为 ""，
    /// 页序号/代数/时间戳负值归零，Id 集合去空去重。
    /// 非课件画布（Overlay/PptOverlay 之外）一律清空课件与动画字段，避免旧课件身份串到别的画板。
    /// </summary>
    public static ClassroomContext Normalize(ClassroomContext context)
    {
        if (context is null) return new ClassroomContext();

        bool isPpt = context.Kind == BoardCanvasKind.PptOverlay;
        return context with
        {
            SessionId = CleanText(context.SessionId),
            RequestId = CleanText(context.RequestId),
            Generation = context.Generation < 0 ? 0 : context.Generation,
            BoardId = CleanText(context.BoardId),
            PageIndex = context.PageIndex < 0 ? 0 : context.PageIndex,
            PresentationKey = isPpt ? CleanText(context.PresentationKey) : "",
            SlideId = isPpt && context.SlideId is > 0 ? context.SlideId : null,
            SlideIndex = isPpt && context.SlideIndex is > 0 ? context.SlideIndex : null,
            AnimationStep = isPpt ? (context.AnimationStep is >= 0 ? context.AnimationStep : 0) : null,
            SelectionId = CleanText(context.SelectionId),
            StrokeIds = CleanIds(context.StrokeIds),
            GroupId = CleanText(context.GroupId),
            ObjectIds = CleanIds(context.ObjectIds),
            CreatedAtMs = context.CreatedAtMs < 0 ? 0 : context.CreatedAtMs,
        };
    }

    private static string CleanText(string value) => value is null ? "" : value.Trim();

    private static IReadOnlyList<string> CleanIds(IReadOnlyList<string> values)
    {
        var items = new List<string>();
        if (values is null) return items;
        foreach (string value in values)
        {
            string text = CleanText(value);
            if (text.Length == 0) continue;
            if (!items.Exists(item => string.Equals(item, text, StringComparison.Ordinal))) items.Add(text);
        }

        return items;
    }
}