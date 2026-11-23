using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.ScreenTracking;

internal static class ScreenTrackingAssistantAdapter
{
    internal static void Register(IModuleContext context, IScreenTrackingHost host, ScreenAnnotationCoordinator coordinator)
    {
        const string owner = "inkcanvas.screen-tracking";
        string State() => JsonSerializer.Serialize(new { enabled = host.Settings.ScreenContentTracking, host.Status,
            host.BoardOpen, followScroll = host.Settings.FollowScroll, followZoom = host.Settings.FollowZoom, rememberPages = host.Settings.RememberPages });
        AssistantPlugin.Context(context, owner, _ => Task.FromResult(State()));
        AssistantPlugin.Tool(context, new("screen_tracking_state", "读取屏幕内容跟随的状态和现有设置。", AssistantPlugin.EmptyParameters)
            { Owner = owner, ReadOnly = true }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new AssistantToolResult(State())); });
        AssistantPlugin.Tool(context, new("screen_tracking_reanchor", "重新绑定当前屏幕批注位置，用于老师明确要求修复跟随定位。", AssistantPlugin.EmptyParameters)
            { Owner = owner }, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!host.Settings.ScreenContentTracking) throw new InvalidOperationException("屏幕内容跟随尚未开启。");
            if (host.BoardOpen) throw new InvalidOperationException("请先切回屏幕批注。");
            coordinator.Reanchor(); return Task.FromResult(new AssistantToolResult(State()));
        });
    }
}
