using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Settings;
using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 一条本地例题：记录来源课件 / 幻灯片 / 画板页与讲评文本，便于跨课堂复用。
/// 字段只追加，不改变既有 JSON 结构；历史文件缺少新字段时按缺省值读取。
/// </summary>
public sealed class ExampleEntry
{
    /// <summary>例题唯一标识；为空时由 <see cref="ExampleLibraryStore.Add"/> 生成。</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Topic { get; set; } = "";

    /// <summary>关联知识点，允许为空列表。</summary>
    public List<string> KnowledgePoints { get; set; } = new();

    /// <summary>来源课件名称（例如 <c>3.2 一次函数.pptx</c>）。</summary>
    public string SourcePresentation { get; set; } = "";

    /// <summary>来源幻灯片编号；无来源时为 0。</summary>
    public int SourceSlideId { get; set; }

    /// <summary>来源画板页序号；无来源时为 0。</summary>
    public int BoardPage { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Answer { get; set; } = "";

    /// <summary>教师订正后的讲评文本。</summary>
    public string CorrectedText { get; set; } = "";

    /// <summary>收藏时所在的 AI 课堂会话标识；旧数据为空。</summary>
    public string AiSessionId { get; set; } = "";

    /// <summary>收藏时对应的 AI 请求标识；旧数据为空。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>收藏时的画板标识："main"/"side"/"overlay"/"document"/"ppt"；旧数据为空。</summary>
    public string BoardId { get; set; } = "";

    /// <summary>收藏时的页面持久标识；Guid.Empty 表示旧数据或未记录。</summary>
    public Guid BoardPageId { get; set; }

    /// <summary>AI 原生对象的 GroupId（若收藏时已贴回原生对象）；旧数据为空。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>AI 原生对象 Id（若收藏时已贴回原生对象）；旧数据为空列表。</summary>
    public List<string> ObjectIds { get; set; } = new();

    /// <summary>AI 分步讲解（<see cref="AssistantStep"/> 数组 JSON）；旧数据为空串。</summary>
    public string StepsJson { get; set; } = "";

    /// <summary>AI 标注点（嵌套点数组 JSON）；旧数据为空串。</summary>
    public string AnnotationsJson { get; set; } = "";

    /// <summary>教师是否订正过该例题的讲评文本。</summary>
    public bool TeacherEdited { get; set; }

    /// <summary>最后一次更新（收藏/订正）时间；旧数据为默认值。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>条目结构版本，用于后续增量兼容。</summary>
    public int Version { get; set; } = 1;

    /// <summary>旧文件缺字段时补齐可写集合，保证调用方无需判空。</summary>
    internal void Normalize()
    {
        KnowledgePoints ??= new List<string>();
        ObjectIds ??= new List<string>();
        AiSessionId ??= "";
        RequestId ??= "";
        BoardId ??= "";
        GroupId ??= "";
        StepsJson ??= "";
        AnnotationsJson ??= "";
    }
}

/// <summary>
/// 例题检索条件。所有字段为空或 null 时不参与过滤，即返回全部例题。
/// 文本条件按「包含且忽略大小写」匹配；日期区间为闭区间。
/// </summary>
public sealed class ExampleQuery
{
    public string Topic { get; set; }

    public string KnowledgePoint { get; set; }

    public string SourcePresentation { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    /// <summary>在标题、课题、知识点、答案与订正文本中检索。</summary>
    public string Keyword { get; set; }
}

/// <summary>
/// 本地例题库存储：默认位于 <c>%APPDATA%\InkCanvas\examples.json</c>，采用原子写入
/// （先写 <c>.tmp</c> 再替换正式文件），损坏文件只按空库读取、绝不删除。
/// 通过 <see cref="FeatureSettings.EnableExampleLibrary"/> 可整体回退；读取不受开关影响。
/// </summary>
public static class ExampleLibraryStore
{
    /// <summary>库文件名。</summary>
    public const string FileName = "examples.json";

    /// <summary>当前文件结构版本，写入 <c>version</c> 字段。</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>默认目录：<c>%APPDATA%\InkCanvas</c>。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");

    /// <summary>例题库文件路径；<paramref name="directory"/> 为空时使用默认目录。</summary>
    public static string PathFor(string directory = null)
        => Path.Combine(string.IsNullOrEmpty(directory) ? DefaultDirectory : directory, FileName);

    /// <summary>
    /// 读取全部例题。文件缺失、内容为空或 JSON 损坏时返回空列表；未知字段与更高
    /// <c>version</c> 均被忽略而不是报错，损坏的原文件保留以便人工修复。
    /// </summary>
    public static IReadOnlyList<ExampleEntry> Load(string directory = null)
    {
        string path = PathFor(directory);
        if (!File.Exists(path))
        {
            return Array.Empty<ExampleEntry>();
        }

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ExampleEntry>();
            }

            LibraryFile file = JsonSerializer.Deserialize<LibraryFile>(json, Options);
            if (file?.Examples is null)
            {
                return Array.Empty<ExampleEntry>();
            }

            List<ExampleEntry> entries = file.Examples.Where(entry => entry is not null).ToList();
            foreach (ExampleEntry entry in entries) entry.Normalize();
            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 读取失败一律降级为空库：课堂现场不应因为一个损坏文件导致功能崩溃。
            return Array.Empty<ExampleEntry>();
        }
    }

    /// <summary>
    /// 新增或按 <see cref="ExampleEntry.Id"/> 覆盖一条例题。功能开关关闭时返回 false 且不落盘。
    /// </summary>
    public static bool Add(ExampleEntry entry, string directory = null, FeatureSettings settings = null)
    {
        if (IsDisabled(settings))
        {
            return false;
        }

        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }

        if (entry.CreatedAt == default)
        {
            entry.CreatedAt = DateTime.Now;
        }

        entry.Normalize();
        List<ExampleEntry> all = Load(directory).ToList();
        int index = all.FindIndex(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            all[index] = entry;
        }
        else
        {
            all.Add(entry);
        }

        Save(all, directory);
        return true;
    }

    /// <summary>按 id 删除例题；功能开关关闭或 id 不存在时返回 false。</summary>
    public static bool Delete(string id, string directory = null, FeatureSettings settings = null)
    {
        if (IsDisabled(settings) || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        List<ExampleEntry> all = Load(directory).ToList();
        int removed = all.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (removed == 0)
        {
            return false;
        }

        Save(all, directory);
        return true;
    }

    /// <summary>
    /// 按条件检索例题；<paramref name="query"/> 为 null 或各字段为空时返回全部。
    /// 功能开关关闭时返回空列表（不写入、不影响磁盘数据）。
    /// </summary>
    public static IReadOnlyList<ExampleEntry> Search(
        ExampleQuery query,
        string directory = null,
        FeatureSettings settings = null)
    {
        if (IsDisabled(settings))
        {
            return Array.Empty<ExampleEntry>();
        }

        IReadOnlyList<ExampleEntry> all = Load(directory);
        if (query is null)
        {
            return all;
        }

        IEnumerable<ExampleEntry> result = all;

        if (!string.IsNullOrWhiteSpace(query.Topic))
        {
            result = result.Where(entry => Contains(entry.Topic, query.Topic));
        }

        if (!string.IsNullOrWhiteSpace(query.KnowledgePoint))
        {
            result = result.Where(entry =>
                entry.KnowledgePoints is not null &&
                entry.KnowledgePoints.Any(point => Contains(point, query.KnowledgePoint)));
        }

        if (!string.IsNullOrWhiteSpace(query.SourcePresentation))
        {
            result = result.Where(entry => Contains(entry.SourcePresentation, query.SourcePresentation));
        }

        if (query.From.HasValue)
        {
            result = result.Where(entry => entry.CreatedAt >= query.From.Value);
        }

        if (query.To.HasValue)
        {
            result = result.Where(entry => entry.CreatedAt <= query.To.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            result = result.Where(entry => MatchesKeyword(entry, query.Keyword));
        }

        return result.ToList();
    }

    private static bool IsDisabled(FeatureSettings settings)
        => settings is not null && !settings.EnableExampleLibrary;

    private static bool Contains(string value, string text)
        => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesKeyword(ExampleEntry entry, string keyword)
    {
        if (Contains(entry.Title, keyword) ||
            Contains(entry.Topic, keyword) ||
            Contains(entry.Answer, keyword) ||
            Contains(entry.CorrectedText, keyword))
        {
            return true;
        }

        return entry.KnowledgePoints is not null &&
               entry.KnowledgePoints.Any(point => Contains(point, keyword));
    }

    private static void Save(List<ExampleEntry> entries, string directory)
    {
        string path = PathFor(directory);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var file = new LibraryFile { Version = CurrentVersion, Examples = entries };
        string json = JsonSerializer.Serialize(file, Options);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程；残留的 .tmp 不会被视为正式文件。
        }
    }

    /// <summary>磁盘格式：<c>{ "version": 1, "examples": [...] }</c>，未知字段在读取时忽略。</summary>
    internal sealed class LibraryFile
    {
        public int Version { get; set; } = CurrentVersion;

        public List<ExampleEntry> Examples { get; set; } = new();
    }
}
/// <summary>
/// 例题附加信息的 JSON 编解码：步骤与标注各自序列化为独立字符串字段，
/// 旧文件缺字段时按空串读取，解析失败一律降级为空列表而不是抛异常。
/// </summary>
public static class ExampleEntryCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>编码 AI 分步讲解；空/null 返回空串。</summary>
    public static string EncodeSteps(IReadOnlyList<AssistantStep> steps)
    {
        if (steps is null || steps.Count == 0) return "";
        return JsonSerializer.Serialize(steps, Options);
    }

    /// <summary>解码 AI 分步讲解；空串、非法 JSON 或类型不符时返回空列表。</summary>
    public static IReadOnlyList<AssistantStep> DecodeSteps(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<AssistantStep>();
        try
        {
            return JsonSerializer.Deserialize<List<AssistantStep>>(json, Options) ?? (IReadOnlyList<AssistantStep>)Array.Empty<AssistantStep>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Array.Empty<AssistantStep>();
        }
    }

    /// <summary>编码 AI 标注点（画布相对坐标的嵌套点数组）；空/null 返回空串。</summary>
    public static string EncodeAnnotations(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> annotations)
    {
        if (annotations is null || annotations.Count == 0) return "";
        return JsonSerializer.Serialize(annotations, Options);
    }

    /// <summary>解码 AI 标注点；空串、非法 JSON 或类型不符时返回空列表。</summary>
    public static IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> DecodeAnnotations(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<IReadOnlyList<IReadOnlyList<double>>>();

        try
        {
            return JsonSerializer.Deserialize<List<List<List<double>>>>(json, Options)
                ?? (IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>)Array.Empty<IReadOnlyList<IReadOnlyList<double>>>();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return Array.Empty<IReadOnlyList<IReadOnlyList<double>>>();
        }
    }
}