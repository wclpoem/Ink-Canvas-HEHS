namespace InkCanvas.Core.Ui;

/// <summary>模态对话框宿主：决定 ContentDialog 使用哪个窗口的 XamlRoot。</summary>
public enum DialogHostSurface
{
    /// <summary>没有可用 XAML 宿主，调用方应放弃弹窗而不是静默降级到隐藏窗口。</summary>
    None,

    /// <summary>板中板窗口。</summary>
    SideBoard,

    /// <summary>主展板窗口。</summary>
    MainBoard,

    /// <summary>文档工作区窗口。</summary>
    Document,

    /// <summary>浮动工具栏窗口（覆层模式唯一带 XAML 的常驻窗口）。</summary>
    FloatingBar,

    /// <summary>AI 助教浮窗（仅最后兜底，不作为首选宿主）。</summary>
    Assistant,
}

/// <summary>
/// 对话框宿主选择输入：只描述“哪些窗口当前可见/可用”，不依赖 WinUI 类型，便于纯逻辑测试。
/// </summary>
public readonly record struct DialogHostContext(
    bool SideBoardVisible,
    bool SideBoardPreferred,
    bool MainBoardVisible,
    bool DocumentVisible,
    bool FloatingBarUsable,
    bool AssistantUsable);

/// <summary>
/// 对话框宿主选择策略：优先当前课堂目标窗口，其次文档、浮条，最后才用 AI 浮窗兜底。
/// 选择/激活/置顶由调用方执行；本类只做纯判定。
/// </summary>
public static class DialogHostPolicy
{
    /// <summary>
    /// 选择宿主：当前目标侧板 → 主展板 → 侧板 → 文档 → 浮条 → AI 浮窗 → 无。
    /// <paramref name="context"/>.SideBoardPreferred 表示 AI/当前画布目标就是侧板。
    /// </summary>
    public static DialogHostSurface Select(in DialogHostContext context)
    {
        if (context.SideBoardVisible && (context.SideBoardPreferred || !context.MainBoardVisible))
            return DialogHostSurface.SideBoard;

        if (context.MainBoardVisible) return DialogHostSurface.MainBoard;
        if (context.SideBoardVisible) return DialogHostSurface.SideBoard;
        if (context.DocumentVisible) return DialogHostSurface.Document;
        if (context.FloatingBarUsable) return DialogHostSurface.FloatingBar;
        if (context.AssistantUsable) return DialogHostSurface.Assistant;
        return DialogHostSurface.None;
    }

    /// <summary>
    /// 板/文档作为宿主时必须暂时收起 AI 浮窗：它通常是被宿主拥有的 topmost 子窗口，
    /// 会盖住宿主内的 ContentDialog；浮条/AI 自身作宿主时无需处理。
    /// </summary>
    public static bool RequiresAssistantSuspension(DialogHostSurface surface)
        => surface is DialogHostSurface.SideBoard
            or DialogHostSurface.MainBoard
            or DialogHostSurface.Document;

    /// <summary>该宿主是否可承载模态 ContentDialog（None 表示调用方应放弃）。</summary>
    public static bool IsUsable(DialogHostSurface surface)
        => surface != DialogHostSurface.None;
}