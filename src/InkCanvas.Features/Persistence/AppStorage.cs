using InkCanvas.Features.Update;

namespace InkCanvas.Features.Persistence;

public sealed record AppStorageInfo(UpdateCacheInfo Updates, long ConfigurationBytes, long TemporaryBytes,
    long UserDataBytes, DateTime? LastSavedUtc, bool HasBackup, string SaveError, bool RecoveredFromBackup);

public sealed class AppStorage(SettingsStore settings, UpdateCache updates)
{
    /// <summary>临时文件多久算「超龄」：1 天内视为其它实例仍在写入，跳过不删。</summary>
    public const int TemporaryRetentionDays = 1;

    private static readonly string[] ConfigurationFileNames = { "Settings.json", "Extensions.json", "LotteryCooldown.json" };

    /// <summary>录制目录（插件数据目录与旧版根目录 Recordings）里的 .tmp 也属于原子写残留。</summary>
    private static readonly string[] RecordingDirectoryNames =
    {
        "Recordings",
        Path.Combine("PluginData", "inkcanvas.time-machine", "Recordings"),
    };

    public AppStorageInfo Inspect()
    {
        var files = StorageFiles.Enumerate(settings.Directory).Where(file =>
            !file.FullName.StartsWith(updates.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
        long configuration = files.Where(file => IsConfiguration(file.Name) && file.DirectoryName == Path.GetFullPath(settings.Directory))
            .Sum(file => file.Length);
        var temporaryFiles = TemporaryFiles().ToArray();
        var temporaryDirectories = TemporaryDirectories().ToArray();
        long temporary = temporaryFiles.Sum(file => file.Length)
            + temporaryDirectories.Sum(directory => DirectorySize(directory.FullName));
        return new(updates.Inspect(), configuration, temporary,
            Math.Max(0, files.Sum(file => file.Length) - configuration - temporary),
            settings.LastSavedUtc, File.Exists(settings.BackupPath), settings.LastError, settings.RecoveredFromBackup);
    }

    public CacheCleanupResult CleanupTemporaryFiles()
    {
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-TemporaryRetentionDays);
        long freed = 0; int removed = 0, skipped = 0;
        foreach (var file in TemporaryFiles().ToArray())
        {
            // Recent temporary files may belong to an ongoing write in another instance.
            if (file.LastWriteTimeUtc > cutoffUtc) { skipped++; continue; }
            try { long size = file.Length; file.Delete(); freed += size; removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }

        foreach (var directory in TemporaryDirectories().ToArray())
        {
            // 解包 / 生成中的临时目录同样保留最近 1 天，避免打断其它实例。
            if (directory.LastWriteTimeUtc > cutoffUtc) { skipped++; continue; }
            try
            {
                long size = DirectorySize(directory.FullName);
                if (!StorageFiles.DeleteDirectory(directory.FullName)) { skipped++; continue; }
                freed += size; removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(freed, removed, skipped);
    }

    /// <summary>
    /// 超龄的原子写残留：根目录配置临时文件、examples.json.tmp、*.icink.tmp、
    /// sessions/*.tmp 与录制目录下的 *.tmp。未知的 *.tmp 不会被误删。
    /// </summary>
    private IEnumerable<FileInfo> TemporaryFiles()
    {
        string root = Path.GetFullPath(settings.Directory);
        foreach (var file in StorageFiles.Enumerate(settings.Directory))
        {
            if (file.FullName.StartsWith(updates.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsTemporaryFile(file, root)) yield return file;
        }
    }

    private static bool IsTemporaryFile(FileInfo file, string root)
    {
        string directory = Path.GetFullPath(file.DirectoryName ?? string.Empty);
        string name = file.Name;

        if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            return IsConfigurationTemporaryName(name)
                || string.Equals(name, "examples.json.tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".icink.tmp", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(directory, Path.Combine(root, "sessions"), StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string relative in RecordingDirectoryNames)
            if (string.Equals(directory, Path.Combine(root, relative), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool IsConfigurationTemporaryName(string name) => ConfigurationFileNames.Any(configured =>
        string.Equals(name, configured + ".tmp", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(configured + ".", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(name.Substring(configured.Length + 1, name.Length - configured.Length - 5), "N", out _));

    /// <summary>
    /// bundles 下的临时目录：未完成的解包暂存目录 <c>.restore-&lt;guid&gt;</c>（进程被终止时会残留），
    /// 以及以 <c>.tmp</c> 结尾的解包目录。完整解包目录属于用户数据，不清理。
    /// </summary>
    private IEnumerable<DirectoryInfo> TemporaryDirectories()
    {
        string bundles = Path.GetFullPath(Path.Combine(settings.Directory, "bundles"));
        if (!Directory.Exists(bundles) || !StorageFiles.IsRegular(bundles)) yield break;

        DirectoryInfo[] children;
        try { children = new DirectoryInfo(bundles).GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var child in children)
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (child.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
                continue;
            }

            DirectoryInfo[] staged;
            try { staged = child.GetDirectories(".restore-*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var item in staged)
                if ((item.Attributes & FileAttributes.ReparsePoint) == 0) yield return item;
        }
    }

    private static long DirectorySize(string path)
    {
        try { return StorageFiles.Size(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool IsConfiguration(string name) => name is "Settings.json" or "Settings.json.bak" or "Extensions.json" or "LotteryCooldown.json";
}