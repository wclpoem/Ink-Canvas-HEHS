using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using CoreCodec = InkCanvas.Core.Classroom.StrokeJsonCodec;
using ModulesCodec = InkCanvas.Modules.TimeMachine.StrokeJsonCodec;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>
/// 阶段 2A：Core 录制与 Modules 时光机共用同一份笔画 JSON 字段布局。
/// 同一笔画两版序列化结果必须逐字符一致，且 ShapeSource/ObjectFrame/Id/LayerOrder/Tilt 往返不丢。
/// </summary>
public sealed class StrokeJsonCodecParityTests
{
    private static InkStroke SampleStroke()
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(0xFF1A2B3C),
            BaseWidth = 4f,
            IsHighlighter = false,
            TaperKind = TaperKind.Calligraphy,
            Nib = PenNib.Ellipse45,
            NibRotation = -18.5f,
            InkWash = new InkWashBrushOptions(0.35f, 60f, 0.5f),
            InkWashSeed = 9001,
        })
        {
            Id = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"),
            LayerOrder = 23,
            IsRecognizedShape = true,
            ShapeSource = new ShapeSource(
                Guid.Parse("12345678-1234-1234-1234-1234567890ab"), "geometry", "{\"shape\":\"line\"}"),
        };
        stroke.AddPoint(10f, 20f, 0.9f, 0f, hasPressure: true, tiltX: 0.25f, tiltY: -0.75f);
        stroke.AddPoint(30f, 40f, 0.6f, 12f);
        stroke.Complete();
        stroke.ObjectFrame = new SelectionFrame(new Rect(8, 9, 38, 49), Matrix3x2.CreateRotation(0.4f));
        return stroke;
    }

    [Fact]
    public void CoreAndModules_SerializeIdentically()
    {
        InkStroke stroke = SampleStroke();

        Assert.Equal(ModulesCodec.Serialize(stroke), CoreCodec.Serialize(stroke));
    }

    [Fact]
    public void CoreAndModules_RoundTripSameFields()
    {
        InkStroke stroke = SampleStroke();
        string json = CoreCodec.Serialize(stroke);

        InkStroke fromCore = CoreCodec.Deserialize(json);
        InkStroke fromModules = ModulesCodec.Deserialize(json);

        Assert.Equal(stroke.Id, fromCore!.Id);
        Assert.Equal(stroke.Id, fromModules!.Id);
        Assert.Equal(stroke.LayerOrder, fromCore.LayerOrder);
        Assert.Equal(stroke.LayerOrder, fromModules.LayerOrder);
        Assert.Equal(stroke.ShapeSource!.GroupId, fromModules.ShapeSource!.GroupId);
        Assert.Equal(stroke.ShapeSource.Definition, fromModules.ShapeSource.Definition);
        Assert.Equal(stroke.ExportObjectFrame(), fromCore.ExportObjectFrame());
        Assert.Equal(stroke.ExportObjectFrame(), fromModules.ExportObjectFrame());
        Assert.Equal(stroke.Attributes.Nib, fromModules.Attributes.Nib);
        Assert.Equal(stroke.Attributes.InkWash, fromModules.Attributes.InkWash);
        Assert.Equal(stroke.Points[0].TiltX, fromModules.Points[0].TiltX);
        Assert.True(fromModules.Points[0].HasPressure);
        Assert.False(fromModules.Points[1].HasPressure);
    }

    [Fact]
    public void LegacyJsonWithoutNewFields_IsReadableByBothCodecs()
    {
        const string legacy = "{\"Color\":4278190080,\"Width\":2.0,\"Highlighter\":false,"
            + "\"HighlighterAlpha\":96,\"Taper\":0,\"Shape\":false,"
            + "\"Points\":[[1.0,2.0,0.5,2.0,10.0]]}";

        InkStroke fromCore = CoreCodec.Deserialize(legacy);
        InkStroke fromModules = ModulesCodec.Deserialize(legacy);

        Assert.NotNull(fromCore);
        Assert.NotNull(fromModules);
        Assert.Single(fromCore!.Points);
        Assert.Single(fromModules!.Points);
        Assert.Null(fromCore.ShapeSource);
        Assert.Null(fromModules.ShapeSource);
        Assert.Null(fromCore.ExportObjectFrame());
        Assert.Null(fromModules.ExportObjectFrame());
        Assert.Equal(fromCore.Points[0].X, fromModules.Points[0].X);
    }
}