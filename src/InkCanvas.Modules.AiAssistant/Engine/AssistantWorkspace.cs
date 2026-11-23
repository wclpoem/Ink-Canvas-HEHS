using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InkCanvas.Modules.AiAssistant;

public sealed class AssistantWorkspace
{
    public int Version { get; set; } = 1;
    public Guid ActiveId { get; set; }
    public List<AssistantChat> Chats { get; set; } = new();

    [JsonIgnore] public AssistantChat Active => Chats.FirstOrDefault(chat => chat.Id == ActiveId);
    public AssistantChat CreateChat()
    {
        var chat = new AssistantChat();
        Chats.Add(chat);
        ActiveId = chat.Id;
        return chat;
    }

    public byte[] Save() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static AssistantWorkspace Load(byte[] data)
    {
        if (data is null) return new();
        var workspace = JsonSerializer.Deserialize<AssistantWorkspace>(data) ?? throw new InvalidDataException("对话记录为空。");
        if (workspace.Version != 1 || workspace.Chats is null) throw new InvalidDataException("此对话记录版本暂不支持。");
        foreach (var chat in workspace.Chats)
        {
            if (chat is null || chat.Turns is null || chat.DraftAttachments is null || chat.Title is null
                || chat.Draft is null || chat.Summary is null || chat.SummarizedTurns < 0 || chat.SummarizedTurns > chat.Turns.Count
                || chat.Turns.Any(turn => turn is null || turn.Attachments is null || turn.Tools is null || turn.Question is null || turn.Answer is null
                    || turn.SummarizedTools < 0 || turn.SummarizedTools > turn.Tools.Count))
                throw new InvalidDataException("对话记录不完整。");
            foreach (var turn in chat.Turns.Where(turn => turn.State == "running")) turn.State = "stopped";
        }
        return workspace;
    }
}

public sealed class AssistantChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新对话";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public bool Thinking { get; set; } = true;
    public string Effort { get; set; } = "high";
    public string Summary { get; set; } = "";
    public int SummarizedTurns { get; set; }
    public string Draft { get; set; } = "";
    public List<AssistantAttachment> DraftAttachments { get; set; } = new();
    public List<AssistantChatTurn> Turns { get; set; } = new();
    public override string ToString() => Title;
}

public sealed class AssistantChatTurn : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Question { get; set; } = "";
    public List<AssistantAttachment> Attachments { get; set; } = new();
    public string Answer { get; set; } = "";
    public string Error { get; set; } = "";
    public string Fallback { get; set; } = "";
    public string State { get; set; } = "running";
    public string Activity { get; set; } = "";
    public string ToolSummary { get; set; } = "";
    public int SummarizedTools { get; set; }
    public List<AssistantToolActivity> Tools { get; set; } = new();
    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public sealed record AssistantToolActivity(string Tool, string Result, string Arguments = "", bool IsError = false);

public sealed class AssistantAttachment
{
    public string Path { get; set; } = "";
    public string Image { get; set; } = "";
    public string Label { get; set; } = "";
    [JsonIgnore] public bool IsImage => !string.IsNullOrEmpty(Image);
    [JsonIgnore] public string Name => IsImage ? Label : System.IO.Path.GetFileName(Path);
}

public sealed class AssistantPreferences
{
    public const int DefaultContextTokens = 256 * 1024;
    public int ContextVersion { get; set; } = 2;
    public bool ToolsEnabled { get; set; } = true;
    public int ContextTokens { get; set; } = DefaultContextTokens;
    public int CompactAtPercent { get; set; } = 80;
    public string Instructions { get; set; } = "";

    public static AssistantPreferences Read(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var preferences = JsonSerializer.Deserialize<AssistantPreferences>(data) ?? new();
        if (!document.RootElement.TryGetProperty(nameof(ContextVersion), out _) && preferences.ContextTokens == 32768)
            preferences.ContextTokens = DefaultContextTokens;
        preferences.ContextVersion = 2;
        if (preferences.ContextTokens is < 8192 or > 1048576) preferences.ContextTokens = DefaultContextTokens;
        preferences.CompactAtPercent = Math.Clamp(preferences.CompactAtPercent, 50, 95);
        preferences.Instructions ??= "";
        return preferences;
    }
}
