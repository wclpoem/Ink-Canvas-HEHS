namespace InkCanvas.Modules.TimeMachine;

/// <summary>笔画级事件类型。板书时光机按这些事件重建任意时刻的画面。</summary>
public enum TimelineEventKind
{
    StrokeAdded,
    StrokesRemoved,
    Cleared,
    PageChanged,
    BackgroundChanged,
    Undo,
    Redo,

    /// <summary>PPT 放映翻页；<see cref="TimelineEvent.Payload"/> 存幻灯片序号（1 起）。</summary>
    PptSlideChanged,

    /// <summary>PPT 幻灯片批注变化；<see cref="TimelineEvent.Payload"/> 存当前批注笔画数。</summary>
    PptInkChanged,

    /// <summary>随机点名抽中；<see cref="TimelineEvent.Payload"/> 存姓名。</summary>
    RosterDrawn,

    /// <summary>倒计时开始 / 继续；<see cref="TimelineEvent.Payload"/> 存剩余毫秒。</summary>
    TimerStarted,

    /// <summary>倒计时暂停 / 停止；<see cref="TimelineEvent.Payload"/> 存剩余毫秒。</summary>
    TimerStopped,

    /// <summary>倒计时归零结束。</summary>
    TimerEnded,

    /// <summary>用户手动添加的章节书签。</summary>
    BookmarkAdded,

    /// <summary>
    /// 视图缩放 / 平移变化；<see cref="TimelineEvent.Payload"/> 存
    /// <c>{"scale":1.5,"tx":120,"ty":80}</c>（scale 由矩阵行列式推导）。
    /// </summary>
    ViewChanged,

    /// <summary>
    /// AI 助教过程事件（公式识别 / 求解 / 贴回 / 局部纠错）。作为独立事件类型记录，
    /// 只用于复盘标注，不参与基础板书回放，缺失时旧数据与回放均不受影响。
    /// </summary>
    AiStep,
}

/// <summary>
/// 板书录制中的单个笔画级事件。
/// <para>
/// <see cref="StrokeId"/> + <see cref="StrokeJson"/> 用于 <see cref="TimelineEventKind.StrokeAdded"/>；
/// <see cref="StrokeIds"/> 在 <see cref="TimelineEventKind.StrokesRemoved"/> 中是删除的笔画；
/// 在 <see cref="TimelineEventKind.Undo"/> / <see cref="TimelineEventKind.Redo"/> 中是本次操作移除的笔画，
/// 而 <see cref="Payload"/> 存放本次操作重新出现的笔画 id（JSON 字符串数组）。
/// </para>
/// <para>
/// <see cref="Payload"/> 的其它用途：<see cref="TimelineEventKind.BackgroundChanged"/> 存背景枚举名，
/// <see cref="TimelineEventKind.PptSlideChanged"/> 存幻灯片序号，
/// <see cref="TimelineEventKind.RosterDrawn"/> 存姓名，
/// <see cref="TimelineEventKind.TimerStarted"/> / <see cref="TimelineEventKind.TimerStopped"/> 存剩余毫秒。
/// </para>
/// </summary>
public sealed class TimelineEvent
{
    /// <summary>所属课堂会话 id；加载时由 <see cref="InkTimeline"/> 按 <see cref="ClassroomSession.Id"/> 补齐。</summary>
    public string SessionId { get; set; }

    /// <summary>会话内单调递增的事件序号，同毫秒事件的稳定排序键。</summary>
    public int Seq { get; set; }

    public long TimeMs { get; set; }

    public int PageIndex { get; set; }

    /// <summary>
    /// 画板归属：0 = 主展板，1 = 板中板。旧会话没有该字段，反序列化为 0（主展板）；
    /// 录制器可按构造参数写入子板 id。
    /// </summary>
    public int BoardId { get; set; }

    /// <summary>课件归属：事件发生时所处 PPT 幻灯片 id；0 表示不属于课件。</summary>
    public int PptSlideId { get; set; }

    public TimelineEventKind Kind { get; set; }

    public string StrokeId { get; set; }

    public string StrokeJson { get; set; }

    public IReadOnlyList<string> StrokeIds { get; set; }

    public string Payload { get; set; }

    /// <summary>
    /// 该事件发生时采集的课堂截图文件路径（入包前为磁盘绝对路径，入包后为
    /// <c>screenshots/</c> 下的相对条目路径；加载并解压后改写回绝对路径）。
    /// 仅 PPT 翻页/截图请求与倒计时结束事件挂载截图；切页、清空、点名等书签
    /// 按旧行为不采集。空值表示事件无截图。
    /// </summary>
    public string Screenshot { get; set; }
}

/// <summary>录制中的一个检查点，用于让 <see cref="InkTimeline.BuildFrame"/> 从最近处增量重放。</summary>
public sealed class SessionCheckpoint
{
    public long TimeMs { get; set; }

    /// <summary>该检查点已经应用到的事件索引（含）。</summary>
    public int EventIndex { get; set; }

    /// <summary>该检查点已应用到的最后一个事件的稳定序号；0 表示旧检查点仅按索引锚定。</summary>
    public int Seq { get; set; }

    public int PageIndex { get; set; }

    public string Background { get; set; }

    /// <summary>该时刻仍存在的全部笔画 id（跨页）。</summary>
    public IReadOnlyList<string> LiveStrokeIds { get; set; }

    /// <summary>
    /// 该时刻仍存在的全部 AI 板书对象 id（跨页）。旧会话没有该字段（null），
    /// 回放对象时从事件流起点重建，避免旧检查点丢失此前的 AI 卡片。
    /// </summary>
    public IReadOnlyList<string> LiveObjectIds { get; set; }
}

/// <summary>
/// 统一课堂会话时间轴上的章节书签。
/// <para>
/// <see cref="Auto"/> 为 true 表示由时间轴规则自动生成；用户重命名后转为手动书签，不再被自动规则覆盖。
/// <see cref="Kind"/> 记录来源（page / cleared / ppt / roster / timer / manual）。
/// </para>
/// </summary>
public sealed class SessionBookmark
{
    public long TimeMs { get; set; }

    public int PageIndex { get; set; }

    public string Name { get; set; } = "";

    public bool Auto { get; set; }

    public string Kind { get; set; } = "";
}

/// <summary>一次完整的板书录制会话（带章节书签）。</summary>
public sealed class ClassroomSession
{
    /// <summary>会话格式版本；加载时按默认值兼容。</summary>
    public int Version { get; set; } = 2;

    public string Id { get; set; }

    public string Name { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<TimelineEvent> Events { get; set; } = new();

    public List<SessionCheckpoint> Checkpoints { get; set; } = new();

    public List<SessionBookmark> Bookmarks { get; set; } = new();
}
