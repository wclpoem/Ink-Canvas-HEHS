namespace InkCanvas.Core.AiAssistant;

public enum AssistantState
{
    Idle,
    Capturing,
    Recognizing,
    Streaming,
    Done,
    Error,
}

public enum AssistantMode
{
    Auto,
    Formula,
    Solve,
    Shape,
    Chat,
}

public readonly record struct AssistantRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public double CenterX => (Left + Right) / 2.0;

    public double CenterY => (Top + Bottom) / 2.0;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed class AssistantPoint
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Pressure { get; set; }

    public double TimeMs { get; set; }
}

public sealed class AssistantStroke
{
    public double BaseWidth { get; set; }

    public uint ColorArgb { get; set; }

    public List<AssistantPoint> Points { get; set; } = new();
}

public sealed class AssistantViewport
{
    public double Scale { get; set; } = 1.0;

    public AssistantRect Rect { get; set; }
}

/// <summary>两阶段请求：先只发墨迹（低带宽快速首屏），收到首个 token 后再补发截图精修。</summary>
public enum AssistantStage
{
    InkOnly,
    WithImage,
}

/// <summary>待纠错的文本区间，用于只替换局部文字而不重发整轮会话。</summary>
public sealed class AssistantRegion
{
    public AssistantRect Rect { get; set; }

    public int TextStart { get; set; }

    public int TextLength { get; set; }

    public string Source { get; set; } = "";
}

public sealed class AssistantRequest
{
    /// <summary>Previous visible conversation turns, oldest first. Screenshots remain in Images.</summary>
    public IReadOnlyList<AssistantMessage> Messages { get; set; } = Array.Empty<AssistantMessage>();
    public string ImageBase64 { get; set; } = "";

    public IReadOnlyList<AssistantStroke> InkStrokes { get; set; } = Array.Empty<AssistantStroke>();

    public AssistantViewport Viewport { get; set; } = new();

    public AssistantMode Mode { get; set; }

    public string SessionId { get; set; } = "";

    public string Question { get; set; } = "";

    public AssistantStage Stage { get; set; } = AssistantStage.WithImage;

    public AssistantRegion Region { get; set; }

    /// <summary>跨选区拼题的附加图片；为空时退回单图 <see cref="ImageBase64"/>。</summary>
    public IReadOnlyList<ProblemRegion> Images { get; set; } = Array.Empty<ProblemRegion>();

    /// <summary>讲题模式；服务端据此裁剪答案与分步。</summary>
    public AssistantTeachingMode Teaching { get; set; } = AssistantTeachingMode.StepByStep;

    /// <summary>请求用途：空为普通讲题，<c>variant</c> 表示变式题，<c>board_write</c> 表示 AI 板书整理/书写。</summary>
    public string Purpose { get; set; } = "";

    /// <summary>板书整理/书写的自然语言指令；仅 <see cref="Purpose"/> 为 <c>board_write</c> 时下发。</summary>
    public string Instruction { get; set; } = "";

    /// <summary>板书写入的目标页索引；-1 表示未指定，由服务端或客户端默认当前页。</summary>
    public int TargetPageIndex { get; set; } = -1;

    /// <summary>变式题难度：basic / advanced；普通讲题为空。</summary>
    public string Difficulty { get; set; } = "";

    /// <summary>课件上下文（标题/本页/前后页文本），仅作辅助，不覆盖框选。</summary>
    public SlideContext SlideContext { get; set; }
}

public sealed record AssistantMessage(string Role, string Content);

public enum AssistantEventKind
{
    Token,
    Formula,
    Step,
    ShapeAnnotation,
    Done,
    Error,
    BoardPlan,
}

public sealed class AssistantEvent
{
    public AssistantEventKind Kind { get; set; }

    public string Text { get; set; } = "";

    public string Latex { get; set; } = "";

    public int Index { get; set; }

    public string Title { get; set; } = "";

    public IReadOnlyList<IReadOnlyList<double>> Points { get; set; } = Array.Empty<IReadOnlyList<double>>();

    public double Confidence { get; set; } = -1;

    public bool? Verified { get; set; }
}

public sealed class AssistantStep
{
    public int Index { get; set; }

    public string Text { get; set; } = "";

    public string Latex { get; set; } = "";
}

public sealed class AssistantSession
{
    public AssistantState State { get; set; }

    public AssistantMode Mode { get; set; }

    public AssistantTeachingMode TeachingMode { get; set; } = AssistantTeachingMode.StepByStep;

    /// <summary>分步引导模式中已向学生揭示的步骤数。</summary>
    public int RevealedSteps { get; set; }

    public string SessionId { get; set; } = "";

    public string Text { get; set; } = "";

    public string Formula { get; set; } = "";

    public List<AssistantStep> Steps { get; set; } = new();

    public List<IReadOnlyList<IReadOnlyList<double>>> Annotations { get; set; } = new();

    public List<AssistantRegion> Regions { get; set; } = new();

    /// <summary>识别/求解置信度，-1 表示尚未提供。</summary>
    public double Confidence { get; set; } = -1;

    /// <summary>服务端自检标记，null 表示未校验。</summary>
    public bool? Verified { get; set; }

    /// <summary>AI 板书计划；仅 <c>board_plan</c> 事件解析成功后填充。</summary>
    public AssistantBoardPlan BoardPlan { get; set; }

    /// <summary>弱网首屏本地提示，不参与最终结果。</summary>
    public string LocalHint { get; set; } = "";

    public string Error { get; set; } = "";

    public int TokenCount { get; set; }

    public long StartedAtMs { get; set; }

    public long FirstTokenAtMs { get; set; }
}
