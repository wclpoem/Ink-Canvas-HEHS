using System.Text.Json;
using InkCanvas.Extensibility;

namespace InkCanvas.Host.Contracts;

public sealed record AssistantToolDefinition(string Name, string Description, string Parameters)
{
    public string Owner { get; init; } = "host";
    public bool ReadOnly { get; init; }
    public bool ChangesTarget { get; init; }
}
public sealed record AssistantToolResult(string Text, byte[] Image = null)
{
    public bool IsError { get; init; }
    public IReadOnlyList<AssistantToolImage> Images { get; init; } = Array.Empty<AssistantToolImage>();
}
public sealed record AssistantToolImage(byte[] Data, string MimeType = "image/png");
public sealed record AssistantContextSection(string Owner, string Text);

/// <summary>Native capabilities available to the assistant. Plugins can contribute tools through this registry.</summary>
public interface IAssistantToolRegistry
{
    IReadOnlyList<AssistantToolDefinition> Definitions { get; }
    event Action Changed;
    IDisposable Register(AssistantToolDefinition definition, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> execute);
    IDisposable RegisterContext(string owner, Func<CancellationToken, Task<string>> read);
    Task<IReadOnlyList<AssistantContextSection>> ReadContextAsync(CancellationToken cancellationToken);
    Task<AssistantToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>Optional integration owned by the contributing module, available with or without the AI module.</summary>
public static class AssistantPlugin
{
    public const string EmptyParameters = """{"type":"object","properties":{},"additionalProperties":false}""";

    public static IAssistantToolRegistry Registry(IModuleContext context)
    {
        try { return context.GetService<IAssistantToolRegistry>(); }
        catch (InvalidOperationException) { return null; }
    }

    public static void Tool(IModuleContext context, AssistantToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<AssistantToolResult>> execute)
    {
        var registry = Registry(context);
        if (registry is not null) context.RegisterCleanup(registry.Register(definition, execute).Dispose);
    }

    public static void Context(IModuleContext context, string owner, Func<CancellationToken, Task<string>> read)
    {
        var registry = Registry(context);
        if (registry is not null) context.RegisterCleanup(registry.RegisterContext(owner, read).Dispose);
    }
}
