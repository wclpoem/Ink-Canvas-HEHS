using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board.AiObjects;

/// <summary>
/// Native, editable formula object. The LaTeX source is the persisted truth; display is laid out
/// from MathML into vector boxes and drawn with a canvas drawing session, never rendered to PNG.
/// </summary>
public sealed class BoardFormulaObject : ISelectionElement, IGroupedSelectionElement, IBoardFormulaElement, IBoardObject
{
    private BoardService _board;
    private InkController _page;
    private int _restoreIndex = -1;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Type { get; } = "formula";

    public string Latex { get; set; } = "";

    public string FontFamily { get; set; } = "Cambria Math";

    public double FontSize { get; set; } = 30;

    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Color { get; set; } = "#FF202024";

    public float Left { get; set; }

    public float Top { get; set; }

    public float Width { get; set; } = 200;

    public float Height { get; set; } = 80;

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

    BoardObjectType IBoardObject.Type => BoardObjectType.Formula;

    string IBoardObject.Content => Latex ?? "";

    BoardObjectStyle IBoardObject.Style => new(
        FontFamily: FontFamily ?? "",
        FontSize: FontSize,
        Color: Color ?? "");

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

    public bool IsPresent => _board is not null && _board.GetFormulaIndex(_page, this) >= 0;

    internal void Attach(BoardService board, InkController page)
    {
        _board = board;
        _page = page;
    }

    public BoardFormulaObject Copy(bool preserveId = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        Latex = Latex,
        FontFamily = FontFamily,
        FontSize = FontSize,
        Color = Color,
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
        if (!present && _board.GetFormulaIndex(_page, this) is var index && index >= 0) _restoreIndex = index;
        _board.SetFormulaPresent(_page, this, present, _restoreIndex);
    }

    private void Refresh() => _board?.NotifyFormulaPlacement(this);

    internal void CopyStateFrom(BoardFormulaObject snapshot)
    {
        Latex = snapshot.Latex;
        FontFamily = snapshot.FontFamily;
        FontSize = snapshot.FontSize;
        Color = snapshot.Color;
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