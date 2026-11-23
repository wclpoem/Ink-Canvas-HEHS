using System.Net;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.AiAssistant;
using InkCanvas.Modules.AiAssistant.Providers;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AssistantAgentTests
{
    private static readonly AiProviderRequest Input = new("协助教师", "设计课堂活动", Array.Empty<AssistantMessage>(), Array.Empty<string>());

    [Theory]
    [InlineData(AiApiFormat.ChatCompletions)]
    [InlineData(AiApiFormat.Responses)]
    [InlineData(AiApiFormat.Anthropic)]
    [InlineData(AiApiFormat.Google)]
    public async Task NativeToolCallsKeepRequiredContinuationAndHideThinking(AiApiFormat format)
    {
        string[] packets = format switch
        {
            AiApiFormat.ChatCompletions => new[]
            {
                """{"choices":[{"delta":{"reasoning_content":"private","tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_workspace","arguments":"{\"page\":"}}]}}]}""",
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]}}]}""",
            },
            AiApiFormat.Responses => new[]
            {
                """{"type":"response.output_item.done","item":{"type":"reasoning","id":"rs_1","encrypted_content":"encrypted","summary":[]}}""",
                """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"read_workspace","arguments":"{\"page\":1}"}}""",
            },
            AiApiFormat.Anthropic => new[]
            {
                """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"private"}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}""",
                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call_1","name":"read_workspace","input":{}}}""",
                """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"page\":1}"}}""",
            },
            _ => new[] { """{"candidates":[{"content":{"parts":[{"thought":true,"text":"private"},{"functionCall":{"name":"read_workspace","args":{"page":1}},"thoughtSignature":"signed"}]}}]}""" },
        };
        using var http = new HttpClient(new Handler(string.Join("\n\n", packets.Select(packet => "data: " + packet)) + "\n\ndata: [DONE]\n\n"));
        using var client = new AiProviderClient(http);
        var text = new StringBuilder();
        var round = await client.RunRoundAsync(new() { BaseUrl = "https://fixture.example/v1", Model = "fixture", Format = format }, "", Input, delta => text.Append(delta), CancellationToken.None);
        var call = Assert.Single(round.Calls);
        Assert.Equal("read_workspace", call.Name); Assert.Equal(1, call.Arguments.GetProperty("page").GetInt32());
        Assert.Empty(round.Text); Assert.Empty(text.ToString());
        string continuation = round.Continuation.ToJsonString();
        if (format == AiApiFormat.Responses) Assert.Contains("encrypted", continuation);
        if (format is AiApiFormat.Anthropic or AiApiFormat.Google) Assert.Contains("signed", continuation);
    }

    [Theory]
    [InlineData(AiApiFormat.ChatCompletions)]
    [InlineData(AiApiFormat.Responses)]
    [InlineData(AiApiFormat.Anthropic)]
    [InlineData(AiApiFormat.Google)]
    public async Task RequestsCarryNativeToolsFilesAndThinking(AiApiFormat format)
    {
        var input = Input with { Thinking = true, Effort = "xhigh", Files = new[] { new AiFileInput("lesson.pdf", "application/pdf", "AAAA") },
            Tools = new[] { new AssistantToolDefinition("read_workspace", "读取白板", """{"type":"object","properties":{}}""") } };
        using var request = AiProviderPayload.Create(new() { BaseUrl = "https://fixture.example/v1", Model = "fixture", Reasoning = "supported", Format = format }, "", input, true);
        using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("tools", out _));
        string json = body.RootElement.GetRawText(); Assert.Contains("read_workspace", json); Assert.Contains("application/pdf", json);
        Assert.Contains(format == AiApiFormat.Google ? "thinkingConfig" : format == AiApiFormat.Anthropic ? "budget_tokens" : format == AiApiFormat.Responses ? "reasoning" : "reasoning_effort", json);
    }

    [Fact]
    public void CompactionKeepsRecentWholeTurnsAndRetainsFullHistory()
    {
        var chat = new AssistantChat();
        for (int index = 0; index < 40; index++) chat.Turns.Add(new() { Question = "问题 " + index, Answer = new string('文', 500), State = "done" });
        int end = AssistantContext.CompactionEnd(chat, 8192);
        Assert.InRange(end, 1, 37);
        chat.Summary = "已确认教学目标"; chat.SummarizedTurns = end;
        var messages = AssistantContext.Messages(chat);
        Assert.Equal("问题 " + end, messages[0].Content); Assert.Equal(40, chat.Turns.Count);
        Assert.DoesNotContain(messages, message => message.Content == "问题 39");
    }

    [Fact]
    public void WorkspacePreservesChatsImagesAndOnlyFilePaths()
    {
        var workspace = new AssistantWorkspace(); var first = workspace.CreateChat();
        first.Turns.Add(new() { Question = "第一轮", Attachments = new() { new() { Label = "image", Image = "data:image/jpeg;base64,AAAA" }, new() { Path = "D:\\lesson.docx" } } });
        var second = workspace.CreateChat(); second.Draft = "继续备课";
        var restored = AssistantWorkspace.Load(workspace.Save());
        Assert.Equal(2, restored.Chats.Count); Assert.Equal(second.Id, restored.Active.Id);
        Assert.Equal("stopped", restored.Chats[0].Turns[0].State);
        Assert.Equal("D:\\lesson.docx", restored.Chats[0].Turns[0].Attachments[1].Path);
        Assert.Equal("data:image/jpeg;base64,AAAA", restored.Chats[0].Turns[0].Attachments[0].Image);
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") });
    }
}
