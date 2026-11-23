using InkCanvas.Core.Ink;
using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 单笔序列化为 JSON。字段布局与既有时光机/存档的 StrokeModel 完全一致
/// （Id/LayerOrder/Nib/NibRotation/InkWash/InkWashSeed/ShapeSource/ObjectFrame/
/// Color/Width/Highlighter/HighlighterAlpha/Taper/Shape/Points），
/// 因此 Core 录制与 Modules 时光机、.icink 存档可互换，不另造格式。
/// 旧 JSON 缺字段时按默认值读取（Id 重新生成、无变换、无形状来源）。
/// </summary>
public static class StrokeJsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StrokeModel
    {
        public Guid? Id { get; set; }
        public long? LayerOrder { get; set; }
        public PenNib Nib { get; set; }
        public float NibRotation { get; set; }
        public InkWashBrushOptions InkWash { get; set; } = InkWashBrushOptions.Default;
        public int InkWashSeed { get; set; }
        public ShapeSource ShapeSource { get; set; }
        public float[] ObjectFrame { get; set; }
        public uint Color { get; set; }
        public float Width { get; set; }
        public bool Highlighter { get; set; }
        public byte HighlighterAlpha { get; set; } = 96;
        public int Taper { get; set; }
        public bool Shape { get; set; }
        /// <summary>Rows of [x, y, pressure, width, timeMs, (tiltX, tiltY, hasPressure)].</summary>
        public List<float[]> Points { get; set; } = new();
    }

    public static string Serialize(InkStroke stroke)
    {
        if (stroke is null) return null;
        var model = new StrokeModel
        {
            Id = stroke.Id,
            LayerOrder = stroke.LayerOrder,
            Nib = stroke.Attributes.Nib,
            NibRotation = stroke.Attributes.NibRotation,
            InkWash = stroke.Attributes.InkWash,
            InkWashSeed = stroke.Attributes.InkWashSeed,
            ShapeSource = stroke.ShapeSource,
            ObjectFrame = stroke.ExportObjectFrame(),
            Color = stroke.Attributes.Color.PackedArgb,
            Width = stroke.Attributes.BaseWidth,
            Highlighter = stroke.Attributes.IsHighlighter,
            HighlighterAlpha = stroke.Attributes.HighlighterAlpha,
            Taper = (int)stroke.Attributes.TaperKind,
            Shape = stroke.IsRecognizedShape,
        };
        foreach (var p in stroke.Points)
            model.Points.Add(p.HasPressure || p.TiltX != 0 || p.TiltY != 0
                ? new[] { p.X, p.Y, p.Pressure, p.Width, p.TimeMs, p.TiltX, p.TiltY, p.HasPressure ? 1f : 0f }
                : new[] { p.X, p.Y, p.Pressure, p.Width, p.TimeMs });
        return JsonSerializer.Serialize(model, Options);
    }

    public static InkStroke Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var model = JsonSerializer.Deserialize<StrokeModel>(json, Options);
        if (model is null) return null;

        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(model.Color),
            BaseWidth = model.Width,
            IsHighlighter = model.Highlighter,
            HighlighterAlpha = model.HighlighterAlpha,
            TaperKind = Enum.IsDefined(typeof(TaperKind), model.Taper) ? (TaperKind)model.Taper : TaperKind.None,
            Nib = Enum.IsDefined(model.Nib) ? model.Nib : PenNib.Round,
            NibRotation = float.IsFinite(model.NibRotation) ? model.NibRotation : 0,
            InkWash = model.InkWash.Normalize(),
            InkWashSeed = model.InkWashSeed,
        };
        var stroke = new InkStroke(attributes)
        {
            Id = model.Id ?? Guid.NewGuid(),
            IsRecognizedShape = model.Shape,
            LayerOrder = model.LayerOrder,
            ShapeSource = model.ShapeSource,
        };
        stroke.RestorePoints((model.Points ?? new List<float[]>())
            .Where(row => row is { Length: >= 4 })
            .Select(row => new InkPoint(row[0], row[1], row[2], row[3], row.Length > 4 ? row[4] : 0f,
                row.Length > 7 && row[7] > 0, row.Length > 5 ? row[5] : 0, row.Length > 6 ? row[6] : 0)));
        stroke.Complete();
        stroke.ImportObjectFrame(model.ObjectFrame);
        return stroke;
    }
}