using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Numerics;

// 与 Core 契约同名但不同程序集的两套时间轴模型：本模块自带一份（含 Screenshot），
// 对 Shell 暴露的合并接口统一使用 Core 版本，这里用别名避免与本命名空间的类型冲突。
using CoreTimelineEvent = InkCanvas.Core.Classroom.TimelineEvent;
using CoreTimelineEventKind = InkCanvas.Core.Classroom.TimelineEventKind;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>
/// 笔画级录制器：订阅宿主暴露的活动控制器、画板、PPT 与会话信号，
/// 生成可回放的 <see cref="ClassroomSession"/>。
/// <para>
/// 笔画添加 / 擦除 / 清空来自 <see cref="InkController"/> 的最小新增事件；
/// 撤销 / 重做没有独立事件，通过比较 <see cref="InkController.Invalidated"/> 前后的
/// 文档笔画 id 集合差异推导（<c>History</c> 的 Undo/Redo 计数用于区分撤销与重做）。
/// </para>
/// <para>
/// AI 板书对象以 <see cref="TimelineEventKind.AiStep"/> 写入结构化快照：
/// 原生文本 / 公式走 <c>nativeObjects</c> 矢量回放，旧图片卡片保留 PNG 兼容字段；
/// 板中板 AI 对象随 <see cref="TimelineAiObject.BoardId"/> 归属，回放永不请求模型。
/// </para>
/// </summary>
public class TimeMachineRecorder : IDisposable
{
    private readonly ITimeMachineHost _host;
    private readonly int _boardId;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private InkController _controller;
    private HashSet<string> _knownIds = new();
    private int _lastUndoCount;
    private int _lastRedoCount;
    private InkTimeline _timeline;
    private ClassroomContext _aiContext;
    private int _nextSeq = 1;
    private bool _disposed;
    private readonly List<Task> _pendingCaptures = new();

    public TimeMachineRecorder(ITimeMachineHost host)
        : this(host, 0)
    {
    }

    protected TimeMachineRecorder(ITimeMachineHost host, int boardId)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _boardId = boardId;
    }

    public ClassroomSession Session { get; private set; }

    public bool IsRecording { get; private set; }

    public InkTimeline Timeline => _timeline;

    /// <summary>
    /// 当前已捕获事件的只读副本（Core 契约版本；会话尚未创建时为空数组）。
    /// 返回副本，避免外部直接修改录制器内部的事件列表。
    /// </summary>
    public IReadOnlyList<CoreTimelineEvent> CapturedEvents
        => Session?.Events?.Select(ToCoreEvent).ToArray() ?? Array.Empty<CoreTimelineEvent>();

    /// <summary>
    /// 绑定当前 AI 请求的统一上下文；<c>null</c> 表示清除。
    /// 按画板归属过滤：子板上下文只对 boardId=1 的录制器生效，
    /// 其余画布种类只对 boardId=0 的录制器生效；不匹配时按未绑定处理。
    /// </summary>
    public void SetAiContext(ClassroomContext context)
    {
        ClassroomContext accepted;
        lock (_gate)
        {
            accepted = FilterAiContext(context);
            if (ReferenceEquals(accepted, _aiContext)) return;
            _aiContext = accepted;
        }

        LogTrace(accepted is null
            ? $"[AI] 录制器 AI 上下文已清除：boardId={_boardId}"
            : $"[AI] 录制器绑定 AI 上下文：boardId={_boardId},kind={accepted.Kind},"
                + $"slide={accepted.SlideId?.ToString() ?? "none"},source={accepted.Source},"
                + $"request={accepted.RequestId ?? ""}");
    }

    /// <summary>
    /// 把另一路录制（子板）的事件并入当前会话：忽略 null 事件，按
    /// <see cref="TimelineEvent.TimeMs"/> 稳定排序后重建时间轴。
    /// 子板事件序号独立从 1 起，合并后按最终顺序重新编号，避免与主会话的
    /// <see cref="TimelineEvent.Seq"/> 冲突影响同毫秒先后与检查点锚定。
    /// 入参使用 Core 契约事件，内部转换为本模块模型后入列。
    /// </summary>
    public void MergeEvents(IEnumerable<CoreTimelineEvent> events)
    {
        lock (_gate)
        {
            if (Session is null)
            {
                LogTrace($"[AI] 合并子板事件被忽略：录制器没有会话，boardId={_boardId}");
                return;
            }

            List<TimelineEvent> incoming = events?
                .Where(item => item is not null)
                .Select(ToLocalEvent)
                .ToList();
            if (incoming is null || incoming.Count == 0) return;

            var merged = Session.Events ??= new List<TimelineEvent>();
            foreach (TimelineEvent ev in incoming)
            {
                // 归属统一按主会话重写，避免复合会话里残留子会话 id。
                ev.SessionId = Session.Id;
                merged.Add(ev);
            }

            SortEventsByTimeStable(merged);
            RenumberEvents(merged);
            // 合并后事件索引整体位移，旧检查点已失效，必须清空重建。
            Session.Checkpoints?.Clear();
            _timeline = new InkTimeline(Session);

            LogTrace($"[AI] 子板事件已合并：boardId={_boardId},incoming={incoming.Count},total={merged.Count}");
        }
    }

    /// <summary>笔画提交时的抽稀容差（DIP）。默认 0.8，设为 0 表示不抽稀。</summary>
    public double SimplificationTolerance { get; set; } = StrokeSimplifier.DefaultTolerance;

    /// <summary>画板关闭时触发（由服务侧决定停止并保存）。</summary>
    public event Action BoardClosed;

    /// <summary>当前录制时钟（毫秒，相对录制开始）。</summary>
    public long ElapsedMs => Math.Max(0, ClockMs);

    /// <summary>
    /// 尚未完成的截图采集任务；测试可在触发事件后 <c>await</c> 本属性，
    /// 等待截图路径回填到对应事件。
    /// </summary>
    public Task PendingCaptures => Task.WhenAll(_pendingCaptures);

    /// <summary>原始时钟读数（毫秒）；派生录制器可据此实现计时修正。</summary>
    protected long RawClockMs => _clock.ElapsedMilliseconds;

    /// <summary>锁对象；派生录制器写自身状态时复用，保证与事件追加同序。</summary>
    protected object SyncRoot => _gate;

    /// <summary>对外暴露的录制时钟（毫秒）；派生录制器可覆盖以扣除暂停时长。</summary>
    protected virtual long ClockMs => RawClockMs;

    /// <summary>是否丢弃本次自动事件；派生录制器覆盖为 true 时暂停期间不入时间轴。</summary>
    protected virtual bool DropEvents => false;

    /// <summary>派生录制器在开始录制后重置自身状态。</summary>
    protected virtual void OnRecordingStarted()
    {
    }

    /// <summary>派生录制器在停止录制后重置自身状态。</summary>
    protected virtual void OnRecordingStopped()
    {
    }

    /// <summary>开始录制。会在 t=0 记录初始页码、背景，并把已有笔画作为起点快照。</summary>
    public void Start(string name = null)
    {
        if (IsRecording) return;

        Session = new ClassroomSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? $"板书 {DateTime.Now:yyyy-MM-dd HH:mm}" : name,
            CreatedAt = DateTime.Now,
            Events = new List<TimelineEvent>(),
            Checkpoints = new List<SessionCheckpoint>(),
        };
        _timeline = new InkTimeline(Session);

        _clock.Restart();
        IsRecording = true;
        _nextSeq = 1;
        _pendingCaptures.Clear();
        OnRecordingStarted();

        BindController(_host.ActiveController);

        AddEvent(TimelineEventKind.PageChanged, CurrentPageIndex());
        AddEvent(TimelineEventKind.BackgroundChanged, CurrentPageIndex(), payload: CurrentBackgroundName());
        SeedExistingStrokes();

        var board = _host.Board;
        if (board is not null)
        {
            board.PageChanged += OnBoardPageChanged;
            board.BackgroundChanged += OnBoardBackgroundChanged;
            // 本地新增：板中板 / AI 板书整理的结构化回放依赖该事件。
            board.AiObjectsChanged += OnAiObjectsChanged;
        }

        var ppt = _host.Ppt;
        if (ppt is not null)
        {
            ppt.InkTransition += OnPptTransition;
            ppt.ScreenshotRequested += OnPptScreenshotRequested;
        }

        _host.ClassroomSignal += OnClassroomSignal;
        _host.ActiveControllerChanged += OnActiveControllerChanged;
        _host.BoardOpenChanged += OnBoardOpenChanged;
    }

    /// <summary>停止录制并解绑全部事件，返回已填充的会话。</summary>
    public ClassroomSession Stop()
    {
        if (!IsRecording) return Session;
        IsRecording = false;
        _clock.Stop();
        OnRecordingStopped();

        var board = _host.Board;
        if (board is not null)
        {
            board.PageChanged -= OnBoardPageChanged;
            board.BackgroundChanged -= OnBoardBackgroundChanged;
            board.AiObjectsChanged -= OnAiObjectsChanged;
        }

        var ppt = _host.Ppt;
        if (ppt is not null)
        {
            ppt.InkTransition -= OnPptTransition;
            ppt.ScreenshotRequested -= OnPptScreenshotRequested;
        }

        _host.ClassroomSignal -= OnClassroomSignal;
        _host.ActiveControllerChanged -= OnActiveControllerChanged;
        _host.BoardOpenChanged -= OnBoardOpenChanged;

        UnbindController();
        return Session;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _controller = null;
    }

    // ---- 事件绑定 ----------------------------------------------------------

    private void OnActiveControllerChanged()
    {
        if (!IsRecording) return;
        BindController(_host.ActiveController);
    }

    private void OnBoardOpenChanged()
    {
        if (!IsRecording) return;
        if (!_host.BoardOpen) BoardClosed?.Invoke();
    }

    private void BindController(InkController controller)
    {
        UnbindController();
        _controller = controller;
        if (_controller is not null)
        {
            _controller.StrokeCommitted += OnStrokeCommitted;
            _controller.StrokesErased += OnStrokesErased;
            _controller.Cleared += OnCleared;
            _controller.Invalidated += OnControllerInvalidated;
            _controller.ViewChanged += OnViewChanged;
            _knownIds = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        }
        else
        {
            _knownIds = new HashSet<string>();
        }
        SyncHistoryCounts();
    }

    private void UnbindController()
    {
        if (_controller is null) return;
        _controller.StrokeCommitted -= OnStrokeCommitted;
        _controller.StrokesErased -= OnStrokesErased;
        _controller.Cleared -= OnCleared;
        _controller.Invalidated -= OnControllerInvalidated;
        _controller.ViewChanged -= OnViewChanged;
    }

    /// <summary>派生录制器恢复录制后重同步已知笔画集合，避免撤销 / 重做误判。</summary>
    protected void ResyncKnownStrokes()
    {
        if (_controller is null) return;
        _knownIds = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        SyncHistoryCounts();
    }

    private void OnBoardPageChanged()
    {
        if (!IsRecording) return;
        BindController(_host.ActiveController);
        AddEvent(TimelineEventKind.PageChanged, CurrentPageIndex());
    }

    private void OnBoardBackgroundChanged()
    {
        if (!IsRecording) return;
        AddEvent(TimelineEventKind.BackgroundChanged, CurrentPageIndex(), payload: CurrentBackgroundName());
    }

    private void OnStrokeCommitted(InkStroke stroke)
    {
        if (!IsRecording || DropEvents || stroke is null) return;
        string id = stroke.Id.ToString();
        _knownIds.Add(id);
        AddEvent(TimelineEventKind.StrokeAdded, CurrentPageIndex(), id, SerializeStroke(stroke));
        SyncHistoryCounts();
    }

    private void OnStrokesErased(IReadOnlyList<InkStroke> strokes)
    {
        if (!IsRecording || DropEvents || strokes is null || strokes.Count == 0) return;
        var ids = strokes.Select(s => s.Id.ToString()).ToList();
        foreach (var id in ids) _knownIds.Remove(id);
        AddEvent(TimelineEventKind.StrokesRemoved, CurrentPageIndex(), ids: ids);
        SyncHistoryCounts();
    }

    private void OnCleared()
    {
        if (!IsRecording || DropEvents) return;
        _knownIds.Clear();
        AddEvent(TimelineEventKind.Cleared, CurrentPageIndex());
        SyncHistoryCounts();
    }

    /// <summary>撤销 / 重做：文档 id 集合的差异即本次操作的效果。</summary>
    private void OnControllerInvalidated()
    {
        if (!IsRecording || DropEvents || _controller is null) return;

        var current = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        if (current.SetEquals(_knownIds))
        {
            SyncHistoryCounts();
            return;
        }

        var removed = _knownIds.Where(id => !current.Contains(id)).ToList();
        var added = current.Where(id => !_knownIds.Contains(id)).ToList();

        bool isUndo = _controller.History.RedoCount > _lastRedoCount;
        bool isRedo = !isUndo && _controller.History.UndoCount > _lastUndoCount;
        var kind = isUndo ? TimelineEventKind.Undo
            : isRedo ? TimelineEventKind.Redo
            : (added.Count > 0 ? TimelineEventKind.Redo : TimelineEventKind.Undo);

        var addedSet = new HashSet<string>(added);
        var addedStrokes = _controller.Document.Strokes
            .Where(s => addedSet.Contains(s.Id.ToString()))
            .ToList();

        // 撤销/重做重新出现的笔画与 StrokeAdded 一样先抽稀，避免回放体积反弹。
        var replayStrokes = addedStrokes
            .Select(s => SimplificationTolerance > 0
                ? StrokeSimplifier.Simplify(s, SimplificationTolerance)
                : s)
            .ToList();

        AddEvent(kind, CurrentPageIndex(), ids: removed,
            payload: replayStrokes.Count > 0 ? InkTimeline.EncodeAddedStrokes(replayStrokes) : null);

        _knownIds = current;
        SyncHistoryCounts();
    }

    /// <summary>视图缩放/平移：记录矩阵推导出的 scale=sqrt(|det|)、tx=M31、ty=M32。</summary>
    private void OnViewChanged(Matrix3x2 transform)
    {
        if (!IsRecording || DropEvents) return;
        double scale = Math.Sqrt(Math.Abs(transform.GetDeterminant()));
        if (!double.IsFinite(scale)) scale = 1;
        AddEvent(TimelineEventKind.ViewChanged, CurrentPageIndex(),
            payload: InkTimeline.EncodeViewChanged(scale, transform.M31, transform.M32));
    }

    private void OnPptTransition(PptInkTransition transition)
    {
        if (!IsRecording || DropEvents || transition is null) return;
        var slide = transition.Current?.Slide;
        if (slide is null) return;

        var ev = new TimelineEvent
        {
            PageIndex = CurrentPageIndex(),
            Kind = TimelineEventKind.PptSlideChanged,
            StrokeId = slide.SlideId.ToString(),
            Payload = InkTimeline.EncodePptSlideChanged(slide.SlideId, slide.Index, ""),
        };
        Append(ev);
        RequestCapture(ev);
    }

    private void OnPptScreenshotRequested(PptScreenshotRequest request)
    {
        if (!IsRecording || DropEvents || request is null) return;
        var slide = request.InkSlide ?? request.Snapshot?.Slide;
        if (slide is null) return;

        var ev = new TimelineEvent
        {
            PageIndex = CurrentPageIndex(),
            Kind = TimelineEventKind.PptSlideChanged,
            StrokeId = slide.SlideId.ToString(),
            Payload = InkTimeline.EncodePptSlideChanged(slide.SlideId, slide.Index, ""),
        };
        Append(ev);
        RequestCapture(ev);
    }

    /// <summary>
    /// 异步采集事件时刻的课堂截图，回填到事件的 <see cref="TimelineEvent.Screenshot"/>；
    /// PPT 事件同时把截图路径写回课件载荷，保持旧回放链路兼容。采集失败或返回空路径时
    /// 事件保留无截图，不阻断录制。
    /// </summary>
    private void RequestCapture(TimelineEvent ev)
    {
        if (ev is null) return;
        _pendingCaptures.Add(CaptureAsync(ev));
    }

    private async Task CaptureAsync(TimelineEvent ev)
    {
        string path;
        try
        {
            path = await _host.CaptureScreenshotAsync();
        }
        catch
        {
            return; // 截图失败：该事件回退无截图，不阻断录制。
        }

        if (!IsRecording || string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            ev.Screenshot = path;
            if (ev.Kind == TimelineEventKind.PptSlideChanged)
            {
                var change = InkTimeline.DecodePptSlideChanged(ev.Payload);
                if (change.IsValid)
                    ev.Payload = InkTimeline.EncodePptSlideChanged(change.SlideId, change.Index, path);
            }
        }
    }

    private void OnClassroomSignal(ClassroomSignal signal)
    {
        if (!IsRecording || DropEvents || signal is null) return;
        switch (signal.Kind)
        {
            case ClassroomSignalKind.RosterDrawn:
                Append(new TimelineEvent
                {
                    PageIndex = CurrentPageIndex(),
                    Kind = TimelineEventKind.RosterDrawn,
                    Payload = signal.Label,
                });
                break;
            case ClassroomSignalKind.TimerStarted:
                Append(new TimelineEvent
                {
                    PageIndex = CurrentPageIndex(),
                    Kind = TimelineEventKind.TimerStarted,
                    Payload = signal.Label,
                });
                break;
            case ClassroomSignalKind.TimerStopped:
                Append(new TimelineEvent
                {
                    PageIndex = CurrentPageIndex(),
                    Kind = TimelineEventKind.TimerStopped,
                    Payload = signal.Label,
                });
                break;
            case ClassroomSignalKind.TimerEnded:
            {
                var timerEnded = new TimelineEvent
                {
                    PageIndex = CurrentPageIndex(),
                    Kind = TimelineEventKind.TimerEnded,
                    Payload = string.IsNullOrWhiteSpace(signal.Label) ? "倒计时结束" : signal.Label,
                };
                Append(timerEnded);
                // 倒计时结束与点名同为自动书签来源；按规范要求，结束时刻补一张课堂截图。
                RequestCapture(timerEnded);
                break;
            }
            case ClassroomSignalKind.PracticeFinished:
            {
                var practiceEnded = new TimelineEvent
                {
                    PageIndex = CurrentPageIndex(),
                    Kind = TimelineEventKind.TimerEnded,
                    // 保留「练习结束」载荷，供自动书签识别为 practice 类别。
                    Payload = string.IsNullOrWhiteSpace(signal.Label) ? "练习结束" : signal.Label,
                };
                Append(practiceEnded);
                RequestCapture(practiceEnded);
                break;
            }
        }
    }

    // ---- 事件追加 ----------------------------------------------------------

    private int CurrentPageIndex() => _host.Board?.CurrentIndex ?? 0;

    private string CurrentBackgroundName() => _host.Board?.Background.ToString() ?? "Black";

    /// <summary>按画板归属过滤 AI 上下文：子板只匹配 boardId=1，其余只匹配 boardId=0。</summary>
    private ClassroomContext FilterAiContext(ClassroomContext context)
    {
        if (context is null) return null;
        bool matches = context.Kind == BoardCanvasKind.SideBoard ? _boardId == 1 : _boardId == 0;
        return matches ? context : null;
    }

    /// <summary>
    /// 取本录制器可用的 AI 上下文：优先使用 Shell 显式绑定的上下文；
    /// 未绑定时回退宿主当前上下文，并同样按画板归属过滤。
    /// </summary>
    private ClassroomContext ResolveAiContext()
    {
        lock (_gate)
        {
            if (_aiContext is not null) return _aiContext;
            return FilterAiContext(_host.AiContext);
        }
    }

    /// <summary>按 <see cref="TimelineEvent.TimeMs"/> 稳定排序：同毫秒保持入列先后。</summary>
    private static void SortEventsByTimeStable(List<TimelineEvent> events)
    {
        var indexed = events.Select((ev, index) => (Event: ev, Index: index)).ToList();
        indexed.Sort((left, right) =>
        {
            int byTime = left.Event.TimeMs.CompareTo(right.Event.TimeMs);
            return byTime != 0 ? byTime : left.Index.CompareTo(right.Index);
        });
        for (int i = 0; i < indexed.Count; i++) events[i] = indexed[i].Event;
    }

    /// <summary>合并后按最终顺序重新编号，保证 <see cref="TimelineEvent.Seq"/> 会话内唯一递增。</summary>
    private void RenumberEvents(List<TimelineEvent> events)
    {
        for (int i = 0; i < events.Count; i++) events[i].Seq = i + 1;
        _nextSeq = events.Count + 1;
    }

    /// <summary>本模块模型 → Core 契约模型；两套枚举成员一一对应，其余字段同名对齐。</summary>
    private static CoreTimelineEvent ToCoreEvent(TimelineEvent source) => new()
    {
        SessionId = source.SessionId,
        Seq = source.Seq,
        TimeMs = source.TimeMs,
        PageIndex = source.PageIndex,
        BoardId = source.BoardId,
        PptSlideId = source.PptSlideId,
        Kind = (CoreTimelineEventKind)(int)source.Kind,
        StrokeId = source.StrokeId,
        StrokeJson = source.StrokeJson,
        StrokeIds = source.StrokeIds,
        Payload = source.Payload,
    };

    /// <summary>Core 契约模型 → 本模块模型；Screenshot 仅模块侧存在，合并场景（子板）无截图。</summary>
    private static TimelineEvent ToLocalEvent(CoreTimelineEvent source) => new()
    {
        SessionId = source.SessionId,
        Seq = source.Seq,
        TimeMs = source.TimeMs,
        PageIndex = source.PageIndex,
        BoardId = source.BoardId,
        PptSlideId = source.PptSlideId,
        Kind = (TimelineEventKind)(int)source.Kind,
        StrokeId = source.StrokeId,
        StrokeJson = source.StrokeJson,
        StrokeIds = source.StrokeIds,
        Payload = source.Payload,
    };

    private void SyncHistoryCounts()
    {
        if (_controller is null) return;
        _lastUndoCount = _controller.History.UndoCount;
        _lastRedoCount = _controller.History.RedoCount;
    }

    private string SerializeStroke(InkStroke stroke)
    {
        if (stroke is null) return null;
        if (!(SimplificationTolerance > 0)) return StrokeJsonCodec.Serialize(stroke);
        return StrokeJsonCodec.Serialize(StrokeSimplifier.Simplify(stroke, SimplificationTolerance));
    }

    private void SeedExistingStrokes()
    {
        var board = _host.Board;
        if (board is null) return;
        for (int page = 0; page < board.Pages.Count; page++)
        {
            foreach (var stroke in board.Pages[page].Document.Strokes)
                AddEvent(TimelineEventKind.StrokeAdded, page, stroke.Id.ToString(), SerializeStroke(stroke));
        }
    }

    private void AddEvent(TimelineEventKind kind, int page,
        string strokeId = null, string strokeJson = null,
        IReadOnlyList<string> ids = null, string payload = null,
        int? boardId = null)
    {
        if (!IsRecording || DropEvents || Session is null) return;

        Append(new TimelineEvent
        {
            TimeMs = ElapsedMs,
            PageIndex = page,
            BoardId = boardId ?? _boardId,
            Kind = kind,
            StrokeId = strokeId,
            StrokeJson = strokeJson,
            StrokeIds = ids,
            Payload = payload,
        });
    }

    /// <summary>
    /// AI 板书对象变化：replace 先补写既有 StrokesRemoved（删除原笔画），再写一条 AiStep。
    /// 原生文本/公式写入 <c>nativeObjects</c> 结构化快照（不含 PNG）；旧图片卡片仍保留 PNG
    /// 兼容字段，超过 400 KB 时只记录 renderMissing=true 并告警。回放永不请求模型。
    /// </summary>
    private void OnAiObjectsChanged(BoardAiObjectChange change)
    {
        if (!IsRecording || DropEvents || change is null) return;
        int imageCount = change.Objects?.Count ?? 0;
        int textCount = change.Texts?.Count ?? 0;
        int formulaCount = change.Formulas?.Count ?? 0;
        if (imageCount + textCount + formulaCount == 0) return;

        int page = change.PageIndex >= 0 ? change.PageIndex : CurrentPageIndex();

        if (change.RemovedStrokes is { Count: > 0 })
        {
            var removedIds = change.RemovedStrokes
                .Where(stroke => stroke is not null)
                .Select(stroke => stroke.Id.ToString())
                .ToList();
            if (removedIds.Count > 0)
            {
                foreach (string id in removedIds) _knownIds.Remove(id);
                AddEvent(TimelineEventKind.StrokesRemoved, page, ids: removedIds);
            }
        }

        var images = change.Objects?
            .Where(image => image is not null)
            .Select(image => ToTimelineObject(image, page))
            .ToList() ?? new List<TimelineAiObject>();
        var natives = new List<TimelineAiObject>();
        foreach (BoardTextObject text in change.Texts ?? Array.Empty<BoardTextObject>())
            if (text is not null) natives.Add(ToNativeTextObject(text, page));
        foreach (BoardFormulaObject formula in change.Formulas ?? Array.Empty<BoardFormulaObject>())
            if (formula is not null) natives.Add(ToNativeFormulaObject(formula, page));
        if (images.Count + natives.Count == 0) return;

        ClassroomContext ai = ResolveAiContext();

        // 记录 AI 请求的统一上下文元数据：幻灯片、内容来源与请求标识。
        // 未绑定（且宿主未提供）上下文时按旧行为传 null（schema=3 但字段留空）。
        string payload = AiObjectPayloadCodec.Encode(
            change.Action, page, _boardId, images, natives,
            ai?.SlideId, ai?.Source.ToString(), ai?.RequestId);
        if (string.IsNullOrEmpty(payload)) return;
        AddEvent(TimelineEventKind.AiStep, page, payload: payload);
    }

    private static TimelineAiObject ToNativeTextObject(BoardTextObject text, int pageIndex)
    {
        string content = text.Content ?? "";
        return new TimelineAiObject
        {
            Id = text.Id.ToString(),
            Kind = string.IsNullOrWhiteSpace(text.Type) ? "text" : text.Type,
            IsNative = true,
            PageIndex = pageIndex,
            Left = text.Left,
            Top = text.Top,
            Width = text.Width,
            Height = text.Height,
            LayerOrder = text.LayerOrder,
            Transform = text.TransformMatrix is { Length: 6 } ? text.TransformMatrix.ToArray() : null,
            Content = content,
            Title = text.Title ?? "",
            Author = text.Author ?? "",
            Dynasty = text.Dynasty ?? "",
            FontFamily = text.FontFamily ?? "Microsoft YaHei UI",
            FontSize = text.FontSize,
            Color = text.Color ?? "#FF202024",
            LineHeight = text.LineHeight,
            Align = text.Align ?? "left",
            Columns = text.Columns <= 0 ? 1 : Math.Clamp(text.Columns, 1, 3),
            GroupId = text.GroupId ?? "",
            Text = content,
            Lines = content.Length == 0
                ? Array.Empty<string>()
                : content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'),
        };
    }

    private static TimelineAiObject ToNativeFormulaObject(BoardFormulaObject formula, int pageIndex) => new()
    {
        Id = formula.Id.ToString(),
        Kind = "formula",
        IsNative = true,
        PageIndex = pageIndex,
        Left = formula.Left,
        Top = formula.Top,
        Width = formula.Width,
        Height = formula.Height,
        LayerOrder = formula.LayerOrder,
        Transform = formula.TransformMatrix is { Length: 6 } ? formula.TransformMatrix.ToArray() : null,
        Latex = formula.Latex ?? "",
        FontFamily = formula.FontFamily ?? "Cambria Math",
        FontSize = formula.FontSize,
        Color = formula.Color ?? "#FF202024",
        GroupId = formula.GroupId ?? "",
    };

    private static TimelineAiObject ToTimelineObject(BoardImageAsset image, int pageIndex)
    {
        AiPasteMetadata metadata = AiPasteMetadata.TryParse(image.Metadata);
        string kind = metadata?.Kind.ToString() ?? "image";

        bool tooLarge = image.Data is { Length: > AiObjectPayloadCodec.MaxRenderPngBytes };
        string png = "";
        if (!tooLarge && image.Data is { Length: > 0 })
            png = Convert.ToBase64String(image.Data);
        if (tooLarge)
        {
            LogTrace($"[AI] 板书回放对象超过 PNG 阈值，回放显示占位：id={image.Id}，"
                + $"bytes={image.Data.Length}，limit={AiObjectPayloadCodec.MaxRenderPngBytes}");
        }

        return new TimelineAiObject
        {
            Id = image.Id.ToString(),
            Kind = kind,
            PageIndex = pageIndex,
            BoardId = 0,
            Left = image.Left,
            Top = image.Top,
            Width = image.Width,
            Height = image.Height,
            LayerOrder = image.LayerOrder,
            Transform = image.Transform is { Length: 6 } ? image.Transform.ToArray() : null,
            Latex = metadata?.Latex ?? "",
            Text = metadata?.Text ?? "",
            Lines = metadata is null ? Array.Empty<string>() : metadata.Lines,
            RenderPngBase64 = png,
            RenderMissing = tooLarge || string.IsNullOrEmpty(png),
            Notes = metadata is null ? Array.Empty<string>() : metadata.Notes,
        };
    }

    private static void LogTrace(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响录制 */ }
    }

    /// <summary>
    /// 线程安全地追加一个外部事件（PPT 翻页 / 点名 / 倒计时等）。与尾部事件的归属和完整内容
    /// 相同时忽略；事件时间早于最后一个事件时按时间插入，并重建检查点。
    /// 标书签候选事件会自动补一条章节书签。
    /// </summary>
    public void Append(TimelineEvent ev)
    {
        if (ev is null || Session is null) return;

        lock (_gate)
        {
            if (!IsRecording || DropEvents) return;
            var events = Session.Events ??= new List<TimelineEvent>();

            // 归属与序号由录制器统一分配：外部事件源（PPT / 点名 / 倒计时）只需给出
            // 时间、页与载荷，禁止自行编号，避免同毫秒事件在恢复后失去稳定顺序。
            ev.SessionId = Session.Id;
            if (ev.Seq <= 0) ev.Seq = _nextSeq++;
            else if (ev.Seq >= _nextSeq) _nextSeq = ev.Seq + 1;

            if (ev.TimeMs <= 0) ev.TimeMs = ElapsedMs;

            if (ev.PptSlideId <= 0 && ev.Kind == TimelineEventKind.PptSlideChanged)
            {
                PptSlideChange change = InkTimeline.DecodePptSlideChanged(ev.Payload);
                if (change.IsValid && change.SlideId > 0) ev.PptSlideId = change.SlideId;
            }

            // 相邻完全重复事件（同一次手势/切换被多次上报）不再入时间轴。
            if (events.Count > 0 && SameSignature(events[^1], ev)) return;

            if (events.Count > 0 && ev.TimeMs < events[^1].TimeMs)
            {
                int index = 0;
                while (index < events.Count && events[index].TimeMs <= ev.TimeMs) index++;
                events.Insert(index, ev);
                Session.Checkpoints.Clear();
                _timeline = new InkTimeline(Session);
            }
            else
            {
                events.Add(ev);
            }

            if (IsBookmarkSource(ev.Kind)) _timeline?.ApplyAutoBookmarks();
        }
    }

    private static bool SameSignature(TimelineEvent a, TimelineEvent b)
    {
        if (a.Kind != b.Kind || a.TimeMs != b.TimeMs || a.PageIndex != b.PageIndex || a.PptSlideId != b.PptSlideId) return false;
        if (!string.Equals(a.StrokeId, b.StrokeId, StringComparison.Ordinal)) return false;

        // AI 对象事件以 objectId+action 开头，但同一对象连续 update 仍可能前缀相同；
        // 这里按完整 payload 比较，避免 64 字符截断把合法事件误判为重复。
        if (a.Kind == TimelineEventKind.AiStep)
            return string.Equals(a.Payload ?? "", b.Payload ?? "", StringComparison.Ordinal);

        return string.Equals(a.StrokeJson, b.StrokeJson, StringComparison.Ordinal)
            && string.Equals(a.Payload, b.Payload, StringComparison.Ordinal)
            && string.Equals(a.Screenshot, b.Screenshot, StringComparison.Ordinal)
            && (a.StrokeIds ?? Array.Empty<string>()).SequenceEqual(b.StrokeIds ?? Array.Empty<string>());
    }

    private static bool IsBookmarkSource(TimelineEventKind kind)
        => kind is TimelineEventKind.PageChanged
            or TimelineEventKind.Cleared
            or TimelineEventKind.PptSlideChanged
            or TimelineEventKind.RosterDrawn
            or TimelineEventKind.TimerEnded;
}

/// <summary>
/// 本地兼容录制器：保留暂停 / 继续与新格式保存能力，供既有 <see cref="BoardService"/> 调用方使用。
/// 云端插件链路使用 <see cref="TimeMachineRecorder"/> + <see cref="ITimeMachineHost"/>。
/// </summary>
public sealed class InkRecorder : TimeMachineRecorder
{
    private long _pausedTotalMs;
    private long _pauseStartedMs = -1;

    public InkRecorder(BoardService board, int boardId = 0)
        : base(new BoardOnlyTimeMachineHost(board), boardId)
    {
    }

    /// <summary>录制已暂停：期间自动事件全部丢弃，计时不计入暂停时长。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>会话目录；默认为主机录制目录。</summary>
    public string SessionDirectory { get; set; }

    protected override bool DropEvents => IsPaused;

    protected override void OnRecordingStarted()
    {
        IsPaused = false;
        _pausedTotalMs = 0;
        _pauseStartedMs = -1;
    }

    protected override void OnRecordingStopped()
    {
        IsPaused = false;
        _pauseStartedMs = -1;
    }

    protected override long ClockMs
    {
        get
        {
            long now = RawClockMs;
            long paused = _pausedTotalMs;
            if (IsPaused && _pauseStartedMs >= 0) paused += Math.Max(0, now - _pauseStartedMs);
            return Math.Max(0, now - paused);
        }
    }

    /// <summary>暂停录制。暂停/恢复本身不写入时间轴，仅记 Trace 日志。</summary>
    public void Pause()
    {
        lock (SyncRoot)
        {
            if (!IsRecording || IsPaused) return;
            IsPaused = true;
            _pauseStartedMs = RawClockMs;
        }
        LogTrace("[AI] 板书录制已暂停（暂停期间事件不入时间轴）");
    }

    /// <summary>继续录制；暂停时长从 <see cref="TimeMachineRecorder.ElapsedMs"/> 中扣除，时间轴保持连续。</summary>
    public void Resume()
    {
        lock (SyncRoot)
        {
            if (!IsRecording || !IsPaused) return;
            long now = RawClockMs;
            if (_pauseStartedMs >= 0) _pausedTotalMs += Math.Max(0, now - _pauseStartedMs);
            _pauseStartedMs = -1;
            IsPaused = false;
        }

        // 暂停期间产生的笔画不补录（需求：全部丢弃），但同步已知集合，
        // 避免恢复后的撤销/重做把它们误判为本次操作差异。
        ResyncKnownStrokes();
        LogTrace("[AI] 板书录制已继续（暂停时长已扣除）");
    }

    /// <summary>保存当前会话为新格式录制容器，返回文件路径。</summary>
    public string Save(string directory = null)
    {
        if (Session is null) return null;
        string target = directory ?? SessionDirectory;
        return RecordingPackage.Save(Session, string.IsNullOrWhiteSpace(target) ? null : target);
    }

    private static void LogTrace(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响录制 */ }
    }
}

/// <summary>
/// 仅暴露 <see cref="BoardService"/> 的最小主机适配，让本地调用方继续以
/// <c>new InkRecorder(board)</c> 构造录制器；PPT 截图 / 课堂信号等能力由插件宿主链路提供。
/// </summary>
internal sealed class BoardOnlyTimeMachineHost : ITimeMachineHost
{
    public BoardOnlyTimeMachineHost(BoardService board)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
    }

    public Window Owner => null;

    public int Theme => 0;

    public BoardService Board { get; }

    public InkController ActiveController => Board.Current;

    public bool BoardOpen => true;

    public PptInkSession Ppt => null;

    public string DataDirectory => null;

    public event Action ActiveControllerChanged
    {
        add => Board.PageChanged += value;
        remove => Board.PageChanged -= value;
    }

    public event Action BoardOpenChanged
    {
        add { }
        remove { }
    }

    public event Action<ClassroomSignal> ClassroomSignal
    {
        add { }
        remove { }
    }

    public Task<string> CaptureScreenshotAsync() => Task.FromResult<string>(null);

    public Task NotifyAsync(string title, string message) => Task.CompletedTask;

    public void ConfigureWindow(Window window, int width = 1120, int height = 820)
    {
    }
}