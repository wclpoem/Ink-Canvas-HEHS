using System.Text.Json;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;

namespace InkCanvas.Features.Persistence;

public sealed record ArchiveSaveResult(string Path, DateTimeOffset SavedAtUtc, int PageCount, long Bytes, string Reason, bool Automatic);

/// <summary>Serializes whole-board backups, tracks edits during a save, and retains marked automatic archives.</summary>
public sealed class BoardArchiveService : IDisposable
{
    private readonly BoardService _board;
    private readonly Func<ArchiveSettings> _settings;
    private readonly string _historyPath;
    private readonly string _kind;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<InkController> _observed = new();
    private long _revision, _savedRevision = -1, _structureVersion = -1;
    private string _savedConfiguration;
    private int _interval;
    private bool _enabled, _disposed;
    public bool IsSaving { get; private set; }
    public string LastError { get; private set; }
    public ArchiveSaveResult LastSave { get; private set; }
    public ArchiveSaveResult LastAutomaticSave { get; private set; }
    public event Action<ArchiveSaveResult> Saved;
    public DateTimeOffset? NextSaveUtc { get; private set; }
    public bool HasUnsavedChanges => !(_settings().SkipEmptyBoards && !_board.HasContent)
        && (_revision != _savedRevision || Configuration(_settings()) != _savedConfiguration);

    public BoardArchiveService(BoardService board, Func<ArchiveSettings> settings, string settingsDirectory)
        : this(board, settings, settingsDirectory, "ArchiveHistory.json", "board") { }

    internal BoardArchiveService(BoardService board, Func<ArchiveSettings> settings, string settingsDirectory, string historyName, string kind)
    {
        _board = board;
        _settings = settings;
        _historyPath = Path.Combine(settingsDirectory, historyName);
        _kind = kind;
        _board.PageChanged += OnPagesChanged;
        _board.ImagesChanged += MarkChanged;
        _board.ExtensionsChanged += MarkChanged;
        _board.BackgroundChanged += MarkChanged;
        _board.ImagePlacementChanged += OnImagePlacement;
        _board.MediaPlacementChanged += OnMediaPlacement;
        OnPagesChanged();
        Configure();
        try
        {
            if (File.Exists(_historyPath))
            {
                var history = JsonSerializer.Deserialize<History>(File.ReadAllBytes(_historyPath));
                LastSave = history?.LastSave;
                LastAutomaticSave = history?.LastAutomaticSave;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Core.Diagnostics.FileLog.WriteLogToFile("读取存档记录失败 " + ex.Message); }
    }

    public void Configure()
    {
        var settings = _settings();
        int interval = Math.Clamp(settings.AutoSaveMinutes, 1, 120);
        if (_enabled != settings.AutoSaveEnabled || interval != _interval)
            NextSaveUtc = settings.AutoSaveEnabled ? DateTimeOffset.UtcNow.AddMinutes(interval) : null;
        _enabled = settings.AutoSaveEnabled;
        _interval = interval;
    }

    public async Task WaitForPendingAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
    }

    public async Task SaveIfDueAsync(DateTimeOffset? now = null)
    {
        if (_disposed || !_enabled || IsSaving || NextSaveUtc is not DateTimeOffset due || (now ?? DateTimeOffset.UtcNow) < due) return;
        NextSaveUtc = (now ?? DateTimeOffset.UtcNow).AddMinutes(_interval);
        if (_board.Pages.Any(page => page.HasActiveInteraction || page.Playback is not null))
        { NextSaveUtc = (now ?? DateTimeOffset.UtcNow).AddSeconds(5); return; }
        try { await SaveAutomaticAsync("定时保存"); }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("白板自动保存失败 " + ex, Core.Diagnostics.LogType.Error); }
    }

    public async Task<ArchiveSaveResult> SaveAutomaticAsync(string reason, CancellationToken cancellationToken = default, bool force = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _board.FlushExtensions();
            var settings = CopySettings();
            string configuration = Configuration(settings);
            if (settings.SkipEmptyBoards && !_board.HasContent
                || !force && settings.OnlyWhenChanged && _revision == _savedRevision && configuration == _savedConfiguration) return null;
            IsSaving = true;
            LastError = null;
            long revision = _revision;
            var localTime = DateTimeOffset.Now;
            string root = ArchivePolicy.AutomaticDirectory(settings);
            string directory = settings.UseDateFolders ? Path.Combine(root, localTime.ToString("yyyy-MM-dd")) : root;
            string name = ArchivePolicy.FileName(settings, localTime, _board.PageCount, reason);
            var metadata = Metadata(settings, reason, true, _board.PageCount, localTime) with { Kind = _kind };
            string path = Path.Combine(directory, name);
            // Automatic backups always create a new file, including when a template omits its date or time.
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 0) path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".icink");
                try { await _board.SaveBoardArchiveAsync(path, metadata, overwrite: false, cancellationToken); break; }
                catch (IOException) when (File.Exists(path) && attempt < 3) { }
            }
            _savedRevision = revision;
            _savedConfiguration = configuration;
            var result = new ArchiveSaveResult(path, metadata.SavedAtUtc, metadata.PageCount, new FileInfo(path).Length, reason, true);
            LastSave = LastAutomaticSave = result;
            await SaveHistoryAsync();
            await Task.Run(() => PruneAsync(root, path, Math.Clamp(settings.AutomaticCopiesToKeep, 1, 200), _kind, cancellationToken), cancellationToken);
            NotifySaved(result);
            return result;
        }
        catch (Exception ex) { LastError = ex.GetBaseException().Message; throw; }
        finally { IsSaving = false; _gate.Release(); }
    }

    public async Task<ArchiveSaveResult> SaveManualAsync(BoardService target, string path, bool entireBoard, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsSaving = true;
            LastError = null;
            target.FlushExtensions();
            long revision = _revision;
            var settings = CopySettings();
            string configuration = Configuration(settings);
            string reason = entireBoard ? "整板保存" : "当前页保存";
            var metadata = Metadata(settings, reason, false, entireBoard ? target.PageCount : 1, DateTimeOffset.Now)
                with { Kind = ReferenceEquals(target, _board) ? "board" : "annotation" };
            if (entireBoard) await target.SaveBoardArchiveAsync(path, metadata, cancellationToken: cancellationToken);
            else await target.SavePageArchiveAsync(path, metadata, cancellationToken: cancellationToken);
            if (ReferenceEquals(target, _board) && entireBoard)
            {
                _savedRevision = revision;
                _savedConfiguration = configuration;
            }
            var result = new ArchiveSaveResult(path, metadata.SavedAtUtc, metadata.PageCount, new FileInfo(path).Length, reason, false);
            LastSave = result;
            await SaveHistoryAsync();
            NotifySaved(result);
            return result;
        }
        catch (Exception ex) { LastError = ex.GetBaseException().Message; throw; }
        finally { IsSaving = false; _gate.Release(); }
    }

    private ArchiveSettings CopySettings() => JsonSerializer.Deserialize<ArchiveSettings>(JsonSerializer.Serialize(_settings()))!;
    private void NotifySaved(ArchiveSaveResult result)
    {
        foreach (Action<ArchiveSaveResult> callback in Saved?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { callback(result); } catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("存档后处理 " + ex.Message); }
    }

    public async Task RelocateAsync(string previous, string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!previous.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(previous)) return;
            if (LastSave?.Path == previous) LastSave = LastSave with { Path = path, Bytes = new FileInfo(path).Length };
            if (LastAutomaticSave?.Path == previous) LastAutomaticSave = LastAutomaticSave with { Path = path, Bytes = new FileInfo(path).Length };
            await SaveHistoryAsync();
        }
        finally { _gate.Release(); }
    }
    private static string Configuration(ArchiveSettings settings) => JsonSerializer.Serialize(new
    { settings.Title, settings.ClassName, settings.Teacher, settings.DefaultDirectory, settings.AutomaticDirectory, settings.FileNameTemplate, settings.UseDateFolders });
    private static BoardArchiveMetadata Metadata(ArchiveSettings settings, string reason, bool automatic, int pages, DateTimeOffset time) => new()
    {
        Title = (settings.Title ?? "").Trim(), ClassName = (settings.ClassName ?? "").Trim(), Teacher = (settings.Teacher ?? "").Trim(),
        Reason = reason, Automatic = automatic, SavedAtUtc = time.ToUniversalTime(), PageCount = pages,
        ApplicationVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "",
    };

    private async Task SaveHistoryAsync()
    {
        string temporary = _historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new History(LastSave, LastAutomaticSave)));
            File.Move(temporary, _historyPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Core.Diagnostics.FileLog.WriteLogToFile("更新存档记录失败 " + ex.Message); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task PruneAsync(string root, string latestPath, int keep, string kind, CancellationToken cancellationToken)
    {
        try
        {
            var files = new List<(string Path, DateTimeOffset SavedAt)>();
            foreach (var file in StorageFiles.Enumerate(root).Where(file => file.Extension.Equals(".icink", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var metadata = await BoardService.ReadArchiveMetadataAsync(file.FullName, cancellationToken);
                    if (metadata is { Product: "InkCanvas", Automatic: true } && metadata.Kind == kind) files.Add((file.FullName, metadata.SavedAtUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            foreach (var file in files.OrderByDescending(file => file.Path == latestPath).ThenByDescending(file => file.SavedAt).Skip(keep))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (StorageFiles.IsRegular(file.Path)) File.Delete(file.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Core.Diagnostics.FileLog.WriteLogToFile("清理白板自动存档未完成 " + ex.Message); }
    }

    private void OnPagesChanged()
    {
        if (_structureVersion == _board.PageStructureVersion) return;
        var present = _board.Pages.ToHashSet();
        foreach (var page in _observed.Where(page => !present.Contains(page)).ToArray()) { Observe(page, false); _observed.Remove(page); }
        foreach (var page in _board.Pages.Where(page => !_observed.Contains(page))) { Observe(page, true); _observed.Add(page); }
        _structureVersion = _board.PageStructureVersion;
        MarkChanged();
    }
    private void Observe(InkController page, bool attach)
    {
        if (attach) { page.Document.Changed += MarkChanged; page.History.Changed += MarkChanged; page.ViewChanged += OnViewChanged; }
        else { page.Document.Changed -= MarkChanged; page.History.Changed -= MarkChanged; page.ViewChanged -= OnViewChanged; }
    }
    private void OnImagePlacement(BoardImageAsset image) => MarkChanged();
    private void OnMediaPlacement(BoardMediaAsset media) => MarkChanged();
    private void OnViewChanged(System.Numerics.Matrix3x2 transform) => MarkChanged();
    private void MarkChanged() => _revision++;
    private sealed record History(ArchiveSaveResult LastSave, ArchiveSaveResult LastAutomaticSave);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _board.PageChanged -= OnPagesChanged;
        _board.ImagesChanged -= MarkChanged;
        _board.ExtensionsChanged -= MarkChanged;
        _board.BackgroundChanged -= MarkChanged;
        _board.ImagePlacementChanged -= OnImagePlacement;
        _board.MediaPlacementChanged -= OnMediaPlacement;
        foreach (var page in _observed) Observe(page, false);
        _observed.Clear();
    }
}
