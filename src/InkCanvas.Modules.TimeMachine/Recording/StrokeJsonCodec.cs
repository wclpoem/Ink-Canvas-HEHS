using InkCanvas.Core.Ink;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>
/// 单笔 JSON 编解码。字段实现已收敛到 <see cref="InkCanvas.Core.Classroom.StrokeJsonCodec"/>，
/// 这里保留时光机命名空间下的入口，确保 Core 录制与 Modules 回放使用同一份字段布局
/// （Id/LayerOrder/Nib/InkWash/ShapeSource/ObjectFrame/Tilt/Points），旧 JSON 缺字段兼容。
/// </summary>
public static class StrokeJsonCodec
{
    public static string Serialize(InkStroke stroke) => InkCanvas.Core.Classroom.StrokeJsonCodec.Serialize(stroke);

    public static InkStroke Deserialize(string json) => InkCanvas.Core.Classroom.StrokeJsonCodec.Deserialize(json);
}