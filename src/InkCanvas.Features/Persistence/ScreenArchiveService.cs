using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.System;

namespace InkCanvas.Features.Persistence;

public sealed record ScreenArchiveCapture(IReadOnlyList<InkStroke> Strokes, IReadOnlyList<BoardImageAsset> Images,
    IReadOnlyList<BoardMediaAsset> Media, Matrix3x2 View, ScreenRect Screen, Vector2 OverlayOrigin, float DpiScale,
    long Revision, string Configuration, ArchiveSettings Naming, string Reason);

/// <summary>Captures all world-space objects before asynchronous encoding or canvas replacement.</summary>
public sealed class ScreenArchiveService : IDisposable
{
    public event Action<ArchiveSaveResult> Saved;
    private readonly BoardService _source;
    private readonly Func<ArchiveSettings> _settings;
    private readonly string _settingsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision, _savedRevision = -1;
    private string _savedConfiguration;
    private int _pending, _interval;
    private bool _enabled;
    public bool IsSaving => _pending > 0;
    public ArchiveSaveResult LastSave { get; private set; }
    public string LastError { get; private set; }
    public DateTimeOffset? NextSaveUtc { get; private set; }

    public ScreenArchiveService(BoardService source, Func<ArchiveSettings> settings, string settingsDirectory)
    {
        _source = source; _settings = settings; _settingsDirectory = settingsDirectory;
        source.Current.Document.Changed += MarkChanged;
        source.Current.History.Changed += MarkChanged;
        source.Current.ViewChanged += OnViewChanged;
        source.ImagesChanged += MarkChanged;
        Configure();
        try
        {
            string path = Path.Combine(settingsDirectory, "ScreenArchiveHistory.json");
            if (File.Exists(path))
            {
                using var history = JsonDocument.Parse(File.ReadAllBytes(path));
                if (history.RootElement.TryGetProperty("LastAutomaticSave", out var last)) LastSave = last.Deserialize<ArchiveSaveResult>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Core.Diagnostics.FileLog.WriteLogToFile("读取截图白板存档记录失败 " + ex.Message); }
    }

    public static string DirectoryFor(ArchiveSettings settings) => string.IsNullOrWhiteSpace(settings.ScreenCapture.Directory)
        ? Path.Combine(ArchivePolicy.ResolveDirectory(settings.DefaultDirectory), "ScreenArchives")
        : ArchivePolicy.ResolveDirectory(settings.ScreenCapture.Directory);

    public bool IsEnabled(bool presentation) => _settings().ScreenCapture.Enabled
        && (presentation || _settings().ScreenCapture.IncludeOrdinaryAnnotation);

    public void Configure()
    {
        var settings = _settings().ScreenCapture;
        int minutes = Math.Clamp(settings.IntervalMinutes, 1, 120);
        if (settings.Enabled != _enabled || minutes != _interval)
            NextSaveUtc = settings.Enabled ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
        _interval = minutes; _enabled = settings.Enabled;
    }

    public bool TakeScheduledSave(bool presentation, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        if (!IsEnabled(presentation) || IsSaving || NextSaveUtc is not { } due || time < due) return false;
        if (_source.Current.HasActiveInteraction || _source.Current.Playback is not null)
        { NextSaveUtc = time.AddSeconds(5); return false; }
        NextSaveUtc = time.AddMinutes(_interval);
        return true;
    }

    public ScreenArchiveCapture Prepare(ScreenRect screen, Vector2 overlayOrigin, float dpiScale, Matrix3x2 view,
        bool presentation, string reason, bool force = false)
    {
        if (!IsEnabled(presentation) || _source.Current.Document.Count == 0) return null;
        var naming = JsonSerializer.Deserialize<ArchiveSettings>(JsonSerializer.Serialize(_settings()))!;
        string configuration = JsonSerializer.Serialize(new { naming.Title, naming.ClassName, naming.Teacher, naming.FileNameTemplate,
            naming.DefaultDirectory, naming.UseDateFolders, naming.ScreenCapture.Directory });
        if (!force && naming.ScreenCapture.OnlyWhenChanged && _revision == _savedRevision && configuration == _savedConfiguration) return null;
        if (screen.Width <= 0 || screen.Height <= 0 || !float.IsFinite(dpiScale) || dpiScale <= 0 || !Matrix3x2.Invert(view, out _))
            throw new InvalidOperationException("屏幕或画布坐标无效，无法保存截图白板。");
        naming.AutomaticDirectory = DirectoryFor(naming);
        naming.AutomaticCopiesToKeep = naming.ScreenCapture.CopiesToKeep;
        naming.OnlyWhenChanged = false; naming.SkipEmptyBoards = false;
        return new(_source.Current.Document.Strokes.Select(stroke => stroke.Clone()).ToArray(),
            _source.CurrentImages.Select(image => image.Copy(copyData: false)).ToArray(),
            _source.CurrentMedia.Select(media => media.Copy(copyData: false)).ToArray(),
            view, screen, overlayOrigin, dpiScale, _revision, configuration, naming, reason);
    }

    public async Task<ArchiveSaveResult> SaveAsync(ScreenArchiveCapture capture, byte[] pixels, int width, int height)
    {
        _pending++;
        await _gate.WaitAsync();
        try
        {
            if (width != capture.Screen.Width || height != capture.Screen.Height || pixels.Length != checked(width * height * 4))
                throw new InvalidDataException("截图大小与屏幕坐标不一致。");
            byte[] png = await Task.Run(() => ScreenshotService.EncodePngAsync(pixels, width, height));
            pixels = null;
            var page = CreatePage(capture, png);
            try
            {
                using var writer = new BoardArchiveService(page, () => capture.Naming, _settingsDirectory, "ScreenArchiveHistory.json", "screen");
                var result = await writer.SaveAutomaticAsync(capture.Reason);
                _savedRevision = capture.Revision; _savedConfiguration = capture.Configuration;
                LastSave = result; LastError = null;
                foreach (Action<ArchiveSaveResult> callback in Saved?.GetInvocationList() ?? Array.Empty<Delegate>())
                    try { callback(result); } catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("截图存档后处理 " + ex.Message); }
                return result;
            }
            finally { foreach (var controller in page.Pages) controller.Dispose(); }
        }
        catch (Exception ex) { LastError = ex.GetBaseException().Message; throw; }
        finally { _pending--; _gate.Release(); }
    }

    public static BoardService CreatePage(ScreenArchiveCapture capture, byte[] backgroundPng)
    {
        if (backgroundPng is not { Length: > 0 } || !Matrix3x2.Invert(capture.View, out var inverse))
            throw new InvalidDataException("截图或画布变换无效。");
        var page = new BoardService();
        page.RenamePage(0, capture.Naming.Title);
        page.Current.Document.AddRange(capture.Strokes);
        foreach (var image in capture.Images) page.SetImagePresent(page.Current, image, true);
        foreach (var media in capture.Media) page.SetMediaPresent(page.Current, media, true);
        // Normalize the snapshot's object order so the screenshot stays behind even legacy or minimum-valued layers.
        long order = 1;
        foreach (var entry in page.Current.DrawingObjects) { var item = entry; item.LayerOrder = order++; }
        var origin = new Vector2(capture.Screen.X, capture.Screen.Y);
        var offset = (origin - capture.OverlayOrigin) / capture.DpiScale;
        var background = new BoardImageAsset
        {
            Name = "屏幕截图背景", Data = backgroundPng, ContentType = "image/png", LayerOrder = 0,
            Width = capture.Screen.Width / capture.DpiScale, Height = capture.Screen.Height / capture.DpiScale,
            Transform = BoardElementMath.Write(Matrix3x2.CreateTranslation(offset) * inverse),
            Metadata = JsonSerializer.Serialize(new { Kind = "screen-capture-background", capture.Screen, capture.DpiScale }),
        };
        page.SetImagePresent(page.Current, background, true);
        page.Current.ViewTransform = capture.View;
        page.Current.NotifyElementsChanged();
        return page;
    }

    public void ReportFailure(Exception error) => LastError = error.GetBaseException().Message;
    public async Task RelocateAsync(string previous, string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (!previous.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(previous)) return;
            if (LastSave?.Path == previous) LastSave = LastSave with { Path = path, Bytes = new FileInfo(path).Length };
            string history = Path.Combine(_settingsDirectory, "ScreenArchiveHistory.json");
            if (!File.Exists(history)) return;
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(history));
            bool changed = false;
            foreach (string property in new[] { "LastSave", "LastAutomaticSave" })
                if (json?[property]?["Path"]?.GetValue<string>() == previous) { json[property]["Path"] = path; json[property]["Bytes"] = new FileInfo(path).Length; changed = true; }
            if (changed)
            {
                await File.WriteAllTextAsync(history + ".tmp", json.ToJsonString());
                File.Move(history + ".tmp", history, true);
            }
        }
        finally { _gate.Release(); }
    }
    private void MarkChanged() => _revision++;
    private void OnViewChanged(Matrix3x2 view) => MarkChanged();
    public void Dispose()
    {
        _source.Current.Document.Changed -= MarkChanged;
        _source.Current.History.Changed -= MarkChanged;
        _source.Current.ViewChanged -= OnViewChanged;
        _source.ImagesChanged -= MarkChanged;
    }
}
