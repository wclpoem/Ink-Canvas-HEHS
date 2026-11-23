using System.Net;
using System.Security.Cryptography;
using System.Text;
using InkCanvas.Core.Update;
using InkCanvas.Features.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class MsiUpdateTests
{
    private const string Prefix = "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v3.10.0/";
    private static string Feed => """
        [{"tag_name":"v3.10.0","assets":[
          {"name":"InkCanvas-WinUI3-3.10.0-win-x64.zip","browser_download_url":"PREFIXInkCanvas-WinUI3-3.10.0-win-x64.zip"},
          {"name":"InkCanvas-WinUI3-3.10.0-win-x64.msi","browser_download_url":"PREFIXInkCanvas-WinUI3-3.10.0-win-x64.msi","digest":"sha256:HASH"},
          {"name":"SHA256SUMS.txt","browser_download_url":"PREFIXSHA256SUMS.txt"}]}]
        """.Replace("PREFIX", Prefix).Replace("HASH", Hash("msi"));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ReleasesSelectThePackageMatchingTheInstallType()
    {
        Assert.True(Assert.Single(ReleaseFeed.ParseStableList(Feed)).IsPortable);
        Assert.True(Assert.Single(ReleaseFeed.ParseStableList(Feed, preferMsi: true)).IsMsi);
        Assert.Empty(ReleaseFeed.ParseStableList(Feed.Replace("-win-x64.msi", "-win-arm64.msi"), preferMsi: true));
        string sums = Hash("msi") + "  InkCanvas-WinUI3-3.10.0-win-x64.msi\n" + Hash("zip") + "  InkCanvas-WinUI3-3.10.0-win-x64.zip";
        Assert.True(ReleaseFeed.ParseLatestChecksums(sums, "QiXinia/Ink-Canvas-HEHS", preferMsi: true).IsMsi);
        Assert.True(ReleaseFeed.ParseLatestChecksums(sums, "QiXinia/Ink-Canvas-HEHS").IsPortable);
    }

    [Fact]
    public async Task MsiDownloadsUseTheSharedVerifiedCacheAndRejectWrongProducts()
    {
        string directory = Directory.CreateTempSubdirectory("InkCanvas-msi-update-").FullName;
        try
        {
            using var transport = new UpdateTransport(new Handler(), Array.Empty<string>());
            using var service = new UpdateService(transport: transport, cache: new UpdateCache(directory), preferMsi: true);
            var release = Assert.Single(await service.GetReleasesAsync());
            string path = await service.DownloadAsync(release);
            Assert.EndsWith("package.msi", path);
            Assert.Equal("msi", File.ReadAllText(path));
            File.WriteAllText(Path.Combine(directory, MsiPackage.MarkerName), "test target");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(release, path, directory, restart: false));
            Assert.False(Directory.GetDirectories(Path.GetDirectoryName(path), "install-*").Any());
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { AssetName = "package.zip" }));
            service.ReleaseCache();
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(request.RequestUri!.Host == "api.github.com" ? Feed : "msi") });
    }
}
