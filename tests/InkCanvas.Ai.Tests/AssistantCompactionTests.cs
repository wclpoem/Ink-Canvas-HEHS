using System.Text.Json.Nodes;
using InkCanvas.Modules.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AssistantCompactionTests
{
    [Fact]
    public void DefaultsAndMigrationUse256kWhilePreservingCustomWindows()
    {
        Assert.Equal(262144, new AssistantPreferences().ContextTokens);
        Assert.Equal(262144, AssistantPreferences.Read("{}"u8.ToArray()).ContextTokens);
        Assert.Equal(262144, AssistantPreferences.Read("""{"ContextTokens":32768}"""u8.ToArray()).ContextTokens);
        Assert.Equal(65536, AssistantPreferences.Read("""{"ContextTokens":65536}"""u8.ToArray()).ContextTokens);
        Assert.Equal(32768, AssistantPreferences.Read("""{"ContextTokens":32768,"ContextVersion":2}"""u8.ToArray()).ContextTokens);
        Assert.Equal(80, new AssistantPreferences().CompactAtPercent);
    }

    [Fact]
    public async Task RollingSummaryIncludesEveryChunkAndKeepsThePreviousSummary()
    {
        var inputs = new List<string>();
        string result = await AssistantCompaction.SummarizeAsync("原始目标", Enumerable.Range(0, 12).Select(index => "资料" + index + new string('字', 500)),
            4096, (prompt, _) => { inputs.Add(prompt); return Task.FromResult("摘要 " + inputs.Count); }, CancellationToken.None);
        Assert.True(inputs.Count > 2);
        Assert.Contains("原始目标", inputs[0]);
        for (int index = 1; index < inputs.Count; index++) Assert.Contains("摘要 " + index, inputs[index]);
        Assert.Equal("摘要 " + inputs.Count, result);
    }

    [Fact]
    public void ChunkingPreservesUnicodeAndRespectsTheInputBudget()
    {
        string source = string.Concat(Enumerable.Repeat("a😀汉字\n", 2000));
        var chunks = AssistantCompaction.Chunks(new[] { source }, 2048).ToArray();
        Assert.Equal(source + "\n", string.Concat(chunks));
        Assert.All(chunks, chunk => { Assert.InRange(AssistantContext.Estimate(chunk), 1, 2048); Assert.False(char.IsHighSurrogate(chunk[^1])); });
    }

    [Fact]
    public async Task FailedCompactionNeverCommitsAPartialSummaryOrRemovesTurns()
    {
        var chat = new AssistantChat { Summary = "原摘要", SummarizedTurns = 0 };
        chat.Turns.Add(new() { Question = "保留原文", Answer = new string('文', 20000) });
        int calls = 0;
        await Assert.ThrowsAsync<IOException>(() => AssistantCompaction.SummarizeAsync(chat.Summary,
            AssistantContext.HistorySegments(chat, 1), 4096, (_, _) => ++calls == 2 ? throw new IOException("offline") : Task.FromResult("中间摘要"), CancellationToken.None));
        Assert.Equal("原摘要", chat.Summary); Assert.Equal(0, chat.SummarizedTurns); Assert.Single(chat.Turns);
    }

    [Fact]
    public void ToolSummariesKeepArgumentsResultsAndFailuresWithoutDuplicatingOldResults()
    {
        var turn = new AssistantChatTurn { ToolSummary = "已新建第2页", SummarizedTools = 1,
            Tools = new() { new("board_page", "旧结果", "{page:2}"), new("pdf_read", "文件缺失", "{page:3}", true) } };
        string context = AssistantContext.ToolContext(turn);
        Assert.Contains("已新建第2页", context); Assert.Contains("{page:3}", context); Assert.Contains("失败", context);
        Assert.DoesNotContain("旧结果", context);
        Assert.Equal(2, turn.Tools.Count);
    }

    [Fact]
    public void ToolTranscriptOmitsHiddenThinkingSignaturesAndImageBytes()
    {
        var exchange = JsonNode.Parse("""
            [{"type":"reasoning","encrypted_content":"hidden"},
             {"role":"assistant","reasoning_content":"private","content":[{"type":"thinking","thinking":"private"},{"type":"text","text":"done"}]},
             {"role":"tool","content":"success","tool_call_id":"id"},
             {"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AAAA"}}]}]
            """).AsArray();
        string transcript = AssistantCompaction.ToolTranscript(exchange);
        Assert.DoesNotContain("private", transcript); Assert.DoesNotContain("hidden", transcript); Assert.DoesNotContain("AAAA", transcript);
        Assert.Contains("success", transcript); Assert.Contains("id", transcript);
    }

    [Fact]
    public void LargeToolCatalogSelectsRelevantSchemasAndKeepsDiscoveryAvailable()
    {
        var tools = Enumerable.Range(0, 120).Select(index => new InkCanvas.Host.Contracts.AssistantToolDefinition("other_" + index, new string('x', 500), """{"type":"object","properties":{}}""")).ToList();
        tools.Add(new("tool_catalog", "查找插件工具", """{"type":"object","properties":{}}"""));
        tools.Add(new("pdf_read", "读取 PDF 页面的文字", """{"type":"object","properties":{}}"""));
        var selected = AssistantToolSelection.Select(tools, "请读取 PDF", 8192);
        Assert.True(selected.Count < tools.Count);
        Assert.Contains(selected, tool => tool.Name == "tool_catalog"); Assert.Contains(selected, tool => tool.Name == "pdf_read");
        var activated = AssistantToolSelection.Select(tools, "继续", 8192, new HashSet<string> { "pdf_read" });
        Assert.Contains(activated, tool => tool.Name == "pdf_read");
    }
}
