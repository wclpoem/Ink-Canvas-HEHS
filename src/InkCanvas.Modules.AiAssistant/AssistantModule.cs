using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.AiAssistant.Providers;

namespace InkCanvas.Modules.AiAssistant;

public sealed class AssistantModule : IApplicationModule
{
    private AssistantService _service;
    private DocumentAiService _documents;
    private AiConnectionStore _connections;

    public void Start(IModuleContext context)
    {
        string connectionDirectory = Path.Combine(context.GetService<IAiDocumentHost>().DataDirectory, "Connections");
        try { _connections = new AiConnectionStore(connectionDirectory); }
        catch (Exception) { _connections = null; }
        _service = new AssistantService(context.GetService<IAiAssistantHost>(), context.GetService<ICanvasToolsHost>(), _connections,
            context.GetService<IAiDocumentHost>().DataDirectory);
        context.RegisterCleanup(_service.Dispose);
        context.Export<IAiAssistantService>(_service);
        context.Export<IAiConnectionSettings>(_service);
        _service.RegisterHistoryTool(context);
        _documents = new DocumentAiService(context.GetService<IAiAssistantHost>(), context.GetService<IAiDocumentHost>(), _connections);
        context.RegisterCleanup(_documents.Dispose);
        context.Export<IAiDocumentService>(_documents);
        context.RegisterCleanup(context.GetService<IModuleSettingsRegistry>().Register(new(
            "inkcanvas.ai-assistant.settings", "AI 助手", "\uEB3D",
            "AI API 端点 供应商 模型 OpenAI Responses Anthropic Google DeepSeek GLM TokenHub Qwen 火山 StepFun MiMo MiniMax Kimi OpenCode PackyCode gateway",
            builder =>
            {
                builder.AddSection("AI 助手服务与模型");
                builder.AddView("API 连接与模型", _service.CreateSettingsView, "供应商 API Key 搜索");
                builder.AddView("助手与工具调用", _service.CreateBehaviorSettingsView, "Agent 上下文 思考 教师 指令");
                builder.AddSection("AI 存档整理");
                builder.AddView("AI 存档命名与描述", _documents.CreateSettingsView, "多图 PNG 长页 高清 局部 模型");
            })).Dispose);
    }

    public void Stop()
    {
        _service?.Dispose();
        _service = null;
        _documents?.Dispose(); _documents = null;
    }
}
