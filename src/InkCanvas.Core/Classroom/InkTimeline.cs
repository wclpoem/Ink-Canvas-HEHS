using InkCanvas.Core.Ink;
using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 某一时刻的课堂场景快照：画板页、背景、PPT 幻灯片（含截图路径）、最近点名与倒计时剩余。
/// <para><see cref="PptScreenshotPath"/> 为空表示该页没有可用截图，回放时应回退纯色背景。</para>
/// </summary>
public readonly record struct ClassroomScene(
    int PageIndex, string Background, int PptSlideIndex, string RosterName, int TimerRemainingMs,
    string PptScreenshotPath = "", double ViewScale = 1, double ViewTx = 0, double ViewTy = 0);

/// <summary>
/// <see cref="TimelineEventKind.ViewChanged"/> 的载荷解析结果。
/// <para><see cref="IsValid"/> 为 false 表示载荷无法解析，调用方应忽略该事件。</para>
/// </summary>
public readonly record struct TimerViewState(double Scale, double Tx, double Ty, bool IsValid);

/// <summary>
/// <see cref="TimelineEventKind.PptSlideChanged"/> 的载荷解析结果。
/// <para><see cref="IsValid"/> 为 false 表示载荷既不是 JSON 也不是旧版纯序号，调用方应忽略该事件。</para>
/// </summary>
public readonly record struct PptSlideChange(int SlideId, int Index, string Screenshot, bool IsValid);

/// <summary>
/// 从 <see cref="ClassroomSession"/> 的事件流重建任意时刻的笔画画面。
/// <para>
/// 纯逻辑、无 UI 依赖：<see cref="BuildFrame"/> 只读取事件（不修改会话），
/// 从最近的 <see cref="SessionCheckpoint"/> 增量重放到目标时刻，并按当时的当前页过滤。
/// </para>
/// </summary>
public sealed class InkTimeline
{
    public const int DefaultEventsPerCheckpoint = 200;
    public const long DefaultCheckpointIntervalMs = 10_000;

    private readonly ClassroomSession _session;
    private readonly List<TimelineEvent> _events;
    private readonly int _eventsPerCheckpoint = DefaultEventsPerCheckpoint;
    private readonly long _checkpointIntervalMs = DefaultCheckpointIntervalMs;

    // id -> 笔画记录（页码 + 添加顺序），由事件流增量构建。
    private sealed class StrokeRecord
    {
        public InkStroke Stroke;
        public int PageIndex;
        public int Order;
    }

    // AI 板书对象记录由事件流按需解析，不做跨时间缓存，避免把晚于目标时刻的 update 提前显示。

    // 已解析笔画缓存。为避免长会话首次定位把全部 StrokeJson 反序列化进内存，
    // 该缓存只保存「最近需要过的」笔画，并由 MaxCachedStrokes 限制上限。
    private readonly Dictionary<string, StrokeRecord> _registry = new();
    private int _registryIndex;

    // 笔画缓存上限：超出后淘汰当前帧未使用的旧记录，避免 90 分钟课堂的
    // 数万笔画长期驻留内存（淘汰后再次需要时按需重新解析）。
    private const int MaxCachedStrokes = 4096;

    // 笔画 id -> 所属页码。注册 StrokeAdded 时只读取事件的 PageIndex，不解析 StrokeJson。
    private readonly Dictionary<string, int> _strokePages = new();

    // 笔画 id -> 首次登记的事件索引，用于保持帧内笔画顺序，也作为按需解析时查找 JSON 的起点。
    private readonly Dictionary<string, int> _strokeOrder = new();

    // AI 对象 id -> 首次登记索引。归属页/板以目标时刻解析出的快照为准，
    // 不能用「整个事件流的最后登记」预过滤，否则历史帧会被未来的跨页/换板事件提前排除。
    private readonly Dictionary<string, int> _objectOrder = new();


    // 缓存插入顺序（FIFO），超出上限时据此淘汰最旧的未使用记录。
    private readonly Queue<string> _cacheQueue = new();

    // 增量生成检查点的游标与状态。
    private int _checkpointAppliedIndex;
    private readonly HashSet<string> _checkpointAlive = new();
    private readonly HashSet<string> _checkpointObjectAlive = new();
    private long _checkpointLastTime = long.MinValue;

    public InkTimeline(ClassroomSession session)
        : this(session, DefaultEventsPerCheckpoint, DefaultCheckpointIntervalMs)
    {
    }

    /// <summary>
    /// 创建时间轴，并指定检查点策略：每 <paramref name="eventsPerCheckpoint"/> 个事件、
    /// 或距上一检查点 <paramref name="checkpointIntervalMs"/> 毫秒时生成检查点。
    /// </summary>
    public InkTimeline(ClassroomSession session, int eventsPerCheckpoint, long checkpointIntervalMs)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _eventsPerCheckpoint = eventsPerCheckpoint > 0 ? eventsPerCheckpoint : DefaultEventsPerCheckpoint;
        _checkpointIntervalMs = checkpointIntervalMs > 0 ? checkpointIntervalMs : DefaultCheckpointIntervalMs;
        _events = session.Events ??= new List<TimelineEvent>();
        _session.Bookmarks ??= new List<SessionBookmark>();

        // 旧数据可能缺 Seq，导入或手工编辑过的会话可能时间乱序。先做一次稳定归一化，
        // 否则 IndexAt 的二分查找与「同时间按序号排序」都会失真。
        bool reordered = NormalizeEventOrder();
        if (reordered) _session.Checkpoints = new List<SessionCheckpoint>();

        // 已带检查点的会话（例如从磁盘加载）直接沿用，避免重复生成。
        if (!reordered && _session.Checkpoints is { Count: > 0 })
        {
            _checkpointAppliedIndex = _events.Count;
            _checkpointAlive = new HashSet<string>(_session.Checkpoints[^1].LiveStrokeIds ?? Array.Empty<string>());

            // 旧检查点没有 LiveObjectIds：对象状态从事件流起点重建，避免漏掉此前的 AI 卡片。
            _checkpointObjectAlive.Clear();
            if (_session.Checkpoints[^1].LiveObjectIds is { } liveObjects)
            {
                foreach (string id in liveObjects) _checkpointObjectAlive.Add(id);
            }
            else
            {
                foreach (string id in RebuildObjectAlive()) _checkpointObjectAlive.Add(id);
            }

            for (int i = 0; i < _events.Count; i++) EnsureEventRegistered(_events[i], i);
            _registryIndex = _events.Count;
        }
        else
        {
            _session.Checkpoints ??= new List<SessionCheckpoint>();
        }
    }

    /// <summary>
    /// 补齐会话归属与事件序号，并把时间乱序的事件稳定重排为
    /// <c>(TimeMs, Seq, 原索引)</c> 顺序。返回是否发生了重排（重排后索引失效，需重建检查点）。
    /// </summary>
    private bool NormalizeEventOrder()
    {
        if (_events.Count == 0) return false;

        if (_events.Any(e => e is null))
        {
            _events.RemoveAll(e => e is null);
            if (_events.Count == 0) return false;
        }

        bool hasMissingSeq = false;
        bool timeOrdered = true;
        long previousTime = long.MinValue;
        int previousSeq = 0;

        for (int i = 0; i < _events.Count; i++)
        {
            TimelineEvent e = _events[i];
            if (string.IsNullOrEmpty(e.SessionId)) e.SessionId = _session.Id;
            if (e.Seq <= 0) hasMissingSeq = true;
            if (e.TimeMs < previousTime) timeOrdered = false;
            else if (e.TimeMs == previousTime && e.Seq < previousSeq) timeOrdered = false;
            previousTime = e.TimeMs;
            previousSeq = e.Seq;
        }

        // 只有缺序号（旧数据）时才按当前数组顺序补号；已有序号即使非递增也保留，
        // 由下面的排序把它放到正确位置，避免重编号改变「同毫秒先后」的语义。
        if (hasMissingSeq)
        {
            for (int i = 0; i < _events.Count; i++) _events[i].Seq = i + 1;
        }

        if (timeOrdered) return false;

        var ordered = _events
            .Select((e, index) => (Event: e, Index: index))
            .OrderBy(x => x.Event.TimeMs)
            .ThenBy(x => x.Event.Seq)
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();
        _events.Clear();
        _events.AddRange(ordered);
        return true;
    }

    public ClassroomSession Session => _session;

    /// <summary>录制总时长（毫秒），等于最后一个事件的时间。</summary>
    public long Duration => _events.Count == 0 ? 0 : Math.Max(0, _events[^1].TimeMs);

    /// <summary>目标时刻应显示的当前页笔画（按添加顺序）。</summary>
    public IReadOnlyList<InkStroke> BuildFrame(long timeMs)
    {
        EnsureCheckpoints();
        EnsureRegistry();

        int cutoff = IndexAt(timeMs);
        if (cutoff < 0) return Array.Empty<InkStroke>();

        int page = PageAt(timeMs);
        var checkpoint = NearestCheckpoint(timeMs);

        HashSet<string> alive;
        int start;
        if (checkpoint is not null && checkpoint.EventIndex <= cutoff)
        {
            alive = new HashSet<string>(checkpoint.LiveStrokeIds ?? Array.Empty<string>());
            start = checkpoint.EventIndex + 1;
        }
        else
        {
            alive = new HashSet<string>();
            start = 0;
        }

        for (int i = start; i <= cutoff; i++) ApplyEvent(alive, _events[i]);

        var result = new List<(InkStroke Stroke, int Order)>();
        var frameIds = new HashSet<string>();
        foreach (var id in alive)
        {
            // 先用轻量页码索引过滤，避免为其它页的笔画做无谓的反序列化。
            if (!_strokePages.TryGetValue(id, out int strokePage) || strokePage != page) continue;

            // 按需解析：仅对当前帧真正需要的笔画反序列化，成功后进入缓存。
            InkStroke stroke = ResolveStroke(id);
            if (stroke is null) continue; // 载荷为空或解析失败：跳过该笔画，不抛异常

            // 顺序仍取首次登记的事件索引，与是否命中缓存无关。
            int order = _strokeOrder.TryGetValue(id, out int knownOrder) ? knownOrder : int.MaxValue;
            result.Add((stroke, order));
            frameIds.Add(id);
        }
        result.Sort((a, b) => a.Order.CompareTo(b.Order));
        TrimCache(frameIds);
        return result.Select(x => x.Stroke).ToList();
    }

    /// <summary>
    /// 目标时刻当前页的 AI 板书对象（默认主展板）。只读取录制事件中的对象快照与 PNG，
    /// 不调用模型、不重新渲染；无 PNG 或超阈值对象由调用方显示占位。
    /// </summary>
    public IReadOnlyList<TimelineAiObject> BuildObjectFrame(long timeMs) => BuildObjectFrame(timeMs, 0);

    /// <summary>
    /// 取指定画板（0 = 主展板，1 = 板中板）在目标时刻仍存在的 AI 对象。
    /// 旧会话/旧检查点缺少对象字段时，从事件流起点重建对象状态。
    /// </summary>
    public IReadOnlyList<TimelineAiObject> BuildObjectFrame(long timeMs, int boardId)
    {
        EnsureCheckpoints();
        EnsureRegistry();

        int cutoff = IndexAt(timeMs);
        if (cutoff < 0) return Array.Empty<TimelineAiObject>();

        int page = PageAt(timeMs);
        SessionCheckpoint checkpoint = NearestObjectCheckpoint(timeMs);

        HashSet<string> alive;
        int start;
        if (checkpoint is not null)
        {
            alive = new HashSet<string>(checkpoint.LiveObjectIds ?? Array.Empty<string>());
            start = checkpoint.EventIndex + 1;
        }
        else
        {
            alive = new HashSet<string>();
            start = 0;
        }

        // 增量窗口内出现的对象快照是目标时刻的最新状态；检查点之前就存在的对象
        // 再从首次登记位置解析到检查点索引，确保不会把晚于目标时刻的 update 提前显示。
        var latest = new Dictionary<string, TimelineAiObject>(StringComparer.Ordinal);
        for (int i = start; i <= cutoff; i++)
        {
            ApplyObjectEvent(alive, _events[i]);
            if (_events[i].Kind != TimelineEventKind.AiStep) continue;
            if (!AiObjectPayloadCodec.TryDecode(_events[i].Payload, out AiObjectsPayload payload)) continue;
            foreach (TimelineAiObject snapshot in payload.AllObjects) latest[snapshot.Id] = snapshot;
        }

        int fallbackIndex = checkpoint?.EventIndex ?? cutoff;
        var result = new List<(TimelineAiObject Object, long LayerOrder, int Order)>();
        foreach (string id in alive)
        {
            TimelineAiObject item = latest.TryGetValue(id, out TimelineAiObject snapshotItem)
                ? snapshotItem
                : ResolveObjectAt(id, fallbackIndex);
            if (item is null) continue;

            // 按目标时刻快照自身的页/板过滤：同一对象可能在后续事件中跨页或从子板移回主展板，
            // 用未来状态预过滤会让「从头播放/倒退/直接定位」看到不同的对象集合。
            if (item.PageIndex != page) continue;
            if (item.BoardId != boardId) continue;

            int order = _objectOrder.TryGetValue(id, out int knownOrder) ? knownOrder : int.MaxValue;
            result.Add((item, item.LayerOrder ?? long.MaxValue, order));
        }

        result.Sort((a, b) =>
        {
            int byLayer = a.LayerOrder.CompareTo(b.LayerOrder);
            return byLayer != 0 ? byLayer : a.Order.CompareTo(b.Order);
        });
        return result.Select(item => item.Object).ToList();
    }

    /// <summary>目标时刻所在的页码。</summary>
    public int PageAt(long timeMs)
    {
        int cutoff = IndexAt(timeMs);
        int page = _events.Count > 0 ? _events[0].PageIndex : 0;
        for (int i = 0; i <= cutoff; i++)
        {
            if (_events[i].Kind == TimelineEventKind.PageChanged) page = _events[i].PageIndex;
        }
        return page;
    }

    /// <summary>目标时刻的背景枚举名（无信息时回退到第一个 BackgroundChanged，再回退 "Black"）。</summary>
    public string BackgroundAt(long timeMs)
    {
        int cutoff = IndexAt(timeMs);
        string background = null;
        for (int i = 0; i <= cutoff; i++)
        {
            if (_events[i].Kind == TimelineEventKind.BackgroundChanged) background = _events[i].Payload;
        }
        if (background is null)
        {
            foreach (var e in _events)
            {
                if (e.Kind == TimelineEventKind.BackgroundChanged) { background = e.Payload; break; }
            }
        }
        return background ?? "Black";
    }

    /// <summary>目标时刻的完整课堂场景：按事件顺序累积页 / 背景 / PPT 页 / 点名 / 倒计时剩余。</summary>
    public ClassroomScene SceneAt(long timeMs)
    {
        int cutoff = IndexAt(timeMs);
        int page = _events.Count > 0 ? _events[0].PageIndex : 0;
        string background = null;
        int pptSlide = 0;
        string screenshot = "";
        string roster = "";
        int timer = 0;
        double viewScale = 1, viewTx = 0, viewTy = 0;

        for (int i = 0; i <= cutoff; i++)
        {
            var e = _events[i];
            switch (e.Kind)
            {
                case TimelineEventKind.PageChanged:
                    page = e.PageIndex;
                    break;
                case TimelineEventKind.BackgroundChanged:
                    background = e.Payload;
                    break;
                case TimelineEventKind.PptSlideChanged:
                    PptSlideChange change = DecodePptSlideChanged(e.Payload);
                    if (!change.IsValid) break;
                    if (change.Index > 0) pptSlide = change.Index;
                    // 新页没有截图时显式清空，避免沿用上一页的图片。
                    screenshot = change.Screenshot ?? "";
                    break;
                case TimelineEventKind.RosterDrawn:
                    roster = e.Payload ?? roster;
                    break;
                case TimelineEventKind.TimerStarted:
                case TimelineEventKind.TimerStopped:
                    timer = Math.Max(0, ParseInt(e.Payload, timer));
                    break;
                case TimelineEventKind.TimerEnded:
                    timer = 0;
                    break;
                case TimelineEventKind.ViewChanged:
                    TimerViewState view = DecodeViewChanged(e.Payload);
                    if (!view.IsValid) break;
                    viewScale = view.Scale;
                    viewTx = view.Tx;
                    viewTy = view.Ty;
                    break;
            }
        }

        if (background is null)
        {
            foreach (var e in _events)
            {
                if (e.Kind == TimelineEventKind.BackgroundChanged) { background = e.Payload; break; }
            }
        }

        return new ClassroomScene(page, background ?? "Black", pptSlide, roster ?? "", timer, screenshot ?? "",
            viewScale, viewTx, viewTy);
    }

    /// <summary>时间 &lt;= <paramref name="timeMs"/> 的最后一个事件索引；没有则返回 -1。</summary>
    public int IndexAt(long timeMs)
    {
        int lo = 0, hi = _events.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_events[mid].TimeMs <= timeMs) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }

    public long TimeAtEvent(int index)
        => index >= 0 && index < _events.Count ? _events[index].TimeMs : 0;

    /// <summary>
    /// 目标时刻的书写光标位置：当前页最近一笔的最后一个点；没有可用笔画时返回 null。
    /// 仅用于导出 / 回放的可选光标叠加，不参与笔画集合计算。
    /// </summary>
    public (float X, float Y)? CursorPointAt(long timeMs)
    {
        EnsureRegistry();
        int cutoff = IndexAt(timeMs);
        if (cutoff < 0) return null;
        int page = PageAt(timeMs);

        for (int i = cutoff; i >= 0; i--)
        {
            TimelineEvent e = _events[i];
            if (e.Kind != TimelineEventKind.StrokeAdded) continue;
            if (e.PageIndex != page || string.IsNullOrEmpty(e.StrokeId)) continue;

            // 不再依赖全量缓存：需要时按需解析该笔画。
            InkStroke stroke = ResolveStroke(e.StrokeId);
            if (stroke is null) continue;

            var points = stroke.Points;
            if (points.Count == 0) continue;
            InkPoint last = points[^1];
            return (last.X, last.Y);
        }
        return null;
    }

    /// <summary>
    /// 异步定位的回调闸门：token 已取消或版本落后（已有更新请求）时返回 false，
    /// 调用方不得把该帧应用到界面，保证只呈现最后一次目标。
    /// </summary>
    public static bool ShouldApplySeek(CancellationToken token, int requestVersion, int latestVersion)
        => !token.IsCancellationRequested && requestVersion == latestVersion;

    // ---- 章节书签 ---------------------------------------------------------

    private const long AutoBookmarkMergeWindowMs = 2_000;
    private readonly HashSet<long> _suppressedAutoTimes = new();

    /// <summary>
    /// 按时间轴规则生成自动章节书签（不修改会话）：
    /// 切页 / 清空 / PPT 翻页 / 点名 / 倒计时结束处生成，与前一个自动书签相距 &lt; 2s 时合并为一个。
    /// </summary>
    public IReadOnlyList<SessionBookmark> AutoBookmarks()
    {
        var result = new List<SessionBookmark>();
        int pptSlide = 0;

        foreach (var e in _events)
        {
            string name;
            string kind;
            switch (e.Kind)
            {
                case TimelineEventKind.PageChanged:
                    name = $"第 {e.PageIndex + 1} 页";
                    kind = SessionBookmarkKind.Page;
                    break;
                case TimelineEventKind.Cleared:
                    name = $"清空第 {e.PageIndex + 1} 页";
                    kind = SessionBookmarkKind.Cleared;
                    break;
                case TimelineEventKind.PptSlideChanged:
                    PptSlideChange change = DecodePptSlideChanged(e.Payload);
                    if (change.IsValid && change.Index > 0) pptSlide = change.Index;
                    name = pptSlide > 0 ? $"PPT 第 {pptSlide} 页" : "PPT 翻页";
                    kind = SessionBookmarkKind.Ppt;
                    break;
                case TimelineEventKind.RosterDrawn:
                    name = string.IsNullOrWhiteSpace(e.Payload) ? "点名" : $"点名：{e.Payload}";
                    kind = SessionBookmarkKind.Roster;
                    break;
                case TimelineEventKind.TimerEnded:
                    // 练习场景的倒计时结束单独标记，便于时间轴区分「练习结束」与普通倒计时。
                    if (IsPracticeTimerEnded(e.Payload))
                    {
                        name = "练习结束";
                        kind = SessionBookmarkKind.Practice;
                    }
                    else
                    {
                        name = "倒计时结束";
                        kind = SessionBookmarkKind.Timer;
                    }
                    break;
                default:
                    continue;
            }

            if (_suppressedAutoTimes.Contains(e.TimeMs)) continue;

            if (result.Count > 0 && e.TimeMs >= result[^1].TimeMs
                && e.TimeMs - result[^1].TimeMs < AutoBookmarkMergeWindowMs)
            {
                // 合并到上一书签：保留首个事件的时间，采用最新事件的名称与页码。
                result[^1].Name = name;
                result[^1].PageIndex = e.PageIndex;
                result[^1].Kind = kind;
                continue;
            }

            result.Add(new SessionBookmark
            {
                TimeMs = e.TimeMs,
                PageIndex = e.PageIndex,
                Name = name,
                Auto = true,
                Kind = kind,
            });
        }

        return result;
    }

    /// <summary>把自动书签同步进 <see cref="ClassroomSession.Bookmarks"/>，保留用户手动书签。</summary>
    public void ApplyAutoBookmarks()
    {
        var list = _session.Bookmarks ??= new List<SessionBookmark>();
        var manual = list.Where(b => b is not null && !b.Auto).ToList();
        var auto = AutoBookmarks();
        var manualTimes = new HashSet<long>(manual.Select(b => b.TimeMs));

        list.Clear();
        list.AddRange(manual);
        foreach (var bookmark in auto)
        {
            // 用户已在该时刻保留自定义书签时，不再生成同位的自动书签。
            if (!manualTimes.Contains(bookmark.TimeMs)) list.Add(bookmark);
        }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        if (_session.Version < 2) _session.Version = 2;
    }

    /// <summary>插入一个书签（按时间排序），返回其索引。</summary>
    public int AddBookmark(long timeMs, string name, string kind = SessionBookmarkKind.Manual, bool auto = false)
    {
        var list = _session.Bookmarks ??= new List<SessionBookmark>();
        timeMs = Math.Max(0, timeMs);
        var bookmark = new SessionBookmark
        {
            TimeMs = timeMs,
            PageIndex = PageAt(timeMs),
            Name = name ?? "",
            Auto = auto,
            Kind = kind ?? "",
        };

        int index = list.FindIndex(b => b is not null && b.TimeMs > timeMs);
        if (index < 0)
        {
            list.Add(bookmark);
            return list.Count - 1;
        }
        list.Insert(index, bookmark);
        return index;
    }

    /// <summary>重命名书签。用户重命名后该条转为手动书签，不再被自动规则覆盖。</summary>
    public bool RenameBookmark(int index, string name)
    {
        var list = _session.Bookmarks;
        if (list is null || index < 0 || index >= list.Count) return false;
        if (string.IsNullOrWhiteSpace(name)) return false;

        list[index].Name = name.Trim();
        list[index].Auto = false;
        list[index].Kind = SessionBookmarkKind.Manual;
        return true;
    }

    /// <summary>删除书签；删除自动书签后它会保持隐藏，直到时间轴重建。</summary>
    public bool RemoveBookmark(int index)
    {
        var list = _session.Bookmarks;
        if (list is null || index < 0 || index >= list.Count) return false;

        var bookmark = list[index];
        list.RemoveAt(index);
        if (bookmark.Auto) _suppressedAutoTimes.Add(bookmark.TimeMs);
        return true;
    }

    /// <summary>
    /// 书签摘要：取书签时刻（或之后最近）的事件类型 / 载荷，最多保留
    /// <paramref name="maxChars"/> 个字符；没有可用事件时回退到 <see cref="SessionBookmark.Kind"/>。
    /// </summary>
    public string BookmarkSummary(SessionBookmark bookmark, int maxChars = 20)
    {
        if (bookmark is null) return "";

        string text = EventSummaryAt(bookmark.TimeMs);
        if (string.IsNullOrWhiteSpace(text)) text = KindLabel(bookmark.Kind);
        text = (text ?? "").Trim();
        if (maxChars > 0 && text.Length > maxChars) text = text[..maxChars];
        return text;
    }

    /// <summary>书签在轨道上的完整说明：<c>时间 · 第 N 页 · 摘要</c>。</summary>
    public string DescribeBookmark(SessionBookmark bookmark, int maxChars = 20)
    {
        if (bookmark is null) return "";
        string time = TimeSpan.FromMilliseconds(bookmark.TimeMs).ToString(@"mm\:ss");
        int page = bookmark.PageIndex >= 0 ? bookmark.PageIndex + 1 : 1;
        string summary = BookmarkSummary(bookmark, maxChars);
        return string.IsNullOrEmpty(summary)
            ? $"{time} · 第 {page} 页"
            : $"{time} · 第 {page} 页 · {summary}";
    }

    private string EventSummaryAt(long timeMs)
    {
        int index = -1;
        for (int i = 0; i < _events.Count; i++)
        {
            if (_events[i].TimeMs >= timeMs) { index = i; break; }
        }
        if (index < 0) index = _events.Count - 1;
        if (index < 0) return "";
        return EventSummary(_events[index]);
    }

    private static string EventSummary(TimelineEvent e)
    {
        if (e is null) return "";
        switch (e.Kind)
        {
            case TimelineEventKind.PageChanged:
                return $"切换页 第 {e.PageIndex + 1} 页";
            case TimelineEventKind.Cleared:
                return "清空板书";
            case TimelineEventKind.PptSlideChanged:
                PptSlideChange change = DecodePptSlideChanged(e.Payload);
                return change.IsValid && change.Index > 0 ? $"PPT 第 {change.Index} 页" : "PPT 翻页";
            case TimelineEventKind.RosterDrawn:
                return string.IsNullOrWhiteSpace(e.Payload) ? "点名" : $"点名 {e.Payload}";
            case TimelineEventKind.TimerStarted:
                return "倒计时开始";
            case TimelineEventKind.TimerStopped:
                return "倒计时暂停";
            case TimelineEventKind.TimerEnded:
                return "倒计时结束";
            case TimelineEventKind.StrokeAdded:
                return "书写";
            case TimelineEventKind.StrokesRemoved:
                return "擦除";
            case TimelineEventKind.Undo:
                return "撤销";
            case TimelineEventKind.Redo:
                return "重做";
            case TimelineEventKind.BackgroundChanged:
                return string.IsNullOrWhiteSpace(e.Payload) ? "背景切换" : $"背景 {e.Payload}";
            case TimelineEventKind.ViewChanged:
                return "缩放/平移";
            case TimelineEventKind.BookmarkAdded:
                return "手动书签";
            case TimelineEventKind.AiStep:
                return "AI 助教";
            default:
                return "";
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        SessionBookmarkKind.Page => "切换页",
        SessionBookmarkKind.Cleared => "清空板书",
        SessionBookmarkKind.Ppt => "PPT 翻页",
        SessionBookmarkKind.Roster => "点名",
        SessionBookmarkKind.Timer => "倒计时",
        SessionBookmarkKind.Practice => "练习结束",
        SessionBookmarkKind.Manual => "手动书签",
        _ => "",
    };

    /// <summary>
    /// 判断倒计时结束事件是否属于练习场景：载荷为含 <c>practice:true</c> 的 JSON 对象，
    /// 或含 <c>scene</c> 等于 "practice"（不区分大小写），或文本含「练习结束」信号时返回 true；
    /// 非法 JSON / 空载荷返回 false，绝不抛异常。
    /// </summary>
    public static bool IsPracticeTimerEnded(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        // 兼容旧数据与时光机 PracticeFinished 信号：载荷文本直接带了结束说明。
        if (payload.Contains("练习结束", StringComparison.Ordinal)) return true;

        try
        {
            using var document = JsonDocument.Parse(payload.Trim());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "practice", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.True)
                    return true;
            }

            string scene = ReadString(root, "scene", "Scene");
            return string.Equals(scene, "practice", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, out int parsed) ? parsed : fallback;

    // ---- 视图载荷 -----------------------------------------------------------

    /// <summary>
    /// 构造 <see cref="TimelineEventKind.ViewChanged"/> 的载荷：
    /// <c>{"scale":1.5,"tx":120,"ty":80}</c>，键统一 camelCase。
    /// </summary>
    public static string EncodeViewChanged(double scale, double tx, double ty)
    {
        try
        {
            return JsonSerializer.Serialize(new ViewChangedDto
            {
                scale = double.IsFinite(scale) ? scale : 1,
                tx = double.IsFinite(tx) ? tx : 0,
                ty = double.IsFinite(ty) ? ty : 0,
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 <see cref="TimelineEventKind.ViewChanged"/> 载荷；无法识别时 IsValid 为 false。</summary>
    public static TimerViewState DecodeViewChanged(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new TimerViewState(1, 0, 0, false);
        try
        {
            using var document = JsonDocument.Parse(payload.Trim());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new TimerViewState(1, 0, 0, false);

            double scale = ReadDouble(root, "scale", "Scale");
            double tx = ReadDouble(root, "tx", "Tx");
            double ty = ReadDouble(root, "ty", "Ty");
            if (!double.IsFinite(scale)) scale = 1;
            if (!double.IsFinite(tx)) tx = 0;
            if (!double.IsFinite(ty)) ty = 0;
            return new TimerViewState(scale, tx, ty, true);
        }
        catch
        {
            return new TimerViewState(1, 0, 0, false);
        }
    }

    private sealed class ViewChangedDto
    {
        public double scale { get; set; } = 1;
        public double tx { get; set; }
        public double ty { get; set; }
    }

    private static double ReadDouble(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                return number;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                return parsed;
        }
        return double.NaN;
    }

    // ---- PPT 翻页载荷 -----------------------------------------------------

    /// <summary>
    /// 构造 <see cref="TimelineEventKind.PptSlideChanged"/> 的载荷：
    /// <c>{"slideId":123,"index":3,"screenshot":"C:\\...\\slide.png"}</c>，键统一 camelCase。
    /// <paramref name="screenshot"/> 为空表示该页没有可用截图。
    /// </summary>
    public static string EncodePptSlideChanged(int slideId, int index, string screenshot)
    {
        try
        {
            return JsonSerializer.Serialize(new PptSlideChangedDto
            {
                slideId = slideId,
                index = index,
                screenshot = screenshot ?? "",
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 <see cref="TimelineEventKind.PptSlideChanged"/> 载荷，兼容旧版纯序号（如 <c>"3"</c>）。
    /// 无法识别时返回 <see cref="PptSlideChange.IsValid"/> 为 false 的结果。
    /// </summary>
    public static PptSlideChange DecodePptSlideChanged(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new PptSlideChange(0, 0, "", false);
        string trimmed = payload.Trim();

        if (int.TryParse(trimmed, out int legacyIndex))
            return new PptSlideChange(0, legacyIndex, "", true);

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new PptSlideChange(0, 0, "", false);

            int slideId = ReadInt(root, "slideId", "SlideId");
            int index = ReadInt(root, "index", "Index");
            string screenshot = ReadString(root, "screenshot", "Screenshot") ?? "";
            return new PptSlideChange(slideId, index, screenshot, true);
        }
        catch
        {
            return new PptSlideChange(0, 0, "", false);
        }
    }

    private sealed class PptSlideChangedDto
    {
        public int slideId { get; set; }
        public int index { get; set; }
        public string screenshot { get; set; } = "";
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
                return parsed;
        }
        return 0;
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
        }
        return null;
    }

    /// <summary>
    /// 距离 <paramref name="timeMs"/> 最近且事件索引不超过目标时刻的检查点；没有则返回 null。
    /// <para>
    /// 时间相同的多个事件可能落在同一毫秒，因此同时用 <see cref="InkTimeline.IndexAt"/> 过滤
    /// <see cref="SessionCheckpoint.EventIndex"/>，避免检查点越过目标时刻（否则会重放多余事件）。
    /// </para>
    /// </summary>
    public SessionCheckpoint NearestCheckpoint(long timeMs)
    {
        int cutoff = IndexAt(timeMs);
        if (cutoff < 0) return null;

        SessionCheckpoint best = null;
        foreach (var cp in _session.Checkpoints)
        {
            if (cp is null || cp.EventIndex > cutoff || cp.TimeMs > timeMs) continue;
            if (best is null || cp.TimeMs > best.TimeMs
                || (cp.TimeMs == best.TimeMs && cp.EventIndex > best.EventIndex))
                best = cp;
        }
        return best;
    }

    /// <summary>在目标时刻生成一个检查点并返回它。</summary>
    public SessionCheckpoint AddCheckpoint(long timeMs)
    {
        EnsureRegistry();
        int cutoff = IndexAt(timeMs);
        var alive = new HashSet<string>();
        for (int i = 0; i <= cutoff; i++) ApplyEvent(alive, _events[i]);

        var objectAlive = new HashSet<string>();
        for (int i = 0; i <= cutoff; i++) ApplyObjectEvent(objectAlive, _events[i]);

        var checkpoint = new SessionCheckpoint
        {
            TimeMs = cutoff < 0 ? 0 : _events[cutoff].TimeMs,
            EventIndex = cutoff,
            Seq = cutoff < 0 ? 0 : _events[cutoff].Seq,
            PageIndex = PageAt(timeMs),
            Background = BackgroundAt(timeMs),
            LiveStrokeIds = alive.ToList(),
            LiveObjectIds = objectAlive.ToList(),
        };
        _session.Checkpoints.Add(checkpoint);
        // 同步增量游标，后续 EnsureCheckpoints 从该检查点继续。
        _checkpointAppliedIndex = cutoff + 1;
        _checkpointAlive.Clear();
        foreach (var id in alive) _checkpointAlive.Add(id);
        _checkpointObjectAlive.Clear();
        foreach (var id in objectAlive) _checkpointObjectAlive.Add(id);
        _checkpointLastTime = checkpoint.TimeMs;
        return checkpoint;
    }

    /// <summary>追加一个现成的检查点。</summary>
    public void AddCheckpoint(SessionCheckpoint checkpoint)
    {
        if (checkpoint is null) return;
        _session.Checkpoints ??= new List<SessionCheckpoint>();
        _session.Checkpoints.Add(checkpoint);
    }

    // ---- 事件应用 ---------------------------------------------------------

    private void ApplyEvent(HashSet<string> alive, TimelineEvent e)
    {
        switch (e.Kind)
        {
            case TimelineEventKind.StrokeAdded:
                if (!string.IsNullOrEmpty(e.StrokeId)) alive.Add(e.StrokeId);
                break;

            case TimelineEventKind.StrokesRemoved:
                foreach (var id in e.StrokeIds ?? Array.Empty<string>()) alive.Remove(id);
                break;

            case TimelineEventKind.Cleared:
                foreach (var id in alive.ToList())
                {
                    // 清空按页码删除笔画；用轻量页码索引判断，避免依赖尚未解析的缓存记录，
                    // 否则未解析的笔画会被漏删。
                    if (_strokePages.TryGetValue(id, out int strokePage) && strokePage == e.PageIndex)
                        alive.Remove(id);
                }
                break;

            case TimelineEventKind.Undo:
            case TimelineEventKind.Redo:
                foreach (var id in e.StrokeIds ?? Array.Empty<string>()) alive.Remove(id);
                foreach (var id in DecodeAddedIds(e.Payload)) alive.Add(id);
                break;

            case TimelineEventKind.AiStep:
                // AI 对象在独立注册表/覆盖层中维护，不参与笔画存活集合。
                break;
        }
    }

    /// <summary>按 add/replace/update/remove 增量维护 AI 对象存活集合。</summary>
    private void ApplyObjectEvent(HashSet<string> alive, TimelineEvent e)
    {
        if (e is null || e.Kind != TimelineEventKind.AiStep) return;
        if (!AiObjectPayloadCodec.TryDecode(e.Payload, out AiObjectsPayload payload)) return;

        bool remove = string.Equals(payload.Action, "remove", StringComparison.OrdinalIgnoreCase);
        foreach (TimelineAiObject item in payload.AllObjects)
        {
            if (string.IsNullOrEmpty(item.Id)) continue;
            if (remove) alive.Remove(item.Id);
            else alive.Add(item.Id);
        }
    }

    private HashSet<string> RebuildObjectAlive()
    {
        var alive = new HashSet<string>();
        foreach (TimelineEvent e in _events) ApplyObjectEvent(alive, e);
        return alive;
    }

    /// <summary>
    /// 最近一个包含对象状态的检查点。旧检查点（LiveObjectIds 为 null）会被跳过，
    /// 让对象回退到事件流起点重建，避免旧会话丢对象。
    /// </summary>
    private SessionCheckpoint NearestObjectCheckpoint(long timeMs)
    {
        int cutoff = IndexAt(timeMs);
        if (cutoff < 0) return null;

        SessionCheckpoint best = null;
        foreach (SessionCheckpoint cp in _session.Checkpoints)
        {
            if (cp is null || cp.EventIndex > cutoff || cp.TimeMs > timeMs) continue;
            if (cp.LiveObjectIds is null) continue;
            if (best is null || cp.TimeMs > best.TimeMs
                || (cp.TimeMs == best.TimeMs && cp.EventIndex > best.EventIndex))
                best = cp;
        }
        return best;
    }

    private void EnsureRegistry()
    {
        while (_registryIndex < _events.Count)
        {
            EnsureEventRegistered(_events[_registryIndex], _registryIndex);
            _registryIndex++;
        }
    }

    private void EnsureEventRegistered(TimelineEvent e, int index)
    {
        if (e is null) return;
        if (e.Kind == TimelineEventKind.StrokeAdded && !string.IsNullOrEmpty(e.StrokeId))
        {
            // 只登记页码与顺序，不解析 StrokeJson：长会话首次定位不再全量反序列化。
            if (_strokePages.ContainsKey(e.StrokeId)) return;
            _strokePages[e.StrokeId] = e.PageIndex;
            _strokeOrder[e.StrokeId] = index;
        }
        else if (e.Kind is TimelineEventKind.Undo or TimelineEventKind.Redo)
        {
            foreach (var (id, json) in DecodeAddedStrokes(e.Payload))
            {
                if (string.IsNullOrEmpty(id)) continue;

                bool known = _strokePages.ContainsKey(id);
                if (!known)
                {
                    _strokePages[id] = e.PageIndex;
                    _strokeOrder[id] = index;
                }

                // 撤销/重做重新出现的笔画：若此前从未登记（如形状识别替换），
                // 这里用事件携带的 JSON 补登记，保证回放完整。
                // 已有来源的笔画留给 BuildFrame 按需解析，避免重复反序列化。
                if (known || string.IsNullOrEmpty(json) || _registry.ContainsKey(id)) continue;
                InkStroke stroke = TryDeserialize(json);
                if (stroke is not null)
                    CacheStroke(id, stroke, _strokePages[id], _strokeOrder[id]);
            }
        }

        if (e.Kind == TimelineEventKind.AiStep &&
            AiObjectPayloadCodec.TryDecode(e.Payload, out AiObjectsPayload payload))
        {
            foreach (TimelineAiObject item in payload.AllObjects)
            {
                if (string.IsNullOrEmpty(item.Id)) continue;
                // 只登记首次出现顺序；页/板不在此固化，读取时以目标时刻快照为准。
                if (!_objectOrder.ContainsKey(item.Id))
                    _objectOrder[item.Id] = index;
            }
        }
    }

    /// <summary>
    /// 按需解析笔画：命中缓存直接返回；否则从首次登记的事件索引起查找可用的 JSON，
    /// 解析成功后写入缓存。载荷为空或解析失败时返回 null（调用方跳过，不抛异常）。
    /// </summary>
    private InkStroke ResolveStroke(string id)
    {
        if (_registry.TryGetValue(id, out StrokeRecord cached)) return cached.Stroke;
        if (!_strokeOrder.TryGetValue(id, out int start)) return null;

        for (int i = start; i < _events.Count; i++)
        {
            TimelineEvent e = _events[i];
            if (e is null) continue;

            string json;
            if (e.Kind == TimelineEventKind.StrokeAdded)
            {
                if (e.StrokeId != id) continue;
                json = e.StrokeJson;
            }
            else if (e.Kind is TimelineEventKind.Undo or TimelineEventKind.Redo)
            {
                json = FindAddedStrokeJson(e.Payload, id);
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(json)) continue;
            InkStroke stroke = TryDeserialize(json);
            if (stroke is null) continue;

            int page = _strokePages.TryGetValue(id, out int knownPage) ? knownPage : e.PageIndex;
            int order = _strokeOrder.TryGetValue(id, out int knownOrder) ? knownOrder : i;
            CacheStroke(id, stroke, page, order);
            return stroke;
        }
        return null;
    }

    /// <summary>
    /// 按需解析 AI 对象：从首次登记索引扫描到 <paramref name="upToIndex"/>，取该时刻之前的
    /// 最新对象快照。快照里的 PNG 直接来自录制事件，不做重新渲染或网络请求。
    /// </summary>
    private TimelineAiObject ResolveObjectAt(string id, int upToIndex)
    {
        if (!_objectOrder.TryGetValue(id, out int start)) return null;

        TimelineAiObject latest = null;
        int limit = Math.Min(upToIndex, _events.Count - 1);
        for (int i = start; i <= limit; i++)
        {
            TimelineEvent e = _events[i];
            if (e is null || e.Kind != TimelineEventKind.AiStep) continue;
            if (!AiObjectPayloadCodec.TryDecode(e.Payload, out AiObjectsPayload payload)) continue;

            foreach (TimelineAiObject item in payload.AllObjects)
                if (item.Id == id) latest = item;
        }

        return latest;
    }

    private static InkStroke TryDeserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return StrokeJsonCodec.Deserialize(json);
        }
        catch
        {
            return null;
        }
    }

    private static string FindAddedStrokeJson(string payload, string id)
    {
        foreach (var (addedId, json) in DecodeAddedStrokes(payload))
        {
            if (addedId == id && !string.IsNullOrEmpty(json)) return json;
        }
        return null;
    }

    private void CacheStroke(string id, InkStroke stroke, int pageIndex, int order)
    {
        _registry[id] = new StrokeRecord { Stroke = stroke, PageIndex = pageIndex, Order = order };
        _cacheQueue.Enqueue(id);
    }

    /// <summary>
    /// 缓存上限控制：仅淘汰不在 <paramref name="keepIds"/>（当前帧结果集）中的旧记录，
    /// 保证当前帧正在使用的笔画绝不会被移除。
    /// </summary>
    private void TrimCache(HashSet<string> keepIds)
    {
        if (_registry.Count <= MaxCachedStrokes) return;

        // 最多遍历一轮初始队列长度，避免被保护项占满队列时无限重排。
        int attempts = _cacheQueue.Count;
        while (attempts-- > 0 && _registry.Count > MaxCachedStrokes && _cacheQueue.Count > 0)
        {
            string id = _cacheQueue.Dequeue();
            if (keepIds.Contains(id) || !_registry.ContainsKey(id))
            {
                _cacheQueue.Enqueue(id);
                continue;
            }
            _registry.Remove(id);
        }
    }

    private void EnsureCheckpoints()
    {
        // 事件可能新增，先保证 registry 已覆盖，Cleared 应用时能查到页码。
        EnsureRegistry();
        while (_checkpointAppliedIndex < _events.Count)
        {
            int index = _checkpointAppliedIndex;
            ApplyEvent(_checkpointAlive, _events[index]);
            ApplyObjectEvent(_checkpointObjectAlive, _events[index]);
            _checkpointAppliedIndex++;

            long time = _events[index].TimeMs;
            if (_checkpointLastTime == long.MinValue) _checkpointLastTime = time;

            int sinceLast = _session.Checkpoints.Count == 0
                ? _checkpointAppliedIndex
                : _checkpointAppliedIndex - (LastCheckpointEventIndex() + 1);

            bool byCount = sinceLast >= _eventsPerCheckpoint;
            bool byTime = time - _checkpointLastTime >= _checkpointIntervalMs;
            if (byCount || byTime)
            {
                _session.Checkpoints.Add(new SessionCheckpoint
                {
                    TimeMs = time,
                    EventIndex = index,
                    Seq = _events[index].Seq,
                    PageIndex = PageAt(time),
                    Background = BackgroundAt(time),
                    LiveStrokeIds = _checkpointAlive.ToList(),
                    LiveObjectIds = _checkpointObjectAlive.ToList(),
                });
                _checkpointLastTime = time;
            }
        }
    }

    private int LastCheckpointEventIndex()
        => _session.Checkpoints.Count == 0 ? -1 : _session.Checkpoints[^1].EventIndex;


    // ---- Undo/Redo 载荷（重新出现的笔画）-------------------------------

    private sealed class AddedStrokeDto
    {
        public string Id { get; set; }
        public string Json { get; set; }
    }

    /// <summary>把重新出现的笔画（含数据）编码为 Undo/Redo 的 <see cref="TimelineEvent.Payload"/>。</summary>
    public static string EncodeAddedStrokes(IEnumerable<InkStroke> strokes)
    {
        if (strokes is null) return null;
        var dto = strokes.Select(s => new AddedStrokeDto { Id = s.Id.ToString(), Json = StrokeJsonCodec.Serialize(s) }).ToArray();
        return JsonSerializer.Serialize(dto);
    }

    /// <summary>仅编码 id 列表（调用方保证这些笔画已有 StrokeAdded 记录）。</summary>
    public static string EncodeAddedIds(IEnumerable<string> ids)
        => ids is null ? null : JsonSerializer.Serialize(ids.ToArray());

    /// <summary>解析 Undo/Redo 载荷。兼容纯 id 字符串数组与 {id,json} 对象数组。</summary>
    public static IReadOnlyList<(string Id, string Json)> DecodeAddedStrokes(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return Array.Empty<(string, string)>();
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<(string, string)>();
            var result = new List<(string, string)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    result.Add((element.GetString(), null));
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    string id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString()
                        : element.TryGetProperty("id", out var idProp2) ? idProp2.GetString() : null;
                    string json = element.TryGetProperty("Json", out var jsonProp) ? jsonProp.GetString()
                        : element.TryGetProperty("json", out var jsonProp2) ? jsonProp2.GetString() : null;
                    if (!string.IsNullOrEmpty(id)) result.Add((id, json));
                }
            }
            return result;
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    public static IReadOnlyList<string> DecodeAddedIds(string payload)
        => DecodeAddedStrokes(payload).Select(x => x.Id).ToList();
}