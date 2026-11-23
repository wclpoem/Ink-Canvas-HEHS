using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.Board.AiObjects;

namespace InkCanvas.Features.Classroom;

public enum ExerciseDifficulty
{
    Basic,
    Advanced,
}

/// <summary>AI 变式题草稿。先生成、教师预览编辑，确认后才允许贴入新画板页。</summary>
public sealed class VariantQuestionDraft
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public ExerciseDifficulty Difficulty { get; set; }

    public string Text { get; set; } = "";

    public string Answer { get; set; } = "";

    public string KnowledgePoint { get; set; } = "";

    public string SourceSessionId { get; set; } = "";

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public long CreatedAtMs { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Text);

    public VariantQuestionDraft Clone() => new()
    {
        Version = Version,
        Id = Id,
        Difficulty = Difficulty,
        Text = Text,
        Answer = Answer,
        KnowledgePoint = KnowledgePoint,
        SourceSessionId = SourceSessionId,
        SlideId = SlideId,
        SlideIndex = SlideIndex,
        CreatedAtMs = CreatedAtMs,
    };
}

/// <summary>「开始练习」动作清单：贴题 → 切页 → 启动倒计时 → 进入练习场景。</summary>
public sealed class PracticePlan
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ExerciseId { get; set; } = "";

    public int BoardPageIndex { get; set; } = -1;

    public int CountdownMinutes { get; set; } = 3;

    public string SceneId { get; set; } = "practice";

    public bool SaveSnapshotOnFinish { get; set; } = true;

    public string BookmarkName { get; set; } = "练习结束";

    /// <summary>发起练习的 AI 会话 id；无 AI 场景为空字符串。</summary>
    public string AiSessionId { get; set; } = "";

    /// <summary>练习页的稳定页令牌，用于写回时校验同索引换页。</summary>
    public Guid BoardPageId { get; set; }

    /// <summary>本次练习的分组 id（同一批板书对象共享），便于整体选中/撤销。</summary>
    public string GroupId { get; set; } = "";
}

public static class ExerciseFlowPolicy
{
    public static AssistantMode ModeFor(ExerciseDifficulty difficulty)
        => difficulty == ExerciseDifficulty.Advanced ? AssistantMode.Solve : AssistantMode.Auto;

    /// <summary>变式题请求：携带合并后的拼题上下文，不覆盖原题。</summary>
    public static AssistantRequest BuildRequest(
        ProblemContext context,
        ExerciseDifficulty difficulty,
        string sessionId,
        AssistantTeachingMode teaching = AssistantTeachingMode.StepByStep)
    {
        IReadOnlyList<ProblemRegion> ordered = ProblemContextPolicy.Ordered(context);
        return new AssistantRequest
        {
            ImageBase64 = ProblemContextPolicy.PrimaryImage(context),
            Images = ordered,
            Viewport = new AssistantViewport { Rect = ProblemContextPolicy.CombinedAnchor(context) },
            Mode = ModeFor(difficulty),
            SessionId = sessionId ?? "",
            Teaching = teaching,
            Purpose = "variant",
            Difficulty = difficulty == ExerciseDifficulty.Advanced ? "advanced" : "basic",
        };
    }

    /// <summary>把 AI 文本切成题干与答案；没有明确标记时整段作为题干。</summary>
    public static VariantQuestionDraft Parse(
        string aiText,
        ExerciseDifficulty difficulty,
        string knowledgePoint = null,
        string sourceSessionId = null,
        int slideId = 0,
        int slideIndex = 0,
        long nowMs = 0)
    {
        var draft = new VariantQuestionDraft
        {
            Id = Guid.NewGuid().ToString("N"),
            Difficulty = difficulty,
            KnowledgePoint = knowledgePoint ?? "",
            SourceSessionId = sourceSessionId ?? "",
            SlideId = slideId,
            SlideIndex = slideIndex,
            CreatedAtMs = nowMs,
            Text = (aiText ?? "").Trim(),
        };

        foreach (string rawLine in (aiText ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("答案：", StringComparison.Ordinal)
                || line.StartsWith("答案:", StringComparison.Ordinal))
            {
                draft.Answer = line.Substring(3).Trim();
                draft.Text = RemoveLine(draft.Text, line);
            }
            else if (line.StartsWith("知识点：", StringComparison.Ordinal)
                || line.StartsWith("知识点:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(draft.KnowledgePoint))
                    draft.KnowledgePoint = line.Substring(4).Trim();
                draft.Text = RemoveLine(draft.Text, line);
            }
        }

        draft.Text = draft.Text.Trim();
        return draft;
    }

    public static bool CanStart(VariantQuestionDraft draft) => draft is not null && draft.IsValid;

    /// <summary>开始练习：只贴到新页（newBoardPageIndex 由调用方先新建），不覆盖原题。</summary>
    public static PracticePlan Plan(
        VariantQuestionDraft draft,
        int newBoardPageIndex,
        int countdownMinutes,
        string sceneId = "practice",
        string aiSessionId = null,
        Guid boardPageId = default,
        string groupId = null)
    {
        return new PracticePlan
        {
            ExerciseId = draft?.Id ?? "",
            BoardPageIndex = Math.Max(0, newBoardPageIndex),
            CountdownMinutes = Math.Max(1, countdownMinutes),
            SceneId = string.IsNullOrWhiteSpace(sceneId) ? "practice" : sceneId,
            SaveSnapshotOnFinish = true,
            BookmarkName = "练习结束",
            AiSessionId = aiSessionId ?? "",
            BoardPageId = boardPageId,
            GroupId = groupId ?? "",
        };
    }

    /// <summary>
    /// 拆分知识点：按中文逗号、英文逗号、顿号、中文/英文分号拆分，Trim 去空后按首次出现顺序去重。
    /// </summary>
    public static IReadOnlyList<string> SplitKnowledgePoints(string value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in value.Split(KnowledgePointSeparators, StringSplitOptions.None))
        {
            string trimmed = (part ?? "").Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>知识点规范化：拆分后统一用中文逗号连接；空结果返回空字符串。</summary>
    public static string NormalizeKnowledgePoints(string value)
    {
        IReadOnlyList<string> parts = SplitKnowledgePoints(value);
        return parts.Count == 0 ? "" : string.Join("，", parts);
    }

    private static readonly char[] KnowledgePointSeparators = { '，', ',', '、', '；', ';' };

    /// <summary>
    /// 练习页原生板书计划：题目 → 知识点 → 参考答案三个文本块，供
    /// <c>BoardPlanWriter</c> 生成可编辑文本对象（本方法不渲染 PNG）。
    /// 草稿为空或题干空白时返回 Blocks 为空的计划，由调用方决定是否继续。
    /// </summary>
    public static AssistantBoardPlan BuildPracticeBoardPlan(VariantQuestionDraft draft, string title = "随堂练习")
    {
        var blocks = new List<AssistantBoardBlock>();
        if (draft is null || string.IsNullOrWhiteSpace(draft.Text))
            return new AssistantBoardPlan { Intent = "practice", TargetBoard = "main", Confidence = 1.0, Blocks = blocks };

        blocks.Add(new AssistantBoardBlock
        {
            Id = "practice-question",
            Kind = "text",
            Title = "题目",
            Text = draft.Text,
            Style = new AssistantBoardStyle { FontSize = 26, Align = "left", Columns = 1, LineHeight = 1.4 },
        });

        string knowledge = NormalizeKnowledgePoints(draft.KnowledgePoint);
        if (!string.IsNullOrWhiteSpace(knowledge))
        {
            blocks.Add(new AssistantBoardBlock
            {
                Id = "practice-knowledge",
                Kind = "text",
                Title = "知识点",
                Text = knowledge,
                Style = new AssistantBoardStyle { FontSize = 20, Align = "left", Columns = 1, LineHeight = 1.4 },
            });
        }

        string answer = (draft.Answer ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(answer))
        {
            blocks.Add(new AssistantBoardBlock
            {
                Id = "practice-answer",
                Kind = "text",
                Title = "参考答案",
                Text = answer,
                Style = new AssistantBoardStyle { FontSize = 20, Align = "left", Columns = 1, LineHeight = 1.4 },
            });
        }

        return new AssistantBoardPlan
        {
            Intent = "practice",
            TargetBoard = "main",
            Confidence = 1.0,
            Blocks = blocks,
        };
    }

    /// <summary>
    /// 练习页对象纵向排版（纯逻辑）：依次把对象左对齐，并按其高度 + 间距自上而下堆叠；
    /// 不改变宽高、文字与 Id，便于单测和后续手动微调。
    /// </summary>
    public static void LayoutPracticeObjects(
        IReadOnlyList<BoardTextObject> texts, float left = 48f, float top = 48f, float gap = 24f)
    {
        if (texts is null) return;

        float cursor = top;
        foreach (BoardTextObject text in texts)
        {
            if (text is null) continue;
            text.Left = left;
            text.Top = cursor;
            cursor += text.Height + gap;
        }
    }

    public static IReadOnlyList<string> BuildPasteLines(VariantQuestionDraft draft)
    {
        var lines = new List<string>();
        if (draft is null) return lines;
        string knowledge = NormalizeKnowledgePoints(draft.KnowledgePoint);
        if (!string.IsNullOrWhiteSpace(knowledge)) lines.Add("知识点：" + knowledge);
        foreach (string line in (draft.Text ?? "").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) lines.Add(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(draft.Answer)) lines.Add("参考答案：" + draft.Answer.Trim());
        return lines;
    }

    private static string RemoveLine(string text, string line)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var kept = new List<string>();
        foreach (string candidate in text.Split('\n'))
        {
            if (!string.Equals(candidate.Trim(), line, StringComparison.Ordinal)) kept.Add(candidate);
        }

        return string.Join("\n", kept);
    }
}

/// <summary>倒计时结束时的练习收尾数据；由 Shell 层落快照与书签。</summary>
public sealed class PracticeFinishRecord
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string ExerciseId { get; set; } = "";

    public int BoardPageIndex { get; set; } = -1;

    public string SnapshotPath { get; set; } = "";

    public string BookmarkName { get; set; } = "练习结束";

    public bool BookmarkCreated { get; set; }

    /// <summary>结束快照是否已成功保存到磁盘；未保存时调用方可提示教师手动导出。</summary>
    public bool SnapshotSaved { get; set; }

    public long FinishedAtMs { get; set; }
}

public static class PracticeFinishPolicy
{
    /// <summary>结束时无论是否录制都要保留板书快照；录制中额外生成章节书签。</summary>
    public static PracticeFinishRecord Plan(
        PracticePlan plan,
        bool isRecording,
        bool snapshotSaved,
        string snapshotPath = "",
        long nowMs = 0)
        => new()
        {
            ExerciseId = plan?.ExerciseId ?? "",
            BoardPageIndex = plan?.BoardPageIndex ?? -1,
            BookmarkName = plan?.BookmarkName ?? "练习结束",
            BookmarkCreated = isRecording,
            SnapshotSaved = snapshotSaved,
            SnapshotPath = snapshotPath ?? "",
            FinishedAtMs = nowMs,
        };

    public static bool ShouldNotify(bool isRecording) => isRecording;
}

/// <summary>练习结束后的下一步动作；由 App 层按清单渲染按钮，不在纯逻辑层绑定 UI。</summary>
public enum PracticeFinishActionKind
{
    NextVariant,
    Collect,
    Export,
}

/// <summary>结束动作描述：<see cref="RequiresAi"/> 为 true 时离线也要列出，由 UI 提示需联网。</summary>
public sealed class PracticeFinishAction
{
    public PracticeFinishActionKind Kind { get; init; }

    public string Label { get; init; } = "";

    public bool RequiresAi { get; init; }
}

public static class PracticeFinishActionPolicy
{
    /// <summary>
    /// 固定返回「再来一题（变式）→ 收题（返回画板）→ 导出本次练习」三项。
    /// <paramref name="aiAvailable"/> 与 <paramref name="snapshotAvailable"/> 不改变清单，
    /// 只保留给 UI 后续扩展（离线时变式按钮置灰提示）。
    /// </summary>
    public static IReadOnlyList<PracticeFinishAction> Build(bool aiAvailable, bool snapshotAvailable)
    {
        _ = aiAvailable;
        _ = snapshotAvailable;

        return new[]
        {
            new PracticeFinishAction
            {
                Kind = PracticeFinishActionKind.NextVariant,
                Label = "再来一题（变式）",
                RequiresAi = true,
            },
            new PracticeFinishAction
            {
                Kind = PracticeFinishActionKind.Collect,
                Label = "收题（返回画板）",
                RequiresAi = false,
            },
            new PracticeFinishAction
            {
                Kind = PracticeFinishActionKind.Export,
                Label = "导出本次练习",
                RequiresAi = false,
            },
        };
    }
}

/// <summary>练习关联信息：由 <see cref="TeachingSession.Link"/> 的 Note 用固定 key=value 格式承载。</summary>
public sealed class PracticeLinkInfo
{
    public string AiSessionId { get; set; } = "";

    public Guid BoardPageId { get; set; }

    public string GroupId { get; set; } = "";

    public string SnapshotPath { get; set; } = "";
}

/// <summary>
/// 练习关联 note 编解码。格式固定为
/// <c>ai=&lt;id&gt;;page=&lt;Guid N 或空&gt;;group=&lt;id&gt;;snapshot=&lt;路径&gt;</c>；
/// 空值保留键。路径放最后一个键，解析时取其后剩余全部文本以容忍分号，不引入新的 JSON 版本。
/// </summary>
public static class PracticeLinkNote
{
    private const string AiKey = "ai=";
    private const string PageKey = "page=";
    private const string GroupKey = "group=";
    private const string SnapshotKey = "snapshot=";

    public static string Build(string aiSessionId, Guid boardPageId, string groupId, string snapshotPath = "")
    {
        string page = boardPageId == Guid.Empty ? "" : boardPageId.ToString("N");
        return AiKey + (aiSessionId ?? "")
            + ";" + PageKey + page
            + ";" + GroupKey + (groupId ?? "")
            + ";" + SnapshotKey + (snapshotPath ?? "");
    }

    /// <summary>解析 note；null / 空白 / 非法内容返回空 info 对象，绝不抛异常。</summary>
    public static PracticeLinkInfo Parse(string note)
    {
        var info = new PracticeLinkInfo();
        if (string.IsNullOrWhiteSpace(note)) return info;

        string text = note.Trim();
        int aiIndex = text.IndexOf(AiKey, StringComparison.Ordinal);
        int pageIndex = text.IndexOf(PageKey, StringComparison.Ordinal);
        int groupIndex = text.IndexOf(GroupKey, StringComparison.Ordinal);
        int snapshotIndex = text.IndexOf(SnapshotKey, StringComparison.Ordinal);

        info.AiSessionId = ReadValue(text, aiIndex, AiKey, pageIndex, groupIndex, snapshotIndex);
        string pageValue = ReadValue(text, pageIndex, PageKey, groupIndex, snapshotIndex);
        if (Guid.TryParse(pageValue, out Guid boardPageId)) info.BoardPageId = boardPageId;
        info.GroupId = ReadValue(text, groupIndex, GroupKey, snapshotIndex);
        info.SnapshotPath = snapshotIndex < 0
            ? ""
            : text[(snapshotIndex + SnapshotKey.Length)..].Trim();
        return info;
    }

    private static string ReadValue(string text, int keyIndex, string key, params int[] nextIndexes)
    {
        if (keyIndex < 0) return "";

        int start = keyIndex + key.Length;
        int end = text.Length;
        foreach (int next in nextIndexes)
        {
            if (next > start && next < end) end = next;
        }

        return text[start..end].TrimEnd(';').Trim();
    }
}