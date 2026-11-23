using System.Text.Json;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.CloudSync;

internal sealed partial class CloudSyncService
{
    internal void RegisterAssistant(IModuleContext context)
    {
        const string owner = "inkcanvas.cloud-sync";
        string State() => JsonSerializer.Serialize(new { signedIn = !string.IsNullOrWhiteSpace(_state.UserId), enabled = _state.Enabled,
            preferences = _state.SyncPreferences, lastSync = _state.LastSyncUtc, pending = _state.Pending.Count, status = _status });
        AssistantPlugin.Context(context, owner, _ => Task.FromResult(State()));
        AssistantPlugin.Tool(context, new("cloud_sync_state", "读取云同步状态、待备份数量与最近同步时间。", AssistantPlugin.EmptyParameters)
            { Owner = owner, ReadOnly = true }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new AssistantToolResult(State())); });
        AssistantPlugin.Tool(context, new("cloud_sync_run", "按老师已配置的云同步设置执行一次同步，返回实际状态。", AssistantPlugin.EmptyParameters)
            { Owner = owner }, async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(_state.UserId)) throw new InvalidOperationException("请先在云同步设置中登录。");
            if (!_state.Enabled && !_state.SyncPreferences) throw new InvalidOperationException("请先在云同步设置中启用所需的同步内容。");
            await SyncForAssistantAsync(token); return new(State());
        });
    }
}
