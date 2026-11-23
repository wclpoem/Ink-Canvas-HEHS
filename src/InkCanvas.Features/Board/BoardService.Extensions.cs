namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    // The host preserves plugin payloads as opaque bytes. Only their owning plugin interprets them.
    private Dictionary<string, byte[]> _extensions = new(StringComparer.Ordinal);
    public Guid DocumentId { get; private set; } = Guid.NewGuid();
    public event Action ExtensionsChanged;
    public event Action DocumentChanged;
    public event Action Snapshotting;
    public void FlushExtensions() => Snapshotting?.Invoke();

    public byte[] ReadExtension(string id) => _extensions.TryGetValue(id, out var data) ? data.ToArray() : null;

    public void WriteExtension(string id, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
            throw new ArgumentException("插件存储标识无效。", nameof(id));
        if (data is null) _extensions.Remove(id);
        else _extensions[id] = data.ToArray();
        ExtensionsChanged?.Invoke();
    }

    private void ReplaceExtensions(Dictionary<string, byte[]> extensions)
    {
        _extensions = extensions ?? new(StringComparer.Ordinal);
        DocumentId = Guid.NewGuid();
        ExtensionsChanged?.Invoke();
        DocumentChanged?.Invoke();
    }
}
