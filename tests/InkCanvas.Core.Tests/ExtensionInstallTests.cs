using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Extensions;
using InkCanvas.Features.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ExtensionInstallTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "InkCanvas-extension-test-" + Guid.NewGuid().ToString("N"));
    private const string Repo = "QiXinia/Ink-Canvas-HEHS";
    private const string Id = "inkcanvas.pdf";
    public ExtensionInstallTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static ExtensionRelease Release(string version = "1.0.0", string id = Id) => new(id, "PDF", "PDF", version,
        RepositoryExtensionKind.Module, new AppVersion(3, 8, 0, 0), "v3.8.0", new(id + "-" + version + ".zip",
            $"https://github.com/{Repo}/releases/download/v3.8.0/{id}-{version}.zip", 0, null, null));
    private string Archive(string version = "1.0.0", string id = Id, string extra = null, int api = 1)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(id + "/module.json", JsonSerializer.Serialize(new { Id = id, Name = "PDF", Version = version, ApiVersion = api, EntryAssembly = "Module.dll", EntryType = "Example.Module" }));
        Write(id + "/Module.dll", "module-" + version);
        if (extra is not null) Write(extra, "invalid");
        return path;
        void Write(string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
    }

    [Fact]
    public async Task ACorruptPendingListIsPreservedAndDoesNotBlockANewInstall()
    {
        string root = Path.Combine(_directory, "ExtensionUpdates"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pending.json"), "{invalid");
        var store = new ExtensionInstallStore(_directory); store.ApplyPending();
        Assert.Single(Directory.GetFiles(root, "pending.json.invalid-*"));
        await store.StageAsync(Archive(), Release(), CancellationToken.None);
        Assert.Single(store.Pending);
    }

    [Fact]
    public async Task InstallsOnlyAtStartupAndAnUpdateKeepsThePreviousPackage()
    {
        var store = new ExtensionInstallStore(_directory);
        string first = Archive();
        await store.StageAsync(first, Release(), CancellationToken.None);
        Assert.Single(store.Pending); Assert.False(Directory.Exists(Path.Combine(store.UserModules, Id)));
        store.ApplyPending();
        Assert.Empty(store.Pending); Assert.True(Assert.Single(store.Results).Success);
        string module = Path.Combine(store.UserModules, Id, "Module.dll");
        Assert.Equal("module-1.0.0", File.ReadAllText(module));
        await store.StageAsync(Archive("1.1.0"), Release("1.1.0"), CancellationToken.None);
        Assert.Equal("module-1.0.0", File.ReadAllText(module));
        new ExtensionInstallStore(_directory).ApplyPending();
        Assert.Equal("module-1.1.0", File.ReadAllText(module));
        Assert.Equal("module-1.0.0", File.ReadAllText(Path.Combine(_directory, "ExtensionUpdates", "backup", Id, "Module.dll")));
    }

    [Theory]
    [InlineData("inkcanvas.pdf/../outside.dll")]
    [InlineData("other/Module.dll")]
    [InlineData("inkcanvas.pdf/Module.dll")]
    [InlineData("inkcanvas.pdf/C:/bad.dll")]
    public async Task RejectsInvalidArchivesWithoutChangingInstalledContent(string extra)
    {
        var store = new ExtensionInstallStore(_directory);
        await store.StageAsync(Archive(), Release(), CancellationToken.None); store.ApplyPending();
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => store.StageAsync(Archive("1.1.0", extra: extra), Release("1.1.0"), CancellationToken.None));
        Assert.Empty(store.Pending);
        Assert.Equal("module-1.0.0", File.ReadAllText(Path.Combine(store.UserModules, Id, "Module.dll")));
    }

    [Fact]
    public async Task IdentityAndApiMismatchNeverBecomePending()
    {
        var store = new ExtensionInstallStore(_directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StageAsync(Archive("1.1.0"), Release(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.StageAsync(Archive(api: 20), Release(), CancellationToken.None));
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task CancelingPreparationDoesNotLeaveAnInstallIntent()
    {
        var store = new ExtensionInstallStore(_directory);
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(Archive(), Release(), token.Token));
        Assert.Empty(store.Pending);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, "ExtensionUpdates", "staged")));
    }

    [Fact]
    public async Task TamperedStagingLeavesTheWorkingPackageIntact()
    {
        var store = new ExtensionInstallStore(_directory);
        await store.StageAsync(Archive(), Release(), CancellationToken.None); store.ApplyPending();
        await store.StageAsync(Archive("1.1.0"), Release("1.1.0"), CancellationToken.None);
        string staged = Path.Combine(_directory, "ExtensionUpdates", "staged", store.Pending[0].Stage, "Module.dll");
        File.WriteAllText(staged, "tampered");
        store.ApplyPending();
        Assert.False(Assert.Single(store.Results).Success); Assert.Single(store.Pending);
        Assert.Equal("module-1.0.0", File.ReadAllText(Path.Combine(store.UserModules, Id, "Module.dll")));
    }

    [Fact]
    public async Task PreparingAReplacementRetainsOnlyTheNewestIntent()
    {
        var store = new ExtensionInstallStore(_directory);
        await store.StageAsync(Archive(), Release(), CancellationToken.None);
        await store.StageAsync(Archive("1.1.0"), Release("1.1.0"), CancellationToken.None);
        Assert.Equal("1.1.0", Assert.Single(store.Pending).Version);
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "ExtensionUpdates", "staged")));
    }

    [Fact]
    public async Task NewerBundledModuleTakesPrecedenceAfterAnApplicationUpdate()
    {
        var store = new ExtensionInstallStore(_directory);
        await store.StageAsync(Archive(), Release(), CancellationToken.None); store.ApplyPending();
        string app = Path.Combine(_directory, "app");
        string bundled = Path.Combine(app, "Modules", Id, "module.json"); Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, JsonSerializer.Serialize(new { Id, Name = "PDF", Version = "2.0.0", EntryType = "Example.Module", EntryAssembly = "Module.dll" }));
        Assert.Equal(bundled, store.ModuleManifest(app, Id));
    }

    [Fact]
    public async Task PendingStateSurvivesACompletedDirectoryMoveBeforeIntentCleanup()
    {
        var store = new ExtensionInstallStore(_directory);
        await store.StageAsync(Archive(), Release(), CancellationToken.None);
        string state = File.ReadAllText(Path.Combine(_directory, "ExtensionUpdates", "pending.json"));
        store.ApplyPending();
        File.WriteAllText(Path.Combine(_directory, "ExtensionUpdates", "pending.json"), state);
        var restarted = new ExtensionInstallStore(_directory); restarted.ApplyPending();
        Assert.Empty(restarted.Pending); Assert.True(Assert.Single(restarted.Results).Success);
    }

    [Fact]
    public void CatalogPicksHighestPluginVersionAndSkipsDraftsAndForeignLinks()
    {
        object Item(string tag, string version, bool draft = false, string origin = "github.com") => new
        {
            tag_name = tag, draft, prerelease = false, assets = new[] { new { name = $"{Id}-{version}.zip",
                browser_download_url = $"https://{origin}/{Repo}/releases/download/{tag}/{Id}-{version}.zip", size = 12, digest = "sha256:" + new string('a',64) } },
        };
        string json = JsonSerializer.Serialize(new[] { Item("v3.7.0", "1.0.0"), Item("v3.8.0", "1.2.0"),
            Item("v3.9.0", "9.0.0", true), Item("v3.9.0", "8.0.0", origin: "example.com") });
        var release = Assert.Single(ExtensionFeed.Parse(json, Repo));
        Assert.Equal("1.2.0", release.Version); Assert.Equal("v3.8.0", release.ReleaseTag);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Urls.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(respond(request)); }
    }

    [Fact]
    public async Task OfficialManagedPluginOverridesTheBundledCopyWithoutDuplicateFailure()
    {
        var store = new ExtensionInstallStore(_directory);
        string pluginId = "sample.guide-lines";
        string moduleZip = Archive(id: pluginId);
        string zipPath = Path.Combine(_directory, "plugin.zip");
        using (var source = ZipFile.OpenRead(moduleZip))
        using (var destination = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var entry in source.Entries)
            {
                string name = entry.FullName.Replace("module.json", "plugin.json");
                using var input = entry.Open(); using var output = destination.CreateEntry(name).Open(); input.CopyTo(output);
            }
        var release = Release(id: pluginId) with { Kind = RepositoryExtensionKind.Plugin };
        await store.StageAsync(zipPath, release, CancellationToken.None); store.ApplyPending();
        string bundled = Path.Combine(_directory, "app", "Plugins", pluginId); Directory.CreateDirectory(bundled);
        File.Copy(Path.Combine(store.UserPlugins, pluginId, "plugin.json"), Path.Combine(bundled, "plugin.json"));
        File.Copy(Path.Combine(store.UserPlugins, pluginId, "Module.dll"), Path.Combine(bundled, "Module.dll"));
        using var manager = new PluginManager("unused.exe", Path.Combine(_directory, "data"));
        manager.DiscoverManaged(Path.GetDirectoryName(bundled)!, store.UserPlugins);
        Assert.Equal(ExtensionState.Installed, Assert.Single(manager.Status).State);
    }

    [Fact]
    public async Task RepositoryCatalogUsesPublicPagesWhenApisAreUnavailable()
    {
        string page = $"<section><a href=\"/{Repo}/releases/tag/v3.8.0\">Stable</a></section>";
        string assets = $"<ul><li><a href=\"/{Repo}/releases/download/v3.8.0/{Id}-1.0.0.zip\">PDF</a><span>sha256:{new string('a',64)}</span></li></ul>";
        var handler = new Handler(request => request.RequestUri!.Host == "api.github.com" ? new(HttpStatusCode.Forbidden)
            : new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.Contains("expanded_assets") ? assets : page) });
        using var service = new UpdateService(transport: new UpdateTransport(handler, Array.Empty<string>()), cache: new UpdateCache(Path.Combine(_directory, "updates")));
        var release = Assert.Single(await service.GetExtensionsAsync());
        Assert.Equal(Id, release.Id); Assert.Equal(new string('a', 64), release.Asset.Sha256);
    }

    [Fact]
    public async Task PluginDownloadsReuseApplicationTransportMirrorsHashesAndCache()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("plugin bytes"); string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var handler = new Handler(request => request.RequestUri!.Host == "github.com" ? throw new HttpRequestException("offline")
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var service = new UpdateService(transport: new UpdateTransport(handler, new[] { "https://mirror.example/" }), cache: new UpdateCache(Path.Combine(_directory, "updates")));
        var asset = new RepositoryAsset("plugin.zip", $"https://github.com/{Repo}/releases/download/v3.8.0/plugin.zip", bytes.Length, hash, null);
        string path = await service.DownloadAssetAsync(asset);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(path, await service.DownloadAssetAsync(asset)); Assert.Equal(2, handler.Urls.Count);
    }
}
