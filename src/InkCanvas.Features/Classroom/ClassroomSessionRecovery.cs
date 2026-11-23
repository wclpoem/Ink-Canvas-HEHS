using InkCanvas.Core.Classroom;

namespace InkCanvas.Features.Classroom;

/// <summary>异常退出后未完成录制的摘要，仅用于启动提示（不额外加载时间轴以外的数据）。</summary>
public sealed record PendingRecording(string Id, string Name, int EventCount, long LastEventTimeMs)
{
    public TimeSpan PlaybackLength => TimeSpan.FromMilliseconds(Math.Max(0, LastEventTimeMs));
}

/// <summary>
/// 课堂录制的异常恢复入口：扫描未完成增量日志，按「快照 + 增量日志」合并成正式会话，
/// 并登记到 <see cref="ClassroomSessionStore"/> 的会话目录（复用既有 JSON 格式，不新增文件格式）。
/// 恢复成功或主动放弃后都会删除 journal / snapshot，避免下次启动重复提示。
/// </summary>
public static class ClassroomSessionRecovery
{
    public const string RecoveredNameSuffix = "（已恢复）";

    /// <summary>扫描存在未完成日志的录制；单条损坏不会阻断其它候选。</summary>
    public static IReadOnlyList<PendingRecording> ScanPending(string directory = null)
    {
        var result = new List<PendingRecording>();
        foreach (string id in ClassroomSessionStore.PendingJournalIds(directory))
        {
            try
            {
                ClassroomSession session = ClassroomSessionStore.LoadWithJournal(id, directory);
                if (session is null) continue;
                result.Add(new PendingRecording(
                    id,
                    DisplayName(session, id),
                    session.Events?.Count ?? 0,
                    SessionJournal.LastEventTimeMs(id, directory)));
            }
            catch (Exception error)
            {
                LogTrace($"[AI] 课堂录制恢复扫描跳过 {id}：{error.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 恢复一条录制：合并快照与增量日志，标记「（已恢复）」后写入正式会话文件，
    /// 再清理 journal / snapshot。返回 null 表示没有可恢复内容或落盘失败。
    /// </summary>
    public static ClassroomSession Recover(string id, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        ClassroomSession session;
        try
        {
            session = ClassroomSessionStore.LoadWithJournal(id, directory);
        }
        catch (Exception error)
        {
            LogTrace($"[AI] 课堂录制恢复失败 {id}：{error.Message}");
            return null;
        }

        if (session is null || session.Events is not { Count: > 0 })
        {
            // 日志存在但没有任何事件：不登记空会话，只清掉恢复残留。
            ClassroomSessionStore.DeleteRecoveryArtifacts(id, directory);
            LogTrace($"[AI] 课堂录制恢复跳过空日志 {id}");
            return null;
        }

        session.Id = string.IsNullOrEmpty(session.Id) ? id : session.Id;
        session.Name = MarkRecovered(session.Name);
        try
        {
            ClassroomSessionStore.Save(session, directory);
        }
        catch (Exception error)
        {
            // 落盘失败时保留 journal / snapshot，下次启动仍可重试。
            LogTrace($"[AI] 课堂录制恢复落盘失败 {id}：{error.Message}");
            return null;
        }

        ClassroomSessionStore.DeleteRecoveryArtifacts(id, directory);
        LogTrace($"[AI] 课堂录制已恢复：id={id},events={session.Events.Count}");
        return session;
    }

    /// <summary>放弃恢复：删除 journal / snapshot，不动已有正式会话文件。</summary>
    public static bool Discard(string id, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool hadRecovery = ClassroomSessionStore.HasPendingJournal(id, directory);
        ClassroomSessionStore.DeleteRecoveryArtifacts(id, directory);
        LogTrace($"[AI] 课堂录制恢复已放弃：id={id},hadJournal={hadRecovery}");
        return hadRecovery;
    }

    /// <summary>批量恢复，返回实际登记的会话数量。</summary>
    public static int RecoverAll(IEnumerable<string> ids, string directory = null)
    {
        int recovered = 0;
        foreach (string id in ids ?? Array.Empty<string>())
            if (Recover(id, directory) is not null) recovered++;
        return recovered;
    }

    /// <summary>批量放弃，返回实际清理掉的恢复残留数量。</summary>
    public static int DiscardAll(IEnumerable<string> ids, string directory = null)
    {
        int discarded = 0;
        foreach (string id in ids ?? Array.Empty<string>())
            if (Discard(id, directory)) discarded++;
        return discarded;
    }

    /// <summary>给恢复会话补一次性后缀，重复调用不会叠加。</summary>
    public static string MarkRecovered(string name)
    {
        string trimmed = string.IsNullOrWhiteSpace(name) ? "课堂录制" : name.Trim();
        return trimmed.EndsWith(RecoveredNameSuffix, StringComparison.Ordinal)
            ? trimmed
            : trimmed + RecoveredNameSuffix;
    }

    private static string DisplayName(ClassroomSession session, string id)
        => MarkRecovered(string.IsNullOrWhiteSpace(session.Name) ? id : session.Name);

    private static void LogTrace(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响恢复 */ }
    }
}