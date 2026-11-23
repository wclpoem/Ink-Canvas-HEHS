using System.IO.Compression;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Modules.TimeMachine;
using Xunit;

namespace InkCanvas.TimeMachine.Tests;

/// <summary>新格式 .icink（ZIP）容器写入/读取一致、清单标记、列表/重命名/删除/另存与格式拒绝测试。</summary>
public sealed class PackageRoundTripTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-tm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    private static ClassroomSession BuildSession()
    {
        var s1 = Stroke(42);
        return new ClassroomSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "课堂板书",
            CreatedAt = new DateTime(2026, 11, 22, 10, 30, 0),
            Events = new List<TimelineEvent>
            {
                new() { TimeMs = 0, PageIndex = 0, Kind = TimelineEventKind.PageChanged },
                new() { TimeMs = 0, PageIndex = 0, Kind = TimelineEventKind.BackgroundChanged, Payload = "Grid" },
                new()
                {
                    TimeMs = 100, PageIndex = 0, Kind = TimelineEventKind.StrokeAdded,
                    StrokeId = s1.Id.ToString(), StrokeJson = StrokeJsonCodec.Serialize(s1),
                },
                new()
                {
                    TimeMs = 200, PageIndex = 0, Kind = TimelineEventKind.StrokesRemoved,
                    StrokeIds = new[] { s1.Id.ToString() },
                },
            },
            Checkpoints = new List<SessionCheckpoint>(),
        };
    }

    [Fact]
    public void SaveLoad_RoundTripsSession()
    {
        string dir = TempDir();
        try
        {
            var session = BuildSession();
            string path = RecordingPackage.Save(session, dir);
            Assert.True(File.Exists(path));

            var loaded = RecordingPackage.Load(path);
            Assert.Equal(session.Id, loaded.Id);
            Assert.Equal("课堂板书", loaded.Name);
            Assert.Equal(session.CreatedAt, loaded.CreatedAt);
            Assert.Equal(4, loaded.Events.Count);
            Assert.Equal(TimelineEventKind.StrokeAdded, loaded.Events[2].Kind);
            Assert.Single(loaded.Events[3].StrokeIds!);

            var restored = StrokeJsonCodec.Deserialize(loaded.Events[2].StrokeJson!);
            Assert.NotNull(restored);
            Assert.Equal(2, restored!.Count);
            Assert.Equal(42f, restored.Points[0].X, 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveLoad_FrameMatchesOriginal()
    {
        string dir = TempDir();
        try
        {
            var session = BuildSession();
            var original = new InkTimeline(session).BuildFrame(150);

            string path = RecordingPackage.Save(session, dir);
            var loaded = RecordingPackage.Load(path);
            var replayed = new InkTimeline(loaded).BuildFrame(150);

            Assert.Equal(original.Count, replayed.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Container_ManifestMarksClassroomFormat()
    {
        string dir = TempDir();
        try
        {
            string path = RecordingPackage.Save(BuildSession(), dir);

            using var archive = ZipFile.OpenRead(path);
            var manifestEntry = archive.GetEntry(ClassroomRecordingFormat.ManifestEntry);
            Assert.NotNull(manifestEntry);

            using var reader = new StreamReader(manifestEntry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            Assert.Equal(ClassroomRecordingFormat.Format, root.GetProperty("format").GetString());
            Assert.Equal(ClassroomRecordingFormat.Version, root.GetProperty("version").GetInt32());
            Assert.NotNull(archive.GetEntry("timeline.json"));
            Assert.Contains(archive.Entries, e => e.FullName.StartsWith("history/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void List_Rename_Delete_CopyWork()
    {
        string dir = TempDir();
        try
        {
            string path = RecordingPackage.Save(BuildSession(), dir);
            var listed = RecordingPackage.List(dir);
            Assert.Single(listed);
            var info = listed[0];
            Assert.Equal("课堂板书", info.Name);

            Assert.True(RecordingPackage.Rename(path, "新名称"));
            Assert.Equal("新名称", RecordingPackage.List(dir)[0].Name);

            string copyPath = Path.Combine(dir, "copy" + ClassroomRecordingFormat.Extension);
            RecordingPackage.CopyTo(path, copyPath);
            Assert.Equal(2, RecordingPackage.List(dir).Count);

            Assert.True(RecordingPackage.Delete(copyPath));
            Assert.Single(RecordingPackage.List(dir));

            Assert.True(RecordingPackage.Delete(path));
            Assert.Empty(RecordingPackage.List(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveLoad_EventScreenshot_RoundTripsIntoPackage()
    {
        string dir = TempDir();
        string extractDir = Path.Combine(dir, "extracted");
        Directory.CreateDirectory(extractDir);
        string png = Path.Combine(extractDir, "timer.png");
        File.WriteAllBytes(png, new byte[] { 137, 80, 78, 71 });
        try
        {
            var session = BuildSession();
            session.Events.Add(new TimelineEvent
            {
                TimeMs = 300, PageIndex = 0, Kind = TimelineEventKind.TimerEnded,
                Payload = "练习结束", Screenshot = png,
            });

            string path = RecordingPackage.Save(session, dir);

            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, e => e.FullName.StartsWith("screenshots/", StringComparison.Ordinal));

            var loaded = RecordingPackage.Load(path, extractDir);
            var ended = Assert.Single(loaded.Events, e => e.Kind == TimelineEventKind.TimerEnded);
            Assert.NotNull(ended.Screenshot);
            Assert.StartsWith(extractDir, ended.Screenshot);
            Assert.True(File.Exists(ended.Screenshot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveLoad_PptPayloadScreenshot_RoundTrips()
    {
        string dir = TempDir();
        string extractDir = Path.Combine(dir, "extracted");
        Directory.CreateDirectory(extractDir);
        string png = Path.Combine(extractDir, "slide.png");
        File.WriteAllBytes(png, new byte[] { 137, 80, 78, 71 });
        try
        {
            var session = BuildSession();
            session.Events.Add(new TimelineEvent
            {
                TimeMs = 250, PageIndex = 0, Kind = TimelineEventKind.PptSlideChanged,
                StrokeId = "7",
                Payload = InkTimeline.EncodePptSlideChanged(7, 2, png),
            });

            string path = RecordingPackage.Save(session, dir);
            var loaded = RecordingPackage.Load(path, extractDir);

            var ppt = Assert.Single(loaded.Events, e => e.Kind == TimelineEventKind.PptSlideChanged);
            var change = InkTimeline.DecodePptSlideChanged(ppt.Payload);
            Assert.True(change.IsValid);
            Assert.StartsWith(extractDir, change.Screenshot);
            Assert.True(File.Exists(change.Screenshot));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsWrongFormat_WithChineseMessage()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "legacy" + ClassroomRecordingFormat.Extension);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(ClassroomRecordingFormat.ManifestEntry);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(JsonSerializer.Serialize(new
                {
                    format = "legacy-session",
                    version = 1,
                    id = "x",
                    name = "old",
                    createdAt = DateTime.Now,
                    timeline = "timeline.json",
                }));
            }

            var ex = Assert.Throws<InvalidOperationException>(() => RecordingPackage.Load(path));
            Assert.Contains("格式", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsPlainTextFile()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "not-a-package" + ClassroomRecordingFormat.Extension);
            File.WriteAllText(path, "plain text");
            Assert.Throws<InvalidOperationException>(() => RecordingPackage.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
