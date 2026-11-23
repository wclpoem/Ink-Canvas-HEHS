using System.Text.RegularExpressions;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.AiAssistant;

public static class AssistantToolSelection
{
    public static IReadOnlyList<AssistantToolDefinition> Select(IReadOnlyList<AssistantToolDefinition> tools, string query,
        int budget, IReadOnlySet<string> activated = null)
    {
        int available = Math.Max(1024, Math.Min(32768, budget / 6));
        int Cost(AssistantToolDefinition tool) => AssistantContext.Estimate(tool.Name + tool.Description + tool.Parameters) + 24;
        if (tools.Sum(Cost) <= available) return tools;
        string[] keywords = Regex.Matches(query ?? "", @"[A-Za-z_][A-Za-z_0-9.-]*|[\p{IsCJKUnifiedIdeographs}]{2,}")
            .Select(match => match.Value).SelectMany(word => word.Any(character => character > 127)
                ? Enumerable.Range(0, word.Length - 1).Select(index => word.Substring(index, 2)) : new[] { word })
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        int Score(AssistantToolDefinition tool)
        {
            if (tool.Name is "tool_catalog" or "assistant_history_read" or "read_workspace") return int.MaxValue;
            if (activated?.Contains(tool.Name) == true) return int.MaxValue - 1;
            string description = tool.Name + " " + tool.Description + " " + tool.Owner;
            return keywords.Count(keyword => description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        var selected = new List<AssistantToolDefinition>();
        foreach (var tool in tools.OrderByDescending(Score).ThenBy(tool => tool.Name, StringComparer.Ordinal))
        {
            int cost = Cost(tool);
            if (cost > available) continue;
            selected.Add(tool); available -= cost;
        }
        return selected;
    }
}
