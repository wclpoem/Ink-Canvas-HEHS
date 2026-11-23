using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using System.Diagnostics;
using System.Numerics;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 笔画级录制器：订阅 <see cref="BoardService"/> 当前页控制器的笔画事件与切页 / 背景事件，
/// 生成可回放的 <see cref="ClassroomSession"/>。
/// <para>
/// 笔画添加 / 擦除 / 清空来自 <see cref="InkController"/> 的最小新增事件；
/// 撤销 / 重做没有独立事件，通过比较 <see cref="InkController.Invalidated"/> 前后的
/// 文档笔画 id 集合差异推导（<c>History</c> 的 Undo/Redo 计数用于区分撤销与重做）。
/// </para>
/// </summary>
public sealed class InkRecorder : IDisposable
{
    private const long SnapshotIntervalMs = 2_000;

    private readonly BoardService _board;
    private readonly int _boardId;
    private readonly bool _persistOnStop;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private InkController _controller;
    private ClassroomContext _aiContext;
    private HashSet<string> _knownIds = new();
    private int _lastUndoCount;
    private int _lastRedoCount;
    private InkTimeline _timeline;
    private SessionJournal _journal;
    private long _lastSnapshotTimestamp;
    private int _nextSeq = 1;

    // 暂停计时：ElapsedMs = 时钟已过 - (已累计暂停 + 当前暂停段)。
    private long _pausedTotalMs;
    private long _pauseStartedMs = -1;
    private const int DedupeSignaturePrefixLength = 64;


    /// <param name="boardId">画板归属：0 = 主展板，1 = 板中板。</param>
    /// <param name="persistOnStop">
    /// 停止时是否把会话保存为正式录制。子板录制由 Shell 合并进主会话，应传 <c>false</c>：
    /// 此时 <see cref="Stop"/> 只关闭增量日志并清理恢复产物，不额外落盘。
    /// </param>
    public InkRecorder(BoardService board, int boardId = 0, bool persistOnStop = true)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _boardId = boardId;
        _persistOnStop = persistOnStop;
    }

    public ClassroomSession Session { get; private set; }

    public bool IsRecording { get; private set; }

    /// <summary>录制已暂停：期间 <see cref="Append"/> 与自动事件全部丢弃，计时不计入暂停时长。</summary>
    public bool IsPaused { get; private set; }

    public InkTimeline Timeline => _timeline;

    /// <summary>
    /// 当前已捕获事件的只读副本（会话尚未创建时为空数组）。
    /// 返回副本，避免外部直接修改录制器内部的事件列表。
    /// </summary>
    public IReadOnlyList<TimelineEvent> CapturedEvents
        => Session?.Events?.ToArray() ?? Array.Empty<TimelineEvent>();

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

        Log(accepted is null
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
    /// </summary>
    public void MergeEvents(IEnumerable<TimelineEvent> events)
    {
        lock (_gate)
        {
            if (Session is null)
            {
                Log($"[AI] 合并子板事件被忽略：录制器没有会话，boardId={_boardId}");
                return;
            }

            List<TimelineEvent> incoming = events?.Where(item => item is not null).ToList();
            if (incoming is null || incoming.Count == 0) return;

            var merged = Session.Events ??= new List<TimelineEvent>();
            foreach (TimelineEvent ev in incoming)
            {
                // 归属统一按主会话重写，避免复合会话里残留子会话 id。
                ev.SessionId = Session.Id;
                merged.Add(ev);
                // 合并发生在主录制器停止之前：同步写入增量日志，异常恢复时也不丢子板事件。
                _journal?.Append(ev);
            }

            SortEventsByTimeStable(merged);
            RenumberEvents(merged);
            // 合并后事件索引整体位移，旧检查点已失效，必须清空重建。
            Session.Checkpoints?.Clear();
            _timeline = new InkTimeline(Session);
            MaybeSaveSnapshot();

            Log($"[AI] 子板事件已合并：boardId={_boardId},incoming={incoming.Count},total={merged.Count}");
        }
    }

    /// <summary>录制中用于异常恢复的增量日志；未启用时为 null。</summary>
    public SessionJournal Journal => _journal;

    /// <summary>笔画提交时的抽稀容差（DIP）。默认 0.8，设为 0 表示不抽稀。</summary>
    public double SimplificationTolerance { get; set; } = StrokeSimplifier.DefaultTolerance;

    /// <summary>会话 / 日志 / 快照目录；默认 <see cref="ClassroomSessionStore.DefaultDirectory"/>。</summary>
    public string SessionDirectory { get; set; }

    /// <summary>
    /// 当前录制时钟（毫秒，相对录制开始，已扣除暂停时长）。供外部事件源打时间戳。
    /// 暂停期间继续增长，恢复后不产生空档：恢复前暂停的增量会累加进扣除值。
    /// </summary>
    public long ElapsedMs
    {
        get
        {
            long now = _clock.ElapsedMilliseconds;
            long paused = _pausedTotalMs;
            if (IsPaused && _pauseStartedMs >= 0) paused += Math.Max(0, now - _pauseStartedMs);
            return Math.Max(0, now - paused);
        }
    }

    /// <summary>暂停录制。暂停/恢复本身不写入时间轴，仅记 Trace 日志。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRecording || IsPaused) return;
            IsPaused = true;
            _pauseStartedMs = _clock.ElapsedMilliseconds;
        }
        Log("板书录制已暂停（暂停期间事件不入时间轴）");
    }

    /// <summary>继续录制；暂停时长从 <see cref="ElapsedMs"/> 中扣除，时间轴保持连续。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!IsRecording || !IsPaused) return;
            long now = _clock.ElapsedMilliseconds;
            if (_pauseStartedMs >= 0) _pausedTotalMs += Math.Max(0, now - _pauseStartedMs);
            _pauseStartedMs = -1;
            IsPaused = false;
        }

        // 暂停期间产生的笔画不补录（需求：全部丢弃），但同步已知集合，
        // 避免恢复后的撤销/重做把它们误判为本次操作差异。
        if (_controller is not null)
            _knownIds = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        SyncHistoryCounts();

        Log("板书录制已继续（暂停时长已扣除）");
    }

    private static void Log(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响录制 */ }
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
        OpenJournal();

        _clock.Restart();
        IsRecording = true;
        IsPaused = false;
        _pausedTotalMs = 0;
        _pauseStartedMs = -1;
        _nextSeq = 1;

        Bind(_board.Current);

        AddEvent(TimelineEventKind.PageChanged, _board.CurrentIndex);
        AddEvent(TimelineEventKind.BackgroundChanged, _board.CurrentIndex, payload: _board.Background.ToString());
        SeedExistingStrokes();
        SaveSnapshot();

        _board.PageChanged += OnBoardPageChanged;
        _board.BackgroundChanged += OnBoardBackgroundChanged;
        _board.AiObjectsChanged += OnAiObjectsChanged;
    }

    /// <summary>
    /// 停止录制：刷盘增量日志，合并为正式会话 JSON，并删除日志与快照。
    /// 合并失败时保留日志，下次启动可由 <see cref="ClassroomSessionStore.LoadWithJournal"/> 恢复。
    /// </summary>
    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        IsPaused = false;
        _pauseStartedMs = -1;
        _clock.Stop();

        _board.PageChanged -= OnBoardPageChanged;
        _board.BackgroundChanged -= OnBoardBackgroundChanged;
        _board.AiObjectsChanged -= OnAiObjectsChanged;
        UnbindController();
        FinishJournal();
    }

    /// <summary>保存当前会话到会话目录，返回文件路径。</summary>
    public string Save(string directory = null)
        => Session is null ? null : ClassroomSessionStore.Save(Session, directory);

    private void OpenJournal()
    {
        if (Session is null) return;
        try
        {
            _journal = SessionJournal.Open(Session.Id, SessionDirectory, Session.Name, Session.CreatedAt);
        }
        catch
        {
            // 日志不可用时录制照常进行，只是失去异常恢复能力。
            _journal = null;
        }
    }

    private void FinishJournal()
    {
        var journal = _journal;
        _journal = null;
        if (journal is null) return;

        try { journal.Close(); }
        catch { /* 已尽量刷盘 */ }

        // 子板录制（persistOnStop=false）只关闭增量日志并清理恢复产物：
        // 事件由 Shell 合并进主会话并随主会话落盘，避免产生一份独立录制文件。
        if (!_persistOnStop)
        {
            try
            {
                ClassroomSessionStore.DeleteRecoveryArtifacts(Session.Id, SessionDirectory);
            }
            catch
            {
                // 清理失败不影响合并与主会话保存。
            }

            Log($"[AI] 子板录制不落盘：已关闭增量日志并清理恢复产物，session={Session.Id},boardId={_boardId}");
            return;
        }

        bool saved = false;
        try
        {
            ClassroomSessionStore.Save(Session, SessionDirectory);
            saved = true;
        }
        catch
        {
            // 保存失败时保留日志供恢复。
        }

        if (saved) ClassroomSessionStore.DeleteRecoveryArtifacts(Session.Id, SessionDirectory);
    }

    /// <summary>按画板归属过滤 AI 上下文：子板只匹配 boardId=1，其余只匹配 boardId=0。</summary>
    private ClassroomContext FilterAiContext(ClassroomContext context)
    {
        if (context is null) return null;
        bool matches = context.Kind == BoardCanvasKind.SideBoard ? _boardId == 1 : _boardId == 0;
        return matches ? context : null;
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

    private void SaveSnapshot()
    {
        if (Session is null) return;
        try
        {
            ClassroomSessionStore.SaveSnapshot(Session, SessionDirectory);
            _lastSnapshotTimestamp = Stopwatch.GetTimestamp();
        }
        catch
        {
            // 快照失败不影响录制。
        }
    }

    private void MaybeSaveSnapshot()
    {
        if (_journal is null || _lastSnapshotTimestamp == 0) return;
        long elapsed = (Stopwatch.GetTimestamp() - _lastSnapshotTimestamp) * 1000 / Stopwatch.Frequency;
        if (elapsed >= SnapshotIntervalMs) SaveSnapshot();
    }

    private string SerializeStroke(InkStroke stroke)
    {
        if (stroke is null) return null;
        if (!(SimplificationTolerance > 0)) return StrokeJsonCodec.Serialize(stroke);
        return StrokeJsonCodec.Serialize(StrokeSimplifier.Simplify(stroke, SimplificationTolerance));
    }

    private void SeedExistingStrokes()
    {
        for (int page = 0; page < _board.Pages.Count; page++)
        {
            foreach (var stroke in _board.Pages[page].Document.Strokes)
                AddEvent(TimelineEventKind.StrokeAdded, page, stroke.Id.ToString(), SerializeStroke(stroke));
        }
    }

    private void Bind(InkController controller)
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

    /// <summary>
    /// 视图缩放/平移：记录矩阵推导出的 scale=sqrt(|det|)、tx=M31、ty=M32。
    /// 暂停期间由 <see cref="AddEvent"/> 统一丢弃。
    /// </summary>
    private void OnViewChanged(Matrix3x2 transform)
    {
        if (!IsRecording || IsPaused) return;
        double scale = Math.Sqrt(Math.Abs(transform.GetDeterminant()));
        if (!double.IsFinite(scale)) scale = 1;
        AddEvent(TimelineEventKind.ViewChanged, _board.CurrentIndex,
            payload: InkTimeline.EncodeViewChanged(scale, transform.M31, transform.M32));
    }

    private void OnBoardPageChanged()
    {
        if (!IsRecording) return;
        Bind(_board.Current);
        AddEvent(TimelineEventKind.PageChanged, _board.CurrentIndex);
    }

    private void OnBoardBackgroundChanged()
    {
        if (!IsRecording) return;
        AddEvent(TimelineEventKind.BackgroundChanged, _board.CurrentIndex, payload: _board.Background.ToString());
    }

    private void OnStrokeCommitted(InkStroke stroke)
    {
        if (!IsRecording || IsPaused || stroke is null) return;
        string id = stroke.Id.ToString();
        _knownIds.Add(id);
        AddEvent(TimelineEventKind.StrokeAdded, _board.CurrentIndex, id, SerializeStroke(stroke));
        SyncHistoryCounts();
    }

    private void OnStrokesErased(IReadOnlyList<InkStroke> strokes)
    {
        if (!IsRecording || IsPaused || strokes is null || strokes.Count == 0) return;
        var ids = strokes.Select(s => s.Id.ToString()).ToList();
        foreach (var id in ids) _knownIds.Remove(id);
        AddEvent(TimelineEventKind.StrokesRemoved, _board.CurrentIndex, ids: ids);
        SyncHistoryCounts();
    }

    private void OnCleared()
    {
        if (!IsRecording || IsPaused) return;
        _knownIds.Clear();
        AddEvent(TimelineEventKind.Cleared, _board.CurrentIndex);
        SyncHistoryCounts();
    }

    /// <summary>撤销 / 重做：文档 id 集合的差异即本次操作的效果。</summary>
    private void OnControllerInvalidated()
    {
        if (!IsRecording || IsPaused || _controller is null) return;

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

        AddEvent(kind, _board.CurrentIndex, ids: removed,
            payload: replayStrokes.Count > 0 ? InkTimeline.EncodeAddedStrokes(replayStrokes) : null);

        _knownIds = current;
        SyncHistoryCounts();
    }

    private void SyncHistoryCounts()
    {
        if (_controller is null) return;
        _lastUndoCount = _controller.History.UndoCount;
        _lastRedoCount = _controller.History.RedoCount;
    }

    private void AddEvent(TimelineEventKind kind, int page,
        string strokeId = null, string strokeJson = null,
        IReadOnlyList<string> ids = null, string payload = null,
        int? boardId = null)
    {
        if (!IsRecording || IsPaused || Session is null) return;

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
        if (!IsRecording || IsPaused || change is null) return;
        int imageCount = change.Objects?.Count ?? 0;
        int textCount = change.Texts?.Count ?? 0;
        int formulaCount = change.Formulas?.Count ?? 0;
        if (imageCount + textCount + formulaCount == 0) return;

        int page = change.PageIndex >= 0 ? change.PageIndex : _board.CurrentIndex;

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

        ClassroomContext ai;
        lock (_gate) { ai = _aiContext; }

        // 记录 AI 请求的统一上下文元数据：幻灯片、内容来源与请求标识。
        // 未绑定上下文时按旧行为传 null（schema=3 但字段留空）。
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
            Log($"[AI] 板书回放对象超过 PNG 阈值，回放显示占位：id={image.Id}，"
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

    /// <summary>
    /// 线程安全地追加一个外部事件（PPT 翻页 / 点名 / 倒计时等）。
    /// 暂停期间丢弃；与尾部事件完全同签名（Kind + TimeMs + StrokeId + Payload 前 64 字符）时忽略；
    /// 事件时间早于最后一个事件时按时间插入，并重建检查点（增量游标只支持尾部追加）。
    /// 非相邻的重复事件是合法重放，予以保留。
    /// 标书签候选事件会自动补一条章节书签。
    /// </summary>
    public void Append(TimelineEvent ev)
    {
        if (ev is null || Session is null) return;

        lock (_gate)
        {
            if (!IsRecording || IsPaused) return;
            var events = Session.Events ??= new List<TimelineEvent>();

            // 归属与序号由录制器统一分配：外部事件源（PPT / 点名 / 倒计时）只需给出
            // 时间、页与载荷，禁止自行编号，避免同毫秒事件在恢复后失去稳定顺序。
            ev.SessionId = Session.Id;
            if (ev.Seq <= 0) ev.Seq = _nextSeq++;
            else if (ev.Seq >= _nextSeq) _nextSeq = ev.Seq + 1;

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

            // 增量日志在每次追加后立即进入缓冲，2 秒 / 50 个事件自动刷盘。
            _journal?.Append(ev);
            MaybeSaveSnapshot();
        }
    }

    private static bool SameSignature(TimelineEvent a, TimelineEvent b)
    {
        if (a.Kind != b.Kind || a.TimeMs != b.TimeMs) return false;
        if (!string.Equals(a.StrokeId, b.StrokeId, StringComparison.Ordinal)) return false;

        // AI 对象事件以 objectId+action 开头，但同一对象连续 update 仍可能前缀相同；
        // 这里按完整 payload 比较，避免 64 字符截断把合法事件误判为重复。
        if (a.Kind == TimelineEventKind.AiStep)
            return string.Equals(a.Payload ?? "", b.Payload ?? "", StringComparison.Ordinal);

        string pa = a.Payload ?? "";
        string pb = b.Payload ?? "";
        int la = Math.Min(pa.Length, DedupeSignaturePrefixLength);
        int lb = Math.Min(pb.Length, DedupeSignaturePrefixLength);
        return la == lb && string.CompareOrdinal(pa, 0, pb, 0, la) == 0;
    }

    private static bool IsBookmarkSource(TimelineEventKind kind)
        => kind is TimelineEventKind.PageChanged
            or TimelineEventKind.Cleared
            or TimelineEventKind.PptSlideChanged
            or TimelineEventKind.RosterDrawn
            or TimelineEventKind.TimerEnded;

    public void Dispose()
    {
        Stop();
        _controller = null;
    }
}