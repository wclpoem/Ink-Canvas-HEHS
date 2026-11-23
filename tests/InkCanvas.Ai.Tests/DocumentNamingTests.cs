using System.Net;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class DocumentNamingTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    public void ScreensWithin4KKeepNativePixels(int width, int height)
    {
        var image = Assert.Single(ArchiveRenderPlan.Create(new(0, 0, width, height), true, width, height, Array.Empty<(Rect, int)>()));
        Assert.Equal(width, image.Width); Assert.Equal(height, image.Height); Assert.Equal("original", image.Role);
    }

    [Fact]
    public void LargeAnnotationHasBoundedOverviewAndDenseInkDetails()
    {
        var regions = ArchiveRenderPlan.Create(new(-8000, -4000, 12000, 12000), true, 1920, 1080,
            new[] { (new Rect(-6000, -2000, -5900, -1900), 800), (new Rect(100, 100, 200, 200), 5) });
        Assert.Equal("overview", regions[0].Role);
        Assert.InRange(regions[0].Width * (long)regions[0].Height, 1, ArchiveRenderPlan.MaximumPixels + 8000L);
        Assert.Contains(regions, image => image.Role == "detail" && image.Bounds.Contains(new System.Numerics.Vector2(-6000, -2000)));
        Assert.All(regions.Where(image => image.Role == "detail"), image => Assert.Equal((int)Math.Ceiling(image.Bounds.Width), image.Width));
    }

    [Fact]
    public void LongWhiteboardIsSplitWithOverlapAndCompleteCoverage()
    {
        var regions = ArchiveRenderPlan.Create(new(-100, 0, 1820, 9000), false, 1920, 1080, Array.Empty<(Rect, int)>());
        Assert.True(regions.Count > 4);
        Assert.Equal(-100, regions[0].Bounds.Left);
        Assert.Equal(9000, regions[^1].Bounds.Bottom);
        Assert.All(regions, image => Assert.Equal("segment", image.Role));
        for (int index = 1; index < regions.Count; index++) Assert.True(regions[index].Bounds.Top <= regions[index - 1].Bounds.Bottom);
    }

    [Theory]
    [InlineData("```json\n{\"title\":\"二次函数/复习\",\"description\":\"顶点式与图像\"}\n```", "二次函数复习")]
    [InlineData("{\"title\":\"CON\",\"description\":\"描述\"}", "文稿 CON")]
    public void ModelFilenamesAreSanitized(string response, string expected) => Assert.Equal(expected, DocumentNamingClient.Parse(response).Title);

    [Fact]
    public void EmptyModelResponseNeverBecomesAFileName() => Assert.ThrowsAny<Exception>(() => DocumentNamingClient.Parse("{\"title\":\"\",\"description\":\"\"}"));

    [Fact]
    public async Task MultimodalRequestStreamsAllImagesAndParsesTheCaption()
    {
        string directory = Directory.CreateTempSubdirectory("InkCanvas-document-http-").FullName;
        try
        {
            byte[] first = { 137, 80, 78, 71, 1, 2, 3 }, second = { 137, 80, 78, 71, 4, 5 };
            string a = Path.Combine(directory, "a.png"), b = Path.Combine(directory, "b.png");
            await File.WriteAllBytesAsync(a, first); await File.WriteAllBytesAsync(b, second);
            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            var client = new DocumentNamingClient(http);
            var caption = await client.DescribeAsync(new[] { new AiDocumentImage(a, 1, "overview", 100, 100), new AiDocumentImage(b, 1, "detail", 50, 50) }, "前一批是函数定义", new() { Endpoint = "http://localhost/v1/chat/completions", Model = "test-vision" }, default);
            Assert.Equal("函数课堂", caption.Title);
            Assert.Equal("application/json", handler.ContentType);
            using var payload = JsonDocument.Parse(handler.Payload);
            var content = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Contains("前一批是函数定义", content[0].GetProperty("text").GetString());
            var urls = content.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "image_url").Select(item => item.GetProperty("image_url").GetProperty("url").GetString()).ToArray();
            Assert.Equal(new[] { "data:image/png;base64," + Convert.ToBase64String(first), "data:image/png;base64," + Convert.ToBase64String(second) }, urls);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Payload, ContentType;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ContentType = request.Content.Headers.ContentType?.MediaType;
            Payload = await request.Content.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"title\":\"函数课堂\",\"description\":\"二次函数的图像与性质\"}" } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
