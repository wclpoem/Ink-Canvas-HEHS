using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Extensions;
using InkCanvas.Features.Update;
using Microsoft.UI.Dispatching;

namespace InkCanvas.App.Services;

/// <summary>Repository package operations share the application updater's transport, hashes and cache leases.</summary>
public sealed partial class ExtensionUpdateCoordinator : IDisposable
{
    private readonly AppServices _services;
    private readonly UpdateService _update;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _operation;
    private bool _disposed;
    private int _operationId;
    private readonly string _catalogPath;
    public event Action Changed;
    public IReadOnlyList<ExtensionRelease> Catalog { get; private set; } = Array.Empty<ExtensionRelease>();
    public string Status { get; private set; } = "获取仓库插件，查看可安装和可更新的版本";
    public UpdateProgress Progress { get; private set; } = new(UpdateStage.Idle);
    public bool IsBusy { get; private set; }
    public bool HasChecked { get; private set; }
    public string ActiveId { get; private set; }
    public AppVersion HostVersion => _update.CurrentVersion;
    public IReadOnlyList<PendingExtension> Pending => _services.ExtensionInstalls.Pending;

    public ExtensionUpdateCoordinator(AppServices services, UpdateService update = null)
    {
        _services = services;
        _update = update ?? new UpdateService(cache: services.UpdateCache);
        _update.StatusChanged += OnStatus;
        _update.ProgressChanged += OnProgress;
        _catalogPath = Path.Combine(services.Store.Directory, "ExtensionUpdates", "catalog.json");
        ReadExplorerVersion();
        try
        {
            if (File.Exists(_catalogPath)) Catalog = (JsonSerializer.Deserialize<ExtensionRelease[]>(File.ReadAllText(_catalogPath)) ?? Array.Empty<ExtensionRelease>())
                .Where(item => item?.Asset is not null && ExtensionFeed.Known.TryGetValue(item.Id ?? "", out var metadata)
                    && metadata.Kind == item.Kind && Version.TryParse(item.Version, out _) && ExtensionFeed.Trusted(item.Asset.Url, _update.Repository)).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public string InstalledVersion(ExtensionRelease release) => release.Kind == RepositoryExtensionKind.Explorer ? _explorerVersion
        : (release.Kind == RepositoryExtensionKind.Module ? _services.Modules.Status : _services.Plugins.Status)
            .FirstOrDefault(item => item.Id == release.Id && item.State != ExtensionState.NotInstalled)?.Version;
    public bool HasUpdate(ExtensionRelease release) => Version.TryParse(InstalledVersion(release), out var installed)
        && Version.Parse(release.Version) > installed;
    public bool FailedInstall(string id) => _services.ExtensionInstalls.Results.Any(item => item.Id == id && !item.Success);
    public string UnavailableReason(ExtensionRelease release)
    {
        if (release.Kind == RepositoryExtensionKind.Module && release.HostVersion > HostVersion) return $"需要先将主程序更新至 {release.HostVersion} 或更新版本。";
        if (string.IsNullOrWhiteSpace(release.Asset.Sha256) && string.IsNullOrWhiteSpace(release.Asset.ChecksumsUrl)) return "该发布文件缺少 SHA256 校验信息。";
        return null;
    }
    public bool CanInstall(ExtensionRelease release) => !IsBusy && UnavailableReason(release) is null
        && (FailedInstall(release.Id) || !Pending.Any(item => item.Id == release.Id && Version.Parse(item.Version) >= Version.Parse(release.Version)))
        && (!Version.TryParse(InstalledVersion(release), out var installed) || Version.Parse(release.Version) > installed);

    public Task CheckAsync() => RunAsync(async token =>
    {
        HasChecked = true;
        SetStatus("正在获取仓库插件列表…", UpdateStage.Checking);
        var catalog = await _update.GetExtensionsAsync(token);
        Catalog = catalog;
        string path = _catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Catalog), token);
            File.Move(path, _catalogPath, true);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        SetStatus($"已获取 {catalog.Count} 个仓库插件 · {_update.LastSource}", UpdateStage.Idle);
    });

    public Task InstallAsync(ExtensionRelease release) => RunAsync(async token =>
    {
        if (UnavailableReason(release) is string reason) throw new InvalidOperationException(reason);
        if (Version.TryParse(InstalledVersion(release), out var installed) && Version.Parse(release.Version) <= installed)
        { SetStatus("已安装相同或更新版本", UpdateStage.Idle); return; }
        if (!FailedInstall(release.Id) && Pending.Any(item => item.Id == release.Id && Version.Parse(item.Version) >= Version.Parse(release.Version)))
        { SetStatus("插件已准备，重启后生效", UpdateStage.Waiting); return; }
        await InstallCoreAsync(release, token);
        SetStatus(release.Kind == RepositoryExtensionKind.Explorer ? $"{release.Name} {release.Version} 已为当前用户安装"
            : $"{release.Name} {release.Version} 已准备，重启后生效", UpdateStage.Waiting);
    });

    public Task UpdateAllAsync() => RunAsync(async token =>
    {
        var updates = Catalog.Where(item => HasUpdate(item) && UnavailableReason(item) is null
            && (FailedInstall(item.Id) || !Pending.Any(pending => pending.Id == item.Id && Version.Parse(pending.Version) >= Version.Parse(item.Version)))).ToArray();
        int completed = 0; var errors = new List<string>();
        foreach (var release in updates)
        {
            token.ThrowIfCancellationRequested();
            try { await InstallCoreAsync(release, token); completed++; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { errors.Add(release.Name + " · " + exception.Message); }
        }
        SetStatus(errors.Count > 0 ? $"已准备 {completed} 个插件，{errors.Count} 个未完成\n" + string.Join("\n", errors)
            : completed > 0 ? $"已准备 {completed} 个插件，重启后统一生效" : "已安装的插件均为最新版本",
            errors.Count > 0 ? UpdateStage.Failed : completed > 0 ? UpdateStage.Waiting : UpdateStage.Idle);
    });

    private async Task InstallCoreAsync(ExtensionRelease release, CancellationToken token)
    {
        ActiveId = release.Id;
        SetStatus("正在下载 " + release.Name + " " + release.Version, UpdateStage.Downloading);
        string archive = await _update.DownloadAssetAsync(release.Asset, cancellation: token);
        SetStatus("正在校验并准备 " + release.Name, UpdateStage.Preparing);
        if (release.Kind == RepositoryExtensionKind.Explorer) await InstallExplorerAsync(archive, release, token);
        else await Task.Run(() => _services.ExtensionInstalls.StageAsync(archive, release, token), token);
        _services.ExtensionInstalls.Results.RemoveAll(result => result.Id == release.Id);
        _update.ReleaseCache();
        Notify();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true; int id = ++_operationId;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Notify();
        try { await operation(_operation.Token); }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested) { if (!_disposed) SetStatus("已取消，已准备的插件会在重启后生效", UpdateStage.Canceled); }
        catch (Exception exception)
        {
            if (!_disposed) SetStatus("插件操作未完成 · " + exception.Message, UpdateStage.Failed);
            Core.Diagnostics.FileLog.WriteLogToFile("插件更新 · " + exception, Core.Diagnostics.LogType.Error);
        }
        finally
        {
            _update.ReleaseCache(); _operation.Dispose(); _operation = null; ActiveId = null; IsBusy = false;
            if (!_disposed && id == _operationId) Notify();
        }
    }
    public void Cancel() { if (Progress.Stage != UpdateStage.Installing) _operation?.Cancel(); }
    private void SetStatus(string text, UpdateStage stage)
    { Status = text; Progress = new(stage, Source: Progress.Source); Notify(); }
    private void OnStatus(string status)
    {
        int operation = _operationId;
        _dispatcher.TryEnqueue(() => { if (!_disposed && IsBusy && operation == _operationId) { Status = status; Notify(); } });
    }
    private void OnProgress(UpdateProgress progress)
    {
        int operation = _operationId;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !IsBusy || operation != _operationId) return;
            Progress = progress; Notify();
        });
    }
    private void Notify() => Changed?.Invoke();
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        _update.StatusChanged -= OnStatus; _update.ProgressChanged -= OnProgress; _update.Dispose();
    }
}
