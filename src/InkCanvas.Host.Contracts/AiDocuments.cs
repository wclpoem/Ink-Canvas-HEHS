using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public sealed record AiDocumentImage(string Path, int Page, string Role, int Width, int Height)
{
    public InkCanvas.Core.Ink.Rect? SourceRegion { get; init; }
}

/// <summary>Optional plugins supply PNG pages lazily. Native boards use their saved .icink path.</summary>
public sealed record AiDocumentRequest(string Path, string SourcePluginId = "inkcanvas.library")
{
    public Func<string, CancellationToken, IAsyncEnumerable<AiDocumentImage>> ExportImages { get; init; }
    public bool PreviewOnly { get; init; }
}

public sealed record AiDocumentResult(string OriginalPath, string Path, string Title, string Description, int Images, DateTimeOffset CompletedAtUtc);

/// <summary>Implemented and exported by the AI plugin. Calling plugins have no dependency on its implementation.</summary>
public interface IAiDocumentService
{
    string Status { get; }
    bool SupportsSuggestions => false;
    FrameworkElement CreateSettingsView();
    Task<AiDocumentResult> DescribeAsync(AiDocumentRequest request, CancellationToken token = default);
}

public interface IAiDocumentBridge
{
    bool Available { get; }
    bool SupportsSuggestions => false;
    void Completed(AiDocumentResult result) { }
    void Saved(AiDocumentRequest request);
    Task<AiDocumentResult> DescribeAsync(AiDocumentRequest request, CancellationToken token = default);
}

public interface IAiDocumentHost
{
    string DataDirectory { get; }
    event Action<AiDocumentRequest> DocumentSaved;
    void Completed(AiDocumentResult result);
}
