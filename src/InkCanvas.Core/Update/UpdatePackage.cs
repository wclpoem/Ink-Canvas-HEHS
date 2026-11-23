using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace InkCanvas.Core.Update;

public sealed record UpdateFile(string Path, string Sha256);
public sealed record UpdatePlan(string TargetDirectory, string PayloadDirectory, string Version, UpdateFile[] Files,
    int ParentProcessId, long ParentStartedUtcTicks, string ParentExecutable, bool Restart)
{
    public string MsiPackage { get; init; }
    public string MsiSha256 { get; init; }
}
public sealed record UpdateResult(bool Success, string Version, string Message);

/// <summary>Portable package staging and a rollback journal for replacement after the host exits.</summary>
public static class UpdatePackage
{
    public static async Task<UpdatePlan> PrepareAsync(string archive, string directory, string target, string version,
        int parentId = 0, long parentStarted = 0, string parentExecutable = null, bool restart = true, CancellationToken token = default)
    {
        directory = Path.GetFullPath(directory); target = Path.GetFullPath(target);
        string payload = Path.Combine(directory, "payload");
        if (Directory.Exists(payload)) throw new IOException("更新暂存目录已经存在。");
        Directory.CreateDirectory(payload);
        using var zip = ZipFile.OpenRead(archive);
        string prefix = zip.Entries.Any(entry => entry.FullName.Equals("InkCanvas/InkCanvas.exe", StringComparison.OrdinalIgnoreCase)) ? "InkCanvas/" : "";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<UpdateFile>(); long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含应用目录外的文件。");
            string relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            string destination = SafePath(payload, relative);
            if (!paths.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("安装包包含重复文件或链接。");
            if ((total += entry.Length) > 2L * 1024 * 1024 * 1024 || paths.Count > 20000)
                throw new InvalidDataException("安装包解压大小超过限制。");
            if (!ManagedFile(relative)) throw new InvalidDataException("安装包包含用户数据目录。");
            // Optional modules already installed on this machine are updated with the application.
            if (relative.StartsWith("Modules/", StringComparison.OrdinalIgnoreCase))
            {
                string module = relative.Split('/')[1];
                if (!Directory.Exists(SafePath(target, "Modules/" + module))) continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            await using (var input = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            await using var data = File.OpenRead(destination);
            files.Add(new(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(data, token).ConfigureAwait(false))));
        }
        foreach (string required in new[] { "InkCanvas.exe", "InkCanvas.dll", "InkCanvas.runtimeconfig.json", "InkCanvas.deps.json", "InkCanvas.pri" })
            if (!files.Any(file => file.Path.Equals(required, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("安装包缺少 " + required);
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(payload, "InkCanvas.dll"));
        if (assembly.Name != "InkCanvas" || !Versioning.AppVersion.TryParse(version, out var expected)
            || assembly.Version != new Version(expected.Major, expected.Minor, expected.Patch, expected.Build))
            throw new InvalidDataException("安装包的程序版本与 Release 不一致。");
        return new(target, payload, version, files.ToArray(), parentId, parentStarted, parentExecutable, restart);
    }

    public static async Task ApplyAsync(UpdatePlan plan, string workDirectory, CancellationToken token = default)
    {
        string target = Path.GetFullPath(plan.TargetDirectory), payload = Path.GetFullPath(plan.PayloadDirectory);
        if (!File.Exists(SafePath(target, "InkCanvas.exe"))) throw new InvalidDataException("目标目录不是 Ink Canvas 安装目录。");
        if (target.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("无效的安装目录。");
        string backup = Path.Combine(workDirectory, "backup"), journal = Path.Combine(workDirectory, "journal.json");
        Directory.CreateDirectory(backup);
        await using var installLock = new FileStream(Path.Combine(target, ".inkcanvas-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(journal)) Recover(target, backup, journal);
        var files = plan.Files.ToList();
        if (files.Count == 0 || files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("无效的更新文件清单。");
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!ManagedFile(file.Path)) throw new InvalidDataException("更新不能写入用户数据目录。");
            string source = SafePath(payload, file.Path);
            SafePath(target, file.Path);
            await using var input = File.OpenRead(source);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("暂存文件校验失败 " + file.Path);
        }
        string inventory = ".inkcanvas-update-files.json";
        var obsolete = new List<string>();
        string inventoryPath = SafePath(target, inventory);
        if (File.Exists(inventoryPath))
        {
            foreach (string previous in JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(inventoryPath, token).ConfigureAwait(false)) ?? Array.Empty<string>())
                if (ManagedFile(previous) && !files.Any(file => file.Path.Equals(previous, StringComparison.OrdinalIgnoreCase))
                    && !previous.StartsWith("Modules/", StringComparison.OrdinalIgnoreCase)) obsolete.Add(previous);
        }
        var records = new List<(string Path, bool Existed)>();
        try
        {
            foreach (string path in files.Select(file => file.Path).Concat(obsolete).Append(inventory))
            {
                string destination = SafePath(target, path);
                bool existed = File.Exists(destination);
                if (existed)
                {
                    string copy = SafePath(backup, path); Directory.CreateDirectory(Path.GetDirectoryName(copy));
                    File.Copy(destination, copy, true);
                }
                records.Add((path, existed));
            }
            // Journal is written before the first replacement, allowing interrupted updates to restore.
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(records.Select(record => new JournalEntry(record.Path, record.Existed))), token).ConfigureAwait(false);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string destination = SafePath(target, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(SafePath(payload, file.Path), destination, true);
            }
            foreach (string path in obsolete) File.Delete(SafePath(target, path));
            await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(files.Select(file => file.Path)), token).ConfigureAwait(false);
            File.Delete(journal);
        }
        catch
        {
            if (File.Exists(journal)) Recover(target, backup, journal);
            throw;
        }
    }

    public static void Recover(string target, string backup, string journal)
    {
        var entries = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(journal)) ?? throw new InvalidDataException("无效的回滚清单。");
        foreach (var entry in entries.Reverse())
        {
            string path = SafePath(target, entry.Path);
            if (entry.Existed) File.Copy(SafePath(backup, entry.Path), path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        File.Delete(journal);
    }

    private sealed record JournalEntry(string Path, bool Existed);

    public static string SafePath(string root, string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')
            || IsDeviceName(part)) || Path.IsPathRooted(relative))
            throw new InvalidDataException("无效的更新文件路径。");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新目录包含重解析链接。");
        string path = root;
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径包含重解析链接。");
        }
        return path;
    }

    private static bool ManagedFile(string path)
    {
        string first = path.Replace('\\', '/').Split('/')[0];
        return !new[] { "Plugins", "PluginData", "Settings", "UserData", "AutoSaved", ".git", "backup",
            ".inkcanvas-update-files.json", ".inkcanvas-update.lock" }
            .Contains(first, StringComparer.OrdinalIgnoreCase) && !path.EndsWith(".icink", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeviceName(string part)
    {
        string name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] is >= '0' and <= '9';
    }
}
