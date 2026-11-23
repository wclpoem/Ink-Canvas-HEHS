using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;

namespace InkCanvas.App.Services;

internal sealed class CloudSyncHost : ICloudSyncHost, IDisposable
{
    private static readonly string[] PortableGroups = { "appearance", "canvas", "gesture", "inkToShape", "behavior", "features" };
    private readonly AppServices _services;
    public CloudSyncHost(AppServices services)
    {
        _services = services;
        services.Archives.Saved += Saved;
        services.ScreenArchives.Saved += Saved;
    }
    public string DataDirectory => Path.Combine(_services.Store.Directory, "PluginData", "inkcanvas.cloud-sync");
    public string DefaultDirectory => ArchivePolicy.ResolveDirectory(_services.Settings.Archive.DefaultDirectory);
    public event Action<string> BoardSaved;
    public event Action PreferencesSaved;
    internal void NotifyPreferencesSaved()
    {
        foreach (Action observer in PreferencesSaved?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { observer(); } catch { }
    }
    private void Saved(ArchiveSaveResult result)
    {
        foreach (Action<string> observer in BoardSaved?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { observer(result.Path); } catch { }
    }
    public async Task<string> SaveSnapshotAsync(CancellationToken token)
    {
        if (!_services.Board.HasContent || _services.Board.Pages.Any(page => page.HasActiveInteraction || page.Playback is not null)) return null;
        var saved = await _services.Archives.SaveAutomaticAsync("云同步", token);
        string path = saved?.Path ?? _services.Archives.LastSave?.Path;
        return File.Exists(path) ? path : null;
    }
    public string ExportPreferences()
    {
        var all = JsonSerializer.SerializeToNode(_services.Settings).AsObject();
        var portable = new JsonObject();
        foreach (string group in PortableGroups) portable[group] = all[group]?.DeepClone();
        return portable.ToJsonString();
    }
    public void ImportPreferences(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidDataException("云端配置大小超限。");
        var values = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("云端配置无效。");
        if (values.Any(pair => !PortableGroups.Contains(pair.Key))) throw new InvalidDataException("云端配置包含设备或敏感设置。");
        var merged = JsonSerializer.SerializeToNode(_services.Settings).AsObject();
        foreach (var pair in values) merged[pair.Key] = pair.Value?.DeepClone() ?? throw new InvalidDataException("云端配置分组为空。");
        var restored = merged.Deserialize<AppSettings>() ?? throw new InvalidDataException("云端配置无效。");
        var previous = _services.Settings.Clone();
        try
        {
            Apply(restored);
            _services.ApplySettings();
            _services.SaveSettings();
        }
        catch
        {
            Apply(previous);
            _services.ApplySettings();
            throw;
        }
    }
    private void Apply(AppSettings restored)
    {
        _services.Settings.Appearance = restored.Appearance;
        _services.Settings.Canvas = restored.Canvas;
        _services.Settings.Gesture = restored.Gesture;
        _services.Settings.InkToShape = restored.InkToShape;
        _services.Settings.PowerPoint = restored.PowerPoint;
        _services.Settings.Features = restored.Features;
    }
    public void Dispose()
    {
        _services.Archives.Saved -= Saved;
        _services.ScreenArchives.Saved -= Saved;
    }
}
