using InkCanvas.Extensions;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private void RefreshExtensionsPage() => DispatcherQueue.TryEnqueue(() =>
    {
        _searchIndexed = false;
        if (!_closed && _workspace?.SelectedPage == "模块与插件") QueueRebuild();
        else if (!_closed) RefreshControls();
    });

    private void BuildExtensionsCategory()
    {
        BuildRepositoryExtensions();
        _content.Children.Add(InfoRow("原生功能插件提供 AI 助手、PDF 和解析几何等独立功能，安装于 Modules 目录，开关重启后生效。外部命令插件使用独立进程，可即时启用或停用。"));
        AddSection("原生功能插件");
        if (_services.Modules.Status.Count == 0) _content.Children.Add(InfoRow("模块状态在主工具栏启动后显示。"));
        foreach (var module in _services.Modules.Status)
        {
            if (module.State == ExtensionState.NotInstalled)
            {
                _content.Children.Add(ToggleRow(module.Name + " · 原生插件", () => false, _ => { }, enabled: false,
                    disabledReason: "尚未安装，可从上方仓库插件中下载，重启后生效。"));
                continue;
            }
            _content.Children.Add(ToggleRow(module.Name + " · 原生插件", () => !_services.ExtensionPreferences.DisabledModules.Contains(module.Id), enabled =>
            {
                bool previous = !_services.ExtensionPreferences.DisabledModules.Contains(module.Id);
                if (enabled) _services.ExtensionPreferences.DisabledModules.Remove(module.Id); else _services.ExtensionPreferences.DisabledModules.Add(module.Id);
                try { _services.ExtensionStore.Save(_services.ExtensionPreferences); ShowNotification("模块开关将在下次启动时生效"); }
                catch (Exception ex)
                {
                    if (previous) _services.ExtensionPreferences.DisabledModules.Remove(module.Id); else _services.ExtensionPreferences.DisabledModules.Add(module.Id);
                    ShowNotification("保存失败 " + ex.Message); QueueRebuild();
                }
            }, note: module.Id + " · " + module.Version + " · " + StateText(module.State) + (module.Error is null ? "" : "\n" + module.Error)));
        }
        AddSection("外部插件");
        _content.Children.Add(InfoRow("仓库插件安装后重启即可在此启用。也可将其他插件包放入用户配置目录的 Plugins 子目录。"));
        _content.Children.Add(ButtonRow("打开用户插件目录", "配置目录中的插件可与主体程序独立更新", () =>
        {
            try
            {
                string path = Path.Combine(_services.Store.Directory, "Plugins"); Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowNotification(ex.Message); }
        }));
        if (_services.Plugins.Status.Count == 0) _content.Children.Add(InfoRow("尚未安装外部插件。"));
        foreach (var plugin in _services.Plugins.Status)
        {
            _content.Children.Add(ToggleRow(plugin.Name + " · 插件", () => _services.ExtensionPreferences.EnabledPlugins.Contains(plugin.Id), enabled =>
            {
                _ = SetPluginEnabledAsync(plugin.Id, enabled);
            }, note: plugin.Id + " · " + plugin.Version + " · " + StateText(plugin.State) + (plugin.Error is null ? "" : "\n" + plugin.Error)));
        }
        var commands = _shell?.ExtensionCommands ?? _services.Plugins.Commands;
        if (commands.Any()) AddSection("插件功能");
        foreach (var command in commands)
            _content.Children.Add(Require(ButtonRow(command.Title, command.Description ?? command.PluginId, async () =>
            { if (_shell is not null) await _shell.ExecuteExtensionCommandAsync(command); }, actionLabel: "运行"),
                () => _shell is null ? "画布命令宿主未启动，请从主程序打开设置。" : null));
    }
    private async Task SetPluginEnabledAsync(string id, bool enabled)
    {
        bool previous = _services.ExtensionPreferences.EnabledPlugins.Contains(id);
        if (enabled) _services.ExtensionPreferences.EnabledPlugins.Add(id); else _services.ExtensionPreferences.EnabledPlugins.Remove(id);
        try
        {
            _services.ExtensionStore.Save(_services.ExtensionPreferences);
            await _services.Plugins.SetEnabledAsync(id, enabled);
        }
        catch (Exception ex)
        {
            if (previous) _services.ExtensionPreferences.EnabledPlugins.Add(id); else _services.ExtensionPreferences.EnabledPlugins.Remove(id);
            try { _services.ExtensionStore.Save(_services.ExtensionPreferences); } catch { }
            if (!_closed) ShowNotification("插件状态更新失败 " + ex.Message);
        }
        if (!_closed) QueueRebuild();
    }
    private static string StateText(ExtensionState state) => state switch
    {
        ExtensionState.Running => "运行中", ExtensionState.Disabled => "已停用", ExtensionState.Starting => "正在启动",
        ExtensionState.Failed => "加载或运行失败", ExtensionState.Blocked => "依赖不可用", ExtensionState.Stopped => "已停止", _ => "已安装",
    };
}
