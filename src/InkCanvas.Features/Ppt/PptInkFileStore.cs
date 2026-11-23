using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using System.Text.Json;

namespace InkCanvas.Features.Ppt;

/// <summary>
/// File-backed per-presentation ink archive. The directory shape mirrors the old WPF layout
/// (&lt;root&gt;/Auto Saved - Presentations/&lt;name&gt;_&lt;slide-count&gt;), but stroke data is stored as
/// the current app's .icink plus a manifest, never as legacy WPF .icstk/.icart ISF.
/// </summary>
public sealed class PptInkFileStore : IPptInkStore
{
    public const int CurrentManifestVersion = 2;
    private const string ManifestName = "InkCanvas-Ppt.json";
    private const string PositionName = "Position";
    private const string SlideFilePrefix = "slide-";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _manifestLock = new();
    private string _cachedManifestPath;
    private DateTime _cachedManifestWriteUtc;
    private long _cachedManifestLength;
    private Dictionary<int, string> _cachedSlideFiles;
    private string _root;
    public void SetAutoSaveRoot(string root) => _root = InkCanvas.Core.Settings.ArchivePolicy.ResolveDirectory(root);

    public PptInkFileStore(string autoSaveRoot)
    {
        _root = string.IsNullOrWhiteSpace(autoSaveRoot)
            ? throw new ArgumentException("An auto-save root directory is required.", nameof(autoSaveRoot))
            : Path.GetFullPath(autoSaveRoot);
    }

    public PptInkArchive Load(PptPresentationInfo presentation) => Load(presentation, out _);

    /// <summary>Reads modern archive metadata without decoding every slide's ink.</summary>
    public PptInkArchive LoadForSession(PptPresentationInfo presentation) => Load(presentation, out _, metadataOnly: true);

    /// <summary>
    /// 同 <see cref="Load(PptPresentationInfo)"/>，并通过 <paramref name="diagnostic"/> 输出
    /// 旧存档目录回填的结果（来源目录、复制数量或失败原因）。回填只复制文件到当前命名目录，
    /// 不修改/删除旧目录；回填成功后重新读取当前目录，使本次返回值即包含回填的 .icink 与 manifest。
    /// 回填失败（或回填后重读异常）时返回回填前第一次读取的结果，且不向调用方抛出异常。
    /// </summary>
    public PptInkArchive Load(PptPresentationInfo presentation, out string diagnostic) => Load(presentation, out diagnostic, metadataOnly: false);

    private PptInkArchive Load(PptPresentationInfo presentation, out string diagnostic, bool metadataOnly)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        diagnostic = null;
        string directory = GetPresentationDirectory(presentation);
        bool directoryExists = Directory.Exists(directory);
        // 目录名含页数：插页/删页后按新页数算出的目录可能尚不存在，或存在但还没有任何墨迹文件。
        // 先按原有逻辑读取一次，作为回填失败时的兜底结果。
        bool needsLegacyRestore = !directoryExists || !HasArchivePayload(directory);
        // 当前目录已有 manifest 时不做整目录回填（会覆盖现有布局），但旧目录可能仍持有
        // 目标 manifest 未登记的页（放映时先写入当前页 manifest 的竞态），需要补齐条目。
        string manifestPath = Path.Combine(directory, ManifestName);
        bool manifestCompletionOnly = !needsLegacyRestore && File.Exists(manifestPath);
        ArchiveReadResult read;
        try
        {
            read = ReadArchive(directory, metadataOnly);
        }
        catch (Exception ex)
        {
            // 目录存在但不可读时保持「Load 不抛异常」：回填流程与调用方都只做降级处理。
            FileLog.WriteLogToFile($"PPT ink load failed: {ex}", LogType.Info);
            read = ArchiveReadResult.Empty;
        }
        if ((needsLegacyRestore || manifestCompletionOnly) &&
            TryRestoreLegacyDirectory(directory, presentation, manifestCompletionOnly, out diagnostic))
        {
            // 回填已写入 .icink 与 manifest，必须重新读取才能让本次 Load 的 Slides 反映旧批注；
            // 重读失败时保留第一次的结果，绝不因回填流程向外抛异常。
            try
            {
                read = ReadArchive(directory, metadataOnly);
            }
            catch (Exception ex)
            {
                diagnostic = $"旧墨迹目录已回填，但重新读取当前目录失败，返回回填前的读取结果：{ex.Message}";
                FileLog.WriteLogToFile($"PPT ink reload after legacy restore failed: {ex}", LogType.Info);
            }
        }

        return new PptInkArchive(presentation, read.LastIndex, read.LastPosition, read.Slides,
            read.LegacyInkFilesDetected, read.LastSlideId, read.ManifestVersion);
    }

    /// <summary>
    /// 按当前目录结构读取一次存档（manifest 优先，其次无 manifest 的按页号命名文件），
    /// 不执行任何回填或写入。目录不存在时返回空结果。
    /// </summary>
    private static ArchiveReadResult ReadArchive(string directory, bool metadataOnly)
    {
        bool directoryExists = Directory.Exists(directory);
        var slides = new List<PptStoredSlide>();
        int? lastIndex = null;
        int? lastSlideId = null;
        int? lastPosition = null;
        int manifestVersion = 1;
        bool manifestRead = false;
        bool hasLegacy = directoryExists && (Directory.EnumerateFiles(directory, "*.icstk", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(directory, "*.icart", SearchOption.TopDirectoryOnly).Any());
        string manifestPath = Path.Combine(directory, ManifestName);
        if (directoryExists && File.Exists(manifestPath))
        {
            try
            {
                Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest?.Slides is not null)
                {
                    manifestRead = true;
                    manifestVersion = manifest.Version;
                    lastIndex = manifest.LastSlideIndex;
                    lastSlideId = manifest.LastSlideId;
                    lastPosition = manifest.LastShowPosition;
                    foreach (var entry in manifest.Slides)
                    {
                        string file = SafeManifestFile(directory, entry.File);
                        if (file is null || !File.Exists(file)) continue;
                        try
                        {
                            bool deferred = metadataOnly && manifest.Version >= CurrentManifestVersion;
                            slides.Add(new PptStoredSlide(entry.SlideId, entry.SlideIndex,
                                deferred ? Array.Empty<InkStroke>() : InkFileStore.Load(file), deferred, entry.StrokeCount));
                        }
                        catch { /* Other slides remain available when one file is damaged. */ }
                    }
                }
            }
            catch { /* Preserve legacy-file diagnosis when a manifest is damaged. */ }
        }
        // Index-named files are the pre-manifest layout; only look for them when no manifest
        // described this directory (id-named files must never be misread as page numbers).
        if (directoryExists && !manifestRead && slides.Count == 0) slides.AddRange(LoadIndexNamedSlides(directory));
        string positionPath = Path.Combine(directory, PositionName);
        if (directoryExists && lastIndex is null && File.Exists(positionPath) &&
            int.TryParse(File.ReadAllText(positionPath), out int legacyPosition) && legacyPosition > 0)
        {
            lastIndex = legacyPosition;
            lastPosition = legacyPosition;
        }
        return new ArchiveReadResult(slides, lastIndex, lastPosition, hasLegacy, lastSlideId, manifestVersion);
    }

    /// <summary>
    /// 旧目录回填：在课件存档根目录下查找 <c>&lt;课件名&gt;_&lt;页数&gt;</c> 的兄弟目录（页数后缀不同），
    /// 把其中 <c>.icink</c> 与 manifest 复制到当前命名目录：其它文件同名已存在则跳过、不覆盖；
    /// manifest 已存在时改为「以目标为准 + 补入旧目录尚未登记的页」的合并写回。
    /// 候选目录必须先含 manifest 或 <c>.icink</c>；含 manifest 的优先，再按最近修改时间倒序。
    /// 任何失败都只写入诊断/日志，不抛异常，也不影响本次读取。
    /// </summary>
    /// <param name="manifestCompletionOnly">
    /// 为 <c>true</c> 时表示当前目录已有 manifest，只补齐旧目录中尚未登记的页；
    /// 若没有任何可补内容则不返回成功，也不写诊断（避免正常 Load 产生噪声）。
    /// </param>
    /// <returns>找到并复制/合并了旧目录数据时为 <c>true</c>。</returns>
    private bool TryRestoreLegacyDirectory(string directory, PptPresentationInfo presentation,
        bool manifestCompletionOnly, out string diagnostic)
    {
        diagnostic = null;
        try
        {
            string parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                if (manifestCompletionOnly) return false;
                diagnostic = "当前墨迹目录不存在，且存档根目录不可用，未执行旧目录回填。";
                FileLog.WriteLogToFile($"PPT ink legacy restore skipped: {diagnostic}", LogType.Info);
                return false;
            }

            string prefix = SafeFilePart(presentation.Name) + "_";
            string current = Path.GetFullPath(directory);
            var candidates = new List<LegacyDirectoryCandidate>();
            foreach (string candidate in Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(candidate), current, StringComparison.OrdinalIgnoreCase)) continue;
                string leaf = Path.GetFileName(candidate);
                if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // 只认「课件名_页数」中完整的页数后缀，避免误选课件名相近的其它目录。
                if (!int.TryParse(leaf.AsSpan(prefix.Length), out int legacySlideCount) || legacySlideCount < 0) continue;
                if (!HasArchivePayload(candidate)) continue;
                try
                {
                    candidates.Add(new LegacyDirectoryCandidate(candidate,
                        File.Exists(Path.Combine(candidate, ManifestName)),
                        Directory.GetLastWriteTimeUtc(candidate)));
                }
                catch { /* 单个候选目录不可访问时跳过，其余候选仍可尝试。 */ }
            }

            if (candidates.Count == 0)
            {
                if (manifestCompletionOnly) return false;
                diagnostic = $"未找到课件「{presentation.Name}」的旧墨迹目录，当前目录维持为空。";
                return false;
            }

            Exception lastError = null;
            foreach (LegacyDirectoryCandidate candidate in candidates
                         .OrderByDescending(c => c.HasManifest)
                         .ThenByDescending(c => c.LastWriteUtc))
            {
                if (!TryCopyArchivePayload(candidate.Path, directory, out int copied, out int manifestAdded,
                        out lastError)) continue;
                // 补齐模式：该候选既没有文件可复制、也没有条目可补入，继续尝试下一个候选。
                if (manifestCompletionOnly && copied == 0 && manifestAdded == 0) continue;
                diagnostic = copied > 0 || manifestAdded > 0
                    ? $"已从旧墨迹目录 {candidate.Path} 复制 {copied} 个文件到 {directory}，manifest 补入 {manifestAdded} 条（旧目录保留）。"
                    : $"旧墨迹目录 {candidate.Path} 的数据已在当前目录中，未覆盖任何文件。";
                FileLog.WriteLogToFile($"PPT ink legacy restore: {diagnostic}", LogType.Info);
                return true;
            }

            // 补齐模式下「没有可补条目」属于正常结果，不产生诊断。
            if (manifestCompletionOnly) return false;
            diagnostic = $"旧墨迹目录回填失败（未修改旧目录）：{lastError?.Message}";
            FileLog.WriteLogToFile($"PPT ink legacy restore failed: {diagnostic}", LogType.Info);
            return false;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"PPT ink legacy restore failed: {ex}", LogType.Info);
            if (manifestCompletionOnly) return false;
            diagnostic = $"旧墨迹目录回填失败（未修改旧目录）：{ex.Message}";
            return false;
        }
    }

    /// <summary>当前目录是否已有可读取的墨迹数据（manifest 或任意 <c>.icink</c>）。</summary>
    private static bool HasArchivePayload(string directory)
    {
        if (File.Exists(Path.Combine(directory, ManifestName))) return true;
        return Directory.EnumerateFiles(directory, "*" + InkFileStore.Extension, SearchOption.TopDirectoryOnly).Any();
    }

    /// <summary>
    /// 把旧目录的 <c>.icink</c> 按文件名原样复制（目标同名已存在则跳过，绝不覆盖）；
    /// manifest 则按目标优先的规则合并后写回，避免「目标 manifest 已存在 ⇒ 旧条目整份丢失」。
    /// 返回 <c>false</c> 时 <paramref name="error"/> 说明首个失败原因。
    /// </summary>
    private static bool TryCopyArchivePayload(string source, string target, out int copied, out int manifestAdded,
        out Exception error)
    {
        copied = 0;
        manifestAdded = 0;
        error = null;
        try
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(source, "*" + InkFileStore.Extension,
                         SearchOption.TopDirectoryOnly))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (File.Exists(destination)) continue;
                File.Copy(file, destination, overwrite: false);
                copied++;
            }

            string manifest = Path.Combine(source, ManifestName);
            string manifestTarget = Path.Combine(target, ManifestName);
            if (File.Exists(manifest))
            {
                if (!File.Exists(manifestTarget))
                {
                    // 目标没有 manifest：直接沿用旧 manifest。
                    File.Copy(manifest, manifestTarget, overwrite: false);
                    copied++;
                }
                else
                {
                    // 目标已有 manifest：合并旧目录中目标尚未登记的页，顶层字段保留目标值。
                    manifestAdded = MergeManifest(manifestTarget, manifest, target);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// 把旧 manifest 中「目标 manifest 未登记、且对应 <c>.icink</c> 已复制/存在」的条目合并进目标 manifest。
    /// <para>
    /// 规则：同一 SlideId 以目标条目为准（索引/文件均不覆盖）；补入的条目优先沿用旧 manifest 的
    /// SlideIndex/File，若该 SlideIndex 与已用索引冲突（例如旧 258@3 与目标 259@3），
    /// 则按补入顺序把补入条目改为「当前已用最大索引 + 1」并依次递增，保证合并后索引唯一；
    /// 目标条目的 SlideIndex 始终保持不变。合并结果按 SlideIndex 升序原子写回（tmp + move）。
    /// 目标 manifest 无法解析时按「目标不存在」处理，直接用旧 manifest 覆盖。
    /// </para>
    /// </summary>
    /// <returns>补入的条目数；0 表示未改动目标 manifest。</returns>
    private static int MergeManifest(string manifestTarget, string legacyManifest, string directory)
    {
        int added = 0;
        string temp = null;
        try
        {
            Manifest targetManifest;
            try { targetManifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestTarget), JsonOptions); }
            catch { targetManifest = null; }
            if (targetManifest is null)
            {
                // 目标 manifest 不可解析：视为不存在，直接用旧 manifest（旧条目优先保全）。
                File.Copy(legacyManifest, manifestTarget, overwrite: true);
                return 0;
            }

            Manifest legacy;
            try { legacy = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(legacyManifest), JsonOptions); }
            catch { legacy = null; }
            if (legacy?.Slides is null || legacy.Slides.Count == 0) return 0;

            var merged = new Manifest
            {
                // 顶层字段保留目标值；目标缺失时 Manifest 的默认值即为旧布局的等价语义。
                Version = targetManifest.Version,
                LastSlideIndex = targetManifest.LastSlideIndex,
                LastSlideId = targetManifest.LastSlideId,
                LastShowPosition = targetManifest.LastShowPosition,
                Slides = new List<ManifestSlide>(),
            };
            var slideIds = new HashSet<int>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 索引冲突消解基线：目标（本次）条目的 SlideIndex 保持不变，其占用的索引直接视为「已用」。
            var usedIndexes = new HashSet<int>();
            int maxIndex = 0;
            foreach (ManifestSlide entry in targetManifest.Slides ?? new List<ManifestSlide>())
            {
                if (entry is null || entry.SlideId <= 0 || string.IsNullOrWhiteSpace(entry.File)) continue;
                if (!slideIds.Add(entry.SlideId)) continue;
                files.Add(entry.File);
                usedIndexes.Add(entry.SlideIndex);
                if (entry.SlideIndex > maxIndex) maxIndex = entry.SlideIndex;
                merged.Slides.Add(new ManifestSlide
                {
                    SlideId = entry.SlideId,
                    SlideIndex = entry.SlideIndex,
                    File = entry.File,
                    StrokeCount = entry.StrokeCount,
                });
            }

            foreach (ManifestSlide old in legacy.Slides)
            {
                if (old is null || old.SlideId <= 0 || string.IsNullOrWhiteSpace(old.File)) continue;
                // 同 SlideId 已在目标中登记 ⇒ 以目标为准，不重复追加。
                if (slideIds.Contains(old.SlideId)) continue;
                // 文件名已被目标占用时不能重复登记，避免两个 SlideId 指向同一文件。
                if (files.Contains(old.File)) continue;
                // 只补入「.icink 已复制或本就存在」的条目，避免登记无文件的页。
                string existing = SafeManifestFile(directory, old.File);
                if (existing is null || !File.Exists(existing)) continue;
                // 索引冲突消解：补入条目的索引若已被目标（或先前补入的条目）占用，例如
                // 旧 manifest 的 258@3 与目标 259@3 冲突，则按补入顺序改到「当前已用最大索引 + 1」，
                // 必要时继续递增；目标条目的索引始终不动，从而保证合并后索引唯一。
                int slideIndex = old.SlideIndex;
                if (usedIndexes.Add(slideIndex))
                {
                    if (slideIndex > maxIndex) maxIndex = slideIndex;
                }
                else
                {
                    do { maxIndex++; } while (!usedIndexes.Add(maxIndex));
                    slideIndex = maxIndex;
                }
                merged.Slides.Add(new ManifestSlide
                {
                    SlideId = old.SlideId,
                    SlideIndex = slideIndex,
                    File = old.File,
                    StrokeCount = old.StrokeCount,
                });
                slideIds.Add(old.SlideId);
                files.Add(old.File);
                added++;
            }

            if (added == 0) return 0;
            // OrderBy 是稳定排序：目标条目先入列表，同一 SlideIndex 冲突时目标条目在前。
            merged.Slides = merged.Slides.OrderBy(s => s.SlideIndex).ToList();

            temp = manifestTarget + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(merged, JsonOptions));
            File.Move(temp, manifestTarget, overwrite: true);
            temp = null;
            return added;
        }
        catch (Exception ex)
        {
            // 合并失败不影响已复制的 .icink，也不向调用方抛异常。
            FileLog.WriteLogToFile($"PPT ink legacy manifest merge failed: {ex}", LogType.Info);
            return 0;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch { /* 临时文件清理失败不影响回填结果。 */ }
            }
        }
    }

    public void Save(PptInkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string directory = GetPresentationDirectory(archive.Presentation);
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, ManifestName);
        Manifest previous = null;
        try { if (File.Exists(manifestPath)) previous = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions); }
        catch { /* The new manifest may replace an unreadable one; old data files stay untouched. */ }

        var manifest = new Manifest
        {
            Version = CurrentManifestVersion,
            LastSlideIndex = archive.LastSlideIndex,
            LastSlideId = archive.LastSlideId is > 0 ? archive.LastSlideId : null,
            LastShowPosition = archive.LastShowPosition,
            Slides = new List<ManifestSlide>(),
        };
        foreach (PptStoredSlide item in archive.Slides ?? Array.Empty<PptStoredSlide>())
        {
            if (item is null || item.IsDeferred || item.SlideId <= 0 || item.SlideIndex <= 0 || item.Strokes is null)
                continue;
            // An explicitly empty slide is a saved erase, not a missing partial update.
            // Retain its manifest entry so a later merge cannot resurrect the previous ink.
            string fileName = $"slide-{item.SlideId}.icink";
            InkFileStore.Save(Path.Combine(directory, fileName), item.Strokes);
            manifest.Slides.Add(new ManifestSlide { SlideId = item.SlideId, SlideIndex = item.SlideIndex, File = fileName, StrokeCount = item.Strokes.Count });
        }

        // 本次 archive 可能只是部分页（例如导航只回写了当前可见页），
        // 必须把 previous 中仍有效的其它页条目合并进来，否则保存会丢条目：
        // 第 4 页的 slide-258.icink 文件还在，但 manifest 里没有 258 ⇒ 导航/加载看不到该页批注。
        MergePreviousSlides(directory, previous, manifest.Slides);
        // 按 SlideIndex 升序输出；OrderBy 是稳定排序，本次条目先入列表，
        // 因此同一索引冲突时本次条目排在 previous 条目前面（本次优先）。
        manifest.Slides = manifest.Slides.OrderBy(s => s.SlideIndex).ToList();

        string temp = manifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temp, manifestPath, overwrite: true);
        if (archive.LastSlideIndex is > 0)
            File.WriteAllText(Path.Combine(directory, PositionName), archive.LastSlideIndex.Value.ToString());

        // 注意：这里不再按 manifest 差集删除 .icink。
        // 旧实现会把「previous 有、本次没有」的文件删掉，正是第 4 页批注丢失的直接原因；
        // 且迁移契约要求不删除既有数据文件，只允许重写 manifest。
    }

    /// <summary>
    /// 把 previous manifest 中「本次 archive 未包含、但文件仍存在」的条目保留到 <paramref name="current"/>。
    /// <para>
    /// 规则：同 SlideId 以本次为准（不重复追加）；previous 条目需满足 SlideId &gt; 0、File 非空、
    /// 文件名安全且对应文件存在，并沿用 previous 的 SlideIndex/File；文件已不存在的条目直接丢弃。
    /// </para>
    /// </summary>
    private static void MergePreviousSlides(string directory, Manifest previous, List<ManifestSlide> current)
    {
        if (previous?.Slides is null || previous.Slides.Count == 0) return;
        var currentSlideIds = new HashSet<int>();
        var currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestSlide entry in current)
        {
            if (entry is null) continue;
            currentSlideIds.Add(entry.SlideId);
            if (!string.IsNullOrWhiteSpace(entry.File)) currentFiles.Add(entry.File);
        }

        foreach (ManifestSlide old in previous.Slides)
        {
            if (old is null || old.SlideId <= 0 || string.IsNullOrWhiteSpace(old.File)) continue;
            // 同 SlideId 已被本次覆盖（索引/文件以本次为准）。
            if (currentSlideIds.Contains(old.SlideId)) continue;
            // 文件名已被本次占用时不能重复登记，避免两个 SlideId 指向同一文件。
            if (currentFiles.Contains(old.File)) continue;
            string existing = SafeManifestFile(directory, old.File);
            if (existing is null || !File.Exists(existing)) continue;
            current.Add(new ManifestSlide { SlideId = old.SlideId, SlideIndex = old.SlideIndex, File = old.File, StrokeCount = old.StrokeCount });
            currentSlideIds.Add(old.SlideId);
            currentFiles.Add(old.File);
        }
    }

    /// <summary>
    /// Reads a single slide's ink straight from the manifest file layout. Used by the neighbor
    /// prefetcher so it never has to touch the session's in-memory state from a worker thread.
    /// Returns <c>null</c> when the slide has no file or it is damaged.
    /// </summary>
    public IReadOnlyList<InkStroke> TryLoadSlide(PptPresentationInfo presentation, int slideId)
    {
        if (presentation is null || slideId <= 0) return null;
        try
        {
            string directory = GetPresentationDirectory(presentation);
            string file = FindSlideFile(directory, slideId);
            if (file is null) return null;
            if (!File.Exists(file)) return null;
            List<InkStroke> strokes = InkFileStore.Load(file);
            return strokes;
        }
        catch { return null; }
    }

    private string FindSlideFile(string directory, int slideId)
    {
        string path = Path.Combine(directory, ManifestName);
        var info = new FileInfo(path);
        if (!info.Exists) return Path.Combine(directory, $"{SlideFilePrefix}{slideId}{InkFileStore.Extension}");
        lock (_manifestLock)
        {
            if (_cachedManifestPath != path || _cachedManifestWriteUtc != info.LastWriteTimeUtc || _cachedManifestLength != info.Length)
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
                var files = new Dictionary<int, string>();
                foreach (var entry in manifest?.Slides ?? new())
                {
                    if (entry is null || entry.SlideId <= 0) continue;
                    string file = SafeManifestFile(directory, entry.File);
                    if (file is not null) files.TryAdd(entry.SlideId, file);
                }
                _cachedSlideFiles = files;
                _cachedManifestPath = path;
                _cachedManifestWriteUtc = info.LastWriteTimeUtc;
                _cachedManifestLength = info.Length;
            }
            return _cachedSlideFiles.GetValueOrDefault(slideId);
        }
    }

    /// <summary>Public so callers can back up or migrate exactly the directory Load reads.</summary>
    public string GetPresentationDirectory(PptPresentationInfo presentation) =>
        Path.Combine(_root, "Auto Saved - Presentations",
            $"{SafeFilePart(presentation.Name)}_{Math.Max(0, presentation.SlideCount)}");

    /// <summary>
    /// Legacy archives stored one file per page number (<c>slide-3.icink</c>) with no manifest.
    /// Returns them with SlideId 0 and the page number as SlideIndex so the migrator can map
    /// them onto live slide ids by index.
    /// </summary>
    private static IEnumerable<PptStoredSlide> LoadIndexNamedSlides(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, SlideFilePrefix + "*" + InkFileStore.Extension,
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(SlideFilePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(name.AsSpan(SlideFilePrefix.Length), out int index) || index <= 0) continue;
            List<InkStroke> strokes;
            try { strokes = InkFileStore.Load(file); }
            catch { continue; }
            if (strokes.Count == 0) continue;
            yield return new PptStoredSlide(0, index, strokes);
        }
    }

    private static string SafeManifestFile(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            !fileName.EndsWith(InkFileStore.Extension, StringComparison.OrdinalIgnoreCase)) return null;
        string full = Path.GetFullPath(Path.Combine(directory, fileName));
        return full.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full : null;
    }

    private static string SafeFilePart(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Presentation" : value;
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result.Replace('/', '_').Replace('\\', '_');
    }

/// <summary>旧存档目录候选：优先含 manifest，再按最近修改时间倒序。</summary>
    private sealed record LegacyDirectoryCandidate(string Path, bool HasManifest, DateTime LastWriteUtc);

    /// <summary><see cref="ReadArchive"/> 的单次读取结果，供回填前后两次读取与最终装配使用。</summary>
    private sealed record ArchiveReadResult(
        List<PptStoredSlide> Slides,
        int? LastIndex,
        int? LastPosition,
        bool LegacyInkFilesDetected,
        int? LastSlideId,
        int ManifestVersion)
    {
        public static ArchiveReadResult Empty { get; } = new(
            new List<PptStoredSlide>(), null, null, false, null, 1);
    }

    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public int? LastSlideIndex { get; set; }
        public int? LastSlideId { get; set; }
        public int? LastShowPosition { get; set; }
        public List<ManifestSlide> Slides { get; set; } = new();
    }
    private sealed class ManifestSlide
    {
        public int SlideId { get; set; }
        public int SlideIndex { get; set; }
        public string File { get; set; }
        public int? StrokeCount { get; set; }
    }
}
