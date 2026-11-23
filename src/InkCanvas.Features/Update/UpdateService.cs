using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace InkCanvas.Features.Update;

public sealed partial class UpdateService : IDisposable
{
    private readonly UpdateTransport _transport;
    private FileStream _lease;
    private string _leasedDirectory;
    public UpdateService(string repository = "QiXinia/Ink-Canvas-HEHS", UpdateTransport transport = null, UpdateCache cache = null, bool? preferMsi = null)
    {
        if (repository.Split('/').Length != 2 || repository.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid GitHub repository.", nameof(repository));
        Repository = repository;
        _transport = transport ?? new UpdateTransport();
        Cache = cache ?? new UpdateCache();
        PreferMsi = preferMsi ?? File.Exists(Path.Combine(AppContext.BaseDirectory, ".inkcanvas-msi"));
        _transport.ProgressChanged += ForwardProgress;
    }

    public UpdateCache Cache { get; }
    public bool PreferMsi { get; }
    public void ReleaseCache() { _lease?.Dispose(); _lease = null; _leasedDirectory = null; }
    public event Action<UpdateProgress> ProgressChanged;
    private void ForwardProgress(UpdateProgress progress) => ProgressChanged?.Invoke(progress);

    public string Repository { get; }
    public string LastSource => _transport.LastSource;
    public event Action<string> StatusChanged { add => _transport.StatusChanged += value; remove => _transport.StatusChanged -= value; }
    public AppVersion CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(3, 0, 0);
            return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellation = default)
    {
        string json;
        try
        {
            json = await _transport.GetTextAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", text =>
            {
                try { using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == JsonValueKind.Array; }
                catch (JsonException) { return false; }
            }, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!cancellation.IsCancellationRequested)
        {
            Core.Diagnostics.FileLog.WriteLogToFile("Update: 版本 API 不可用，尝试公开发布页。");
            try { return await GetWebReleasesAsync(cancellation).ConfigureAwait(false); }
            catch (HttpRequestException) when (!cancellation.IsCancellationRequested)
            {
                Core.Diagnostics.FileLog.WriteLogToFile("Update: 发布页不可用，尝试最新稳定版的校验清单。");
                string sums = await _transport.GetTextAsync($"https://github.com/{Repository}/releases/latest/download/SHA256SUMS.txt",
                    text => ReleaseFeed.ParseLatestChecksums(text, Repository, PreferMsi) is not null, cancellation).ConfigureAwait(false);
                return new[] { ReleaseFeed.ParseLatestChecksums(sums, Repository, PreferMsi) };
            }
        }
        return ReleaseFeed.ParseStableList(json, PreferMsi).Where(release => (release.IsPortable || release.IsMsi) && TrustedAsset(release.InstallerUrl)).ToArray();
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetWebReleasesAsync(CancellationToken token)
    {
        string page = await _transport.GetTextAsync($"https://github.com/{Repository}/releases",
            text => ReleaseWebFeed.ReadEntries(text, Repository).Count > 0, token).ConfigureAwait(false);
        var releases = new List<ReleaseInfo>();
        foreach (var entry in ReleaseWebFeed.ReadEntries(page, Repository))
        {
            string assets = await _transport.GetTextAsync($"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(entry.Tag)}",
                text => ReleaseWebFeed.IsAssetsPage(text, Repository, entry), token).ConfigureAwait(false);
            var release = ReleaseWebFeed.ReadAssets(assets, Repository, entry, PreferMsi);
            if (release is not null && (release.IsPortable || release.IsMsi) && TrustedAsset(release.InstallerUrl)) releases.Add(release);
        }
        return releases;
    }

    public async Task<ReleaseInfo> CheckAsync(CancellationToken cancellation = default)
        => (await GetReleasesAsync(cancellation).ConfigureAwait(false)).FirstOrDefault();

    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress = null, CancellationToken cancellation = default)
    {
        if (release is null || !(release.IsPortable || release.IsMsi) || !TrustedAsset(release.InstallerUrl)
            || release.IsMsi != PreferMsi)
            throw new InvalidDataException("此发行版没有适用于当前安装方式的 WinUI 3 x64 更新包。");
        return await DownloadAssetAsync(new(release.AssetName, release.InstallerUrl, release.InstallerSize,
            release.Sha256, release.ChecksumsUrl), progress, cancellation).ConfigureAwait(false);
    }

    /// <summary>Shared verified download and cache path for application and extension release assets.</summary>
    public async Task<string> DownloadAssetAsync(RepositoryAsset asset, IProgress<double> progress = null, CancellationToken cancellation = default)
    {
        if (asset is null || !TrustedAsset(asset.Url) || string.IsNullOrWhiteSpace(asset.Name))
            throw new InvalidDataException("此文件不是当前仓库的 Release 附件。");
        string hash = asset.Sha256;
        if (string.IsNullOrEmpty(hash) && TrustedAsset(asset.ChecksumsUrl))
        {
            string sums = await _transport.GetTextAsync(asset.ChecksumsUrl, text => Checksum(text, asset.Name) is not null, cancellation).ConfigureAwait(false);
            hash = Checksum(sums, asset.Name);
        }
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("此安装包缺少 SHA256 校验信息。");
        string directory = Cache.PackageDirectory(hash);
        if (_leasedDirectory != directory)
        {
            _lease?.Dispose(); _lease = null; _leasedDirectory = null;
            _lease = await Cache.AcquireAsync(directory, cancellation).ConfigureAwait(false);
            _leasedDirectory = directory;
        }
        string path = Path.Combine(directory, asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "package.msi" : "package.zip");
        if (File.Exists(path))
        {
            ProgressChanged?.Invoke(new(UpdateStage.Verifying, Source: "本地缓存"));
            await using var cached = File.OpenRead(path);
            string cachedHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(cached, cancellation).ConfigureAwait(false));
            if ((asset.Size <= 0 || cached.Length == asset.Size) && cachedHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ProgressChanged?.Invoke(new(UpdateStage.Verifying, Fraction: 1, Source: "本地缓存"));
                return path;
            }
        }
        await _transport.DownloadAsync(asset.Url, path, asset.Size, hash, progress, cancellation).ConfigureAwait(false);
        return path;
    }

    public async Task<string> PrepareAsync(ReleaseInfo release, string archive, string target, bool restart = true, CancellationToken token = default)
    {
        string directory = Path.Combine(Path.GetDirectoryName(archive), "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var current = Process.GetCurrentProcess();
        try
        {
            ProgressChanged?.Invoke(new(UpdateStage.Preparing));
            UpdatePlan plan;
            if (release.IsMsi)
            {
                if (!PreferMsi || !File.Exists(Path.Combine(target, ".inkcanvas-msi"))) throw new InvalidDataException("目标目录不是 MSI 安装目录。");
                string copy = Path.Combine(directory, "package.msi");
                await using (var input = File.OpenRead(archive))
                await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                await using var staged = File.OpenRead(copy);
                string digest = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(staged, token).ConfigureAwait(false));
                string hash = release.Sha256;
                if (string.IsNullOrEmpty(hash))
                {
                    string sums = await _transport.GetTextAsync(release.ChecksumsUrl, text => Checksum(text, release.AssetName) is not null, token).ConfigureAwait(false);
                    hash = Checksum(sums, release.AssetName);
                }
                if (!digest.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MSI 暂存包 SHA256 校验失败。");
                MsiPackage.Validate(copy, release.Version.ToString());
                plan = new(target, directory, release.Version.ToString(), Array.Empty<UpdateFile>(), current.Id,
                    current.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath, restart) { MsiPackage = copy, MsiSha256 = hash };
            }
            else plan = await Task.Run(() => UpdatePackage.PrepareAsync(archive, directory, target, release.Version.ToString(),
                current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath, restart, token), token).ConfigureAwait(false);
            string planPath = Path.Combine(directory, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), token).ConfigureAwait(false);
            return planPath;
        }
        catch
        {
            try { Persistence.StorageFiles.DeleteDirectory(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static Process LaunchUpdate(string planPath, string hostDirectory = null)
    {
        string helper = hostDirectory ?? Path.Combine(AppContext.BaseDirectory, "UpdateHost");
        string copy = Path.Combine(Path.GetDirectoryName(planPath), "host");
        Directory.CreateDirectory(copy);
        foreach (string path in Directory.GetFiles(helper)) File.Copy(path, Path.Combine(copy, Path.GetFileName(path)), true);
        var start = new ProcessStartInfo(Path.Combine(copy, "InkCanvas.UpdateHost.exe"))
        {
            WorkingDirectory = copy, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(Path.GetFullPath(planPath));
        return Process.Start(start) ?? throw new IOException("无法启动更新进程。");
    }

    public static Process LaunchRestart()
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UpdateHost", "InkCanvas.UpdateHost.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--restart");
        start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Environment.ProcessPath);
        return Process.Start(start) ?? throw new IOException("无法启动重启进程。");
    }

    private bool TrustedAsset(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/" + Repository + "/releases/latest/download/", StringComparison.OrdinalIgnoreCase));

    private static string Checksum(string text, string asset)
    {
        foreach (string line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && parts[1].TrimStart('*', ' ').Equals(asset, StringComparison.Ordinal)) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public void Dispose() { ReleaseCache(); _transport.ProgressChanged -= ForwardProgress; _transport.Dispose(); }
}
