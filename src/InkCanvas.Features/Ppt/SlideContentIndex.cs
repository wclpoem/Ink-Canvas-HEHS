using System.Text.Json;

namespace InkCanvas.Features.Ppt;

public enum SlideFilter
{
    All,
    Annotated,
    Favorite,
}

/// <summary>课件页索引：把页序、批注、收藏、AI 讲解与缩略图合并成一张可筛选的表。</summary>
public sealed class SlideContentEntry
{
    public int SlideId { get; set; }

    public int Index { get; set; }

    public string Name { get; set; } = "";

    public bool HasInk { get; set; }

    public int InkStrokeCount { get; set; }

    public bool Favorite { get; set; }

    public bool HasAi { get; set; }

    public string ThumbnailPath { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>该页最近一次讲解时的动画/点击阶段；0 表示未知或未使用动画。</summary>
    public int AnimationStep { get; set; }

    public bool IsEmpty => !HasInk && !Favorite && !HasAi;
}

/// <summary>
/// 一节课的页级内容索引。重建时按当前页序重排，并保留旧的收藏/AI 标记与缩略图路径，
/// 这样插页、删页、调序后导航面板仍能回查讲解过与收藏过的页。
/// </summary>
public sealed class SlideContentIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Identity { get; set; } = "";

    public string Name { get; set; } = "";

    public long UpdatedAtMs { get; set; }

    public List<SlideContentEntry> Slides { get; set; } = new();

    public int Count => Slides.Count;

    public static SlideContentIndex Build(
        PptPresentationDetails details,
        PptInkArchive archive,
        SlideContentIndex previous = null,
        long nowMs = 0)
    {
        var index = new SlideContentIndex
        {
            Version = CurrentVersion,
            Identity = details?.Presentation?.Identity ?? "",
            Name = details?.Presentation?.Name ?? "",
            UpdatedAtMs = nowMs,
        };

        IReadOnlyList<PptSlideInfo> slides = details?.Slides ?? Array.Empty<PptSlideInfo>();
        IReadOnlyList<PptStoredSlide> stored = archive?.Slides ?? Array.Empty<PptStoredSlide>();
        var storedById = new Dictionary<int, PptStoredSlide>();
        var storedByIndex = new Dictionary<int, PptStoredSlide>();
        foreach (var ink in stored)
        {
            if (ink is null) continue;
            if (ink.SlideId > 0) storedById.TryAdd(ink.SlideId, ink);
            else storedByIndex.TryAdd(ink.SlideIndex, ink);
        }
        var previousBySlideId = new Dictionary<int, SlideContentEntry>();
        var previousByIndex = new Dictionary<int, SlideContentEntry>();
        if (previous?.Slides is not null)
        {
            foreach (SlideContentEntry entry in previous.Slides)
            {
                if (entry.SlideId > 0) previousBySlideId[entry.SlideId] = entry;
                previousByIndex[entry.Index] = entry;
            }
        }

        foreach (PptSlideInfo slide in slides)
        {
            if (slide is null) continue;

            var ink = storedById.GetValueOrDefault(slide.SlideId) ?? storedByIndex.GetValueOrDefault(slide.Index);
            SlideContentEntry kept = Lookup(previousBySlideId, previousByIndex, slide.SlideId, slide.Index);
            int strokeCount = ink?.IsDeferred == true
                ? ink.StoredStrokeCount ?? kept?.InkStrokeCount ?? 0
                : ink?.Strokes?.Count ?? 0;
            index.Slides.Add(new SlideContentEntry
            {
                SlideId = slide.SlideId,
                Index = slide.Index,
                Name = slide.Name ?? "",
                HasInk = strokeCount > 0,
                InkStrokeCount = strokeCount,
                Favorite = kept?.Favorite ?? false,
                HasAi = kept?.HasAi ?? false,
                ThumbnailPath = kept?.ThumbnailPath ?? "",
                Text = kept?.Text ?? "",
                AnimationStep = kept?.AnimationStep ?? 0,
            });
        }

        return index;
    }

    private static SlideContentEntry Lookup(
        Dictionary<int, SlideContentEntry> bySlideId,
        Dictionary<int, SlideContentEntry> byIndex,
        int slideId,
        int slideIndex)
    {
        if (slideId > 0 && bySlideId.TryGetValue(slideId, out SlideContentEntry byId)) return byId;
        return byIndex.TryGetValue(slideIndex, out SlideContentEntry byPosition) ? byPosition : null;
    }

    public SlideContentEntry Find(int slideId)
    {
        foreach (SlideContentEntry entry in Slides)
        {
            if (entry.SlideId == slideId) return entry;
        }

        return null;
    }

    public IReadOnlyList<SlideContentEntry> Filter(SlideFilter filter)
    {
        if (filter == SlideFilter.All) return Slides;

        var result = new List<SlideContentEntry>();
        foreach (SlideContentEntry entry in Slides)
        {
            bool match = filter == SlideFilter.Annotated ? entry.HasInk : entry.Favorite;
            if (match) result.Add(entry);
        }

        return result;
    }

    public bool ToggleFavorite(int slideId)
    {
        SlideContentEntry entry = Find(slideId);
        if (entry is null) return false;
        entry.Favorite = !entry.Favorite;
        return entry.Favorite;
    }

    public bool SetFavorite(int slideId, bool value)
    {
        SlideContentEntry entry = Find(slideId);
        if (entry is null) return false;
        entry.Favorite = value;
        return true;
    }

    public bool MarkAi(int slideId, bool value = true)
    {
        SlideContentEntry entry = Find(slideId);
        if (entry is null) return false;
        entry.HasAi = value;
        return true;
    }

    public bool AttachThumbnail(int slideId, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        SlideContentEntry entry = Find(slideId);
        if (entry is null) return false;
        entry.ThumbnailPath = path;
        return true;
    }

    public bool SetText(int slideId, string text)
    {
        SlideContentEntry entry = Find(slideId);
        if (entry is null) return false;
        entry.Text = text ?? "";
        return true;
    }

    public SlideContentIndex Clone()
    {
        var copy = new SlideContentIndex
        {
            Version = Version,
            Identity = Identity,
            Name = Name,
            UpdatedAtMs = UpdatedAtMs,
            Slides = new List<SlideContentEntry>(Slides.Count),
        };

        foreach (SlideContentEntry entry in Slides)
        {
            copy.Slides.Add(new SlideContentEntry
            {
                SlideId = entry.SlideId,
                Index = entry.Index,
                Name = entry.Name ?? "",
                HasInk = entry.HasInk,
                InkStrokeCount = entry.InkStrokeCount,
                Favorite = entry.Favorite,
                HasAi = entry.HasAi,
                ThumbnailPath = entry.ThumbnailPath ?? "",
                Text = entry.Text ?? "",
                AnimationStep = entry.AnimationStep,
            });
        }

        return copy;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static SlideContentIndex Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            SlideContentIndex index = JsonSerializer.Deserialize<SlideContentIndex>(json);
            if (index?.Slides is null) return null;
            index.Version = Math.Max(1, index.Version);
            return index;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
