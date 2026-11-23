using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using System.Text.Json;

namespace InkCanvas.Features.Persistence;

/// <summary>Loads and saves settings, tolerating a missing or corrupt file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(string directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");
    }

    public string Directory { get; }

    public string SettingsPath => Path.Combine(Directory, "Settings.json");
    public string BackupPath => SettingsPath + ".bak";
    public string LastError { get; private set; }
    public bool RecoveredFromBackup { get; private set; }
    public DateTime? LastSavedUtc => File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : null;
    private readonly object _gate = new();
    private string _savedJson;
    private bool _protectBackup;

    public AppSettings Load()
    {
        lock (_gate)
        {
            RecoveredFromBackup = false;
            foreach (string path in new[] { SettingsPath, BackupPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                    if (settings is null) continue;
                    RecoveredFromBackup = path == BackupPath;
                    _protectBackup = RecoveredFromBackup;
                    _savedJson = RecoveredFromBackup ? null : JsonSerializer.Serialize(settings, Options);
                    LastError = null;
                    return settings;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { LastError = ex.Message; }
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        try
        {
            string json = JsonSerializer.Serialize(settings, Options);
            if (json == _savedJson && File.Exists(SettingsPath)) return;
            System.IO.Directory.CreateDirectory(Directory);
            string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                bool preserveCurrent = File.Exists(SettingsPath) && !_protectBackup;
                if (preserveCurrent)
                {
                    try { preserveCurrent = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) is not null; }
                    catch (JsonException) { preserveCurrent = false; }
                }
                if (preserveCurrent)
                    File.Replace(temporary, SettingsPath, BackupPath);
                else File.Move(temporary, SettingsPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _savedJson = json; LastError = null;
            _protectBackup = false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}

/// <summary>
/// Stroke persistence.
/// <para>
/// The WPF original stored WPF's Ink Serialized Format (ISF), which depends on
/// <c>System.Windows.Ink</c> and does not exist on WinUI 3. This is a deliberate compatibility
/// break: files use our own compact JSON (<c>.icink</c>) and old <c>.isf</c> archives cannot be
/// read. Per-point widths are stored so a reloaded stroke renders exactly as it was drawn.
/// </para>
/// </summary>
public static class InkFileStore
{
    public const string Extension = ".icink";
    private const int FormatVersion = 1;

    private sealed class FileModel
    {
        public int Version { get; set; } = FormatVersion;
        public List<StrokeModel> Strokes { get; set; } = new();
    }

    private sealed class StrokeModel
    {
        public Guid? Id { get; set; }
        public InkWashBrushOptions InkWash { get; set; } = InkWashBrushOptions.Default;
        public int InkWashSeed { get; set; }
        public long? LayerOrder { get; set; }
        public uint Color { get; set; }
        public float Width { get; set; }
        public bool Highlighter { get; set; }
        public byte HighlighterAlpha { get; set; } = 96;
        public int Taper { get; set; }
        public PenNib Nib { get; set; }
        public float NibRotation { get; set; }
        public bool Shape { get; set; }

        public ShapeSource ShapeSource { get; set; }
        public float[] ObjectFrame { get; set; }
        /// <summary>Rows of [x, y, pressure, width, timeMs, tiltX, tiltY, hasPressure]. Older short rows remain valid.</summary>
        public List<float[]> Points { get; set; } = new();
    }

    public static void Save(string path, IEnumerable<InkStroke> strokes)
    {
        var model = new FileModel();
        foreach (var s in strokes)
        {
            var sm = new StrokeModel
            {
                Id = s.Id,
                InkWash = s.Attributes.InkWash,
                InkWashSeed = s.Attributes.InkWashSeed,
                LayerOrder = s.LayerOrder,
                Color = s.Attributes.Color.PackedArgb,
                Width = s.Attributes.BaseWidth,
                Highlighter = s.Attributes.IsHighlighter,
                HighlighterAlpha = s.Attributes.HighlighterAlpha,
                Taper = (int)s.TaperKind,
                Nib = s.Attributes.Nib,
                NibRotation = s.Attributes.NibRotation,
                Shape = s.IsRecognizedShape,
                ShapeSource = s.ShapeSource,
                ObjectFrame = s.ExportObjectFrame(),
            };
            foreach (var p in s.Points)
                sm.Points.Add(p.HasPressure || p.TiltX != 0 || p.TiltY != 0
                    ? new[] { p.X, p.Y, p.Pressure, p.Width, p.TimeMs, p.TiltX, p.TiltY, p.HasPressure ? 1f : 0f }
                    : new[] { p.X, p.Y, p.Pressure, p.Width, p.TimeMs });
            model.Strokes.Add(sm);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, model);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static List<InkStroke> Load(string path)
    {
        var result = new List<InkStroke>();
        if (!File.Exists(path)) return result;
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
        if (model?.Strokes is null) return result;

        foreach (var sm in model.Strokes)
        {
            var attrs = new StrokeAttributes
            {
                Color = InkColor.FromPackedArgb(sm.Color),
                BaseWidth = sm.Width,
                IsHighlighter = sm.Highlighter,
                HighlighterAlpha = sm.HighlighterAlpha,
                TaperKind = (TaperKind)sm.Taper,
                Nib = Enum.IsDefined(sm.Nib) ? sm.Nib : PenNib.Round,
                NibRotation = float.IsFinite(sm.NibRotation) ? sm.NibRotation : 0,
                InkWash = sm.InkWash.Normalize(),
                InkWashSeed = sm.InkWashSeed,
            };
var stroke = new InkStroke(attrs) { Id = sm.Id ?? Guid.NewGuid(), IsRecognizedShape = sm.Shape, LayerOrder = sm.LayerOrder, ShapeSource = sm.ShapeSource };
            // Restore stored widths verbatim: re-running the taper here would change history.
            stroke.RestorePoints(sm.Points
                .Where(r => r.Length >= 4)
                .Select(r => new InkPoint(r[0], r[1], r[2], r[3], r.Length > 4 ? r[4] : 0f,
                    r.Length > 7 && r[7] > 0, r.Length > 5 ? r[5] : 0, r.Length > 6 ? r[6] : 0)));
            stroke.Complete();
            stroke.ImportObjectFrame(sm.ObjectFrame);
            result.Add(stroke);
        }
        ShapeSource.ShareDefinitions(result);
        return result;
    }
}

/// <summary>Writes stroke snapshots on clear/screenshot/exit, and prunes old ones.</summary>
public sealed class AutoSaveService
{
    private Task<string> _pending = Task.FromResult<string>(null);
    public string LastPath { get; private set; }
    public string LastError { get; private set; }
    public DateTimeOffset? LastSavedUtc { get; private set; }
    public AutoSaveService(Core.Settings.AutomationSettings settings)
    {
        Settings = settings;
    }

    public Core.Settings.AutomationSettings Settings { get; set; }

    public string SaveSnapshot(IEnumerable<InkStroke> strokes, string reason)
        => SaveSnapshotCore(strokes, reason, Settings);

    public Task<string> SaveSnapshotAsync(IEnumerable<InkStroke> strokes, string reason)
    {
        var snapshot = strokes.Select(stroke => stroke.Clone()).ToArray();
        var settings = JsonSerializer.Deserialize<Core.Settings.AutomationSettings>(JsonSerializer.Serialize(Settings));
        var previous = _pending;
        return _pending = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            return SaveSnapshotCore(snapshot, reason, settings);
        });
    }

    public Task WaitForPendingAsync() => _pending;

    private string SaveSnapshotCore(IEnumerable<InkStroke> strokes, string reason, Core.Settings.AutomationSettings settings)
    {
        var list = strokes.ToList();
        if (list.Count < Math.Max(0, settings.MinimumAutomationStrokeNumber)) return null;
        try
        {
            string root = settings.AutoSavedStrokesLocation;
            if (string.IsNullOrWhiteSpace(root)) return null;
            root = ArchivePolicy.ResolveDirectory(root);
            string dir = settings.IsSaveScreenshotsInDateFolders
                ? Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"))
                : root;
            System.IO.Directory.CreateDirectory(dir);
            string safeReason = new string((reason ?? "snapshot").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safeReason}-{Guid.NewGuid().ToString("N")[..8]}{InkFileStore.Extension}");
            InkFileStore.Save(path, list);
            LastPath = path; LastSavedUtc = DateTimeOffset.UtcNow; LastError = null;
            return path;
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            Core.Diagnostics.FileLog.WriteLogToFile("墨迹快照保存失败 " + LastError, Core.Diagnostics.LogType.Error);
            return null;
        }
    }

    /// <summary>Deletes snapshots older than the configured threshold. Opt-in.</summary>
    public int Prune()
    {
        if (!Settings.AutoDelSavedFiles) return 0;
        int removed = 0;
        try
        {
            string root = Settings.AutoSavedStrokesLocation;
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root)) return 0;
            root = Path.GetFullPath(root);
            if (root.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar)) return 0;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return 0;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, Settings.AutoDelSavedFilesDaysThreshold));
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", options))
            {
                try
                {
                    string extension = Path.GetExtension(file);
                    bool snapshot = extension.Equals(InkFileStore.Extension, StringComparison.OrdinalIgnoreCase);
                    string relative = Path.GetRelativePath(root, file);
                    bool screenshot = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        && (relative.StartsWith("Auto Saved - PPT Screenshots" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || relative.StartsWith("Screenshots" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileName(file).StartsWith("Screenshot-", StringComparison.OrdinalIgnoreCase));
                    if (!snapshot && !screenshot) continue;
                    if (File.GetLastWriteTime(file) < cutoff) { File.Delete(file); removed++; }
                }
                catch { /* individual file may be locked */ }
            }
        }
        catch { }
        return removed;
    }
}
