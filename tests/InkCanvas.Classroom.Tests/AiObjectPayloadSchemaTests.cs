using InkCanvas.Core.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

public sealed class AiObjectPayloadSchemaTests
{
    private static TimelineAiObject Native() => new()
    {
        Id = "object-1",
        Kind = "formula",
        IsNative = true,
        Content = "x^2",
        Latex = "x^2",
        Left = 10,
        Top = 20,
        Width = 120,
        Height = 40,
        Transform = new[] { 1f, 0f, 0f, 1f, 12.5f, -8f },
        Source = "aiWrite",
        SlideId = 9,
        RequestId = "req-object",
    };

    private static TimelineAiObject Image() => new()
    {
        Id = "image-1",
        Kind = "image",
        RenderPngBase64 = "AAAA",
        Left = 1,
        Top = 2,
        Width = 30,
        Height = 40,
        Transform = new[] { 0f, 1f, -1f, 0f, 3f, 4f },
        Source = "aiOrganize",
        SlideId = 6,
        RequestId = "req-image",
    };

    [Fact]
    public void Encode_ProducesSchema3AndRoundTripsNativeObject()
    {
        TimelineAiObject native = Native();

        string payload = AiObjectPayloadCodec.Encode(
            "add", 2, 1, null, new[] { native },
            slideId: 9, source: "aiWrite", requestId: "req-1", sessionId: "session-1", animationStep: 3);

        Assert.NotNull(payload);
        Assert.Contains("\"schema\":3", payload);

        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        Assert.Equal("add", decoded.Action);
        Assert.Equal(2, decoded.PageIndex);
        Assert.Equal(1, decoded.BoardId);
        Assert.Equal(9, decoded.SlideId);
        Assert.Equal(3, decoded.AnimationStep);
        Assert.Equal("session-1", decoded.SessionId);
        Assert.Equal("aiWrite", decoded.Source);
        Assert.Equal("req-1", decoded.RequestId);

        TimelineAiObject item = Assert.Single(decoded.NativeObjects);
        Assert.True(item.HasTransform);
        Assert.Equal(6, item.Transform.Length);
        Assert.Equal(12.5f, item.Transform[4], 3);
        Assert.Equal(-8f, item.Transform[5], 3);
        Assert.Equal("aiWrite", item.Source);
        Assert.Equal(9, item.SlideId);
        Assert.Equal("req-object", item.RequestId);
        Assert.Equal("x^2", item.Latex);
    }

    [Fact]
    public void Encode_RoundTripsImageTransform()
    {
        string payload = AiObjectPayloadCodec.Encode(
            "replace", 0, 1, new[] { Image() }, null,
            slideId: 6, source: "aiOrganize", requestId: "req-image", sessionId: "session-1", animationStep: 0);

        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        TimelineAiObject item = Assert.Single(decoded.Objects);
        Assert.True(item.HasTransform);
        Assert.Equal(0f, item.Transform[0], 3);
        Assert.Equal(-1f, item.Transform[2], 3);
        Assert.Equal(3f, item.Transform[4], 3);
        Assert.Equal(6, item.SlideId);
        Assert.Equal("req-image", item.RequestId);
    }

    [Fact]
    public void Decode_Schema2Payload_RemainsReadable()
    {
        const string schema2 = """
        {
          "objectId": "old-image",
          "action": "add",
          "schema": 2,
          "pageIndex": 1,
          "boardId": 0,
          "objects": [
            { "id": "old-image", "kind": "image", "left": 1, "top": 2, "width": 3, "height": 4 }
          ],
          "nativeObjects": [
            { "id": "old-text", "type": "text", "content": "第一行" }
          ]
        }
        """;

        Assert.True(AiObjectPayloadCodec.TryDecode(schema2, out AiObjectsPayload decoded));
        Assert.Equal("add", decoded.Action);
        Assert.Null(decoded.SlideId);
        Assert.Null(decoded.AnimationStep);
        Assert.Equal("", decoded.SessionId);
        Assert.Equal("", decoded.Source);
        Assert.Equal("", decoded.RequestId);

        TimelineAiObject image = Assert.Single(decoded.Objects);
        Assert.Null(image.Transform);
        Assert.False(image.HasTransform);
        Assert.Equal("", image.Source);
        Assert.Null(image.SlideId);
        Assert.Equal("", image.RequestId);

        TimelineAiObject text = Assert.Single(decoded.NativeObjects);
        Assert.False(text.HasTransform);
        Assert.Null(text.SlideId);
    }

    [Fact]
    public void InvalidTransformLength_IsNormalizedToNull()
    {
        var native = new TimelineAiObject
        {
            Id = "object-1",
            Kind = "formula",
            IsNative = true,
            Content = "x^2",
            Transform = new[] { 1f, 0f, 0f, 1f, 12f },
        };

        string payload = AiObjectPayloadCodec.Encode("add", 0, 0, null, new[] { native });

        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        TimelineAiObject item = Assert.Single(decoded.NativeObjects);
        Assert.Null(item.Transform);
        Assert.False(item.HasTransform);
    }

    [Fact]
    public void ObjectFields_FallBackToPayloadLevel()
    {
        var native = new TimelineAiObject
        {
            Id = "object-1",
            Kind = "text",
            IsNative = true,
            Content = "内容",
        };

        string payload = AiObjectPayloadCodec.Encode(
            "add", 0, 1, null, new[] { native },
            slideId: 4, source: "aiWrite", requestId: "req-level", sessionId: "session-1", animationStep: 2);

        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        TimelineAiObject item = Assert.Single(decoded.NativeObjects);
        Assert.Equal(4, item.SlideId);
        Assert.Equal("aiWrite", item.Source);
        Assert.Equal("req-level", item.RequestId);
        Assert.Equal(2, decoded.AnimationStep);
        Assert.Equal("session-1", decoded.SessionId);
    }

    [Fact]
    public void ObjectFields_OverridePayloadLevel()
    {
        var native = new TimelineAiObject
        {
            Id = "object-1",
            Kind = "formula",
            IsNative = true,
            Content = "x^2",
            Latex = "x^2",
            Transform = new[] { 1f, 0f, 0f, 1f, 12.5f, -8f },
            SlideId = 11,
            Source = "teacherEdit",
            RequestId = "req-object",
        };

        string payload = AiObjectPayloadCodec.Encode(
            "update", 0, 1, null, new[] { native },
            slideId: 4, source: "aiWrite", requestId: "req-level", sessionId: "session-1");

        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        TimelineAiObject item = Assert.Single(decoded.NativeObjects);
        Assert.Equal(11, item.SlideId);
        Assert.Equal("teacherEdit", item.Source);
        Assert.Equal("req-object", item.RequestId);
    }

    [Fact]
    public void BuildObjectFrame_Schema2Payload_FallsBackToIdentityWithoutThrowing()
    {
        const string schema2Main = """
        {
          "objectId": "legacy-image",
          "action": "add",
          "schema": 2,
          "pageIndex": 0,
          "boardId": 0,
          "objects": [
            { "id": "legacy-image", "kind": "image", "left": 1, "top": 2, "width": 30, "height": 40 }
          ],
          "nativeObjects": [
            { "id": "legacy-text", "type": "text", "content": "旧板书" }
          ]
        }
        """;
        const string schema2Side = """
        {
          "objectId": "legacy-side",
          "action": "add",
          "schema": 2,
          "pageIndex": 0,
          "boardId": 1,
          "objects": [
            { "id": "legacy-side", "kind": "image", "left": 5, "top": 6, "width": 7, "height": 8 }
          ],
          "nativeObjects": []
        }
        """;

        Assert.True(AiObjectPayloadCodec.TryDecode(schema2Main, out _));
        Assert.True(AiObjectPayloadCodec.TryDecode(schema2Side, out _));

        var session = new ClassroomSession
        {
            Id = "schema2-identity",
            Events = new List<TimelineEvent>
            {
                new() { TimeMs = 10, PageIndex = 0, BoardId = 0, Kind = TimelineEventKind.AiStep, Payload = schema2Main },
                new() { TimeMs = 20, PageIndex = 0, BoardId = 1, Kind = TimelineEventKind.AiStep, Payload = schema2Side },
            },
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };

        var timeline = new InkTimeline(session, eventsPerCheckpoint: 2, checkpointIntervalMs: 20);
        IReadOnlyList<TimelineAiObject> mainFrame = timeline.BuildObjectFrame(30, 0);
        IReadOnlyList<TimelineAiObject> sideFrame = timeline.BuildObjectFrame(30, 1);

        Assert.Equal(new[] { "legacy-image", "legacy-text" }, mainFrame.Select(item => item.Id).ToArray());
        Assert.Equal("legacy-side", Assert.Single(sideFrame).Id);

        TimelineAiObject image = mainFrame.Single(item => item.Id == "legacy-image");
        Assert.False(image.HasTransform);
        Assert.Null(image.Transform);
        Assert.Equal("", image.Source);
        Assert.Null(image.SlideId);
        Assert.Equal("", image.RequestId);
        Assert.Equal(0, image.BoardId);

        TimelineAiObject text = mainFrame.Single(item => item.Id == "legacy-text");
        Assert.True(text.IsNative);
        Assert.False(text.HasTransform);
        Assert.Null(text.Transform);
        Assert.Equal("旧板书", text.Content);

        // 反复取帧与冷启动重建都不得抛异常，且对象集合保持不变。
        for (int round = 0; round < 3; round++)
        {
            Assert.Equal(2, timeline.BuildObjectFrame(30, 0).Count);
            Assert.Single(timeline.BuildObjectFrame(30, 1));
        }

        var cold = new InkTimeline(new ClassroomSession
        {
            Id = session.Id,
            Events = session.Events.Select(ev => new TimelineEvent
            {
                SessionId = ev.SessionId,
                Seq = ev.Seq,
                TimeMs = ev.TimeMs,
                PageIndex = ev.PageIndex,
                BoardId = ev.BoardId,
                Kind = ev.Kind,
                Payload = ev.Payload,
            }).ToList(),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        }, eventsPerCheckpoint: 2, checkpointIntervalMs: 20);
        Assert.Equal(2, cold.BuildObjectFrame(30, 0).Count);
        Assert.Single(cold.BuildObjectFrame(30, 1));
    }

    [Fact]
    public void EmptyOrMalformedPayload_ReturnsFalse()
    {
        Assert.False(AiObjectPayloadCodec.TryDecode(null, out _));
        Assert.False(AiObjectPayloadCodec.TryDecode("", out _));
        Assert.False(AiObjectPayloadCodec.TryDecode("{broken", out _));
        Assert.False(AiObjectPayloadCodec.TryDecode(
            "{\"action\":\"add\",\"schema\":3,\"pageIndex\":0,\"boardId\":0,\"objects\":[],\"nativeObjects\":[]}", out _));
        Assert.Null(AiObjectPayloadCodec.Encode("add", 0, 0, null, null));
    }
}