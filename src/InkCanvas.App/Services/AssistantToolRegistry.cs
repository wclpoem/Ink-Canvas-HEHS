using System.Text.Json;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;

namespace InkCanvas.App.Services;

public sealed class AssistantToolRegistry : IAssistantToolRegistry
{
    private sealed record ToolEntry(AssistantToolDefinition Definition, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> Execute,
        CancellationTokenSource Lifetime);
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);
    private sealed record ContextEntry(Func<CancellationToken, Task<string>> Read, CancellationTokenSource Lifetime);
    private readonly Dictionary<string, ContextEntry> _contexts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    public IReadOnlyList<AssistantToolDefinition> Definitions
    { get { lock (_gate) return _tools.Values.Select(tool => tool.Definition).OrderBy(tool => tool.Owner).ThenBy(tool => tool.Name).ToArray(); } }
    public event Action Changed;

    public IDisposable Register(AssistantToolDefinition definition, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> execute)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 64
            || definition.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("工具名称须为 1–64 个字母、数字、下划线或连字符。");
        using var schema = JsonDocument.Parse(definition.Parameters);
        if (schema.RootElement.GetProperty("type").GetString() != "object") throw new ArgumentException("工具参数需要对象类型的 JSON Schema。");
        var entry = new ToolEntry(definition, execute, new());
        lock (_gate) if (!_tools.TryAdd(definition.Name, entry)) throw new InvalidOperationException("工具名称已注册。" + definition.Name);
        Changed?.Invoke();
        return new Registration(() =>
        {
            lock (_gate) _tools.Remove(definition.Name);
            entry.Lifetime.Cancel();
            Changed?.Invoke();
        });
    }

    public IDisposable RegisterContext(string owner, Func<CancellationToken, Task<string>> read)
    {
        var entry = new ContextEntry(read, new());
        lock (_gate) if (!_contexts.TryAdd(owner, entry)) throw new InvalidOperationException("插件上下文已注册。" + owner);
        return new Registration(() => { lock (_gate) _contexts.Remove(owner); entry.Lifetime.Cancel(); });
    }

    public async Task<IReadOnlyList<AssistantContextSection>> ReadContextAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, ContextEntry>[] contexts;
        lock (_gate) contexts = _contexts.ToArray();
        var result = new List<AssistantContextSection>();
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Value.Lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string text = await OnDispatcherAsync(async () =>
                {
                    lock (_gate) if (!_contexts.TryGetValue(context.Key, out var current) || current != context.Value) return "";
                    return await context.Value.Read(timeout.Token);
                }).WaitAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(new(context.Key, text));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { result.Add(new(context.Key, "插件上下文读取超时或插件已停用。")); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { result.Add(new(context.Key, "上下文暂不可用。" + exception.Message)); }
        }
        return result;
    }

    public Task<AssistantToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        => OnDispatcherAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolEntry tool;
            lock (_gate) if (!_tools.TryGetValue(name, out tool)) throw new InvalidOperationException("工具当前不可用。" + name);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tool.Lifetime.Token);
            return await tool.Execute(arguments, lifetime.Token);
        });

    private Task<T> OnDispatcherAsync<T>(Func<Task<T>> execute)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                result.TrySetResult(await execute());
            }
            catch (OperationCanceledException) { result.TrySetCanceled(); }
            catch (Exception exception) { result.TrySetException(exception); }
        })) result.TrySetException(new ObjectDisposedException(nameof(AssistantToolRegistry)));
        return result.Task;
    }

    private sealed class Registration(Action release) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
