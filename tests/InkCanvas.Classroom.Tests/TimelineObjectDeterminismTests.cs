using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 4B：时光机 AI 对象确定性。含 native 对象（带 transform）、旧 PNG 卡片、
/// 跨页对象与子板（boardId=1）的会话，用「从头播放 / 直接定位 / 倒退后重放 / 倍速到达」
/// 四条路径到达同一时间点，断言笔画指纹与对象集合（id/几何/transform/内容/页/板）完全一致。
/// <para>
/// 纯逻辑路径：不启动 UI、不访问网络；回放只读取事件快照与已存 PNG。
/// </para>
/// </summary>
public sealed class TimelineObjectDeterminismTests
{
    private const long T_Page0 = 0;
    private const long T_StrokePage0 = 10;
    private const long T_AddMainObjects = 20;
    private const long T_AddSideObject = 30;
    private const long T_Page1 = 40;
    private const long T_StrokePage1 = 50;
    private const long T_AddPage1Object = 60;
    private const long T_UpdatePage1Object = 70;
    private const long T_AddSidePng = 80;
    private const long T_UpdateMainTransform = 90;
    private const long T_BackPage0 = 100;
    private const long T_CrossPageObject = 110;
    private const long T_StrokePage0Later = 120;

    private static readonly long[] KeyTimes = { 15, 25, 45, 65, 75, 85, 95, 105, 125 };

    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // 主展板文本初始 transform（缩放 + 平移）与后续更新 transform（90° 旋转）。
    private static readonly float[] MainTransform = { 1.2f, 0f, 0f, 1.2f, 4f, 6f };
    private static readonly float[] MainTransformUpdated = { 0f, 1f, -1f, 0f, 12f, -8f };
    private static readonly float[] FormulaTransform = { 1f, 0f, 0f, 1f, 2f, 3f };
    private static readonly float[] SideTransform = { 1.5f, 0f, 0f, 1.5f, 10f, 20f };
    private static readonly float[] IdentityTransform = { 1f, 0f, 0f, 1f, 0f, 0f };

    private static ClassroomSession BuildSession()
    {
        var session = new ClassroomSession
        {
            Id = "determinism-" + Guid.NewGuid().ToString("N"),
            Name = "AI 对象多路径确定性",
            CreatedAt = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc),
            Events = new List<TimelineEvent>(),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };

        void Append(TimelineEvent ev)
        {
            ev.SessionId = session.Id;
            ev.Seq = session.Events.Count + 1;
            session.Events.Add(ev);
        }

        Append(PageChanged(T_Page0, 0));
        Append(StrokeAdded(T_StrokePage0, 0, NewStroke(10f)));
        Append(Ai(T_AddMainObjects, 0, 0, "add",
            new[] { LegacyPng("legacy-png-main", 40, 320, 40) },
            new[]
            {
                NativeText("native-main", "主展板文本", 10, 20, MainTransform, layer: 10),
                NativeFormula("formula-main", "x^2", 20, 120, FormulaTransform, layer: 20),
            }));
        Append(Ai(T_AddSideObject, 0, 1, "add",
            Array.Empty<TimelineAiObject>(),
            new[] { NativeText("native-side", "子板文本", 30, 40, SideTransform, layer: 5) }));
        Append(PageChanged(T_Page1, 1));
        Append(StrokeAdded(T_StrokePage1, 1, NewStroke(50f)));
        Append(Ai(T_AddPage1Object, 1, 0, "add",
            Array.Empty<TimelineAiObject>(),
            new[] { NativeText("native-page1", "第 1 页对象", 60, 60, IdentityTransform, layer: 30) }));
        Append(Ai(T_UpdatePage1Object, 1, 0, "update",
            Array.Empty<TimelineAiObject>(),
            new[] { NativeText("native-page1", "第 1 页对象·改", 60, 60, IdentityTransform, layer: 30) }));
        Append(Ai(T_AddSidePng, 1, 1, "add",
            new[] { LegacyPng("legacy-png-side", 50, 400, 90) },
            Array.Empty<TimelineAiObject>()));
        Append(Ai(T_UpdateMainTransform, 0, 0, "update",
            Array.Empty<TimelineAiObject>(),
            new[] { NativeText("native-main", "主展板文本", 10, 20, MainTransformUpdated, layer: 10) }));
        Append(PageChanged(T_BackPage0, 0));
        // 同一对象 id 从第 1 页移动到第 0 页：历史帧必须仍按各自时刻的归属页/板过滤。
        Append(Ai(T_CrossPageObject, 0, 0, "update",
            Array.Empty<TimelineAiObject>(),
            new[] { NativeText("native-page1", "跨页对象", 60, 60, IdentityTransform, layer: 30) }));
        Append(StrokeAdded(T_StrokePage0Later, 0, NewStroke(120f)));
        return session;
    }

    [Fact]
    public void FourPlaybackPaths_ProduceIdenticalStrokeAndObjectFrames()
    {
        ClassroomSession session = BuildSession();

        Dictionary<long, string> fromStart = Walk(session, KeyTimes);
        Dictionary<long, string> directSeek = KeyTimes
            .ToDictionary(time => time, time => ColdFingerprint(session, time));
        Dictionary<long, string> backwards = Walk(session, KeyTimes.Reverse());
        Dictionary<long, string> fastForward = FastForward(session);

        foreach (long time in KeyTimes)
        {
            Assert.True(fromStart[time] == directSeek[time],
                $"{time}ms：从头播放与直接定位的笔画/对象指纹必须一致。");
            Assert.True(fromStart[time] == backwards[time],
                $"{time}ms：从头播放与倒退后重放的笔画/对象指纹必须一致。");
            Assert.True(fromStart[time] == fastForward[time],
                $"{time}ms：从头播放与倍速到达的笔画/对象指纹必须一致。");
        }
    }

    [Fact]
    public void ObjectFrames_KeepTransformLegacyPngAndPageBoardOwnership()
    {
        ClassroomSession session = BuildSession();
        var timeline = new InkTimeline(session, eventsPerCheckpoint: 2, checkpointIntervalMs: 40);

        // 25ms：主展板 native 文本/公式 + 旧 PNG；transform 原样回放。
        IReadOnlyList<TimelineAiObject> mainAt25 = timeline.BuildObjectFrame(25, 0);
        Assert.Equal(new[] { "native-main", "formula-main", "legacy-png-main" },
            mainAt25.Select(item => item.Id).ToArray());
        TimelineAiObject nativeMain = mainAt25.Single(item => item.Id == "native-main");
        Assert.Equal(MainTransform, nativeMain.Transform);
        Assert.True(nativeMain.IsNative);
        Assert.Equal("主展板文本", nativeMain.Content);
        TimelineAiObject pngMain = mainAt25.Single(item => item.Id == "legacy-png-main");
        Assert.False(pngMain.IsNative);
        Assert.True(pngMain.HasRenderPng);
        Assert.Equal(OnePixelPngBase64, pngMain.RenderPngBase64);

        // 子板只包含 boardId=1 的对象（30ms 添加，35ms 可见）。
        TimelineAiObject sideAt35 = Assert.Single(timeline.BuildObjectFrame(35, 1));
        Assert.Equal("native-side", sideAt35.Id);
        Assert.Equal(SideTransform, sideAt35.Transform);
        Assert.Empty(timeline.BuildObjectFrame(25, 1));
        Assert.Empty(timeline.BuildObjectFrame(45, 0));

        // 65ms：第 1 页对象存在于主展板（旧实现按「事件流最后登记的页」预过滤会丢失该对象）。
        IReadOnlyList<TimelineAiObject> frame65 = timeline.BuildObjectFrame(65, 0);
        Assert.True(frame65.Count > 0,
            "diag page=" + timeline.PageAt(65) + " cutoff=" + timeline.IndexAt(65)
            + " frames=" + string.Join(",", frame65.Select(item => item.Id + "@" + item.PageIndex + "/" + item.BoardId))
            + " cps=" + string.Join(";", session.Checkpoints.Select(cp =>
                cp.EventIndex + ":" + (cp.LiveObjectIds is null ? "null" : string.Join("+", cp.LiveObjectIds)))));
        TimelineAiObject page1At65 = Assert.Single(frame65);
        Assert.Equal("native-page1", page1At65.Id);
        Assert.Equal(1, page1At65.PageIndex);
        Assert.Equal("第 1 页对象", page1At65.Content);

        TimelineAiObject page1At75 = Assert.Single(timeline.BuildObjectFrame(75, 0));
        Assert.Equal("第 1 页对象·改", page1At75.Content);

        // 85ms：第 1 页子板旧 PNG。
        TimelineAiObject sidePng = Assert.Single(timeline.BuildObjectFrame(85, 1));
        Assert.Equal("legacy-png-side", sidePng.Id);
        Assert.Equal(OnePixelPngBase64, sidePng.RenderPngBase64);

        // 90ms 更新发生在第 0 页；第 1 页帧不得提前显示该更新。
        IReadOnlyList<TimelineAiObject> mainAt95 = timeline.BuildObjectFrame(95, 0);
        Assert.DoesNotContain(mainAt95, item => item.Id == "native-main");

        // 105ms：n2 尚未跨页，仍在第 1 页，因此第 0 页帧不应出现它。
        Assert.DoesNotContain(timeline.BuildObjectFrame(105, 0),
            item => item.Id == "native-page1");
        // 主展板 transform 已更新为新值。
        TimelineAiObject updatedMain = timeline.BuildObjectFrame(105, 0).Single(item => item.Id == "native-main");
        Assert.Equal(MainTransformUpdated, updatedMain.Transform);

        // 125ms：对象跨页后出现在第 0 页，内容同步更新。
        TimelineAiObject crossPage = timeline.BuildObjectFrame(125, 0).Single(item => item.Id == "native-page1");
        Assert.Equal(0, crossPage.PageIndex);
        Assert.Equal("跨页对象", crossPage.Content);
    }

    [Fact]
    public void ReplayPath_IsReadOnlyAndHasNoNetworkOrAiModuleReference()
    {
        ClassroomSession session = BuildSession();
        var timeline = new InkTimeline(session, eventsPerCheckpoint: 2, checkpointIntervalMs: 40);
        string before = string.Join("|", session.Events.Select(ev => ev.Payload));

        _ = timeline.BuildFrame(125);
        _ = timeline.BuildObjectFrame(125, 0);
        _ = timeline.BuildObjectFrame(125, 1);
        _ = timeline.SceneAt(125);

        Assert.Equal(before, string.Join("|", session.Events.Select(ev => ev.Payload)));

        // 接口隔离：回放所在 Core 程序集不依赖 HTTP / Platform / AI 插件客户端。
        var assembly = typeof(InkTimeline).Assembly;
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain("InkCanvas.Platform", references);
        Assert.DoesNotContain("InkCanvas.Modules.AiAssistant", references);
        Assert.Null(assembly.GetType("InkCanvas.Core.AiAssistant.AiClient"));
    }

    // ---- 四路径 -------------------------------------------------------------

    private static Dictionary<long, string> Walk(ClassroomSession source, IEnumerable<long> order)
    {
        InkTimeline timeline = CreateTimeline(source);
        var result = new Dictionary<long, string>();
        foreach (long time in order)
            result[time] = Fingerprint(timeline, time);
        return result;
    }

    private static string ColdFingerprint(ClassroomSession source, long timeMs)
        => Fingerprint(CreateTimeline(source), timeMs);

    /// <summary>倍速到达：以 7ms 步长快速走完整条时间线，再取关键时间点。</summary>
    private static Dictionary<long, string> FastForward(ClassroomSession source)
    {
        InkTimeline timeline = CreateTimeline(source);
        long max = KeyTimes.Max();
        for (long time = 0; time <= max; time += 7)
        {
            _ = timeline.BuildFrame(time);
            _ = timeline.BuildObjectFrame(time, 0);
            _ = timeline.BuildObjectFrame(time, 1);
        }

        return KeyTimes.ToDictionary(time => time, time => Fingerprint(timeline, time));
    }

    private static InkTimeline CreateTimeline(ClassroomSession source)
        => new(CloneSession(source), eventsPerCheckpoint: 2, checkpointIntervalMs: 40);

    private static string Fingerprint(InkTimeline timeline, long timeMs)
    {
        string strokes = string.Join(",", timeline.BuildFrame(timeMs).Select(StrokeSignature));
        string mainObjects = ObjectListSignature(timeline.BuildObjectFrame(timeMs, 0));
        string sideObjects = ObjectListSignature(timeline.BuildObjectFrame(timeMs, 1));
        return timeline.PageAt(timeMs) + "/" + strokes + "/" + mainObjects + "/" + sideObjects;
    }

    private static string ObjectListSignature(IReadOnlyList<TimelineAiObject> objects)
        => string.Join(";", objects.Select(ObjectSignature));

    /// <summary>对象稳定签名：id / 归属 / 几何 / transform / 内容 / 旧 PNG 全部参与比较。</summary>
    private static string ObjectSignature(TimelineAiObject item)
    {
        string transform = item.Transform is null
            ? "null"
            : string.Join(",", item.Transform.Select(value =>
                value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        return string.Join("|",
            item.Id, item.Kind, item.PageIndex, item.BoardId, item.IsNative,
            item.Left.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Top.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.LayerOrder?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            transform, item.Content, item.Title, item.Author, item.Latex, item.Text,
            item.FontFamily, item.FontSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Color, item.LineHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            item.Align, item.Columns, item.RenderMissing, item.RenderPngBase64);
    }

    private static string StrokeSignature(InkStroke stroke)
    {
        IReadOnlyList<InkPoint> points = stroke.Points;
        if (points.Count == 0) return "empty";
        InkPoint first = points[0];
        InkPoint last = points[^1];
        return points.Count
            + ":" + first.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + first.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + last.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + last.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + stroke.Attributes.BaseWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- 事件构造 -----------------------------------------------------------

    private static TimelineEvent PageChanged(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static TimelineEvent StrokeAdded(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static TimelineEvent Ai(long timeMs, int page, int boardId, string action,
        IReadOnlyList<TimelineAiObject> images, IReadOnlyList<TimelineAiObject> natives) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        BoardId = boardId,
        Kind = TimelineEventKind.AiStep,
        Payload = AiObjectPayloadCodec.Encode(action, page, boardId, images, natives),
    };

    private static InkStroke NewStroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.AddPoint(x + 10f, 12f);
        stroke.Complete();
        return stroke;
    }

    private static TimelineAiObject NativeText(string id, string content, double left, double top,
        float[] transform, long layer) => new()
    {
        Id = id,
        Kind = "text",
        IsNative = true,
        Content = content,
        Title = "确定性标题",
        Author = "教师",
        Dynasty = "唐",
        FontFamily = "Microsoft YaHei UI",
        FontSize = 28,
        Color = "#FF112233",
        LineHeight = 1.5,
        Align = "center",
        Columns = 2,
        Left = left,
        Top = top,
        Width = 200,
        Height = 80,
        LayerOrder = layer,
        Transform = transform,
        GroupId = "determinism-group",
    };

    private static TimelineAiObject NativeFormula(string id, string latex, double left, double top,
        float[] transform, long layer) => new()
    {
        Id = id,
        Kind = "formula",
        IsNative = true,
        Latex = latex,
        FontFamily = "Cambria Math",
        FontSize = 30,
        Color = "#FF112233",
        Left = left,
        Top = top,
        Width = 180,
        Height = 70,
        LayerOrder = layer,
        Transform = transform,
        GroupId = "determinism-group",
    };

    private static TimelineAiObject LegacyPng(string id, long layer, double left, double top) => new()
    {
        Id = id,
        Kind = "image",
        Left = left,
        Top = top,
        Width = 120,
        Height = 60,
        LayerOrder = layer,
        Text = "旧 PNG 卡片",
        RenderPngBase64 = OnePixelPngBase64,
    };

    /// <summary>深拷贝会话事件（含 BoardId/PptSlideId），并清空检查点，模拟四条独立回放路径。</summary>
    private static ClassroomSession CloneSession(ClassroomSession source)
    {
        return new ClassroomSession
        {
            Id = source.Id,
            Name = source.Name,
            CreatedAt = source.CreatedAt,
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
            Events = source.Events.Select(ev => new TimelineEvent
            {
                SessionId = ev.SessionId,
                Seq = ev.Seq,
                TimeMs = ev.TimeMs,
                PageIndex = ev.PageIndex,
                PptSlideId = ev.PptSlideId,
                BoardId = ev.BoardId,
                Kind = ev.Kind,
                StrokeId = ev.StrokeId,
                StrokeJson = ev.StrokeJson,
                StrokeIds = ev.StrokeIds,
                Payload = ev.Payload,
            }).ToList(),
        };
    }
}