namespace InkCanvas.Core.AiAssistant;

public static class AssistantMachine
{
    public static AssistantSession CreateSession(AssistantMode mode, string sessionId)
    {
        return new AssistantSession
        {
            State = AssistantState.Idle,
            Mode = mode,
            SessionId = sessionId ?? "",
        };
    }

    public static void BeginCapture(AssistantSession session, long nowMs)
    {
        if (session == null) return;
        session.State = AssistantState.Capturing;
        session.Error = "";
        session.StartedAtMs = nowMs;
        session.FirstTokenAtMs = 0;
    }

    public static void BeginRecognize(AssistantSession session)
    {
        if (session == null) return;
        session.State = AssistantState.Recognizing;
    }

    public static void ApplyEvent(AssistantSession session, AssistantEvent ev, long nowMs)
    {
        if (session == null || ev == null) return;

        bool terminal = session.State == AssistantState.Done || session.State == AssistantState.Error;

        switch (ev.Kind)
        {
            case AssistantEventKind.Token:
                if (terminal) return;
                session.Text += ev.Text ?? "";
                session.TokenCount++;
                if (session.FirstTokenAtMs == 0) session.FirstTokenAtMs = nowMs;
                session.State = AssistantState.Streaming;
                break;

            case AssistantEventKind.Formula:
                session.Formula = ev.Latex ?? "";
                if (ev.Confidence >= 0) session.Confidence = ev.Confidence;
                session.Verified = ev.Verified;
                break;

            case AssistantEventKind.Step:
                session.Steps.Add(new AssistantStep
                {
                    Index = ev.Index,
                    Text = ev.Text ?? "",
                    Latex = ev.Latex ?? "",
                });
                break;

            case AssistantEventKind.ShapeAnnotation:
                session.Annotations.Add(ev.Points ?? Array.Empty<IReadOnlyList<double>>());
                break;

            case AssistantEventKind.BoardPlan:
                if (terminal) return;
                if (AssistantBoardPlan.TryParse(ev.Text, out AssistantBoardPlan plan, out _))
                    session.BoardPlan = plan;
                if (session.FirstTokenAtMs == 0) session.FirstTokenAtMs = nowMs;
                session.State = AssistantState.Streaming;
                break;

            case AssistantEventKind.Done:
                session.State = AssistantState.Done;
                break;

            case AssistantEventKind.Error:
                session.State = AssistantState.Error;
                session.Error = string.IsNullOrEmpty(ev.Text) ? "assistant error" : ev.Text;
                break;
        }
    }

    public static void Finish(AssistantSession session)
    {
        if (session == null) return;
        if (session.State == AssistantState.Error) return;
        session.State = AssistantState.Done;
    }

    public static void Fail(AssistantSession session, string message)
    {
        if (session == null) return;
        session.State = AssistantState.Error;
        session.Error = message ?? "";
    }

    public static bool FirstTokenTimedOut(AssistantSession session, long nowMs, int timeoutMs)
    {
        if (session == null) return false;
        if (session.FirstTokenAtMs != 0) return false;
        if (session.StartedAtMs <= 0) return false;
        if (session.State != AssistantState.Capturing &&
            session.State != AssistantState.Recognizing &&
            session.State != AssistantState.Streaming)
        {
            return false;
        }

        return nowMs - session.StartedAtMs >= timeoutMs;
    }

    public static bool CanRetry(AssistantSession session)
    {
        return session != null && session.State == AssistantState.Error;
    }

    /// <summary>切换讲题模式。分步模式从零开始揭示，其余模式直接展开全部步骤。</summary>
    public static void SetTeachingMode(AssistantSession session, AssistantTeachingMode mode)
    {
        if (session == null || session.TeachingMode == mode) return;

        session.TeachingMode = mode;
        session.RevealedSteps = mode == AssistantTeachingMode.StepByStep
            ? 0
            : session.Steps?.Count ?? 0;
    }

    public static bool RevealNextStep(AssistantSession session)
    {
        if (session == null) return false;
        if (session.TeachingMode != AssistantTeachingMode.StepByStep) return false;

        int total = session.Steps?.Count ?? 0;
        if (session.RevealedSteps >= total) return false;

        session.RevealedSteps++;
        return true;
    }

    public static void RevealAllSteps(AssistantSession session)
    {
        if (session == null) return;
        session.RevealedSteps = session.Steps?.Count ?? 0;
    }

    public static void BeginFollowUp(AssistantSession session, string question, long nowMs)
    {
        if (session == null) return;

        session.Error = "";
        session.StartedAtMs = nowMs;
        session.FirstTokenAtMs = 0;
        session.State = AssistantState.Streaming;

        if (!string.IsNullOrEmpty(question))
        {
            if (!string.IsNullOrEmpty(session.Text)) session.Text += "\n\n";
            session.Text += question;
        }
    }

    /// <summary>
    /// 开始一次板书整理/书写指令。与 <see cref="BeginFollowUp"/> 的区别：指令文本不追加到
    /// <see cref="AssistantSession.Text"/>，正文只承载服务端输出，避免指令与板书内容混排。
    /// </summary>
    public static void BeginCommand(AssistantSession session, string instruction, long nowMs)
    {
        if (session == null) return;

        session.Error = "";
        session.StartedAtMs = nowMs;
        session.FirstTokenAtMs = 0;
        session.State = AssistantState.Streaming;
    }
}