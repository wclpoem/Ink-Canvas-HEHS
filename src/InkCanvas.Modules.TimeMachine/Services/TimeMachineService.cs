using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>
/// 时光机插件服务：在进程内管理录制生命周期、新格式存储与录制管理窗口。
/// 对宿主的依赖仅经 <see cref="ITimeMachineHost"/> 契约，不触碰宿主内部类型。
/// </summary>
public sealed partial class TimeMachineService : ITimeMachineService, IDisposable
{
    private readonly ITimeMachineHost _host;
    private readonly Func<Action, Action> _protect;
    private TimeMachineRecorder _recorder;
    /// <summary>子板录制器：与主录制器同步起录，停止时事件按 BoardId=1 并入主会话。</summary>
    private InkRecorder _sideRecorder;
    private RecordingsWindow _window;
    private bool _disposed;

    public TimeMachineService(ITimeMachineHost host, Func<Action, Action> protect)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _protect = protect ?? (Func<Action, Action>)(a => a);
    }

    public bool IsRecording => _recorder is { IsRecording: true };
    internal string LastRecordingSaveError { get; private set; }

    public event Action RecordingStateChanged;

    public void StartRecording()
    {
        if (_disposed || IsRecording) return;

        _recorder = new TimeMachineRecorder(_host);
        _recorder.BoardClosed += _protect(OnBoardClosed);
        _recorder.Start();

        // 主展板与子板同时起录：子板事件按 BoardId=1 记录，停止时并入主会话。
        // 子板录制器不单独落盘，持久化由本服务统一处理。
        if (_host.SideBoard is { } side && !ReferenceEquals(side, _host.Board))
        {
            _sideRecorder = new InkRecorder(side, 1);
            _sideRecorder.Start();
            LogTrace($"[AI] 子板录制已启动：pages={side.Pages.Count}");
        }

        RaiseState();
    }

    public void StopRecording()
    {
        if (_disposed || _recorder is null) return;

        TimeMachineRecorder recorder = _recorder;
        InkRecorder sideRecorder = _sideRecorder;
        LastRecordingSaveError = null;
        _recorder = null;
        _sideRecorder = null;

        ClassroomSession session = null;
        try
        {
            session = recorder.Stop();
        }
        finally
        {
            // 子板停止与合并失败都不得影响主会话保存：单独 try/catch，最终统一释放。
            if (sideRecorder is not null)
            {
                try
                {
                    sideRecorder.Stop();
                    if (session is not null && sideRecorder.Session is { Events.Count: > 0 })
                    {
                        int sideEvents = sideRecorder.Session.Events.Count;
                        int mainEvents = session.Events.Count;
                        foreach (TimelineEvent ev in sideRecorder.Session.Events)
                            ev.SessionId = session.Id;

                        session.Events.AddRange(sideRecorder.Session.Events);
                        SortEventsByTimeStable(session.Events);
                        RenumberEvents(session.Events);
                        session.Checkpoints?.Clear();
                        LogTrace($"[AI] 子板录制合并：recordedSideEvents={sideEvents},"
                            + $"recordedMainEvents={mainEvents},mergedEvents={session.Events.Count}");
                    }
                }
                catch (Exception error)
                {
                    LogTrace($"[AI] 子板录制合并失败：{error.Message}");
                }
                finally
                {
                    try { sideRecorder.Dispose(); } catch { /* 释放失败不阻断主保存 */ }
                }
            }

            recorder.Dispose();

            if (session is not null && session.Events.Count > 0)
            {
                try
                {
                    RecordingPackage.Save(session, RecordingsDirectory());
                }
                catch (Exception error)
                {
                    LastRecordingSaveError = error.Message;
                }
            }

            RaiseState();
        }
    }

    public Task OpenRecordingsAsync()
    {
        if (_disposed) return Task.CompletedTask;
        ShowWindow(initial: null, session: null);
        _ = Task.Run(() => CleanupRecordingCache());
        return Task.CompletedTask;
    }

    public async Task OpenFileAsync(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;

        string extractDir = Path.Combine(RecordingsDirectory(), ".cache",
            Path.GetFileNameWithoutExtension(path));
        ClassroomSession session = await Task.Run(() => RecordingPackage.Load(path, extractDir));
        // 打开成功后清理旧的解压缓存，保留本次正在使用的目录。
        await Task.Run(() => CleanupRecordingCache(keepDirectory: extractDir));
        ShowWindow(initial: null, session);
    }

    // ---- 供管理窗口调用 ---------------------------------------------------

    public string RecordingsDirectory() => RecordingPackage.DefaultDirectory(_host.DataDirectory);

    public IReadOnlyList<RecordingInfo> ListRecordings() => RecordingPackage.List(RecordingsDirectory());

    public ClassroomSession OpenSession(RecordingInfo info)
    {
        if (info is null) return null;
        string extractDir = Path.Combine(RecordingsDirectory(), ".cache", info.Id);
        ClassroomSession session = RecordingPackage.Load(info.FilePath, extractDir);
        CleanupRecordingCache(keepDirectory: extractDir);
        return session;
    }

    /// <summary>解压缓存保留天数；超过即视为上次打开的残留。</summary>
    public const int CacheRetentionDays = 7;

    /// <summary>解压缓存大小上限；超出后按最后写入时间从旧到新淘汰。</summary>
    public const long CacheMaxBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 清理 <c>Recordings/.cache</c> 中过期的截图解压目录（保留天数 + 大小上限），
    /// 当前正在查看的目录通过 <paramref name="keepDirectory"/> 排除。返回删除的目录数量。
    /// </summary>
    public int CleanupRecordingCache(string directory = null, string keepDirectory = null)
    {
        if (_disposed) return 0;
        string cacheRoot = Path.Combine(directory ?? RecordingsDirectory(), ".cache");
        if (!Directory.Exists(cacheRoot)) return 0;

        string keep = string.IsNullOrWhiteSpace(keepDirectory)
            ? null
            : Path.GetFullPath(keepDirectory);
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-CacheRetentionDays);

        var entries = new List<(DirectoryInfo Info, long Size)>();
        try
        {
            foreach (var child in new DirectoryInfo(cacheRoot).GetDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (keep is not null && string.Equals(Path.GetFullPath(child.FullName), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                entries.Add((child, DirectorySize(child.FullName)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LogTrace($"[AI] 录制缓存枚举失败：{error.Message}");
            return 0;
        }

        long total = entries.Sum(entry => entry.Size);
        int removed = 0;
        foreach (var entry in entries.OrderBy(entry => entry.Info.LastWriteTimeUtc))
        {
            bool expired = entry.Info.LastWriteTimeUtc < cutoffUtc;
            bool overLimit = total > CacheMaxBytes;
            if (!expired && !overLimit) break;
            if (!TryDeleteDirectory(entry.Info.FullName)) continue;
            total -= entry.Size;
            removed++;
        }

        if (removed > 0)
            LogTrace($"[AI] 录制缓存清理：removed={removed},remainingBytes={Math.Max(0, total)}");
        return removed;
    }

    public bool RenameRecording(RecordingInfo info, string newName)
        => info is not null && RecordingPackage.Rename(info.FilePath, newName);

    public bool DeleteRecording(RecordingInfo info)
        => info is not null && RecordingPackage.Delete(info.FilePath);

    public void CopyRecording(RecordingInfo info, string targetPath)
    {
        if (info is null) return;
        RecordingPackage.CopyTo(info.FilePath, targetPath);
    }

    public Task<ExportResult> ExportAsync(ClassroomSession session, ExportOptions options,
        IProgress<ExportProgress> progress, CancellationToken ct)
        => TimeMachineExporter.ExportWithResultAsync(session, options, progress, ct);

    public ITimeMachineHost Host => _host;

    public void Dispose()
    {
        if (_disposed) return;
        try { StopRecording(); } catch { }
        finally
        {
            // 兜底：即使 StopRecording 提前返回，子板录制器也必须释放。
            try { _sideRecorder?.Dispose(); } catch { }
            _sideRecorder = null;
        }
        _disposed = true;
        _window?.Close();
        _window = null;
    }

    // ---- 内部 -------------------------------------------------------------

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .Sum(file => file.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnBoardClosed()
    {
        StopRecording();
        _ = _host.NotifyAsync("时光机", "画板已关闭，本次录制已自动保存。");
    }

    private void ShowWindow(RecordingInfo initial, ClassroomSession session)
    {
        if (_window is { IsClosed: false })
        {
            _window.Activate();
            return;
        }

        _window = new RecordingsWindow(_host, this, initial, session);
        _host.ConfigureWindow(_window, 1120, 820);
        _window.Closed += (_, _) => _window = null;
        _window.Activate();
    }

    private void RaiseState() => RecordingStateChanged?.Invoke();

    /// <summary>按 <see cref="TimelineEvent.TimeMs"/> 稳定排序：同毫秒保持入列先后（主事件在前）。</summary>
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
    private static void RenumberEvents(List<TimelineEvent> events)
    {
        for (int i = 0; i < events.Count; i++) events[i].Seq = i + 1;
    }

    private static void LogTrace(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响录制生命周期 */ }
    }
}
