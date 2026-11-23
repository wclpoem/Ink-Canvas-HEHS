using System.Text.Json;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.AnalyticGeometry;

internal sealed partial class GeometryService
{
    private readonly SemaphoreSlim _assistantGate = new(1, 1);
    internal void RegisterAssistant(IModuleContext context)
    {
        const string owner = "inkcanvas.analytic-geometry";
        AssistantPlugin.Context(context, owner, _ => Task.FromResult(_window is null ? "解析几何工作区未打开。"
            : JsonSerializer.Serialize(new { objects = _window.Document.State.Objects.Count, revision = _window.Document.Revision, status = _window.StatusText })));
        AssistantPlugin.Tool(context, new("geometry_read", "读取解析几何工作区的原始对象定义、数值和错误。", AssistantPlugin.EmptyParameters)
            { Owner = owner, ReadOnly = true }, (_, token) =>
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(new AssistantToolResult(State()));
        });
        AssistantPlugin.Tool(context, new("geometry_calculate", "使用插件的本地符号引擎计算表达式，如 Solve(x^2=2,x)、Derivative(sin(x),x)、Integral(x^2,x)。", """
            {"type":"object","properties":{"expression":{"type":"string","maxLength":4096}},"required":["expression"],"additionalProperties":false}
            """) { Owner = owner, ReadOnly = true }, async (args, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            string expression = args.GetProperty("expression").GetString();
            var result = await Task.Run(() => ComputerAlgebra.Calculate(expression, timeout.Token), timeout.Token);
            return new(JsonSerializer.Serialize(result));
        });
        AssistantPlugin.Tool(context, new("geometry_execute", "在原生解析几何工作区执行构造指令，复用插件的语法、依赖重算和撤销。例如 A=(1,2);B=(3,4);l=Line(A,B)。", """
            {"type":"object","properties":{"commands":{"type":"string","maxLength":16384}},"required":["commands"],"additionalProperties":false}
            """) { Owner = owner, ChangesTarget = true }, async (args, token) =>
        {
            await _assistantGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested(); Open();
                var window = _window ?? throw new InvalidOperationException("解析几何工作区未打开。");
                string commands = args.GetProperty("commands").GetString();
                if (string.IsNullOrWhiteSpace(commands) || commands.Length > 16384) throw new ArgumentException("构造指令为空或过长。");
                window.Execute(commands); await window.CalculateAsync().WaitAsync(token);
                if (!ReferenceEquals(window, _window)) throw new InvalidOperationException("解析几何工作区已关闭。");
                return new(State());
            }
            finally { _assistantGate.Release(); }
        });
        AssistantPlugin.Tool(context, new("geometry_insert", "将当前解析几何构造作为可编辑对象插入或更新到当前画布，操作可撤销。", AssistantPlugin.EmptyParameters)
            { Owner = owner }, async (_, token) =>
        {
            await _assistantGate.WaitAsync(token);
            try
            {
                var window = _window ?? throw new InvalidOperationException("请先创建解析几何构造。");
                await window.CalculateAsync().WaitAsync(token); token.ThrowIfCancellationRequested();
                bool inserted = ReferenceEquals(window, _window) && window.InsertShape();
                return new(inserted ? "已插入可编辑构造。" : "插入未完成，目标画布或构造已变化。") { IsError = !inserted };
            }
            finally { _assistantGate.Release(); }
        });
    }

    private string State() => _window is null ? "解析几何工作区未打开。" : JsonSerializer.Serialize(new
    {
        revision = _window.Document.Revision, objects = _window.Document.State.Objects.Select(item => new
        {
            item.Label, item.Definition, item.Visible,
            value = _window.Evaluation?.Values.GetValueOrDefault(item.Label)?.Display,
            error = _window.Evaluation?.Values.GetValueOrDefault(item.Label)?.Error,
        }),
    });
}
