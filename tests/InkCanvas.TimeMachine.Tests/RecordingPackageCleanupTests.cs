
using InkCanvas.Features.Board;
using InkCanvas.Features.Ppt;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Modules.TimeMachine;
using Microsoft.UI.Xaml;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>录制包失败清理与解压缓存保留策略。</summary>
public sealed class RecordingPackageCleanupTests
{
    private sealed class MinimalHost : ITimeMachineHost
    {
        public BoardService Board { get; } = new();
        public InkController ActiveController => Board.Current;
        public bool BoardOpen => true;
        public PptInkSession Ppt => null;
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "inkcanvas-tm-clean-" + Guid.NewGuid().ToString("N"));
        public Window Owner => null;
        public int Theme => 0;

        public event Action ActiveControllerChanged;
        public event Action BoardOpenChanged;
        public event Action<ClassroomSignal> ClassroomSignal;

        public Task<string> CaptureScreenshotAsync() => Task.FromResult("");
        public Task NotifyAsync(string title, string message) => Task.CompletedTask;
        public void ConfigureWindow(Window window, int width = 1120, int height = 820) { }
    }

    [Fact]
    public void Save_WritesPackageAndLeavesNoTemporaryFile()
    {
        string dir = TempDir();
        try
        {
            var session = Session();
            string path = RecordingPackage.Save(session, dir);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(session.Id, RecordingPackage.Load(path).Id);
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void Save_FailingAtFinalReplace_CleansTemporaryPackage()
    {
        string dir = TempDir();
        try
        {
            var session = Session();
            string path = RecordingPackage.PathFor(session.Id, dir);
            // 把正式文件路径占成目录，打包完成后 File.Move 必然失败。
            Directory.CreateDirectory(path);

            // 目标路径是目录时 File.Move 以 UnauthorizedAccessException / IOException 失败，均属预期。
            Assert.ThrowsAny<Exception>(() => RecordingPackage.Save(session, dir));

            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(Directory.Exists(path));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void TryDeleteTemporary_RemovesStalePackageFragment()
    {
        string dir = TempDir();
        try
        {
            string tmp = Path.Combine(dir, "broken.icink.tmp");
            File.WriteAllText(tmp, "partial");

            Assert.True(RecordingPackage.TryDeleteTemporary(tmp));
            Assert.False(File.Exists(tmp));
            Assert.False(RecordingPackage.TryDeleteTemporary(tmp));
        }
        finally { DeleteTemp(dir); }
    }

    [Fact]
    public void CleanupRecordingCache_RemovesExpiredButKeepsCurrentSession()
    {
        using var host = new HostScope(new MinimalHost());
        using var service = new TimeMachineService(host.Host, action => action);
        string cache = Path.Combine(service.RecordingsDirectory(), ".cache");
        string expired = Path.Combine(cache, "old-recording");
        string current = Path.Combine(cache, "open-recording");
        Directory.CreateDirectory(expired);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(expired, "slide.png"), "old");
        File.WriteAllText(Path.Combine(current, "slide.png"), "current");
        File.SetLastWriteTimeUtc(Path.Combine(expired, "slide.png"), DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(current, "slide.png"), DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-30));

        int removed = service.CleanupRecordingCache(keepDirectory: current);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(current));
    }

    [Fact]
    public void CleanupRecordingCache_MissingCacheDirectory_ReturnsZero()
    {
        using var host = new HostScope(new MinimalHost());
        using var service = new TimeMachineService(host.Host, action => action);

        Assert.Equal(0, service.CleanupRecordingCache());
    }

    private static ClassroomSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "录制",
        CreatedAt = DateTime.Now,
        Events = new List<TimelineEvent>
        {
            new() { TimeMs = 10, PageIndex = 0, Kind = TimelineEventKind.PageChanged },
        },
    };

    private sealed class HostScope(MinimalHost host) : IDisposable
    {
        public MinimalHost Host { get; } = host;

        public void Dispose()
        {
            try { if (Directory.Exists(Host.DataDirectory)) Directory.Delete(Host.DataDirectory, recursive: true); }
            catch { /* 清理失败不影响断言 */ }
        }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ink-recordings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTemp(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 清理失败不影响断言 */ }
    }
}