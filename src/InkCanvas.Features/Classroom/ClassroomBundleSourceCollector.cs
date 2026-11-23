using InkCanvas.Core.Classroom;
using InkCanvas.Features.Ppt;
using System.Text.Json;

namespace InkCanvas.Features.Classroom;

/// <summary>资源包候选文件来源，用于窗口分组展示与打包顺序。</summary>
public enum ClassroomBundleSourceKind
{
    Whiteboard,
    PptAnnotation,
    ExampleLibrary,
    Recording,
}

/// <summary>一个已确定要进入资源包的只读源文件。</summary>
public sealed record ClassroomBundleSourceFile(
    string SourcePath,
    string FileName,
    ClassroomBundleSourceKind Kind,
    long SizeBytes);

/// <summary>
/// 资源包收集结果：最终入选文件（已去重并受数量/大小上限约束）、实际总字节，
/// 以及因上限被跳过的文件数量，便于窗口如实提示「已截断」。
/// </summary>
public sealed record ClassroomBundleSourcePlan(
    IReadOnlyList<ClassroomBundleSourceFile> Files,
    long TotalBytes,
    int SkippedByLimit,
    int SkippedBySize)
{
    public bool IsEmpty => Files.Count == 0;

    public bool Truncated => SkippedByLimit > 0 || SkippedBySize > 0;

    public static ClassroomBundleSourcePlan Empty { get; } =
        new(Array.Empty<ClassroomBundleSourceFile>(), 0, 0, 0);
}

/// <summary>
/// 课堂资源包来源配置。每个目录都可为空：为空表示本机没有这类数据，收集器直接跳过。
/// 所有源目录只读访问，绝不创建、移动或改写源文件。
/// </summary>
public sealed record ClassroomBundleSourceOptions
{
    /// <summary>白板存档目录（顶层 <c>*.icink</c>）。</summary>
    public string WhiteboardDirectory { get; init; }

    /// <summary>课件批注根目录（其下为 <c>Auto Saved - Presentations</c>）。</summary>
    public string PptAnnotationRoot { get; init; }

    /// <summary>例题库目录（包含 <c>examples.json</c>）。</summary>
    public string ExampleLibraryDirectory { get; init; }

    /// <summary>插件录制目录（顶层 <c>*.icink</c> 录制容器）。</summary>
    public string RecordingDirectory { get; init; }

    /// <summary>白板存档最多收集数量，按修改时间倒序取最近文件。</summary>
    public int MaxWhiteboardFiles { get; init; } = 8;

    /// <summary>课件批注最多收集的课件目录数量，按目录修改时间倒序。</summary>
    public int MaxPresentationDirectories { get; init; } = 4;

    /// <summary>插件录制最多收集数量，按修改时间倒序。</summary>
    public int MaxRecordingFiles { get; init; } = 8;

    /// <summary>资源包总文件数上限（含 manifest 之外的条目）。</summary>
    public int MaxFiles { get; init; } = 64;

    /// <summary>单个文件大小上限；超过时跳过并计入 <see cref="ClassroomBundleSourcePlan.SkippedBySize"/>。</summary>
    public long MaxFileBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>总大小上限；按收集顺序累计，超出后跳过剩余文件。</summary>
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>
/// 课堂资源包候选收集器：白板存档 + 课件批注（manifest 与登记的 slide <c>.icink</c>）
/// + 例题库 + 插件录制包。按固定顺序去重、限量、限大小，输出确定性的文件计划。
/// <para>
/// 只读取目录元数据与 PPT manifest，不解码笔画、不加载录制时间轴；同名文件不在收集阶段改名，
/// 由 <see cref="ClassroomResourceBundle.Archive"/> 统一追加 <c>-1</c> 后缀，保持既有 zip 结构。
/// </para>
/// </summary>
public static class ClassroomBundleSourceCollector
{
    public const string Extension = ".icink";
    public const string PptManifestName = PptInkFileStore.ManifestFileName;


    public static ClassroomBundleSourcePlan Collect(ClassroomBundleSourceOptions options)
    {
        options ??= new ClassroomBundleSourceOptions();

        var candidates = new List<ClassroomBundleSourceFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRecordingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedBySize = 0;

        AddWhiteboard(options, candidates, seenPaths, ref skippedBySize);
        AddPptAnnotations(options, candidates, seenPaths, ref skippedBySize);
        AddExampleLibrary(options, candidates, seenPaths, ref skippedBySize);
        AddRecordings(options, candidates, seenPaths, seenRecordingIds, ref skippedBySize);

        var files = new List<ClassroomBundleSourceFile>();
        long totalBytes = 0;
        int skippedByLimit = 0;
        foreach (ClassroomBundleSourceFile candidate in candidates)
        {
            if (files.Count >= Math.Max(0, options.MaxFiles))
            {
                skippedByLimit++;
                continue;
            }

            if (totalBytes + candidate.SizeBytes > Math.Max(0, options.MaxTotalBytes))
            {
                skippedBySize++;
                continue;
            }

            files.Add(candidate);
            totalBytes += candidate.SizeBytes;
        }

        return new ClassroomBundleSourcePlan(files, totalBytes, skippedByLimit, skippedBySize);
    }

    /// <summary>白板存档：顶层 <c>*.icink</c>，按修改时间倒序取最近 N 个。</summary>
    private static void AddWhiteboard(ClassroomBundleSourceOptions options,
        List<ClassroomBundleSourceFile> candidates, HashSet<string> seenPaths, ref int skippedBySize)
    {
        foreach (FileInfo file in EnumerateRecentFiles(options.WhiteboardDirectory,
                     "*" + Extension, Math.Max(0, options.MaxWhiteboardFiles)))
        {
            AddFile(file, ClassroomBundleSourceKind.Whiteboard, options, candidates, seenPaths, ref skippedBySize, null);
        }
    }

    /// <summary>
    /// 课件批注：每个课件目录先取 manifest，再按 manifest 登记的 slide 文件名取 <c>.icink</c>；
    /// manifest 缺失或损坏时回退到目录内 <c>slide-*.icink</c>，并跳过 manifest 未登记的孤立文件。
    /// </summary>
    private static void AddPptAnnotations(ClassroomBundleSourceOptions options,
        List<ClassroomBundleSourceFile> candidates, HashSet<string> seenPaths, ref int skippedBySize)
    {
        if (string.IsNullOrWhiteSpace(options.PptAnnotationRoot)) return;

        string presentations = Path.Combine(Path.GetFullPath(options.PptAnnotationRoot),
            PptInkFileStore.PresentationsDirectoryName);
        if (!Directory.Exists(presentations)) return;

        var directories = new List<DirectoryInfo>();
        foreach (string directory in Directory.EnumerateDirectories(presentations))
        {
            try { directories.Add(new DirectoryInfo(directory)); }
            catch { /* 单个课件目录不可读时跳过，不影响其余批注。 */ }
        }

        foreach (DirectoryInfo directory in directories
                     .OrderByDescending(item => item.LastWriteTimeUtc)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, options.MaxPresentationDirectories)))
        {
            string manifestPath = Path.Combine(directory.FullName, PptManifestName);
            bool hasManifest = File.Exists(manifestPath);
            if (hasManifest)
            {
                AddFile(new FileInfo(manifestPath), ClassroomBundleSourceKind.PptAnnotation,
                    options, candidates, seenPaths, ref skippedBySize, null);
            }

            // manifest 已登记页时，未登记的孤立 .icink 不进包（保持按 slideId 收集的语义）。
            IReadOnlyList<string> slideFiles = hasManifest
                ? ReadManifestSlideFiles(manifestPath)
                : EnumerateSlideFiles(directory.FullName);

            foreach (string fileName in slideFiles)
            {
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                // manifest 内的文件名必须是纯文件名且以 .icink 结尾，拒绝路径穿越。
                if (Path.GetFileName(fileName) != fileName
                    || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(directory.FullName, fileName);
                if (!File.Exists(path)) continue;
                AddFile(new FileInfo(path), ClassroomBundleSourceKind.PptAnnotation,
                    options, candidates, seenPaths, ref skippedBySize, directory.Name);
            }

        }
    }

    /// <summary>例题库：只有一个 <c>examples.json</c>；不存在时跳过。</summary>
    private static void AddExampleLibrary(ClassroomBundleSourceOptions options,
        List<ClassroomBundleSourceFile> candidates, HashSet<string> seenPaths, ref int skippedBySize)
    {
        if (string.IsNullOrWhiteSpace(options.ExampleLibraryDirectory)) return;

        string path = Path.Combine(Path.GetFullPath(options.ExampleLibraryDirectory),
            ExampleLibraryStore.FileName);
        if (!File.Exists(path)) return;

        AddFile(new FileInfo(path), ClassroomBundleSourceKind.ExampleLibrary,
            options, candidates, seenPaths, ref skippedBySize, null);
    }

    /// <summary>插件录制包：顶层 <c>*.icink</c>，按修改时间倒序取最近 N 个，只读复制。</summary>
    private static void AddRecordings(ClassroomBundleSourceOptions options,
        List<ClassroomBundleSourceFile> candidates, HashSet<string> seenPaths, HashSet<string> seenRecordingIds,
        ref int skippedBySize)
    {
        foreach (FileInfo file in EnumerateRecentFiles(options.RecordingDirectory,
                     "*" + Extension, Math.Max(0, options.MaxRecordingFiles)))
        {
            // 录制容器按会话 Id 命名；同名 Id 只保留最近一次，避免重复打包同一课。
            if (!seenRecordingIds.Add(Path.GetFileNameWithoutExtension(file.Name))) continue;
            AddFile(file, ClassroomBundleSourceKind.Recording, options, candidates, seenPaths, ref skippedBySize, null);
        }
    }

    private static bool AddFile(FileInfo file, ClassroomBundleSourceKind kind,
        ClassroomBundleSourceOptions options, List<ClassroomBundleSourceFile> candidates,
        HashSet<string> seenPaths, ref int skippedBySize, string displayPrefix)
    {
        if (file is null || !file.Exists) return false;

        string fullPath;
        try { fullPath = file.FullName; }
        catch { return false; }

        // 同一路径可能同时出现在白板目录与录制目录（例如两者配置为同一目录），只保留首次出现。
        if (!seenPaths.Add(fullPath)) return false;

        long size;
        try { size = file.Length; }
        catch { seenPaths.Remove(fullPath); return false; }

        if (options.MaxFileBytes > 0 && size > options.MaxFileBytes)
        {
            // 单文件超限：不进入候选，但仍算作被大小限制跳过。
            skippedBySize++;
            return false;
        }

        string name = string.IsNullOrWhiteSpace(displayPrefix)
            ? file.Name
            : displayPrefix + "/" + file.Name;
        candidates.Add(new ClassroomBundleSourceFile(fullPath, name, kind, size));
        return true;
    }

    private static IEnumerable<FileInfo> EnumerateRecentFiles(string directory, string pattern, int take)
    {
        if (take <= 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<FileInfo>();

        var files = new List<FileInfo>();
        foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try { files.Add(new FileInfo(path)); }
            catch { /* 单个文件不可读时跳过。 */ }
        }

        return files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take);
    }

    private static IReadOnlyList<string> EnumerateSlideFiles(string directory)
    {
        var names = new List<string>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "slide-*" + Extension,
                         SearchOption.TopDirectoryOnly))
                names.Add(Path.GetFileName(path));
        }
        catch { /* 目录不可读时返回空列表。 */ }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>读取 manifest 中登记的 slide 文件名；损坏时返回空列表，由调用方回退。</summary>
    private static IReadOnlyList<string> ReadManifestSlideFiles(string manifestPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("slides", out JsonElement slides)
                || slides.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var files = new List<string>();
            foreach (JsonElement slide in slides.EnumerateArray())
            {
                if (slide.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty property in slide.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "file", StringComparison.OrdinalIgnoreCase)) continue;
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        string file = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(file)) files.Add(file);
                    }
                    break;
                }
            }
            return files;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}