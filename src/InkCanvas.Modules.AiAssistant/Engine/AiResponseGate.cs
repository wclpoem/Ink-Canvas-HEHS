namespace InkCanvas.Core.AiAssistant;

/// <summary>
/// 丢弃过期 AI 响应：换题、翻页或关闭浮窗后调用 <see cref="Begin"/> 递增世代，
/// 旧流回调携带的世代不匹配时不得写入新会话。
/// </summary>
public sealed class AiResponseGate
{
    private int _generation;

    public string SessionId { get; private set; } = "";

    public int Generation => _generation;

    public int Begin(string sessionId)
    {
        SessionId = sessionId ?? "";
        _generation++;
        return _generation;
    }

    public bool IsCurrent(int generation, string sessionId)
        => generation == _generation
           && string.Equals(sessionId ?? "", SessionId, StringComparison.Ordinal);

    public bool IsCurrent(int generation)
        => generation == _generation;

    public void Invalidate() => _generation++;
}