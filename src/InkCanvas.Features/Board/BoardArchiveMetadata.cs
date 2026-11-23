namespace InkCanvas.Features.Board;

public sealed record BoardArchiveMetadata
{
    public string Product { get; init; } = "InkCanvas";
    public string Kind { get; init; } = "board";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Teacher { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset SavedAtUtc { get; init; }
    public int PageCount { get; init; }
    public bool Automatic { get; init; }
    public string ApplicationVersion { get; init; } = "";
}
