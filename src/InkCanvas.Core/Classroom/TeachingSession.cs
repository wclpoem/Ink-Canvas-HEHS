using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Core.Classroom;

/// <summary>课堂会话内的一条关联记录：AI 题目、练习、工具结果或导出。</summary>
public sealed class TeachingSessionLink
{
    public string Kind { get; set; } = "";

    public string Id { get; set; } = "";

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public int AnimationStep { get; set; }

    public int BoardPageIndex { get; set; } = -1;

    public long CreatedAtMs { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>
/// 统一课堂会话：把课件、画板、AI 题目、练习、工具结果与课后导出串到同一个 ID 下。
/// 只追加字段，历史 JSON 缺少新字段时按缺省值读取。
/// </summary>
public sealed class TeachingSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public long StartedAtMs { get; set; }

    public long UpdatedAtMs { get; set; }

    public string PresentationIdentity { get; set; } = "";

    public string PresentationName { get; set; } = "";

    public string PresetId { get; set; } = "";

    public int CurrentSlideId { get; set; }

    public int CurrentSlideIndex { get; set; }

    public int CurrentAnimationStep { get; set; }

    public int CurrentBoardPageIndex { get; set; } = -1;

    public List<string> AiSessionIds { get; set; } = new();

    public List<string> ExerciseIds { get; set; } = new();

    public List<string> ExportPaths { get; set; } = new();

    /// <summary>练习结束等节点保存的板书快照路径（去重）。</summary>
    public List<string> SnapshotPaths { get; set; } = new();

    public List<TeachingSessionLink> Links { get; set; } = new();

    public static TeachingSession Create(string name, long nowMs)
    {
        var session = new TeachingSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name ?? "",
            StartedAtMs = nowMs,
            UpdatedAtMs = nowMs,
        };

        return session;
    }

    /// <summary>课件页键。同一页的不同动画阶段用 animationStep 区分，避免讲解状态混淆。</summary>
    public static string SlideKey(int slideId, int slideIndex, int animationStep)
        => "slide:" + slideId + "@" + slideIndex + "#" + animationStep;

    public string CurrentSlideKey()
        => SlideKey(CurrentSlideId, CurrentSlideIndex, CurrentAnimationStep);

    public bool SetCurrentSlide(int slideId, int slideIndex, int animationStep, long nowMs)
    {
        bool changed = slideId != CurrentSlideId
            || slideIndex != CurrentSlideIndex
            || animationStep != CurrentAnimationStep;

        CurrentSlideId = slideId;
        CurrentSlideIndex = slideIndex;
        CurrentAnimationStep = animationStep;
        if (changed) UpdatedAtMs = nowMs;
        return changed;
    }

    public TeachingSessionLink Link(
        string kind,
        string id,
        int slideId,
        int slideIndex,
        int boardPageIndex = -1,
        string note = "",
        long nowMs = 0)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id)) return null;

        if (string.Equals(kind, "ai", StringComparison.Ordinal) && !AiSessionIds.Contains(id))
            AiSessionIds.Add(id);
        if (string.Equals(kind, "exercise", StringComparison.Ordinal) && !ExerciseIds.Contains(id))
            ExerciseIds.Add(id);

        TeachingSessionLink existing = Links.Find(link =>
            string.Equals(link.Kind, kind, StringComparison.Ordinal)
            && string.Equals(link.Id, id, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.SlideId = slideId;
            existing.SlideIndex = slideIndex;
            existing.BoardPageIndex = boardPageIndex;
            if (!string.IsNullOrWhiteSpace(note)) existing.Note = note;
            UpdatedAtMs = nowMs;
            return existing;
        }

        var created = new TeachingSessionLink
        {
            Kind = kind,
            Id = id,
            SlideId = slideId,
            SlideIndex = slideIndex,
            AnimationStep = CurrentAnimationStep,
            BoardPageIndex = boardPageIndex,
            CreatedAtMs = nowMs,
            Note = note ?? "",
        };

        Links.Add(created);
        UpdatedAtMs = nowMs;
        return created;
    }

    public bool HasLink(string kind, string id)
        => Links.Exists(link =>
            string.Equals(link.Kind, kind, StringComparison.Ordinal)
            && string.Equals(link.Id, id, StringComparison.Ordinal));

    public IReadOnlyList<TeachingSessionLink> LinksForSlide(int slideId)
        => Links.FindAll(link => link.SlideId == slideId);

    public void RecordExport(string path, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!ExportPaths.Contains(path)) ExportPaths.Add(path);
        UpdatedAtMs = nowMs;
    }

    /// <summary>记录练习结束快照路径（去重）；空路径忽略。</summary>
    public bool RecordSnapshot(string path, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!SnapshotPaths.Contains(path)) SnapshotPaths.Add(path);
        UpdatedAtMs = nowMs;
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static TeachingSession Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            TeachingSession session = JsonSerializer.Deserialize<TeachingSession>(json, JsonOptions);
            if (session is null) return null;
            session.Version = Math.Max(1, session.Version);
            session.AiSessionIds ??= new List<string>();
            session.ExerciseIds ??= new List<string>();
            session.ExportPaths ??= new List<string>();
            session.SnapshotPaths ??= new List<string>();
            session.Links ??= new List<TeachingSessionLink>();
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}