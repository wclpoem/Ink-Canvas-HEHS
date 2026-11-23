namespace InkCanvas.Core.AiAssistant;

/// <summary>解释步骤与图形标注的对应项；步骤序号从 1 开始。</summary>
public sealed class AnnotationHighlightItem
{
    public int StepIndex { get; set; }

    public int Index { get; set; }

    public List<ShapeAnnotationPoint> Points { get; set; } = new();

    public bool Locatable { get; set; }

    public bool Visible { get; set; }
}

/// <summary>
/// 图形释义标注层：仅在原图位置可靠时生成可绘制项；否则只返回文字解释与原因，
/// 不得生成可能误导学生的标注。
/// </summary>
public sealed class AnnotationLayer
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public bool CanLocate { get; set; }

    public string Reason { get; set; } = "";

    public List<AnnotationHighlightItem> Items { get; set; } = new();

    public int VisibleCount => Items.FindAll(item => item.Visible && item.Locatable).Count;
}

public static class AnnotationLayerPolicy
{
    public const string NoAnchorReason = "原图位置未知，已只显示文字解释，不生成标注。";

    public const string NoAnnotationReason = "本次解释没有可定位的图形对象，仅显示文字。";

    public static AnnotationLayer Build(AssistantSession session, AssistantRect anchor)
    {
        var layer = new AnnotationLayer();
        if (session?.Annotations is null || session.Annotations.Count == 0)
        {
            layer.CanLocate = false;
            layer.Reason = NoAnnotationReason;
            return layer;
        }

        if (anchor.IsEmpty)
        {
            layer.CanLocate = false;
            layer.Reason = NoAnchorReason;
            return layer;
        }

        int stepCount = session.Steps?.Count ?? 0;
        for (int i = 0; i < session.Annotations.Count; i++)
        {
            List<double> flat = new();
            foreach (IReadOnlyList<double> point in session.Annotations[i] ?? Array.Empty<IReadOnlyList<double>>())
            {
                if (point is null || point.Count < 2) continue;
                flat.Add(point[0]);
                flat.Add(point[1]);
            }

            List<ShapeAnnotationPoint> normalized = ShapeAnnotationGeometry.Normalize(flat, 1, 1);
            bool locatable = normalized.Count >= 2
                && normalized.TrueForAll(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y));

            layer.Items.Add(new AnnotationHighlightItem
            {
                Index = i,
                StepIndex = Math.Min(stepCount <= 0 ? 1 : stepCount, i + 1),
                Points = normalized,
                Locatable = locatable,
                Visible = locatable,
            });
        }

        layer.CanLocate = layer.Items.Exists(item => item.Locatable);
        layer.Reason = layer.CanLocate ? "" : NoAnnotationReason;
        return layer;
    }

    /// <summary>标注可见数跟随分步揭示；未揭示的步骤不显示对应标注。</summary>
    public static void SyncVisibility(AnnotationLayer layer, AssistantSession session)
    {
        if (layer is null || session is null) return;

        int visibleSteps = AssistantTeachingPolicy.VisibleSteps(
            session.TeachingMode, session.RevealedSteps, session.Steps?.Count ?? 0);
        bool revealAll = session.TeachingMode != AssistantTeachingMode.StepByStep;

        foreach (AnnotationHighlightItem item in layer.Items)
        {
            item.Visible = item.Locatable && (revealAll || item.StepIndex <= visibleSteps);
        }
    }

    public static void SetAllVisible(AnnotationLayer layer, bool visible)
    {
        if (layer is null) return;
        foreach (AnnotationHighlightItem item in layer.Items)
            item.Visible = visible && item.Locatable;
    }
}