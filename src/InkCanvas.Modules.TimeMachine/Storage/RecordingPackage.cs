using InkCanvas.Host.Contracts;
using System.IO.Compression;
using System.Text.Json;

namespace InkCanvas.Modules.TimeMachine;

/// <summary>一条录制列表项摘要（不加载完整时间轴）。</summary>
public sealed record RecordingInfo(
    string Id, string Name, DateTime CreatedAt, string FilePath,
    int EventCount, long DurationMs);

/// <summary>
/// 新格式课堂录制存储：每会话单个 <c>.icink</c>（ZIP）容器。
/// <para>
/// 容器条目：<c>classroom.json</c>（格式清单，标记
/// <see cref="ClassroomRecordingFormat.Format"/> / <see cref="ClassroomRecordingFormat.Version"/>）、
/// <c>timeline.json</c>（完整会话时间轴）、<c>history/</c>（检查点笔画快照）、
/// <c>screenshots/*.png</c>（PPT 翻页与倒计时结束时刻采集的课堂截图）。写入先落 <c>.tmp</c> 再原子替换。
/// </para>
/// </summary>
public static class RecordingPackage
{
    public const string TimelineEntry = "timeline.json";
    public const string HistoryPrefix = "history/";
    public const string ScreenshotsPrefix = "screenshots/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>默认录制目录：<c>&lt;DataDirectory&gt;\Recordings\</c>。</summary>
    public static string DefaultDirectory(string dataDirectory)
        => Path.Combine(string.IsNullOrWhiteSpace(dataDirectory) ? "Recordings" : dataDirectory, "Recordings");

    /// <summary>会话文件路径（默认目录下按 Id 命名）。</summary>
    public static string PathFor(string sessionId, string directory)
        => Path.Combine(directory, sessionId + ClassroomRecordingFormat.Extension);

    /// <summary>
    /// 保存会话为 <c>.icink</c> 容器，返回文件绝对路径。截图文件从磁盘读入容器并改写为条目相对路径。
    /// </summary>
    public static string Save(ClassroomSession session, string directory)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(session.Id)) session.Id = Guid.NewGuid().ToString("N");

        string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory(null) : directory;
        Directory.CreateDirectory(dir);
        string path = PathFor(session.Id, dir);
        string tmp = path + ".tmp";

        // 落盘前补齐自动书签，保证容器内时间轴与规则一致。
        new InkTimeline(session).ApplyAutoBookmarks();

        var screenshotEntries = EmbedScreenshots(session);

        using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            WriteEntry(archive, ClassroomRecordingFormat.ManifestEntry, JsonSerializer.Serialize(new ManifestDto
            {
                Format = ClassroomRecordingFormat.Format,
                Version = ClassroomRecordingFormat.Version,
                Id = session.Id,
                Name = session.Name,
                CreatedAt = session.CreatedAt,
                Timeline = TimelineEntry,
                HistoryDir = HistoryPrefix,
                ScreenshotsDir = ScreenshotsPrefix,
            }, JsonOptions));

            WriteEntry(archive, TimelineEntry, JsonSerializer.Serialize(session, JsonOptions));

            // 即使没有检查点 / 截图，也保留固定目录条目，便于本体按结构识别容器。
            archive.CreateEntry(HistoryPrefix);
            archive.CreateEntry(ScreenshotsPrefix);

            for (int i = 0; i < session.Checkpoints.Count; i++)
            {
                var checkpoint = session.Checkpoints[i];
                WriteEntry(archive, HistoryPrefix + $"checkpoint-{i:D4}.json",
                    JsonSerializer.Serialize(checkpoint, JsonOptions));
            }

            foreach (var (entryName, bytes) in screenshotEntries)
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// 打开 <c>.icink</c> 容器并还原会话。格式标记不符时抛出带中文提示的
    /// <see cref="InvalidOperationException"/>。<paramref name="extractDirectory"/> 非空时
    /// 把截图解压到该目录并把事件载荷中的相对条目路径改写为绝对路径，供导出读取。
    /// </summary>
    public static ClassroomSession Load(string path, string extractDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("录制文件不存在。", path);

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("该文件不是本机时光机录制格式（无法作为 ZIP 容器打开），无法打开。", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("录制文件无法读取（可能已损坏或被占用）。", ex);
        }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(ClassroomRecordingFormat.ManifestEntry);
        if (manifestEntry is null)
            throw new InvalidOperationException("该文件不是本机时光机录制格式（缺少 classroom.json 清单），无法打开。");

        ManifestDto manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifest = JsonSerializer.Deserialize<ManifestDto>(reader.ReadToEnd(), JsonOptions)
                ?? throw new InvalidOperationException("录制清单损坏，无法打开。");

        if (!string.Equals(manifest.Format, ClassroomRecordingFormat.Format, StringComparison.Ordinal)
            || manifest.Version != ClassroomRecordingFormat.Version)
        {
            throw new InvalidOperationException(
                $"录制文件格式不匹配（期望 {ClassroomRecordingFormat.Format} v{ClassroomRecordingFormat.Version}），" +
                "请确认插件版本与录制文件一致。");
        }

        var timelineEntry = archive.GetEntry(manifest.Timeline ?? TimelineEntry)
            ?? archive.GetEntry(TimelineEntry);
        if (timelineEntry is null)
            throw new InvalidOperationException("录制文件缺少时间轴数据（timeline.json），无法打开。");

        ClassroomSession session;
        using (var reader = new StreamReader(timelineEntry.Open()))
            session = JsonSerializer.Deserialize<ClassroomSession>(reader.ReadToEnd(), JsonOptions)
                ?? throw new InvalidOperationException("时间轴数据损坏，无法打开。");

        session.Events ??= new List<TimelineEvent>();
        session.Checkpoints ??= new List<SessionCheckpoint>();
        session.Bookmarks ??= new List<SessionBookmark>();

            ExtractScreenshots(archive, session, extractDirectory);
            return session;
        }
    }

    /// <summary>列出目录下全部合法录制（按创建时间降序）；损坏条目被跳过。</summary>
    public static IReadOnlyList<RecordingInfo> List(string directory)
    {
        var result = new List<RecordingInfo>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return result;

        foreach (var file in Directory.EnumerateFiles(directory, "*" + ClassroomRecordingFormat.Extension))
        {
            try
            {
                var session = Load(file);
                long duration = session.Events.Count == 0 ? 0 : Math.Max(0, session.Events[^1].TimeMs);
                result.Add(new RecordingInfo(
                    session.Id, session.Name, session.CreatedAt, file, session.Events.Count, duration));
            }
            catch
            {
                // 单个损坏 / 非本格式文件不影响其它录制列出。
            }
        }

        return result.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>重命名会话（同步落盘到原文件）。</summary>
    public static bool Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        if (!File.Exists(path)) return false;

        var session = Load(path);
        session.Name = newName.Trim();
        Save(session, Path.GetDirectoryName(path));
        return true;
    }

    /// <summary>删除录制文件。</summary>
    public static bool Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    /// <summary>另存为：把容器原样复制到目标路径。</summary>
    public static void CopyTo(string sourcePath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("源录制文件不存在。", sourcePath);
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("另存路径不能为空。", nameof(targetPath));

        string dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    // ---- 内部 -------------------------------------------------------------

    private static Dictionary<string, byte[]> EmbedScreenshots(ClassroomSession session)
    {
        var embedded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var absoluteToEntry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in session.Events)
        {
            PptSlideChange change = default;
            string pptPath = null;
            if (e.Kind == TimelineEventKind.PptSlideChanged)
            {
                change = InkTimeline.DecodePptSlideChanged(e.Payload);
                if (change.IsValid) pptPath = change.Screenshot;
            }

            string absolutePath = ResolveLocalPath(pptPath) ?? ResolveLocalPath(e.Screenshot);
            if (absolutePath is null) continue;

            if (!absoluteToEntry.TryGetValue(absolutePath, out var entry))
            {
                if (!File.Exists(absolutePath)) continue;
                try
                {
                    byte[] bytes = File.ReadAllBytes(absolutePath);
                    entry = ScreenshotsPrefix + Guid.NewGuid().ToString("N") + ".png";
                    embedded[entry] = bytes;
                    absoluteToEntry[absolutePath] = entry;
                }
                catch
                {
                    // 截图读入失败：保留无截图引用，不阻断会话保存。
                    continue;
                }
            }

            if (pptPath is not null && change.IsValid)
                e.Payload = InkTimeline.EncodePptSlideChanged(change.SlideId, change.Index, entry);
            e.Screenshot = entry;
        }
        return embedded;
    }

    private static void ExtractScreenshots(ZipArchive archive, ClassroomSession session, string extractDirectory)
    {
        if (string.IsNullOrWhiteSpace(extractDirectory)) return;
        Directory.CreateDirectory(extractDirectory);

        foreach (var e in session.Events)
        {
            PptSlideChange change = default;
            string pptEntry = null;
            if (e.Kind == TimelineEventKind.PptSlideChanged)
            {
                change = InkTimeline.DecodePptSlideChanged(e.Payload);
                if (change.IsValid) pptEntry = change.Screenshot;
            }

            if (TryExtractEntry(archive, pptEntry, extractDirectory, out var pptFile) && change.IsValid)
                e.Payload = InkTimeline.EncodePptSlideChanged(change.SlideId, change.Index, pptFile);
            if (TryExtractEntry(archive, e.Screenshot, extractDirectory, out var eventFile))
                e.Screenshot = eventFile;
        }
    }

    private static string ResolveLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // 已是容器条目路径（上一次保存或从包内读取），不再重复读盘。
        if (path.StartsWith(ScreenshotsPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }

    private static bool TryExtractEntry(ZipArchive archive, string entryName, string extractDirectory, out string filePath)
    {
        filePath = null;
        if (string.IsNullOrWhiteSpace(entryName)
            || !entryName.StartsWith(ScreenshotsPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var entry = archive.GetEntry(entryName);
        if (entry is null) return false;

        string targetPath = Path.Combine(extractDirectory, Path.GetFileName(entryName));
        try
        {
            using var input = entry.Open();
            using var output = File.Create(targetPath);
            input.CopyTo(output);
            filePath = targetPath;
            return true;
        }
        catch
        {
            // 截图解压失败：回退无截图背景，不阻断打开。
            return false;
        }
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class ManifestDto
    {
        public string Format { get; set; }
        public int Version { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Timeline { get; set; }
        public string HistoryDir { get; set; }
        public string ScreenshotsDir { get; set; }
    }
}
