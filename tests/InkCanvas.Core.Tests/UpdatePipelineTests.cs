using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class UpdatePipelineTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests.Add(request.RequestUri.AbsoluteUri); return Task.FromResult(respond(request)); }
    }
    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }

    [Fact]
    public async Task DefaultSourceBatchesIncludeOfficialGitHubAndAllRequestedMirrors()
    {
        const string url = "https://api.github.com/repos/QiXinia/Ink-Canvas-HEHS/releases?per_page=100";
        var handler = new Handler(request => request.RequestUri.Host == "gh.acmsz.top"
            ? Response("[]") : throw new HttpRequestException("offline"));
        using var transport = new UpdateTransport(handler);
        Assert.Equal("[]", await transport.GetTextAsync(url, text => text == "[]", CancellationToken.None));
        Assert.Equal(new[] { url, "https://ghfile.geekertao.top/" + url, "https://github.dpik.top/" + url,
            "https://gh-proxy.com/" + url, "https://fastgit.cc/" + url, "https://gh.acmsz.top/" + url }, handler.Requests);
        Assert.Equal("gh.acmsz.top", transport.LastSource);
    }

    [Fact]
    public async Task DefaultDownloadBatchesIncludeTheSameSourcesAndVerifyTheSuccessfulPackage()
    {
        string directory = Temporary();
        try
        {
            const string url = "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v3.7.1/package.zip";
            var handler = new Handler(request => request.RequestUri.Host == "gh.acmsz.top"
                ? Response("package") : throw new HttpRequestException("offline"));
            using var transport = new UpdateTransport(handler);
            string path = Path.Combine(directory, "package.zip");
            await transport.DownloadAsync(url, path, 7, Hash("package"), null, CancellationToken.None);
            Assert.Equal(new[] { url, "https://ghfile.geekertao.top/" + url, "https://github.dpik.top/" + url,
                "https://gh-proxy.com/" + url, "https://fastgit.cc/" + url, "https://gh.acmsz.top/" + url }, handler.Requests);
            Assert.Equal("package", File.ReadAllText(path)); Assert.False(File.Exists(path + ".partial"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SourceTimeoutIsReportedAsTimeoutAndCallerCancellationStopsFailover()
    {
        var handler = new AsyncHandler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Response("unused"); });
        using var transport = new UpdateTransport(handler, Array.Empty<string>()) { HeaderTimeout = TimeSpan.FromMilliseconds(20) };
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.GetTextAsync("https://api.github.com/repos/a/b/releases", _ => true, CancellationToken.None));
        Assert.Contains("连接超时", failure.Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.GetTextAsync("https://api.github.com/repos/a/b/releases", _ => true, cancellation.Token));
    }

    [Fact]
    public async Task RateLimitedApisFallBackToPublicReleasePageAndRetainDigestVerification()
    {
        const string repository = "QiXinia/Ink-Canvas-HEHS";
        string assets = "<ul><li><a href=\"/" + repository + "/releases/download/v3.7.1/InkCanvas-WinUI3-3.7.1-win-x64.zip\">package</a>"
            + "<span>sha256:" + Hash("package") + "</span></li></ul>";
        string html = "<section><a href=\"/" + repository + "/releases/tag/v3.7.1\">Stable 3.7.1</a></section>";
        var handler = new Handler(request =>
        {
            if (request.RequestUri.AbsolutePath.Contains("api.github.com") || request.RequestUri.Host == "api.github.com")
                return new(HttpStatusCode.Forbidden);
            if (request.RequestUri.AbsolutePath.EndsWith("/releases")) return Response(html);
            if (request.RequestUri.AbsolutePath.Contains("expanded_assets")) return Response(assets);
            return Response("package");
        });
        using var service = new UpdateService(transport: new UpdateTransport(handler), cache: new UpdateCache(Temporary()));
        var release = Assert.Single(await service.GetReleasesAsync());
        Assert.Equal("v3.7.1", release.Tag); Assert.Equal(Hash("package"), release.Sha256);
        string archive = await service.DownloadAsync(release);
        try { Assert.Equal("package", File.ReadAllText(archive)); }
        finally { service.Dispose(); Directory.Delete(service.Cache.Root, true); }
    }

    [Fact]
    public async Task AttachmentOnlyMirrorsCanDiscoverAndDownloadLatestStableRelease()
    {
        var handler = new Handler(request =>
        {
            if (request.RequestUri.Host != "ghfile.geekertao.top") throw new HttpRequestException("offline");
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
                return Response(Hash("package") + "  InkCanvas-WinUI3-3.7.1-win-x64.zip");
            if (request.RequestUri.AbsolutePath.EndsWith(".zip")) return Response("package");
            return new(HttpStatusCode.Forbidden);
        });
        using var service = new UpdateService(transport: new UpdateTransport(handler), cache: new UpdateCache(Temporary()));
        var release = Assert.Single(await service.GetReleasesAsync());
        Assert.Equal("v3.7.1", release.Tag);
        string archive = await service.DownloadAsync(release);
        try { Assert.Equal("package", File.ReadAllText(archive)); Assert.Equal("ghfile.geekertao.top", service.LastSource); }
        finally { service.Dispose(); Directory.Delete(service.Cache.Root, true); }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }

    private sealed class UncancelableStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(_read.Task);
        protected override void Dispose(bool disposing) { _read.TrySetResult(0); base.Dispose(disposing); }
    }

    private sealed class TrickleStream() : MemoryStream(Encoding.UTF8.GetBytes("package"))
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(30, token);
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
        }
    }

    [Fact]
    public void PortablePackageWinsOverExeAndModuleArchives()
    {
        string json = """
            [{"tag_name":"v3.5.0","assets":[
              {"name":"inkcanvas.pdf-1.0.0.zip","browser_download_url":"https://example/module.zip"},
              {"name":"InkCanvas-Setup.exe","browser_download_url":"https://example/setup.exe"},
              {"name":"InkCanvas-WinUI3-3.5.0-win-arm64.zip","browser_download_url":"https://example/arm.zip"},
              {"name":"InkCanvas-WinUI3-3.5.0-win-x64.zip","browser_download_url":"https://example/x64.zip","digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
            ]}]
            """;
        var release = Assert.Single(ReleaseFeed.ParseStableList(json));
        Assert.True(release.IsPortable); Assert.Equal("https://example/x64.zip", release.InstallerUrl);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Null(ReleaseFeed.ParseLatest("{\"tag_name\":\"v9.0.0\",\"draft\":true}"));
        Assert.Empty(ReleaseFeed.ParseStableList("[null,42,{},\"invalid\"]"));
    }

    [Fact]
    public async Task ApiFailureAndInvalidMirrorContentFallThroughToNextSource()
    {
        var handler = new Handler(request => request.RequestUri.Host switch
        {
            "api.github.com" => throw new HttpRequestException("unreachable"),
            "gh-proxy.com" => Response("<html>error</html>"),
            _ => Response("[]"),
        });
        using var transport = new UpdateTransport(handler, new[] { "https://gh-proxy.com/", "https://gh-proxy.org/" });
        using var service = new UpdateService(transport: transport);
        Assert.Empty(await service.GetReleasesAsync());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("gh-proxy.org", transport.LastSource);
        Assert.Contains("/https://api.github.com/repos/QiXinia/Ink-Canvas-HEHS/releases?per_page=100", handler.Requests[1]);
    }

    [Fact]
    public async Task CorruptDownloadRetriesMirrorAndOnlyPublishesVerifiedFile()
    {
        string directory = Temporary();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes("valid package");
            var handler = new Handler(request => Response(request.RequestUri.Host == "github.com" ? "bad" : "valid package"));
            using var transport = new UpdateTransport(handler, new[] { "https://gh-proxy.com/" });
            string path = Path.Combine(directory, "package.zip");
            await transport.DownloadAsync("https://github.com/owner/repo/releases/download/v1/a.zip", path, data.Length,
                Convert.ToHexStringLower(SHA256.HashData(data)), null, CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".partial")); Assert.Equal(2, handler.Requests.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WrongHashAndCancellationNeverLeaveInstallableDownload()
    {
        string directory = Temporary();
        try
        {
            using var transport = new UpdateTransport(new Handler(_ => Response("wrong")), Array.Empty<string>());
            string path = Path.Combine(directory, "package.zip");
            await Assert.ThrowsAsync<HttpRequestException>(() => transport.DownloadAsync("https://github.com/a/b/releases/download/v1/a.zip", path, 5, new string('0', 64), null, CancellationToken.None));
            Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".partial"));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.GetTextAsync("https://api.github.com/repos/a/b/releases", _ => true, cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("truncated")]
    [InlineData("trickle")]
    [InlineData("ignores-cancellation")]
    public async Task StalledOrTruncatedDownloadRetriesMirror(string failure)
    {
        string directory = Temporary();
        try
        {
            var handler = new AsyncHandler(async (request, token) =>
            {
                if (request.RequestUri.Host != "github.com") return Response("package");
                if (failure == "headers") await Task.Delay(Timeout.Infinite, token);
                var response = Response("bad");
                if (failure == "body") response.Content = new StreamContent(new StalledStream());
                if (failure == "ignores-cancellation") response.Content = new StreamContent(new UncancelableStream());
                if (failure == "trickle") response.Content = new StreamContent(new TrickleStream());
                response.Content.Headers.ContentLength = 7;
                return response;
            });
            using var transport = new UpdateTransport(handler, new[] { "https://gh-proxy.com/" })
                { HeaderTimeout = TimeSpan.FromMilliseconds(100), ReadTimeout = TimeSpan.FromMilliseconds(100) };
            string path = Path.Combine(directory, "package.zip");
            await transport.DownloadAsync("https://github.com/a/b/releases/download/v1/a.zip", path, 7, Hash("package"), null, CancellationToken.None);
            Assert.Equal("package", File.ReadAllText(path));
            Assert.Equal("gh-proxy.com", transport.LastSource);
            Assert.False(File.Exists(path + ".partial"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingAssetDigestUsesRepositoryChecksumFile()
    {
        const string url = "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v3.5.0/";
        var release = new ReleaseInfo("v3.5.0", new(3, 5, 0, 0), "release", "", "", url + "app.zip", 7)
            { AssetName = "app.zip", ChecksumsUrl = url + "SHA256SUMS.txt" };
        var handler = new Handler(request => Response(request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? Hash("package") + "  *app.zip\n" : "package"));
        using var service = new UpdateService(transport: new UpdateTransport(handler, Array.Empty<string>()), cache: new UpdateCache(Temporary()));
        string archive = await service.DownloadAsync(release);
        try
        {
            Assert.Equal("package", File.ReadAllText(archive));
            Assert.Equal(2, handler.Requests.Count);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { InstallerUrl = "https://github.com/another/repo/releases/download/v1/app.zip" }));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { service.Dispose(); Directory.Delete(service.Cache.Root, true); }
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/root.dll")]
    [InlineData("a/../../escape.dll")]
    [InlineData("a.dll:stream")]
    [InlineData("folder./file")]
    [InlineData("CON.dll")]
    [InlineData("folder/LPT1")]
    [InlineData(".inkcanvas-update-files.json")]
    [InlineData(".inkcanvas-update.lock")]
    public async Task ArchiveCannotEscapeStaging(string entry)
    {
        string directory = Temporary();
        try
        {
            string archive = Path.Combine(directory, "bad.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            { await using var data = zip.CreateEntry(entry).Open(); await data.WriteAsync(new byte[] { 1 }); }
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.PrepareAsync(archive, Path.Combine(directory, "work"), directory, "3.5.0"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ApplyReplacesProgramPreservesUserFilesAndRemovesOnlyPriorManagedFiles()
    {
        string directory = Temporary();
        try
        {
            var plan = Plan(directory);
            string target = plan.TargetDirectory;
            File.WriteAllText(Path.Combine(target, "notes.icink"), "user");
            Directory.CreateDirectory(Path.Combine(target, "Plugins")); File.WriteAllText(Path.Combine(target, "Plugins", "mine.txt"), "plugin");
            File.WriteAllText(Path.Combine(target, "obsolete.dll"), "old");
            File.WriteAllText(Path.Combine(target, ".inkcanvas-update-files.json"), "[\"obsolete.dll\"]");
            await UpdatePackage.ApplyAsync(plan, Path.Combine(directory, "work"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "InkCanvas.exe")));
            Assert.Equal("user", File.ReadAllText(Path.Combine(target, "notes.icink")));
            Assert.Equal("plugin", File.ReadAllText(Path.Combine(target, "Plugins", "mine.txt")));
            Assert.False(File.Exists(Path.Combine(target, "obsolete.dll")));
            Assert.True(File.Exists(Path.Combine(target, ".inkcanvas-update-files.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MidwayFailureRollsBackProgramFilesAndRemovesNewFiles()
    {
        string directory = Temporary();
        try
        {
            var plan = Plan(directory);
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "blocked.dll"), "blocked");
            Directory.CreateDirectory(Path.Combine(plan.TargetDirectory, "blocked.dll"));
            plan = plan with { Files = plan.Files.Append(new UpdateFile("blocked.dll", Hash("blocked"))).ToArray() };
            await Assert.ThrowsAnyAsync<IOException>(() => UpdatePackage.ApplyAsync(plan, Path.Combine(directory, "work")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")));
            Assert.False(File.Exists(Path.Combine(plan.TargetDirectory, "added.dll")));
            Assert.False(File.Exists(Path.Combine(directory, "work", "journal.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StageTamperingAbortsBeforeAnyReplacement()
    {
        string directory = Temporary();
        try
        {
            var plan = Plan(directory);
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "added.dll"), "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.ApplyAsync(plan, Path.Combine(directory, "work")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InterruptedReplacementRecoversBeforeRetrying()
    {
        string directory = Temporary();
        try
        {
            var plan = Plan(directory);
            string work = Path.Combine(directory, "work"), backup = Path.Combine(work, "backup");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "InkCanvas.exe"), "old");
            File.WriteAllText(Path.Combine(plan.TargetDirectory, "InkCanvas.exe"), "interrupted");
            File.WriteAllText(Path.Combine(plan.TargetDirectory, "added.dll"), "partial");
            File.WriteAllText(Path.Combine(work, "journal.json"), """
                [{"Path":"InkCanvas.exe","Existed":true},{"Path":"added.dll","Existed":false}]
                """);
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "added.dll"), "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.ApplyAsync(plan, work));
            Assert.Equal("old", File.ReadAllText(Path.Combine(plan.TargetDirectory, "InkCanvas.exe")));
            Assert.False(File.Exists(Path.Combine(plan.TargetDirectory, "added.dll")));
            Assert.False(File.Exists(Path.Combine(work, "journal.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static UpdatePlan Plan(string directory)
    {
        string target = Path.Combine(directory, "app"), payload = Path.Combine(directory, "payload");
        Directory.CreateDirectory(target); Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(target, "InkCanvas.exe"), "old");
        File.WriteAllText(Path.Combine(payload, "InkCanvas.exe"), "new");
        File.WriteAllText(Path.Combine(payload, "added.dll"), "added");
        return new(target, payload, "3.5.0", new[] { new UpdateFile("InkCanvas.exe", Hash("new")), new UpdateFile("added.dll", Hash("added")) }, 0, 0, null, false);
    }
    private static string Hash(string data) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    private static string Temporary() { string path = Path.Combine(Path.GetTempPath(), "inkcanvas-update-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
