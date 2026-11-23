using System.Text;
using System.Text.Json;

namespace InkCanvas.Core.AiAssistant;

public sealed class SseStreamParser
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _dataLines = new();
    private string _eventName = "";

    public IReadOnlyList<AssistantEvent> Push(string chunk)
    {
        var events = new List<AssistantEvent>();
        if (string.IsNullOrEmpty(chunk)) return events;

        _buffer.Append(chunk);
        DrainLines(events);
        return events;
    }

    public IReadOnlyList<AssistantEvent> Flush()
    {
        var events = new List<AssistantEvent>();
        if (_buffer.Length > 0)
        {
            string rest = _buffer.ToString();
            _buffer.Clear();
            var parts = rest.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var line in parts) ProcessLine(line, events);
        }

        Dispatch(events);
        return events;
    }

    private void DrainLines(List<AssistantEvent> events)
    {
        while (true)
        {
            int index = -1;
            int length = 0;
            for (int i = 0; i < _buffer.Length; i++)
            {
                char c = _buffer[i];
                if (c == '\n')
                {
                    index = i;
                    length = 1;
                    break;
                }

                if (c == '\r')
                {
                    index = i;
                    length = i + 1 < _buffer.Length && _buffer[i + 1] == '\n' ? 2 : 1;
                    break;
                }
            }

            if (index < 0) return;

            string line = _buffer.ToString(0, index);
            _buffer.Remove(0, index + length);
            ProcessLine(line, events);
        }
    }

    private void ProcessLine(string line, List<AssistantEvent> events)
    {
        if (line.Length == 0)
        {
            Dispatch(events);
            return;
        }

        if (line[0] == ':') return;

        int colon = line.IndexOf(':');
        if (colon < 0) return;

        string field = line.Substring(0, colon);
        string value = line.Substring(colon + 1);
        if (value.StartsWith(" ")) value = value.Substring(1);

        switch (field)
        {
            case "event":
                _eventName = value.Trim();
                break;

            case "data":
                _dataLines.Add(value);
                break;
        }
    }

    private void Dispatch(List<AssistantEvent> events)
    {
        if (_dataLines.Count == 0)
        {
            _eventName = "";
            return;
        }

        string data = string.Join("\n", _dataLines);
        var ev = Parse(_eventName, data);
        if (ev != null) events.Add(ev);

        _dataLines.Clear();
        _eventName = "";
    }

    public static AssistantEvent Parse(string eventName, string data)
    {
        data ??= "";
        string trimmed = data.Trim();
        if (trimmed.Length == 0) return null;

        if (string.Equals(trimmed, "[DONE]", StringComparison.OrdinalIgnoreCase))
            return new AssistantEvent { Kind = AssistantEventKind.Done };

        string name = (eventName ?? "").Trim().ToLowerInvariant();
        AssistantEventKind? kind = KindFromName(name);

        JsonDocument document = null;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document == null)
        {
            if (kind == null) return null;
            return new AssistantEvent { Kind = kind.Value, Text = data, Latex = data };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return kind == null ? null : new AssistantEvent { Kind = kind.Value, Text = data };

            string type = ReadString(root, "type") ?? ReadString(root, "event") ?? "";
            if (kind == null)
                kind = KindFromName(type.Trim().ToLowerInvariant());

            bool hasBlocks = root.TryGetProperty("blocks", out JsonElement blocks) &&
                             blocks.ValueKind == JsonValueKind.Array;
            if (kind == AssistantEventKind.BoardPlan ||
                string.Equals(type.Trim(), "board_plan", StringComparison.OrdinalIgnoreCase) ||
                hasBlocks)
            {
                return new AssistantEvent { Kind = AssistantEventKind.BoardPlan, Text = data };
            }

            string content = ReadOpenAiContent(root) ?? ReadString(root, "content") ?? ReadString(root, "text");
            string latex = ReadString(root, "latex") ?? ReadString(root, "formula");
            string title = ReadString(root, "title") ?? "";
            int index = ReadInt(root, "index") ?? 0;
            string message = ReadString(root, "message") ?? ReadString(root, "error") ?? "";

            if (kind == null && !string.IsNullOrEmpty(content))
                kind = AssistantEventKind.Token;

            if (kind == null) return null;

            return new AssistantEvent
            {
                Kind = kind.Value,
                Text = kind.Value == AssistantEventKind.Error
                    ? (string.IsNullOrEmpty(content) ? message : content)
                    : (content ?? message),
                Latex = latex ?? "",
                Index = index,
                Title = title,
                Points = ReadPoints(root),
            };
        }
    }

    private static AssistantEventKind? KindFromName(string name)
    {
        switch (name)
        {
            case "token":
                return AssistantEventKind.Token;
            case "formula":
                return AssistantEventKind.Formula;
            case "step":
                return AssistantEventKind.Step;
            case "shape_annotation":
            case "shapeannotation":
            case "shape":
                return AssistantEventKind.ShapeAnnotation;
            case "done":
                return AssistantEventKind.Done;
            case "error":
                return AssistantEventKind.Error;
            case "board_plan":
            case "boardplan":
                return AssistantEventKind.BoardPlan;
            default:
                return null;
        }
    }

    private static string ReadOpenAiContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object) continue;

            if (choice.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.Object)
            {
                string deltaContent = ReadString(delta, "content");
                if (!string.IsNullOrEmpty(deltaContent)) return deltaContent;
            }

            if (choice.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object)
            {
                string messageContent = ReadString(message, "content");
                if (!string.IsNullOrEmpty(messageContent)) return messageContent;
            }

            string text = ReadString(choice, "text");
            if (!string.IsNullOrEmpty(text)) return text;
        }

        return null;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return null;
    }

    private static IReadOnlyList<IReadOnlyList<double>> ReadPoints(JsonElement root)
    {
        if (!root.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return Array.Empty<IReadOnlyList<double>>();

        var result = new List<IReadOnlyList<double>>();
        foreach (var pair in points.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array)
            {
                var values = new List<double>();
                foreach (var component in pair.EnumerateArray())
                {
                    if (component.ValueKind == JsonValueKind.Number) values.Add(component.GetDouble());
                }

                result.Add(values);
            }
            else if (pair.ValueKind == JsonValueKind.Object)
            {
                var values = new List<double>();
                foreach (var property in pair.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number) values.Add(property.Value.GetDouble());
                }

                result.Add(values);
            }
        }

        return result;
    }
}