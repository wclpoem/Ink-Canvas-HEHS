using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Extensibility;

namespace InkCanvas.Extensions;

public sealed record PendingExtension(string Id, string Name, string Version, RepositoryExtensionKind Kind, string Stage, UpdateFile[] Files);
public sealed record ExtensionInstallResult(string Id, bool Success, string Message);

/// <summary>Stages verified packages while running and swaps package directories before module discovery.</summary>
public sealed class ExtensionInstallStore
{
    private readonly string _userDirectory;
    private readonly string _root;
    private readonly string _pendingPath;
    private IReadOnlyList<PendingExtension> _pending;
    public ExtensionInstallStore(string userDirectory)
    {
        _userDirectory = Path.GetFullPath(userDirectory);
        _root = Path.Combine(_userDirectory, "ExtensionUpdates");
        _pendingPath = Path.Combine(_root, "pending.json");
    }
    public IReadOnlyList<PendingExtension> Pending => _pending ??= ReadPending();
    public List<ExtensionInstallResult> Results { get; } = new();
    public string UserModules => Path.Combine(_userDirectory, "Modules");
    public string UserPlugins => Path.Combine(_userDirectory, "Plugins");

    public string ModuleManifest(string appDirectory, string id)
    {
        string user = UpdatePackage.SafePath(UserModules, id + "/module.json");
        string bundled = Path.Combine(appDirectory, "Modules", id, "module.json");
        if (!File.Exists(user)) return bundled;
        try
        {
            if (File.Exists(bundled) && Version.Parse(ExtensionPackages.Read(bundled).Version) > Version.Parse(ExtensionPackages.Read(user).Version)) return bundled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        return user;
    }

    public async Task StageAsync(string archive, ExtensionRelease release, CancellationToken token)
    {
        if (release.Kind == RepositoryExtensionKind.Explorer) throw new InvalidDataException("资源管理器扩展需要系统注册，请使用独立安装程序。");
        string directory = Path.Combine(_root, "staged", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var files = new List<UpdateFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = release.Id + "/";
            using var zip = ZipFile.OpenRead(archive);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("插件包必须使用与插件 ID 相同的独立目录。");
                string relative = name[prefix.Length..];
                if (relative.Equals(".repository-package.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件包不能包含安装状态文件。");
                string target = UpdatePackage.SafePath(directory, relative);
                if (!seen.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("插件包包含重复路径或链接。");
                total = checked(total + entry.Length);
                if (total > 512L * 1024 * 1024 || seen.Count > 10000) throw new InvalidDataException("插件包解压大小超过限制。");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                await using (var input = entry.Open())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                { await input.CopyToAsync(output, token).ConfigureAwait(false); }
                await using var data = File.OpenRead(target);
                files.Add(new(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(data, token).ConfigureAwait(false))));
            }
            string manifestName = release.Kind == RepositoryExtensionKind.Module ? "module.json" : "plugin.json";
            var manifest = ExtensionPackages.Read(Path.Combine(directory, manifestName));
            if (manifest.Id != release.Id || new Version(manifest.Version) != new Version(release.Version))
                throw new InvalidDataException("插件清单与发布的 ID 或版本不一致。");
            string assembly = ExtensionPackages.EntryPath(directory, manifest.EntryAssembly);
            if (!File.Exists(assembly)) throw new InvalidDataException("插件包缺少入口程序集。");
            if (release.Kind == RepositoryExtensionKind.Plugin && manifest.Dependencies.Length > 0)
                throw new InvalidDataException("外部插件不能依赖其他插件。");
            token.ThrowIfCancellationRequested();
            ValidateId(release.Id);
            await File.WriteAllTextAsync(Path.Combine(directory, ".repository-package.json"), JsonSerializer.Serialize(new { release.Id, release.Version }), token).ConfigureAwait(false);
            using var gate = Lock();
            var pending = ReadPending().Where(item => item.Id != release.Id).ToList();
            var previous = ReadPending().FirstOrDefault(item => item.Id == release.Id);
            pending.Add(new(release.Id, release.Name, release.Version, release.Kind, Path.GetFileName(directory), files.ToArray()));
            WritePending(pending);
            if (previous is not null)
                try { DeleteStage(previous.Stage); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        catch { if (Directory.Exists(directory)) Directory.Delete(directory, true); throw; }
    }

    public void ApplyPending()
    {
        Results.Clear();
        if (!Directory.Exists(_root)) return;
        try
        {
            using var gate = Lock();
            RecoverInterrupted();
            var remaining = new List<PendingExtension>();
            foreach (var pending in ReadPending())
            {
                try { Apply(pending); Results.Add(new(pending.Id, true, "已安装 " + pending.Version)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                { remaining.Add(pending); Results.Add(new(pending.Id, false, exception.Message)); }
            }
            WritePending(remaining);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { _pending = Array.Empty<PendingExtension>(); Results.Add(new("", false, "插件安装暂未完成 · " + exception.Message)); }
    }

    private void Apply(PendingExtension pending)
    {
        ValidateId(pending.Id);
        if (pending.Kind is not (RepositoryExtensionKind.Module or RepositoryExtensionKind.Plugin)) throw new InvalidDataException("无效的插件类型。");
        string stage = UpdatePackage.SafePath(Path.Combine(_root, "staged"), pending.Stage);
        string targetRoot = pending.Kind == RepositoryExtensionKind.Module ? UserModules : UserPlugins;
        string target = UpdatePackage.SafePath(targetRoot, pending.Id);
        string backup = UpdatePackage.SafePath(Path.Combine(_root, "backup"), pending.Id);
        if (!Directory.Exists(stage) && Directory.Exists(target))
        {
            string identity = pending.Kind == RepositoryExtensionKind.Module ? "module.json" : "plugin.json";
            var installed = ExtensionPackages.Read(Path.Combine(target, identity));
            if (installed.Id == pending.Id && installed.Version == pending.Version) return;
        }
        if (pending.Files is not { Length: > 0 }) throw new InvalidDataException("插件文件清单为空。");
        foreach (var file in pending.Files)
        {
            using var stream = File.OpenRead(UpdatePackage.SafePath(stage, file.Path));
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("待安装插件文件已变化，请重新下载。");
        }
        string manifestName = pending.Kind == RepositoryExtensionKind.Module ? "module.json" : "plugin.json";
        var manifest = ExtensionPackages.Read(Path.Combine(stage, manifestName));
        if (manifest.Id != pending.Id || manifest.Version != pending.Version) throw new InvalidDataException("待安装插件清单不一致。");
        Directory.CreateDirectory(targetRoot); Directory.CreateDirectory(Path.GetDirectoryName(backup));
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        string journal = Path.Combine(_root, "journal.json");
        bool existed = Directory.Exists(target);
        File.WriteAllText(journal, JsonSerializer.Serialize(new InstallJournal(pending, existed)));
        try
        {
            if (existed) Directory.Move(target, backup);
            Directory.Move(stage, target);
            File.Delete(journal);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
            if (Directory.Exists(target) || !existed) File.Delete(journal);
            throw;
        }
    }

    private sealed record InstallJournal(PendingExtension Package, bool Existed);
    private void RecoverInterrupted()
    {
        string journal = Path.Combine(_root, "journal.json");
        if (!File.Exists(journal)) return;
        var state = JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(journal)) ?? throw new InvalidDataException("插件安装日志无效。");
        ValidateId(state.Package.Id);
        string root = state.Package.Kind == RepositoryExtensionKind.Module ? UserModules : UserPlugins;
        string target = UpdatePackage.SafePath(root, state.Package.Id);
        string backup = UpdatePackage.SafePath(Path.Combine(_root, "backup"), state.Package.Id);
        string stage = UpdatePackage.SafePath(Path.Combine(_root, "staged"), state.Package.Stage);
        if (Directory.Exists(target) && !Directory.Exists(stage))
        {
            // The new package was moved successfully before the process stopped.
            WritePending(ReadPending().Where(item => item.Id != state.Package.Id).ToArray());
        }
        else if (!Directory.Exists(target) && state.Existed && Directory.Exists(backup)) Directory.Move(backup, target);
        File.Delete(journal);
    }

    private List<PendingExtension> ReadPending()
    {
        if (!File.Exists(_pendingPath)) return new();
        try { return JsonSerializer.Deserialize<List<PendingExtension>>(File.ReadAllText(_pendingPath)) ?? new(); }
        catch (JsonException)
        {
            File.Move(_pendingPath, _pendingPath + ".invalid-" + Guid.NewGuid().ToString("N"));
            Results.Add(new("", false, "待安装列表损坏，已保留原文件，可重新下载插件。"));
            return new();
        }
    }
    private void WritePending(IReadOnlyList<PendingExtension> pending)
    {
        Directory.CreateDirectory(_root);
        string temporary = _pendingPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending)); File.Move(temporary, _pendingPath, true);
        _pending = pending.ToArray();
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(_root);
        return new FileStream(UpdatePackage.SafePath(_root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void DeleteStage(string name)
    {
        string stage = UpdatePackage.SafePath(Path.Combine(_root, "staged"), name);
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
    }
    private static void ValidateId(string id)
    {
        if (id is null || !ExtensionFeed.Known.ContainsKey(id)) throw new InvalidDataException("插件 ID 不属于仓库插件目录。");
    }
}
