using InkCanvas.Core.Classroom;
using System.Security.Cryptography;
using System.Text.Json;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 板书会话的持久化：保存 / 加载到 <c>%APPDATA%\InkCanvas\sessions\&lt;id&gt;.json</c>，
/// 并支持会话列表、重命名、删除与导入 / 导出。
/// JSON 键统一使用 camelCase。
/// </summary>
public static class ClassroomSessionStore
{
    /// <summary>会话列表默认最多加载的条数；只影响内存列表，不删除磁盘文件。</summary>
    public const int MaxSessionsLoaded = 20;

    /// <summary>会话默认保留天数；启动清理只删除早于该天数且不在最近保留范围内的会话。</summary>
    public const int DefaultRetentionDays = 90;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas", "sessions");

    public static string PathFor(string id, string directory = null)
        => Path.Combine(directory ?? DefaultDirectory, id + ".json");

    /// <summary>会话快照路径：录制中用于异常恢复，与正式会话文件分开保存。</summary>
    public static string PathForSnapshot(string id, string directory = null)
        => Path.Combine(directory ?? DefaultDirectory, id + ".snapshot.json");

    /// <summary>会话文件的 SHA-256 旁车文件；旧会话没有该文件，加载时按「未校验」处理。</summary>
    public static string PathForChecksum(string path) => path + ".sha256";

    /// <summary>会话文件的迁移前备份；首次加载旧版会话时写入，后续不再覆盖。</summary>
    public static string PathForBackup(string path) => path + ".bak";

    public static string Save(ClassroomSession session, string directory = null)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(session.Id)) session.Id = Guid.NewGuid().ToString("N");
        string path = PathFor(session.Id, directory);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteAtomic(path, JsonSerializer.Serialize(session, Options));
        return path;
    }

    /// <summary>保存录制中的完整快照（异常退出后与增量日志合并恢复）。</summary>
    public static string SaveSnapshot(ClassroomSession session, string directory = null)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(session.Id)) session.Id = Guid.NewGuid().ToString("N");
        string path = PathForSnapshot(session.Id, directory);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        WriteAtomic(path, JsonSerializer.Serialize(session, Options));
        return path;
    }

    /// <summary>
    /// 原子写入：先写临时文件并刷盘，再替换正式文件，最后写旁车校验。
    /// 任一步失败都不会留下「半文件被当作正式文件」的状态。
    /// </summary>
    private static void WriteAtomic(string path, string json)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        WriteChecksum(path);
    }

    private static void WriteChecksum(string path)
    {
        try
        {
            string hash = ComputeSha256(path);
            string checksumPath = PathForChecksum(path);
            string tmp = checksumPath + ".tmp";
            File.WriteAllText(tmp, hash);
            File.Move(tmp, checksumPath, overwrite: true);
        }
        catch
        {
            // 校验文件写失败不影响会话本身；下次保存会重试。
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool VerifyChecksum(string path)
    {
        try
        {
            string checksumPath = PathForChecksum(path);
            if (!File.Exists(checksumPath)) return true; // 旧会话没有旁车文件：按未校验放行。
            string expected = File.ReadAllText(checksumPath).Trim();
            if (string.IsNullOrEmpty(expected)) return true;
            return string.Equals(expected, ComputeSha256(path), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // 校验器自身不可用时不应把可读会话判死。
        }
    }

    public static ClassroomSession LoadSnapshot(string id, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return Load(PathForSnapshot(id, directory)); }
        catch { return null; } // 快照损坏时回退到正式会话 / 增量日志。
    }

    /// <summary>
    /// 加载会话：存在未完成日志时优先用「快照 + 增量日志」恢复（最多丢最后约 2 秒），
    /// 否则读取正式会话文件。
    /// </summary>
    public static ClassroomSession LoadWithJournal(string id, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var recovered = SessionJournal.Recover(id, directory);
        return recovered ?? Load(PathFor(id, directory));
    }

    /// <summary>目录中仍有未完成日志的会话 id（用于启动时提示恢复）。</summary>
    public static IReadOnlyList<string> PendingJournalIds(string directory = null)
        => SessionJournal.PendingSessionIds(directory);

    public static bool HasPendingJournal(string id, string directory = null)
        => SessionJournal.HasPending(id, directory);

    /// <summary>正常结束录制后删除快照与增量日志。</summary>
    public static void DeleteRecoveryArtifacts(string id, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        foreach (var path in new[] { PathForSnapshot(id, directory), SessionJournal.PathFor(id, directory) })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 清理失败不影响会话本身。
            }
        }
    }

    public static ClassroomSession Load(string path)
    {
        if (!File.Exists(path)) return null;

        // 完整性校验：有旁车文件且不匹配时，优先用迁移前备份，其次拒绝加载损坏内容。
        bool integrityOk = VerifyChecksum(path);
        string source = path;
        if (!integrityOk)
        {
            string backup = PathForBackup(path);
            if (!File.Exists(backup))
                throw new InvalidDataException("会话文件完整性校验失败，且没有可用备份：" + path);
            source = backup;
        }

        string json = File.ReadAllText(source);
        var session = JsonSerializer.Deserialize<ClassroomSession>(json, Options);
        if (session is null) return null;

        session.Events ??= new List<TimelineEvent>();
        session.Checkpoints ??= new List<SessionCheckpoint>();

        if (integrityOk) MigrateIfNeeded(path, json, session);
        else session.Bookmarks ??= new List<SessionBookmark>();
        return session;
    }

    /// <summary>
    /// 旧版会话迁移：迁移前把原文件复制为 <c>.bak</c>（只写一次），补齐 v2 字段并把版本提升到 2。
    /// 迁移本身不写回原文件，失败也不会改动历史记录；下一次正常保存才落新版格式。
    /// </summary>
    private static void MigrateIfNeeded(string path, string json, ClassroomSession session)
    {
        bool legacy = !HasVersionProperty(json);
        if (!legacy && session.Version >= 2)
        {
            session.Bookmarks ??= new List<SessionBookmark>();
            return;
        }

        string backup = PathForBackup(path);
        try
        {
            if (!File.Exists(backup)) File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // 备份失败时不写回原文件；内存迁移照常进行，历史文件保持不变。
        }

        if (session.Version < 2) session.Version = 2;
        session.Bookmarks ??= new List<SessionBookmark>();
    }

    private static bool HasVersionProperty(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("version", out _)
                    || document.RootElement.TryGetProperty("Version", out _));
        }
        catch
        {
            return true; // 无法解析时不当作旧版，交给反序列化错误处理。
        }
    }

    /// <summary>
    /// 列出会话，默认最多返回最近 <see cref="MaxSessionsLoaded"/> 条（按创建时间降序）。
    /// 仅截断内存列表，不会删除磁盘上的会话文件。
    /// </summary>
    public static List<ClassroomSession> LoadAll(string directory = null)
        => LoadAll(MaxSessionsLoaded, directory);

    /// <summary>列出最近 <paramref name="max"/> 条会话（按创建时间降序），不删除磁盘文件。</summary>
    public static List<ClassroomSession> LoadAll(int max, string directory = null)
        => LoadAll(max, out _, directory);

    /// <summary>
    /// 列出最近 <paramref name="max"/> 条会话，并通过 <paramref name="omitted"/>
    /// 告知因上限被省略的会话数量（供 UI 提示，不删除文件）。
    /// </summary>
    public static List<ClassroomSession> LoadAll(int max, out int omitted, string directory = null)
    {
        var all = LoadAllCore(directory);
        if (max <= 0)
        {
            omitted = all.Count;
            return new List<ClassroomSession>();
        }
        omitted = Math.Max(0, all.Count - max);
        return all.Take(max).ToList();
    }

    /// <summary>
    /// 计算超出 <paramref name="keep"/> 条的旧会话 Id（按创建时间降序，返回最旧的那些）。
    /// 只返回候选，不删除任何文件；由 UI 提示后用 <see cref="DeleteOldSessions"/> 显式删除。
    /// </summary>
    public static IReadOnlyList<string> PruneOldSessions(int keep = MaxSessionsLoaded, string directory = null)
    {
        var all = LoadAllCore(directory);
        int start = Math.Max(0, keep);
        if (start >= all.Count) return Array.Empty<string>();
        return all.Skip(start)
            .Select(s => s.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
    }

    /// <summary>
    /// 删除超出 <paramref name="keep"/> 条的旧会话：仅删除 <c>sessions/*.json</c> 与
    /// 对应的 journal / snapshot，不动会话子目录。返回实际删除的会话数量。
    /// </summary>
    public static int DeleteOldSessions(int keep = MaxSessionsLoaded, string directory = null)
    {
        int removed = 0;
        foreach (string id in PruneOldSessions(keep, directory))
        {
            if (DeleteSessionFiles(id, directory)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// 按保留天数清理旧会话：最近 <paramref name="minimumKeep"/> 条永不自动删除；
    /// 其余仅在创建时间早于保留天数时才删除。返回实际删除的会话数量。
    /// </summary>
    public static int DeleteExpiredSessions(int retentionDays = DefaultRetentionDays,
        int minimumKeep = MaxSessionsLoaded, string directory = null)
    {
        if (retentionDays <= 0) return 0;
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        var sessions = LoadAllCore(directory);
        int keep = Math.Max(0, minimumKeep);
        int removed = 0;
        for (int index = 0; index < sessions.Count; index++)
        {
            // 列表按创建时间降序，前 keep 条是最近会话，永不自动删除。
            if (index < keep) continue;
            ClassroomSession session = sessions[index];
            if (session is null || string.IsNullOrWhiteSpace(session.Id)) continue;
            if (session.CreatedAt.ToUniversalTime() >= cutoffUtc) continue;
            // 未完成录制（仍有 journal）留给启动恢复提示，不能当作过期历史会话删除。
            if (HasPendingJournal(session.Id, directory)) continue;
            if (DeleteSessionFiles(session.Id, directory)) removed++;
        }
        return removed;
    }

    private static bool DeleteSessionFiles(string id, string directory)
    {
        string path = PathFor(id, directory);
        bool removedAny = TryDelete(path);
        removedAny |= TryDelete(PathForChecksum(path));
        removedAny |= TryDelete(PathForBackup(path));
        removedAny |= TryDelete(PathForSnapshot(id, directory));
        removedAny |= TryDelete(SessionJournal.PathFor(id, directory));
        return removedAny;
    }

    private static List<ClassroomSession> LoadAllCore(string directory)
    {
        string dir = directory ?? DefaultDirectory;
        var result = new List<ClassroomSession>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            // 快照是录制中的恢复中间产物，不作为独立会话列出。
            if (file.EndsWith(".snapshot.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var session = Load(file);
                if (session is null) continue;
                result.Add(session);
                if (!string.IsNullOrEmpty(session.Id)) seen.Add(session.Id);
            }
            catch
            {
                // 单个损坏文件不影响其它会话的列出。
            }
        }

        // 异常退出留下的增量日志：合并快照 + 日志后列出，标记为已恢复。
        foreach (var id in SessionJournal.PendingSessionIds(dir))
        {
            if (seen.Contains(id)) continue;
            try
            {
                var recovered = SessionJournal.Recover(id, dir);
                if (recovered is null || recovered.Events.Count == 0) continue;
                if (!string.IsNullOrEmpty(recovered.Name) && !recovered.Name.EndsWith("（已恢复）", StringComparison.Ordinal))
                    recovered.Name += "（已恢复）";
                result.Add(recovered);
            }
            catch
            {
                // 损坏的日志不影响其它会话。
            }
        }

        return result.OrderByDescending(s => s.CreatedAt).ToList();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按 id 重命名会话（同步落盘）。</summary>
    public static bool Rename(string id, string newName, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        string path = PathFor(id, directory);
        var session = Load(path);
        if (session is null) return false;
        session.Name = newName.Trim();
        Save(session, directory);
        return true;
    }

    public static bool Delete(string id, string directory = null)
    {
        string path = PathFor(id, directory);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        TryDelete(PathForChecksum(path));
        TryDelete(PathForBackup(path));
        return true;
    }

    /// <summary>导出到任意路径（用于分享）。</summary>
    public static void Export(ClassroomSession session, string path)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(session, Options));
    }

    /// <summary>从任意路径导入并写入会话目录；若 id 冲突则保留原 id 覆盖。</summary>
    public static ClassroomSession Import(string path, string directory = null)
    {
        var session = Load(path);
        if (session is null) return null;
        if (string.IsNullOrEmpty(session.Id)) session.Id = Guid.NewGuid().ToString("N");
        Save(session, directory);
        return session;
    }
}