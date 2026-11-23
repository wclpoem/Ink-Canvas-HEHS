using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.CloudSync;

public sealed class CloudSyncModule : IApplicationModule
{
    private CloudSyncService _service;
    public void Start(IModuleContext context)
    {
        _service = new CloudSyncService(context.GetService<ICloudSyncHost>());
        context.RegisterCleanup(_service.Dispose);
        context.Export<ICloudSyncService>(_service);
        _service.RegisterAssistant(context);
        context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(new(
            "inkcanvas.cloud-sync.settings", "云同步", "\uE3C4", "云端 自动保存 备份 账号 登录 配置 同步 学校 部署",
            builder =>
            {
                builder.AddSection("账号与云端备份");
                builder.AddView("云同步账号与文件", _service.CreateSettingsView, "HTTPS 服务 邀请 登录 注销 白板 配置 冲突");
            })).Dispose);
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}
