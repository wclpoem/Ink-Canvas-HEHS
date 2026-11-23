using System.Net;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Modules.AiAssistant.Providers;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AiProviderTests
{
    private static readonly AiProviderRequest Input = new("System", "Question", new[] { new AssistantMessage("assistant", "Previous") }, new[] { "AQID" });

    [Theory]
    [InlineData(AiApiFormat.ChatCompletions, "/proxy/v1/chat/completions", "messages")]
    [InlineData(AiApiFormat.Responses, "/proxy/v1/responses", "input")]
    [InlineData(AiApiFormat.Anthropic, "/proxy/v1/messages", "messages")]
    [InlineData(AiApiFormat.Google, "/proxy/v1/models/test-model:streamGenerateContent", "contents")]
    public async Task FormatsKeepGatewayPrefixAndEncodeMultimodalHistory(AiApiFormat format, string path, string field)
    {
        var connection = new AiConnection { BaseUrl = "https://gateway.example/proxy/v1", Format = format, Model = "test-model" };
        using var request = AiProviderPayload.Create(connection, "fixture-key", Input, true);
        Assert.Equal(path, request.RequestUri.AbsolutePath);
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty(field).GetArrayLength() >= 2);
        Assert.Contains("AQID", json.RootElement.ToString());
        Assert.DoesNotContain("fixture-key", json.RootElement.ToString());
        Assert.DoesNotContain("fixture-key", request.RequestUri.ToString());
        Assert.True(request.Headers.Contains(format switch { AiApiFormat.Anthropic => "x-api-key", AiApiFormat.Google => "x-goog-api-key", _ => "Authorization" }));
    }

    [Theory]
    [InlineData("https://a.example/v1/chat/completions", AiApiFormat.Responses, "https://a.example/v1/responses")]
    [InlineData("https://a.example/anthropic/v1/messages", AiApiFormat.Anthropic, "https://a.example/anthropic/v1/messages")]
    [InlineData("https://a.example/v1beta/models/old:generateContent", AiApiFormat.Google, "https://a.example/v1beta/models/test:generateContent")]
    public void FullEndpointDoesNotDuplicateSuffix(string url, AiApiFormat format, string expected)
        => Assert.Equal(expected, AiEndpoint.Completion(new() { BaseUrl = url, Format = format, Model = "test" }, false).AbsoluteUri);

    [Theory]
    [InlineData("file:///c:/private")]
    [InlineData("https://user:key@example.com/v1")]
    [InlineData("not-a-url")]
    public void InvalidEndpointsFailBeforeSending(string url) => Assert.Throws<ArgumentException>(() => AiEndpoint.Validate(url));

    [Theory]
    [InlineData("deepseek", AiApiFormat.ChatCompletions, "deepseek_web_search")]
    [InlineData("deepseek", AiApiFormat.Responses, "deepseek_web_search")]
    [InlineData("deepseek", AiApiFormat.Anthropic, "deepseek_web_search")]
    [InlineData("qwen", AiApiFormat.Responses, "web_search")]
    [InlineData("openai-responses", AiApiFormat.Responses, "web_search")]
    public async Task NativeServerSearchToolsUseProviderFormat(string provider, AiApiFormat format, string tool)
    {
        using var request = AiProviderPayload.Create(new() { BaseUrl = "https://api.example/v1", Provider = provider, Format = format, Model = "m", Search = true }, "key", Input, true);
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Assert.Equal(tool, json.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.False(json.RootElement.TryGetProperty("web_search_options", out _));
    }

    [Fact]
    public async Task QwenSearchUsesEnableSearchAndTextOnlyChoiceOmitsImages()
    {
        using var request = AiProviderPayload.Create(new() { BaseUrl = "https://api.example/v1", Provider = "qwen", Model = "qwen", Search = true, SendImages = false }, "key", Input, true);
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("enable_search").GetBoolean());
        Assert.DoesNotContain("AQID", json.RootElement.ToString());
    }

    [Fact]
    public void UnsupportedSearchExplainsWhy() => Assert.Throws<InvalidOperationException>(() => AiProviderPayload.Create(
        new() { BaseUrl = "https://api.example/v1", Model = "m", Search = true }, "key", Input, true));

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"},{\"id\":\"b\"}]}", 2)]
    [InlineData("{\"models\":[{\"name\":\"models/gemini\",\"supportedGenerationMethods\":[\"generateContent\"]},{\"name\":\"models/embed\",\"supportedGenerationMethods\":[\"embedContent\"]}]}", 1)]
    [InlineData("[\"one\",\"two\"]", 2)]
    public void ModelListHandlesGatewayGoogleAndDeduplication(string source, int count)
    {
        using var json = JsonDocument.Parse(source);
        var models = AiProviderClient.ParseModels(json.RootElement);
        Assert.Equal(count, models.Count);
        Assert.All(models, model => Assert.DoesNotContain("models/", model.Id));
    }

    [Theory]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"答案\"}}]}")]
    [InlineData("{\"type\":\"response.output_text.delta\",\"delta\":\"答案\"}")]
    [InlineData("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"答案\"}}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"private\",\"thought\":true},{\"text\":\"答案\"}]}}]}")]
    public void StreamingDecodersExtractAnswerAndHideReasoning(string source)
    {
        using var json = JsonDocument.Parse(source);
        Assert.Equal("答案", AiProviderResponse.Text(json.RootElement, true));
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"secret\"}}")]
    [InlineData("{\"type\":\"response.failed\"}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}]}")]
    public void ServerErrorsAndTruncationAreNeverSuccess(string source)
    {
        using var json = JsonDocument.Parse(source);
        Assert.Throws<InvalidDataException>(() => AiProviderResponse.Text(json.RootElement, true));
    }

    [Fact]
    public async Task RealHttpPipelineHandlesSseMultilineAndNonstreamJson()
    {
        using var http = new HttpClient(new StubHandler(request => request.Headers.Accept.Any(value => value.MediaType == "text/event-stream")
            ? new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"一\"}}]}\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"二\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n", Encoding.UTF8, "text/event-stream")
            : new StringContent("{\"choices\":[{\"message\":{\"content\":\"完成\"}}]}", Encoding.UTF8, "application/json")));
        using var client = new AiProviderClient(http);
        var connection = new AiConnection { BaseUrl = "https://fixture.example/v1", Model = "fixture" };
        var text = new StringBuilder();
        await foreach (string part in client.StreamAsync(connection, "key", Input)) text.Append(part);
        Assert.Equal("一二", text.ToString());
        Assert.Equal("完成", await client.CompleteAsync(connection, "key", Input));
    }

    [Fact]
    public async Task StreamingErrorsAndEmptyResultsDoNotBecomeCompletedReplies()
    {
        using var http = new HttpClient(new StubHandler(_ => new StringContent("data: {\"error\":{\"message\":\"bad\"}}\n\n", Encoding.UTF8, "text/event-stream")));
        using var client = new AiProviderClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (string text in client.StreamAsync(new() { BaseUrl = "https://a.example/v1", Model = "m" }, "key", Input)) { } });
    }

    [Fact]
    public async Task ModelPaginationKeepsGatewayPathAndUsesPageToken()
    {
        var addresses = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            addresses.Add(request.RequestUri);
            return new StringContent(addresses.Count == 1
                ? "{\"models\":[{\"name\":\"models/first\"}],\"nextPageToken\":\"next\"}"
                : "{\"models\":[{\"name\":\"models/second\"}]}", Encoding.UTF8, "application/json");
        }));
        using var client = new AiProviderClient(http);
        var models = await client.ModelsAsync(new() { BaseUrl = "https://gateway.example/proxy/v1beta", Format = AiApiFormat.Google }, "key");
        Assert.Equal(2, models.Count);
        Assert.Equal("/proxy/v1beta/models", addresses[1].AbsolutePath);
        Assert.Contains("pageToken=next", addresses[1].Query);
    }

    [Fact]
    public void ConnectionStateAndCredentialsPersistSeparatelyAndDeleteSafely()
    {
        string directory = Directory.CreateTempSubdirectory("inkcanvas-ai-connections-").FullName;
        try
        {
            var credentials = new MemoryCredentials();
            var store = new AiConnectionStore(directory, credentials);
            var connection = new AiConnection { Name = "Gateway", BaseUrl = "https://gateway.example/proxy/v1", Model = "model" };
            store.Save(connection, "never-write-key-to-json");
            Assert.DoesNotContain("never-write-key-to-json", File.ReadAllText(Path.Combine(directory, "connections.json")));
            var reopened = new AiConnectionStore(directory, credentials);
            Assert.Equal(connection, reopened.Active);
            Assert.Equal("never-write-key-to-json", reopened.Key(connection.Id));
            reopened.Activate(""); Assert.Null(reopened.Active);
            reopened.Activate(connection.Id); reopened.Delete(connection.Id);
            Assert.Empty(reopened.State.Connections); Assert.Equal("", reopened.Key(connection.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CatalogIncludesAllRequestedProvidersAndValidPresetEndpoints()
    {
        foreach (string provider in new[] { "openai", "openai-responses", "anthropic", "google", "deepseek", "glm", "tokenhub", "qwen",
            "volcengine", "stepfun", "mimo", "minimax", "kimi", "kimi-code", "opencode", "packycode" })
        {
            var preset = Assert.Single(AiProviderCatalog.Presets, item => item.Id == provider);
            Assert.Equal("https", AiEndpoint.Validate(preset.BaseUrl).Scheme);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpContent> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = reply(request) });
    }
    private sealed class MemoryCredentials : IAiCredentialStore
    {
        private readonly Dictionary<string, string> _keys = new();
        public string Read(string id) => _keys.GetValueOrDefault(id, "");
        public void Write(string id, string key) => _keys[id] = key;
    }
}
