using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Modules.AiAssistant;

/// <summary>A response owns its display state so later requests cannot change earlier answers.</summary>
public sealed class AssistantTurn(string question, AssistantRect anchor, string presentation, int slideId, int boardPage)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Question { get; } = question ?? "";
    public AssistantRect Anchor { get; } = anchor;
    public string Presentation { get; } = presentation ?? "";
    public int SlideId { get; } = slideId;
    public int BoardPage { get; } = boardPage;
    public AssistantSession Session { get; private set; } = new();
    public bool Pasted { get; set; }
    public bool Pasting { get; set; }

    public void Update(AssistantSession session)
    {
        if (Pasted && (Session.Text != session.Text || Session.Formula != session.Formula
            || Session.RevealedSteps != session.RevealedSteps || Session.TeachingMode != session.TeachingMode)) Pasted = false;
        Session = Snapshot(session);
    }

    public static AssistantSession Snapshot(AssistantSession source) => new()
    {
        State = source.State, Mode = source.Mode, TeachingMode = source.TeachingMode,
        RevealedSteps = source.RevealedSteps, SessionId = source.SessionId, Text = source.Text,
        Formula = source.Formula, Confidence = source.Confidence, Verified = source.Verified,
        LocalHint = source.LocalHint, Error = source.Error, TokenCount = source.TokenCount,
        StartedAtMs = source.StartedAtMs, FirstTokenAtMs = source.FirstTokenAtMs,
        Steps = source.Steps.Select(step => new AssistantStep { Index = step.Index, Text = step.Text, Latex = step.Latex }).ToList(),
        Annotations = source.Annotations.Select(shape => (IReadOnlyList<IReadOnlyList<double>>)shape
            .Select(point => (IReadOnlyList<double>)point.ToArray()).ToArray()).ToList(),
        Regions = source.Regions.Select(region => new AssistantRegion { Rect = region.Rect, TextStart = region.TextStart,
            TextLength = region.TextLength, Source = region.Source }).ToList(),
    };

    /// <summary>Copy, paste and follow-up context all respect the teacher's revealed steps.</summary>
    public static string VisibleText(AssistantSession session)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Text)) lines.Add(session.Text);
        int visible = AssistantTeachingPolicy.VisibleSteps(session.TeachingMode, session.RevealedSteps, session.Steps.Count);
        foreach (var step in session.Steps.Take(visible))
        {
            if (!string.IsNullOrWhiteSpace(step.Text)) lines.Add(step.Text);
            if (!string.IsNullOrWhiteSpace(step.Latex)) lines.Add(step.Latex);
        }
        if (AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, session.RevealedSteps, session.Steps.Count)
            && !string.IsNullOrWhiteSpace(session.Formula)) lines.Add(session.Formula);
        return string.Join("\n\n", lines);
    }
}
