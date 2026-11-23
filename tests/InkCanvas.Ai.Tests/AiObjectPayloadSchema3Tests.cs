using InkCanvas.Core.Classroom;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>
/// 时光机 AI 对象载荷 schema=3：新增 slideId/source/requestId 的写入与读取，
/// 以及 schema=2 旧载荷（无新字段）的兼容回归。
/// </summary>
public sealed class AiObjectPayloadSchema3Tests
{
    private static TimelineAiObject NativeText(string id, string content) => new()
    {
        Id = id,
        Kind = "text",
        IsNative = true,
        Content = content,
        FontFamily = "Microsoft YaHei UI",
        FontSize = 32,
        Color = "#FF202024",
        LineHeight = 1.4,
        Align = "left",
        Columns = 1,
        Left = 10,
        Top = 20,
        Width = 320,
        Height = 140,
    };

    [Fact]
    public void Schema_Constant_IsThree()
    {
        Assert.Equal(3, AiObjectPayloadCodec.Schema);
    }

    [Fact]
    public void Encode_NativeObjectsOnly_EmitsSchemaThree()
    {
        string json = AiObjectPayloadCodec.Encode(
            "add", 1, 0, objects: null, nativeObjects: new[] { NativeText("native-1", "板书内容") });

        Assert.NotNull(json);
        Assert.Contains("\"schema\":3", json);

        Assert.True(AiObjectPayloadCodec.TryDecode(json, out AiObjectsPayload payload));
        Assert.Equal(3, AiObjectPayloadCodec.Schema);
        Assert.Equal("add", payload.Action);
        Assert.Single(payload.NativeObjects);
        Assert.Equal("native-1", payload.NativeObjects[0].Id);
    }

    [Fact]
    public void Encode_WithSlideSourceAndRequest_RoundTripsNewFields()
    {
        string json = AiObjectPayloadCodec.Encode(
            "add",
            2,
            1,
            objects: null,
            nativeObjects: new[] { NativeText("native-2", "第二页板书") },
            slideId: 7,
            source: "AiWrite",
            requestId: "req-1");

        Assert.NotNull(json);
        Assert.True(AiObjectPayloadCodec.TryDecode(json, out AiObjectsPayload payload));
        Assert.Equal(7, payload.SlideId);
        Assert.Equal("AiWrite", payload.Source);
        Assert.Equal("req-1", payload.RequestId);
    }

    [Fact]
    public void LegacySchemaTwoJson_WithoutNewFields_DecodesWithEmptyDefaults()
    {
        const string legacyJson = """
            {"objectId":"legacy-native","action":"add","schema":2,"pageIndex":0,"boardId":0,
            "objects":[],
            "nativeObjects":[{"id":"legacy-native","type":"text","content":"旧板书","fontFamily":"Microsoft YaHei UI","fontSize":32,"color":"#FF202024","lineHeight":1.4,"align":"left","columns":1,"left":10,"top":20,"width":320,"height":140}]}
            """;

        Assert.True(AiObjectPayloadCodec.TryDecode(legacyJson, out AiObjectsPayload payload));
        Assert.Equal("add", payload.Action);
        Assert.Null(payload.SlideId);
        Assert.Equal("", payload.Source);
        Assert.Equal("", payload.RequestId);
        Assert.Equal("旧板书", Assert.Single(payload.NativeObjects).Content);
    }
}