using System.Numerics;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 2B：时光机对象变换的录制往返、回放绘制矩阵，以及 schema=2 缺 transform 的 identity 回退。
/// </summary>
public sealed class TimelineObjectTransformTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static readonly float[] Rotation90 = { 0f, 1f, -1f, 0f, 0f, 0f };

    private static TimelineAiObject NativeText(float[] transform) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = "text",
        IsNative = true,
        Content = "变换回放",
        Left = 10,
        Top = 20,
        Width = 100,
        Height = 50,
        Transform = transform,
    };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-transform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static float[] ScaleTranslate(float sx, float sy, float dx, float dy)
        => new[] { sx, 0f, 0f, sy, dx, dy };

    [Fact]
    public void NativeElement_AppliesRecordedTransformToFrameAndBounds()
    {
        var element = new TimelineNativeTextElement(NativeText(Rotation90));

        Assert.Equal(0f, element.Frame.Transform.M11, 4);
        Assert.Equal(1f, element.Frame.Transform.M12, 4);
        Assert.Equal(-1f, element.Frame.Transform.M21, 4);
        Assert.Equal(0f, element.Frame.Transform.M22, 4);

        // 未变换局部矩形保持原样，绘制由 frame.Transform 承担。
        Assert.Equal(10, element.Frame.Bounds.Left, 3);
        Assert.Equal(100, element.Frame.Bounds.Width, 3);
        Assert.Equal(50, element.Frame.Bounds.Height, 3);

        // 90° 旋转后外接框宽高互换：(10,20,110,70) -> (-70,10,-20,110)。
        Assert.Equal(-70, element.Bounds.Left, 3);
        Assert.Equal(10, element.Bounds.Top, 3);
        Assert.Equal(-20, element.Bounds.Right, 3);
        Assert.Equal(110, element.Bounds.Bottom, 3);
    }

    [Fact]
    public void LegacySchema2WithoutTransform_FallsBackToIdentity()
    {
        var element = new TimelineNativeTextElement(NativeText(null));

        Assert.Equal(Matrix3x2.Identity, element.Frame.Transform);
        Assert.Equal(element.Frame.Bounds, element.Bounds);
        Assert.Equal(10, element.Bounds.Left, 3);
        Assert.Equal(110, element.Bounds.Right, 3);

        Assert.False(NativeText(null).HasTransform);
    }

    [Fact]
    public void EncodeDecode_RoundTripsTransformAndReplayFrameKeepsIt()
    {
        var session = new ClassroomSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "transform-roundtrip",
            CreatedAt = DateTime.Now,
            Events = new List<TimelineEvent>
            {
                new()
                {
                    TimeMs = 100,
                    PageIndex = 0,
                    BoardId = 0,
                    Kind = TimelineEventKind.AiStep,
                    Payload = AiObjectPayloadCodec.Encode(
                        "add", 0, 0, null, new[] { NativeText(ScaleTranslate(1.5f, 1.5f, 30f, -12f)) }),
                },
            },
            Checkpoints = new List<SessionCheckpoint>(),
        };

        TimelineAiObject replay = Assert.Single(new InkTimeline(session).BuildObjectFrame(150));
        Assert.True(replay.HasTransform);
        Assert.Equal(1.5f, replay.Transform[0], 4);
        Assert.Equal(-12f, replay.Transform[5], 4);

        var element = new TimelineNativeTextElement(replay);
        Assert.Equal(1.5f, element.Frame.Transform.M11, 4);
        Assert.Equal(30f, element.Frame.Transform.M31, 4);
    }

    [Fact]
    public void Recorder_WritesTransformForNativeObjectsAndImages()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("变换录制");

            var textTransform = ScaleTranslate(2f, 2f, 12f, 24f);
            var text = new BoardTextObject
            {
                Type = "text",
                Content = "变换文本",
                Left = 10,
                Top = 10,
                Width = 200,
                Height = 80,
                TransformMatrix = textTransform,
            };
            var formulaTransform = Rotation90;
            var formula = new BoardFormulaObject
            {
                Latex = "x^2",
                Left = 10,
                Top = 120,
                Width = 180,
                Height = 70,
                TransformMatrix = formulaTransform,
            };
            board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "transform-group");

            var imageTransform = ScaleTranslate(1f, 1f, 5f, 6f);
            var image = new BoardImageAsset
            {
                Name = "AI 卡片",
                Data = Convert.FromBase64String(OnePixelPngBase64),
                ContentType = "image/png",
                Left = 300,
                Top = 40,
                Width = 120,
                Height = 60,
                Transform = imageTransform,
            };
            board.AddImagesAtomic(new[] { image }, "AI 板书");

            List<TimelineEvent> aiEvents = recorder.Session.Events
                .Where(item => item.Kind == TimelineEventKind.AiStep).ToList();
            Assert.Equal(2, aiEvents.Count);

            AiObjectsPayload nativePayload = Decode(aiEvents[0]);
            TimelineAiObject textSnapshot = Assert.Single(nativePayload.NativeObjects,
                item => item.Kind == "text");
            Assert.True(textSnapshot.HasTransform);
            Assert.Equal(12f, textSnapshot.Transform[4], 3);

            TimelineAiObject formulaSnapshot = Assert.Single(nativePayload.NativeObjects,
                item => item.Kind == "formula");
            Assert.True(formulaSnapshot.HasTransform);
            Assert.Equal(-1f, formulaSnapshot.Transform[2], 3);

            TimelineAiObject imageSnapshot = Assert.Single(Decode(aiEvents[1]).Objects);
            Assert.True(imageSnapshot.HasTransform);
            Assert.Equal(5f, imageSnapshot.Transform[4], 3);
            Assert.Equal(6f, imageSnapshot.Transform[5], 3);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    private static AiObjectsPayload Decode(TimelineEvent item)
    {
        Assert.True(AiObjectPayloadCodec.TryDecode(item.Payload, out AiObjectsPayload payload));
        return payload;
    }
}