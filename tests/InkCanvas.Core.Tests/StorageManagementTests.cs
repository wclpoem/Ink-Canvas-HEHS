using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Update;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StorageManagementTests
{
    [Fact]
    public void SettingsSkipUnchangedWritesAndRecoverPreviousValidConfiguration()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.Path);
        var settings = new AppSettings();
        settings.Canvas.InkWidth = 4;
        store.Save(settings);
        var stamp = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(store.SettingsPath, stamp);
        store.Save(settings);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(store.SettingsPath));
        settings.Canvas.InkWidth = 8;
        store.Save(settings);
        Assert.True(File.Exists(store.BackupPath));
        File.WriteAllText(store.SettingsPath, "{corrupt");
        var recoveredStore = new SettingsStore(directory.Path);
        var recovered = recoveredStore.Load();
        Assert.True(recoveredStore.RecoveredFromBackup);
        Assert.Equal(4, recovered.Canvas.InkWidth);
        recoveredStore.Save(recovered);
        Assert.Equal(4, new SettingsStore(directory.Path).Load().Canvas.InkWidth);
        Assert.Null(recoveredStore.LastError);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        recovered.Canvas.InkWidth = 10;
        File.WriteAllText(store.SettingsPath, "{external-corruption");
        recoveredStore.Save(recovered);
        Assert.Equal(4, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.BackupPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).Canvas.InkWidth);
    }

    [Fact]
    public void CleanupKeepsConfigurationUserDataRecentWritesAndUnknownTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.Path);
        store.Save(new());
        string old = directory.File("Settings.json." + Guid.NewGuid().ToString("N") + ".tmp", "old");
        string recent = directory.File("LotteryCooldown.json." + Guid.NewGuid().ToString("N") + ".tmp", "new");
        string user = directory.File("lesson.icink", "lesson");
        string unknown = directory.File("unrelated.tmp", "keep");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        var storage = new AppStorage(store, new UpdateCache(System.IO.Path.Combine(directory.Path, "Updates")));
        Assert.Equal(6, storage.Inspect().TemporaryBytes);
        var result = storage.CleanupTemporaryFiles();
        Assert.Equal(3, result.FreedBytes);
        Assert.False(File.Exists(old));
        Assert.All(new[] { store.SettingsPath, recent, user, unknown }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void UpdateCleanupSkipsActiveLeasesPendingInstallsAndRollbackJournals()
    {
        using var directory = new TestDirectory();
        var cache = new UpdateCache(directory.Path);
        string old = Package("a", "old");
        string active = Package("b", "active");
        string rollback = Package("c", "rollback");
        string pending = Package("d", "pending");
        string unrelated = directory.File("personal/keep.txt", "keep");
        File.WriteAllText(System.IO.Path.Combine(rollback, "journal.json"), "[]");
        File.WriteAllText(System.IO.Path.Combine(pending, "plan.json"), "{}");
        using (var lease = new FileStream(System.IO.Path.Combine(active, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = cache.Cleanup(7, allUnused: true);
            Assert.Equal(1, result.RemovedItems);
            Assert.False(Directory.Exists(old));
            Assert.All(new[] { active, rollback, pending }, path => Assert.True(Directory.Exists(path)));
        }
        Assert.True(File.Exists(unrelated));

        string Package(string key, string data)
        {
            string path = cache.PackageDirectory(new string(key[0], 64));
            Directory.CreateDirectory(path); File.WriteAllText(System.IO.Path.Combine(path, "package.zip"), data);
            return path;
        }
    }

    [Fact]
    public void AutomaticCleanupHonorsAgeAndRemovesAbandonedLegacyDownloads()
    {
        using var directory = new TestDirectory();
        var cache = new UpdateCache(directory.Path);
        string old = directory.File(Guid.NewGuid().ToString("N") + "/package.zip", "expired");
        string recent = directory.File(Guid.NewGuid().ToString("N") + "/package.zip", "recent");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));
        var result = cache.Cleanup(7);
        Assert.Equal(1, result.RemovedItems);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.NotNull(cache.LastCleanupUtc);
    }

    [Fact]
    public async Task CachedPackageIsVerifiedAndReusedAndCorruptionTriggersRedownload()
    {
        using var directory = new TestDirectory();
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package")));
        var handler = new PackageHandler();
        using var service = new UpdateService(transport: new UpdateTransport(handler, Array.Empty<string>()), cache: new UpdateCache(directory.Path));
        var release = new ReleaseInfo("v9.0.0", new(9, 0, 0, 0), "test", "", "",
            "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v9.0.0/app.zip", 7) { AssetName = "app.zip", Sha256 = hash };
        var stages = new List<UpdateStage>();
        service.ProgressChanged += progress => stages.Add(progress.Stage);
        string archive = await service.DownloadAsync(release);
        Assert.Equal(archive, await service.DownloadAsync(release));
        Assert.Equal(1, handler.Requests);
        Assert.Contains(UpdateStage.Downloading, stages);
        Assert.Contains(UpdateStage.Verifying, stages);
        Assert.Equal(0, service.Cache.Cleanup(7, allUnused: true).RemovedItems);
        File.WriteAllText(archive, "corrupt");
        await service.DownloadAsync(release);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("package", File.ReadAllText(archive));
    }

    [Fact]
    public void CleanupRemovesExpiredAtomicWriteLeftoversAndBundleTempDirectories()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.Path);
        store.Save(new());

        var expiredFiles = new[]
        {
            directory.File("sessions/" + Guid.NewGuid().ToString("N") + ".json.tmp", "session"),
            directory.File("examples.json.tmp", "examples"),
            directory.File("export.icink.tmp", "export"),
            directory.File("Recordings/lesson.icink.tmp", "recordings"),
            directory.File("PluginData/inkcanvas.time-machine/Recordings/demo.icink.tmp", "plugin"),
        };
        foreach (string path in expiredFiles)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        string recentSession = directory.File("sessions/recent.json.tmp", "recent");
        string savedSession = directory.File("sessions/keep.json", "{}");
        string expiredBundle = directory.File("bundles/20250101-000000.tmp/board.json", "{}");
        string completedBundle = directory.File("bundles/20260101-000000/board.json", "{}");
        string stagedRestore = directory.File("bundles/20260101-000000/.restore-" + Guid.NewGuid().ToString("N") + "/partial.bin", "partial");
        File.SetLastWriteTimeUtc(expiredBundle, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(stagedRestore, DateTime.UtcNow.AddDays(-2));
        string expiredBundleDir = System.IO.Path.Combine(directory.Path, "bundles", "20250101-000000.tmp");
        string stagedRestoreDir = System.IO.Path.GetDirectoryName(stagedRestore);
        Directory.SetLastWriteTimeUtc(expiredBundleDir, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(stagedRestoreDir, DateTime.UtcNow.AddDays(-2));

        var storage = new AppStorage(store, new UpdateCache(System.IO.Path.Combine(directory.Path, "Updates")));
        var result = storage.CleanupTemporaryFiles();

        Assert.All(expiredFiles, path => Assert.False(File.Exists(path)));
        Assert.False(Directory.Exists(expiredBundleDir));
        Assert.False(Directory.Exists(stagedRestoreDir));
        Assert.Equal(expiredFiles.Length + 2, result.RemovedItems);
        Assert.All(new[] { recentSession, savedSession, completedBundle }, path => Assert.True(File.Exists(path)));
    }

    private sealed class PackageHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("package") });
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ink-storage-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public string File(string name, string contents)
        {
            string path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, contents);
            return path;
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
