using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.Mcp;

public sealed class McpModule : IApplicationModule
{
    private McpService _service;
    public void Start(IModuleContext context)
    {
        _service = new McpService(context.GetService<IMcpHost>(), context.GetService<IAssistantToolRegistry>());
        context.RegisterCleanup(_service.Dispose);
        context.Export<IMcpService>(_service);
        context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(new(
            "inkcanvas.mcp.settings", "MCP 工具连接", "\uF1F9", "MCP stdio HTTP SSE 工具 资源 提示词 外部客户端",
            builder => builder.AddView("MCP 服务与应用工具", _service.CreateSettingsView, "MCP 配置 连接 刷新"))).Dispose);
        _service.Start();
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}
