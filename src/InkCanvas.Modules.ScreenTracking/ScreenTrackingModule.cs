using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.ScreenTracking;

public sealed class ScreenTrackingModule : IApplicationModule
{
    private ScreenAnnotationCoordinator _coordinator;
    public void Start(IModuleContext context)
    {
        var host = context.GetService<IScreenTrackingHost>();
        _coordinator = new ScreenAnnotationCoordinator(host, context.Protect);
        context.RegisterCleanup(_coordinator.Dispose);
        context.Export<IScreenTrackingService>(_coordinator);
        ScreenTrackingAssistantAdapter.Register(context, host, _coordinator);
        context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(new(
            "inkcanvas.screen-tracking.settings", "屏幕内容跟随", "\uF6B3",
            "实验功能 实验性设置 Word PowerPoint Edge 屏幕内容跟随 页面记忆",
            builder => BuildSettings(builder, host, context.Protect(_coordinator.Reanchor)))
            { Aliases = new[] { "实验功能", "实验性设置" } }).Dispose);
    }
    public void Stop() { _coordinator?.Dispose(); _coordinator = null; }

    private static void BuildSettings(IModuleSettingsPageBuilder builder, IScreenTrackingHost host, Action reanchor)
    {
        string Requirement() => host.Settings.ScreenContentTracking ? null : "请先启用屏幕内容跟随。";
        builder.AddInfo("屏幕内容跟随优先适配 Word、PowerPoint 与 Microsoft Edge。隐藏墨迹或切回鼠标模式后仍读取页面状态，返回批注时恢复对应位置。识别失败时暂停显示该页墨迹，可手动重新绑定。");
        builder.AddToggle("启用屏幕内容跟随（实验性）", () => host.Settings.ScreenContentTracking,
            value => host.Settings.ScreenContentTracking = value);
        builder.AddSection("跟随与页面记忆");
        builder.AddToggle("批注跟随正文滚动", () => host.Settings.FollowScroll, value => host.Settings.FollowScroll = value,
            unavailableReason: Requirement);
        builder.AddToggle("批注跟随页面缩放", () => host.Settings.FollowZoom, value => host.Settings.FollowZoom = value,
            unavailableReason: Requirement);
        builder.AddToggle("遮罩固定区域和悬浮界面", () => host.Settings.MaskFixedRegions, value => host.Settings.MaskFixedRegions = value,
            note: "正文区域外的墨迹始终裁切；页面内部的固定页眉和侧栏在确认滚动后加入遮罩。", unavailableReason: Requirement);
        builder.AddToggle("自动记忆不同文档与页面的墨迹", () => host.Settings.RememberPages, value => host.Settings.RememberPages = value,
            note: "本次运行期间按 Word 文档、网页地址和 PPT 幻灯片标识保存墨迹、图片及撤销记录。同一页面的工具栏、标题变化不会触发切换。", unavailableReason: Requirement);
        builder.AddToggle("保留页面中未变化区域的批注", () => host.Settings.PreserveUnchangedRegions,
            value => host.Settings.PreserveUnchangedRegions = value,
            note: "仅同步能够唯一匹配的相同段落区域。共享区域发生跨页同步时会重置目标页撤销记录，避免撤销恢复过期内容。",
            unavailableReason: () => Requirement() ?? (host.Settings.RememberPages ? null : "请先启用页面墨迹记忆。"));
        builder.AddSection("适配应用");
        builder.AddToggle("适配 Microsoft Word", () => host.Settings.Word, value => host.Settings.Word = value, unavailableReason: Requirement);
        builder.AddToggle("适配 Microsoft PowerPoint", () => host.Settings.PowerPoint, value => host.Settings.PowerPoint = value, unavailableReason: Requirement);
        builder.AddToggle("适配 Microsoft Edge", () => host.Settings.Edge, value => host.Settings.Edge = value, unavailableReason: Requirement);
        builder.AddStatus("屏幕跟随状态", () => host.Status);
        builder.AddButton("重新绑定当前批注位置", "定位中断或页面排版发生大幅变化时使用", reanchor, Requirement);
        builder.AddInfo("需要目标应用提供可访问性滚动与缩放信息；受保护窗口、自绘画布和部分网页可能无法识别。页面记忆保存在当前进程内，不会修改文档、注入网页脚本或上传屏幕内容。白板画布不参与此实验。");
    }
}
