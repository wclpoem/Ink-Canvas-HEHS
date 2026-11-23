using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>时光机录制插件入口：进程内受信模块，导出 <see cref="ITimeMachineService"/>。</summary>
public sealed class TimeMachineModule : IApplicationModule
{
    private TimeMachineService _service;

    public void Start(IModuleContext context)
    {
        _service = new TimeMachineService(context.GetService<ITimeMachineHost>(), context.Protect);
        context.RegisterCleanup(_service.Dispose);
        context.Export<ITimeMachineService>(_service);
        _service.RegisterAssistant(context);
    }

    public void Stop()
    {
        _service?.Dispose();
        _service = null;
    }
}
