using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board;

/// <summary>Image/video selection adapter. Snapshot geometry only; embedded bytes are immutable and shared.</summary>
public sealed class BoardSelectionElement : IImageSelectionElement
{
    private readonly BoardService _board;
    private readonly InkController _page;
    internal InkController Page => _page;
    private int _restoreIndex = -1;
    public BoardImageAsset Image { get; }
    public BoardMediaAsset Media { get; }

    public BoardSelectionElement(BoardService board, BoardImageAsset image, InkController page = null)
    { _board = board; _page = page ?? board.Current; Image = image; }
    public BoardSelectionElement(BoardService board, BoardMediaAsset media, InkController page = null)
    { _board = board; _page = page ?? board.Current; Media = media; }

    public Guid Id => Image?.Id ?? Media.Id;
    public long? LayerOrder
    {
        get => Image is not null ? Image.LayerOrder : Media.LayerOrder;
        set { if (Image is not null) Image.LayerOrder = value; else Media.LayerOrder = value; }
    }
    public byte[] ImageData => Image?.Data;
    public string ImageContentType => Image?.ContentType;
    private string _annotationMetadata;
    private IReadOnlyList<IReadOnlyList<ShapeAnnotationPoint>> _annotations = Array.Empty<IReadOnlyList<ShapeAnnotationPoint>>();
    public IReadOnlyList<IReadOnlyList<ShapeAnnotationPoint>> ImageAnnotations
    {
        get
        {
            if (Image is null || string.Equals(_annotationMetadata, Image.Metadata, StringComparison.Ordinal))
                return _annotations;
            _annotationMetadata = Image.Metadata;
            var metadata = AiPasteMetadata.TryParse(_annotationMetadata);
            _annotations = metadata?.Kind == AiPasteKind.Shape && metadata.Annotations is { Count: > 0 }
                ? metadata.Annotations.Select(line => ShapeAnnotationGeometry.Normalize(line, Image.Width, Image.Height)).ToArray()
                : Array.Empty<IReadOnlyList<ShapeAnnotationPoint>>();
            return _annotations;
        }
    }
    public float Opacity
    {
        get => Image?.Opacity ?? 1;
        set
        {
            if (Image is null) return;
            Image.Opacity = Math.Clamp(value, 0, 1);
            Refresh();
        }
    }
    public bool IsPresent => PresenceIndex >= 0;
    private int PresenceIndex => Image is not null ? _board.GetImageIndex(_page, Image) : _board.GetMediaIndex(_page, Media);
    public Rect Bounds => Image is not null
        ? BoardElementMath.TransformedBounds(Image.Left, Image.Top, Image.Width, Image.Height, Image.Transform)
        : BoardElementMath.TransformedBounds(Media.Left, Media.Top, Media.Width, Media.Height, Media.Transform);
    public SelectionFrame Frame => Image is not null
        ? new SelectionFrame(new Rect(Image.Left, Image.Top, Image.Left + Image.Width, Image.Top + Image.Height), BoardElementMath.Read(Image.Transform))
        : new SelectionFrame(new Rect(Media.Left, Media.Top, Media.Left + Media.Width, Media.Top + Media.Height), BoardElementMath.Read(Media.Transform));
    private sealed record Geometry(float Left, float Top, float Width, float Height, float[] Transform, float Opacity = 1);
    public object Capture() => Image is not null
        ? new Geometry(Image.Left, Image.Top, Image.Width, Image.Height, Image.Transform?.ToArray(), Image.Opacity)
        : new Geometry(Media.Left, Media.Top, Media.Width, Media.Height, Media.Transform?.ToArray());

    public void Restore(object snapshot)
    {
        var state = (Geometry)snapshot;
        if (Image is not null)
        { Image.Left = state.Left; Image.Top = state.Top; Image.Width = state.Width; Image.Height = state.Height; Image.Transform = state.Transform?.ToArray(); Image.Opacity = state.Opacity; }
        else
        { Media.Left = state.Left; Media.Top = state.Top; Media.Width = state.Width; Media.Height = state.Height; Media.Transform = state.Transform?.ToArray(); }
        Refresh();
    }
    public void Transform(Matrix3x2 delta)
    {
        if (Image is not null) Image.Transform = BoardElementMath.Append(Image.Transform, delta);
        else Media.Transform = BoardElementMath.Append(Media.Transform, delta);
        Refresh();
    }
    public void TransformFrom(object snapshot, Matrix3x2 delta)
    {
        var state = (Geometry)snapshot;
        var transform = BoardElementMath.Append(state.Transform, delta);
        if (Image is not null) Image.Transform = transform;
        else Media.Transform = transform;
        Refresh();
    }
    public ISelectionElement Duplicate() => Image is not null
        ? new BoardSelectionElement(_board, Image.Copy(preserveId: false, copyData: false), _page)
        : new BoardSelectionElement(_board, new BoardMediaAsset { Name = Media.Name, Data = Media.Data, ContentType = Media.ContentType,
            Left = Media.Left, Top = Media.Top, Width = Media.Width, Height = Media.Height, Transform = Media.Transform?.ToArray(), LayerOrder = Media.LayerOrder,
            PosterData = Media.PosterData, NaturalWidth = Media.NaturalWidth, NaturalHeight = Media.NaturalHeight }, _page);
    private void Refresh()
    {
        if (Image is not null) _board.NotifyImagePlacement(Image);
        else _board.NotifyMediaPlacement(Media);
    }
    public void SetPresent(bool present)
    {
        if (!present && PresenceIndex is var index && index >= 0) _restoreIndex = index;
        if (Image is not null) _board.SetImagePresent(_page, Image, present, _restoreIndex);
        else _board.SetMediaPresent(_page, Media, present, _restoreIndex);
    }
}
