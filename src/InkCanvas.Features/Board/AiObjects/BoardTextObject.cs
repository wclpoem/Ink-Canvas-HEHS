using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board.AiObjects;

/// <summary>
/// Native board text/word-list/poem/summary object. Rendered as vector text through
/// <c>CanvasTextLayout</c>; it is never rasterised into an image card.
/// Geometry and transform mirror <see cref="BoardImageAsset"/> so selection, move, resize,
/// rotation, duplicate and undo share the existing image path.
/// </summary>
public sealed class BoardTextObject : ISelectionElement, IGroupedSelectionElement, IBoardTextElement, IBoardObject
{
    private BoardService _board;
    private InkController _page;
    private int _restoreIndex = -1;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>text | wordList | poem | summary.</summary>
    public string Type { get; set; } = "text";

    public string Content { get; set; } = "";

    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    public string Dynasty { get; set; } = "";

    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    public double FontSize { get; set; } = 32;

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Color { get; set; } = "#FF202024";

    public double LineHeight { get; set; } = 1.4;

    /// <summary>left | center | right | justify.</summary>
    public string Align { get; set; } = "left";

    /// <summary>词语列表多列排布；1-3 列，默认 1。</summary>
    public int Columns { get; set; } = 1;

    public float Left { get; set; }

    public float Top { get; set; }

    public float Width { get; set; } = 360;

    public float Height { get; set; } = 200;

    public long? LayerOrder { get; set; }

    public string GroupId { get; set; } = "";

    public string Source { get; set; } = "ai";

    public string Instruction { get; set; } = "";

    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <inheritdoc cref="BoardImageAsset.Transform"/>
    public float[] TransformMatrix { get; set; }

    public string SelectionGroupId => GroupId ?? "";

    /// <summary>对象结构版本；旧文件缺字段时按 1 读取。</summary>
    public int Version { get; set; } = BoardObjectProvenance.DefaultVersion;

    Guid IBoardObject.Id => Id;

    BoardObjectType IBoardObject.Type => BoardObjectType.Text;

    string IBoardObject.Content => Content ?? "";

    BoardObjectStyle IBoardObject.Style => new(
        FontFamily: FontFamily ?? "",
        FontSize: FontSize,
        Color: Color ?? "",
        LineHeight: LineHeight,
        Align: Align ?? "",
        Columns: Columns);

    Rect IBoardObject.Rect => new(Left, Top, Left + Width, Top + Height);

    Matrix3x2 IBoardObject.RectTransform => BoardElementMath.Read(TransformMatrix);

    string IBoardObject.GroupId => GroupId ?? "";

    ObjectProvenance IBoardObject.Provenance
        => BoardObjectProvenance.FromLegacy(Source, Instruction, CreatedAt, GroupId);

    int IBoardObject.Version => Version;

    object IBoardObject.Source => this;

    public Rect Bounds => BoardElementMath.TransformedBounds(Left, Top, Width, Height, TransformMatrix);

    public SelectionFrame Frame => new(
        new Rect(Left, Top, Left + Width, Top + Height), BoardElementMath.Read(TransformMatrix));

    public bool IsPresent => _board is not null && _board.GetTextIndex(_page, this) >= 0;

    internal void Attach(BoardService board, InkController page)
    {
        _board = board;
        _page = page;
    }

    public BoardTextObject Copy(bool preserveId = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        Type = Type,
        Content = Content,
        Title = Title,
        Author = Author,
        Dynasty = Dynasty,
        FontFamily = FontFamily,
        FontSize = FontSize,
        Color = Color,
        LineHeight = LineHeight,
        Align = Align,
        Columns = Columns,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        LayerOrder = LayerOrder,
        GroupId = GroupId,
        Source = Source,
        Instruction = Instruction,
        CreatedAt = CreatedAt,
        Version = Version,
        TransformMatrix = TransformMatrix?.ToArray(),
    };

    private sealed record Geometry(float Left, float Top, float Width, float Height, float[] Transform);

    public object Capture() => new Geometry(Left, Top, Width, Height, TransformMatrix?.ToArray());

    public void Restore(object snapshot)
    {
        var state = (Geometry)snapshot;
        Left = state.Left;
        Top = state.Top;
        Width = state.Width;
        Height = state.Height;
        TransformMatrix = state.Transform?.ToArray();
        Refresh();
    }

    public void Transform(Matrix3x2 delta)
    {
        TransformMatrix = BoardElementMath.Append(TransformMatrix, delta);
        Refresh();
    }

    public void TransformFrom(object snapshot, Matrix3x2 delta)
    {
        var state = (Geometry)snapshot;
        TransformMatrix = BoardElementMath.Append(state.Transform, delta);
        Refresh();
    }

    public ISelectionElement Duplicate()
    {
        var copy = Copy(preserveId: false);
        if (_board is not null) copy.Attach(_board, _page);
        return copy;
    }

    public void SetPresent(bool present)
    {
        if (_board is null) return;
        if (!present && _board.GetTextIndex(_page, this) is var index && index >= 0) _restoreIndex = index;
        _board.SetTextPresent(_page, this, present, _restoreIndex);
    }

    private void Refresh() => _board?.NotifyTextPlacement(this);

    /// <summary>Copy every mutable field from <paramref name="snapshot"/>; keeps this instance identity.</summary>
    internal void CopyStateFrom(BoardTextObject snapshot)
    {
        Type = snapshot.Type;
        Content = snapshot.Content;
        Title = snapshot.Title;
        Author = snapshot.Author;
        Dynasty = snapshot.Dynasty;
        FontFamily = snapshot.FontFamily;
        FontSize = snapshot.FontSize;
        Color = snapshot.Color;
        LineHeight = snapshot.LineHeight;
        Align = snapshot.Align;
        Columns = snapshot.Columns;
        Left = snapshot.Left;
        Top = snapshot.Top;
        Width = snapshot.Width;
        Height = snapshot.Height;
        LayerOrder = snapshot.LayerOrder;
        GroupId = snapshot.GroupId;
        Source = snapshot.Source;
        Instruction = snapshot.Instruction;
        CreatedAt = snapshot.CreatedAt;
        Version = snapshot.Version;
        TransformMatrix = snapshot.TransformMatrix?.ToArray();
    }
}